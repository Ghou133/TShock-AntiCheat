using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M16CombatTargetSnapshot(int Slot, int Generation, int Type, int NetId, int AiStyle,
    bool Active, int Life, float Phase, bool DontTakeDamage);

/// <summary>Current authoritative Moon Lord core phase policy. This cannot establish cheating:
/// a legitimate in-flight hit can arrive after a phase changes. No damage ceiling is inferred.</summary>
public static class M16CombatImmunityRules
{
    public const string RuleId = "COMBAT16.MoonLordCoreImmunity";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria326-moonlord-core-current-phase-v1";
    public const int CoreType = 398;

    public static BusinessRuleResult Evaluate(NpcStrikeObservation request, RuleInputContext input,
        M16CombatTargetSnapshot? target, bool currentNativeContext)
    {
        var facts = RuleResults.Facts(("contract", ContractVersion), ("npcSlot", request.TargetSlot),
            ("requestGeneration", request.TargetGeneration), ("targetGeneration", target?.Generation),
            ("targetType", target?.Type), ("targetNetId", target?.NetId), ("targetAiStyle", target?.AiStyle),
            ("targetPhase", target?.Phase), ("targetDontTakeDamage", target?.DontTakeDamage),
            ("wireDamage", request.Damage), ("critical", request.CriticalFlag),
            ("currentNativeContext", currentNativeContext), ("amountLegitimacyInferred", false),
            ("inFlightLegalHitMayBeRejectedByCurrentPhasePolicy", true));
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "server-or-plugin-native-strike-outside-client-phase-policy", facts);
        if (!input.Complete || !currentNativeContext || target is null || target.Slot != request.TargetSlot)
            return RuleResults.Unknown(RuleId, "current-npc-phase-context-unavailable", facts);
        if (target.Generation != request.TargetGeneration)
            return RuleResults.Pass(RuleId, "old-generation-strike-retains-native-noop", facts);
        if (!target.Active || target.Life <= 0)
            return RuleResults.Pass(RuleId, "inactive-or-dead-target-retains-native-noop", facts);
        if (target.Type != CoreType || target.NetId != CoreType || target.AiStyle != 77)
            return RuleResults.Unknown(RuleId, "npc-outside-audited-core-phase-domain", facts);
        // The existing structural guard owns malformed numbers. This policy never upgrades them.
        if (request.Damage is < short.MinValue or > short.MaxValue || !float.IsFinite(request.Knockback) ||
            request.EncodedDirection is < 0 or > 2 || request.CriticalFlag is < 0 or > 1)
            return RuleResults.Unknown(RuleId, "npc-phase-request-structure-unavailable", facts);
        if (target.Phase == 1f && !target.DontTakeDamage)
            return RuleResults.Pass(RuleId, "native-core-vulnerable-phase-damage-amount-not-adjudicated", facts);
        if (target.Phase is not (-2f or -1f or 0f or 2f or 3f) || !target.DontTakeDamage)
            return RuleResults.Unknown(RuleId, "core-phase-and-immunity-state-not-in-supported-contract", facts);
        // StrikeNPC_Inner clamps even zero/negative-wire damage to at least one life point and
        // sets justHit. Refuse the whole current client action before either side effect occurs.
        return RuleResults.Block(RuleId, "current-native-core-immune-phase-denies-client-strike", facts);
    }
}
