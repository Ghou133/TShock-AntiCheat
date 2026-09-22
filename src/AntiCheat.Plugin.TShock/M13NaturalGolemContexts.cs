using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed partial class M5ProgressionContexts
{
    private BusinessRuleResult EvaluateGolem(int sender, int type, SessionKey session, TSPlayer actor,
        State? state, bool bound, bool plugins)
    {
        bool complete = installed && !exportObservationFailed && !sigilExportObservationFailed &&
            Main.netMode == 2 && Main.myPlayer == 255 && Main.maxTilesX > 0 &&
            updateThread == Environment.CurrentManagedThreadId && epoch == session.WorldEpoch &&
            state is { GolemComplete: true } && state.Session == session && state.World.Id == Main.worldID &&
            state.GolemFeatures == (Main.hardMode, NPC.downedPlantBoss);
        var result = M13NaturalGolemRules.Evaluate(sender, type,
            new(new(session, fingerprint, fingerprint, true, complete, bound, complete && plugins),
                complete, Main.hardMode, NPC.downedPlantBoss,
                !actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo, plugins));
        return result with { Facts = result.Facts
            .SetItem("worldId", Main.worldID.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetItem("worldEpoch", session.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetItem("currentAccountAndActorBound", bound.ToString())
            .SetItem("worldExportObservationHealthy", (!exportObservationFailed && !sigilExportObservationFailed).ToString()) };
    }
}
