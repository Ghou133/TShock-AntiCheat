using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M7SigilContext(RuleInputContext Input, bool WorldUnchangedSinceConnection,
    bool HardMode, bool GolemDefeated, bool InitialSynchronization, bool PluginContractComplete);

/// <summary>An active native summon contract, separate from MKLP's Cultist item-possession policy.
/// Current progression flags are used only as client action gates, never as acquisition history.</summary>
public static class M7ProgressionRules
{
    public const string SigilRuleId = "PG-NAT-121.CelestialSigilWorld";
    public const string Version = "1.0.0";

    public static ImmutableDictionary<string, string> Facts(int sender, int summonType, bool hardMode, bool golem) =>
        RuleResults.Facts(("sender", sender), ("summonType", summonType), ("itemId", 3601),
            ("sourceOperation", "MKLP-OP-121"), ("sourceClassification", "server_policy-not-adopted"),
            ("sourcePredicate", "!NPC.downedAncientCultist && currentbossdefeated != BossDType.LunaticCultist"),
            ("hardMode", hardMode), ("golemDefeated", golem),
            ("proofKind", "natural-client-action-world-gate"), ("acquisitionHistoryClaimed", false));

    public static BusinessRuleResult EvaluateSigil(int sender, int summonType, M7SigilContext context)
    {
        var facts = Facts(sender, summonType, context.HardMode, context.GolemDefeated);
        if (summonType != -8) return RuleResults.Pass(SigilRuleId, "other-summon-outside-sigil-contract", facts);
        if (context.HardMode && context.GolemDefeated)
            return RuleResults.Pass(SigilRuleId, "native-sigil-progression-gates-open", facts);
        if (sender != context.Input.Session.Slot)
            return RuleResults.Unknown(SigilRuleId, "summon-sender-not-current-subject", facts);
        if (!context.WorldUnchangedSinceConnection || context.InitialSynchronization || !context.PluginContractComplete)
            return RuleResults.Unknown(SigilRuleId, "world-export-session-or-plugin-contract-incomplete", facts);
        return RuleResults.Candidate(SigilRuleId, "native-client-cannot-request-sigil-with-closed-world-gate", context.Input, facts);
    }
}
