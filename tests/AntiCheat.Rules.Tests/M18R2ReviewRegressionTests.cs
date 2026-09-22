// Reviewer-proposed pure-rule regression cases for the uploaded r2 source.
// These cases are intentionally run in the Rules project only. They do not
// claim a native-game false-positive reproduction.
using System;
using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18R2ReviewRegressionTests
{
    private static SessionKey NewSession() => new(Guid.NewGuid(), 1, 7, 1);

    private static M18GroundItemPlayerControlObservation GroundControl(SessionKey s)
        => new(s, 731, 320f, 480f, true, true, true, true, true, false)
        {
            ServerActorX = 320f,
            ServerActorY = 480f,
            ServerActorPositionSnapshotComplete = true,
        };

    private static M18GroundItemClearObservation Pickup(SessionKey s, int slot)
        => new(s, 731, slot, 1, 2, 1, 320f, 480f, float.NaN, float.NaN,
            true, true, false, false, true, true, true, true)
        {
            PacketId = 151,
            RequestCoordinatesComplete = false,
            ActorX = 320f,
            ActorY = 480f,
            ActorPositionSnapshotComplete = true,
        };

    [Test]
    public void SamePositionControlsBetweenThreeLocalPickupsMustNotCreateWipeProof()
    {
        var s = NewSession();
        var q = new M18GroundItemClearQueue(M18GroundItemClearQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true, WindowTicks = 60 });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, GroundControl(s));
        q.ObservePlayerControls(2, GroundControl(s));
        var first = q.Observe(3, Pickup(s, 10));
        q.ObservePlayerControls(4, GroundControl(s));
        var second = q.Observe(5, Pickup(s, 11));
        q.ObservePlayerControls(6, GroundControl(s));
        var third = q.Observe(7, Pickup(s, 12));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Pass),
                "A same-position control update must not turn normal pickup into wipe stop-loss.");
            Assert.That(third.ControlJumpDetected, Is.False);
            Assert.That(third.RemoteOrMismatched, Is.False);
            Assert.That(third.IsSanctionCandidate, Is.False,
                "No movement, no remote target, and three valid local clears do not prove WipeGroundItems.");
        });
    }

    private static M18NpcPlayerControlObservation NpcControl(SessionKey s, float x = 150f, float y = 150f)
        => new(s, 731, x, y, true, true, true, true, true, false)
        {
            ServerActorX = x,
            ServerActorY = y,
            ServerActorPositionSnapshotComplete = true,
        };

    private static M18NpcStrikeObservation Hit(SessionKey s, int slot,
        int damage = 10, int maxLife = 5, float x = 200f, float y = 200f)
        => new(s, 731, slot, 1, 1, damage, damage, true, true, true, true, true, true)
        {
            WorldId = 1,
            Stage = M18NpcStrikeStage.PreHardmode,
            StageSnapshotComplete = true,
            TargetLife = maxLife,
            TargetLifeMax = maxLife,
            TargetX = x,
            TargetY = y,
            TargetPositionSnapshotComplete = true,
            ActorX = x,
            ActorY = y,
            ActorPositionSnapshotComplete = true,
            WireKnockback = 1f,
            WireDirection = 2,
            WireCriticalFlag = 0,
        };

    private static M18NpcStrikePostNativeObservation Death(M18NpcStrikeObservation h,
        M18NpcStrikeQueueDecision initial)
        => new(h.Session, h.AccountId, h.TargetSlot, h.TargetGeneration, h.TargetType,
            h.WireDamage, h.ReceiverDamage, h.TargetLifeMax, h.TargetLife, -5,
            false, false, h.Stage, true,
            initial.ButcherPositionContextComplete, initial.ButcherPreControlAtTarget,
            initial.ButcherControlJumpDetected, true, true, true, true, true, true);

    [Test]
    public void IdenticalControlsCannotBeAControlJumpOrCrossTargetSanction()
    {
        var s = NewSession();
        var q = new M18NpcStrikeQueue(M18NpcStrikeQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, NpcControl(s));
        q.ObservePlayerControls(2, NpcControl(s));
        var hit = Hit(s, 10);
        var first = q.Observe(3, hit);
        q.ObservePostNative(3, Death(hit, first), first);
        q.ObservePlayerControls(4, NpcControl(s));
        q.ObservePlayerControls(5, NpcControl(s));
        var second = q.Observe(6, Hit(s, 11));

        Assert.Multiple(() =>
        {
            Assert.That(first.ButcherControlJumpDetected, Is.False,
                "Previous and current controls are identical; distance-to-NPC is not player displacement.");
            Assert.That(second.ButcherControlJumpDetected, Is.False);
            Assert.That(second.IsButcherSequenceSanctionCandidate, Is.False,
                "Two nearby overkills without player displacement must not inherit a fictitious jump proof.");
        });
    }

    [Test]
    public void MatchingContinuationMustNotBorrowDeathFromDifferentSignature()
    {
        var s = NewSession();
        var q = new M18NpcStrikeQueue(M18NpcStrikeQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, NpcControl(s, 100f, 100f));
        q.ObservePlayerControls(2, NpcControl(s, 640f, 480f) with
        { ServerActorX = 100f, ServerActorY = 100f });
        var a = Hit(s, 10, 10, 5, 640f, 480f);
        var first = q.Observe(3, a);
        q.ObservePostNative(3, Death(a, first), first);
        q.ObservePlayerControls(4, NpcControl(s, 100f, 100f));

        q.ObservePlayerControls(5, NpcControl(s, 700f, 480f) with
        { ServerActorX = 100f, ServerActorY = 100f });
        q.Observe(6, Hit(s, 11, 11, 500, 700f, 480f));
        q.ObservePlayerControls(7, NpcControl(s, 100f, 100f));
        q.ObservePlayerControls(8, NpcControl(s, 760f, 480f) with
        { ServerActorX = 100f, ServerActorY = 100f });
        var third = q.Observe(9, Hit(s, 12, 11, 5, 760f, 480f));

        Assert.That(third.IsButcherSequenceSanctionCandidate, Is.False,
            "The current continuation matches a non-death segment; a separate death cannot satisfy its death prerequisite.");
    }

    [Test]
    public void DelayedDeathCannotAttachToReplacementSignatureOnSameTarget()
    {
        var s = NewSession();
        var q = new M18NpcStrikeQueue(M18NpcStrikeQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, NpcControl(s, 100f, 100f));
        q.ObservePlayerControls(2, NpcControl(s, 640f, 480f) with
        { ServerActorX = 100f, ServerActorY = 100f });
        var firstHit = Hit(s, 10, damage: 10, maxLife: 5, x: 640f, y: 480f);
        var first = q.Observe(3, firstHit);
        var replacement = q.Observe(4, Hit(s, 10, damage: 11, maxLife: 500,
            x: 640f, y: 480f));
        Assert.That(first.ButcherPendingSequenceId, Is.Not.EqualTo(replacement.ButcherPendingSequenceId));

        // The native completion belongs to the first request. The pending
        // sequence was replaced by a different signature before it arrived.
        q.ObservePostNative(5, Death(firstHit, first), first);
        q.ObservePlayerControls(6, NpcControl(s, 100f, 100f));
        q.ObservePlayerControls(7, NpcControl(s, 700f, 480f) with
        { ServerActorX = 100f, ServerActorY = 100f });
        var continuation = q.Observe(8, Hit(s, 11, damage: 11, maxLife: 5,
            x: 700f, y: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(continuation.ButcherCrossTargetContinuation, Is.True);
            Assert.That(continuation.ButcherMatchingTargetDeathSequences, Is.Zero);
            Assert.That(continuation.IsButcherSequenceSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void PreLoginControlsCannotBecomeAuthenticatedButcherEvidence()
    {
        var s = NewSession();
        var q = new M18NpcStrikeQueue(M18NpcStrikeQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, NpcControl(s, 100f, 100f) with { AccountId = 0 });
        q.ObservePlayerControls(2, NpcControl(s, 640f, 480f) with
        { AccountId = 0, ServerActorX = 100f, ServerActorY = 100f });

        var afterLogin = q.Observe(3, Hit(s, 10, x: 640f, y: 480f));

        Assert.Multiple(() =>
        {
            Assert.That(afterLogin.ButcherPreControlAtTarget, Is.False);
            Assert.That(afterLogin.ButcherControlJumpDetected, Is.False);
            Assert.That(afterLogin.IsButcherSequenceSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void PreLoginControlsCannotBecomeAuthenticatedGroundClearEvidence()
    {
        var s = NewSession();
        var q = new M18GroundItemClearQueue(M18GroundItemClearQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, GroundControl(s) with
        { AccountId = 0, PositionX = 100f, PositionY = 100f,
          ServerActorX = 100f, ServerActorY = 100f });
        q.ObservePlayerControls(2, GroundControl(s) with
        { AccountId = 0, PositionX = 640f, PositionY = 480f,
          ServerActorX = 100f, ServerActorY = 100f });

        var afterLogin = q.Observe(3, Pickup(s, 10) with
        { TargetX = 640f, TargetY = 480f, ActorX = 640f, ActorY = 480f });

        Assert.Multiple(() =>
        {
            Assert.That(afterLogin.PreControlAtTarget, Is.False);
            Assert.That(afterLogin.WipeSequenceDetected, Is.False);
            Assert.That(afterLogin.IsSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void CancelledOrUnacceptedTargetControlCannotFormButcherJump()
    {
        var s = NewSession();
        var q = new M18NpcStrikeQueue(M18NpcStrikeQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, NpcControl(s, 100f, 100f));
        q.ObservePlayerControls(2, NpcControl(s, 640f, 480f) with
        { AlreadyCancelled = true, ServerActorX = 100f, ServerActorY = 100f });
        var cancelled = q.Observe(3, Hit(s, 10, x: 640f, y: 480f));
        q.ObservePlayerControls(4, NpcControl(s, 640f, 480f) with
        { ServerActorX = 100f, ServerActorY = 100f });
        var unaccepted = q.Observe(5, Hit(s, 11, x: 640f, y: 480f) with
        { ActorX = 100f, ActorY = 100f });

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.ButcherPreControlAtTarget, Is.False);
            Assert.That(unaccepted.ButcherPreControlAtTarget, Is.False);
            Assert.That(unaccepted.ButcherControlJumpDetected, Is.False);
            Assert.That(unaccepted.IsButcherSequenceSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void CancelledOrUnacceptedTargetControlCannotFormGroundClearSequence()
    {
        var s = NewSession();
        var q = new M18GroundItemClearQueue(M18GroundItemClearQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        q.AdvanceWorld(s.WorldEpoch);
        q.ObservePlayerControls(1, GroundControl(s) with
        { PositionX = 100f, PositionY = 100f,
          ServerActorX = 100f, ServerActorY = 100f });
        q.ObservePlayerControls(2, GroundControl(s) with
        { PositionX = 640f, PositionY = 480f, AlreadyCancelled = true,
          ServerActorX = 100f, ServerActorY = 100f });
        var cancelled = q.Observe(3, Pickup(s, 10) with
        { TargetX = 640f, TargetY = 480f, ActorX = 640f, ActorY = 480f });
        q.ObservePlayerControls(4, GroundControl(s) with
        { PositionX = 640f, PositionY = 480f,
          ServerActorX = 100f, ServerActorY = 100f });
        var unaccepted = q.Observe(5, Pickup(s, 11) with
        { TargetX = 640f, TargetY = 480f, ActorX = 100f, ActorY = 100f });

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.WipeSequenceDetected, Is.False);
            Assert.That(unaccepted.PreControlAtTarget, Is.False);
            Assert.That(unaccepted.WipeSequenceDetected, Is.False);
            Assert.That(unaccepted.IsSanctionCandidate, Is.False);
        });
    }

    [Test]
    public void AlreadyCancelledRequestsAreNotCountedOrControlledAgain()
    {
        var s = NewSession();
        var strikes = new M18NpcStrikeQueue(M18NpcStrikeQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        var clears = new M18GroundItemClearQueue(M18GroundItemClearQueueOptions.TestLabCandidate with
        { EnablePermanentSanctions = true });
        strikes.AdvanceWorld(s.WorldEpoch);
        clears.AdvanceWorld(s.WorldEpoch);

        var strike = strikes.Observe(1, Hit(s, 10, damage: 9999) with { AlreadyCancelled = true });
        var clear = clears.Observe(1, Pickup(s, 10) with { AlreadyCancelled = true });

        Assert.Multiple(() =>
        {
            Assert.That(strike.Counted, Is.False);
            Assert.That(strike.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(strike.IsButcherSequenceSanctionCandidate, Is.False);
            Assert.That(clear.Counted, Is.False);
            Assert.That(clear.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(clear.IsSanctionCandidate, Is.False);
        });
    }
}
