using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>The closed NPC.AddBuff producer category union; it does not authorize a duration or existing NPC state.</summary>
public static class M15NpcBuffTypeRules
{
    public const string RuleId = "G03.NpcBuffTypeContract";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-npc-add-buff-type-producers-v1";
    public const int BuffTypeCount = 401;

    // Every literal and bounded dynamic NPC.AddBuff type in the original target is retained,
    // including server-only branches. Packet53's arbitrary received type is quiet and cannot
    // generate another client53. This deliberately includes native Burning44, unlike Bouncer's list.
    public static bool IsNativeProducerType(int type) => type is
        20 or 24 or 25 or 30 or 31 or 36 or 39 or 44 or 69 or 70 or 72 or 103 or 119 or 120 or
        137 or 151 or 153 or 165 or 169 or 183 or 186 or 189 or 203 or 204 or 310 or 320 or
        323 or 324 or 337 or 344 or 353 or 362 or 375 or 395 or 397 or 398 or 399 or 400;

    public static BusinessRuleResult Evaluate(M13NpcBuffObservation packet, RuleInputContext input,
        bool? scopedClientExtensionAuthorized = false, string contractVersion = ContractVersion)
    {
        var facts = RuleResults.Facts(("messageId", (int)packet.Operation), ("npcIndex", packet.NpcIndex),
            ("buffType", packet.BuffType), ("wireTimeInt16", packet.Time), ("contractVersion", contractVersion),
            ("nativeProducerType", IsNativeProducerType(packet.BuffType)),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "npc-buff-type-contract-unverified", facts);
        if (packet.Operation != M13NpcBuffOperation.Add)
            return RuleResults.Pass(RuleId, "outside-npc-buff-add-type-contract", facts);
        if (packet.NpcIndex is < 0 or >= 200 || packet.BuffType <= 0 || packet.BuffType >= BuffTypeCount ||
            packet.Time is < short.MinValue or > short.MaxValue)
            return RuleResults.Block(RuleId, "npc-buff-type-request-out-of-domain", facts);
        if (!input.ClientOrigin || scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "server-or-scoped-npc-buff-type", facts);
        if (scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "npc-buff-type-extension-unverified", facts);
        // Keep all signed Int16 projections for a native category. Neither a long request,
        // accumulated NPC time, NPC immunity nor a presently inactive target proves this rule.
        if (IsNativeProducerType(packet.BuffType))
            return RuleResults.Pass(RuleId, "native-npc-buff-producer-type", facts);
        if (packet.Time <= 0)
            return RuleResults.Block(RuleId, "nonpositive-nonnative-npc-buff-request", facts);
        return RuleResults.Candidate(RuleId, "npc-buff-type-outside-all-native-producers", input, facts);
    }
}
