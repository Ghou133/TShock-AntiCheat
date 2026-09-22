using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M5ProjectileDestroyTests
{
    private const int Slot = 7;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private AntiCheatPlugin plugin = null!;
    private AntiCheatEngine engine = null!;
    private M2BusinessAdapter business = null!;
    private Store store = null!;
    private Actor actor = null!;
    private SessionKey session;
    private Player oldPlayer = null!;
    private TSPlayer? oldActor;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private RemoteClient oldClient = null!;
    private int oldMode, oldMyPlayer, oldWidth;
    private bool oldDedServ;
    private Vector4 oldBounds;
    private readonly List<(int Packet, int Key, float X, float Y)> sent = [];

    [SetUp]
    public async Task SetUp()
    {
        oldPlayer = Main.player[Slot]; oldActor = ServerTShock.Players[Slot];
        oldProjectiles = Main.projectile; oldMap = Projectile.keyToIndex; oldClient = Netplay.Clients[Slot];
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldWidth = Main.maxTilesX; oldDedServ = Main.dedServ;
        oldBounds = new(Main.leftWorld, Main.rightWorld, Main.topWorld, Main.bottomWorld);
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 4200; Main.dedServ = true;
        Main.leftWorld = 0; Main.rightWorld = 10000; Main.topWorld = 0; Main.bottomWorld = 10000;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001];
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        actor = new Actor(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Group = new Group("native-cleanup-test"), Account = new UserAccount { ID = 2707, Name = "native-cleanup-test" } };
        ServerTShock.Players[Slot] = actor;
        store = new Store();
        engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 2707), Is.EqualTo(AuthenticationResult.Authenticated));
        business = new M2BusinessAdapter(TargetRuntime.Fingerprint, Path.Combine(TestContext.CurrentContext.WorkDirectory, "no-optional-cleanup-data"));
        business.Update(CurrentSession, session.WorldEpoch);
        plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, Private)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab); Set("_business", business);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "native-cleanup-fixture"));
        BindCurrentSession();
        var handler = typeof(AntiCheatPlugin).GetMethod("OnGetData", Private)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        ServerApi.Hooks.NetGetData.Register(plugin, handler, 1000);
        HookEvents.Terraria.NetMessage.SendData += CaptureSend;
        sent.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= CaptureSend;
        plugin?.Dispose(); if (plugin is not null) await plugin.ShutdownCompletion;
        Main.player[Slot] = oldPlayer; ServerTShock.Players[Slot] = oldActor;
        Main.projectile = oldProjectiles; Projectile.keyToIndex = oldMap; Netplay.Clients[Slot] = oldClient;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.maxTilesX = oldWidth; Main.dedServ = oldDedServ;
        Main.leftWorld = oldBounds.X; Main.rightWorld = oldBounds.Y; Main.topWorld = oldBounds.Z; Main.bottomWorld = oldBounds.W;
    }

    private SessionSnapshot? CurrentSession(int slot) => slot == Slot ? engine.GetSession(session) : null;
    private void BindCurrentSession()
    {
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        var binding = Activator.CreateInstance(bindingType, session, actor)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!).SetValue(binding, Slot);
    }
    private void CaptureSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        sent.Add((args.msgType, args.number, args.number2, args.number3));
        args.ContinueExecution = false; // Capture a real native call, without socket traffic.
    }
    private Projectile Install(ProjectileKey key, int owner, Vector2 position)
    {
        var entity = new Projectile { whoAmI = 0, key = key, owner = owner, type = ProjectileID.WoodenArrowFriendly,
            active = true, position = position, width = 10, height = 10, aiStyle = 1 };
        Main.projectile[0] = entity; Projectile.keyToIndex[key.Spawner, key.Index] = 0;
        return entity;
    }
    private byte[] NativeCleanup(ProjectileKey key, int owner, bool nanPosition = false)
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        var entity = Install(key, owner, new(nanPosition ? float.NaN : -100, 300));
        sent.Clear();
        entity.Update(0); // Target's real client-side out-of-bounds cleanup path.
        Assert.That(entity.active, Is.False);
        var packet = sent.Single(x => x.Packet == 29);
        Assert.That(packet.Key, Is.EqualTo((int)key));
        Assert.That(float.IsNaN(packet.X) && float.IsNaN(packet.Y), Is.True);
        Main.netMode = 2; Main.myPlayer = 255; sent.Clear();
        return DestroyBody(key, packet.X, packet.Y);
    }
    private static byte[] DestroyBody(ProjectileKey key, float x, float y)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, key.bits);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4), x);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), y);
        return body;
    }
    private void NativeReceive(byte packet, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = packet; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out int messageType);
        Assert.That(messageType, Is.EqualTo(packet));
    }
    private GetDataEventArgs RootReceive(byte packet, byte[] body, bool alreadyHandled = false)
    {
        var args = M2ContractsTests.Packet((PacketTypes)packet, body, Slot); args.Handled = alreadyHandled;
        ServerApi.Hooks.NetGetData.Invoke(args);
        return args;
    }
    private Projectile NativeCreateOwned(ProjectileKey key)
    {
        byte[] create = CreateBody(key);
        Assert.That(RootReceive(27, create).Handled, Is.False);
        NativeReceive(27, create);
        business.Update(CurrentSession, session.WorldEpoch);
        Assert.That(Projectile.TryLookup(key, out var entity) && entity.active, Is.True);
        return entity;
    }
    private static byte[] CreateBody(ProjectileKey key)
    {
        byte[] create = new byte[23]; BinaryPrimitives.WriteUInt32LittleEndian(create, key.bits);
        BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(4), 300);
        BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(8), 300);
        BinaryPrimitives.WriteInt16LittleEndian(create.AsSpan(20), ProjectileID.WoodenArrowFriendly);
        return create;
    }
    private BusinessRuleResult AuthorityResult(byte[] body) => business.Evaluate(new(M2PacketKind.ProjectileDestroy, body),
        session, actor, slot => (CurrentSession(slot), slot == Slot ? actor : null), false)
        .Single(x => x.RuleId == ProjectileRules.AuthorityRuleId);

    [TestCase(255, false)]
    [TestCase(255, true)]
    [TestCase(8, false)]
    [TestCase(8, true)]
    public async Task ActualNativeRemoteCleanupIsIgnoredByCoreAndBlockedWithoutAnyBan(int owner, bool nanPosition)
    {
        var key = new ProjectileKey(owner, 3, 43);
        var body = NativeCleanup(key, owner, nanPosition);
        var serverEntity = Install(key, owner, new(300, 300));
        NativeReceive(29, body);
        Assert.That(serverEntity.active, Is.True, "Native case29 rejects a foreign actual owner before any write.");
        Assert.That(serverEntity.position, Is.EqualTo(new Vector2(300, 300)));
        Assert.That(sent, Is.Empty, "Native rejection has no kill or relay side effect.");
        var result = business.Evaluate(new(M2PacketKind.ProjectileDestroy, body), session, actor,
            slot => (CurrentSession(slot), slot == Slot ? actor : null), false).Single();
        Assert.That(result.Reason, Is.EqualTo("foreign-owner-projectile-destroy-native-noop"));
        Assert.That(result.Version, Is.EqualTo("1.2.0"));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        var args = RootReceive(29, body);
        Assert.Multiple(() =>
        {
            Assert.That(args.Handled, Is.True, "Plugin retains core-equivalent rejection.");
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            Assert.That(actor.Disconnects, Is.Zero);
            Assert.That(serverEntity.active, Is.True);
            Assert.That(sent, Is.Empty);
        });
        await engine.PumpAsync(); Assert.That(store.Bans, Is.Zero);
        TestContext.Out.WriteLine($"Actual native Update emitted29 NaN/NaN for owner{owner}; actual native GetData29 ignored it; root blocked with zero sanctions/bans.");
    }

    [TestCase(false)] [TestCase(true)]
    public async Task NativeServerKillOfRemoteArrowNeedsLater29RelayToCleanAnIndependentPeer(bool missingKey)
    {
        var key = new ProjectileKey(Slot, 3, 2);
        var serverEntity = NativeCreateOwned(key);
        sent.Clear();
        serverEntity.Kill();
        Assert.That(serverEntity.active, Is.False);
        Assert.That(sent.All(x => x.Packet != 29), Is.True,
            "Actual server Kill does not send29 for owner7 when Main.myPlayer is255.");
        Projectile? replacement = null;
        if (missingKey)
        {
            replacement = new Projectile { whoAmI = serverEntity.whoAmI, active = true,
                key = new ProjectileKey(Slot, 3, 3), owner = Slot, type = ProjectileID.WoodenArrowFriendly,
                position = new(500, 500) };
            Main.projectile[serverEntity.whoAmI] = replacement;
            Assert.That(M2ProjectileLookup.TryGet(key, out _, out bool complete), Is.False);
            Assert.That(complete, Is.True, "A newer key in the pooled slot must not authorize writes to that object.");
        }
        var body = DestroyBody(key, 315, 320);
        var result = AuthorityResult(body);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(result.Reason, Is.EqualTo(missingKey ? "native-cleanup-key-missing" : "native-cleanup-server-inactive"));
        int nativeKills = 0;
        void Kill(Projectile _, HookEvents.Terraria.Projectile.KillEventArgs __) => nativeKills++;
        HookEvents.Terraria.Projectile.Kill += Kill;
        try
        {
            sent.Clear();
            Assert.That(RootReceive(29, body).Handled, Is.False);
            NativeReceive(29, body);
            Assert.That(nativeKills, Is.Zero, "The server must not repeat Kill effects for an inactive or absent key.");
            Assert.That(sent.Count(x => x.Packet == 29), Is.EqualTo(1));
            var relay = sent.Single(x => x.Packet == 29);
            Assert.That(relay.Key, Is.EqualTo((int)key));
            Assert.That(relay.X, Is.EqualTo(315)); Assert.That(relay.Y, Is.EqualTo(320));
            if (replacement is not null)
            {
                Assert.That(replacement.active, Is.True);
                Assert.That(replacement.position, Is.EqualTo(new Vector2(500, 500)));
            }
            var serverProjectiles = Main.projectile; var serverMap = Projectile.keyToIndex;
            try
            {
                // The independent peer still holds the arrow. No inference is made from
                // the server's inactive state to that peer's local simulation state.
                Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
                Projectile.keyToIndex = new int[256, 1001];
                var peerEntity = Install(key, Slot, new(310, 310));
                Main.netMode = 1; Main.myPlayer = Slot + 1;
                NativeReceive(29, DestroyBody((ProjectileKey)relay.Key, relay.X, relay.Y));
                Assert.That(nativeKills, Is.EqualTo(1));
                Assert.That(peerEntity.active, Is.False);
                Assert.That(peerEntity.position, Is.EqualTo(new Vector2(315, 320)));
            }
            finally
            {
                Main.projectile = serverProjectiles; Projectile.keyToIndex = serverMap;
                Main.netMode = 2; Main.myPlayer = 255;
            }
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            await engine.PumpAsync(); Assert.That(store.Bans, Is.Zero);
            TestContext.Out.WriteLine($"Actual server Kill emits zero29 for remote-owned type1; {result.Reason} leaves native server relay intact; its same-key/position29 causes exactly one native Kill on an independent peer, with zero server repeat Kill and zero bans. Native method execution, not TCP or GUI.");
        }
        finally { HookEvents.Terraria.Projectile.Kill -= Kill; }
    }

    [TestCase(false)] [TestCase(true)]
    public void KnownOldSessionStillBlocksInactiveOrMissingCleanupAfterSlotReuse(bool missingKey)
    {
        var key = new ProjectileKey(Slot, 3, 2);
        var entity = NativeCreateOwned(key); entity.Kill();
        if (missingKey) Main.projectile[entity.whoAmI] = new Projectile();
        engine.Disconnect(session); session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 2707), Is.EqualTo(AuthenticationResult.Authenticated));
        BindCurrentSession(); sent.Clear();
        var body = DestroyBody(key, 315, 320);
        Assert.That(AuthorityResult(body).Reason, Is.EqualTo("projectile-belongs-to-previous-session"));
        Assert.That(RootReceive(29, body).Handled, Is.True);
        Assert.That(sent, Is.Empty); Assert.That(engine.CanWrite(session), Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase(false)] [TestCase(true)]
    public void MissingOwnershipCacheNeverTurnsNativeOwnCleanupIntoAClaimOfCompleteOwnership(bool expireExisting)
    {
        var key = new ProjectileKey(Slot, 3, 2);
        var entity = expireExisting ? NativeCreateOwned(key) : Install(key, Slot, new(300, 300));
        entity.Kill();
        if (expireExisting) for (int i = 0; i <= 1800; i++) business.Update(CurrentSession, session.WorldEpoch);
        sent.Clear();
        var body = DestroyBody(key, 315, 320);
        var result = AuthorityResult(body);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(result.PrerequisitesComplete, Is.False);
        Assert.That(RootReceive(29, body).Handled, Is.False);
        NativeReceive(29, body);
        Assert.That(sent.Count(x => x.Packet == 29), Is.EqualTo(1));
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void ExactInactiveKey27RemainsAdmittedWithoutInheritingItsPreviousSession()
    {
        var key = new ProjectileKey(Slot, 3, 1);
        NativeCreateOwned(key).Kill();
        engine.Disconnect(session); session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 2707), Is.EqualTo(AuthenticationResult.Authenticated));
        BindCurrentSession();
        Assert.That(RootReceive(27, CreateBody(key)).Handled, Is.False);
        NativeReceive(27, CreateBody(key));
        business.Update(CurrentSession, session.WorldEpoch);
        Assert.That(Projectile.TryLookup(key, out var next), Is.True);
        Assert.That(next.active, Is.False,
            "The native receiver does not reactivate an exact inactive key of unchanged type; admission is not a new lifecycle witness.");
        Assert.That(AuthorityResult(DestroyBody(key, 315, 320)).Reason, Is.EqualTo("native-cleanup-server-inactive"));
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void Native14BitGenerationWrapDoesNotInheritTheOlderFullKeySession()
    {
        NativeCreateOwned(new ProjectileKey(Slot, 3, 1)).Kill();
        engine.Disconnect(session); session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 2707), Is.EqualTo(AuthenticationResult.Authenticated));
        BindCurrentSession();
        NativeCreateOwned(new ProjectileKey(Slot, 3, 16383)).Kill();
        NativeCreateOwned(new ProjectileKey(Slot, 3, 16384)).Kill();
        var wrapped = new ProjectileKey(Slot, 3, 16385);
        var next = NativeCreateOwned(wrapped);
        Assert.That(wrapped.Generation, Is.EqualTo(1)); Assert.That(next.active, Is.True);
        Assert.That(AuthorityResult(DestroyBody(wrapped, 315, 320)).Reason, Is.EqualTo("current-session-owns-projectile"));
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase(false)] [TestCase(true)]
    public void StaleCleanupAllowancePreservesEarlierCancellationAndRevokedSessionWrites(bool revoked)
    {
        var key = new ProjectileKey(Slot, 3, 2); NativeCreateOwned(key).Kill();
        if (revoked)
        {
            byte[] create = new byte[23];
            BinaryPrimitives.WriteUInt32LittleEndian(create, new ProjectileKey(Slot + 1, 5, 1).bits);
            BinaryPrimitives.WriteInt16LittleEndian(create.AsSpan(20), ProjectileID.WoodenArrowFriendly);
            Assert.That(RootReceive(27, create).Handled, Is.True);
            Assert.That(engine.CanWrite(session), Is.False);
        }
        sent.Clear();
        Assert.That(RootReceive(29, DestroyBody(key, 315, 320), alreadyHandled: !revoked).Handled, Is.True);
        Assert.That(sent, Is.Empty);
        Assert.That(engine.SanctionCount, Is.EqualTo(revoked ? 1 : 0));
    }

    [Test]
    public async Task OwnedNativeNanCleanupPassesWithoutWritingNanOrInvokingKill()
    {
        var key = new ProjectileKey(Slot, 3, 2);
        var body = NativeCleanup(key, Slot);
        Main.projectile[0] = new Projectile(); // Independent server state has not received this creation yet.
        byte[] create = new byte[23]; BinaryPrimitives.WriteUInt32LittleEndian(create, key.bits);
        BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(4), 300); BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(8), 300);
        BinaryPrimitives.WriteInt16LittleEndian(create.AsSpan(20), ProjectileID.WoodenArrowFriendly);
        Assert.That(RootReceive(27, create).Handled, Is.False); NativeReceive(27, create);
        business.Update(CurrentSession, session.WorldEpoch);
        Assert.That(Projectile.TryLookup(key, out var entity), Is.True);
        Assert.That(entity.active, Is.True);
        sent.Clear();
        int kills = 0;
        void Kill(Projectile projectile, HookEvents.Terraria.Projectile.KillEventArgs args) { kills++; }
        HookEvents.Terraria.Projectile.Kill += Kill;
        try
        {
            var args = RootReceive(29, body);
            Assert.That(args.Handled, Is.False, "NaN/NaN is a versioned native cleanup sentinel.");
            NativeReceive(29, body);
            Assert.That(entity.active, Is.False);
            Assert.That(entity.position, Is.EqualTo(new Vector2(300, 300)));
            Assert.That(kills, Is.Zero, "Native sentinel skips Kill and its gameplay effects.");
            Assert.That(sent.Single().Packet, Is.EqualTo(29));
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            await engine.PumpAsync(); Assert.That(store.Bans, Is.Zero);
        }
        finally { HookEvents.Terraria.Projectile.Kill -= Kill; }
    }

    [TestCase(float.PositiveInfinity, float.NaN)]
    [TestCase(float.NaN, 300)]
    [TestCase(float.NegativeInfinity, float.NegativeInfinity)]
    public void OtherNonfiniteDestroyShapesKeepIndependentNumericSafety(float x, float y)
    {
        var key = new ProjectileKey(Slot, 3, 1); Install(key, Slot, new(300, 300));
        var args = RootReceive(29, DestroyBody(key, x, y));
        Assert.That(args.Handled, Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        Assert.That(Main.projectile[0].active, Is.True);
    }

    [Test]
    public void EarlierCoreCancellationIsNeverRestoredByNativeSentinelAllowance()
    {
        var key = new ProjectileKey(Slot, 3, 1); Install(key, Slot, new(300, 300));
        Assert.That(RootReceive(29, DestroyBody(key, float.NaN, float.NaN), true).Handled, Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public async Task ForeignFreshCreationStillRevokesOnItsFirstCompleteProofUnderNewVersion()
    {
        byte[] create = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(create, new ProjectileKey(Slot + 1, 5, 1).bits);
        BinaryPrimitives.WriteInt16LittleEndian(create.AsSpan(20), ProjectileID.WoodenArrowFriendly);
        Assert.That(RootReceive(27, create).Handled, Is.True);
        Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.SanctionCount, Is.EqualTo(1));
        await engine.PumpAsync();
        Assert.That(store.Bans, Is.EqualTo(1)); Assert.That(store.Intent!.Evidence.RuleVersion, Is.EqualTo("1.2.0"));
    }

    private sealed class Actor(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public override void Disconnect(string reason) => Disconnects++;
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
}
