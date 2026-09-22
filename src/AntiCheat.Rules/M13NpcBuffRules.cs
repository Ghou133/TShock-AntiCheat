using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum M13NpcBuffOperation { Add = 53, Remove = 137 }
public sealed record M13NpcBuffObservation(M13NpcBuffOperation Operation, int NpcIndex, int BuffType, int Time = 0);

/// <summary>NPC buff request semantics for the exact 1.4.5.8 producer contract, not a buff-duration heuristic.</summary>
public static class M13NpcBuffRules
{
    public const string RuleId = "G03.NpcBuffRemovalContract";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-npc-buff-removal-empty-set-v1";
    public const int BuffTypeCount = 401;

    public static BusinessRuleResult Evaluate(M13NpcBuffObservation observation, RuleInputContext input,
        int npcCapacity, bool nativeRemovalTableIntact, string contractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", (int)observation.Operation), ("npcIndex", observation.NpcIndex),
            ("buffType", observation.BuffType), ("time", observation.Time), ("contractVersion", contractVersion),
            ("removalTableIntact", nativeRemovalTableIntact), ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized));
        if (!Enum.IsDefined(observation.Operation) || !input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "npc-buff-request-contract-unverified", facts);
        if (npcCapacity <= 0 || npcCapacity > 32768)
            return RuleResults.Unknown(RuleId, "npc-buff-capacity-unverified", facts);
        if (observation.NpcIndex < 0 || observation.NpcIndex >= npcCapacity ||
            observation.BuffType <= 0 || observation.BuffType >= BuffTypeCount)
            return RuleResults.Block(RuleId, "npc-buff-request-out-of-domain", facts);
        // AddBuff is the real original-client path. Neither duration nor current NPC state
        // is a cheat predicate here; TShock's existing AddNPCBuff checks remain responsible.
        if (observation.Operation == M13NpcBuffOperation.Add)
            return RuleResults.Pass(RuleId, "native-npc-buff-add-request", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "server-npc-buff-removal", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-npc-buff-removal-extension", facts);
        if (scopedClientExtensionAuthorized is null || !nativeRemovalTableIntact)
            return RuleResults.Unknown(RuleId, "npc-buff-removal-extension-or-table-unverified", facts);
        // In the locked target, the entire CanBeRemovedByNetMessage set is empty. The
        // sole native packet137 producer tests this set before FindBuffIndex/DelBuff/SendData.
        // A missing/expired NPC buff, or network delay, is deliberately not part of the proof.
        return RuleResults.Candidate(RuleId, "client-requested-nonremovable-npc-buff", input, facts);
    }
}
