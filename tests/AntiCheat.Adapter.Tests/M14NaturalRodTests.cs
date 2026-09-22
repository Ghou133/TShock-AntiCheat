using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M14NaturalRodTests
{
    private const int Slot = 14;
    private int oldMode, oldLocal, oldWidth, oldHeight, oldMouseX, oldMouseY;
    private Vector2 oldScreen;
    private Player oldPlayer = null!;
    private TSPlayer actor = null!;
    private M5ProgressionContexts contexts = null!;
    private SessionKey session;
    private readonly List<(int Id, int Mode, float Slot, float X, float Y, int Style)> sends = [];

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY;
        oldMouseX = Main.mouseX; oldMouseY = Main.mouseY; oldScreen = Main.screenPosition; oldPlayer = Main.player[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = Main.maxTilesY = 500;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, position = new(320, 320) };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m14-rod-default"), Account = new UserAccount { ID = 141, Name = "m14-rod" } };
        session = new(Guid.NewGuid(), 5, Slot, 1); contexts = new(TargetRuntime.Fingerprint); contexts.Install();
        contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true;
        sends.Clear(); HookEvents.Terraria.NetMessage.SendData += Sink;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, actor.Account.ID, false, DateTimeOffset.UtcNow), actor) : (null, null);
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add((args.msgType, args.number, args.number2, args.number3, args.number4, args.number5)); args.ContinueExecution = false; }

    [TearDown]
    public void Cleanup()
    {
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
        Main.mouseX = oldMouseX; Main.mouseY = oldMouseY; Main.screenPosition = oldScreen; Main.player[Slot] = oldPlayer;
    }

    private static byte[] Body(float x, float y, byte flags = 0, byte style = 1, short sender = Slot)
    {
        byte[] body = new byte[(flags & 8) != 0 ? 16 : 12]; body[0] = flags;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), sender);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(3), x);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(7), y); body[11] = style; return body;
    }
    private BusinessRuleResult Evaluate(float x = 50, float y = 320, byte flags = 0, byte style = 1, short sender = Slot) =>
        contexts.EvaluateNaturalTeleport(M2ContractsTests.Packet((PacketTypes)65, Body(x, y, flags, style, sender), Slot), session, actor, true)!;
    private void Reconnect()
    {
        actor.ReceivedInfo = false; session = session with { Generation = session.Generation + 1 };
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true; contexts.Tick(session.WorldEpoch, Lookup);
    }

    [TestCase(51, 51)] [TestCase(7949, 7949)] [TestCase(400, 320)]
    public void InteriorRemainsPassRegardlessOfInventoryOrProgression(int x, int y)
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.ClockworkAssaultRifle);
        actor.TPlayer.inventory[8].SetDefaults(1326);
        actor.TPlayer.itemAnimation = 30;
        var result = Evaluate(x, y);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(result.Facts["acquisitionHistoryClaimed"], Is.EqualTo("False"));
        Assert.That(result.Facts["tileViewClaimed"], Is.EqualTo("False"));
        Assert.That(contexts.EvaluateNaturalTeleport(M2ContractsTests.Packet(PacketTypes.PlayerSlot, new byte[8], Slot), session, actor, true), Is.Null);
    }

    [TestCase(0, 320)] [TestCase(49, 320)] [TestCase(50, 320)] [TestCase(7950, 320)]
    [TestCase(400, 0)] [TestCase(400, 50)] [TestCase(400, 7950)] [TestCase(8000, 8000)]
    public void FiniteBorderBandIsNewProofBeyondM9ParameterSafety(int x, int y)
    {
        var parsed = M9PlayerTeleportGuard.ReadPayload(65, Body(x, y));
        Assert.That(M9PlayerTeleportGuard.Evaluate(parsed.Packet!, session, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Pass));
        var result = Evaluate(x, y);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(result.Facts["geometryHistoryComplete"], Is.EqualTo("True"));
    }

    [TestCase(1326, 50, false)] [TestCase(1326, 51, false)] [TestCase(5335, 50, false)] [TestCase(5335, 51, false)]
    [TestCase(1326, 50, true)] [TestCase(1326, 51, true)] [TestCase(5335, 50, true)] [TestCase(5335, 51, true)]
    public void NativeRodProducerNormalizesWorldEdgePointerAndRechecksFinalBorderForBothItems(int itemType, int x, bool noLimits)
    {
        Main.netMode = 1; Main.myPlayer = Slot; Main.screenPosition = Vector2.Zero;
        actor.TPlayer.itemAnimation = 30; actor.TPlayer.itemTime = 0; actor.TPlayer.gravDir = 1;
        var item = new Item(); item.SetDefaults(itemType);
        Main.mouseX = x + actor.TPlayer.width / 2; Main.mouseY = 320 + actor.TPlayer.height;
        bool oldNoLimits = Terraria.Testing.DebugOptions.noLimits;
        Terraria.Testing.DebugOptions.noLimits = noLimits;
        var destination = new Vector2(x, 320); actor.TPlayer.LimitPointToPlayerReachableArea(ref destination);
        bool permitted = destination.X > 50 && destination.X < 7950 && destination.Y > 50 && destination.Y < 7950;
        var tiles = new List<(int X, int Y, ITile Tile)>();
        int tileX = (int)(destination.X / 16), tileY = (int)(destination.Y / 16);
        for (int i = tileX - 1; i <= tileX + 3; i++)
            for (int j = tileY - 1; j <= tileY + 4; j++)
            { tiles.Add((i, j, Main.tile[i, j])); Main.tile[i, j] = new Tile(); }
        var use = typeof(Player).GetMethod("ItemCheck_UseTeleportRod", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        try { use.Invoke(actor.TPlayer, [item]); }
        finally { foreach (var tile in tiles) Main.tile[tile.X, tile.Y] = tile.Tile; Terraria.Testing.DebugOptions.noLimits = oldNoLimits; }
        TestContext.Out.WriteLine($"Native rod {itemType}: noLimits={noLimits}, pointer-derived X={x}, final destination={destination}, emitted={sends.Count(s => s.Id == 65)}.");
        var requests = sends.Where(s => s.Id == 65).ToArray();
        Assert.That(requests.Length, Is.EqualTo(permitted ? 1 : 0));
        Main.netMode = 2; Main.myPlayer = 255;
        if (permitted)
        {
            var request = requests.Single(); Assert.That(request.Mode, Is.Zero); Assert.That(request.Style, Is.EqualTo(1));
            Assert.That(request.X, Is.EqualTo(destination.X)); Assert.That(request.Y, Is.EqualTo(destination.Y));
            Assert.That(Evaluate(request.X, request.Y).Verdict, Is.EqualTo(Verdict.Pass));
        }
        else Assert.That(Evaluate(x, 320).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [TestCase(1, 1)] [TestCase(2, 1)] [TestCase(3, 1)] [TestCase(4, 1)] [TestCase(8, 1)] [TestCase(16, 1)]
    [TestCase(0, 0)] [TestCase(0, 2)] [TestCase(0, 8)] [TestCase(0, 10)] [TestCase(0, 11)]
    public void OtherModesStylesExtensionsAndServerAcksRemainOutsideContract(byte flags, byte style) =>
        Assert.That(Evaluate(0, 0, flags, style).Verdict, Is.EqualTo(Verdict.Pass));

    [Test]
    public void NonFiniteMalformedAndUnverifiedInputNeverBecomeRodProof()
    {
        Assert.That(Evaluate(float.NaN).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(float.PositiveInfinity).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(contexts.EvaluateNaturalTeleport(M2ContractsTests.Packet((PacketTypes)65, new byte[11], Slot), session, actor, true), Is.Null);
        Assert.That(contexts.EvaluateNaturalTeleport(M2ContractsTests.Packet((PacketTypes)65, Body(50, 320), Slot), session, actor, false), Is.Null);
    }

    [TestCase(true)] [TestCase(false)]
    public void ExportedLargerGeometryThenResetKeepsDelayedLegalDestinationUnknown(bool width)
    {
        if (width) Main.maxTilesX = 600; else Main.maxTilesY = 600;
        NetMessage.SendData(7, Slot); Main.maxTilesX = Main.maxTilesY = 500;
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate(7950, 7950).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(400, 320).Verdict, Is.EqualTo(Verdict.Pass));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ProgressionFlagsAndOldClientWallViewsDoNotRestrictInteriorDestinations()
    {
        bool oldHard = Main.hardMode, oldPlant = NPC.downedPlantBoss;
        var priorTile = Main.tile[25, 20];
        try
        {
            Main.hardMode = !oldHard; NPC.downedPlantBoss = !oldPlant; NetMessage.SendData(7, Slot);
            Main.tile[25, 20] = new Tile { wall = 87 };
            Assert.That(Evaluate(400, 320).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
        }
        finally { Main.hardMode = oldHard; NPC.downedPlantBoss = oldPlant; Main.tile[25, 20] = priorTile; }
    }

    [Test]
    public void IdentitySscHistoryAndThreadGapsStayUnknown()
    {
        Assert.That(Evaluate(sender: Slot + 1).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.HasSentInventory = false; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown)); actor.HasSentInventory = true;
        actor.IgnoreSSCPackets = true; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown)); actor.IgnoreSSCPackets = false;
        contexts.Tick(session.WorldEpoch, slot => slot == Slot
            ? (new SessionSnapshot(session, actor.Account.ID, true, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown)); contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Task.Run(() => Evaluate()).GetAwaiter().GetResult().Verdict, Is.EqualTo(Verdict.Unknown));
        session = session with { Generation = session.Generation + 1 }; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
        session = session with { WorldEpoch = session.WorldEpoch + 1 }; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void UnknownPluginExportAndOffThreadExportCannotBeHealedByTicks()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var unknown = new UnsupportedHost(); var container = new PluginContainer(unknown); plugins.Add(container);
        try { NetMessage.SendData(7, Slot); } finally { plugins.Remove(container); }
        contexts.Tick(session.WorldEpoch, Lookup); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
        var thread = new Thread(() => NetMessage.SendData(7, Slot)); thread.Start(); thread.Join();
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase(50, true)] [TestCase(51, false)]
    public async Task ActualRootPreservesLegalRodAndFirstBorderProofStopsBeforeNativePositionAndRelay(int x, bool violation)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var oldActor = TShockAPI.TShock.Players[Slot]; var oldClient = Netplay.Clients[Slot];
        var oldProjectiles = Main.projectile; var oldDust = Main.dust;
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
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m14-rod-native-root"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", instance)!.GetValue(plugin)!).SetValue(
            Activator.CreateInstance(bindingType, session, actor), Slot);
        var root = typeof(AntiCheatPlugin).GetMethod("OnGetData", instance)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        TShockAPI.TShock.Players[Slot] = actor; Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Main.dust = Enumerable.Range(0, 6001).Select(_ => new Dust { active = true }).ToArray();
        try
        {
            sends.Clear(); byte[] body = Body(x, 320);
            var args = M2ContractsTests.Packet((PacketTypes)65, body, Slot); root(args);
            Assert.That(args.Handled, Is.EqualTo(violation));
            Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(320, 320)));
            if (!args.Handled)
            {
                var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = 65;
                body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out _);
                Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(x, 320)), "Legal root pass must reach the native position write.");
                Assert.That(sends.Any(s => s.Id == 65 && s.X == x && s.Y == 320), Is.True);
                Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            }
            else
            {
                Assert.That(sends.Any(s => s.Id is 13 or 65), Is.False);
                Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.SanctionCount, Is.EqualTo(1));
                actor.TPlayer.inventory[9].SetDefaults(ItemID.Wood); actor.TPlayer.inventory[9].stack = 7;
                byte[] write = [Slot, 9, 0, 99, 0, 0, (byte)ItemID.Wood, 0];
                var later = M2ContractsTests.Packet(PacketTypes.PlayerSlot, write, Slot); root(later);
                Assert.That(later.Handled, Is.True); Assert.That(actor.TPlayer.inventory[9].stack, Is.EqualTo(7));
                root(M2ContractsTests.Packet((PacketTypes)65, body, Slot));
                Assert.That(engine.SanctionCount, Is.EqualTo(1));
                await engine.PumpAsync(); Assert.That(store.Bans, Is.EqualTo(1));
                Assert.That(store.Intent!.AccountId, Is.EqualTo(actor.Account.ID));
                Assert.That(store.Intent.Evidence.RuleId, Is.EqualTo(M14NaturalRodRules.RuleId));
            }
        }
        finally
        {
            plugin.Dispose(); await plugin.ShutdownCompletion;
            TShockAPI.TShock.Players[Slot] = oldActor; Netplay.Clients[Slot] = oldClient;
            Main.projectile = oldProjectiles; Main.dust = oldDust;
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
