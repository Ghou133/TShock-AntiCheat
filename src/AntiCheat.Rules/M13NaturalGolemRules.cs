using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M13GolemContext(RuleInputContext Input, bool WorldHistoryComplete,
    bool HardMode, bool PlanteraDefeated, bool InitialSynchronization, bool PluginContractComplete);

/// <summary>The altar interaction rechecks these world gates at emission. This contract makes no
/// claim about acquisition of the battery, held-item identity, range, or a previous animation.</summary>
public static class M13NaturalGolemRules
{
    public const string RuleId = "PG-NAT-108.GolemSummonWorld";
    public const string Version = "1.0.0";

    public static BusinessRuleResult Evaluate(int sender, int summonType, M13GolemContext context)
    {
        var facts = RuleResults.Facts(("sender", sender), ("summonType", summonType), ("itemId", 1293),
            ("sourceOperation", "MKLP-OP-108"), ("sourceClassification", "server_policy-not-adopted"),
            ("sourcePredicate", "!NPC.downedPlantBoss && currentbossdefeated != BossDType.Plantera && !allowtemplerush"),
            ("hardMode", context.HardMode), ("planteraDefeated", context.PlanteraDefeated),
            ("worldBaselineComplete", context.WorldHistoryComplete),
            ("pluginContractComplete", context.PluginContractComplete),
            ("initialSynchronization", context.InitialSynchronization),
            ("proofKind", "natural-client-altar-request-world-gate"),
            ("acquisitionHistoryClaimed", false), ("animationStarterIdentityRequired", false));
        if (summonType != 245) return Result(RuleResults.Pass(RuleId, "outside-golem-altar-contract", facts));
        if (context.HardMode && context.PlanteraDefeated)
            return Result(RuleResults.Pass(RuleId, "native-golem-altar-world-gates-open", facts));
        if (sender != context.Input.Session.Slot)
            return Result(RuleResults.Unknown(RuleId, "golem-sender-not-current-subject", facts));
        if (!context.WorldHistoryComplete || context.InitialSynchronization || !context.PluginContractComplete)
            return Result(RuleResults.Unknown(RuleId, "golem-world-export-session-or-plugin-history-incomplete", facts));
        return Result(RuleResults.Candidate(RuleId, "native-client-cannot-request-golem-with-closed-world-gate", context.Input, facts));
    }

    private static BusinessRuleResult Result(BusinessRuleResult result) => result with { Version = Version };
}
