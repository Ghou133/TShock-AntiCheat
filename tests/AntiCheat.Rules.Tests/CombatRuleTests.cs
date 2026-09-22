using System.Collections.Immutable;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class CombatRuleTests
{
    private static CombatTarget Target => new(Actor with { Slot = 8, Generation = 14 }, true, true);
    private static CombatContext Context => new(Input, Target, Target.Session, true, false, true);
    private static BuffCatalog Catalog => new(Fingerprint, "target-test-client-add-buff", 400,
        new Dictionary<int, BuffDefinition>
        {
            [1] = new(3600, false, false), [2] = new(1800, true, false), [3] = new(600, false, true)
        }.ToImmutableDictionary(), true);
    private static BuffObservation Buff => new(8, 1, 3600);
    private static HealObservation Heal => new(8, 20);
    private static HealSourceContext Healing => new(Fingerprint, "test-healing-mechanism", true, Actor, Target.Session, 20, true);

    [Test] public void NormalOtherPlayerBuffIsLegal() => Pass(CombatRules.EvaluateBuff(Buff, Context, Catalog));
    [Test] public void LegalSelfOnlyBuffUsesSelfTarget() =>
        Pass(CombatRules.EvaluateBuff(Buff with { TargetSlot = Actor.Slot, Type = 2, DurationTicks = 1800 },
            Context with { Target = new(Actor, true, false), TargetSnapshotSession = Actor }, Catalog));
    [Test] public void ValidPvpDebuffAndLongPerTypeDurationAreLegal()
    {
        Pass(CombatRules.EvaluateBuff(Buff with { Type = 3, DurationTicks = 600 }, Context, Catalog));
        Pass(CombatRules.EvaluateBuff(Buff with { DurationTicks = 200000 }, Context,
            Catalog with { ClientAddBuffTypes = Catalog.ClientAddBuffTypes.SetItem(1, new(200000, false, false)) }));
    }
    [Test] public void SelfOnlyBuffSentToAnotherPlayerHasSenderAttribution()
    {
        var result = CombatRules.EvaluateBuff(Buff with { Type = 2, DurationTicks = 1800 }, Context, Catalog);
        Candidate(result);
        Assert.That(result.Facts["senderSlot"], Is.EqualTo(Actor.Slot.ToString()));
        Assert.That(result.Facts["targetSlot"], Is.EqualTo(Target.Session.Slot.ToString()));
    }
    [Test] public void ExcessVerifiedBuffDurationIsSingleCompletePredicate() =>
        Candidate(CombatRules.EvaluateBuff(Buff with { DurationTicks = 3601 }, Context, Catalog));
    [Test] public void PassiveServerEffectDoesNotBlameRecipient() =>
        Pass(CombatRules.EvaluateBuff(Buff with { Type = 2 }, Context with { Input = Input with { ClientOrigin = false } }, Catalog));
    [Test] public void ScopedPluginCanApplyItsOwnBuff() =>
        Pass(CombatRules.EvaluateBuff(Buff with { Type = 200, DurationTicks = 50000 }, Context with { ScopedServerOrPluginAction = true }, Catalog));
    [Test] public void PvpTransitionAndRecycledTargetSlotAreUnknown()
    {
        Unknown(CombatRules.EvaluateBuff(Buff with { Type = 3 }, Context with { TargetTransitionInProgress = true }, Catalog));
        Unknown(CombatRules.EvaluateBuff(Buff, Context with { TargetSnapshotSession = Target.Session with { Generation = 13 } }, Catalog));
    }
    [Test] public void OldSelfTargetGenerationCannotProvePlayerAction() =>
        Unknown(CombatRules.EvaluateBuff(Buff with { TargetSlot = Actor.Slot },
            Context with { Target = new(Actor with { Generation = 11 }, true, true), TargetSnapshotSession = Actor with { Generation = 11 } }, Catalog));
    [Test] public void InvalidBuffAndPvpPolicyDenialRemainBlockOnly()
    {
        BlockOnly(CombatRules.EvaluateBuff(Buff with { Type = 400 }, Context, Catalog));
        BlockOnly(CombatRules.EvaluateBuff(Buff with { Type = 3 }, Context with { Target = Target with { PvpEnabled = false } }, Catalog));
        BlockOnly(CombatRules.EvaluateBuff(Buff with { DurationTicks = 0 }, Context, Catalog));
    }
    [Test] public void UnverifiedBuffTableAndMissingAttributionNeverProve()
    {
        Unknown(CombatRules.EvaluateBuff(Buff with { Type = 2 }, Context, Catalog with { AddBuffProtocolComplete = false }));
        Unknown(CombatRules.EvaluateBuff(Buff with { DurationTicks = 5000 }, Context with { Input = Input with { AttributionComplete = false } }, Catalog));
    }
    [Test] public void LegalCrossPlayerHealingPasses() => Pass(CombatRules.EvaluateHeal(Heal, Context, Healing));
    [Test] public void LargeHealCanBeLegalForItsCorrelatedMechanism() =>
        Pass(CombatRules.EvaluateHeal(Heal with { Amount = 1000 }, Context, Healing with { MaximumAmount = 1000 }));
    [Test] public void CorrelatedHealBoundViolationIsCompletePredicate() =>
        Candidate(CombatRules.EvaluateHeal(Heal with { Amount = 21 }, Context, Healing));
    [Test] public void HealingWrongTargetRequiresCompleteMechanismAttribution() =>
        Candidate(CombatRules.EvaluateHeal(Heal, Context, Healing with { TargetAllowedByMechanism = false }));
    [Test] public void UnknownHealingSourceNeverUsesGlobalDamageThreshold() =>
        Unknown(CombatRules.EvaluateHeal(Heal with { Amount = 10000 }, Context, Healing with { CorrelationComplete = false }));
    [Test] public void OtherActorOrOldTargetHealingWitnessIsUnknown()
    {
        Unknown(CombatRules.EvaluateHeal(Heal with { Amount = 10000 }, Context, Healing with { Actor = Target.Session }));
        Unknown(CombatRules.EvaluateHeal(Heal with { Amount = 10000 }, Context, Healing with { Recipient = Target.Session with { Generation = 13 } }));
    }
    [Test] public void NonpositiveHealingIsSafetyRejection() =>
        BlockOnly(CombatRules.EvaluateHeal(Heal with { Amount = 0 }, Context, Healing));

    private static AttackObservation Attack => new(10, 20, 100);
    private static AttackMechanism Mechanism => new(Fingerprint, "verified-test-weapon-mechanism",
        ImmutableDictionary<int, ImmutableHashSet<int>>.Empty.Add(10, ImmutableHashSet.Create(20, 21)), true, true);
    private static AttackContext AttackContext => new(Input, Actor, true, false, false, true, 100);
    [Test] public void CorrelatedWeaponAlternateProjectilePasses() =>
        Pass(CombatRules.EvaluateAttack(Attack with { ProjectileType = 21 }, AttackContext, Mechanism));
    [Test] public void ImpossibleCorrelatedWeaponProjectileIsCompletePredicate() =>
        Candidate(CombatRules.EvaluateAttack(Attack with { ProjectileType = 30 }, AttackContext, Mechanism));
    [Test] public void ObservedHeldWeaponAloneCannotProveImpossibleCombination() =>
        Unknown(CombatRules.EvaluateAttack(Attack with { ProjectileType = 30 }, AttackContext with { IndependentSourceCorrelationComplete = false }, Mechanism));
    [Test] public void WeaponSwitchAndPluginMutationAreExplicitExceptions()
    {
        Unknown(CombatRules.EvaluateAttack(Attack with { ProjectileType = 30 }, AttackContext with { SourceTransitionInProgress = true }, Mechanism));
        Pass(CombatRules.EvaluateAttack(Attack with { ProjectileType = 30 }, AttackContext with { ScopedAuthorizedMutation = true }, Mechanism));
    }
    [Test] public void CooldownNeedsIndependentCompleteCause()
    {
        Unknown(CombatRules.EvaluateAttack(Attack with { ServerActionTick = 99 }, AttackContext with { CooldownCausalityComplete = false }, Mechanism));
        Candidate(CombatRules.EvaluateAttack(Attack with { ServerActionTick = 99 }, AttackContext, Mechanism));
    }
    [Test] public void UnmodeledWeaponAndStaleSessionAreUnknown()
    {
        Unknown(CombatRules.EvaluateAttack(Attack with { WeaponType = 99 }, AttackContext, Mechanism));
        Unknown(CombatRules.EvaluateAttack(Attack, AttackContext with { SourceSession = Actor with { Generation = 11 } }, Mechanism));
    }
}
