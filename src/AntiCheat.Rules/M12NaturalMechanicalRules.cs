using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M12MechanicalSummonContext(RuleInputContext Input, bool WorldHistoryComplete,
    bool NativeMechdusaWorld, bool PluginContractComplete, bool InitialSynchronization,
    string ObservedPossibleItemIds, bool LostUseHistory);

/// <summary>Selected MKLP OP081 items reach a different native emission prerequisite: their item
/// Variant. The original prehardmode possession policy is not adopted. A changed server world fact
/// is not a client item refresh, and present item identity does not certify an animation starter.</summary>
public static class M12NaturalMechanicalRules
{
    public const string RuleId = "PG-NAT-081.MechanicalSummonVariant";
    public const string Version = "1.0.0";
    public static bool IsMechanicalSummon(int type) => type is 125 or 126 or 127 or 134;
    public static BusinessRuleResult Evaluate(int sender, int type, M12MechanicalSummonContext context)
    {
        int item = type is 125 or 126 ? 544 : type == 127 ? 557 : type == 134 ? 556 : 0;
        var facts = RuleResults.Facts(("sender", sender), ("summonType", type), ("itemId", item),
            ("sourceOperation", "MKLP-OP-081"), ("sourceClassification", "source-prehardmode-policy-not-adopted"),
            ("sourcePredicate", "!Main.hardMode && currentbossdefeated != BossDType.WallOfFlesh"),
            ("nativeMechdusaWorld", context.NativeMechdusaWorld), ("worldHistoryComplete", context.WorldHistoryComplete),
            ("observedPossibleItemIds", context.ObservedPossibleItemIds), ("lostUseHistory", context.LostUseHistory),
            ("nativeEmissionChecksItemVariant", true), ("possibleClientVariants", "default,DisabledBossSummonVariant"),
            ("clientVariantHistoryComplete", false), ("hardCandidateAdmitted", false),
            ("acquisitionHistoryClaimed", false), ("sscExportProvesClientReceipt", false));
        if (!IsMechanicalSummon(type)) return RuleResults.Pass(RuleId, "outside-mechanical-variant-group", facts) with { Version = Version };
        if (!context.NativeMechdusaWorld)
            return RuleResults.Pass(RuleId, "native-mechanical-summon-variant-enabled", facts) with { Version = Version };
        if (sender != context.Input.Session.Slot || !context.WorldHistoryComplete || !context.PluginContractComplete || context.InitialSynchronization)
            return RuleResults.Unknown(RuleId, "mechanical-variant-world-session-or-plugin-history-incomplete", facts) with { Version = Version };
        return RuleResults.Unknown(RuleId, "mechanical-client-variant-refresh-and-use-source-history-unproved", facts) with { Version = Version };
    }
}
