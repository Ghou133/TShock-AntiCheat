using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M10ConnectionPhaseScanTests
{
    private static readonly Guid Run = Guid.Parse("7E73178C-83CB-427A-BDC8-39A7E3968418");
    private static SessionKey Key(int slot = 0, long generation = 1, long worldEpoch = 1) => new(Run, worldEpoch, slot, generation);
    private static NetworkControlOptions Options => new()
    {
        MaxSessions = 8, ConnectionOpeningCost = 1,
        ConnectionBudget = new(256, 100, 10, TimeSpan.FromMinutes(2)),
        SourceBudget = new(256, 1000, 100, TimeSpan.FromMinutes(2)),
        GlobalBudget = new(1, 10000, 1000, TimeSpan.FromMinutes(2)),
        HandshakeTimeout = TimeSpan.FromSeconds(2), AuthenticationTimeout = TimeSpan.FromSeconds(3),
        WorldSyncTimeout = TimeSpan.FromSeconds(4), PlayerUpdateTimeout = TimeSpan.FromSeconds(5),
        ConnectionIdleTtl = TimeSpan.FromSeconds(30), SourceEventCooldown = TimeSpan.FromSeconds(2)
    };

    [Test]
    public void AcceptedHelloBeforeNextPacketUsesAuthenticationDeadlineInTheSameRound()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        var first = controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating);
        var observation = first.Observations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(first.Decisions, Is.Empty, "The old handshake has expired, but the accepted phase has advanced.");
            Assert.That(observation.Before.Phase, Is.EqualTo(ConnectionPhase.Handshake));
            Assert.That(observation.After!.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
            Assert.That(observation.After.PhaseEnteredAt, Is.EqualTo(clock.GetTimestamp()));
            Assert.That(observation.After.LastPacketAt, Is.Zero, "A read is not an incoming packet.");
            Assert.That(observation.After.LastPlayerUpdateAt, Is.Zero);
            Assert.That(observation.Status, Is.EqualTo(ConnectionPhaseReadStatus.Accepted));
        });
        clock.Advance(TimeSpan.FromSeconds(2.999));
        Assert.That(controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating).Decisions, Is.Empty);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating), Key(), "authentication-phase-timeout");
    }

    [Test]
    public void AcceptedWorldSyncAtTheAuthenticationDeadlineUsesItsNewObservationTime()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); controls.EnterPhase(Key(), ConnectionPhase.Authenticating);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.That(controls.InspectTimeouts(8, _ => ConnectionPhase.WorldSync).Decisions, Is.Empty);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.That(controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating).Decisions, Is.Empty);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.WorldSync), Key(), "world-sync-phase-timeout");
    }

    [Test]
    public void PlayingObservationStartsItsAllowanceWithoutManufacturingAHeartbeat()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(4));
        var first = controls.InspectTimeouts(8, _ => ConnectionPhase.Playing);
        Assert.Multiple(() =>
        {
            Assert.That(first.Decisions, Is.Empty);
            Assert.That(first.Observations.Single().After!.PhaseEnteredAt, Is.EqualTo(TimeSpan.FromSeconds(4).Ticks));
            Assert.That(first.Observations.Single().After!.LastPlayerUpdateAt, Is.Zero);
            Assert.That(first.Observations.Single().After!.LastPacketAt, Is.Zero);
        });
        clock.Advance(TimeSpan.FromSeconds(4)); controls.Consume(Key(), 1);
        Assert.That(controls.InspectTimeouts(8, _ => ConnectionPhase.Playing).Decisions, Is.Empty);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Playing), Key(), "player-update-timeout");
    }

    [Test]
    public void OnlyRealPlayerUpdateExtendsThePlayingAllowance()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(4));
        controls.InspectTimeouts(8, _ => ConnectionPhase.Playing);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.That(controls.RecordPlayerUpdate(Key()), Is.True);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.That(controls.InspectTimeouts(8, _ => ConnectionPhase.Playing).Decisions, Is.Empty);
        var snapshot = controls.CapturePhase(Key())!;
        Assert.That(snapshot.LastPlayerUpdateAt, Is.EqualTo(TimeSpan.FromSeconds(8).Ticks));
        Assert.That(snapshot.PhaseEnteredAt, Is.EqualTo(TimeSpan.FromSeconds(4).Ticks));
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Playing), Key(), "player-update-timeout");
    }

    [Test]
    public void RepeatingRegressingAndOrdinaryTrafficDoNotRenewTheAcceptedPhase()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating);
        clock.Advance(TimeSpan.FromSeconds(1)); controls.Consume(Key(), 1);
        controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake);
        clock.Advance(TimeSpan.FromSeconds(1)); controls.Consume(Key(), 1);
        controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating);
        Assert.That(controls.CapturePhase(Key())!.PhaseEnteredAt, Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake), Key(), "authentication-phase-timeout");
    }

    [Test]
    public void TransitionTimestampIsTheCurrentMonotonicObservationTimeAfterTheRead()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        var result = controls.InspectTimeouts(8, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            return ConnectionPhase.Authenticating;
        });
        Assert.That(result.Decisions, Is.Empty);
        Assert.That(result.Observations.Single().After!.PhaseEnteredAt, Is.EqualTo(TimeSpan.FromSeconds(6).Ticks));
        clock.Advance(TimeSpan.FromSeconds(3));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Authenticating), Key(), "authentication-phase-timeout");
    }

    [Test]
    public void UtcJumpsInEitherDirectionDoNotChangeSampledDeadlines()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); controls.InspectTimeouts(8, _ => ConnectionPhase.WorldSync);
        clock.Advance(TimeSpan.FromSeconds(3)); clock.JumpUtc(TimeSpan.FromDays(365));
        Assert.That(controls.InspectTimeouts(8, _ => ConnectionPhase.WorldSync).Decisions, Is.Empty);
        clock.JumpUtc(TimeSpan.FromDays(-730)); clock.Advance(TimeSpan.FromSeconds(1));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.WorldSync), Key(), "world-sync-phase-timeout");
    }

    [Test]
    public void UnavailablePhaseSkipsOnlyItsPhaseDeadlineAndDoesNotRefreshIdle()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        var unavailable = controls.InspectTimeouts(8, _ => null);
        Assert.That(unavailable.Decisions, Is.Empty);
        Assert.That(unavailable.Observations.Single().Status, Is.EqualTo(ConnectionPhaseReadStatus.Unavailable));
        Assert.That(controls.CapturePhase(Key())!.LastPacketAt, Is.Zero);
        clock.Advance(TimeSpan.FromSeconds(28));
        AssertTimeout(controls.InspectTimeouts(8, _ => null), Key(), "connection-idle-ttl");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FaultedPhaseReadIsBoundedAndDoesNotDisableOtherConnectionsOrIndependentIdle(bool invalidEnum)
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); controls.Open(Key(1), "127.0.0.2");
        clock.Advance(TimeSpan.FromSeconds(2));
        ConnectionPhase? Read(SessionKey key) => key.Slot != 0 ? ConnectionPhase.Handshake
            : invalidEnum ? (ConnectionPhase)100 : throw new InvalidOperationException(new string('x', 10000));
        var first = controls.InspectTimeouts(8, Read);
        AssertTimeout(first, Key(1), "handshake-phase-timeout");
        Assert.That(first.Observations.Single(x => x.Session == Key()).Status, Is.EqualTo(ConnectionPhaseReadStatus.Faulted));
        Assert.That(first.Observations.Single(x => x.Session == Key()).AcceptedPhase, Is.Null);
        Assert.That(controls.CapturePhase(Key()), Is.Not.Null);
        clock.Advance(TimeSpan.FromSeconds(28));
        AssertTimeout(controls.InspectTimeouts(8, Read), Key(), "connection-idle-ttl");
    }

    [Test]
    public void SampleFailureIsNotLatchedAndTheNextHealthyRoundStillChecksTheOriginalDeadline()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(controls.InspectTimeouts(8, _ => throw new IOException("local read failed")).Decisions, Is.Empty);
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake), Key(), "handshake-phase-timeout");
    }

    [Test]
    public void InvalidMonotonicClockKeepsItsExistingAvailabilityOutcomeAndNoSourceProposal()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        clock.Advance(TimeSpan.FromSeconds(3)); controls.Open(Key(), "127.0.0.1");
        clock.Advance(TimeSpan.FromSeconds(-1));
        AssertTimeout(controls.InspectTimeouts(8, _ => null), Key(), "network-monotonic-clock-invalid");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedGenerationOrWorldEpochDropsTheSelectedOldConnection(bool changeWorld)
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        var fresh = changeWorld ? Key(worldEpoch: 2) : Key(generation: 2);
        var result = controls.InspectTimeouts(8, old =>
        {
            Assert.That(old, Is.EqualTo(Key()));
            Assert.That(controls.Close(old), Is.True);
            Assert.That(controls.Open(fresh, "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
            return ConnectionPhase.Playing;
        });
        AssertStale(result);
        Assert.That(controls.CapturePhase(Key()), Is.Null);
        Assert.That(controls.CapturePhase(fresh)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        Assert.That(controls.Close(Key()), Is.False);
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake), fresh, "handshake-phase-timeout");
    }

    [Test]
    public void CloseAndReopenWithTheSameKeyStillDropsTheOldConnectionObject()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        var result = controls.InspectTimeouts(8, key =>
        {
            Assert.That(controls.Close(key), Is.True);
            Assert.That(controls.Open(key, "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
            return ConnectionPhase.Playing;
        });
        AssertStale(result);
        Assert.That(controls.CapturePhase(Key())!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        Assert.That(controls.CapturePhase(Key())!.PhaseEnteredAt, Is.EqualTo(TimeSpan.FromSeconds(2).Ticks));
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake), Key(), "handshake-phase-timeout");
    }

    [Test]
    public async Task CallbackRunsOutsideNetworkGateWhileAnotherThreadClosesAndOpensTheSlot()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(2));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var scan = Task.Run(() => controls.InspectTimeouts(8, _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test callback release deadline.");
            return ConnectionPhase.Playing;
        }));
        Task? mutation = null;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            mutation = Task.Run(() =>
            {
                Assert.That(controls.Close(Key()), Is.True);
                Assert.That(controls.Open(Key(generation: 2), "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
            });
            await mutation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await scan.WaitAsync(TimeSpan.FromSeconds(2));
            if (mutation is not null) await mutation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        AssertStale(await scan);
        Assert.That(controls.CapturePhase(Key(generation: 2))!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
    }

    [Test]
    public void SameRoundDoesNotDiscoverAnInitiallyEmptySlotOpenedDuringItsCallback()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options);
        controls.Open(Key(), "127.0.0.1"); var reads = new List<SessionKey>();
        var result = controls.InspectTimeouts(8, key =>
        {
            reads.Add(key); controls.Open(Key(1), "127.0.0.1"); return ConnectionPhase.Authenticating;
        });
        Assert.That(reads, Is.EqualTo(new[] { Key() }));
        Assert.That(result.InspectedSlots, Is.EqualTo(8));
        Assert.That(result.Observations.Length, Is.EqualTo(1));
        Assert.That(controls.CapturePhase(Key(1))!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
    }

    [Test]
    public void AConcurrentForwardTransitionCannotBeRegressedByAnOlderSample()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); clock.Advance(TimeSpan.FromSeconds(1));
        var result = controls.InspectTimeouts(8, key =>
        {
            controls.EnterPhase(key, ConnectionPhase.WorldSync);
            clock.Advance(TimeSpan.FromSeconds(1));
            return ConnectionPhase.Authenticating;
        });
        Assert.That(result.Decisions, Is.Empty);
        Assert.That(result.Observations.Single().After!.Phase, Is.EqualTo(ConnectionPhase.WorldSync));
        Assert.That(result.Observations.Single().After!.PhaseEnteredAt, Is.EqualTo(TimeSpan.FromSeconds(1).Ticks));
    }

    [Test]
    public void DefaultBatchCountsEmptySlotsAndWrapsOnceAcrossAll256Slots()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options with { MaxSessions = 256 });
        int[] occupied = [0, 7, 15, 16, 31, 127, 128, 254, 255];
        foreach (int slot in occupied) controls.Open(Key(slot), "127.0.0.1");
        clock.Advance(TimeSpan.FromSeconds(2));
        for (int round = 0; round < 16; round++)
        {
            var reads = new List<int>();
            var result = controls.InspectTimeouts(key =>
            {
                reads.Add(key.Slot);
                return key.Slot % 2 == 0 ? ConnectionPhase.Authenticating : ConnectionPhase.Handshake;
            });
            Assert.That(result.InspectedSlots, Is.EqualTo(16));
            Assert.That(reads, Is.EqualTo(occupied.Where(slot => slot / 16 == round)));
            Assert.That(result.Observations.Length, Is.EqualTo(reads.Count));
            Assert.That(result.Decisions.Select(x => x.Session.Slot), Is.EqualTo(reads.Where(slot => slot % 2 != 0)));
        }
        var wrappedReads = new List<int>();
        controls.InspectTimeouts(key => { wrappedReads.Add(key.Slot); return ConnectionPhase.Authenticating; });
        Assert.That(wrappedReads, Is.EqualTo(new[] { 0 }));
        Assert.That(controls.Count, Is.EqualTo(occupied.Count(slot => slot % 2 == 0)));
    }

    [Test]
    public void HardLimitBoundsBothCallbacksAndFaultRecordsTo64SelectedSlots()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options with { MaxSessions = 256 });
        for (int slot = 0; slot < 80; slot++) Assert.That(controls.Open(Key(slot), "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
        int reads = 0;
        var result = controls.InspectTimeouts(64, _ => { reads++; throw new IOException("bounded fixture"); });
        Assert.That(reads, Is.EqualTo(64));
        Assert.That(result.InspectedSlots, Is.EqualTo(64));
        Assert.That(result.Observations.Length, Is.EqualTo(64));
        Assert.That(result.Observations.All(x => x.Status == ConnectionPhaseReadStatus.Faulted), Is.True);
        Assert.That(result.Decisions, Is.Empty);
        var next = new List<int>();
        controls.InspectTimeouts(16, key => { next.Add(key.Slot); return null; });
        Assert.That(next, Is.EqualTo(Enumerable.Range(64, 16)));
    }

    [Test]
    public void LegacyAndSampledScansShareOneCursorAndDoNotDoubleScanTheSelectedBatch()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options with { MaxSessions = 32 });
        controls.Open(Key(), "127.0.0.1"); controls.Open(Key(16), "127.0.0.1");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(controls.InspectTimeouts(16).Single().Session, Is.EqualTo(Key()));
        var reads = new List<SessionKey>();
        controls.InspectTimeouts(key => { reads.Add(key); return ConnectionPhase.Authenticating; });
        Assert.That(reads, Is.EqualTo(new[] { Key(16) }));
    }

    [Test]
    public void ExpiryReleasesTheConnectionOnlyOnceWhileSourceDebtSurvives()
    {
        var clock = new NetworkTestClock();
        var controls = new NetworkControls(clock, Options with { SourceBudget = new(256, 3, .001, TimeSpan.FromMinutes(2)) });
        controls.Open(Key(), "127.0.0.1");
        Assert.That(controls.Consume(Key(), 1024).Disposition, Is.EqualTo(NetworkDisposition.Allow));
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake), Key(), "handshake-phase-timeout");
        Assert.That(controls.Count, Is.Zero); Assert.That(controls.CapturePhase(Key()), Is.Null);
        int repeatReads = 0;
        Assert.That(controls.InspectTimeouts(8, _ => { repeatReads++; return ConnectionPhase.Handshake; }).Decisions, Is.Empty);
        Assert.That(repeatReads, Is.Zero);
        Assert.That(controls.SourceBucketCount, Is.EqualTo(1));
        Assert.That(controls.Open(Key(generation: 2), "127.0.0.1").Reason, Is.EqualTo("source-cost-budget-exhausted"));
    }

    [Test]
    public void FreshConnectionHasItsOwnBudgetAfterThePreviousOneExpires()
    {
        var clock = new NetworkTestClock();
        var controls = new NetworkControls(clock, Options with { ConnectionBudget = new(256, 3, .001, TimeSpan.FromMinutes(2)) });
        controls.Open(Key(), "127.0.0.1");
        Assert.That(controls.Consume(Key(), 1024).Disposition, Is.EqualTo(NetworkDisposition.Allow));
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertTimeout(controls.InspectTimeouts(8, _ => ConnectionPhase.Handshake), Key(), "handshake-phase-timeout");
        Assert.That(controls.Open(Key(generation: 2), "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
    }

    [Test]
    public void InvalidArgumentsDoNotAdvanceTheScanCursor()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options); controls.Open(Key(), "127.0.0.1");
        Assert.Throws<ArgumentOutOfRangeException>(() => controls.InspectTimeouts(0, _ => null));
        Assert.Throws<ArgumentOutOfRangeException>(() => controls.InspectTimeouts(65, _ => null));
        Assert.Throws<ArgumentNullException>(() => controls.InspectTimeouts(1, null!));
        Assert.That(controls.InspectTimeouts(1, _ => null).Observations.Single().Session, Is.EqualTo(Key()));
    }

    private static void AssertStale(NetworkTimeoutScanResult result)
    {
        Assert.That(result.Decisions, Is.Empty);
        Assert.That(result.Observations.Single().Status, Is.EqualTo(ConnectionPhaseReadStatus.Stale));
        Assert.That(result.Observations.Single().After, Is.Null);
    }

    private static void AssertTimeout(NetworkTimeoutScanResult result, SessionKey key, string reason)
    {
        var decision = result.Decisions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(decision.Session, Is.EqualTo(key));
            Assert.That(decision.Reason, Is.EqualTo(reason));
            Assert.That(decision.Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(decision.SourceEvent, Is.Null, "A timeout is not a cheating proof or an IP-block proposal.");
        });
    }
}
