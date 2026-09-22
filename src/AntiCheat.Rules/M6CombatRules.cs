using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record NpcStrikeObservation(int TargetSlot, int TargetGeneration, int Damage,
    float Knockback, int EncodedDirection, int CriticalFlag);

/// <summary>Direct packet28 structure, separate from any claim about a weapon's reachable damage.</summary>
public static class M6CombatRules
{
    public const string StrikeRuleId = "C0.NpcStrikeStructure";
    public const string Version = "1.0.0";
    public const string MechanismVersion = "terraria-1.4.5.8-npc-strike-wire-v1";

    public static BusinessRuleResult EvaluateStrike(NpcStrikeObservation observation, RuleInputContext input,
        int npcCapacity, bool targetSnapshotComplete, bool targetActive, int currentGeneration)
    {
        var facts = RuleResults.Facts(("npcSlot", observation.TargetSlot), ("npcGeneration", observation.TargetGeneration),
            ("currentGeneration", currentGeneration), ("damage", observation.Damage), ("knockback", observation.Knockback),
            ("encodedDirection", observation.EncodedDirection), ("criticalFlag", observation.CriticalFlag),
            ("mechanismVersion", MechanismVersion), ("damageSourceCompleteness", "unavailable"));
        if (!input.ClientOrigin) return RuleResults.Pass(StrikeRuleId, "server-npc-strike", facts);
        if (!input.VersionMatched || !input.ParserComplete || npcCapacity <= 0)
            return RuleResults.Unknown(StrikeRuleId, "npc-strike-wire-contract-unavailable", facts);
        if (observation.TargetSlot < 0 || observation.TargetSlot >= npcCapacity)
            return RuleResults.Block(StrikeRuleId, "npc-strike-target-out-of-range", facts);
        // The native receiver performs this check before PlayerInteraction/StrikeNPC. A delayed hit
        // for an old generation is a normal no-op, never evidence against the sending account.
        if (targetSnapshotComplete && observation.TargetGeneration != currentGeneration)
            return RuleResults.Pass(StrikeRuleId, "npc-strike-old-generation-native-noop", facts);
        if (!float.IsFinite(observation.Knockback) || observation.EncodedDirection is < 0 or > 2 ||
            observation.CriticalFlag is < 0 or > 1)
            return RuleResults.Block(StrikeRuleId, "npc-strike-noncanonical-numeric-input", facts);
        if (!targetSnapshotComplete || !targetActive)
            return RuleResults.Unknown(StrikeRuleId, "npc-strike-target-lifecycle-unavailable", facts);
        // Damage is intentionally not capped here. Packet28 carries no weapon/projectile key;
        // even negative Int16 cannot by itself prove a role violation (the writer narrows a float).
        return RuleResults.Unknown(StrikeRuleId, "npc-strike-cause-and-reachable-damage-unavailable", facts);
    }
}
