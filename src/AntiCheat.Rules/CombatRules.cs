using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record CombatTarget(SessionKey Session, bool Active, bool PvpEnabled);
public sealed record CombatContext(RuleInputContext Input, CombatTarget Target, SessionKey TargetSnapshotSession,
    bool TargetSnapshotComplete, bool TargetTransitionInProgress, bool? InRange,
    bool ScopedServerOrPluginAction = false);
public sealed record BuffDefinition(int MaximumTicks, bool SelfOnly, bool RequiresTargetPvp);
public sealed record BuffCatalog(string RuntimeFingerprint, string DataVersion, int BuffTypeExclusiveMax,
    ImmutableDictionary<int, BuffDefinition> ClientAddBuffTypes, bool AddBuffProtocolComplete);
public sealed record BuffObservation(int TargetSlot, int Type, int DurationTicks);
public sealed record HealObservation(int TargetSlot, int Amount);
/// <summary>Bounds from an independently correlated healing cause, not global damage or current held item.</summary>
public sealed record HealSourceContext(string RuntimeFingerprint, string MechanismVersion, bool CorrelationComplete,
    SessionKey Actor, SessionKey Recipient, int MaximumAmount, bool TargetAllowedByMechanism);
public sealed record AttackObservation(int WeaponType, int ProjectileType, long ServerActionTick);
public sealed record AttackMechanism(string RuntimeFingerprint, string DataVersion,
    ImmutableDictionary<int, ImmutableHashSet<int>> ProjectileTypesByWeapon, bool CombinationRulesComplete,
    bool CooldownRulesComplete);
public sealed record AttackContext(RuleInputContext Input, SessionKey SourceSession, bool IndependentSourceCorrelationComplete,
    bool SourceTransitionInProgress, bool ScopedAuthorizedMutation, bool CooldownCausalityComplete,
    long EarliestAllowedServerTick);

public static class CombatRules
{
    public const string BuffRuleId = "C5.BuffProtocol";
    public const string HealRuleId = "C5.HealSource";
    public const string WeaponRuleId = "C6.WeaponProjectileCausality";

    public static BusinessRuleResult EvaluateBuff(BuffObservation observation, CombatContext context, BuffCatalog catalog)
    {
        var facts = RuleResults.Facts(("senderSlot", context.Input.Session.Slot), ("targetSlot", observation.TargetSlot),
            ("buffType", observation.Type), ("durationTicks", observation.DurationTicks), ("dataVersion", catalog.DataVersion));
        var preliminary = CheckTarget(BuffRuleId, observation.TargetSlot, context, facts);
        if (preliminary is not null) return preliminary;
        if (catalog.RuntimeFingerprint != context.Input.RuntimeFingerprint || string.IsNullOrWhiteSpace(catalog.DataVersion) ||
            !catalog.AddBuffProtocolComplete || catalog.BuffTypeExclusiveMax <= 0)
            return RuleResults.Unknown(BuffRuleId, "client-add-buff-table-unverified", facts);
        if (observation.Type <= 0 || observation.Type >= catalog.BuffTypeExclusiveMax || observation.DurationTicks <= 0)
            return RuleResults.Block(BuffRuleId, "buff-type-or-duration-out-of-domain", facts);
        if (!catalog.ClientAddBuffTypes.TryGetValue(observation.Type, out var definition))
            return RuleResults.Block(BuffRuleId, "buff-not-in-client-add-buff-protocol", facts);
        if (definition.MaximumTicks <= 0) return RuleResults.Unknown(BuffRuleId, "buff-duration-definition-invalid", facts);
        if (definition.SelfOnly && observation.TargetSlot != context.Input.Session.Slot)
            return RuleResults.Candidate(BuffRuleId, "self-only-buff-sent-to-another-player", context.Input, facts);
        if (definition.RequiresTargetPvp && !context.Target.PvpEnabled)
            return RuleResults.Block(BuffRuleId, "buff-target-pvp-policy-denied", facts);
        if (observation.DurationTicks > definition.MaximumTicks)
            return RuleResults.Candidate(BuffRuleId, "buff-duration-exceeds-verified-protocol", context.Input,
                facts.Add("maximumTicks", definition.MaximumTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return RuleResults.Pass(BuffRuleId, "legal-client-add-buff", facts);
    }

    public static BusinessRuleResult EvaluateHeal(HealObservation observation, CombatContext context, HealSourceContext source)
    {
        var facts = RuleResults.Facts(("senderSlot", context.Input.Session.Slot), ("targetSlot", observation.TargetSlot),
            ("amount", observation.Amount), ("maximumAmount", source.MaximumAmount), ("mechanismVersion", source.MechanismVersion));
        var preliminary = CheckTarget(HealRuleId, observation.TargetSlot, context, facts);
        if (preliminary is not null) return preliminary;
        if (observation.Amount <= 0) return RuleResults.Block(HealRuleId, "nonpositive-heal-request", facts);
        if (source.RuntimeFingerprint != context.Input.RuntimeFingerprint || string.IsNullOrWhiteSpace(source.MechanismVersion) ||
            !source.CorrelationComplete || source.Actor != context.Input.Session || source.Recipient != context.Target.Session ||
            source.MaximumAmount <= 0)
            return RuleResults.Unknown(HealRuleId, "independent-healing-cause-unavailable", facts);
        if (!source.TargetAllowedByMechanism)
            return RuleResults.Candidate(HealRuleId, "healing-cause-does-not-authorize-target", context.Input, facts);
        if (observation.Amount > source.MaximumAmount)
            return RuleResults.Candidate(HealRuleId, "heal-exceeds-correlated-mechanism-bound", context.Input, facts);
        return RuleResults.Pass(HealRuleId, "legal-correlated-healing-request", facts);
    }

    public static BusinessRuleResult EvaluateAttack(AttackObservation observation, AttackContext context, AttackMechanism mechanism)
    {
        var facts = RuleResults.Facts(("weaponType", observation.WeaponType), ("projectileType", observation.ProjectileType),
            ("serverActionTick", observation.ServerActionTick), ("earliestAllowedServerTick", context.EarliestAllowedServerTick),
            ("dataVersion", mechanism.DataVersion));
        if (context.ScopedAuthorizedMutation || !context.Input.ClientOrigin)
            return RuleResults.Pass(WeaponRuleId, "scoped-authorized-attack-mechanism", facts);
        if (!context.Input.VersionMatched || mechanism.RuntimeFingerprint != context.Input.RuntimeFingerprint ||
            string.IsNullOrWhiteSpace(mechanism.DataVersion) || context.SourceSession != context.Input.Session ||
            !context.IndependentSourceCorrelationComplete || context.SourceTransitionInProgress)
            return RuleResults.Unknown(WeaponRuleId, "independent-weapon-cause-incomplete-or-transitioning", facts);
        if (!mechanism.CombinationRulesComplete ||
            !mechanism.ProjectileTypesByWeapon.TryGetValue(observation.WeaponType, out var allowedTypes))
            return RuleResults.Unknown(WeaponRuleId, "weapon-projectile-mechanism-unmodeled", facts);
        if (!allowedTypes.Contains(observation.ProjectileType))
            return RuleResults.Candidate(WeaponRuleId, "projectile-impossible-for-correlated-weapon-cause", context.Input, facts);
        if (!mechanism.CooldownRulesComplete || !context.CooldownCausalityComplete)
            return RuleResults.Unknown(WeaponRuleId, "weapon-combination-valid-cooldown-proof-unavailable", facts);
        if (observation.ServerActionTick < 0 || context.EarliestAllowedServerTick < 0)
            return RuleResults.Unknown(WeaponRuleId, "invalid-server-tick-witness", facts);
        if (observation.ServerActionTick < context.EarliestAllowedServerTick)
            return RuleResults.Candidate(WeaponRuleId, "attack-precedes-verified-mechanism-cooldown", context.Input, facts);
        return RuleResults.Pass(WeaponRuleId, "legal-correlated-weapon-action", facts);
    }

    private static BusinessRuleResult? CheckTarget(string ruleId, int targetSlot, CombatContext context,
        ImmutableDictionary<string, string> facts)
    {
        if (!context.Input.ParserComplete) return RuleResults.Unknown(ruleId, "combat-parser-incomplete", facts);
        if (context.ScopedServerOrPluginAction || !context.Input.ClientOrigin)
            return RuleResults.Pass(ruleId, "server-or-plugin-origin-combat-effect", facts);
        if (targetSlot is < 0 or >= 256) return RuleResults.Block(ruleId, "combat-target-out-of-range", facts);
        if (!context.Input.VersionMatched || !context.TargetSnapshotComplete ||
            context.TargetSnapshotSession != context.Target.Session || context.Target.Session.Slot != targetSlot ||
            (targetSlot == context.Input.Session.Slot && context.Target.Session != context.Input.Session) ||
            context.Target.Session.WorldEpoch != context.Input.Session.WorldEpoch ||
            context.Target.Session.ServerRunId != context.Input.Session.ServerRunId || context.TargetTransitionInProgress)
            return RuleResults.Unknown(ruleId, "combat-target-snapshot-stale-or-transitioning", facts);
        if (!context.Target.Active) return RuleResults.Block(ruleId, "combat-target-inactive", facts);
        if (context.InRange == false) return RuleResults.Block(ruleId, "combat-target-out-of-range-policy", facts);
        if (context.InRange is null) return RuleResults.Unknown(ruleId, "combat-target-range-context-unavailable", facts);
        return null;
    }
}
