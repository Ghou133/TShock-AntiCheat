using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class NetworkControlTests
{
    private static readonly Guid Run = Guid.Parse("6824836F-DBF8-4C9A-BF69-2668A1B5D2BA");
    private static SessionKey Key(int slot = 4, long generation = 1) => new(Run, 1, slot, generation);
    private static NetworkControlOptions Options => new()
    {
        MaxSessions = 8, ConnectionOpeningCost = 1,
        ConnectionBudget = new(8, 100, 10, TimeSpan.FromSeconds(10)),
        SourceBudget = new(16, 1000, 100, TimeSpan.FromSeconds(10)),
        GlobalBudget = new(1, 10000, 1000, TimeSpan.FromSeconds(10)),
        HandshakeTimeout = TimeSpan.FromSeconds(2), AuthenticationTimeout = TimeSpan.FromSeconds(3),
        WorldSyncTimeout = TimeSpan.FromSeconds(4), PlayerUpdateTimeout = TimeSpan.FromSeconds(5),
        ConnectionIdleTtl = TimeSpan.FromSeconds(10), SourceEventCooldown = TimeSpan.FromSeconds(2)
    };

    [Test]
    public void DefaultBudgetsPermitThirtyTwoSharedNatFullInventorySyncFixtures()
    {
        var controls = new NetworkControls(new NetworkTestClock());
        // Deliberately 990 fixture slots including reserved banks, not a claim about target runtime Count.
        for (int slot = 0; slot < 32; slot++)
        {
            var key = Key(slot);
            Assert.That(controls.Open(key, "203.0.113.8").Disposition, Is.EqualTo(NetworkDisposition.Allow));
            for (int i = 0; i < 990; i++)
                Assert.That(controls.Consume(key, 16, NetworkRequestKind.EntitySync).Disposition, Is.EqualTo(NetworkDisposition.Allow));
        }
        Assert.That(controls.Count, Is.EqualTo(32));
        Assert.That(controls.SourceBucketCount, Is.EqualTo(1));
    }

    [Test]
    public void PhaseTimeStartsOnTransitionAndDoesNotUseTotalConnectionAge()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1");
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.That(controls.EnterPhase(Key(), ConnectionPhase.Authenticating), Is.True);
        clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.That(controls.InspectTimeouts(8), Is.Empty);
        clock.Advance(TimeSpan.FromSeconds(.1));
        Assert.That(controls.InspectTimeouts(8).Single().Reason, Is.EqualTo("authentication-phase-timeout"));
        Assert.That(controls.Count, Is.Zero);
    }

    [Test]
    public void RepeatingOrRegressingPhaseAndOrdinaryTrafficCannotExtendHandshake()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(controls.EnterPhase(Key(), ConnectionPhase.Handshake), Is.False);
        controls.Consume(Key(), 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(controls.InspectTimeouts(8).Single().Reason, Is.EqualTo("handshake-phase-timeout"));
    }

    [Test]
    public void PlayingHeartbeatIsSeparateFromPacketTrafficAndWallClock()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); controls.EnterPhase(Key(), ConnectionPhase.Playing);
        clock.Advance(TimeSpan.FromSeconds(4)); controls.Consume(Key(), 10);
        clock.JumpUtc(TimeSpan.FromDays(1));
        Assert.That(controls.InspectTimeouts(8), Is.Empty);
        clock.Advance(TimeSpan.FromSeconds(1));
        var decision = controls.InspectTimeouts(8).Single();
        Assert.Multiple(() =>
        {
            Assert.That(decision.Reason, Is.EqualTo("player-update-timeout"));
            Assert.That(decision.Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(decision.SourceEvent, Is.Null, "Timeout is not evidence to block a NAT.");
        });
    }

    [Test]
    public void RealPlayerUpdateResetsOnlyItsOwnTimer()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock, Options);
        controls.Open(Key(), "127.0.0.1"); controls.EnterPhase(Key(), ConnectionPhase.Playing);
        clock.Advance(TimeSpan.FromSeconds(4)); controls.Consume(Key(), 10); controls.RecordPlayerUpdate(Key());
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.That(controls.InspectTimeouts(8), Is.Empty);
        Assert.That(controls.EnterPhase(Key(), ConnectionPhase.WorldSync), Is.False);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(controls.InspectTimeouts(8).Single().Reason, Is.EqualTo("player-update-timeout"));
    }

    [Test]
    public void ConnectionBudgetLimitsOneConnectionWithoutAttributingTheSharedAddress()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options with { ConnectionBudget = new(8, 2, 1, TimeSpan.FromSeconds(10)) });
        controls.Open(Key(), "203.0.113.8");
        var result = controls.Consume(Key(), 2048);
        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo("connection-cost-budget-exhausted"));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(result.SourceEvent!.Recommendation, Is.EqualTo(FirewallRecommendation.RecordOnly));
            Assert.That(result.SourceEvent.ProposedTarget, Is.Null);
        });
        Assert.That(controls.Open(Key(5), "203.0.113.8").Disposition, Is.EqualTo(NetworkDisposition.Allow));
    }

    [Test]
    public void SharedSourceAndGlobalBudgetsCannotBeBypassedByNewConnections()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options with { SourceBudget = new(16, 5, 1, TimeSpan.FromSeconds(10)) });
        controls.Open(Key(), "203.0.113.8"); controls.Open(Key(5), "::ffff:203.0.113.8");
        Assert.That(controls.Consume(Key(), 64).Disposition, Is.EqualTo(NetworkDisposition.Allow));
        Assert.That(controls.Consume(Key(5), 64).Disposition, Is.EqualTo(NetworkDisposition.Allow));
        var source = controls.Consume(Key(), 64);
        Assert.That(source.Reason, Is.EqualTo("source-cost-budget-exhausted"));
        Assert.That(source.NormalizedPeer, Is.EqualTo("203.0.113.8"));
        var global = new NetworkControls(new NetworkTestClock(), Options with { GlobalBudget = new(1, 2, 1, TimeSpan.FromSeconds(10)) });
        global.Open(Key(), "203.0.113.8"); global.Open(Key(5), "198.51.100.9");
        Assert.That(global.Consume(Key(5), 64).Reason, Is.EqualTo("global-cost-budget-exhausted"));
    }

    [Test]
    public void ReconnectDoesNotResetSourceDebtAndStaleKeysCannotEvictTheCurrentSession()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options with { SourceBudget = new(16, 1, 1, TimeSpan.FromSeconds(10)) });
        Assert.That(controls.Open(Key(), "203.0.113.8").Disposition, Is.EqualTo(NetworkDisposition.Allow));
        Assert.That(controls.Close(Key()), Is.True);
        Assert.That(controls.Open(Key(generation: 2), "203.0.113.8").Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
        var fresh = new NetworkControls(new NetworkTestClock(), Options);
        fresh.Open(Key(generation: 2), "127.0.0.1");
        Assert.That(fresh.Open(Key(), "127.0.0.1").Reason, Is.EqualTo("stale-network-registration"));
        Assert.That(fresh.Close(Key()), Is.False);
        Assert.That(fresh.Consume(Key(generation: 2), 10).Disposition, Is.EqualTo(NetworkDisposition.Allow));
        Assert.That(fresh.Open(Key() with { ServerRunId = Guid.NewGuid() }, "127.0.0.1").Reason, Is.EqualTo("network-server-run-mismatch"));
    }

    [Test]
    public void BytesAndExpensiveRequestCostsAreChargedInsteadOfCountingOnlyPackets()
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options with { ConnectionBudget = new(8, 10, 1, TimeSpan.FromSeconds(10)) });
        controls.Open(Key(), "127.0.0.1");
        Assert.That(controls.Consume(Key(), 1024).ChargedCost, Is.EqualTo(2));
        Assert.That(controls.Consume(Key(), 1024, NetworkRequestKind.WorldMutation).ChargedCost, Is.EqualTo(5));
        Assert.That(controls.Consume(Key(), 2048).Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
        Assert.That(controls.Consume(Key(), 65536).Verdict, Is.EqualTo(Verdict.UnsafeInput));
    }

    [Test]
    public void SourceEventsHaveBoundedCooldownAcrossConnectionsAndProxyNeverBecomesTarget()
    {
        var clock = new NetworkTestClock();
        var controls = new NetworkControls(clock, Options with { ConnectionBudget = new(8, 2, 1, TimeSpan.FromSeconds(10)) });
        controls.Open(Key(), "203.0.113.8", true, "198.51.100.9", true);
        var first = controls.Consume(Key(), 65535);
        Assert.That(first.SourceEvent, Is.Not.Null);
        Assert.That(first.SourceEvent!.ProxyPeer, Is.True);
        Assert.That(first.SourceEvent.ProposedTarget, Is.Null);
        Assert.That(controls.Consume(Key(), 65535).SourceEvent, Is.Null);
        controls.Open(Key(5), "203.0.113.8");
        Assert.That(controls.Consume(Key(5), 65535).SourceEvent, Is.Null);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(controls.Consume(Key(), 65535).SourceEvent, Is.Not.Null);
    }

    [Test]
    public void SourceCapacityExpiresAndTimeoutScanWorkIsBounded()
    {
        var clock = new NetworkTestClock();
        var controls = new NetworkControls(clock, Options with { SourceBudget = new(1, 100, 10, TimeSpan.FromSeconds(2)) });
        controls.Open(Key(), "203.0.113.8"); controls.Close(Key());
        Assert.That(controls.Open(Key(generation: 2), "198.51.100.9").Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(controls.Open(Key(generation: 3), "198.51.100.9").Disposition, Is.EqualTo(NetworkDisposition.Allow));
        clock.Advance(TimeSpan.FromSeconds(2));
        int expired = 0;
        for (int i = 0; i < 8; i++) expired += controls.InspectTimeouts(1).Length;
        Assert.That(expired, Is.EqualTo(1));
        Assert.That(controls.Count, Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => controls.InspectTimeouts(65));
    }

    [TestCase("203.0.113.8; remove-all")]
    [TestCase("player-name")]
    [TestCase("http://127.0.0.1")]
    public void PlayerTextAndUnverifiedProxyClaimsCannotBecomeSocketSources(string input)
    {
        var controls = new NetworkControls(new NetworkTestClock(), Options);
        Assert.That(controls.Open(Key(), input).Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
        Assert.That(controls.Open(Key(), "127.0.0.1", verifiedClientAddress: "203.0.113.8").Reason, Is.EqualTo("unverified-proxy-address-claim"));
        Assert.That(controls.Count, Is.Zero);
    }
}

internal sealed class NetworkTestClock : TimeProvider
{
    private long ticks;
    private DateTimeOffset utc = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => utc;
    internal void Advance(TimeSpan amount) { ticks += amount.Ticks; utc += amount; }
    internal void JumpUtc(TimeSpan amount) => utc += amount;
}
