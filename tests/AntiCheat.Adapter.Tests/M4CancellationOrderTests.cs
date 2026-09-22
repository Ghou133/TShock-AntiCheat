using System.Diagnostics;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

// Fault injection against the actual root/target hooks; no TCP or SQLite durability is claimed.
[TestFixture, NonParallelizable]
public sealed class M4CancellationOrderTests
{
    private const int Slot = 19;
    private const string Fingerprint = "target326-cancellation-fault-test";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private AntiCheatPlugin plugin = null!;
    private AntiCheatEngine engine = null!;
    private Store store = null!;
    private SessionKey session;
    private FaultPlayer actor = null!;
    private object binding = null!;
    private TSPlayer? oldActor;
    private Player oldPlayer = null!;
    private Chest[] oldChests = null!;
    private Dictionary<Point, Chest> oldChestIndex = null!;
    private TShockConfig oldConfig = null!;
    private LogWriterManager oldLog = null!;
    private FaultLogWriter? faultLog;
    private int oldMode;

    [SetUp]
    public async Task SetUp()
    {
        oldActor = ServerTShock.Players[Slot]; oldPlayer = Main.player[Slot]; oldChests = Main.chest;
        oldChestIndex = Chest._chestsByCoords;
        oldConfig = ServerTShock.Config; oldLog = ServerApi.LogWriter; oldMode = Main.netMode;
        Main.netMode = 2;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, chest = -1, position = new(320, 320) };
        Main.chest = new Chest[8000];
        Chest._chestsByCoords = [];
        ServerTShock.Config = new TShockConfig();
        ServerTShock.Config.Settings.RegionProtectChests = false;
        actor = new FaultPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Group = new Group("cancellation-fault-test"), Account = new UserAccount { ID = 1919, Name = "cancellation-fault-test" } };
        ServerTShock.Players[Slot] = actor;
        store = new Store();
        engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 1919), Is.EqualTo(AuthenticationResult.Authenticated));
        plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, Private)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", Fingerprint, "explicit-fault-fixture"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        binding = Activator.CreateInstance(bindingType, session, actor)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!).SetValue(binding, Slot);
        var handler = typeof(AntiCheatPlugin).GetMethod("OnGetData", Private)!
            .CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        ServerApi.Hooks.NetGetData.Register(plugin, handler, 1000);
    }

    [TearDown]
    public async Task TearDown()
    {
        typeof(ServerApi).GetProperty(nameof(ServerApi.LogWriter))!.SetValue(null, oldLog);
        faultLog?.Dispose(); faultLog = null;
        plugin.Dispose(); await plugin.ShutdownCompletion;
        ServerTShock.Players[Slot] = oldActor; Main.player[Slot] = oldPlayer; Main.chest = oldChests;
        Chest._chestsByCoords = oldChestIndex;
        ServerTShock.Config = oldConfig; Main.netMode = oldMode;
    }

    private void BreakLogger()
    {
        // Both the attached logger and the target manager's fallback throw, matching a failed log device.
        faultLog = new FaultLogWriter(Path.Combine(Path.GetTempPath(), "anticheat-log-fault-" + Guid.NewGuid().ToString("N") + ".log"));
        var manager = (LogWriterManager)Activator.CreateInstance(typeof(LogWriterManager),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [false], null)!;
        typeof(LogWriterManager).GetProperty("DefaultLogWriter", Private)!.SetValue(manager, faultLog);
        typeof(LogWriterManager).GetProperty("WrappedLogWriter", Private)!.SetValue(manager, faultLog);
        typeof(ServerApi).GetProperty(nameof(ServerApi.LogWriter))!.SetValue(null, manager);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ProvenRawPacketIsAlreadyHandledInsideThrowingDisconnectAndLaterHandlersCannotApplyIt(bool failedLogger)
    {
        var legal = M2ContractsTests.Packet((PacketTypes)120, [Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False);
        Assert.That(engine.SanctionCount, Is.Zero);
        var illegal = M2ContractsTests.Packet((PacketTypes)120, [Slot + 1, 0], Slot);
        bool handledAtDisconnect = false;
        actor.BeforeDisconnect = () => handledAtDisconnect = illegal.Handled;
        actor.ThrowOnDisconnect = true;
        int laterWrites = 0;
        void LaterHandler(GetDataEventArgs args) { if (!args.Handled) laterWrites++; }
        ServerApi.Hooks.NetGetData.Register(plugin, LaterHandler, 0);
        try
        {
            if (failedLogger) BreakLogger();
            Assert.DoesNotThrow(() => ServerApi.Hooks.NetGetData.Invoke(illegal));
            Assert.Multiple(() =>
            {
                Assert.That(handledAtDisconnect, Is.True, "Cancellation must commit before attempting a fallible disconnect.");
                Assert.That(illegal.Handled, Is.True);
                Assert.That(laterWrites, Is.Zero, "TSAPI continues later handlers after a handler fault.");
                Assert.That(engine.CanWrite(session), Is.False);
                Assert.That(engine.SanctionCount, Is.EqualTo(1));
                Assert.That(actor.Disconnects, Is.EqualTo(1), "A failed diagnostic must not skip the disconnect attempt.");
                if (failedLogger) Assert.That(faultLog!.Calls, Is.GreaterThan(0));
            });
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(store.Bans, Is.EqualTo(1));
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, LaterHandler); }
    }

    [Test]
    public void ActualCraftWrapperStillReturnsFalseWhenRootBlockDiagnosticsAndFallbackLoggerThrow()
    {
        var near = Chest.CreateWorldChest(0, 20, 20);
        var far = Chest.CreateWorldChest(1, 400, 400);
        bool Process(SessionKey _, TSPlayer __, BusinessRuleResult result)
        {
            var method = typeof(AntiCheatPlugin).GetMethod("ApplyBusinessResult", Private)!;
            var arguments = new object?[method.GetParameters().Length];
            arguments[0] = (byte)0; arguments[1] = false; arguments[2] = binding; arguments[3] = result;
            return (bool)method.Invoke(plugin, arguments)!;
        }
        using var inventory = new M3InventoryContexts(Fingerprint,
            id => id == Slot ? (session, actor, engine.CanWrite(session)) : (null, null, false), Process);
        // Target return stub avoids world tile setup; the real generated CanCraft hook and root callback still run.
        void AcceptedTarget(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        { args.ContinueExecution = false; args.HookReturnValue = true; }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += AcceptedTarget;
        try
        {
            inventory.Install();
            Assert.That(CraftingRequests.CanCraftFromChest(near, Slot), Is.True);
            BreakLogger();
            bool allowed = true;
            Assert.DoesNotThrow(() => allowed = CraftingRequests.CanCraftFromChest(far, Slot));
            Assert.That(allowed, Is.False);
            Assert.That(faultLog!.Calls, Is.GreaterThan(0));
            Assert.That(engine.CanWrite(session), Is.True, "Range safety is not a cheating proof.");
            Assert.That(engine.SanctionCount, Is.Zero);
        }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= AcceptedTarget; }
    }

    [Test]
    public void M5GlobalDispatcherFaultStopsBothExistingAccountsBeforeDownstreamWritesWithoutBanningThem()
    {
        const int otherSlot = Slot + 1;
        var oldOther = ServerTShock.Players[otherSlot];
        bool connected = Netplay.HasFullyConnectedClients;
        var other = new FaultPlayer(otherSlot) { IsLoggedIn = true, HasSentInventory = true,
            Group = new Group("maintenance-control"), Account = new UserAccount { ID = 2020, Name = "maintenance-control" } };
        ServerTShock.Players[otherSlot] = other;
        var otherSession = engine.OpenSession(otherSlot)!.Value;
        engine.Authenticate(otherSession, 2020);
        var bindings = (Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!;
        bindings.SetValue(Activator.CreateInstance(binding.GetType(), otherSession, other), otherSlot);
        int writes = 0;
        void Later(GetDataEventArgs args) { if (!args.Handled) writes++; }
        ServerApi.Hooks.NetGetData.Register(plugin, Later, 0);
        try
        {
            foreach (int slot in new[] { Slot, otherSlot })
                ServerApi.Hooks.NetGetData.Invoke(M2ContractsTests.Packet((PacketTypes)120, [(byte)slot, 0], slot));
            Assert.That(writes, Is.EqualTo(2), "Both authenticated actors reached the live downstream branch before the fault.");
            var update = typeof(AntiCheatPlugin).GetMethod("OnUpdate", Private)!.CreateDelegate<Action<EventArgs>>(plugin);
            var idle = typeof(AntiCheatPlugin).GetMethod("OnIdleMaintenance", Private)!.CreateDelegate<Action>(plugin);
            Netplay.HasFullyConnectedClients = false;
            update(EventArgs.Empty);
            var wrongThread = new Thread(() => idle());
            wrongThread.Start(); Assert.That(wrongThread.Join(TimeSpan.FromSeconds(3)), Is.True);
            Assert.That(typeof(AntiCheatPlugin).GetField("_infrastructureFailed", Private)!.GetValue(plugin), Is.True);
            foreach (int slot in new[] { Slot, otherSlot })
            foreach (byte packet in new byte[] { 5, 13, 16, 17, 20, 21, 27, 28, 31, 32, 33, 42, 48, 61, 82, 100, 109, 120, 153, 154, 155 })
            {
                var request = M2ContractsTests.Packet((PacketTypes)packet, [], slot);
                ServerApi.Hooks.NetGetData.Invoke(request);
                Assert.That(request.Handled, Is.True, $"Global maintenance must stop packet {packet} for account slot {slot}.");
            }
            var chat = new ServerChatEventArgs();
            typeof(ServerChatEventArgs).GetProperty(nameof(ServerChatEventArgs.Who))!.SetValue(chat, Slot);
            typeof(ServerChatEventArgs).GetProperty(nameof(ServerChatEventArgs.Text))!.SetValue(chat, "/build");
            typeof(AntiCheatPlugin).GetMethod("OnChat", Private)!.CreateDelegate<Action<ServerChatEventArgs>>(plugin)(chat);
            Assert.That(chat.Handled, Is.True, "Command/chat runs before NetGetData and must be protected separately.");
            var direct = typeof(AntiCheatPlugin).GetMethod("ApplyBusinessResult", Private)!;
            Assert.That(direct.Invoke(plugin, new object?[] { (byte)0, false, binding,
                new BusinessRuleResult("PASS", "1.0.0", ControlAction.Pass, Verdict.Pass, "direct-craft", true, true,
                    System.Collections.Immutable.ImmutableDictionary<string, string>.Empty), null }), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(writes, Is.EqualTo(2));
                Assert.That(engine.CanWrite(session) && engine.CanWrite(otherSession), Is.True,
                    "This reproduces the root-only fault: healthy engine sessions cannot bypass the global root guard.");
                Assert.That(engine.SanctionCount, Is.Zero);
                Assert.That(store.Bans, Is.Zero);
                Assert.That(actor.Disconnects + other.Disconnects, Is.Zero);
            });
            idle(); update(EventArgs.Empty);
            Assert.That(typeof(AntiCheatPlugin).GetField("_maintenanceCallbackFailed", Private)!.GetValue(plugin), Is.EqualTo(1),
                "Unconfirmed dispatcher thread integrity is not automatically cleared.");
        }
        finally
        {
            ServerApi.Hooks.NetGetData.Deregister(plugin, Later);
            bindings.SetValue(null, otherSlot); engine.Disconnect(otherSession);
            ServerTShock.Players[otherSlot] = oldOther; Netplay.HasFullyConnectedClients = connected;
        }
    }

    [Test]
    public async Task M5RecoverableJournalFailureResumesNormalInputsOnlyAfterActualSuccessfulRecovery()
    {
        var legal = M2ContractsTests.Packet((PacketTypes)120, [Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False);
        store.FailReads = true;
        Assert.That(await engine.RecoverAsync(), Is.False);
        var blocked = M2ContractsTests.Packet((PacketTypes)120, [Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(blocked);
        Assert.That(blocked.Handled, Is.True);
        Assert.That(actor.Disconnects, Is.Zero, "Temporary journal failure is not a revoked-account disconnect.");
        Assert.That(engine.SanctionCount, Is.Zero);
        store.FailReads = false;
        Assert.That(await engine.RecoverAsync(), Is.True);
        var resumed = M2ContractsTests.Packet((PacketTypes)120, [Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(resumed);
        Assert.That(resumed.Handled, Is.False);
        Assert.That(engine.CanWrite(session), Is.True);
        Assert.That(store.Bans, Is.Zero);
    }

    [Test]
    public void M5MaintenanceOnlyAllowsBoundedEmptyPingFromExistingAuthenticatedUnrevokedSession()
    {
        var network = (NetworkControls)typeof(AntiCheatPlugin).GetField("_network", Private)!.GetValue(plugin)!;
        Assert.That(network.Open(session, "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
        binding.GetType().GetProperty("NetworkRegistered")!.SetValue(binding, true);
        typeof(AntiCheatPlugin).GetField("_infrastructureFailed", Private)!.SetValue(plugin, true);
        var ping = M2ContractsTests.Packet((PacketTypes)154, [], Slot);
        ServerApi.Hooks.NetGetData.Invoke(ping);
        Assert.That(ping.Handled, Is.False, "The target's empty ping is the only permitted response path.");
        var malformed = M2ContractsTests.Packet((PacketTypes)154, [0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(malformed);
        Assert.That(malformed.Handled, Is.True);
        var upstream = M2ContractsTests.Packet((PacketTypes)154, [], Slot); upstream.Handled = true;
        ServerApi.Hooks.NetGetData.Invoke(upstream);
        Assert.That(upstream.Handled, Is.True);
        var proof = ProtocolRules.EvaluateIdentity(SelfIdentityMessage.Emoji, Slot, Slot + 1, true, true, true);
        engine.ObserveBusiness(new(session, 120, proof, new(Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
        var revoked = M2ContractsTests.Packet((PacketTypes)154, [], Slot);
        ServerApi.Hooks.NetGetData.Invoke(revoked);
        Assert.That(revoked.Handled, Is.True);
        Assert.That(engine.SanctionCount, Is.EqualTo(1), "Maintenance traffic itself creates no additional sanction.");
    }

    [TestCase(20, false)]
    [TestCase(20, true)]
    [TestCase(109, false)]
    public void M5RepeatedMalformedWorldFramesConsumeRootAdmissionBudgetWithoutAccountSanctions(int packetId, bool oversizedGrid)
    {
        var network = new NetworkControls(new FrozenClock(), new NetworkControlOptions
        {
            ConnectionOpeningCost = 1,
            ConnectionBudget = new(256, 16, 1, TimeSpan.FromMinutes(1))
        });
        typeof(AntiCheatPlugin).GetField("_network", Private)!.SetValue(plugin, network);
        Assert.That(network.Open(session, "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
        binding.GetType().GetProperty("NetworkRegistered")!.SetValue(binding, true);
        var legal = M2ContractsTests.Packet((PacketTypes)120, [Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False, "The same registered actor reaches the effective root admission path.");
        byte[] body = oversizedGrid ? [0, 0, 0, 0, 255, 255, 0] : new byte[packetId == 20 ? 6 : 8];
        int downstreamWrites = 0;
        void Later(GetDataEventArgs args) { if (!args.Handled) downstreamWrites++; }
        ServerApi.Hooks.NetGetData.Register(plugin, Later, 0);
        try
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                var frame = M2ContractsTests.Packet((PacketTypes)packetId, body, Slot);
                ServerApi.Hooks.NetGetData.Invoke(frame);
                Assert.That(frame.Handled, Is.True);
                Assert.That(actor.Disconnects, Is.EqualTo(attempt == 4 ? 1 : 0),
                    "Malformed input pays fixed request/byte cost; a claimed grid does not charge unvalidated cell work.");
            }
            Assert.Multiple(() =>
            {
                Assert.That(downstreamWrites, Is.Zero);
                Assert.That(engine.CanWrite(session), Is.True, "Resource disconnection is not account revocation.");
                Assert.That(engine.SanctionCount, Is.Zero);
                Assert.That(store.Bans, Is.Zero);
            });
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, Later); }
    }

    [Test]
    public void M5ProducerAndLoggerFaultStillDisableOnlyThatContextAndDrainIndependentMaintenance()
    {
        int producerCalls = 0;
        var vitals = new M4VitalContexts(Fingerprint);
        vitals.Install(_ => { producerCalls++; throw new IOException("injected-vital-producer-fault"); });
        typeof(AntiCheatPlugin).GetField("_vitals", Private)!.SetValue(plugin, vitals);
        var dispatcher = (ServerThreadDispatcher)typeof(AntiCheatPlugin).GetField("_dispatcher", Private)!.GetValue(plugin)!;
        int maintenanceCalls = 0;
        var queued = dispatcher.InvokeAsync(() => maintenanceCalls++).AsTask();
        var update = typeof(AntiCheatPlugin).GetMethod("OnUpdate", Private)!.CreateDelegate<Action<EventArgs>>(plugin);
        BreakLogger();
        Assert.DoesNotThrow(() => update(EventArgs.Empty));
        Assert.Multiple(() =>
        {
            Assert.That(faultLog!.Calls, Is.GreaterThan(0), "The actual target logger and its fallback both fail.");
            Assert.That(typeof(AntiCheatPlugin).GetField("_vitals", Private)!.GetValue(plugin), Is.Null);
            Assert.That(queued.IsCompletedSuccessfully, Is.True);
            Assert.That(maintenanceCalls, Is.EqualTo(1));
            Assert.That(typeof(AntiCheatPlugin).GetField("_infrastructureFailed", Private)!.GetValue(plugin), Is.False);
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
        });
        Assert.DoesNotThrow(() => update(EventArgs.Empty));
        Assert.That(producerCalls, Is.EqualTo(1), "A failed diagnostic does not reactivate the damaged producer.");
        var legal = M2ContractsTests.Packet((PacketTypes)120, [Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False, "An independent identity path remains available after the context fault.");
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => 0;
    }

    private sealed class FaultPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public bool ThrowOnDisconnect;
        public Action? BeforeDisconnect;
        public override void Disconnect(string reason)
        {
            Disconnects++; BeforeDisconnect?.Invoke();
            if (ThrowOnDisconnect) throw new IOException("injected-disconnect-send-failure");
        }
    }

    private sealed class FaultLogWriter(string path) : ServerLogWriter(path)
    {
        public int Calls;
        protected override void WriteLine(string context, string message, TraceLevel kind)
        { Calls++; throw new IOException("injected-log-device-failure"); }
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public int Bans;
        public bool FailReads;
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => FailReads
            ? ValueTask.FromException<IReadOnlyList<BanIntent>>(new IOException("injected-journal-unavailable"))
            : ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Bans++; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
