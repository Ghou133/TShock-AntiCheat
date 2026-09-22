using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M13NaturalGolemTests
{
    private const int Slot = 14;
    private int oldMode, oldLocal, oldWidth, oldHeight;
    private bool oldHard, oldPlant, oldGolem, oldRight;
    private NPC[] oldNpcs = null!;
    private Player oldPlayer = null!;
    private ITile oldAltar = null!;
    private TSPlayer actor = null!;
    private M5ProgressionContexts contexts = null!;
    private SessionKey session;
    private readonly List<(int Id, int Player, float Type)> sends = [];

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY;
        oldHard = Main.hardMode; oldPlant = NPC.downedPlantBoss; oldGolem = NPC.downedGolemBoss;
        oldRight = Main.mouseRightRelease; oldNpcs = Main.npc; oldPlayer = Main.player[Slot]; oldAltar = Main.tile[20, 20];
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = Main.maxTilesY = 500;
        Main.hardMode = NPC.downedPlantBoss = NPC.downedGolemBoss = false;
        Main.npc = Enumerable.Range(0, Main.maxNPCs + 1).Select(_ => new NPC()).ToArray();
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, position = new(320, 320) };
        Main.tile[20, 20] = new Tile { type = TileID.LihzahrdAltar };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m13-golem-default"), Account = new UserAccount { ID = 141, Name = "m13-golem" } };
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
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
        Main.hardMode = oldHard; NPC.downedPlantBoss = oldPlant; NPC.downedGolemBoss = oldGolem;
        Main.mouseRightRelease = oldRight; Main.npc = oldNpcs; Main.player[Slot] = oldPlayer; Main.tile[20, 20] = oldAltar;
    }

    private BusinessRuleResult Evaluate(short type = 245, short sender = Slot)
    {
        byte[] body = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(body, sender);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), type);
        return contexts.Evaluate(M2ContractsTests.Packet((PacketTypes)61, body, Slot), session, actor, true)!;
    }

    private void Reconnect()
    {
        actor.ReceivedInfo = false; session = session with { Generation = session.Generation + 1 };
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true; contexts.Tick(session.WorldEpoch, Lookup);
    }

    [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
    public void NativeAltarProducerRechecksBothWorldGatesAndValidUsePassesEffectiveRule(bool hardMode, bool plantera)
    {
        Main.hardMode = hardMode; NPC.downedPlantBoss = plantera; Reconnect();
        Main.netMode = 1; Main.myPlayer = Slot;
        // Battery may be transferred by a teammate. The current held item and a continued
        // animation are irrelevant: the altar's emission path checks the world again.
        actor.TPlayer.inventory[0].SetDefaults(ItemID.ClockworkAssaultRifle);
        actor.TPlayer.inventory[8].SetDefaults(ItemID.LihzahrdPowerCell); actor.TPlayer.inventory[8].stack = 2;
        actor.TPlayer.itemAnimation = 30; actor.TPlayer.itemTime = 0;
        actor.TPlayer.tileInteractAttempted = actor.TPlayer.releaseUseTile = true;
        actor.TPlayer.TileInteractionsUse(20, 20);
        bool allowed = hardMode && plantera;
        Assert.That(sends.Where(x => x.Id == 61).Select(x => (x.Player, x.Type)),
            Is.EqualTo(allowed ? new[] { (Slot, 245f) } : Array.Empty<(int, float)>()));
        Assert.That(actor.TPlayer.inventory[8].stack, Is.EqualTo(allowed ? 1 : 2));
        Assert.That(Main.npc.All(n => !n.active), Is.True);
        Main.netMode = 2; Main.myPlayer = 255;
        var result = Evaluate();
        Assert.That(result.RuleId, Is.EqualTo(M13NaturalGolemRules.RuleId));
        Assert.That(result.Verdict, Is.EqualTo(allowed ? Verdict.Pass : Verdict.ProvenCheat));
        Assert.That(result.Facts["worldBaselineComplete"], Is.EqualTo("True"));
        Assert.That(result.Facts["acquisitionHistoryClaimed"], Is.EqualTo("False"));
        TestContext.Out.WriteLine($"Native TileInteractionsUse altar237/cell1293: hard={hardMode},plant={plantera}; request245={allowed}, rule={result.Verdict}. Held rifle and ongoing animation do not bypass the final world gate.");
    }

    [Test]
    public void PassiveCellReceiptAndOtherSummonsNeverBecomeThisRuleEvidence()
    {
        actor.TPlayer.inventory[8].SetDefaults(ItemID.LihzahrdPowerCell);
        Assert.That(contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerSlot, new byte[8], Slot), session, actor, true), Is.Null);
        Assert.That(Evaluate(50).RuleId, Is.Not.EqualTo(M13NaturalGolemRules.RuleId));
        Main.hardMode = NPC.downedPlantBoss = true; Reconnect();
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(NPC.downedGolemBoss, Is.False, "First legal Golem summon cannot require an earlier Golem kill.");
    }

    [TestCase(true, false)] [TestCase(false, true)]
    public void ExportedFlagThenResetKeepsDelayedLegalRequestUnknown(bool changeHard, bool changePlantera)
    {
        Main.hardMode = changeHard; NPC.downedPlantBoss = changePlantera;
        NetMessage.SendData(7, Slot);
        Main.hardMode = NPC.downedPlantBoss = false;
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate().Facts["worldBaselineComplete"], Is.EqualTo("False"));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void PlanteraAndGolemHistoriesAreIndependentAndWorldChangeCannotBorrowOldProof()
    {
        NPC.downedGolemBoss = true; NetMessage.SendData(7, Slot); NPC.downedGolemBoss = false;
        Assert.That(Evaluate(-8).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Reconnect(); NPC.downedPlantBoss = true; NetMessage.SendData(7, Slot); NPC.downedPlantBoss = false;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(-8).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        session = session with { WorldEpoch = session.WorldEpoch + 1 };
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void WrongSenderRevocationLateAttachSscAndWrongThreadStayUnknown()
    {
        Assert.That(Evaluate(sender: Slot + 1).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.HasSentInventory = false; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown)); actor.HasSentInventory = true;
        contexts.Tick(session.WorldEpoch, slot => slot == Slot
            ? (new SessionSnapshot(session, actor.Account.ID, true, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Task.Run(() => Evaluate()).GetAwaiter().GetResult().Verdict, Is.EqualTo(Verdict.Unknown));
        session = session with { Generation = session.Generation + 1 };
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void UnknownPluginWorldExportCannotRecoverByUnloadOrTicks()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnsupportedHost(); var container = new PluginContainer(plugin); plugins.Add(container);
        try { NetMessage.SendData(7, Slot); } finally { plugins.Remove(container); }
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void OffThreadWorldExportLatchesFailureBeyondReconnect()
    {
        var thread = new Thread(() => NetMessage.SendData(7, Slot)); thread.Start(); thread.Join();
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
    public async Task ActualRaw61RootBlocksFirstClosedGateBeforeEffectsAndRevokesFollowingWrite(bool hardMode, bool plantera)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var oldActor = TShockAPI.TShock.Players[Slot]; var oldClient = Netplay.Clients[Slot];
        var store = new Store();
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        Main.hardMode = hardMode; NPC.downedPlantBoss = plantera;
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, actor.Account.ID), Is.EqualTo(AuthenticationResult.Authenticated));
        actor.ReceivedInfo = false; contexts.ObserveConnection(session, actor);
        contexts.Tick(session.WorldEpoch, slot => slot == Slot ? (engine.GetSession(session), actor) : (null, null));
        actor.ReceivedInfo = true;
        var plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, instance)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab); Set("_naturalProgression", contexts);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m13-golem-native-root"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", instance)!.GetValue(plugin)!).SetValue(
            Activator.CreateInstance(bindingType, session, actor), Slot);
        var root = typeof(AntiCheatPlugin).GetMethod("OnGetData", instance)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        TShockAPI.TShock.Players[Slot] = actor; Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        try
        {
            sends.Clear(); byte[] body = new byte[4];
            BinaryPrimitives.WriteInt16LittleEndian(body, Slot); BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), 245);
            var args = M2ContractsTests.Packet((PacketTypes)61, body, Slot);
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
            root(args);
            Assert.That(args.Handled, Is.True);
            Assert.That(Main.npc.All(n => !n.active), Is.True);
            Assert.That(sends.Any(x => x.Id is 7 or 23 or 61), Is.False);
            Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.SanctionCount, Is.EqualTo(1));
            Assert.That(engine.GetSession(session)!.AccountId, Is.EqualTo(actor.Account.ID));
            actor.TPlayer.inventory[9].SetDefaults(ItemID.Wood); actor.TPlayer.inventory[9].stack = 7;
            byte[] write = [Slot, 9, 0, 99, 0, 0, (byte)ItemID.Wood, 0];
            var later = M2ContractsTests.Packet(PacketTypes.PlayerSlot, write, Slot); root(later);
            Assert.That(later.Handled, Is.True); Assert.That(actor.TPlayer.inventory[9].stack, Is.EqualTo(7));
            root(M2ContractsTests.Packet((PacketTypes)61, body, Slot));
            Assert.That(engine.SanctionCount, Is.EqualTo(1));
            await engine.PumpAsync(); Assert.That(store.Bans, Is.EqualTo(1));
            Assert.That(store.Intent!.AccountId, Is.EqualTo(actor.Account.ID));
            Assert.That(store.Intent.Evidence.RuleId, Is.EqualTo(M13NaturalGolemRules.RuleId));
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
        public BanIntent? Intent;
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) { Intent = intent; return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Bans++; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class UnsupportedHost() : TerrariaPlugin(null!) { public override void Initialize() { } }
}
