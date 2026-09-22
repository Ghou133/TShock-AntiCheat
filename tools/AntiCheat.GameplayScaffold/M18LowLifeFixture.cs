using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // One-time, console-owned setup for the low-life Butcher observation. It
    // changes only the selected native NPC's current life in the disposable
    // server process. No strike, packet, immortality or repeated keep-alive
    // action is installed; the next client packet observes the real target
    // state and may kill it through the ordinary native path.
    private void M18LowLifeCommand(string[] args)
    {
        Require(args.Length == 3,
            "Use qa_m18_low_life <exact authenticated player> <native NPC index> <positive life below lifeMax>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_low_life.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated account without bypass required.");
        Require(int.TryParse(args[1], out int index) && index >= 0 && index < Main.npc.Length,
            "The low-life target index must be a valid native NPC slot.");
        Require(int.TryParse(args[2], out int requestedLife) && requestedLife > 0,
            "The low-life target life must be positive.");

        string marker = Path.Combine(root!, "qa-m18-low-life-" + player.Index + "-" + index + ".json");
        Require(!File.Exists(marker), "The one-time M18 low-life setup already ran; existing target state is retained.");

        var npc = Main.npc[index];
        Require(npc.active && !npc.friendly && npc.type != NPCID.TargetDummy && npc.type == 50,
            "The low-life fixture requires an active native hostile King Slime target, not a town or dummy fixture.");
        Require(npc.generation > 0 && npc.lifeMax > 1 && requestedLife < npc.lifeMax,
            "The low-life fixture requires a live native target and a requested life strictly below lifeMax.");

        var before = new
        {
            index,
            generation = npc.generation,
            type = npc.type,
            active = npc.active,
            life = npc.life,
            lifeMax = npc.lifeMax,
            position = new { x = npc.position.X, y = npc.position.Y }
        };
        npc.life = requestedLife;
        npc.netUpdate = true;
        TSPlayer.All.SendData(PacketTypes.NpcUpdate, number: index);
        var after = new
        {
            index,
            generation = npc.generation,
            type = npc.type,
            active = npc.active,
            life = npc.life,
            lifeMax = npc.lifeMax,
            position = new { x = npc.position.X, y = npc.position.Y }
        };
        Require(after.active && after.life == requestedLife && after.life < after.lifeMax,
            "The native low-life setup did not retain the requested positive life below lifeMax.");

        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            fixtureArtificial = true,
            command = "qa_m18_low_life",
            actor = player.Name,
            accountId = player.Account!.ID,
            playerIndex = player.Index,
            source = "native Main.npc slot selected from the prior live Butcher predicate witness",
            before,
            requestedLife,
            after,
            mutation = "one-time current NPC life assignment plus NpcUpdate; no StrikeNPC, no keep-alive, no bypass",
            nextOperation = "ordinary authenticated packet28 source-equivalent Butcher request; target may die through the native receiver",
            sanction = "none"
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(payload, jsonOptions));
        Record("m18-low-life-native-target", payload);
        TSPlayer.Server.SendInfoMessage("M18 low-life native target prepared: index=" + index +
            ", generation=" + after.generation + ", life=" + after.life + "/" + after.lifeMax + "; no keep-alive installed.");
    }
}
