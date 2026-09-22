using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18GroundItemClearQueueRuleTests
{
    private static readonly SessionKey Session = new(Guid.NewGuid(), 1, 7, 1);

    private static M18GroundItemClearQueueOptions Options => new()
    {
        Enabled = true,
        WindowTicks = 3,
        PerSessionClearCapacity = 2,
        EventCapacity = 3,
        MaxRequestPositionDelta = 64f,
        EnablePreForwardBlocks = true,
    };

    private static M18GroundItemClearObservation Observation(
        SessionKey? session = null,
        long accountId = 731,
        int targetSlot = 12,
        int targetGeneration = 4,
        int targetType = 2,
        int targetStack = 10,
        float targetX = 320f,
        float targetY = 480f,
        float requestX = 320f,
        float requestY = 480f,
        bool targetSnapshotComplete = true,
        bool targetActive = true,
        bool targetBeingGrabbed = false,
        bool normalPickupShape = true,
        bool parseComplete = true,
        bool clientOrigin = true,
        bool beforeSideEffects = true,
        bool attributionComplete = true,
        byte packetId = 0,
        bool requestCoordinatesComplete = true,
        float? actorX = null,
        float? actorY = null,
        bool actorPositionSnapshotComplete = true)
        => new(session ?? Session, accountId, targetSlot, targetGeneration, targetType, targetStack,
            targetX, targetY, requestX, requestY, targetSnapshotComplete, targetActive,
            targetBeingGrabbed, normalPickupShape, parseComplete, clientOrigin,
            beforeSideEffects, attributionComplete)
        {
            PacketId = packetId,
            RequestCoordinatesComplete = requestCoordinatesComplete,
            ActorX = actorX ?? targetX,
            ActorY = actorY ?? targetY,
            ActorPositionSnapshotComplete = actorPositionSnapshotComplete,
        };

    [Test]
    public void NormalPickupIsAllowedButARemoteTargetMismatchStopsTheConnectionOnly()
    {
        var queue = new M18GroundItemClearQueue(Options);
        queue.AdvanceWorld(Session.WorldEpoch);

        var pickup = queue.Observe(1, Observation());
        var remote = queue.Observe(1, Observation(targetSlot: 13, targetGeneration: 2,
            requestX: 0, requestY: 0, normalPickupShape: false));
        var rule = M18GroundItemClearQueueRules.Observe(
            new(Session, "fixture", "fixture", true, true, true, true), remote);

        Assert.Multiple(() =>
        {
            Assert.That(pickup.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(pickup.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(remote.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(remote.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(remote.IsResourceBlock, Is.True);
            Assert.That(remote.RemoteOrMismatched, Is.True);
            Assert.That(rule.PredicateSatisfied, Is.False);
            Assert.That(rule.PrerequisitesComplete, Is.False);
        });
    }

    [Test]
    public void Packet151WithoutWireCoordinatesUsesAcceptedPlayerPositionInsteadOfInventingMismatch()
    {
        var queue = new M18GroundItemClearQueue(Options with { EnablePermanentSanctions = true });
        queue.AdvanceWorld(Session.WorldEpoch);

        var decision = queue.Observe(1, Observation(
            targetSlot: 18, targetGeneration: 6, targetX: 320f, targetY: 480f,
            requestX: float.NaN, requestY: float.NaN, normalPickupShape: false,
            packetId: 151, requestCoordinatesComplete: false,
            actorX: 320f, actorY: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(decision.RequestCoordinatesComplete, Is.False);
            Assert.That(decision.RemoteOrMismatched, Is.False);
            Assert.That(decision.ServerPositionAligned, Is.True);
            Assert.That(decision.IsSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void NormalConcentratedPickupAndOtherPlayerHandoffRemainLegal()
    {
        var queue = new M18GroundItemClearQueue(Options with { EnablePermanentSanctions = true });
        queue.AdvanceWorld(Session.WorldEpoch);
        var pickup = queue.Observe(1, Observation(targetSlot: 30, targetGeneration: 2,
            targetX: 320f, targetY: 480f, requestX: 320f, requestY: 480f,
            packetId: 21, actorX: 320f, actorY: 480f));

        var otherSession = new SessionKey(Guid.NewGuid(), Session.WorldEpoch, 2, 1);
        var handoff = queue.Observe(1, Observation(session: otherSession, accountId: 902,
            targetSlot: 30, targetGeneration: 3, targetX: 320f, targetY: 480f,
            requestX: 320f, requestY: 480f, packetId: 21,
            actorX: 320f, actorY: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(pickup.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(pickup.RemoteOrMismatched, Is.False);
            Assert.That(handoff.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(handoff.RemoteOrMismatched, Is.False);
            Assert.That(handoff.IsSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void CapacityIsFiniteAndDoesNotTurnIncompleteOrUnknownIntoBlock()
    {
        var queue = new M18GroundItemClearQueue(Options);
        queue.AdvanceWorld(Session.WorldEpoch);

        var incomplete = queue.Observe(1, Observation(targetSnapshotComplete: false));
        var first = queue.Observe(1, Observation());
        var second = queue.Observe(1, Observation(targetSlot: 13, targetGeneration: 2));
        var exhausted = queue.Observe(1, Observation(targetSlot: 14, targetGeneration: 3));
        var expired = queue.Observe(4, Observation(targetSlot: 15, targetGeneration: 4));

        Assert.Multiple(() =>
        {
            Assert.That(incomplete.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(incomplete.Counted, Is.False);
            Assert.That(first.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(exhausted.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(exhausted.CapacityExhausted, Is.True);
            Assert.That(exhausted.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(expired.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(expired.SessionClearCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void CandidateControlCanBeDisabledWithoutChangingObservationOrSanctionContract()
    {
        var queue = new M18GroundItemClearQueue(Options with { EnablePreForwardBlocks = false });
        queue.AdvanceWorld(Session.WorldEpoch);
        var decision = queue.Observe(1, Observation(requestX: 0, requestY: 0, normalPickupShape: false));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(decision.Reason, Does.EndWith("-block-disabled"));
            Assert.That(decision.Counted, Is.True);
        });
    }

    [Test]
    public void WipeSequenceStopsAtTheWriteBeforeBoundaryEvenWhenPayloadCoordinatesAreAccurate()
    {
        var queue = new M18GroundItemClearQueue(Options with
        {
            WindowTicks = 10,
            PerSessionClearCapacity = 32,
            EventCapacity = 32,
        });
        queue.AdvanceWorld(Session.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(640f, 480f));

        var decision = queue.Observe(1, Observation(targetSlot: 20, targetGeneration: 8,
            targetX: 640f, targetY: 480f, requestX: 640f, requestY: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(decision.IsWipeSequenceBlock, Is.True);
            Assert.That(decision.RemoteOrMismatched, Is.False);
            Assert.That(decision.ControlJumpDetected, Is.True);
            Assert.That(decision.RequestTargetAligned, Is.True);
            Assert.That(decision.Reason, Is.EqualTo("ground-item-clear-wipe-sequence-stop-loss"));
        });
    }

    [Test]
    public void WipeBatchStopsOnTheNextTargetAfterACompletedRestorePair()
    {
        var queue = new M18GroundItemClearQueue(Options with { WindowTicks = 10 });
        queue.AdvanceWorld(Session.WorldEpoch);
        queue.ObservePlayerControls(1, Control(320f, 480f));
        queue.ObservePlayerControls(1, Control(324f, 480f));
        var first = queue.Observe(1, Observation(targetSlot: 20, targetGeneration: 8,
            targetX: 324f, targetY: 480f, requestX: 324f, requestY: 480f));
        queue.ObservePlayerControls(1, Control(320f, 480f));
        queue.ObservePlayerControls(1, Control(332f, 480f));
        var second = queue.Observe(1, Observation(targetSlot: 21, targetGeneration: 9,
            targetX: 332f, targetY: 480f, requestX: 332f, requestY: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(first.WipeSequenceDetected, Is.False);
            Assert.That(second.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(second.IsWipeSequenceBlock, Is.True);
            Assert.That(second.BatchContinuationDetected, Is.True);
            Assert.That(second.CompletedWipeSequences, Is.EqualTo(1));
        });
    }

    [Test]
    public void ThirdRemotePacket151InAnActiveWipeSequenceBecomesAnAccountSanctionCandidate()
    {
        var queue = new M18GroundItemClearQueue(Options with
        {
            WindowTicks = 10,
            EnablePermanentSanctions = true,
        });
        queue.AdvanceWorld(Session.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(640f, 480f));
        var first = queue.Observe(1, Observation(targetSlot: 20, targetGeneration: 8,
            targetX: 640f, targetY: 480f, requestX: float.NaN, requestY: float.NaN,
            normalPickupShape: false, packetId: 151, requestCoordinatesComplete: false,
            actorX: 100f, actorY: 100f));
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(700f, 480f));
        var second = queue.Observe(1, Observation(targetSlot: 21, targetGeneration: 9,
            targetX: 700f, targetY: 480f, requestX: float.NaN, requestY: float.NaN,
            normalPickupShape: false, packetId: 151, requestCoordinatesComplete: false,
            actorX: 100f, actorY: 100f));
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(760f, 480f));
        var third = queue.Observe(1, Observation(targetSlot: 22, targetGeneration: 10,
            targetX: 760f, targetY: 480f, requestX: float.NaN, requestY: float.NaN,
            normalPickupShape: false, packetId: 151, requestCoordinatesComplete: false,
            actorX: 100f, actorY: 100f));
        var rule = M18GroundItemClearQueueRules.Observe(
            new(Session, "fixture", "fixture", true, true, true, true), third);

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(first.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(first.PermanentSanctionCandidate, Is.False);
            Assert.That(second.BatchContinuationDetected, Is.True);
            Assert.That(second.CompletedWipeSequences, Is.EqualTo(1));
            Assert.That(second.IsSanctionCandidate, Is.False);
            Assert.That(third.ControlJumpDetected, Is.True);
            Assert.That(third.SuspiciousClearCount, Is.EqualTo(3));
            Assert.That(third.RequestCoordinatesComplete, Is.False);
            Assert.That(third.RemoteOrMismatched, Is.True);
            Assert.That(third.IsSanctionCandidate, Is.True);
            Assert.That(rule.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(rule.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(rule.PredicateSatisfied, Is.True);
            Assert.That(rule.PrerequisitesComplete, Is.True);
            Assert.That(rule.Facts["sanctionContract"], Does.Contain("account-sanction"));
        });
    }

    [Test]
    public void ThirdAlignedPacket151InAnActiveWipeSequenceStillBecomesAnAccountSanctionCandidate()
    {
        var queue = new M18GroundItemClearQueue(Options with
        {
            WindowTicks = 10,
            EnablePermanentSanctions = true,
        });
        queue.AdvanceWorld(Session.WorldEpoch);
        queue.ObservePlayerControls(1, Control(640f, 480f));
        var first = queue.Observe(1, Observation(targetSlot: 20, targetGeneration: 8,
            targetX: 640f, targetY: 480f, requestX: float.NaN, requestY: float.NaN,
            normalPickupShape: false, packetId: 151, requestCoordinatesComplete: false,
            actorX: 640f, actorY: 480f));
        queue.ObservePlayerControls(1, Control(640f, 480f));
        queue.ObservePlayerControls(1, Control(700f, 480f));
        var second = queue.Observe(1, Observation(targetSlot: 21, targetGeneration: 9,
            targetX: 700f, targetY: 480f, requestX: float.NaN, requestY: float.NaN,
            normalPickupShape: false, packetId: 151, requestCoordinatesComplete: false,
            actorX: 700f, actorY: 480f));
        queue.ObservePlayerControls(1, Control(640f, 480f));
        queue.ObservePlayerControls(1, Control(760f, 480f));
        var third = queue.Observe(1, Observation(targetSlot: 22, targetGeneration: 10,
            targetX: 760f, targetY: 480f, requestX: float.NaN, requestY: float.NaN,
            normalPickupShape: false, packetId: 151, requestCoordinatesComplete: false,
            actorX: 760f, actorY: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(second.IsWipeSequenceBlock, Is.True);
            Assert.That(third.RemoteOrMismatched, Is.False);
            Assert.That(third.ServerPositionAligned, Is.True);
            Assert.That(third.SessionClearCount, Is.EqualTo(3));
            Assert.That(third.SuspiciousClearCount, Is.Zero);
            Assert.That(third.IsSanctionCandidate, Is.True);
        });
    }

    [Test]
    public void WipeSequenceUsesFinalRequestCoordinatesWhenTheServerItemMoves()
    {
        var queue = new M18GroundItemClearQueue(Options with { WindowTicks = 10 });
        queue.AdvanceWorld(Session.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(640f, 480f));

        var decision = queue.Observe(1, Observation(targetSlot: 22, targetGeneration: 10,
            targetX: 650f, targetY: 490f, requestX: 640f, requestY: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(decision.IsWipeSequenceBlock, Is.True);
            Assert.That(decision.PreControlAtTarget, Is.True);
            Assert.That(decision.ControlJumpDetected, Is.True);
            Assert.That(decision.RequestTargetAligned, Is.True);
        });
    }

    [Test]
    public void UnauthenticatedRemoteClearStillGetsConnectionStopLossButNormalPickupIsNotPromoted()
    {
        var queue = new M18GroundItemClearQueue(Options);
        queue.AdvanceWorld(Session.WorldEpoch);
        var remote = queue.Observe(1, Observation(accountId: 0, attributionComplete: false,
            requestX: 0f, requestY: 0f, normalPickupShape: false));
        var normal = queue.Observe(1, Observation(accountId: 0, attributionComplete: false,
            targetSlot: 13, targetGeneration: 2));

        Assert.Multiple(() =>
        {
            Assert.That(remote.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(remote.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(remote.IdentityComplete, Is.False);
            Assert.That(normal.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(normal.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(normal.Reason, Is.EqualTo("ground-item-clear-identity-incomplete"));
        });
    }

    private static M18GroundItemPlayerControlObservation Control(float x, float y,
        bool alreadyCancelled = false)
        => new(Session, 731, x, y, true, true, true, true, true, alreadyCancelled);
}
