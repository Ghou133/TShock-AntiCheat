using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M16TimeoutTransportRetirementTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealRootDeadlineIsolatesCurrentAuthenticatedSessionBeforeOwnedCloseOrRetry(bool firstCloseFails)
    {
        await using var f = await Fixture.Create();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string fingerprint = "m16-owned-tcp-root-fixture";
        var store = new TimeoutStore();
        var engine = new AntiCheatEngine(f.Clock, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        var controls = new NetworkControls(f.Clock, new NetworkControlOptions
        { HandshakeTimeout = TimeSpan.FromSeconds(2), AuthenticationTimeout = TimeSpan.FromSeconds(3), ConnectionIdleTtl = TimeSpan.FromSeconds(30) });
        var plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, flags)!.SetValue(plugin, value);
        void Call(string name, params object[] args) => typeof(AntiCheatPlugin).GetMethod(name, flags)!.Invoke(plugin, args);
        object Binding() => ((Array)typeof(AntiCheatPlugin).GetField("_bindings", flags)!.GetValue(plugin)!).GetValue(Fixture.Slot)!;
        SessionKey Key() => (SessionKey)Binding().GetType().GetProperty("Key")!.GetValue(Binding())!;
        Set("_engine", engine); Set("_network", controls); Set("_timeoutRetirements", f.Queue);
        Set("_scope", ExecutionScope.TestLab);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "1.4.5.8", fingerprint, "explicit-locked-root-fixture"));
        try
        {
            var connect = new ConnectEventArgs();
            typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Fixture.Slot);
            Call("OnConnect", connect); Assert.That(connect.Handled, Is.False);
            SessionKey key = Key();
            f.Player.Account = new UserAccount { ID = 11602, Name = "owned-I02-fixture" }; f.Player.IsLoggedIn = true;
            Call("OnLogin", new PlayerPostLoginEventArgs(f.Player));
            Assert.That(engine.GetSession(key)!.AccountId, Is.EqualTo(11602));
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "AdapterFixture");
            f.Clock.Advance(2.99);
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "AdapterFixture");
            Assert.That(engine.CanWrite(key), Is.True); await f.Pair.AssertLive();
            f.Pair.Subject.FailuresRemaining = firstCloseFails ? 1 : 0;
            f.Clock.Advance(.01); int maintenanceThread = Environment.CurrentManagedThreadId;
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "AdapterFixture");
            Assert.That(f.Pair.Subject.LastCloseThreadId, Is.EqualTo(maintenanceThread), "No detached close task/thread was scheduled.");
            Assert.That(engine.CanWrite(key), Is.False, "The root write boundary closes synchronously even when transport Close fails.");
            Assert.That(controls.CapturePhase(key), Is.Null);
            var blocked = M2ContractsTests.Packet(PacketTypes.PlayerUpdate, [], Fixture.Slot);
            Call("OnGetData", blocked); Assert.That(blocked.Handled, Is.True);
            object expiredBinding = Binding();
            f.Player.IsLoggedIn = false; Call("OnLogout", new PlayerLogoutEventArgs(f.Player));
            f.Player.IsLoggedIn = true; Call("OnLogin", new PlayerPostLoginEventArgs(f.Player));
            Assert.That(Binding(), Is.SameAs(expiredBinding)); Assert.That(Key(), Is.EqualTo(key));
            Assert.That(engine.GetSession(key), Is.Null, "Account hooks cannot revive an expired physical connection.");
            if (firstCloseFails)
            {
                await f.Pair.AssertLive(); Assert.That(f.Queue.Snapshot.Pending, Is.EqualTo(1));
                f.Clock.Advance(.25); f.FullScan();
            }
            await f.Pair.AssertClosed();
            Assert.That(engine.SanctionCount, Is.Zero); Assert.That(store.Bans, Is.Zero); Assert.That(store.Intents, Is.Zero);
            Assert.That(f.Queue.Snapshot.Closed, Is.EqualTo(1)); Assert.That(f.Queue.Snapshot.Pending, Is.Zero);
        }
        finally { plugin.Dispose(); await plugin.ShutdownCompletion; }
    }

    private sealed class TimeoutStore : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public int Bans, Intents;
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) { Intents++; return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Bans++; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
