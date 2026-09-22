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
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed partial class M6NpcStrikeTests
{
    private const int Slot = 7, NpcSlot = 11;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private NPC[] oldNpcs = null!;
    private Player oldPlayer = null!;
    private TSPlayer? oldActor;
    private CombatText[] oldText = null!;
    private RemoteClient oldClient = null!;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private AntiCheatPlugin plugin = null!;
    private AntiCheatEngine engine = null!;
    private SessionKey session;
    private Store store = null!;
    private readonly List<(int Id, int Target, float Damage, float Knockback, float Direction, int Critical)> sent = [];

    [SetUp]
    public async Task SetUp()
    {
        oldNpcs = Main.npc; oldPlayer = Main.player[Slot]; oldActor = ServerTShock.Players[Slot];
        oldText = Main.combatText; oldClient = Netplay.Clients[Slot];
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ;
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true;
        Main.npc = Enumerable.Range(0, Main.maxNPCs).Select(i => new NPC { whoAmI = i }).ToArray();
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.combatText = Enumerable.Range(0, 100).Select(_ => new CombatText { active = true }).ToArray();
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        var actor = new M18RootCapturingPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Group = new Group("m6-strike-fixture"), Account = new UserAccount { ID = 6207, Name = "m6-strike-fixture" } };
        ServerTShock.Players[Slot] = actor;
        store = new Store();
        engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 6207), Is.EqualTo(AuthenticationResult.Authenticated));
        plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, Private)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m6-strike-native-fixture"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!).SetValue(
            Activator.CreateInstance(bindingType, session, actor), Slot);
        var handler = typeof(AntiCheatPlugin).GetMethod("OnGetData", Private)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        ServerApi.Hooks.NetGetData.Register(plugin, handler, 1000);
        HookEvents.Terraria.NetMessage.SendData += Capture;
        Target.SetDefaults(NPCID.BlueSlime); Target.active = true; Target.generation = 3;
        Target.life = Target.lifeMax = 5000; Target.defense = 0; Target.takenDamageMultiplier = 1;
        Target.knockBackResist = 1; Target.position = new(320, 320); Target.velocity = Vector2.Zero;
        sent.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture;
        plugin?.Dispose(); if (plugin is not null) await plugin.ShutdownCompletion;
        Main.npc = oldNpcs; Main.player[Slot] = oldPlayer; ServerTShock.Players[Slot] = oldActor;
        Main.combatText = oldText; Netplay.Clients[Slot] = oldClient;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
    }

    private NPC Target => Main.npc[NpcSlot];
    private void Capture(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        sent.Add((args.msgType, args.number, args.number2, args.number3, args.number4, args.number5));
        args.ContinueExecution = false;
    }
    private static byte[] Body(short damage = 9, float knockback = 0, byte direction = 2, byte critical = 0,
        byte generation = 3, byte target = NpcSlot)
    {
        byte[] body = new byte[10]; body[0] = target; body[1] = generation;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), damage);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4), knockback); body[8] = direction; body[9] = critical;
        return body;
    }
    private GetDataEventArgs Root(byte[] body, bool cancelled = false)
    {
        var args = M2ContractsTests.Packet((PacketTypes)28, body, Slot); args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args); return args;
    }
    private void Receive(byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = 28; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out int type); Assert.That(type, Is.EqualTo(28));
    }

    [TestCase((short)9, false, 9)]
    [TestCase((short)1000, false, 1000)]
    [TestCase((short)1005, true, 2010)]
    [TestCase((short)-1, false, 1)]
    public async Task ActualOrdinaryStrikeRemainsUnblockedWithExactLifeAndOutgoingEvidence(short damage, bool critical, int loss)
    {
        var body = Body(damage, critical: critical ? (byte)1 : (byte)0);
        var parsed = M6NpcStrikeReader.ReadPayload(body).Packet!;
        Assert.That(M6NpcStrikeReader.Evaluate(parsed, session, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Root(body).Handled, Is.False); Receive(body);
        Assert.That(Target.life, Is.EqualTo(5000 - loss)); Assert.That(Target.active, Is.True);
        Assert.That(sent.Count(x => x.Id == 28), Is.EqualTo(1));
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        await engine.PumpAsync(); Assert.That(store.Bans, Is.Zero);
        TestContext.Out.WriteLine($"Target native packet28 damage={damage},crit={critical}: life5000->{Target.life}, output28 present; no exclusive damage proof, zero sanctions.");
    }

    [Test]
    public void NativeUnboundedDirectionChangesVelocityButRootRejectsBeforeAllStrikeEffects()
    {
        var body = Body(knockback: 4, direction: 255);
        Receive(body); // Unprotected target-method control, no public socket or production world.
        Assert.That(Target.life, Is.EqualTo(4991)); Assert.That(Target.velocity.X, Is.EqualTo(1016));
        Assert.That(sent.Single(x => x.Id == 28).Direction, Is.EqualTo(254));
        Target.life = 5000; Target.velocity = Vector2.Zero; Target.playerInteraction[Slot] = false; sent.Clear();
        Assert.That(Root(body).Handled, Is.True);
        Assert.That(Target.life, Is.EqualTo(5000)); Assert.That(Target.velocity, Is.EqualTo(Vector2.Zero));
        Assert.That(Target.playerInteraction[Slot], Is.False); Assert.That(sent, Is.Empty);
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
    public void NumericRejectionDoesNotBanAndPreservesAlreadyCancelledState(float knockback)
    {
        var body = Body(knockback: knockback);
        Assert.That(Root(body).Handled, Is.True); Assert.That(Root(Body(), true).Handled, Is.True);
        Assert.That(Target.life, Is.EqualTo(5000)); Assert.That(sent, Is.Empty);
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void StaleGenerationIsRealNativeNoopWithAckButNoDamageRelay()
    {
        var body = Body(1000, float.NaN, 255, 255, generation: 2);
        Assert.That(M6NpcStrikeReader.Evaluate(M6NpcStrikeReader.ReadPayload(body).Packet!, session,
            TargetRuntime.Fingerprint).Reason, Is.EqualTo("npc-strike-old-generation-native-noop"));
        Assert.That(Root(body).Handled, Is.False); Receive(body);
        Assert.That(Target.life, Is.EqualTo(5000)); Assert.That(Target.playerInteraction[Slot], Is.False);
        Assert.That(sent.Select(x => x.Id), Is.EqualTo(new[] { 162 }));
    }

    [Test]
    public void ReaderRequiresExactFrameAndVerifiedTargetWithoutUnboundedAllocation()
    {
        foreach (int length in new[] { 0, 9, 11, 4096 })
            Assert.That(M6NpcStrikeReader.ReadPayload(new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M6NpcStrikeReader.Read(M2ContractsTests.Packet((PacketTypes)28, Body(), Slot), false).Kind,
            Is.EqualTo(PacketReadKind.UnknownRuntime));
        Assert.That(Root(new byte[9]).Handled, Is.True);
    }

    [Test]
    public void TestLabQueueCancelsNpc28BeforeReceiverWithoutCreatingSanction()
    {
        var queueOptions = new M18NpcStrikeQueueOptions
        {
            Enabled = true,
            WindowTicks = 3,
            LowDamageMaximum = 1,
            PerTargetLowDamageLimit = 2,
            PerSessionLowDamageLimit = 4,
            LowDamageRingCapacity = 4,
            HighSampleCapacity = 2,
        };
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint, queueOptions);
        causes.Install(); causes.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, causes);
        try
        {
            Assert.That(Root(Body(1000)).Handled, Is.False, "High damage remains record-only but consumes a bounded positive-damage unit.");
            Assert.That(Root(Body(1)).Handled, Is.True, "A high positive-damage unit plus one low unit reaches the target budget.");
            Assert.That(Root(Body(1)).Handled, Is.True, "The candidate budget must keep cancellation in the raw hook after the first block.");
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            Assert.That(causes.CaptureClientStrike(session), Is.Null, "A blocked current request must not leave a stale M8 sample.");

            causes.Forget(session);
            Assert.That(Root(Body(1)).Handled, Is.False, "Exact-session forget starts a fresh candidate window.");
        }
        finally
        {
            typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, null);
        }
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void QueueEnabledPreservesStructuralRejectionAndOldGenerationNoop(float knockback)
    {
        var queueOptions = new M18NpcStrikeQueueOptions
        {
            Enabled = true,
            WindowTicks = 3,
            LowDamageMaximum = 1,
            PerTargetLowDamageLimit = 4,
            PerSessionLowDamageLimit = 8,
            LowDamageRingCapacity = 8,
            HighSampleCapacity = 2,
        };
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint, queueOptions);
        causes.Install();
        causes.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, causes);
        try
        {
            Assert.That(Root(Body(knockback: knockback)).Handled, Is.True,
                "The queue's record-only result must not downgrade M6's numeric BLOCK.");
            Assert.That(Root(Body(knockback: 1, direction: 255)).Handled, Is.True,
                "The queue's record-only result must not downgrade M6's direction BLOCK.");

            var stale = Body(1000, float.NaN, 255, 255, generation: 2);
            Assert.That(Root(stale).Handled, Is.False,
                "The old-generation path remains the native no-op rather than a queue block.");
            Receive(stale);
            Assert.That(Target.life, Is.EqualTo(5000));
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
        }
        finally
        {
            typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, null);
        }
    }

    [Test]
    public void RejectedOutOfRangeRequestClearsPendingTransactionBeforeLaterNativeEvent()
    {
        var queueOptions = new M18NpcStrikeQueueOptions
        {
            Enabled = true,
            WindowTicks = 3,
            LowDamageMaximum = 1,
            PerTargetLowDamageLimit = 4,
            PerSessionLowDamageLimit = 8,
            LowDamageRingCapacity = 8,
            HighSampleCapacity = 2,
        };
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint, queueOptions);
        causes.Install();
        causes.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, causes);
        try
        {
            Assert.That(Root(Body()).Handled, Is.False);
            var transactions = (Array)typeof(M7NpcStrikeCauseContexts)
                .GetField("strikeTransactions", Private)!.GetValue(causes)!;
            Assert.That(transactions.GetValue(Slot), Is.Not.Null,
                "The accepted request must have a pending same-tick transaction before native delivery.");

            Assert.That(Root(Body(target: (byte)Main.maxNPCs)).Handled, Is.True,
                "The structural out-of-range rejection must cancel before native dispatch.");
            Assert.That(transactions.GetValue(Slot), Is.Null);

            Target.StrikeNPC(9, 0, 1, false, true, Slot, Main.player[Slot]);
            Assert.That(causes.CaptureClientStrike(session), Is.Null,
                "A later native call must not consume the rejected request's old transaction.");
        }
        finally
        {
            typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, null);
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
