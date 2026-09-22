using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public static class M15NpcBuffTypeAdapter
{
    /// <summary>Called by the runtime-verified raw hook with its engine-owned current connection binding.</summary>
    public static BusinessRuleResult Evaluate(M13NpcBuffObservation packet, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity && Main.maxNPCs == 200 &&
            BuffID.Count == M15NpcBuffTypeRules.BuffTypeCount;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        if (actor.HasPermission(Permissions.ignorenpcbuffdetection)) scopedClientExtensionAuthorized = true;
        var input = new RuleInputContext(session, fingerprint, fingerprint, true, complete, identity, host);
        return M14LBuffAddPacketReader.WithProvenance(
            M15NpcBuffTypeRules.Evaluate(packet, input, scopedClientExtensionAuthorized), input, host, identity);
    }
}
