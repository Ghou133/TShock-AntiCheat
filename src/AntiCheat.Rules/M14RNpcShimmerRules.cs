using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>Limits the single request, never an NPC's cumulative or server-owned Buff state.</summary>
public static class M14RNpcShimmerRules
{
    public const string RuleId = "G03.NpcShimmerAddContract";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-npc-shimmer-producers-v1";
    public const int BuffType = 353;
    public const int MaximumRequestTicks = 100;

    public static BusinessRuleResult Evaluate(M13NpcBuffObservation packet, RuleInputContext input,
        bool? scopedClientExtensionAuthorized = false, string contractVersion = ContractVersion)
    {
        var facts = RuleResults.Facts(("messageId", (int)packet.Operation), ("npcIndex", packet.NpcIndex),
            ("buffType", packet.BuffType), ("wireTimeInt16", packet.Time), ("contractVersion", contractVersion),
            ("maximumRequestTicks", MaximumRequestTicks), ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "npc-shimmer-contract-unverified", facts);
        if (packet.Operation != M13NpcBuffOperation.Add || packet.BuffType != BuffType)
            return RuleResults.Pass(RuleId, "outside-npc-shimmer-add-contract", facts);
        if (packet.NpcIndex is < 0 or >= 200 || packet.Time is < short.MinValue or > short.MaxValue || packet.Time <= 0)
            return RuleResults.Block(RuleId, "npc-shimmer-add-out-of-domain", facts);
        if (!input.ClientOrigin || scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "server-or-scoped-npc-shimmer-add", facts);
        if (scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "npc-shimmer-extension-unverified", facts);
        // Projectile.AI style60/type1042 sends353/100; water collision's repeated353/100
        // is server-only. Receive53 is quiet, and AddBuff does not amplify request time.
        if (packet.Time > MaximumRequestTicks)
            return RuleResults.Candidate(RuleId, "shimmer-duration-outside-all-native-producers", input, facts);
        return RuleResults.Pass(RuleId, "native-shimmer-add-duration", facts);
    }
}
