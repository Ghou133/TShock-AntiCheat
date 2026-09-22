using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M5VitalsContextTests
{
    private const int Slot = 7, Victim = 8;
    private M4VitalContexts contexts = null!;
    private CapturingPlayer actor = null!;
    private SessionKey session;
    private Player oldPlayer = null!, oldVictim = null!;
    private int oldMode, oldMyPlayer;
    private bool oldSsc, oldDedServ;
    private TShockConfig oldConfig = null!;
    private ServerSideConfig oldSscConfig = null!;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldVictim = Main.player[Victim]; oldMode = Main.netMode;
        oldMyPlayer = Main.myPlayer; oldDedServ = Main.dedServ; oldSsc = Main.ServerSideCharacter;
        oldConfig = ServerTShock.Config; oldSscConfig = ServerTShock.ServerSideCharacterConfig;
        ServerTShock.Config = new TShockConfig(); ServerTShock.ServerSideCharacterConfig = new ServerSideConfig();
        Main.netMode = 2; Main.dedServ = true; Main.myPlayer = 255; Main.ServerSideCharacter = true;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, statLife = 100, statLifeMax = 100, statLifeMax2 = 100 };
        Main.player[Victim] = new Player { whoAmI = Victim, active = true, statLife = 100, statLifeMax = 100, statLifeMax2 = 100 };
        actor = new CapturingPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m5-vitals-normal"), Account = new UserAccount { ID = 81, Name = "m5-vitals" } };
        session = new(Guid.NewGuid(), 1, Slot, 1);
        contexts = new M4VitalContexts("m5-vitals-target326"); contexts.Install(Lookup); contexts.Tick(); actor.ReceivedInfo = true;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, 81, false, DateTimeOffset.UtcNow), actor) : (null, null);

    [TearDown]
    public void TearDown()
    {
        contexts.Dispose(); Main.player[Slot] = oldPlayer; Main.player[Victim] = oldVictim;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedServ; Main.ServerSideCharacter = oldSsc;
        ServerTShock.Config = oldConfig; ServerTShock.ServerSideCharacterConfig = oldSscConfig;
    }

    internal static byte[] HurtPayload(int target, bool pvp, PlayerDeathReason? reason = null, short damage = 20, sbyte cooldown = -1)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write((byte)target); (reason ?? PlayerDeathReason.ByOther(0)).WriteSelfTo(writer);
        writer.Write(damage); writer.Write((byte)1); writer.Write((byte)(pvp ? 2 : 0)); writer.Write(cooldown);
        return output.ToArray();
    }

    private BusinessRuleResult Evaluate(byte[] payload)
    {
        var parsed = M4VitalPacketReader.Read(M2ContractsTests.Packet(PacketTypes.PlayerHurtV2, payload, Slot), true);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        return contexts.Evaluate(parsed.Packet!, session, actor);
    }

    [TestCase(Slot, false)]
    [TestCase(Slot, true)]
    [TestCase(Victim, true)]
    public void ActualDeathReasonSerializerAndRuntimeProducerPreserveNativeRoles(int target, bool pvp)
    {
        foreach (var reason in new[] { PlayerDeathReason.ByOther(20), PlayerDeathReason.ByNPC(1),
                     PlayerDeathReason.ByPlayer(Slot), PlayerDeathReason.ByCustomReason(new string('生', 400)) })
        {
            var payload = HurtPayload(target, pvp, reason);
            Assert.That(Evaluate(payload).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(M4VitalPacketReader.ReadPayload(117, payload[..^1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        }
    }

    [Test]
    public void AllReasonPresenceLayoutsUseExactTargetParserWithoutUnboundedStringAllocation()
    {
        for (int mask = 0; mask < 256; mask++)
        {
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            writer.Write((byte)Victim); writer.Write((byte)mask);
            for (int bit = 0; bit < 7; bit++) if ((mask & (1 << bit)) != 0)
                if (bit is 3 or 6) writer.Write((byte)1); else writer.Write((short)1);
            if ((mask & 128) != 0) writer.Write("合法来源测试");
            writer.Write((short)20); writer.Write((byte)1); writer.Write((byte)2); writer.Write((sbyte)-1);
            var payload = output.ToArray();
            using var stream = new MemoryStream(payload); using var reader = new BinaryReader(stream);
            reader.ReadByte(); PlayerDeathReason.FromReader(reader);
            Assert.That(stream.Position, Is.EqualTo(payload.Length - 5));
            Assert.That(Evaluate(payload).Verdict, Is.EqualTo(Verdict.Pass), "presence=" + mask);
            Assert.That(M4VitalPacketReader.ReadPayload(117, payload.Concat(new byte[1]).ToArray()).Kind,
                Is.EqualTo(PacketReadKind.Malformed));
        }
        Assert.That(M4VitalPacketReader.ReadPayload(117, new byte[] { Victim, 128, 255, 255, 255, 255, 127, 0, 0, 0, 0, 0 }).Kind,
            Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void DelayedPvpAfterHostileToggleAndJitterPauseResumeNeverBecomeNonPvpProof()
    {
        // Deterministic arrival schedule; no sleep, network-delivery or TCP-ACK inference.
        long elapsedMilliseconds = 0;
        foreach (int delay in new[] { 0, 120, 17, 400, 2000, 4, 60 })
        {
            elapsedMilliseconds += delay;
            Main.player[Victim].hostile = elapsedMilliseconds % 2 == 0;
            contexts.Tick();
            Assert.That(Evaluate(HurtPayload(Victim, true)).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(Evaluate(HurtPayload(Slot, false, PlayerDeathReason.ByOther(20))).Verdict, Is.EqualTo(Verdict.Pass));
            var current = new M4VitalObservation(M4VitalKind.Life, Slot, Current: 100, RawMaximum: 100);
            Assert.That(contexts.Evaluate(current, session, actor).Verdict, Is.EqualTo(Verdict.Pass));
        }
        TestContext.Out.WriteLine("Scenario elapsed arrival schedule: " + elapsedMilliseconds + "ms; no packets or timeouts imply applied damage.");
        Assert.That(Evaluate(HurtPayload(Victim, false)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void SynchronizationAndUnverifiedThreadStayUnknownButHistoricalVitalLossIsUnneeded()
    {
        actor.IgnoreSSCPackets = true;
        Assert.That(Evaluate(HurtPayload(Victim, false)).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IgnoreSSCPackets = false;
        session = session with { Generation = session.Generation + 1 };
        contexts.Tick(); // Late vital history is irrelevant to this packet-local role contract.
        Main.ServerSideCharacter = false;
        Assert.That(Evaluate(HurtPayload(Victim, false)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        var outsideThread = Task.Factory.StartNew(() => Evaluate(HurtPayload(Victim, false)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        Assert.That(outsideThread.Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void ActualTargetDamageHealingImmunityAndManaMethodsExposeOverlapWithoutLockBloodVerdict()
    {
        var player = actor.TPlayer;
        double damage = player.Hurt(PlayerDeathReason.ByOther(0), 20, 0, quiet: true, dodgeable: false);
        Assert.That(damage, Is.GreaterThan(0)); Assert.That(player.statLife, Is.LessThan(100));
        int afterDamage = player.statLife;
        player.Heal(1000);
        Assert.That(player.statLife, Is.EqualTo(100));
        player.creativeGodMode = true;
        Assert.That(player.Hurt(PlayerDeathReason.ByOther(0), 20, 0, quiet: true), Is.Zero);
        Assert.That(player.statLife, Is.EqualTo(100));
        player.KillMe(PlayerDeathReason.ByOther(0), 999, 0);
        Assert.That(player.dead, Is.False); Assert.That(player.statLife, Is.EqualTo(100));
        player.creativeGodMode = false;
        player.statMana = 100; player.statManaMax = 100; player.statManaMax2 = 100; player.manaCost = 1;
        Assert.That(player.CheckMana(5, pay: true, blockQuickMana: true), Is.True);
        Assert.That(player.statMana, Is.EqualTo(95));
        player.statMana = 100; player.manaCost = 0.1f;
        Assert.That(player.CheckMana(5, pay: true, blockQuickMana: true), Is.True);
        Assert.That(player.statMana, Is.EqualTo(100), "Target rounds the discounted mana cost to an integer; no consumption alone is not proof.");
        foreach (int life in new[] { afterDamage, 100 })
            Assert.That(contexts.Evaluate(new(M4VitalKind.Life, Slot, life, 100), session, actor).Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(contexts.Evaluate(new(M4VitalKind.Mana, Slot, 100, 100), session, actor).Verdict, Is.EqualTo(Verdict.Pass));
        TestContext.Out.WriteLine($"Actual target Hurt:100->{afterDamage}; Heal:100; creativeGodMode Hurt/KillMe:100/alive; mana pay:100->95; integer-rounded cost:100->100.");
    }

    [Test]
    public async Task RegisteredRootCancelsFirstForeignNonPvpBeforeVictimDamageBroadcastAndPersistence()
    {
        var oldTsPlayer = ServerTShock.Players[Slot];
        string oldSavePath = ServerTShock.SavePath;
        ServerTShock.SavePath = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", "m5-vitals-" + Guid.NewGuid().ToString("N"));
        var plugin = new AntiCheatPlugin(null!);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, flags)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(AntiCheatPlugin).GetField(name, flags)!.GetValue(plugin)!;
        int downstreamHurtAttempts = 0;
        void OutboundSink(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
        {
            downstreamHurtAttempts++; args.ContinueExecution = false;
        }
        HookEvents.Terraria.NetMessage.SendPlayerHurt += OutboundSink;
        try
        {
            plugin.Initialize();
            var priorEngine = Get<AntiCheatEngine>("_engine");
            Assert.That(SpinWait.SpinUntil(() => !priorEngine.IsMaintenanceMode, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await priorEngine.CompleteShutdownAsync(), Is.True);
            var store = new Store();
            var policy = new BusinessRulePolicy(M5VitalsRules.HurtRuleId, M5VitalsRules.Version, "m5-vitals-target326",
                M2RuleRegistry.ContextVersion, RuleQualification.TestLab, "docs/m5-vitals.md");
            var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab },
                RulePolicy.Disabled, store, store, [policy]);
            Assert.That(await engine.RecoverAsync(), Is.True);
            Get<M4VitalContexts?>("_vitals")?.Dispose();
            Set("_engine", engine); Set("_scope", ExecutionScope.TestLab);
            Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", "m5-vitals-target326", "explicit-target-method-fixture"));
            Set("_vitals", contexts);
            actor.ReceivedInfo = false; ServerTShock.Players[Slot] = actor;
            var connect = new ConnectEventArgs();
            typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
            ServerApi.Hooks.ServerConnect.Invoke(connect); Assert.That(connect.Handled, Is.False);
            PlayerHooks.OnPlayerPostLogin(actor);
            var binding = Get<Array>("_bindings").GetValue(Slot)!;
            session = (SessionKey)binding.GetType().GetProperty("Key")!.GetValue(binding)!;
            ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty); actor.ReceivedInfo = true;
            var nativeSelf = M2ContractsTests.Packet(PacketTypes.PlayerHurtV2, HurtPayload(Slot, false), Slot);
            ServerApi.Hooks.NetGetData.Invoke(nativeSelf); Assert.That(nativeSelf.Handled, Is.False);
            GetDataHandlers.InitGetDataHandler();
            using (var stream = new MemoryStream(nativeSelf.Msg.readBuffer, 0, nativeSelf.Length - 1))
                Assert.That(GetDataHandlers.HandlerGetData(PacketTypes.PlayerHurtV2, actor, stream), Is.False);
            actor.TPlayer.Hurt(PlayerDeathReason.ByOther(0), 20, 0, quiet: true, dodgeable: false);
            Assert.That(actor.TPlayer.statLife, Is.LessThan(100), "Legal self declaration reaches actual target damage method.");
            var nativePvp = M2ContractsTests.Packet(PacketTypes.PlayerHurtV2, HurtPayload(Victim, true), Slot);
            ServerApi.Hooks.NetGetData.Invoke(nativePvp); Assert.That(nativePvp.Handled, Is.False);
            int victimLife = Main.player[Victim].statLife;
            downstreamHurtAttempts = 0;
            var illegal = M2ContractsTests.Packet(PacketTypes.PlayerHurtV2, HurtPayload(Victim, false), Slot);
            ServerApi.Hooks.NetGetData.Invoke(illegal);
            Assert.Multiple(() =>
            {
                Assert.That(illegal.Handled, Is.True);
                Assert.That(engine.CanWrite(session), Is.False);
                Assert.That(engine.SanctionCount, Is.EqualTo(1));
                Assert.That(actor.Disconnects, Is.EqualTo(1));
                Assert.That(Main.player[Victim].statLife, Is.EqualTo(victimLife));
                Assert.That(downstreamHurtAttempts, Is.Zero);
                Assert.That(store.Appends, Is.Zero); Assert.That(store.Bans, Is.Zero);
            });
            var followup = M2ContractsTests.Packet(PacketTypes.PlayerHurtV2, HurtPayload(Slot, false), Slot);
            ServerApi.Hooks.NetGetData.Invoke(followup); Assert.That(followup.Handled, Is.True);
            var alreadyCancelled = M2ContractsTests.Packet(PacketTypes.PlayerHurtV2, HurtPayload(Victim, false), Slot);
            alreadyCancelled.Handled = true; ServerApi.Hooks.NetGetData.Invoke(alreadyCancelled);
            Assert.That(alreadyCancelled.Handled, Is.True); Assert.That(engine.SanctionCount, Is.EqualTo(1));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(store.Bans, Is.EqualTo(1)); Assert.That(store.AccountId, Is.EqualTo(81));
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendPlayerHurt -= OutboundSink;
            plugin.Dispose(); await plugin.ShutdownCompletion;
            ServerTShock.Players[Slot] = oldTsPlayer; ServerTShock.SavePath = oldSavePath;
        }
    }

    private sealed class CapturingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public override void Disconnect(string reason) => Disconnects++;
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public int Appends, Bans; public long AccountId;
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) { Appends++; return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default)
        { Bans++; AccountId = intent.AccountId; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
