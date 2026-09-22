using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed partial class M5ProgressionContexts
{
    public BusinessRuleResult? EvaluateNaturalTeleport(GetDataEventArgs args, SessionKey session,
        TSPlayer actor, bool verifiedRuntime)
    {
        var read = M9PlayerTeleportGuard.Read(args, verifiedRuntime);
        if (read.Kind != PacketReadKind.Parsed || read.Packet is not { MessageId: 65 } request) return null;
        ObserveConnection(session, actor);
        var state = (uint)session.Slot < states.Length ? states[session.Slot] : null;
        var target = lookup?.Invoke(session.Slot);
        var current = target?.Session;
        bool bound = current is not null && current.Key == session && !current.Revoked &&
            ReferenceEquals(target?.Player, actor) && actor.Index == session.Slot && actor.IsLoggedIn &&
            actor.Account is not null && current.AccountId == actor.Account.ID;
        bool complete = installed && !exportObservationFailed && !sigilExportObservationFailed &&
            Main.netMode == 2 && Main.myPlayer == 255 && updateThread == Environment.CurrentManagedThreadId &&
            epoch == session.WorldEpoch && state is { GeometryComplete: true } && state.Session == session &&
            state.World.Id == Main.worldID && state.InitialGeometry == (Main.maxTilesX, Main.maxTilesY);
        bool plugins = state is { PluginContractComplete: true } && NativePluginsOnly();
        var result = M14NaturalRodRules.Evaluate(request.MessageId, request.Flags, request.ClaimedSlot,
            request.X, request.Y, request.Style,
            new(new(session, fingerprint, fingerprint, true, complete, bound, complete && plugins),
                complete, Main.maxTilesX, Main.maxTilesY,
                !actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo, plugins));
        return result with { Facts = result.Facts
            .SetItem("worldId", Main.worldID.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetItem("worldEpoch", session.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetItem("currentAccountAndActorBound", bound.ToString())
            .SetItem("worldExportObservationHealthy", (!exportObservationFailed && !sigilExportObservationFailed).ToString()) };
    }
}
