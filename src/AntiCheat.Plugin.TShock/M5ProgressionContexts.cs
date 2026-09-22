using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.GameContent;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// A fixed 256-session, per-world feature baseline. A feature change invalidates the affected session
/// for its entire lifetime; waiting for a few ticks never pretends the client applied an update.
/// </summary>
public sealed partial class M5ProgressionContexts(string fingerprint, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly record struct World(int Id, bool Zenith, bool Remix, bool Worthy);
    private sealed class State(SessionKey session, World world, bool fromStart, bool nativePlugins)
    {
        public SessionKey Session { get; } = session;
        public World World { get; } = world;
        public bool Complete { get; set; } = fromStart && nativePlugins;
        public bool PluginContractComplete { get; set; } = nativePlugins;
        public (bool HardMode, bool Golem) SigilFeatures { get; } = (Main.hardMode, NPC.downedGolemBoss);
        public bool SigilComplete { get; set; } = fromStart && nativePlugins;
        // Solar Tablet needs only hardmode history. A Golem/Plantera change must not poison it.
        public bool InitialHardMode { get; } = Main.hardMode;
        public bool SolarTabletComplete { get; set; } = fromStart && nativePlugins;
        public (bool HardMode, bool Plantera) GolemFeatures { get; } = (Main.hardMode, NPC.downedPlantBoss);
        public bool GolemComplete { get; set; } = fromStart && nativePlugins;
        public (int Width, int Height) InitialGeometry { get; } = (Main.maxTilesX, Main.maxTilesY);
        public bool GeometryComplete { get; set; } = fromStart && nativePlugins;
        public M12ItemUsePossibilities? UsePossibilities { get; set; }
    }
    private readonly State?[] states = new State?[256];
    private readonly M6RazorRecipeContexts razorRecipes = new(fingerprint);
    private int updateThread;
    private bool installed;
    private volatile bool exportObservationFailed;
    private volatile bool sigilExportObservationFailed;
    private long sigilExportOffThreadCount;
    private int sigilExportFirstThread;
    private int sigilExportTraceCount;
    private long epoch;
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? lookup;

    public void Install()
    {
        if (installed) return;
        HookEvents.Terraria.NetMessage.SendData += OnSendData;
        Main.OnTickForThirdPartySoftwareOnly += OnIdleExecutionContext;
        installed = true;
    }

    public void Dispose()
    {
        if (installed)
        {
            HookEvents.Terraria.NetMessage.SendData -= OnSendData;
            Main.OnTickForThirdPartySoftwareOnly -= OnIdleExecutionContext;
        }
        installed = false;
        lookup = null;
        ResetWorld();
    }

    private void OnIdleExecutionContext()
    {
        if (!installed || Main.netMode != 2 || Netplay.HasFullyConnectedClients) return;
        // Locked Main.DedServ raises this callback immediately before Netplay.UpdateInMainThread
        // while no client is fully connected. The first client's packet7 may therefore precede
        // every GameUpdate. Bind only its execution thread, without pretending a gameplay Tick,
        // SSC acknowledgement, world refresh or session lookup has happened.
        int thread = Environment.CurrentManagedThreadId;
        int prior = Interlocked.CompareExchange(ref updateThread, thread, 0);
        if (prior != 0 && prior != thread) sigilExportObservationFailed = true;
        // Never clear exportObservationFailed or sigilExportObservationFailed here.
    }

    private void OnSendData(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!installed || !args.ContinueExecution || Main.netMode != 2) return;
        try
        {
            ObserveItemExport(args);
            // An unknown plugin can export packet7 and unload between two updates. Its
            // client-side remix/worthy state survives even if this server's fields do not change.
            if (!NativePluginsOnly())
            {
                if (Environment.CurrentManagedThreadId != updateThread) exportObservationFailed = true;
                else foreach (var state in states)
                    if (state is not null) { state.Complete = false; state.SigilComplete = false; state.SolarTabletComplete = false; state.GolemComplete = false; state.PluginContractComplete = false; }
            }
            // A native packet7 export may occur between ticks. It is the actual place where
            // the client can learn changed hardmode/Golem flags; a later reset must not erase it.
            if (args.msgType == 7)
            {
                TraceEarlySigilExport(args);
                if (Environment.CurrentManagedThreadId != updateThread)
                {
                    // Initial world setup can export packet7 before the first update assigns
                    // updateThread. With no observed connection there is no client baseline
                    // to invalidate. Once any session exists, keep the failure latched; a later
                    // Tick, disconnect or field reset must not erase its export history.
                    if (states.Any(state => state is not null)) sigilExportObservationFailed = true;
                    Interlocked.Increment(ref sigilExportOffThreadCount);
                    Interlocked.CompareExchange(ref sigilExportFirstThread, Environment.CurrentManagedThreadId, 0);
                }
                else foreach (var state in states)
                    if (state is not null)
                    {
                        if (state.World.Id != Main.worldID || state.SigilFeatures != (Main.hardMode, NPC.downedGolemBoss))
                            state.SigilComplete = false;
                        if (state.World.Id != Main.worldID || state.InitialHardMode != Main.hardMode)
                            state.SolarTabletComplete = false;
                        if (state.World.Id != Main.worldID || state.GolemFeatures != (Main.hardMode, NPC.downedPlantBoss))
                            state.GolemComplete = false;
                        if (state.World.Id != Main.worldID || state.InitialGeometry != (Main.maxTilesX, Main.maxTilesY))
                            state.GeometryComplete = false;
                    }
            }
        }
        catch { exportObservationFailed = true; }
    }

    private void TraceEarlySigilExport(HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (Volatile.Read(ref sigilExportTraceCount) >= 8) return;
        int sequence = Interlocked.Increment(ref sigilExportTraceCount);
        if (sequence > 8) return;
        try
        {
            var observed = states.Select((state, slot) => (state, slot)).Where(x => x.state is not null)
                .Select(x => new { slot = x.slot, generation = x.state!.Session.Generation,
                    worldEpoch = x.state.Session.WorldEpoch, worldId = x.state.World.Id,
                    x.state.Complete, x.state.SigilComplete, x.state.PluginContractComplete,
                    hardMode = x.state.SigilFeatures.HardMode, golem = x.state.SigilFeatures.Golem,
                    receivedInfo = TShockAPI.TShock.Players[x.slot]?.ReceivedInfo,
                    hasSentInventory = TShockAPI.TShock.Players[x.slot]?.HasSentInventory,
                    loggedIn = TShockAPI.TShock.Players[x.slot]?.IsLoggedIn,
                    nativeClientState = Netplay.Clients[x.slot]?.State }).ToArray();
            Console.WriteLine("ANTICHEAT_SIGIL_EXPORT_TRACE " + System.Text.Json.JsonSerializer.Serialize(new
            {
                sequence, installed, thread = Environment.CurrentManagedThreadId, updateThread,
                epoch, Main.netMode, Main.worldID, Main.hardMode, NPC.downedGolemBoss,
                Netplay.HasFullyConnectedClients, args.remoteClient, args.ignoreClient,
                args.ContinueExecution, statesCount = observed.Length, states = observed,
                exportObservationFailed, sigilExportObservationFailed
            }));
        }
        catch { /* Bounded diagnostics cannot affect the independent proof/context state. */ }
    }

    public void Tick(long worldEpoch, Func<int, (SessionSnapshot? Session, TSPlayer? Player)> targets)
    {
        updateThread = Environment.CurrentManagedThreadId;
        lookup = targets;
        razorRecipes.Refresh();
        if (epoch != worldEpoch) { ResetWorld(); epoch = worldEpoch; }
        for (int slot = 0; slot < states.Length; slot++)
        {
            var target = targets(slot);
            if (target.Session is not { } session || target.Player is not { } actor) states[slot] = null;
            else ObserveConnection(session.Key, actor);
        }
    }

    /// <summary>Call from the existing ServerConnect/first raw entry, before ReceivedInfo is accepted.</summary>
    public void ObserveConnection(SessionKey session, TSPlayer actor)
    {
        if ((uint)session.Slot >= states.Length) return;
        // ServerConnect can precede the first world Tick. Preserve the actual from-start capture;
        // a later Tick must not mistake first initialization for a world replacement.
        if (epoch == 0) epoch = session.WorldEpoch;
        var world = ReadWorld();
        bool nativePlugins = NativePluginsOnly();
        if (states[session.Slot] is not { } state || state.Session != session)
            states[session.Slot] = new(session, world, !actor.ReceivedInfo && Main.netMode == 2 && Main.maxTilesX > 0, nativePlugins);
        else
        {
            if (state.World != world) state.Complete = false;
            if (state.World.Id != world.Id || state.SigilFeatures != (Main.hardMode, NPC.downedGolemBoss))
                state.SigilComplete = false;
            if (state.World.Id != world.Id || state.InitialHardMode != Main.hardMode)
                state.SolarTabletComplete = false;
            if (state.World.Id != world.Id || state.GolemFeatures != (Main.hardMode, NPC.downedPlantBoss))
                state.GolemComplete = false;
            if (state.World.Id != world.Id || state.InitialGeometry != (Main.maxTilesX, Main.maxTilesY))
                state.GeometryComplete = false;
            // An unsupported plugin may already have exported different world features to this
            // client. Unloading it cannot retroactively restore this connection's native contract.
            if (!nativePlugins) { state.Complete = false; state.SigilComplete = false; state.SolarTabletComplete = false; state.GolemComplete = false; state.PluginContractComplete = false; }
        }
    }

    public void ResetWorld() => Array.Clear(states);

    public BusinessRuleResult? Evaluate(GetDataEventArgs args, SessionKey session, TSPlayer actor, bool verifiedRuntime)
    {
        if (verifiedRuntime) ObserveUseIntent(args, session, actor);
        if (args.MsgID != PacketTypes.SpawnBossorInvasion) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (!verifiedRuntime || buffer is null || length != 4 || args.Index < 0 || args.Index > buffer.Length - length)
            return null; // The root/core parser owns malformed input safety; a parse failure is never this proof.
        return EvaluatePayload(buffer.AsSpan(args.Index, length), session, actor);
    }

    public BusinessRuleResult? EvaluatePayload(ReadOnlySpan<byte> body, SessionKey session, TSPlayer actor)
    {
        if (body.Length != 4) return null;
        int sender = BinaryPrimitives.ReadInt16LittleEndian(body);
        int type = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
        ObserveConnection(session, actor);
        var state = (uint)session.Slot < states.Length ? states[session.Slot] : null;
        var target = lookup?.Invoke(session.Slot);
        var current = target?.Session;
        bool bound = current is not null && current.Key == session && !current.Revoked &&
            ReferenceEquals(target?.Player, actor) && actor.Index == session.Slot && actor.IsLoggedIn &&
            actor.Account is not null && current.AccountId == actor.Account.ID;
        bool complete = installed && !exportObservationFailed && Main.netMode == 2 && Main.myPlayer == 255 && Main.maxTilesX > 0 &&
            updateThread == Environment.CurrentManagedThreadId && epoch == session.WorldEpoch &&
            state is { Complete: true } && state.Session == session && state.World == ReadWorld();
        bool plugins = state is { PluginContractComplete: true } && NativePluginsOnly();
        if (type == 245) return EvaluateGolem(sender, type, session, actor, state, bound, plugins);
        if (type == -6)
        {
            var sources = CaptureUsePossibilities(session, actor, recordArrival: true);
            // The shared outgoing-packet7 integrity signal applies; Sigil's Golem state does not.
            bool solarComplete = installed && !exportObservationFailed && !sigilExportObservationFailed &&
                Main.netMode == 2 && Main.myPlayer == 255 && Main.maxTilesX > 0 &&
                updateThread == Environment.CurrentManagedThreadId && epoch == session.WorldEpoch &&
                state is { SolarTabletComplete: true } && state.Session == session &&
                state.World.Id == Main.worldID && state.InitialHardMode == Main.hardMode;
            var solar = M11NaturalItemRules.EvaluateSolarTablet(sender, type,
                new(new(session, fingerprint, fingerprint, true, solarComplete, bound, solarComplete && plugins),
                    solarComplete, Main.hardMode, !actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo, plugins));
            return solar with { Facts = solar.Facts
                    .SetItem("worldId", Main.worldID.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("hardModeHistoryComplete", solarComplete.ToString())
                    .SetItem("currentAccountAndActorBound", bound.ToString())
                    .SetItem("observedPossibleUseItemIds", string.Join(',', sources.Observed.Select(x => x.ItemType).Distinct()))
                    .SetItem("possibleUseSourceSetComplete", sources.Complete.ToString())
                    .SetItem("unobservedAnimationStartStillPossible", sources.UnobservedStartPossible.ToString())
                    .SetItem("useSourceHistoryLostEntries", sources.LostEntries.ToString())
                    .SetItem("sscExportProvesClientReceipt", "False")
                    .SetItem("pluginContractComplete", plugins.ToString())
                    .SetItem("worldExportObservationHealthy", (!exportObservationFailed && !sigilExportObservationFailed).ToString()) };
        }
        if (M12NaturalMechanicalRules.IsMechanicalSummon(type))
        {
            var sources = CaptureUsePossibilities(session, actor, recordArrival: true);
            return M12NaturalMechanicalRules.Evaluate(sender, type,
                new(new(session, fingerprint, fingerprint, true, complete, bound, complete && plugins),
                    complete, SpecialSeedFeatures.Mechdusa, plugins,
                    !actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo,
                    string.Join(',', sources.Observed.Select(x => x.ItemType).Distinct()), sources.LostEntries));
        }
        if (type == -8)
        {
            bool sigilComplete = installed && !exportObservationFailed && !sigilExportObservationFailed && Main.netMode == 2 && Main.myPlayer == 255 &&
                Main.maxTilesX > 0 && updateThread == Environment.CurrentManagedThreadId && epoch == session.WorldEpoch &&
                state is { SigilComplete: true } && state.Session == session && state.World.Id == Main.worldID &&
                state.SigilFeatures == (Main.hardMode, NPC.downedGolemBoss);
            return M7ProgressionRules.EvaluateSigil(sender, type,
                new(new(session, fingerprint, fingerprint, true, sigilComplete, bound, sigilComplete && plugins),
                    sigilComplete, Main.hardMode, NPC.downedGolemBoss,
                    !actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo, plugins))
                with { Facts = M7ProgressionRules.Facts(sender, type, Main.hardMode, NPC.downedGolemBoss)
                    .SetItem("worldId", Main.worldID.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("worldEpoch", session.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("worldBaselineComplete", sigilComplete.ToString())
                    .SetItem("currentAccountAndActorBound", bound.ToString())
                    .SetItem("pluginContractComplete", plugins.ToString())
                    .SetItem("sigilStateComplete", (state is { SigilComplete: true }).ToString())
                    .SetItem("initialSynchronization", (!actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo).ToString())
                    .SetItem("sigilExportOffThreadCount", Interlocked.Read(ref sigilExportOffThreadCount).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("sigilExportFirstThread", sigilExportFirstThread.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("updateThread", updateThread.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("worldExportObservationHealthy", (!exportObservationFailed && !sigilExportObservationFailed).ToString()) };
        }
        var recipe = razorRecipes.Capture(plugins);
        var result = M5ProgressionRules.EvaluateMechdusa(sender, type,
            new(new(session, fingerprint, fingerprint, true, complete, bound, complete && plugins),
                complete, Main.zenithWorld, SpecialSeedFeatures.Mechdusa,
                !actor.HasSentInventory || actor.IgnoreSSCPackets || !actor.ReceivedInfo, plugins));
        return result with { Facts = result.Facts
            .SetItem("worldId", Main.worldID.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetItem("worldEpoch", session.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetItem("remixWorld", Main.remixWorld.ToString())
            .SetItem("getGoodWorld", Main.getGoodWorld.ToString())
            .SetItem("worldBaselineComplete", complete.ToString())
            .SetItem("currentAccountAndActorBound", bound.ToString())
            .SetItem("pluginContractComplete", plugins.ToString())
            .SetItem("worldExportObservationHealthy", (!exportObservationFailed).ToString())
            .SetItem("razorRecipeTableVerified", recipe.RecipeTableVerified.ToString())
            .SetItem("razorNativeWorldPermitsRecipe", recipe.NativeWorldPermitsRecipe.ToString())
            .SetItem("razorItemAcquisitionComplete", recipe.AcquisitionPathsComplete.ToString())
            .SetItem("sourceOP081Prohibition", recipe.SourceHardModeProhibition.ToString()) };
    }

    private static World ReadWorld() => new(Main.worldID, Main.zenithWorld, Main.remixWorld, Main.getGoodWorld);
    private static bool NativePluginsOnly() => ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(TShockAPI.TShock) ||
        x.Plugin.GetType() == typeof(AntiCheatPlugin));
}
