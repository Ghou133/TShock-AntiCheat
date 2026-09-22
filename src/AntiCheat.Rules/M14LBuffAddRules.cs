using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M14LPlayerBuffAddObservation(int TargetSlot, int BuffType, int Time);

/// <summary>Original 1.4.5.8 packet55 producer semantics. The target is not a sender assertion.</summary>
public static class M14LBuffAddRules
{
    public const string PlayerRuleId = "G03.PlayerBuffAddContract";
    public const string NpcRuleId = "G03.NpcShadowFlameAddContract";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-player-add-buff-producer-v1";
    public const int BuffCount = 401;
    public const int ShadowFlameBuff = 153;
    public const int ShadowFlameMaximumRequestTicks = 600;
    // Main.Initialize, complete target pvpBuff set; checked against the loaded table by the adapter.
    public static bool IsNativePvpBuff(int type) => type is 20 or 24 or 30 or 31 or 36 or 39 or 44 or 69 or 70 or
        103 or 119 or 120 or 137 or 320 or 323 or 324 or 397 or 398 or 399 or 400;

    public static BusinessRuleResult EvaluatePlayer(M14LPlayerBuffAddObservation packet, RuleInputContext input,
        bool nativePvpTableIntact, string contractVersion = ContractVersion, bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", 55), ("senderSlot", input.Session.Slot),
            ("targetSlot", packet.TargetSlot), ("buffType", packet.BuffType), ("wireTimeInt32", packet.Time),
            ("contractVersion", contractVersion), ("nativePvpTableIntact", nativePvpTableIntact),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(PlayerRuleId, "player-buff-add-contract-unverified", facts);
        if (packet.TargetSlot is < 0 or >= 255 || packet.BuffType <= 0 || packet.BuffType >= BuffCount || packet.Time <= 0)
            return RuleResults.Block(PlayerRuleId, "player-buff-add-out-of-domain", facts);
        if (!input.ClientOrigin || scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(PlayerRuleId, "server-or-scoped-player-buff-add", facts);
        if (!nativePvpTableIntact || scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(PlayerRuleId, "player-buff-add-table-or-extension-unverified", facts);
        // Player.AddBuff is the sole original client55 producer: remote target and pvpBuff[type].
        // Receiving55 uses fromNetPvP:true on the local player and never echoes it.
        if (packet.TargetSlot == input.Session.Slot)
            return RuleResults.Candidate(PlayerRuleId, "client-self-targeted-remote-buff-add", input, facts);
        if (!IsNativePvpBuff(packet.BuffType))
            return RuleResults.Candidate(PlayerRuleId, "client-declared-nonpvp-remote-buff-type", input, facts);
        // Time, active/hostile state and range are not independent cheat evidence here.
        return RuleResults.Pass(PlayerRuleId, "native-remote-player-buff-add-shape", facts);
    }

    public static BusinessRuleResult EvaluateNpc(M13NpcBuffObservation packet, RuleInputContext input,
        string contractVersion = ContractVersion, bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", (int)packet.Operation), ("npcIndex", packet.NpcIndex),
            ("buffType", packet.BuffType), ("wireTimeInt16", packet.Time), ("contractVersion", contractVersion),
            ("maximumShadowFlameRequestTicks", ShadowFlameMaximumRequestTicks),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(NpcRuleId, "npc-buff-add-contract-unverified", facts);
        if (packet.Operation != M13NpcBuffOperation.Add)
            return RuleResults.Pass(NpcRuleId, "not-npc-buff-add", facts);
        if (packet.NpcIndex is < 0 or >= 200 || packet.BuffType <= 0 || packet.BuffType >= BuffCount ||
            packet.Time is < short.MinValue or > short.MaxValue)
            return RuleResults.Block(NpcRuleId, "npc-buff-add-out-of-domain", facts);
        if (!input.ClientOrigin || scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(NpcRuleId, "server-or-scoped-npc-buff-add", facts);
        if (scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(NpcRuleId, "npc-buff-add-extension-unverified", facts);
        if (packet.BuffType != ShadowFlameBuff)
            return RuleResults.Pass(NpcRuleId, "npc-buff-type-outside-shadowflame-contract", facts);
        if (packet.Time <= 0) return RuleResults.Block(NpcRuleId, "nonpositive-shadowflame-add-duration", facts);
        // Every native153 call is in StatusNPC. Its six source branches send at most600 ticks.
        // NPC.AddBuff sends the original request before deduplication and never amplifies time.
        if (packet.Time > ShadowFlameMaximumRequestTicks)
            return RuleResults.Candidate(NpcRuleId, "shadowflame-duration-outside-all-native-producers", input, facts);
        return RuleResults.Pass(NpcRuleId, "native-shadowflame-add-duration", facts);
    }
}
