using System.Buffers.Binary;
using System.Text;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M4VitalContextTests
{
    private const int Slot = 7;
    private M4VitalContexts contexts = null!;
    private TSPlayer actor = null!;
    private SessionKey session;
    private Player oldPlayer = null!;
    private int oldMode;
    private bool oldSsc;
    private TShockConfig oldConfig = null!;
    private ServerSideConfig oldSscConfig = null!;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldMode = Main.netMode; oldSsc = Main.ServerSideCharacter;
        oldConfig = ServerTShock.Config;
        oldSscConfig = ServerTShock.ServerSideCharacterConfig;
        ServerTShock.Config = new TShockConfig();
        ServerTShock.ServerSideCharacterConfig = new ServerSideConfig();
        ServerTShock.Config.Settings.MaxHP = 500; ServerTShock.Config.Settings.MaxMP = 200;
        Main.netMode = 2; Main.ServerSideCharacter = true;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, statLifeMax = 500, statManaMax = 200 };
        actor = new CapturingPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("vitals-normal"), Account = new UserAccount { ID = 81, Name = "m4-vitals" } };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        contexts = new M4VitalContexts("target326-vitals-test");
        contexts.Install(Lookup);
        contexts.Tick(); // Observe the session before PlayerInfo/SSC restoration.
        actor.ReceivedInfo = true;
        HookEvents.Terraria.NetMessage.SendData += Sink;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, 81, false, DateTimeOffset.UtcNow), actor) : (null, null);

    // A fixture transport sink cancels the actual target method after this slice's real hook has observed it.
    // These are adapter method/hook checks, not TCP or successful delivery/acknowledgment claims.
    private static void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts.Dispose(); Main.player[Slot] = oldPlayer; Main.netMode = oldMode;
        Main.ServerSideCharacter = oldSsc; ServerTShock.Config = oldConfig;
        ServerTShock.ServerSideCharacterConfig = oldSscConfig;
    }

    private BusinessRuleResult Evaluate(int id, short maximum, short current = 1)
    {
        byte[] bytes = new byte[5]; bytes[0] = Slot;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(1), current);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(3), maximum);
        var packet = M4VitalPacketReader.Read(M2ContractsTests.Packet((PacketTypes)id, bytes, Slot), true);
        Assert.That(packet.Kind, Is.EqualTo(PacketReadKind.Parsed));
        return contexts.Evaluate(packet.Packet!, session, actor);
    }

    [TestCase(16, 500, 600)]
    [TestCase(42, 200, 400)]
    [TestCase(16, 40, 1)]
    public void ActualTargetReaderAndProducerAllowEffectiveAndHardcoreBoundaries(int id, short raw, short current) =>
        Assert.That(Evaluate(id, raw, current).Action, Is.EqualTo(ControlAction.Pass));

    [TestCase(16)]
    [TestCase(42)]
    public void FixedReaderRejectsTruncationTrailingAndUnverifiedRuntime(int id)
    {
        var packet = M2ContractsTests.Packet((PacketTypes)id, new byte[5], Slot);
        Assert.That(M4VitalPacketReader.Read(packet, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        packet.Length--;
        Assert.That(M4VitalPacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        packet = M2ContractsTests.Packet((PacketTypes)id, new byte[6], Slot);
        Assert.That(M4VitalPacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void PlayerInfoReadsPermanentFlagsAfterTargetVoiceFieldsAndUtf8Name()
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            writer.Write((byte)Slot); writer.Write((byte)0); writer.Write((byte)2); writer.Write(0.2f); writer.Write((byte)0);
            writer.Write("生命测试"); writer.Write(new byte[25]);
            writer.Write((byte)4); writer.Write((byte)31); writer.Write((byte)127);
        }
        var read = M4VitalPacketReader.ReadPayload(4, memory.ToArray());
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.Multiple(() =>
        {
            Assert.That(read.Packet!.UnlockFlags, Is.EqualTo(127));
            Assert.That(read.Packet.TorchFlags, Is.EqualTo(31));
            Assert.That(read.Packet.DifficultyFlags, Is.EqualTo(4));
            Assert.That(contexts.Evaluate(read.Packet, session, actor).Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(M4VitalPacketReader.ReadPayload(4, memory.ToArray()[..^1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        });
    }

    [Test]
    public void SscSelfExportAllowsNoncanonicalNativeGrowthAndOldDelayedEchoButNotLargerClaims()
    {
        actor.TPlayer.statManaMax = 199;
        NetMessage.SendData(42, Slot, -1, null, Slot);
        actor.TPlayer.statManaMax = 20;
        NetMessage.SendData(42, Slot, -1, null, Slot);
        Assert.Multiple(() =>
        {
            Assert.That(Evaluate(42, 219).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(Evaluate(42, 220).Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.ProvenCheat)); // Finite mana exception is not life bypass.
        });
    }

    [Test]
    public void ServerAcceptedValueAndRelayToPeersCannotAuthorizeSender()
    {
        actor.TPlayer.statLifeMax = 3000;
        NetMessage.SendData(16, -1, Slot, null, Slot);
        Assert.That(Evaluate(16, 3000).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void PoisonedServerExportOnlyDisablesItsVitalRule()
    {
        actor.TPlayer.statLifeMax = -1;
        NetMessage.SendData(16, Slot, -1, null, Slot);
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(42, 220).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void OffUpdateThreadSelfExportReportsOnceAndPreventsFurtherProof()
    {
        int notifications = 0;
        Exception? observedFault = null;
        contexts.IntegrityFault = exception => { notifications++; observedFault = exception; };
        // A dedicated thread is outside Tick's verified context. The real hook must reject the
        // history before reading game state; a second export must not emit another fault report.
        Task.Factory.StartNew(() =>
        {
            NetMessage.SendData(16, Slot, -1, null, Slot);
            NetMessage.SendData(42, Slot, -1, null, Slot);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        Assert.Multiple(() =>
        {
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(observedFault, Is.TypeOf<InvalidOperationException>());
            Assert.That(observedFault!.Message, Does.Contain("outside the verified update context"));
            Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(Evaluate(42, 220).Verdict, Is.EqualTo(Verdict.Unknown));
        });
    }

    [Test]
    public void SyncPermissionCustomPolicyAndLateHistoryStayUnknown()
    {
        Main.ServerSideCharacter = false;
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
        Main.ServerSideCharacter = true;
        actor.IgnoreSSCPackets = true;
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IgnoreSSCPackets = false;
        actor.Group.AddPermission(Permissions.ignorehp);
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(42, 220).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        actor.Group.RemovePermission(Permissions.ignorehp);
        ServerTShock.Config.Settings.MaxHP = 1000;
        Assert.That(Evaluate(16, 1001).Verdict, Is.EqualTo(Verdict.Unknown));
        ServerTShock.Config.Settings.MaxHP = 500;
        session = session with { Generation = session.Generation + 1 };
        contexts.Tick(); // Already received info: cannot retrospectively assert export-history completeness.
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void SessionAndWorldReplacementDoNotInheritServerExportAllowance()
    {
        actor.TPlayer.statLifeMax = 1000; NetMessage.SendData(16, Slot, -1, null, Slot);
        Assert.That(Evaluate(16, 1000).Verdict, Is.EqualTo(Verdict.Pass));
        session = session with { Generation = session.Generation + 1 };
        actor.ReceivedInfo = false; contexts.Tick(); actor.ReceivedInfo = true;
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        session = session with { WorldEpoch = session.WorldEpoch + 1 };
        contexts.Tick();
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void DisposedProducerCannotIssueProof()
    {
        contexts.Dispose();
        Assert.That(Evaluate(16, 505).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase(16, 505)]
    [TestCase(42, 220)]
    public async Task FirstProvenEventFromActualPacketRevokesBeforeIoAndBansOnce(int id, short raw)
    {
        var store = new Store();
        string ruleId = id == 16 ? M4VitalRules.LifeRuleId : M4VitalRules.ManaRuleId;
        var policy = new BusinessRulePolicy(ruleId, M4VitalRules.Version, "target326-vitals-test", M2RuleRegistry.ContextVersion,
            RuleQualification.TestLab, "docs/m4-vitals-inputs.md");
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, [policy]);
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        engine.Authenticate(session, 81);
        actor.ReceivedInfo = false; contexts.Tick(); actor.ReceivedInfo = true;
        var result = Evaluate(id, raw);
        var observation = new BusinessObservation(session, (byte)id, result,
            new("target326-vitals-test", M2RuleRegistry.ContextVersion, true, true, true, true));
        var first = engine.ObserveBusiness(observation);
        Assert.Multiple(() =>
        {
            Assert.That(first.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(first.Incident!.AccountId, Is.EqualTo(81));
            Assert.That(engine.CanWrite(session), Is.False);
            Assert.That(store.Appends, Is.Zero);
            Assert.That(store.Bans, Is.Zero);
        });
        Assert.That(engine.ObserveBusiness(observation).Incident!.IncidentId, Is.EqualTo(first.Incident!.IncidentId));
        Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(store.Bans, Is.EqualTo(1));
    }

    [TestCase(16, 505, 500)]
    [TestCase(42, 220, 200)]
    public async Task RegisteredRootHookBlocksBeforeActualCoreStateWritesAndDisconnectsFirstProof(int id, short raw, short legal)
    {
        var oldTsPlayer = ServerTShock.Players[Slot];
        string oldSavePath = ServerTShock.SavePath;
        ServerTShock.SavePath = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", "m4-vital-root-" + Guid.NewGuid().ToString("N"));
        var plugin = new AntiCheatPlugin(null!);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, flags)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(AntiCheatPlugin).GetField(name, flags)!.GetValue(plugin)!;
        try
        {
            plugin.Initialize();
            var priorEngine = Get<AntiCheatEngine>("_engine");
            Assert.That(SpinWait.SpinUntil(() => !priorEngine.IsMaintenanceMode, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await priorEngine.CompleteShutdownAsync(), Is.True);
            var store = new Store();
            var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
                store, store, M2RuleRegistry.Create("target326-vitals-test", ExecutionScope.TestLab));
            Assert.That(await engine.RecoverAsync(), Is.True);
            Get<M4VitalContexts?>("_vitals")?.Dispose();
            Set("_engine", engine); Set("_scope", ExecutionScope.TestLab);
            Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", "target326-vitals-test", "explicit-root-hook-fixture"));
            Set("_vitals", contexts);
            actor.ReceivedInfo = false;
            ServerTShock.Players[Slot] = actor;
            var connect = new ConnectEventArgs();
            typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
            ServerApi.Hooks.ServerConnect.Invoke(connect);
            Assert.That(connect.Handled, Is.False);
            PlayerHooks.OnPlayerPostLogin(actor);
            var binding = Get<Array>("_bindings").GetValue(Slot)!;
            session = (SessionKey)binding.GetType().GetProperty("Key")!.GetValue(binding)!;
            ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty);
            actor.ReceivedInfo = true;
            actor.PlayerData = new PlayerData(); // A real login creates this SSC store before HP/MP handlers run.
            GetDataEventArgs Packet(short maximum)
            {
                var payload = new byte[5]; payload[0] = Slot;
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1), 100);
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(3), maximum);
                return M2ContractsTests.Packet((PacketTypes)id, payload, Slot);
            }
            // The real TShock handler is reached only if the high-priority root hook leaves the candidate writable.
            GetDataHandlers.InitGetDataHandler();
            var legalPacket = Packet(legal);
            ServerApi.Hooks.NetGetData.Invoke(legalPacket);
            Assert.That(legalPacket.Handled, Is.False);
            using (var stream = new MemoryStream(legalPacket.Msg.readBuffer, 0, 5))
                Assert.That(GetDataHandlers.HandlerGetData((PacketTypes)id, actor, stream), Is.False);
            int stateBefore = id == 16 ? actor.TPlayer.statLifeMax : actor.TPlayer.statManaMax;
            var illegal = Packet(raw);
            ServerApi.Hooks.NetGetData.Invoke(illegal);
            Assert.Multiple(() =>
            {
                Assert.That(illegal.Handled, Is.True);
                Assert.That(engine.CanWrite(session), Is.False);
                Assert.That(engine.SanctionCount, Is.EqualTo(1));
                Assert.That(((CapturingPlayer)actor).Disconnects, Is.EqualTo(1));
                Assert.That(id == 16 ? actor.TPlayer.statLifeMax : actor.TPlayer.statManaMax, Is.EqualTo(stateBefore));
                Assert.That(id == 16 ? actor.PlayerData.maxHealth : actor.PlayerData.maxMana, Is.EqualTo(legal));
            });
            var alreadyCancelled = Packet(raw); alreadyCancelled.Handled = true;
            ServerApi.Hooks.NetGetData.Invoke(alreadyCancelled);
            Assert.That(alreadyCancelled.Handled, Is.True);
            Assert.That(engine.SanctionCount, Is.EqualTo(1));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(store.Bans, Is.EqualTo(1));
        }
        finally
        {
            plugin.Dispose(); await plugin.ShutdownCompletion;
            ServerTShock.Players[Slot] = oldTsPlayer;
            ServerTShock.SavePath = oldSavePath;
        }
    }

    private sealed class CapturingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public override void Disconnect(string reason) => Disconnects++;
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public int Appends, Bans;
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) { Appends++; return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Bans++; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
