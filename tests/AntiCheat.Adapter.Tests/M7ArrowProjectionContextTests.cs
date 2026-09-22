using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7ArrowProjectionContextTests
{
    private const int Slot = 7;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private Player[] oldPlayers = null!;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private RemoteClient oldClient = null!;
    private TSPlayer? oldActor;
    private int oldMode, oldLocal;
    private bool oldDedicated;
    private M5CombatContexts contexts = null!;
    private AntiCheatPlugin plugin = null!;
    private AntiCheatEngine engine = null!;
    private SessionKey session;
    private TSPlayer actor = null!;
    private Store store = null!;
    private int outputs;

    [SetUp]
    public async Task SetUp()
    {
        oldPlayers = Main.player; oldProjectiles = Main.projectile; oldMap = Projectile.keyToIndex;
        oldClient = Netplay.Clients[Slot]; oldActor = TShockAPI.TShock.Players[Slot];
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldDedicated = Main.dedServ;
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
        Main.player[Slot].active = true;
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001];
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true;
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Group = new Group("m7-projection-fixture"), Account = new UserAccount { ID = 7307, Name = "m7-projection" } };
        TShockAPI.TShock.Players[Slot] = actor;
        store = new Store();
        engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 7307), Is.EqualTo(AuthenticationResult.Authenticated));
        contexts = new(TargetRuntime.Fingerprint); contexts.Install(); Tick();
        plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, Private)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab); Set("_arrowLifecycle", contexts);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m7-projection-native"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!).SetValue(
            Activator.CreateInstance(bindingType, session, actor), Slot);
        var handler = typeof(AntiCheatPlugin).GetMethod("OnGetData", Private)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        ServerApi.Hooks.NetGetData.Register(plugin, handler, 1000);
        HookEvents.Terraria.NetMessage.SendData += Sink; outputs = 0;
    }

    [TearDown]
    public async Task TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        plugin?.Dispose(); if (plugin is not null) await plugin.ShutdownCompletion;
        contexts?.Dispose();
        Main.player = oldPlayers; Main.projectile = oldProjectiles; Projectile.keyToIndex = oldMap;
        Netplay.Clients[Slot] = oldClient; TShockAPI.TShock.Players[Slot] = oldActor;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.dedServ = oldDedicated;
    }

    private void Tick() => contexts.Tick(session.WorldEpoch, slot => slot == Slot ? (engine.GetSession(session), actor) : (null, null));
    private void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { if (args.msgType == 27) outputs++; args.ContinueExecution = false; }
    private static byte[] Body(short damage, int identity = 3, int generation = 1)
    {
        byte[] body = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Slot, identity, generation).bits);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4), 400); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), 400);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 1); body[22] = 16;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(23), damage); return body;
    }
    private GetDataEventArgs Root(byte[] body, bool cancelled = false)
    { var args = M2ContractsTests.Packet(PacketTypes.ProjectileNew, body, Slot); args.Handled = cancelled; ServerApi.Hooks.NetGetData.Invoke(args); return args; }
    private static void Receive(byte[] body)
    { var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = 27; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out _); }
    private BusinessRuleResult Observe(short damage, int identity = 3, int generation = 1) => contexts.EvaluateProjection(new(M2PacketKind.ProjectileNew, Body(damage, identity, generation)), session, actor, false)!;
    private Projectile Arrow => Main.projectile.Single(x => x.active && x.type == 1);

    [TestCase((short)9)] [TestCase((short)1005)] [TestCase((short)32767)]
    public async Task FirstDeclarationRemainsUnknownWhileActualNativeRelayBuildsTheWitness(short first)
    {
        Assert.That(Root(Body(first)).Handled, Is.False); Receive(Body(first));
        Assert.That(contexts.WitnessCount, Is.EqualTo(1)); Assert.That(Arrow.damage, Is.EqualTo(first));
        Assert.That(Observe(first).Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(engine.CanWrite(session), Is.True); await engine.PumpAsync(); Assert.That(store.Banned, Is.Empty);
    }

    [Test]
    public async Task NativeWrapCounterexamplePassesAndOldC6RemainsWithdrawn()
    {
        Assert.That(Root(Body(4)).Handled, Is.False); Receive(Body(4));
        Assert.That(Observe(16385).Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(Root(Body(16385)).Handled, Is.False); Receive(Body(16385));
        Assert.That(Arrow.damage, Is.EqualTo(16385)); Assert.That(engine.CanWrite(session), Is.True);
        await engine.PumpAsync(); Assert.That(store.Banned, Is.Empty);
        TestContext.Out.WriteLine("Native raw/relay witness4 accepts16385 using all Int32 preimages; no C6 no-wrap premise is manufactured.");
    }

    [Test]
    public async Task LegalWrapAcrossTickDoesNotDisableIndependentProtectionForTheSameSession()
    {
        Assert.That(Root(Body(4)).Handled, Is.False); Receive(Body(4));
        Assert.That(Root(Body(16385)).Handled, Is.False); Receive(Body(16385));
        Tick();
        Assert.That(Observe(16385).Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(Root(Body(9, identity: 4)).Handled, Is.False); Receive(Body(9, identity: 4));
        Assert.That(Observe(1005, identity: 4).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(Root(Body(1005, identity: 4)).Handled, Is.True);
        Assert.That(engine.CanWrite(session), Is.False);
        Assert.That(Main.projectile.Single(x => x.active && x.key.Index == 4).damage, Is.EqualTo(9));
        await engine.PumpAsync(); Assert.That(store.Banned, Is.EquivalentTo(new long[] { 7307 }));
    }

    [TestCase(false)] [TestCase(true)]
    public async Task FirstImpossibleProjectionCancelsBeforeWriteRevokesAndPersistsOnlyTheAuthenticatedAccount(bool priorCancel)
    {
        Assert.That(Root(Body(9)).Handled, Is.False); Receive(Body(9)); int initialOutputs = outputs;
        Assert.That(Root(Body(1005), priorCancel).Handled, Is.True);
        Assert.That(Arrow.damage, Is.EqualTo(9)); Assert.That(outputs, Is.EqualTo(initialOutputs));
        Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.SanctionCount, Is.EqualTo(1));
        Assert.That(Root(Body(9)).Handled, Is.True); Assert.That(Arrow.damage, Is.EqualTo(9));
        await engine.PumpAsync(); Assert.That(store.Banned, Is.EquivalentTo(new long[] { 7307 }));
        engine.Disconnect(session); session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 7307), Is.EqualTo(AuthenticationResult.AccountBlocked));
    }

    [Test]
    public void CancelledCreationAndOwnerExportNeverCreateAProof()
    {
        Assert.That(Root(Body(9), true).Handled, Is.True); Receive(Body(9));
        Assert.That(contexts.WitnessCount, Is.Zero); Assert.That(Observe(1005).Verdict, Is.EqualTo(Verdict.Unknown));
        Arrow.active = false;
        Assert.That(Root(Body(9, 4)).Handled, Is.False); Receive(Body(9, 4));
        NetMessage.SendData(27, Slot, -1, number: Arrow.whoAmI);
        Assert.That(Observe(1005, 4).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void NativeDefaultsResetSameObjectAndSameKeyWithout29InvalidatesTheWitness()
    {
        Assert.That(Root(Body(9)).Handled, Is.False); Receive(Body(9));
        var arrow = Arrow; var key = arrow.key;
        arrow.SetDefaults(1); arrow.key = key; arrow.owner = Slot; arrow.active = true; arrow.damage = 9;
        Assert.That(contexts.WitnessCount, Is.Zero);
        Assert.That(Observe(1005).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public async Task ActualReceiverWrapsGeneration16383ToZeroToOneWithoutInheritingTheOldOneWitness()
    {
        Assert.That(Main.NoPooling, Is.False, "This experiment requires the native pooled-object branch.");
        Assert.That(Root(Body(9)).Handled, Is.False); Receive(Body(9));
        var reused = Arrow; int physicalSlot = reused.whoAmI;
        var atDefaults = new List<(int Generation, int WitnessCount)>();
        void ObserveDefaults(Projectile entity, HookEvents.Terraria.Projectile.SetDefaultsEventArgs args)
        { if (ReferenceEquals(entity, reused)) atDefaults.Add((entity.key.Generation, contexts.WitnessCount)); }
        HookEvents.Terraria.Projectile.SetDefaults += ObserveDefaults;
        try
        {
            // Expiry is server state in this experiment; no29 is sent and no collector Tick sweeps stale witnesses.
            foreach (var step in new[] { (Generation: 16383, Damage: (short)1005), (Generation: 16384, Damage: (short)4), (Generation: 16385, Damage: (short)1005) })
            {
                var previousKey = reused.key;
                reused.active = false;
                var nextKey = new ProjectileKey(Slot, 3, step.Generation);
                Assert.That(nextKey.Generation, Is.EqualTo(step.Generation & 16383));
                Assert.That(M2ProjectileLookup.TryGet(nextKey, out _, out bool beforeComplete), Is.False);
                Assert.That(beforeComplete, Is.True);
                Assert.That(Observe(step.Damage, generation: step.Generation).Verdict, Is.EqualTo(Verdict.Unknown));
                Assert.That(Root(Body(step.Damage, generation: step.Generation)).Handled, Is.False);
                Receive(Body(step.Damage, generation: step.Generation));
                Assert.That(ReferenceEquals(Arrow, reused), Is.True);
                Assert.That(Arrow.whoAmI, Is.EqualTo(physicalSlot));
                Assert.That(Projectile.keyToIndex[Slot, 3], Is.EqualTo(physicalSlot));
                Assert.That(M2ProjectileLookup.TryGet(previousKey, out _, out bool oldComplete), Is.False);
                Assert.That(oldComplete, Is.True);
                Assert.That(M2ProjectileLookup.TryGet(nextKey, out var found, out bool complete), Is.True);
                Assert.That(complete, Is.True); Assert.That(ReferenceEquals(found, reused), Is.True);
                Assert.That(Observe(step.Damage, generation: step.Generation).Verdict, Is.EqualTo(Verdict.Pass));
            }
            Assert.That(atDefaults.Select(x => x.Generation), Is.EqualTo(new[] { 16383, 0, 1 }));
            Assert.That(atDefaults[^1].WitnessCount, Is.EqualTo(2), "The original generation1 witness is removed in real SetDefaults before the replacement relay.");
            Assert.That(Arrow.damage, Is.EqualTo(1005));
            Assert.That(Observe(1005).Verdict, Is.EqualTo(Verdict.Pass), "The replacement generation1 uses its own first1005, never the former9.");
            Assert.That(engine.CanWrite(session), Is.True); await engine.PumpAsync(); Assert.That(store.Banned, Is.Empty);
            TestContext.Out.WriteLine($"Actual MessageBuffer27 on physical slot{physicalSlot}, identity3: generation1/damage9 ->16383/1005 ->0/4 ->1/1005; pooled object reused, previous lookup rejected, native SetDefaults removed old generation1 witness before new relay, zero bans and no29.");
        }
        finally { HookEvents.Terraria.Projectile.SetDefaults -= ObserveDefaults; }
    }

    [Test]
    public void TypeRoundTripCannotRestoreInitialWitnessAndExpiredExportExceptionCannotBecomeAProof()
    {
        Assert.That(Root(Body(9)).Handled, Is.False); Receive(Body(9));
        var changed = Body(9); BinaryPrimitives.WriteInt16LittleEndian(changed.AsSpan(20), 2);
        contexts.Evaluate(new(M2PacketKind.ProjectileNew, changed), session, actor, false);
        Receive(changed); Receive(Body(9));
        Assert.That(contexts.WitnessCount, Is.Zero); Assert.That(Observe(1005).Verdict, Is.EqualTo(Verdict.Unknown));
        Arrow.active = false;
        Assert.That(Root(Body(9, 4)).Handled, Is.False); Receive(Body(9, 4));
        NetMessage.SendData(27, Slot, -1, number: Arrow.whoAmI);
        for (int i = 0; i <= M5CombatContexts.WitnessTtlTicks; i++) Tick();
        Arrow.active = false;
        Assert.That(Root(Body(9, 5)).Handled, Is.False); Receive(Body(9, 5));
        Assert.That(Observe(1005, 5).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(contexts.WitnessCount, Is.Zero);
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public HashSet<long> Banned { get; } = [];
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Banned.Add(intent.AccountId); return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
