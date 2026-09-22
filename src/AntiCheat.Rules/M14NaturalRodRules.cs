using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M14RodGeometryContext(RuleInputContext Input, bool GeometryHistoryComplete,
    int WidthTiles, int HeightTiles, bool InitialSynchronization, bool PluginContractComplete);

/// <summary>Only the original client's plain mode0/style1 rod request is covered. Its final
/// geometry predicate is independent of inventory provenance, starting animation, or tile view.</summary>
public static class M14NaturalRodRules
{
    public const string RuleId = "G05.NaturalRodWorldBorder";
    public const string Version = "1.0.0";
    public const string Contract = "terraria1.4.5.8-326-plain-rod-world-interior-v1";

    public static BusinessRuleResult Evaluate(int messageId, byte flags, int sender, float x, float y,
        int style, M14RodGeometryContext context)
    {
        var facts = RuleResults.Facts(("messageId", messageId), ("flags", flags), ("sender", sender),
            ("style", style), ("x", x), ("y", y), ("widthTiles", context.WidthTiles),
            ("heightTiles", context.HeightTiles), ("geometryHistoryComplete", context.GeometryHistoryComplete),
            ("pluginContractComplete", context.PluginContractComplete), ("initialSynchronization", context.InitialSynchronization),
            ("contract", Contract), ("nativeItemIds", "1326,5335"), ("sourceOperations", "MKLP-OP-081,MKLP-OP-125"),
            ("sourcePossessionPolicyAdopted", false), ("acquisitionHistoryClaimed", false),
            ("currentHeldItemRequired", false), ("tileViewClaimed", false),
            ("proofKind", "natural-client-final-rod-emission-world-interior"));
        if (messageId != 65 || flags != 0 || style != 1)
            return RuleResults.Pass(RuleId, "outside-plain-rod-contract", facts);
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return RuleResults.Unknown(RuleId, "nonfinite-destination-owned-by-parameter-safety", facts);
        if (context.WidthTiles is < 7 or > short.MaxValue || context.HeightTiles is < 7 or > short.MaxValue)
            return RuleResults.Unknown(RuleId, "native-world-geometry-unavailable", facts);
        if (x > 50f && x < context.WidthTiles * 16 - 50 && y > 50f && y < context.HeightTiles * 16 - 50)
            return RuleResults.Pass(RuleId, "within-native-rod-world-interior", facts);
        if (sender != context.Input.Session.Slot || !context.GeometryHistoryComplete ||
            context.InitialSynchronization || !context.PluginContractComplete)
            return RuleResults.Unknown(RuleId, "rod-world-history-or-subject-incomplete", facts);
        return RuleResults.Candidate(RuleId, "native-rod-cannot-emit-destination-outside-world-interior", context.Input, facts);
    }
}
