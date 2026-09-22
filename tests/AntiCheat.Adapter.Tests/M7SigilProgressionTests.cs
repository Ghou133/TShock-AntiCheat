using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7SigilProgressionTests
{
    private const int Slot = 14;
    private int oldMode, oldLocal, oldWidth, oldCountdown;
    private bool oldHard, oldGolem, oldCultist, oldZenith, oldRemix, oldWorthy;
    private NPC[] oldNpcs = null!;
    private Player oldPlayer = null!;
    private TSPlayer actor = null!;
    private M5ProgressionContexts contexts = null!;
    private SessionKey session;
    private readonly List<(int Id, int Player, float Type)> sends = [];

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX;
        oldHard = Main.hardMode; oldGolem = NPC.downedGolemBoss; oldCultist = NPC.downedAncientCultist;
        oldZenith = Main.zenithWorld; oldRemix = Main.remixWorld; oldWorthy = Main.getGoodWorld;
        oldCountdown = NPC.MoonLordCountdown; oldNpcs = Main.npc; oldPlayer = Main.player[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 500;
        Main.hardMode = NPC.downedGolemBoss = NPC.downedAncientCultist = false;
        Main.zenithWorld = Main.remixWorld = Main.getGoodWorld = false;
        NPC.MoonLordCountdown = 0;
        Main.npc = Enumerable.Range(0, Main.maxNPCs + 1).Select(_ => new NPC()).ToArray();
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m7-sigil-default"), Account = new UserAccount { ID = 141, Name = "m7-sigil" } };
        session = new(Guid.NewGuid(), 5, Slot, 1); contexts = new(TargetRuntime.Fingerprint); contexts.Install();
        contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true;
        sends.Clear(); HookEvents.Terraria.NetMessage.SendData += Sink;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, actor.Account.ID, false, DateTimeOffset.UtcNow), actor) : (null, null);
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add((args.msgType, args.number, args.number2)); args.ContinueExecution = false; }

    [TearDown]
    public void Cleanup()
    {
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth;
        Main.hardMode = oldHard; NPC.downedGolemBoss = oldGolem; NPC.downedAncientCultist = oldCultist;
        Main.zenithWorld = oldZenith; Main.remixWorld = oldRemix; Main.getGoodWorld = oldWorthy;
        NPC.MoonLordCountdown = oldCountdown; Main.npc = oldNpcs; Main.player[Slot] = oldPlayer;
    }

    private BusinessRuleResult Evaluate(short type = -8, short sender = Slot)
    {
        byte[] body = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(body, sender);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), type);
        return contexts.Evaluate(M2ContractsTests.Packet((PacketTypes)61, body, Slot), session, actor, true)!;
    }

    private void NewConnection()
    {
        actor.ReceivedInfo = false; session = session with { Generation = session.Generation + 1 };
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true; contexts.Tick(session.WorldEpoch, Lookup);
    }

    [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
    public void RealSigilUseChecksBothFlagsAndDoesNotRequireCultistAcquisitionHistory(bool hardMode, bool golem)
    {
        Main.hardMode = hardMode; NPC.downedGolemBoss = golem; NewConnection();
        Main.netMode = 1; Main.myPlayer = Slot;
        var sigil = new Item(); sigil.SetDefaults(3601);
        actor.TPlayer.itemAnimation = 30; actor.TPlayer.itemTime = 0;
        actor.TPlayer.ItemCheck_UseEventItems(sigil);
        bool permitted = hardMode && golem;
        Assert.That(sends.Where(x => x.Id == 61).Select(x => x.Type),
            Is.EqualTo(permitted ? new[] { -8f } : Array.Empty<float>()));
        Assert.That(actor.TPlayer.itemTime > 0, Is.EqualTo(permitted));
        Assert.That(NPC.downedAncientCultist, Is.False, "An imported or teammate-given Sigil is legal once the native use gates open.");
        Assert.That(NPC.MoonLordCountdown, Is.Zero, "The client producer emits a request, not a server effect.");
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(Evaluate().Verdict, Is.EqualTo(permitted ? Verdict.Pass : Verdict.ProvenCheat));
        Assert.That(Evaluate().Facts["acquisitionHistoryClaimed"], Is.EqualTo("False"));
    }

    [Test]
    public void StoragePassiveReceiptAndSpecialWorldAreNotIllegalAcquisitionProofs()
    {
        actor.TPlayer.inventory[0].SetDefaults(3601);
        Assert.That(contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerSlot, new byte[8], Slot), session, actor, true), Is.Null);
        Main.zenithWorld = Main.remixWorld = Main.getGoodWorld = true;
        Main.hardMode = NPC.downedGolemBoss = true; NewConnection();
        Main.netMode = 1; Main.myPlayer = Slot;
        actor.TPlayer.itemAnimation = 30; actor.TPlayer.itemTime = 0;
        actor.TPlayer.ItemCheck_UseEventItems(actor.TPlayer.inventory[0]);
        Assert.That(sends.Single(x => x.Id == 61).Type, Is.EqualTo(-8f));
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void ExportedFlagsThenResetCannotReviveHardProofAndDoNotDisableMechdusa()
    {
        Main.hardMode = NPC.downedGolemBoss = true;
        NetMessage.SendData(7, Slot); // Real outgoing observer, between ticks.
        Main.hardMode = NPC.downedGolemBoss = false;
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(-16).Verdict, Is.EqualTo(Verdict.ProvenCheat), "The existing world-feature rule does not depend on Sigil progression flags.");
        NewConnection(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void LateAttachWrongSenderRevocationAndWrongThreadStayUnknown()
    {
        Assert.That(Evaluate(sender: Slot + 1).Verdict, Is.EqualTo(Verdict.Unknown));
        contexts.Tick(session.WorldEpoch, slot => slot == Slot
            ? (new SessionSnapshot(session, actor.Account.ID, true, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Task.Run(() => Evaluate()).GetAwaiter().GetResult().Verdict, Is.EqualTo(Verdict.Unknown));
        session = session with { Generation = session.Generation + 1 };
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        NewConnection(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void UnknownPluginExportHistoryCannotBeClearedByUnloadingOrTicks()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnknownProgressionPlugin(); var container = new PluginContainer(plugin);
        plugins.Add(container);
        try { NetMessage.SendData(7, Slot); } finally { plugins.Remove(container); }
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        NewConnection(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    private sealed class UnknownProgressionPlugin() : TerrariaPlugin(null!)
    { public override void Initialize() { } }

    [Test]
    public void StartupWorldExportBeforeFirstTickAndBeforeAnyConnectionDoesNotPoisonFutureNativeSession()
    {
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts = new(TargetRuntime.Fingerprint); contexts.Install(); HookEvents.Terraria.NetMessage.SendData += Sink;
        NetMessage.SendData(7); NetMessage.SendData(7); // Actual startup observer, updateThread is not assigned yet.
        actor.ReceivedInfo = false; contexts.ObserveConnection(session, actor);
        contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(Evaluate().Facts["worldExportObservationHealthy"], Is.EqualTo("True"));
        Assert.That(Evaluate().Facts["sigilExportOffThreadCount"], Is.EqualTo("2"));
    }

    [Test]
    public void WorldExportBeforeFirstTickWithAnObservedConnectionKeepsItsIntegrityFailure()
    {
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts = new(TargetRuntime.Fingerprint); contexts.Install(); HookEvents.Terraria.NetMessage.SendData += Sink;
        actor.ReceivedInfo = false; contexts.ObserveConnection(session, actor);
        NetMessage.SendData(7); // Session exists; an unverified execution context must still invalidate its proof.
        contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(-16).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    private Action RegisteredNativeIdleCallback() => (Action)((Action?)typeof(Main).GetField("OnTickForThirdPartySoftwareOnly",
        BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null))!.GetInvocationList().Single(d => ReferenceEquals(d.Target, contexts));

    [Test]
    public void ActualIdleSubscriptionBindsFirstClientExportThreadWithoutInventingTickOrSscCompletion()
    {
        bool oldConnected = Netplay.HasFullyConnectedClients;
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts = new(TargetRuntime.Fingerprint); contexts.Install(); HookEvents.Terraria.NetMessage.SendData += Sink;
        try
        {
            Netplay.HasFullyConnectedClients = false;
            RegisteredNativeIdleCallback()(); // The actual callback registered on the audited native idle event.
            actor.ReceivedInfo = false; contexts.ObserveConnection(session, actor);
            actor.ReceivedInfo = true;
            NetMessage.SendData(7, Slot); NetMessage.SendData(7, Slot);
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown), "Idle binding cannot invent the gameplay Tick's current-account lookup.");
            Assert.That(Evaluate().Facts["worldExportObservationHealthy"], Is.EqualTo("True"));
            Assert.That(Evaluate().Facts["sigilExportOffThreadCount"], Is.EqualTo("0"));
            Netplay.HasFullyConnectedClients = true; contexts.Tick(session.WorldEpoch, Lookup);
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
        }
        finally { Netplay.HasFullyConnectedClients = oldConnected; }
    }

    [Test]
    public void IdleBindingNeverClearsExistingExportFailureAndSubscriptionIsRemovedOnDispose()
    {
        bool oldConnected = Netplay.HasFullyConnectedClients;
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts = new(TargetRuntime.Fingerprint); contexts.Install(); HookEvents.Terraria.NetMessage.SendData += Sink;
        try
        {
            Netplay.HasFullyConnectedClients = false;
            actor.ReceivedInfo = false; contexts.ObserveConnection(session, actor);
            NetMessage.SendData(7, Slot); // Already observed subject, execution context still unknown.
            RegisteredNativeIdleCallback()(); contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true;
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(Evaluate(-16).Verdict, Is.EqualTo(Verdict.ProvenCheat));
            contexts.Dispose();
            var remaining = (Action?)typeof(Main).GetField("OnTickForThirdPartySoftwareOnly", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.That(remaining?.GetInvocationList().Any(d => ReferenceEquals(d.Target, contexts)) ?? false, Is.False);
        }
        finally { Netplay.HasFullyConnectedClients = oldConnected; }
    }

    [Test]
    public async Task ActualRaw61RootCancelsBeforeCoreAndNativeWorldEffectsAndRevokesFirstProof()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var oldActor = TShockAPI.TShock.Players[Slot]; var oldClient = Netplay.Clients[Slot];
        var store = new Store();
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, actor.Account.ID), Is.EqualTo(AuthenticationResult.Authenticated));
        actor.ReceivedInfo = false; contexts.ObserveConnection(session, actor);
        contexts.Tick(session.WorldEpoch, slot => slot == Slot ? (engine.GetSession(session), actor) : (null, null));
        actor.ReceivedInfo = true;
        var plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, instance)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab); Set("_naturalProgression", contexts);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m7-sigil-native-root"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", instance)!.GetValue(plugin)!).SetValue(
            Activator.CreateInstance(bindingType, session, actor), Slot);
        var root = typeof(AntiCheatPlugin).GetMethod("OnGetData", instance)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        int rootEntries = 0, coreEntries = 0;
        TShockAPI.TShock.Players[Slot] = actor; Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        try
        {
            sends.Clear(); byte[] body = new byte[4];
            BinaryPrimitives.WriteInt16LittleEndian(body, Slot); BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), -8);
            void Dispatch()
            {
                // NUnit has no TSAPI bootstrap bridge. Invoke the real root handler, then
                // follow its actual cancellation contract before any target core/native work.
                var args = M2ContractsTests.Packet((PacketTypes)61, body, Slot); rootEntries++; root(args);
                if (args.Handled) return;
                coreEntries++;
                var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = 61;
                body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, 5, out _);
            }
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Dispatch(); Assert.That(rootEntries, Is.EqualTo(1)); Assert.That(coreEntries, Is.Zero);
            Assert.That(NPC.MoonLordCountdown, Is.Zero); Assert.That(Main.npc.All(n => !n.active), Is.True);
            Assert.That(sends.Any(x => x.Id is 7 or 23 or 61), Is.False, "No native world/NPC/summon export; disconnect output is separately allowed.");
            Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.SanctionCount, Is.EqualTo(1));
            Dispatch(); Assert.That(rootEntries, Is.EqualTo(2));
            Assert.That(coreEntries, Is.Zero); Assert.That(engine.SanctionCount, Is.EqualTo(1));
            await engine.PumpAsync(); Assert.That(store.Bans, Is.EqualTo(1));
        }
        finally
        {
            plugin.Dispose(); await plugin.ShutdownCompletion;
            TShockAPI.TShock.Players[Slot] = oldActor; Netplay.Clients[Slot] = oldClient;
        }
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public int Bans;
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Bans++; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
