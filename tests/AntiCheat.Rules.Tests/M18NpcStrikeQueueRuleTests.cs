using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18NpcStrikeQueueRuleTests
{
    private static M18NpcStrikeQueueOptions Options => new()
    {
        Enabled = true,
        WindowTicks = 3,
        LowDamageMaximum = 1,
        PerTargetLowDamageLimit = 3,
        PerSessionLowDamageLimit = 4,
        LowDamageRingCapacity = 4,
        HighSampleCapacity = 2,
    };

    private static M18NpcStrikeObservation Observation(
        SessionKey? session = null,
        long accountId = 1807,
        int targetSlot = 11,
        int targetGeneration = 3,
        int targetType = 1,
        int wireDamage = 1,
        int receiverDamage = 1,
        bool targetSnapshotComplete = true,
        bool targetActive = true,
        bool generationMatches = true,
        bool clientOrigin = true,
        bool attributionComplete = true,
        bool legalExceptionsExcluded = true,
        int worldId = 0,
        M18NpcStrikeStage stage = M18NpcStrikeStage.PreHardmode,
        bool stageSnapshotComplete = true,
        int targetLife = 100,
        int targetLifeMax = 100,
        bool targetFriendly = false,
        bool targetDummy = false,
        float targetX = 0f,
        float targetY = 0f,
        bool targetPositionSnapshotComplete = false,
        float wireKnockback = 0f,
        int wireDirection = 1,
        int wireCriticalFlag = 0,
        bool alreadyCancelled = false,
        bool summonContextComplete = false,
        bool summonBuff = false,
        bool summonEntity = false,
        int summonEntityCount = 0)
        => new(session ?? Actor, accountId, targetSlot, targetGeneration, targetType,
            wireDamage, receiverDamage, targetSnapshotComplete, targetActive,
            generationMatches, clientOrigin, attributionComplete, legalExceptionsExcluded)
        {
            WorldId = worldId,
            Stage = stage,
            StageSnapshotComplete = stageSnapshotComplete,
            TargetLife = targetLife,
            TargetLifeMax = targetLifeMax,
            TargetFriendly = targetFriendly,
            TargetDummy = targetDummy,
            TargetX = targetX,
            TargetY = targetY,
            TargetPositionSnapshotComplete = targetPositionSnapshotComplete || targetX != 0f || targetY != 0f,
            WireKnockback = wireKnockback,
            WireDirection = wireDirection,
            WireCriticalFlag = wireCriticalFlag,
            AlreadyCancelled = alreadyCancelled,
            SummonContextComplete = summonContextComplete,
            SummonMaintenanceBuffObserved = summonBuff,
            MatchingSummonEntityObserved = summonEntity,
            MatchingSummonEntityCount = summonEntityCount,
        };

    [Test]
    public void LowDamageBudgetBlocksOnlyAfterTheBoundedTargetThreshold()
    {
        var queue = new M18NpcStrikeQueue(Options);
        queue.AdvanceWorld(Actor.WorldEpoch);

        var first = queue.Observe(1, Observation());
        var second = queue.Observe(1, Observation());
        var third = queue.Observe(1, Observation());

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(third.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(third.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(third.IsResourceBlock, Is.True);
            Assert.That(third.TargetLowDamageCount, Is.EqualTo(3));
            Assert.That(third.SessionLowDamageCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void SessionBudgetAggregatesDifferentNpcGenerationsButDoesNotCrossExactSessionKeys()
    {
        var queue = new M18NpcStrikeQueue(Options with { PerTargetLowDamageLimit = 8, PerSessionLowDamageLimit = 3 });
        queue.AdvanceWorld(Actor.WorldEpoch);

        var first = queue.Observe(2, Observation(targetSlot: 11, targetGeneration: 3));
        var second = queue.Observe(2, Observation(targetSlot: 12, targetGeneration: 7));
        var third = queue.Observe(2, Observation(targetSlot: 13, targetGeneration: 9));
        var newSession = queue.Observe(2, Observation(session: Actor with { Generation = 13 }, targetSlot: 11));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(third.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(third.TargetLowDamageCount, Is.EqualTo(1));
            Assert.That(third.SessionLowDamageCount, Is.EqualTo(3));
            Assert.That(third.Reason, Is.EqualTo("npc-strike-low-damage-session-budget-exhausted"));
            Assert.That(newSession.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(newSession.SessionLowDamageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void WindowExpiryRemovesOldSamplesAndNpcGenerationIsPartOfTheKey()
    {
        var queue = new M18NpcStrikeQueue(Options with { PerTargetLowDamageLimit = 2, PerSessionLowDamageLimit = 4 });
        queue.AdvanceWorld(Actor.WorldEpoch);

        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Block));
        var afterWindow = queue.Observe(4, Observation());
        var newGenerationFirst = queue.Observe(4, Observation(targetGeneration: 4));
        var newGenerationSecond = queue.Observe(4, Observation(targetGeneration: 4));

        Assert.Multiple(() =>
        {
            Assert.That(afterWindow.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(afterWindow.TargetLowDamageCount, Is.EqualTo(1));
            Assert.That(newGenerationFirst.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(newGenerationFirst.TargetLowDamageCount, Is.EqualTo(1));
            Assert.That(newGenerationSecond.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(newGenerationSecond.TargetLowDamageCount, Is.EqualTo(2));
            Assert.That(newGenerationSecond.SessionLowDamageCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void HighDamageBelowExplicitExtremeLineIsAResettableBoundedRecord()
    {
        var queue = new M18NpcStrikeQueue(Options);
        queue.AdvanceWorld(Actor.WorldEpoch);

        var first = queue.Observe(1, Observation(wireDamage: 1000, receiverDamage: 1000));
        var second = queue.Observe(1, Observation(targetSlot: 12, wireDamage: 2000, receiverDamage: 2000));
        var third = queue.Observe(1, Observation(targetSlot: 13, wireDamage: 3000, receiverDamage: 3000));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(third.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(third.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(third.HighSamplesRetained, Is.EqualTo(2));
            Assert.That(third.HighSamplesDropped, Is.EqualTo(1));
            Assert.That(third.MaxWireDamage, Is.EqualTo(3000));
            Assert.That(third.MaxTargetType, Is.EqualTo(1));
        });
    }

    [Test]
    public void PositiveDamageBudgetCountsRepresentativeAdjustableDamageAcrossNpcTargets()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            PerTargetLowDamageLimit = 3,
            PerSessionLowDamageLimit = 4,
            LowDamageRingCapacity = 8,
            ExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);

        var damage2 = queue.Observe(1, Observation(wireDamage: 2, receiverDamage: 2));
        var damage5 = queue.Observe(1, Observation(wireDamage: 5, receiverDamage: 5));
        var otherTarget = queue.Observe(1, Observation(targetSlot: 12, wireDamage: 9, receiverDamage: 9));
        var sessionLimit = queue.Observe(1, Observation(targetSlot: 12, wireDamage: 17, receiverDamage: 17));

        Assert.Multiple(() =>
        {
            Assert.That(damage2.Counted && damage5.Counted && otherTarget.Counted, Is.True);
            Assert.That(damage2.LowDamage, Is.False);
            Assert.That(damage5.TargetPositiveDamageCount, Is.EqualTo(2));
            Assert.That(otherTarget.SessionPositiveDamageCount, Is.EqualTo(3));
            Assert.That(sessionLimit.Counted, Is.True);
            Assert.That(sessionLimit.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(sessionLimit.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(sessionLimit.Reason, Is.EqualTo("npc-strike-positive-damage-session-budget-exhausted"));
            Assert.That(sessionLimit.SessionPositiveDamageCount, Is.EqualTo(4));
            Assert.That(sessionLimit.TargetPositiveDamageCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void ExplicitExtremeLineBlocksBeforeNativeReceiverWithoutCheatVerdict()
    {
        var queue = new M18NpcStrikeQueue(Options with { ExtremeDamageThreshold = 9999 });
        queue.AdvanceWorld(Actor.WorldEpoch);

        var below = queue.Observe(1, Observation(wireDamage: 9998, receiverDamage: 9998));
        var extreme = queue.Observe(1, Observation(targetSlot: 12, wireDamage: 9999, receiverDamage: 9999));
        var result = M18NpcStrikeQueueRules.Observe(Input, InputFacts(), extreme);

        Assert.Multiple(() =>
        {
            Assert.That(below.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(below.ExtremeDamage, Is.False);
            Assert.That(extreme.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(extreme.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(extreme.IsExtremeDamageBlock, Is.True);
            Assert.That(extreme.ExtremeDamage, Is.True);
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
            Assert.That(result.Facts["strikeQueueClassification"], Is.EqualTo("extreme-damage-preforward-stop"));
            Assert.That(result.Facts["strikeQueueActionContract"], Is.EqualTo("block-before-native-receiver-no-sanction"));
        });
    }

    [Test]
    public void OrdinaryButcherSequenceStopsTheNextTargetWithoutUsingThe64HitBudget()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            PerTargetLowDamageLimit = 64,
            PerSessionLowDamageLimit = 192,
            LowDamageRingCapacity = 256,
            ExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var first = queue.Observe(1, Observation(targetSlot: 11, targetGeneration: 3,
            targetX: 500f, targetY: 500f, wireDamage: 1000, receiverDamage: 1000));
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(700f, 700f));
        var second = queue.Observe(1, Observation(targetSlot: 12, targetGeneration: 4,
            targetX: 700f, targetY: 700f, wireDamage: 1000, receiverDamage: 1000));

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(first.ButcherPatternDetected, Is.False);
            Assert.That(first.TargetLowDamageCount, Is.EqualTo(1));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(second.IsButcherPatternBlock, Is.True);
            Assert.That(second.ButcherCrossTargetContinuation, Is.True);
            Assert.That(second.ButcherCompletedTargetSequences, Is.EqualTo(1));
            Assert.That(second.Reason, Is.EqualTo("npc-strike-ordinary-butcher-sequence-preforward-stop"));
        });
    }

    [Test]
    public void LowDamageCrossTargetButcherSequenceBecomesAnAccountSanctionCandidate()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            EnablePermanentSanctions = true,
            PerTargetLowDamageLimit = 64,
            PerSessionLowDamageLimit = 192,
            LowDamageRingCapacity = 256,
            ExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var first = queue.Observe(1, Observation(targetSlot: 11, targetGeneration: 3,
            targetX: 500f, targetY: 500f, wireDamage: 1, receiverDamage: 1));
        queue.ObservePostNative(1, new M18NpcStrikePostNativeObservation(
            Session: Actor,
            AccountId: 1807,
            TargetSlot: 11,
            TargetGeneration: 3,
            TargetType: 1,
            WireDamage: 1,
            ReceiverDamage: 1,
            TargetLifeMax: 100,
            LifeBefore: 1,
            LifeAfter: 0,
            TargetFriendly: false,
            TargetDummy: false,
            Stage: first.Stage,
            StageSnapshotComplete: true,
            PositionContextComplete: first.ButcherPositionContextComplete,
            PreControlAtTarget: first.ButcherPreControlAtTarget,
            ControlJumpDetected: first.ButcherControlJumpDetected,
            ClientOrigin: true,
            AttributionComplete: true,
            LegalExceptionsExcluded: true,
            NativeStrikeEntryObserved: true,
            LootMethodEntryObserved: true,
            RelayAttemptObserved: true), first);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(700f, 700f));
        var second = queue.Observe(1, Observation(targetSlot: 12, targetGeneration: 4,
            targetX: 700f, targetY: 700f, wireDamage: 1, receiverDamage: 1));
        var rule = M18NpcStrikeQueueRules.Observe(Input, InputFacts(), second);

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(second.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(second.IsButcherPatternBlock, Is.True);
            Assert.That(second.IsButcherSequenceSanctionCandidate, Is.True);
            Assert.That(second.ButcherCrossTargetContinuation, Is.True);
            Assert.That(second.ButcherControlJumpDetected, Is.True);
            Assert.That(second.ButcherCompletedTargetSequences, Is.EqualTo(1));
            Assert.That(rule.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(rule.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(rule.PredicateSatisfied, Is.True);
            Assert.That(rule.Facts["strikeQueueClassification"], Is.EqualTo("ordinary-butcher-sequence-account-sanction-candidate"));
            Assert.That(rule.Facts["strikeQueueActionContract"], Is.EqualTo("account-sanction-after-complete-butcher-sequence"));
        });
    }

    [Test]
    public void CancelledButcherTailRestoreStillClosesTheCompletedTargetSegment()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            EnablePermanentSanctions = true,
            PerTargetLowDamageLimit = 64,
            PerSessionLowDamageLimit = 192,
            LowDamageRingCapacity = 256,
            ExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var first = queue.Observe(1, Observation(targetSlot: 11, targetGeneration: 3,
            targetX: 500f, targetY: 500f, wireDamage: 1, receiverDamage: 1));
        queue.ObservePostNative(1, new M18NpcStrikePostNativeObservation(
            Session: Actor,
            AccountId: 1807,
            TargetSlot: 11,
            TargetGeneration: 3,
            TargetType: 1,
            WireDamage: 1,
            ReceiverDamage: 1,
            TargetLifeMax: 100,
            LifeBefore: 1,
            LifeAfter: 0,
            TargetFriendly: false,
            TargetDummy: false,
            Stage: first.Stage,
            StageSnapshotComplete: true,
            PositionContextComplete: first.ButcherPositionContextComplete,
            PreControlAtTarget: first.ButcherPreControlAtTarget,
            ControlJumpDetected: first.ButcherControlJumpDetected,
            ClientOrigin: true,
            AttributionComplete: true,
            LegalExceptionsExcluded: true,
            NativeStrikeEntryObserved: true,
            LootMethodEntryObserved: true,
            RelayAttemptObserved: true), first);
        queue.ObservePlayerControls(1, Control(100f, 100f, alreadyCancelled: true));
        queue.ObservePlayerControls(1, Control(700f, 700f));
        var second = queue.Observe(1, Observation(targetSlot: 12, targetGeneration: 4,
            targetX: 700f, targetY: 700f, wireDamage: 1, receiverDamage: 1));

        Assert.Multiple(() =>
        {
            Assert.That(second.ButcherCrossTargetContinuation, Is.True);
            Assert.That(second.ButcherCompletedTargetSequences, Is.EqualTo(1));
            Assert.That(second.IsButcherSequenceSanctionCandidate, Is.True);
        });
    }

    [Test]
    public void CompletedDeathEvidenceExpiresWithTheExactTargetSequence()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            WindowTicks = 3,
            EnableButcherSequenceStopLoss = false,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var first = queue.Observe(1, Observation(targetX: 500f, targetY: 500f));
        queue.ObservePostNative(1, new M18NpcStrikePostNativeObservation(
            Actor, 1807, 11, 3, 1, 1, 1, 100, 1, 0, false, false,
            M18NpcStrikeStage.PreHardmode, true, first.ButcherPositionContextComplete,
            first.ButcherPreControlAtTarget, first.ButcherControlJumpDetected,
            true, true, true, true, false, true), first);
        queue.ObservePlayerControls(1, Control(100f, 100f));

        queue.ObservePlayerControls(4, Control(500f, 500f));
        var afterExpiry = queue.Observe(4, Observation(targetSlot: 12, targetGeneration: 4,
            targetX: 500f, targetY: 500f));

        Assert.Multiple(() =>
        {
            Assert.That(first.ButcherCompletedTargetDeathSequences, Is.EqualTo(0));
            Assert.That(afterExpiry.ButcherCompletedTargetDeathSequences, Is.EqualTo(0));
            Assert.That(afterExpiry.ButcherCompletedTargetSequences, Is.EqualTo(0));
        });
    }

    [Test]
    public void DeathBitIsEvictedWithItsCompletedEventInsteadOfLeavingAStaleCounter()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            WindowTicks = 100,
            EnableButcherSequenceStopLoss = false,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);

        for (int index = 0; index < 9; index++)
        {
            float x = 500f + index * 100f;
            queue.ObservePlayerControls(1, Control(100f, 100f));
            queue.ObservePlayerControls(1, Control(x, 500f));
            var strike = queue.Observe(1, Observation(targetSlot: 20 + index,
                targetGeneration: 10 + index, targetX: x, targetY: 500f));
            if (index < 8)
            {
                queue.ObservePostNative(1, new M18NpcStrikePostNativeObservation(
                    Actor, 1807, 20 + index, 10 + index, 1, 1, 1, 100, 1, 0,
                    false, false, M18NpcStrikeStage.PreHardmode, true,
                    strike.ButcherPositionContextComplete, strike.ButcherPreControlAtTarget,
                    strike.ButcherControlJumpDetected, true, true, true, true, false, true), strike);
            }
            queue.ObservePlayerControls(1, Control(100f, 100f));
        }

        queue.ObservePlayerControls(1, Control(1400f, 500f));
        var snapshot = queue.Observe(1, Observation(targetSlot: 29, targetGeneration: 19,
            targetX: 1400f, targetY: 500f));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ButcherCompletedTargetSequences, Is.EqualTo(8));
            Assert.That(snapshot.ButcherCompletedTargetDeathSequences, Is.EqualTo(7));
        });
    }

    [Test]
    public void RepeatedSameTargetSignatureStopsBeforeTheThirdStrikeAndDoesNotNeedADeathBudget()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            PerTargetLowDamageLimit = 64,
            PerSessionLowDamageLimit = 192,
            LowDamageRingCapacity = 256,
            ExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var first = queue.Observe(1, Observation(targetX: 500f, targetY: 500f,
            wireDamage: 1000, receiverDamage: 1000));
        var second = queue.Observe(1, Observation(targetX: 500f, targetY: 500f,
            wireDamage: 1000, receiverDamage: 1000));
        var tail = queue.Observe(1, Observation(targetX: 500f, targetY: 500f,
            wireDamage: 1000, receiverDamage: 1000));
        var result = M18NpcStrikeQueueRules.Observe(Input, InputFacts(), second);

        Assert.Multiple(() =>
        {
            Assert.That(first.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(second.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(second.IsButcherPatternBlock, Is.True);
            Assert.That(second.ButcherRepeatedAttackSignature, Is.True);
            Assert.That(second.ButcherCurrentTargetStrikes, Is.EqualTo(2));
            Assert.That(tail.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(tail.IsButcherPatternBlock, Is.True);
            Assert.That(tail.ButcherCurrentTargetStrikes, Is.EqualTo(3));
            Assert.That(result.Facts["strikeQueueClassification"], Is.EqualTo("ordinary-butcher-sequence-preforward-stop"));
            Assert.That(result.Facts["strikeQueueActionContract"], Is.EqualTo("block-before-native-receiver-no-sanction"));
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
        });
    }

    [Test]
    public void MatchingSummonContextIsRecordedButNeverExemptsAnAbnormalButcherSequence()
    {
        var queue = new M18NpcStrikeQueue(Options with { ExtremeDamageThreshold = 30000 });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var first = queue.Observe(1, Observation(targetX: 500f, targetY: 500f,
            wireDamage: 1000, receiverDamage: 1000,
            summonContextComplete: true, summonBuff: true, summonEntity: true, summonEntityCount: 1));
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(700f, 700f));
        var second = queue.Observe(1, Observation(targetSlot: 12, targetGeneration: 4,
            targetX: 700f, targetY: 700f, wireDamage: 1000, receiverDamage: 1000,
            summonContextComplete: true, summonBuff: true, summonEntity: true, summonEntityCount: 1));

        Assert.Multiple(() =>
        {
            Assert.That(first.SummonMaintenanceBuffObserved, Is.True);
            Assert.That(first.MatchingSummonEntityObserved, Is.True);
            Assert.That(second.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(second.IsButcherPatternBlock, Is.True);
            Assert.That(second.SummonMaintenanceBuffObserved, Is.True);
            Assert.That(second.MatchingSummonEntityCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void HighOverkillBecomesSanctionableOnlyAfterAClosedActiveCrossTargetSequence()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            EnablePermanentSanctions = true,
            ExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(500f, 500f));
        var initial = queue.Observe(1, Observation(targetX: 500f, targetY: 500f,
            targetLife: 10, targetLifeMax: 100, wireDamage: 1000, receiverDamage: 1000));
        var post = new M18NpcStrikePostNativeObservation(
            Session: Actor,
            AccountId: 1807,
            TargetSlot: 11,
            TargetGeneration: 3,
            TargetType: 1,
            WireDamage: 1000,
            ReceiverDamage: 1000,
            TargetLifeMax: 100,
            LifeBefore: 10,
            LifeAfter: 0,
            TargetFriendly: false,
            TargetDummy: false,
            Stage: M18NpcStrikeStage.PreHardmode,
            StageSnapshotComplete: true,
            PositionContextComplete: initial.ButcherPositionContextComplete,
            PreControlAtTarget: initial.ButcherPreControlAtTarget,
            ControlJumpDetected: initial.ButcherControlJumpDetected,
            ClientOrigin: true,
            AttributionComplete: true,
            LegalExceptionsExcluded: true,
            NativeStrikeEntryObserved: true,
            LootMethodEntryObserved: false,
            RelayAttemptObserved: true);

        var completed = queue.ObservePostNative(1, post, initial);
        var postRule = M18NpcStrikeQueueRules.PostNativeObserve(Input, post, completed);
        queue.ObservePlayerControls(1, Control(100f, 100f));
        queue.ObservePlayerControls(1, Control(600f, 500f));
        var second = queue.Observe(2, Observation(targetSlot: 12, targetGeneration: 4,
            targetX: 600f, targetY: 500f, targetLife: 5, targetLifeMax: 5,
            wireDamage: 1000, receiverDamage: 1000));
        var rule = M18NpcStrikeQueueRules.Observe(Input, InputFacts(), second);

        Assert.Multiple(() =>
        {
            Assert.That(initial.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(initial.ButcherControlJumpDetected, Is.True);
            Assert.That(completed.PostNativeOneShotDeathEvidence, Is.True);
            Assert.That(completed.IsPostNativeSanctionCandidate, Is.False);
            Assert.That(second.ButcherCrossTargetContinuation, Is.True);
            Assert.That(second.ButcherCompletedTargetDeathSequences, Is.EqualTo(1));
            Assert.That(second.IsButcherSequenceSanctionCandidate, Is.True);
            Assert.That(rule.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(rule.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(rule.PredicateSatisfied, Is.True);
            Assert.That(rule.PrerequisitesComplete, Is.True);
            Assert.That(rule.Facts.Count, Is.LessThanOrEqualTo(23));
            Assert.That(rule.Facts.Values.All(value => value.Length <= 256), Is.True);
            Assert.That(postRule.Facts["postNativeEvidence"], Does.Contain("death=True"));
        });
    }

    [Test]
    public void OneShotDeathWithoutAnActiveSequenceRemainsNonSanctionEvidence()
    {
        var queue = new M18NpcStrikeQueue(Options with { EnablePermanentSanctions = true });
        queue.AdvanceWorld(Actor.WorldEpoch);
        var initial = queue.Observe(1, Observation(targetX: 500f, targetY: 500f,
            targetLife: 5, targetLifeMax: 5, wireDamage: 1000, receiverDamage: 1000));
        var post = new M18NpcStrikePostNativeObservation(
            Session: Actor,
            AccountId: 1807,
            TargetSlot: 11,
            TargetGeneration: 3,
            TargetType: 1,
            WireDamage: 1000,
            ReceiverDamage: 1000,
            TargetLifeMax: 5,
            LifeBefore: 5,
            LifeAfter: -1995,
            TargetFriendly: false,
            TargetDummy: false,
            Stage: M18NpcStrikeStage.PreHardmode,
            StageSnapshotComplete: true,
            PositionContextComplete: true,
            PreControlAtTarget: true,
            ControlJumpDetected: false,
            ClientOrigin: true,
            AttributionComplete: true,
            LegalExceptionsExcluded: true,
            NativeStrikeEntryObserved: true,
            LootMethodEntryObserved: true,
            RelayAttemptObserved: true);

        var completed = queue.ObservePostNative(1, post, initial);

        Assert.Multiple(() =>
        {
            Assert.That(completed.PostNativeOneShotDeathEvidence, Is.True);
            Assert.That(completed.IsPostNativeSanctionCandidate, Is.False);
            Assert.That(completed.Reason, Is.EqualTo("npc-strike-queue-record-only"));
        });
    }

    [Test]
    public void ExtremeLineUsesCompleteStageAndTargetSnapshot()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            ExtremeDamageThreshold = 9999,
            HardmodeExtremeDamageThreshold = 15000,
            PostPlanteraExtremeDamageThreshold = 22000,
            PostMoonlordExtremeDamageThreshold = 30000,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);

        var preHardmode = queue.Observe(1, Observation(worldId: 41, stage: M18NpcStrikeStage.PreHardmode,
            wireDamage: 9999, receiverDamage: 9999));
        var stageLineBelowTargetMax = queue.Observe(1, Observation(targetSlot: 16, worldId: 41,
            stage: M18NpcStrikeStage.PreHardmode, targetLife: 1000, targetLifeMax: 20000,
            wireDamage: 9999, receiverDamage: 9999));
        var hardmodeBelow = queue.Observe(1, Observation(targetSlot: 12, worldId: 41,
            stage: M18NpcStrikeStage.Hardmode, wireDamage: 9999, receiverDamage: 9999));
        var hardmodeExtreme = queue.Observe(1, Observation(targetSlot: 13, worldId: 41,
            stage: M18NpcStrikeStage.Hardmode, wireDamage: 15000, receiverDamage: 15000));
        var incomplete = queue.Observe(1, Observation(targetSlot: 14, worldId: 41,
            stage: M18NpcStrikeStage.PostMoonlord, stageSnapshotComplete: false,
            wireDamage: 30000, receiverDamage: 30000));
        var friendly = queue.Observe(1, Observation(targetSlot: 15, worldId: 41,
            stage: M18NpcStrikeStage.PreHardmode, wireDamage: 9999, receiverDamage: 9999,
            targetFriendly: true));

        Assert.Multiple(() =>
        {
            Assert.That(preHardmode.ExtremeDamage, Is.True);
            Assert.That(preHardmode.StageExtremeDamageThreshold, Is.EqualTo(9999));
            Assert.That(preHardmode.ExtremeGateSatisfied, Is.True);
            Assert.That(preHardmode.WorldId, Is.EqualTo(41));
            Assert.That(stageLineBelowTargetMax.ExtremeDamage, Is.True);
            Assert.That(stageLineBelowTargetMax.ExtremeGateSatisfied, Is.True);
            Assert.That(stageLineBelowTargetMax.TargetMaxLifeExceeded, Is.False,
                "The stage threshold is independent from the target-max-life observation.");
            Assert.That(hardmodeBelow.ExtremeDamage, Is.False);
            Assert.That(hardmodeBelow.StageExtremeDamageThreshold, Is.EqualTo(15000));
            Assert.That(hardmodeExtreme.ExtremeDamage, Is.True);
            Assert.That(hardmodeExtreme.ExtremeGateSatisfied, Is.True);
            Assert.That(incomplete.ExtremeDamage, Is.False);
            Assert.That(incomplete.StageExtremeDamageThreshold, Is.EqualTo(0));
            Assert.That(incomplete.ExtremeGateSatisfied, Is.False);
            Assert.That(friendly.ExtremeDamage, Is.False);
            Assert.That(friendly.ExtremeGateSatisfied, Is.False);
        });
    }

    [Test]
    public void WorldHighSampleProjectionIsBoundedAndResetsOnWorldIdentityChange()
    {
        var queue = new M18NpcStrikeQueue(Options with { HighSampleCapacity = 2 });
        queue.AdvanceWorld(Actor.WorldEpoch);

        queue.Observe(1, Observation(worldId: 51, wireDamage: 1000, receiverDamage: 1000));
        queue.Observe(1, Observation(targetSlot: 12, worldId: 51, wireDamage: 2000, receiverDamage: 2000));
        var third = queue.Observe(1, Observation(targetSlot: 13, worldId: 51, wireDamage: 3000, receiverDamage: 3000));
        var nextWorld = queue.Observe(1, Observation(worldId: 52, wireDamage: 4000, receiverDamage: 4000));

        Assert.Multiple(() =>
        {
            Assert.That(third.WorldHighMaxDamage, Is.EqualTo(3000));
            Assert.That(third.StageHighMaxDamage, Is.EqualTo(3000));
            Assert.That(third.WorldHighSamplesRetained, Is.EqualTo(2));
            Assert.That(third.WorldHighSamplesDropped, Is.EqualTo(1));
            Assert.That(nextWorld.WorldId, Is.EqualTo(52));
            Assert.That(nextWorld.WorldHighMaxDamage, Is.EqualTo(4000));
            Assert.That(nextWorld.StageHighMaxDamage, Is.EqualTo(4000));
            Assert.That(nextWorld.WorldHighSamplesRetained, Is.EqualTo(1));
            Assert.That(nextWorld.WorldHighSamplesDropped, Is.EqualTo(0));
        });
    }

    private static M18NpcPlayerControlObservation Control(float x, float y,
        bool alreadyCancelled = false)
        => new(Actor, 1807, x, y, true, true, true, true, true, alreadyCancelled);

    [Test]
    public void ExtremeGateIsOnlyAStopLossWithoutAnActiveSequenceProof()
    {
        var queue = new M18NpcStrikeQueue(Options with { EnablePermanentSanctions = true });
        queue.AdvanceWorld(Actor.WorldEpoch);
        var decision = queue.Observe(1, Observation(worldId: 53, wireDamage: 9999, receiverDamage: 9999));
        var result = M18NpcStrikeQueueRules.Observe(Input, InputFacts(), decision);

        Assert.Multiple(() =>
        {
            Assert.That(decision.PermanentSanctionCandidate, Is.False);
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
            Assert.That(result.Reason, Is.EqualTo("npc-strike-extreme-damage-preforward-stop"));
            Assert.That(result.Facts["strikeQueueActionContract"],
                Is.EqualTo("block-before-native-receiver-no-sanction"));
        });
    }

    [Test]
    public void IncompleteAttributionDoesNotMutateTheQueue()
    {
        var queue = new M18NpcStrikeQueue(Options with { PerTargetLowDamageLimit = 2 });
        queue.AdvanceWorld(Actor.WorldEpoch);

        var incomplete = queue.Observe(1, Observation(attributionComplete: false));
        var valid = queue.Observe(1, Observation());

        Assert.Multiple(() =>
        {
            Assert.That(incomplete.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(incomplete.Counted, Is.False);
            Assert.That(valid.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(valid.TargetLowDamageCount, Is.EqualTo(1));
            Assert.That(valid.SessionLowDamageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void LowRingCapacityIsAResourceStopLossWithNoCheatVerdict()
    {
        var queue = new M18NpcStrikeQueue(Options with
        {
            PerTargetLowDamageLimit = 4,
            PerSessionLowDamageLimit = 4,
            LowDamageRingCapacity = 2,
        });
        queue.AdvanceWorld(Actor.WorldEpoch);

        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(queue.Observe(1, Observation(targetSlot: 12)).Action, Is.EqualTo(ControlAction.Unknown));
        var exhausted = queue.Observe(1, Observation(targetSlot: 13));

        Assert.Multiple(() =>
        {
            Assert.That(exhausted.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(exhausted.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(exhausted.IsResourceBlock, Is.True);
            Assert.That(exhausted.CapacityExhausted, Is.True);
            Assert.That(exhausted.SessionLowDamageCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void WorldEpochAndSessionSlotReuseClearState()
    {
        var queue = new M18NpcStrikeQueue(Options with { PerTargetLowDamageLimit = 2 });
        queue.AdvanceWorld(Actor.WorldEpoch);
        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Unknown));

        queue.AdvanceWorld(Actor.WorldEpoch + 1);
        var nextWorld = queue.Observe(1, Observation(session: Actor with { WorldEpoch = Actor.WorldEpoch + 1 }));

        Assert.Multiple(() =>
        {
            Assert.That(nextWorld.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(nextWorld.TargetLowDamageCount, Is.EqualTo(1));
            Assert.That(nextWorld.SessionLowDamageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ResourceBlockResultIsNeverAProvenCheatCandidate()
    {
        var queue = new M18NpcStrikeQueue(Options with { PerTargetLowDamageLimit = 1 });
        queue.AdvanceWorld(Actor.WorldEpoch);
        var decision = queue.Observe(1, Observation());
        var result = M18NpcStrikeQueueRules.ResourceBlock(Input, InputFacts(), decision);

        Assert.Multiple(() =>
        {
            Assert.That(result.RuleId, Is.EqualTo(M18NpcStrikeQueueRules.RuleId));
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
        });
    }

    [Test]
    public void QueueObservationNeverDowngradesAnExistingStructuralDecision()
    {
        var priorFacts = InputFacts().Add("producer", "M6NpcStrikeReader");
        var priorBlock = new BusinessRuleResult(
            M6CombatRules.StrikeRuleId,
            M6CombatRules.Version,
            ControlAction.Block,
            Verdict.UnsafeInput,
            "npc-strike-noncanonical-numeric-input",
            false,
            false,
            priorFacts);
        var queue = new M18NpcStrikeQueue(Options);
        queue.AdvanceWorld(Actor.WorldEpoch);
        var queueResult = M18NpcStrikeQueueRules.Observe(Input, priorFacts,
            queue.Observe(1, Observation(wireDamage: 9, receiverDamage: 9)));

        var mergedBlock = M18NpcStrikeQueueRules.MergeWithPrior(priorBlock, queueResult);
        Assert.Multiple(() =>
        {
            Assert.That(mergedBlock.RuleId, Is.EqualTo(M6CombatRules.StrikeRuleId));
            Assert.That(mergedBlock.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(mergedBlock.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(mergedBlock.Reason, Is.EqualTo("npc-strike-noncanonical-numeric-input"));
            Assert.That(mergedBlock.Facts["producer"], Is.EqualTo("M6NpcStrikeReader"));
            Assert.That(mergedBlock.Facts.ContainsKey("strikeQueue"), Is.True);
        });

        var priorPass = priorBlock with
        {
            Action = ControlAction.Pass,
            Verdict = Verdict.Pass,
            Reason = "npc-strike-old-generation-native-noop",
            PrerequisitesComplete = true
        };
        var mergedPass = M18NpcStrikeQueueRules.MergeWithPrior(priorPass, queueResult);
        Assert.Multiple(() =>
        {
            Assert.That(mergedPass.RuleId, Is.EqualTo(M6CombatRules.StrikeRuleId));
            Assert.That(mergedPass.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(mergedPass.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(mergedPass.Reason, Is.EqualTo("npc-strike-old-generation-native-noop"));
        });

        var resourceQueue = new M18NpcStrikeQueue(Options with { PerTargetLowDamageLimit = 1 });
        resourceQueue.AdvanceWorld(Actor.WorldEpoch);
        var resourceDecision = resourceQueue.Observe(1, Observation());
        var resourceResult = M18NpcStrikeQueueRules.Observe(Input, priorFacts,
            resourceDecision);
        var mergedResource = M18NpcStrikeQueueRules.MergeWithPrior(priorBlock, resourceResult);
        Assert.Multiple(() =>
        {
            Assert.That(resourceDecision.IsResourceBlock, Is.True);
            Assert.That(mergedResource.RuleId, Is.EqualTo(M6CombatRules.StrikeRuleId));
            Assert.That(mergedResource.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(mergedResource.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(mergedResource.Reason, Is.EqualTo("npc-strike-noncanonical-numeric-input"));
        });
    }

    private static System.Collections.Immutable.ImmutableDictionary<string, string> InputFacts()
        => System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
}
