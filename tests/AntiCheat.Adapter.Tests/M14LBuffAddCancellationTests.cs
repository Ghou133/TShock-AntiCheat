using System.Diagnostics;
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

/// <summary>Actual integrated NetGetData cancellation and fallible disconnect/log ordering; no TCP claim.</summary>
[TestFixture, NonParallelizable]
public sealed class M14LBuffAddCancellationTests
{
    private const int Slot = 23;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private AntiCheatPlugin plugin = null!;
    private AntiCheatEngine engine = null!;
    private Store store = null!;
    private SessionKey session;
    private FaultPlayer actor = null!;
    private TSPlayer? oldActor;
    private Player oldPlayer = null!;
    private int oldMode, oldMyPlayer;
    private bool[] oldPvp = null!;
    private LogWriterManager oldLog = null!;
    private FaultLogWriter? faultLog;

    [SetUp]
    public async Task SetUp()
    {
        oldActor = TShockAPI.TShock.Players[Slot]; oldPlayer = Main.player[Slot];
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldLog = ServerApi.LogWriter;
        Main.netMode = 2; Main.myPlayer = 255;
        oldPvp = Main.pvpBuff;
        Main.pvpBuff = Enumerable.Range(0, 401).Select(M14LBuffAddRules.IsNativePvpBuff).ToArray();
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, chest = -1 };
        actor = new FaultPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = true,
            Group = new Group("m14l-buff-cancellation"), Account = new UserAccount { ID = 2323, Name = "m14l-buff-cancellation" } };
        TShockAPI.TShock.Players[Slot] = actor;
        store = new Store();
        engine = new(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, actor.Account.ID), Is.EqualTo(AuthenticationResult.Authenticated));
        plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, Private)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m14l-buff-cancellation-fixture"));
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!).SetValue(
            Activator.CreateInstance(bindingType, session, actor), Slot);
        var root = typeof(AntiCheatPlugin).GetMethod("OnGetData", Private)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        ServerApi.Hooks.NetGetData.Register(plugin, root, 1000);
    }

    [TearDown]
    public async Task TearDown()
    {
        typeof(ServerApi).GetProperty(nameof(ServerApi.LogWriter))!.SetValue(null, oldLog);
        faultLog?.Dispose(); faultLog = null;
        plugin.Dispose(); await plugin.ShutdownCompletion;
        TShockAPI.TShock.Players[Slot] = oldActor; Main.player[Slot] = oldPlayer;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.pvpBuff = oldPvp;
    }

    [TestCase(53, false, 153)] [TestCase(53, true, 153)] [TestCase(55, false, 24)] [TestCase(55, true, 24)]
    [TestCase(53, false, 353)] [TestCase(53, true, 353)]
    public async Task FirstAdditionProofCancelsAndRevokesBeforeFallibleDisconnect(int message, bool alreadyCancelled, int buffType)
    {
        string rule = message == 53 ? (buffType == 353 ? M14RNpcShimmerRules.RuleId : M14LBuffAddRules.NpcRuleId) : M14LBuffAddRules.PlayerRuleId;
        byte[] legalBody = message == 53 ? [3, 0, (byte)(buffType & 255), (byte)(buffType >> 8), (byte)(buffType == 353 ? 100 : 88), (byte)(buffType == 353 ? 0 : 2)] : [(byte)(Slot + 1), 24, 0, 180, 0, 0, 0];
        var legal = M2ContractsTests.Packet((PacketTypes)message, legalBody, Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False); Assert.That(engine.SanctionCount, Is.Zero);
        byte[] illegalBody = message == 53 ? [3, 0, (byte)(buffType & 255), (byte)(buffType >> 8), 192, 75] : [Slot, 24, 0, 180, 0, 0, 0];
        var illegal = M2ContractsTests.Packet((PacketTypes)message, illegalBody, Slot);
        illegal.Handled = alreadyCancelled;
        bool cancelledAtDisconnect = false, revokedAtDisconnect = false;
        actor.BeforeDisconnect = () => { cancelledAtDisconnect = illegal.Handled; revokedAtDisconnect = !engine.CanWrite(session); };
        int laterWrites = 0;
        void Later(GetDataEventArgs args) { if (!args.Handled) laterWrites++; }
        ServerApi.Hooks.NetGetData.Register(plugin, Later, 0);
        try
        {
            BreakLogger();
            Assert.DoesNotThrow(() => ServerApi.Hooks.NetGetData.Invoke(illegal));
            Assert.That(cancelledAtDisconnect, Is.True); Assert.That(revokedAtDisconnect, Is.True);
            Assert.That(illegal.Handled, Is.True); Assert.That(laterWrites, Is.Zero);
            Assert.That(engine.SanctionCount, Is.EqualTo(1)); Assert.That(actor.Disconnects, Is.EqualTo(1));
            var next = M2ContractsTests.Packet(PacketTypes.PlayerSlot, [Slot, 9, 0, 99, 0, 0, 9, 0], Slot);
            Assert.DoesNotThrow(() => ServerApi.Hooks.NetGetData.Invoke(next));
            Assert.That(next.Handled, Is.True); Assert.That(laterWrites, Is.Zero);
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(store.Bans, Is.EqualTo(1));
            Assert.That(store.Intent!.AccountId, Is.EqualTo(actor.Account.ID));
            Assert.That(store.Intent.Evidence.RuleId, Is.EqualTo(rule));
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, Later); }
    }

    private void BreakLogger()
    {
        faultLog = new FaultLogWriter(Path.Combine(Path.GetTempPath(), "m14l-buff-log-fault-" + Guid.NewGuid().ToString("N") + ".log"));
        var manager = (LogWriterManager)Activator.CreateInstance(typeof(LogWriterManager),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [false], null)!;
        typeof(LogWriterManager).GetProperty("DefaultLogWriter", Private)!.SetValue(manager, faultLog);
        typeof(LogWriterManager).GetProperty("WrappedLogWriter", Private)!.SetValue(manager, faultLog);
        typeof(ServerApi).GetProperty(nameof(ServerApi.LogWriter))!.SetValue(null, manager);
    }

    private sealed class FaultPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public Action? BeforeDisconnect;
        public override void Disconnect(string reason) { Disconnects++; BeforeDisconnect?.Invoke(); throw new IOException("m14l-injected-disconnect-failure"); }
    }
    private sealed class FaultLogWriter(string path) : ServerLogWriter(path)
    {
        public int Calls;
        protected override void WriteLine(string context, string message, TraceLevel kind) { Calls++; throw new IOException("m14l-injected-log-failure"); }
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
