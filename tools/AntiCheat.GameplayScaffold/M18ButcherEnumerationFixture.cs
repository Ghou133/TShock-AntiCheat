using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private readonly HashSet<string> m18NativeStrikeTargets = new(StringComparer.Ordinal);
    private readonly List<object> m18NativeStrikeEvents = new(256);
    private int m18NativeStrikeEventsDropped;

    // This observer never changes ContinueExecution or any native object. It is
    // armed only after the source-shaped, read-only Main.npc enumeration below
    // has selected the live targets. Its fromNet/owner fields let the NetworkLab
    // distinguish a blocked client packet from unrelated native AI state change.
    private void ObserveM18NativeStrike(NPC npc, HookEvents.Terraria.NPC.StrikeNPCEventArgs args)
    {
        if (!recording || !m18NativeStrikeTargets.Contains(npc.whoAmI + ":" + npc.generation)) return;
        if (m18NativeStrikeEvents.Count >= 256)
        {
            if (m18NativeStrikeEventsDropped < int.MaxValue) m18NativeStrikeEventsDropped++;
            return;
        }
        m18NativeStrikeEvents.Add(new
        {
            utc = DateTimeOffset.UtcNow,
            target = npc.whoAmI,
            generation = npc.generation,
            type = npc.type,
            lifeAtEntry = npc.life,
            damage = args.Damage,
            fromNet = args.fromNet,
            owner = args.owner,
            critical = args.crit,
            continueExecution = args.ContinueExecution,
            thread = Environment.CurrentManagedThreadId
        });
    }

    // Read-only source-shaped observation for P0-A. It deliberately mirrors
    // ButcherAllHostileNPCs' predicate over the live server Main.npc array,
    // but never calls a strike, sends a packet, or changes an NPC.
    private void M18ButcherEnumerationCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_butcher_enum <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_butcher_enum.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated account without bypass required.");

        string marker = Path.Combine(root!, "qa-m18-butcher-enumeration-" + player.Index + ".json");
        Require(!File.Exists(marker), "The one-time M18 Butcher enumeration already ran; existing observation is retained.");

        var slots = Main.npc.Select((npc, index) => new
        {
            index,
            active = npc.active,
            friendly = npc.friendly,
            targetDummy = npc.type == NPCID.TargetDummy,
            selected = npc.active && !npc.friendly && npc.type != NPCID.TargetDummy,
            type = npc.type,
            generation = npc.generation,
            life = npc.life,
            lifeMax = npc.lifeMax,
            defense = npc.defense,
            immortal = npc.immortal,
            position = new { x = npc.position.X, y = npc.position.Y }
        }).ToArray();
        var selected = slots.Where(npc => npc.selected).ToArray();
        m18NativeStrikeTargets.Clear();
        foreach (var npc in selected.Where(npc => npc.type == 50).Take(8))
            m18NativeStrikeTargets.Add(npc.index + ":" + npc.generation);
        m18NativeStrikeEvents.Clear();
        m18NativeStrikeEventsDropped = 0;

        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            fixtureArtificial = false,
            sourcePredicate = "Main.npc[i].active && !Main.npc[i].friendly && Main.npc[i].type != NPCID.TargetDummy",
            source = "TerraAngel Tools/Butcher.cs: ButcherAllHostileNPCs",
            actor = player.Name,
            accountId = player.Account!.ID,
            playerIndex = player.Index,
            scannedSlots = slots.Length,
            activeCount = slots.Count(npc => npc.active),
            friendlyActiveCount = slots.Count(npc => npc.active && npc.friendly),
            targetDummyActiveCount = slots.Count(npc => npc.active && npc.targetDummy),
            selectedCount = selected.Length,
            selected,
            nativeStrikeObserverArmed = m18NativeStrikeTargets.Order(StringComparer.Ordinal).ToArray(),
            mutation = "none; read-only Main.npc enumeration; no StrikeNPC, NetMessage or state assignment",
            bypassPath = "not invoked; NPCDebuffDamage remains a separate source branch",
            sanction = "none"
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(payload, jsonOptions));
        Record("m18-butcher-hostile-enumeration", payload);
        TSPlayer.Server.SendInfoMessage("M18 Butcher hostile enumeration recorded: scanned=" + slots.Length +
            ", active=" + payload.activeCount + ", selected=" + selected.Length + ", targetDummyExcluded=" + payload.targetDummyActiveCount + ". Read-only; no NPC mutation.");
    }
}
