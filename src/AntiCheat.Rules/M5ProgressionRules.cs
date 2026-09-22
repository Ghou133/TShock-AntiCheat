using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M5MechdusaContext(RuleInputContext Input, bool WorldUnchangedSinceConnection,
    bool ZenithWorld, bool NativeMechdusaWorld, bool InitialSynchronization, bool PluginContractComplete);

/// <summary>
/// An action-specific refinement of MKLP-OP-012. Possession or origin of Ocram's Razor is
/// irrelevant: the native client producer returns before sending when this world feature is off.
/// </summary>
public static class M5ProgressionRules
{
    public const string RuleId = "PG-NAT-012.MechdusaSummonWorld";
    public const string Version = "1.0.0";
    public static BusinessRuleResult EvaluateMechdusa(int sender, int summonType, M5MechdusaContext context)
    {
        var facts = RuleResults.Facts(("sender", sender), ("summonType", summonType),
            ("sourceOperation", "MKLP-OP-012"), ("sourcePredicate", "!Main.zenithWorld"),
            ("nativeFeature", context.NativeMechdusaWorld), ("zenithWorld", context.ZenithWorld),
            ("proofKind", "natural-client-action-world-gate"));
        if (summonType != -16) return RuleResults.Pass(RuleId, "other-summon-outside-mechdusa-contract", facts);
        // The original condition stays intact; the target's additional legal combined-seed path is excluded.
        if (context.ZenithWorld || context.NativeMechdusaWorld)
            return RuleResults.Pass(RuleId, "native-mechdusa-or-source-world-exception", facts);
        if (sender != context.Input.Session.Slot)
            return RuleResults.Unknown(RuleId, "summon-sender-not-current-subject", facts);
        if (!context.WorldUnchangedSinceConnection || context.InitialSynchronization || !context.PluginContractComplete)
            return RuleResults.Unknown(RuleId, "world-export-session-or-plugin-contract-incomplete", facts);
        return RuleResults.Candidate(RuleId, "native-client-cannot-request-mechdusa-in-this-world", context.Input, facts);
    }
}
