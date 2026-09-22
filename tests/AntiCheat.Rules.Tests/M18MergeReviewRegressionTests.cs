// Merge-gate regressions for source commit
// f9a71408031e679b69dc34d87281b391b8ca869d.
// The exact attachment was run before this comment update: 4/4 failed on that
// source HEAD and 4/4 passed on the selective review worktree. These are pure
// Rules-level counterexamples, not reproduced stock-client false bans.
using System;
using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18MergeReviewRegressionTests
{
    private const long Account = 731;
    private static SessionKey NewSession() => new(Guid.NewGuid(), 1, 7, 1);

    private static M18GroundItemPlayerControlObservation GroundControl(
        SessionKey session, float x, long accountId = Account)
        => new(session, accountId, x, 480f,
            PositionSnapshotComplete: true,
            ParseComplete: true,
            ClientOrigin: true,
            BeforeSideEffects: true,
            AttributionComplete: accountId > 0,
            AlreadyCancelled: false);

    private static M18GroundItemClearObservation Pickup(
        SessionKey session, int slot, float x, long accountId = Account)
        => new(session, accountId, slot, 1, 2, 1,
            x, 480f, float.NaN, float.NaN,
            TargetSnapshotComplete: true,
            TargetActive: true,
            TargetBeingGrabbed: false,
            NormalPickupShape: false,
            ParseComplete: true,
            ClientOrigin: true,
            BeforeSideEffects: true,
            AttributionComplete: accountId > 0)
        {
            PacketId = 151,
            RequestCoordinatesComplete = false,
            ActorX = x,
            ActorY = 480f,
            ActorPositionSnapshotComplete = true,
        };

    private static M18NpcPlayerControlObservation NpcControl(
        SessionKey session, float x, long accountId = Account)
        => new(session, accountId, x, 480f,
            PositionSnapshotComplete: true,
            ParseComplete: true,
            ClientOrigin: true,
            BeforeSideEffects: true,
            AttributionComplete: accountId > 0,
            AlreadyCancelled: false);

    private static M18NpcStrikeObservation Hit(
        SessionKey session, int slot, float x, long accountId = Account)
        => new(session, accountId, slot, 1, 1, 10, 10,
            TargetSnapshotComplete: true,
            TargetActive: true,
            TargetGenerationMatchesCurrent: true,
            ClientOrigin: true,
            AttributionComplete: accountId > 0,
            LegalExceptionsExcluded: true)
        {
            WorldId = 1,
            Stage = M18NpcStrikeStage.PreHardmode,
            StageSnapshotComplete = true,
            TargetLife = 100,
            TargetLifeMax = 100,
            TargetX = x,
            TargetY = 480f,
            TargetPositionSnapshotComplete = true,
            WireKnockback = 1f,
            WireDirection = 2,
            WireCriticalFlag = 0,
        };

    [Test]
    public void F08_TwoOrdinaryPickupsMustNotSupplyTargetsForAnUnrelatedThirdJump()
    {
        var session = NewSession();
        var queue = new M18GroundItemClearQueue(
            M18GroundItemClearQueueOptions.TestLabCandidate with
            { WindowTicks = 60, EnablePermanentSanctions = true });
        queue.AdvanceWorld(session.WorldEpoch);

        queue.ObservePlayerControls(1, GroundControl(session, 320f));
        queue.ObservePlayerControls(2, GroundControl(session, 320f));
        var first = queue.Observe(3, Pickup(session, 10, 320f));
        var second = queue.Observe(4, Pickup(session, 11, 320f));
        // A 20px movement is not, on its own, a completed wipe sequence.
        // There is no return to the saved position and no closed target pair.
        queue.ObservePlayerControls(5, GroundControl(session, 340f));
        var third = queue.Observe(6, Pickup(session, 12, 340f));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(third.CompletedWipeSequences, Is.Zero);
            Assert.That(third.BatchContinuationDetected, Is.False);
            Assert.That(third.SuspiciousClearCount, Is.Zero);
            Assert.That(third.ServerPositionAligned, Is.True);
            Assert.That(third.IsSanctionCandidate, Is.False,
                "Unrelated ordinary pickups must not complete the distinct-target proof for a new attempt.");
        });
    }

    [Test]
    public void F06_TwoStationaryNonlethalHitsMustNotBeAButcherStopLoss()
    {
        var session = NewSession();
        var queue = new M18NpcStrikeQueue(
            M18NpcStrikeQueueOptions.TestLabCandidate with
            { WindowTicks = 60, EnablePermanentSanctions = true });
        queue.AdvanceWorld(session.WorldEpoch);
        queue.ObservePlayerControls(1, NpcControl(session, 320f));
        queue.ObservePlayerControls(2, NpcControl(session, 320f));
        var first = queue.Observe(3, Hit(session, 10, 320f));
        var second = queue.Observe(4, Hit(session, 10, 320f));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.Not.EqualTo(ControlAction.Block));
            Assert.That(second.ButcherControlJumpDetected, Is.False);
            Assert.That(second.ButcherCrossTargetContinuation, Is.False);
            Assert.That(second.ButcherCompletedTargetDeathSequences, Is.Zero);
            Assert.That(second.TargetPositiveDamageCount, Is.LessThan(64));
            Assert.That(second.IsButcherSequenceSanctionCandidate, Is.False);
            Assert.That(second.Action, Is.Not.EqualTo(ControlAction.Block),
                "Two identical nearby hit inputs alone do not prove an illegal Butcher sequence or exhaust the budget.");
        });
    }

    [Test]
    public void F06_FirstAuthenticatedStrikeMustNotInheritUnauthenticatedControls()
    {
        var session = NewSession();
        var queue = new M18NpcStrikeQueue(
            M18NpcStrikeQueueOptions.TestLabCandidate with
            { WindowTicks = 60, EnablePermanentSanctions = true });
        queue.AdvanceWorld(session.WorldEpoch);
        queue.ObservePlayerControls(1, NpcControl(session, 100f, accountId: 0));
        queue.ObservePlayerControls(2, NpcControl(session, 640f, accountId: 0));
        var decision = queue.Observe(3, Hit(session, 10, 640f));

        Assert.Multiple(() =>
        {
            Assert.That(decision.ButcherPositionContextComplete, Is.False,
                "No control sample in this tracker is attributed to the newly authenticated account.");
            Assert.That(decision.ButcherControlJumpDetected, Is.False);
            Assert.That(decision.IsButcherSequenceSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void F08_FirstAuthenticatedPickupMustNotInheritUnauthenticatedControls()
    {
        var session = NewSession();
        var queue = new M18GroundItemClearQueue(
            M18GroundItemClearQueueOptions.TestLabCandidate with
            { WindowTicks = 60, EnablePermanentSanctions = true });
        queue.AdvanceWorld(session.WorldEpoch);
        queue.ObservePlayerControls(1, GroundControl(session, 320f, accountId: 0));
        queue.ObservePlayerControls(2, GroundControl(session, 340f, accountId: 0));
        var decision = queue.Observe(3, Pickup(session, 10, 340f));

        Assert.Multiple(() =>
        {
            Assert.That(decision.ServerPositionAligned, Is.True);
            Assert.That(decision.WipeSequenceDetected, Is.False,
                "Unauthenticated declaration history must not become the new account's active wipe evidence.");
            Assert.That(decision.Action, Is.EqualTo(ControlAction.Pass));
        });
    }
}
