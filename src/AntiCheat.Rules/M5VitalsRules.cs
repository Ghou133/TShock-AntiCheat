using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// A packet-role relation, not a damage amount, invulnerability, or missing-response detector.
/// All reviewed target-native cross-player hurt producers explicitly set pvp=true.
/// </summary>
public static class M5VitalsRules
{
    public const string HurtRuleId = "VITAL04.NonPvpHurtTarget";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria326-hurt-subject-v1";

    public static BusinessRuleResult EvaluateHurt(M4VitalObservation observation, RuleInputContext input,
        bool knownPluginSet, bool synchronizing)
    {
        var facts = RuleResults.Facts(("targetSlot", observation.ClaimedSlot), ("senderSlot", input.Session.Slot),
            ("pvpClaim", observation.Pvp), ("damageClaim", observation.HurtDamage),
            ("cooldownClaim", observation.HurtCooldown), ("contract", ContractVersion),
            ("knownPluginSet", knownPluginSet), ("synchronizing", synchronizing),
            ("damageApplicationOrMissingResponseNotInferred", true));
        if (observation.Kind != M4VitalKind.HurtDeclaration || !input.ParserComplete || !input.VersionMatched || !input.ClientOrigin)
            return RuleResults.Unknown(HurtRuleId, "target-hurt-contract-unavailable", facts);
        if (observation.ClaimedSlot is < 0 or >= 255)
            return RuleResults.Unknown(HurtRuleId, "invalid-hurt-target-left-to-core-safety", facts);
        if (observation.ClaimedSlot == input.Session.Slot)
            return RuleResults.Pass(HurtRuleId, "native-self-hurt-declaration", facts);
        // PvP may race a legitimate hostile toggle or be delayed. No present-time hostile-state inference.
        if (observation.Pvp)
            return RuleResults.Pass(HurtRuleId, "native-cross-player-pvp-role", facts);
        if (!knownPluginSet || synchronizing)
            return RuleResults.Unknown(HurtRuleId, "hurt-source-exceptions-or-session-incomplete", facts);
        return RuleResults.Candidate(HurtRuleId, "client-cross-player-hurt-must-declare-pvp", input, facts);
    }
}
