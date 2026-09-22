using System.Reflection;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

// Extension of the existing isolated GameplayScaffold. Never distributed as a server plugin.
// All mutation commands retain the original console/loopback/marker/world/database checks.
public sealed partial class GameplayScaffold
{
    private const BindingFlags PrivateM5 = BindingFlags.NonPublic | BindingFlags.Instance;
    private int m5Target = -1;
    private int m8StrikeActor = -1;
    private byte m5Generation;
    private int m5LastExportedLife = int.MinValue;
    private readonly List<object> m5Effects = new(512);
    private int m5EffectsDropped;
    private FileStream? m5ReadLock;
    private M6RuntimeDiagnostics? m6RuntimeDiagnostics;

    private void InstallM5()
    {
        // The output is available only after the existing console/loopback/world/marker validation.
        m6RuntimeDiagnostics = new(() => recording ? output : null, this);
        m6RuntimeDiagnostics.Install();
        InstallM6Safety();
        HookEvents.Terraria.NPC.StrikeNPC += ObserveM5Strike;
        HookEvents.Terraria.NPC.GetHurtByDebuff += ObserveM5Debuff;
        HookEvents.Terraria.NPC.Teleport += ObserveM5Teleport;
        HookEvents.Terraria.NPC.NPCLoot += ObserveM5Loot;
        HookEvents.Terraria.NetMessage.SendData += ObserveM5Send;
        GetDataHandlers.NewProjectile.Register(ObserveM5Projectile, HandlerPriority.Lowest, true);
    }

    private void DisposeM5()
    {
        m6RuntimeDiagnostics?.Dispose(); m6RuntimeDiagnostics = null;
        DisposeM6Safety();
        HookEvents.Terraria.NPC.StrikeNPC -= ObserveM5Strike;
        HookEvents.Terraria.NPC.GetHurtByDebuff -= ObserveM5Debuff;
        HookEvents.Terraria.NPC.Teleport -= ObserveM5Teleport;
        HookEvents.Terraria.NPC.NPCLoot -= ObserveM5Loot;
        HookEvents.Terraria.NetMessage.SendData -= ObserveM5Send;
        GetDataHandlers.NewProjectile.UnRegister(ObserveM5Projectile);
        m5ReadLock?.Dispose(); m5ReadLock = null;
    }

    private void M5Effect(object effect)
    {
        if (!recording) return;
        // Keep later GUI actions observable through the existing bounded journal even when
        // the first-512 snapshot budget has been consumed by fixture setup or NPC combat.
        Observe("native-method-effect", effect);
        if (m5Effects.Count < 512) m5Effects.Add(new { utc = DateTimeOffset.UtcNow, effect });
        else if (m5EffectsDropped < int.MaxValue) m5EffectsDropped++;
    }

    private void ObserveM5Strike(NPC npc, HookEvents.Terraria.NPC.StrikeNPCEventArgs args)
    {
        if (npc.whoAmI == m5Target && npc.generation == m5Generation)
            M5Effect(new { kind = "strike-method-entry", target = npc.whoAmI, npc.generation, npc.active,
                lifeBefore = npc.life, args.Damage, args.fromNet, args.owner, args.crit, args.ContinueExecution });
    }
    private void ObserveM5Debuff(NPC npc, HookEvents.Terraria.NPC.GetHurtByDebuffEventArgs args)
    {
        if (npc.whoAmI == m5Target && npc.generation == m5Generation) M5Effect(new { kind = "debuff-method-entry", target = npc.whoAmI,
            lifeBefore = npc.life, args.amount, args.ContinueExecution });
    }
    private void ObserveM5Teleport(NPC npc, HookEvents.Terraria.NPC.TeleportEventArgs args)
    {
        if (npc.whoAmI == m5Target && npc.generation == m5Generation) M5Effect(new { kind = "teleport-method-entry", target = npc.whoAmI,
            xBefore = npc.position.X, yBefore = npc.position.Y, x = args.newPos.X, y = args.newPos.Y, args.Style, args.ContinueExecution });
    }
    private void ObserveM5Loot(NPC npc, HookEvents.Terraria.NPC.NPCLootEventArgs args)
    {
        if (npc.whoAmI == m5Target && npc.generation == m5Generation)
            M5Effect(new { kind = "loot-method-entry", target = npc.whoAmI, npc.generation, args.ContinueExecution });
    }
    private void ObserveM5Projectile(object? sender, GetDataHandlers.NewProjectileEventArgs args)
    {
        if (args.Type is 601 or 602)
            M5Effect(new { kind = "post-core-projectile-handler", type = args.Type, args.Handled, player = args.Player.Index });
    }
    private void ObserveM5Send(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!recording || !args.ContinueExecution) return;
        bool lifeExport = args.msgType == 23 && args.number == m5Target &&
            Main.npc[m5Target].life != m5LastExportedLife;
        if (lifeExport) m5LastExportedLife = Main.npc[m5Target].life;
        // Repeated position-only NPC syncs must not exhaust method evidence during GUI setup.
        // Every life transition is retained; detailed positions still come from bounded snapshots.
        if (args.msgType is 100 or 153 or 155 || lifeExport || args.msgType == 28 && args.number == m5Target)
            M5Effect(new { kind = "send-method-entry", packet = args.msgType, target = args.number,
                args.number2, args.number3, args.number4, args.number5, args.remoteClient, args.ignoreClient });
        if (args.msgType == 27 && (uint)args.number < Main.projectile.Length && Main.projectile[args.number] is { } shot)
            M5Effect(new { kind = "projectile-export", shot.type, shot.damage, shot.originalDamage, key = shot.key.bits,
                args.remoteClient, args.ignoreClient });
    }

    private void PrepareM5Target(string[] arguments)
    {
        Require(arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "gui",
            "Use qa_m5_target <exact authenticated player> [gui].");
        bool gui = arguments.Length == 2;
        var player = ResolvePlayer(arguments[0]);
        Require(!player.HasPermission(Permissions.bypassssc) && !player.HasPermission("anticheat.bypass"), "Ordinary account required.");
        Require(m5Target < 0 || !Main.npc[m5Target].active || Main.npc[m5Target].generation != m5Generation,
            "Existing live M5 target retained; inspect it instead of resetting its effects.");
        m5Target = NPC.NewNPC(new EntitySource_DebugCommand(), (int)player.TPlayer.position.X + 120,
            (int)player.TPlayer.position.Y - 80, NPCID.BlueSlime);
        Require((uint)m5Target < Main.maxNPCs && Main.npc[m5Target].active, "M5 target creation failed.");
        var target = Main.npc[m5Target];
        m8StrikeActor = player.Index;
        m5Generation = target.generation;
        m5LastExportedLife = int.MinValue;
        // Fixed isolated sample makes small legitimate and large tool hits measurable before death.
        // This artificial target setup is reported separately from the input being tested.
        target.life = 3000;
        target.lifeMax = gui ? 3001 : 3000;
        if (!gui)
        {
            target.defense = 0; target.damage = 0;
            target.aiStyle = -1; target.noGravity = true; target.noTileCollide = true; target.knockBackResist = 0;
            target.velocity = Microsoft.Xna.Framework.Vector2.Zero;
        }
        // GUI uses native AI/physics on both peers. In packet23, life==lifeMax omits life
        // and the receiving client substitutes its own native maximum (25 for BlueSlime).
        // A distinct artificial maximum forces the actual 3000 current life onto the wire.
        target.timeLeft = 36000; target.netUpdate = true;
        NetMessage.SendData(23, number: m5Target);
        M5Effect(new { kind = "fixture-target-created", target = m5Target, target.generation, target.life,
            gui, target.lifeMax, target.aiStyle, target.defense, target.damage,
            note = gui ? "Console setup, not a player hit; native AI/physics, explicit current-life sync. Artificial server maximum 3001 is not a native BlueSlime maximum."
                : "Console setup, not a player hit; AI/gravity disabled on this owned target only." });
        WriteM5State();
    }

    private object M5Plugin() => ServerApi.Plugins.Select(x => x.Plugin)
        .Single(x => x.GetType().FullName == "AntiCheat.Plugin.TShock.AntiCheatPlugin");

    private void InjectM5Maintenance()
    {
        var plugin = M5Plugin();
        var maintenance = plugin.GetType().GetMethod("RunInfrastructureMaintenance", PrivateM5)!;
        Exception? failure = null;
        var thread = new Thread(() => { try { maintenance.Invoke(plugin, null); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.Start(); Require(thread.Join(TimeSpan.FromSeconds(3)), "Fault injection thread did not terminate.");
        Require(failure is null, "Maintenance callback escaped its isolation boundary.");
        Require((bool)plugin.GetType().GetField("_infrastructureFailed", PrivateM5)!.GetValue(plugin)!, "Dispatcher fault was not latched.");
        WriteM5State();
    }

    private void InjectM5Recoverable()
    {
        Require(m5ReadLock is null, "A journal read lock is already held.");
        var plugin = M5Plugin();
        var operation = plugin.GetType().GetField("_operation", PrivateM5)!;
        Require(operation.GetValue(plugin) is not Task { IsCompleted: false }, "Wait for existing persistence work.");
        string directory = Path.Combine(TShock.SavePath, "anticheat", "enforcement");
        var path = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).FirstOrDefault();
        Require(path is not null && IsUnder(path, root!), "A real existing isolated intent is required.");
        m5ReadLock = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.None);
        var engine = plugin.GetType().GetField("_engine", PrivateM5)!.GetValue(plugin)!;
        var recovery = engine.GetType().GetMethod("RecoverAsync")!.Invoke(engine, new object[] { CancellationToken.None })!;
        operation.SetValue(plugin, recovery.GetType().GetMethod("AsTask")!.Invoke(recovery, null));
        M5Effect(new { kind = "real-journal-file-read-lock", fileName = Path.GetFileName(path), bytesUnchanged = true });
        WriteM5State();
    }
    private void RecoverM5()
    {
        Require(m5ReadLock is not null, "Only the owned recoverable read lock can be released.");
        m5ReadLock!.Dispose(); m5ReadLock = null;
        // Do not clear engine/root flags. The existing recovery loop must validate the unchanged file itself.
        M5Effect(new { kind = "journal-read-lock-released", manualSecurityStateReset = false });
        WriteM5State();
    }

    private void WriteM5State()
    {
        var npc = (uint)m5Target < Main.maxNPCs ? Main.npc[m5Target] : null;
        // The identical runtime diagnostic control also runs with only TShock and this scaffold loaded.
        // Mutation/fault-injection commands keep the mandatory M5Plugin() lookup above.
        var plugin = ServerApi.Plugins.Select(x => x.Plugin)
            .SingleOrDefault(x => x.GetType().FullName == "AntiCheat.Plugin.TShock.AntiCheatPlugin");
        object? PluginField(string name) => plugin?.GetType().GetField(name, PrivateM5)?.GetValue(plugin);
        var engine = PluginField("_engine");
        var binding = (uint)m8StrikeActor < 255 ? (PluginField("_bindings") as Array)?.GetValue(m8StrikeActor) : null;
        var session = binding?.GetType().GetProperty("Key")?.GetValue(binding);
        var strikeObserver = PluginField("_npcStrikeCauses");
        var clientStrike = session is null ? null : strikeObserver?.GetType().GetMethod("CaptureClientStrike")?.Invoke(strikeObserver, [session]);
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            pluginAbsent = plugin is null,
            clientStrike,
            arrowUnionEvaluations = PluginField("_arrowCandidates") is { } arrows ?
                arrows.GetType().GetProperty("DamageUnionEvaluations")?.GetValue(arrows) : null,
            target = npc is null ? null : new { index = m5Target, npc.type, npc.generation, npc.active, npc.life, npc.lifeMax,
                x = npc.position.X, y = npc.position.Y, vx = npc.velocity.X, vy = npc.velocity.Y },
            effects = m5Effects.ToArray(), effectsDropped = m5EffectsDropped,
            packetObservations = m6PacketObservations.ToArray(), packetObservationsDropped = m6PacketObservationsDropped,
            toolTargetChests = Enumerable.Range(0, 100).Select(DescribeChest).ToArray(),
            worldItems = Main.item.Select((item, index) => new { index, item.active, item.type, item.stack,
                x = item.position.X, y = item.position.Y }).Where(x => x.active).Take(400).ToArray(),
            infrastructureFailed = PluginField("_infrastructureFailed"),
            callbackFailed = PluginField("_maintenanceCallbackFailed"),
            engineMaintenance = engine?.GetType().GetProperty("IsMaintenanceMode")!.GetValue(engine),
            engineReason = engine?.GetType().GetProperty("MaintenanceReason")!.GetValue(engine),
            sanctions = engine?.GetType().GetProperty("SanctionCount")!.GetValue(engine),
            dispatcherPending = PluginField("_dispatcher") is { } dispatcher
                ? dispatcher.GetType().GetProperty("PendingCount")!.GetValue(dispatcher) : null,
            dispatcherCapacity = PluginField("_dispatcher") is { } boundedDispatcher
                ? boundedDispatcher.GetType().GetField("_capacity", PrivateM5)!.GetValue(boundedDispatcher) : null,
            dispatcherMaxDrainMs = PluginField("_maxDispatchTicks") is long dispatchTicks ?
                dispatchTicks * 1000d / System.Diagnostics.Stopwatch.Frequency : (double?)null,
            process = new { workingSet = Environment.WorkingSet, managedBytes = GC.GetTotalMemory(false),
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
            note = "Passive target/method-entry/export observations; transport delivery is separately checked by NetworkLab."
        };
        File.WriteAllText(Path.Combine(output!, "m5-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage("QA_M5_STATE " + JsonSerializer.Serialize(new { payload.utc, payload.infrastructureFailed,
            payload.engineMaintenance, payload.callbackFailed, target = m5Target }));
    }
}
