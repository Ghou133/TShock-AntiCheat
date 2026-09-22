using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum NpcServerAuthorityMessage { DebuffDamage = 153, PortalTeleport = 100 }

public sealed record NpcServerAuthorityObservation(NpcServerAuthorityMessage Message, int NpcIndex,
    int DebuffAmount = 0, int PortalColorIndex = 0, float X = 0, float Y = 0, float VelocityX = 0, float VelocityY = 0);

public sealed record ChestResizeObservation(int ChestIndex, int NewSize);

/// <summary>
/// Protocol-role proofs, independently implemented after inspecting the locked target's actual
/// producers and consumers. Damage magnitude and teleport distance are not the proof predicate.
/// </summary>
public static class M3CheatAttemptRules
{
    public const string DebuffRuleId = "NPC01.ServerDebuffDamage";
    public const string PortalRuleId = "NPC02.ServerPortalTeleport";
    public const string ChestResizeRuleId = "CONTAINER01.ChestResizeAuthority";
    public const string ContractVersion = "terraria1.4.5.8-326-npc-server-authority-v1";

    public static BusinessRuleResult Evaluate(NpcServerAuthorityObservation observation, RuleInputContext input,
        int npcCapacity, string auditedContractVersion)
    {
        string id = observation.Message == NpcServerAuthorityMessage.DebuffDamage ? DebuffRuleId : PortalRuleId;
        var facts = RuleResults.Facts(("messageId", (int)observation.Message), ("npcIndex", observation.NpcIndex),
            ("debuffAmount", observation.DebuffAmount), ("portalColor", observation.PortalColorIndex),
            ("positionX", observation.X), ("positionY", observation.Y),
            ("velocityX", observation.VelocityX), ("velocityY", observation.VelocityY),
            ("contractVersion", auditedContractVersion), ("actualOrigin", input.ClientOrigin ? "client-connection" : "server"));
        if (!Enum.IsDefined(observation.Message) || !input.ParserComplete)
            return RuleResults.Unknown(id, "npc-authority-message-or-parser-unverified", facts);
        if (!input.VersionMatched || auditedContractVersion != ContractVersion)
            return RuleResults.Unknown(id, "npc-authority-target-contract-unverified", facts);
        if (npcCapacity <= 0 || npcCapacity > 256)
            return RuleResults.Unknown(id, "npc-capacity-unverified", facts);
        if (observation.NpcIndex < 0 || observation.NpcIndex >= npcCapacity)
            return RuleResults.Block(id, "npc-target-out-of-range", facts);
        if (observation.Message == NpcServerAuthorityMessage.PortalTeleport &&
            (!float.IsFinite(observation.X) || !float.IsFinite(observation.Y) ||
             !float.IsFinite(observation.VelocityX) || !float.IsFinite(observation.VelocityY)))
            return RuleResults.Block(id, "nonfinite-npc-portal-vector", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(id, "legitimate-server-npc-authority-message", facts);
        // NPC.ApplyEelWhipDoT emits 153 only in netMode 2. PortalHelper's NPC branch emits
        // 100 only in netMode 2; its legitimate player branch uses packet 96 instead.
        // Incoming server broadcasts are never observed as a client's NetGetData here.
        return RuleResults.Candidate(id, observation.Message == NpcServerAuthorityMessage.DebuffDamage
            ? "client-issued-server-only-npc-debuff-damage" : "client-issued-server-only-npc-portal-teleport",
            input, facts);
    }

    /// <summary>
    /// Vanilla only sends 155 as server chest-size synchronization. The audited TShock additionally
    /// exposes a narrow resizechests permission for client extensions, so it is an explicit exception.
    /// </summary>
    public static BusinessRuleResult EvaluateChestResize(ChestResizeObservation observation, RuleInputContext input,
        int chestCapacity, bool targetExists, bool? resizePermission, string auditedContractVersion)
    {
        var facts = RuleResults.Facts(("messageId", 155), ("chestIndex", observation.ChestIndex),
            ("newSize", observation.NewSize), ("resizePermission", resizePermission), ("contractVersion", auditedContractVersion));
        if (!input.ParserComplete || !input.VersionMatched || auditedContractVersion != ContractVersion)
            return RuleResults.Unknown(ChestResizeRuleId, "chest-resize-target-contract-unverified", facts);
        if (chestCapacity <= 0 || chestCapacity > 32768)
            return RuleResults.Unknown(ChestResizeRuleId, "chest-capacity-unverified", facts);
        if (observation.ChestIndex < 0 || observation.ChestIndex >= chestCapacity || !targetExists)
            return RuleResults.Block(ChestResizeRuleId, "chest-resize-target-unavailable", facts);
        if (observation.NewSize < 0)
            return RuleResults.Block(ChestResizeRuleId, "negative-chest-size", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(ChestResizeRuleId, "legitimate-server-chest-size-sync", facts);
        if (resizePermission is null)
            return RuleResults.Unknown(ChestResizeRuleId, "chest-resize-permission-unavailable", facts);
        if (resizePermission == true)
            return RuleResults.Pass(ChestResizeRuleId, "scoped-tshock-chest-resize-permission", facts);
        return RuleResults.Candidate(ChestResizeRuleId, "client-chest-resize-without-scoped-permission", input, facts);
    }
}
