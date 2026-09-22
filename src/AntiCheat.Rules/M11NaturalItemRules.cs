using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M11SolarTabletContext(RuleInputContext Input, bool HardModeHistoryComplete,
    bool HardMode, bool InitialSynchronization, bool PluginContractComplete);

/// <summary>Investigation of Solar Tablet use, independently of MKLP's Plantera/temple-rush policy.
/// The native start gate does not establish the identity of the item that started an existing
/// animation: SSC can replace its selected slot. Closed world history alone cannot authorize HARD.</summary>
public static class M11NaturalItemRules
{
    public const string SolarTabletRuleId = "PG-NAT-108.SolarTabletHardmode";
    public const string Version = "1.0.1";

    public static BusinessRuleResult EvaluateSolarTablet(int sender, int summonType, M11SolarTabletContext context)
    {
        var facts = RuleResults.Facts(("sender", sender), ("summonType", summonType), ("itemId", 2767),
            ("sourceOperation", "MKLP-OP-108"), ("sourceClassification", "server_policy-not-adopted"),
            ("sourcePredicate", "!NPC.downedPlantBoss && currentbossdefeated != BossDType.Plantera && !allowtemplerush"),
            ("hardMode", context.HardMode), ("hardModeHistoryComplete", context.HardModeHistoryComplete),
            ("proofKind", "unproved-natural-client-active-use-world-gate"),
            ("startedItemIdentityComplete", false), ("hardCandidateAdmitted", false),
            ("acquisitionHistoryClaimed", false), ("clientDayTimeRequiredForProof", false));
        if (summonType != -6) return RuleResults.Pass(SolarTabletRuleId, "other-action-outside-solar-tablet-contract", facts) with { Version = Version };
        if (context.HardMode) return RuleResults.Pass(SolarTabletRuleId, "native-solar-tablet-hardmode-gate-open", facts) with { Version = Version };
        if (sender != context.Input.Session.Slot)
            return RuleResults.Unknown(SolarTabletRuleId, "solar-tablet-sender-not-current-subject", facts) with { Version = Version };
        if (!context.HardModeHistoryComplete || context.InitialSynchronization || !context.PluginContractComplete)
            return RuleResults.Unknown(SolarTabletRuleId, "solar-tablet-hardmode-export-session-or-plugin-history-incomplete", facts) with { Version = Version };
        return RuleResults.Unknown(SolarTabletRuleId, "solar-tablet-started-item-identity-history-unproved", facts) with { Version = Version };
    }
}
