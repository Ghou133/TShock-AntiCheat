using System.Reflection;
using System.Runtime.CompilerServices;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M16TimeoutTransportRetirementTests
{
    [Test]
    public async Task PreHelloScanDoesNotCallUnknownProviderUnderItsMetadataLock()
    {
        await using var f = await Fixture.Create(); f.Native.State = 0; ServerTShock.Players[Fixture.Slot] = null;
        var unknown = new UntrustedPreHelloSocket(); f.Native.Socket = unknown;
        using var guard = new M17PreHelloDeadlineGuard(f.Clock); guard.Install();
        for (int i = 0; i < 16; i++) Assert.That(guard.Inspect(), Is.Empty);
        Assert.That(unknown.Queries, Is.Zero, "Unknown socket code cannot introduce a reverse lock acquisition from guard metadata.");
        Assert.That(guard.Healthy, Is.True); Assert.That(guard.Count, Is.Zero); await f.Pair.AssertLive();
    }

    [Test]
    public async Task NativeResetObserverRevalidatesSocketUnderLockBeforeRemovingReplacementClock()
    {
        await using var f = await Fixture.Create(); await using var next = await Pair.Create();
        using var nativeBuffer = new PreHelloNativeBuffer(); f.Native.State = 0; ServerTShock.Players[Fixture.Slot] = null;
        using var guard = new M17PreHelloDeadlineGuard(f.Clock); guard.Install();
        for (int i = 0; i < 16; i++) guard.Inspect();
        var first = guard.CaptureRetirement(Fixture.Slot); f.Clock.Advance(1);
        object metadataGate = typeof(M17PreHelloDeadlineGuard).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(guard)!;
        Exception? error = null;
        var reset = new Thread(() => { try { f.Native.Reset(); } catch (Exception caught) { error = caught; } });
        M16TimeoutTransportRetirement? replacement;
        lock (metadataGate)
        {
            reset.Start();
            Assert.That(SpinWait.SpinUntil(() => f.Native.Socket is null &&
                reset.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(2)), Is.True,
                "Actual native reset completed while its passive observer waits for the metadata gate.");
            // A distinct owned transport is committed before the delayed passive cleanup. This
            // explicit concurrency fixture never lets either guard close a foreign socket.
            f.Native.Socket = new TShockAPI.Sockets.LinuxTcpSocket(next.Subject); f.Native.IsActive = true;
            for (int i = 0; i < 16; i++) guard.Inspect();
            replacement = guard.CaptureRetirement(Fixture.Slot);
            Assert.That(replacement, Is.Not.Null.And.Not.SameAs(first));
        }
        Assert.That(reset.Join(TimeSpan.FromSeconds(2)), Is.True); Assert.That(error, Is.Null);
        Assert.That(guard.CaptureRetirement(Fixture.Slot), Is.SameAs(replacement), "Delayed reset observer must not erase the new transport's origin.");
        await next.AssertLive();
    }

    private sealed class UntrustedPreHelloSocket : Terraria.Net.Sockets.ISocket
    {
        public int Queries;
        public bool IsConnected() { Queries++; throw new InvalidOperationException("unknown provider must not be invoked"); }
        public void Close() => throw new InvalidOperationException();
        public void Connect(Terraria.Net.RemoteAddress address) => throw new InvalidOperationException();
        public Terraria.Net.RemoteAddress GetRemoteAddress() => throw new InvalidOperationException();
        public bool IsDataAvailable() => throw new InvalidOperationException();
        public void AsyncSend(byte[] data, int offset, int size, Terraria.Net.Sockets.SocketSendCallback callback, object state = null!) => throw new InvalidOperationException();
        public void AsyncReceive(byte[] data, int offset, int size, Terraria.Net.Sockets.SocketReceiveCallback callback, object state = null!) => throw new InvalidOperationException();
        public bool StartListening(Terraria.Net.Sockets.SocketConnectionAccepted callback) => throw new InvalidOperationException();
        public void StopListening() => throw new InvalidOperationException();
    }

    [Test]
    public async Task NativeOnlyPartialFrameHasNoAccountAndCannotRenewMonotonicDeadline()
    {
        await using var f = await Fixture.Create();
        f.Native.State = 0; ServerTShock.Players[Fixture.Slot] = null;
        using var guard = new M17PreHelloDeadlineGuard(f.Clock, TimeSpan.FromSeconds(3)); guard.Install();
        var priorBuffer = NetMessage.buffer[Fixture.Slot];
        NetMessage.buffer[Fixture.Slot] = new MessageBuffer { whoAmI = Fixture.Slot };
        try
        {
            for (int i = 0; i < 16; i++) Assert.That(guard.Inspect(), Is.Empty);
            long origin = guard.CaptureFirstSeenAt(Fixture.Slot)!.Value;
            Assert.That(guard.CaptureRetirement(Fixture.Slot)!.Session, Is.Null);
            f.Native.TimeOutTimer = 123;
            NetMessage.ReceiveBytes([100, 0, 1], 3, Fixture.Slot);
            for (int i = 0; i < 5; i++)
            {
                f.Clock.Advance(.5); NetMessage.ReceiveBytes([84], 1, Fixture.Slot); NetMessage.CheckBytes(Fixture.Slot);
                Assert.That(f.Native.TimeOutTimer, Is.EqualTo(123), "Partial native frames do not enter GetData's timer reset.");
                for (int j = 0; j < 16; j++) Assert.That(guard.Inspect(), Is.Empty);
                Assert.That(guard.CaptureFirstSeenAt(Fixture.Slot), Is.EqualTo(origin));
            }
            await f.Pair.AssertLive(); f.Clock.Advance(.5);
            M16TimeoutTransportRetirement? expiry = null;
            for (int i = 0; i < 16; i++) expiry ??= guard.Inspect().SingleOrDefault();
            Assert.That(expiry, Is.Not.Null); Assert.That(expiry!.Session, Is.Null);
            Assert.That(expiry.TryAuthorizeBeforeHello(), Is.True); Assert.That(guard.IsTerminal(Fixture.Slot), Is.True);
            f.Queue.EnqueueAuthorized(expiry); await f.Pair.AssertClosed();
            Assert.That(f.Native.PendingTermination, Is.False, "Deadline helper does not assign native cleanup flags.");
        }
        finally { NetMessage.buffer[Fixture.Slot] = priorBuffer; }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RootPreHelloExpiryCannotCreateAccountOrReviveItsFailedClose(bool firstCloseFails)
    {
        await using var f = await Fixture.Create();
        f.Native.State = 0; ServerTShock.Players[Fixture.Slot] = null;
        using var guard = new M17PreHelloDeadlineGuard(f.Clock, TimeSpan.FromSeconds(3)); guard.Install();
        var store = new TimeoutStore();
        var engine = new AntiCheatEngine(f.Clock, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create("prehello-fixture", ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        var plugin = new AntiCheatPlugin(null!); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, flags)!.SetValue(plugin, value);
        void Call(string name, params object[] args) => typeof(AntiCheatPlugin).GetMethod(name, flags)!.Invoke(plugin, args);
        Set("_engine", engine); Set("_timeoutRetirements", f.Queue); Set("_preHelloDeadlines", guard);
        try
        {
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "prehello-fixture");
            f.Clock.Advance(2.99);
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "prehello-fixture");
            await f.Pair.AssertLive(); f.Pair.Subject.FailuresRemaining = firstCloseFails ? 1 : 0;
            f.Clock.Advance(.01);
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "prehello-fixture");
            var retirement = guard.CaptureRetirement(Fixture.Slot)!;
            Assert.That(retirement.Session, Is.Null); Assert.That(retirement.Authorized, Is.True);
            // A complete Hello arriving after deadline can cause the earlier TShock hook to
            // create an actor, but root must deny it before manufacturing a new session.
            ServerTShock.Players[Fixture.Slot] = new TSPlayer(Fixture.Slot);
            var connect = new ConnectEventArgs(); typeof(ConnectEventArgs).GetProperty("Who")!.SetValue(connect, Fixture.Slot);
            Call("OnConnect", connect); Assert.That(connect.Handled, Is.True);
            Assert.That(((Array)typeof(AntiCheatPlugin).GetField("_bindings", flags)!.GetValue(plugin)!).GetValue(Fixture.Slot), Is.Null);
            if (firstCloseFails) { await f.Pair.AssertLive(); f.Clock.Advance(.25); f.FullScan(); }
            await f.Pair.AssertClosed(); Assert.That(store.Bans, Is.Zero); Assert.That(store.Intents, Is.Zero);
            Assert.That(engine.SanctionCount, Is.Zero);
        }
        finally { plugin.Dispose(); await plugin.ShutdownCompletion; }
    }

    [TestCase("complete-hello")]
    [TestCase("different-provider")]
    [TestCase("raw-socket-replacement")]
    [TestCase("native-reset")]
    public async Task LegalCompletionOrNewNativeTransportDoesNotInheritPreHelloExpiry(string change)
    {
        await using var f = await Fixture.Create(); await using var next = await Pair.Create();
        using var nativeBuffer = new PreHelloNativeBuffer();
        f.Native.State = 0; ServerTShock.Players[Fixture.Slot] = null;
        using var guard = new M17PreHelloDeadlineGuard(f.Clock, TimeSpan.FromSeconds(3)); guard.Install();
        for (int i = 0; i < 16; i++) guard.Inspect();
        var first = guard.CaptureRetirement(Fixture.Slot)!; f.Clock.Advance(2.99);
        if (change == "complete-hello") { f.Native.State = 1; ServerTShock.Players[Fixture.Slot] = f.Player; }
        else if (change == "different-provider") f.Native.Socket = new TShockAPI.Sockets.LinuxTcpSocket(next.Subject);
        else if (change == "raw-socket-replacement") f.Pair.Subject.Client = next.Subject.Client;
        else
        {
            f.Native.Reset(); Assert.That(f.Native.Socket, Is.Null); Assert.That(guard.Count, Is.Zero);
            f.Native.Socket = new TShockAPI.Sockets.LinuxTcpSocket(next.Subject); f.Native.IsActive = true;
        }
        for (int i = 0; i < 16; i++) Assert.That(guard.Inspect(), Is.Empty);
        f.Clock.Advance(.02);
        for (int i = 0; i < 16; i++) Assert.That(guard.Inspect(), Is.Empty);
        Assert.That(first.Authorized, Is.False);
        if (change == "complete-hello") { Assert.That(guard.Count, Is.Zero); await f.Pair.AssertLive(); }
        else { Assert.That(guard.CaptureRetirement(Fixture.Slot), Is.Not.SameAs(first)); await next.AssertLive(); }
    }

    [Test]
    public async Task CanceledNativeResetRetainsDeadlineAndObserverFailureCannotStopNativeReset()
    {
        await using var f = await Fixture.Create(); f.Native.State = 0; ServerTShock.Players[Fixture.Slot] = null;
        using var nativeBuffer = new PreHelloNativeBuffer();
        using var guard = new M17PreHelloDeadlineGuard(f.Clock, TimeSpan.FromSeconds(3)); guard.Install();
        for (int i = 0; i < 16; i++) guard.Inspect();
        long? origin = guard.CaptureFirstSeenAt(Fixture.Slot);
        void Cancel(RemoteClient client, HookEvents.Terraria.RemoteClient.ResetEventArgs args) { if (client == f.Native) args.ContinueExecution = false; }
        HookEvents.Terraria.RemoteClient.Reset += Cancel;
        try { f.Native.Reset(); Assert.That(guard.CaptureFirstSeenAt(Fixture.Slot), Is.EqualTo(origin)); await f.Pair.AssertLive(); }
        finally { HookEvents.Terraria.RemoteClient.Reset -= Cancel; }
        int faults = 0; guard.IntegrityFault = _ => { faults++; throw new IOException("diagnostic failure"); };
        f.Clock.Throw = true; Assert.That(guard.Inspect(), Is.Empty); Assert.That(guard.Healthy, Is.False);
        Assert.That(guard.Inspect(), Is.Empty); Assert.That(faults, Is.EqualTo(1));
        f.Native.Reset(); Assert.That(f.Native.Socket, Is.Null); Assert.That(guard.Count, Is.Zero);
        guard.Dispose(); Assert.That(guard.Count, Is.Zero);
    }

    private sealed class PreHelloNativeBuffer : IDisposable
    {
        private readonly MessageBuffer previous = NetMessage.buffer[Fixture.Slot];
        public PreHelloNativeBuffer() => NetMessage.buffer[Fixture.Slot] = new MessageBuffer { whoAmI = Fixture.Slot };
        public void Dispose() => NetMessage.buffer[Fixture.Slot] = previous;
    }

    [Test]
    public async Task ActualTsapiRepeatedHelloAfterFirstAcceptedStatePreservesActorSessionAndDeadline()
    {
        await using var f = await Fixture.Create();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var oldBans = ServerTShock.Bans; var oldWhitelist = ServerTShock.Whitelist;
        var oldGeo = ServerTShock.Geo; bool oldShuttingDown = ServerTShock.ShuttingDown;
        var path = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "whitelist.txt"), "127.0.0.1");
        using var database = new SqliteConnection("Data Source=" + Path.Combine(path, "bans.sqlite") + ";Pooling=False");
        ServerTShock.Bans = new BanManager(database); ServerTShock.Whitelist = new Whitelist(Path.Combine(path, "whitelist.txt"));
        ServerTShock.Geo = null!; ServerTShock.ShuttingDown = false;
        // This exact target method uses no TShock instance fields. Avoid its unrelated startup/disposal.
        var tshock = (ServerTShock)RuntimeHelpers.GetUninitializedObject(typeof(ServerTShock));
        var native = typeof(ServerTShock).GetMethod("OnConnect", flags)!;
        var store = new TimeoutStore();
        var engine = new AntiCheatEngine(f.Clock, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create("native-hello-fixture", ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        var controls = new NetworkControls(f.Clock, new() { AuthenticationTimeout = TimeSpan.FromSeconds(3) });
        var plugin = new AntiCheatPlugin(null!);
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, flags)!.SetValue(plugin, value);
        void Call(string name, params object[] args) => typeof(AntiCheatPlugin).GetMethod(name, flags)!.Invoke(plugin, args);
        object Binding() => ((Array)typeof(AntiCheatPlugin).GetField("_bindings", flags)!.GetValue(plugin)!).GetValue(Fixture.Slot)!;
        SessionKey Key() => (SessionKey)Binding().GetType().GetProperty("Key")!.GetValue(Binding())!;
        void Native(ConnectEventArgs e) => native.Invoke(tshock, [e]);
        void Root(ConnectEventArgs e) => Call("OnConnect", e);
        void Hello()
        {
            var method = typeof(HookManager).GetMethod("InvokeNetGetData", flags)!;
            object[] args = [(byte)1, new MessageBuffer { whoAmI = Fixture.Slot }, 0, 14];
            Assert.That(method.Invoke(ServerApi.Hooks, args), Is.EqualTo(false));
        }
        Set("_engine", engine); Set("_network", controls); Set("_timeoutRetirements", f.Queue);
        Set("_scope", ExecutionScope.TestLab); Set("_targetRuntime", new TargetRuntimeStatus(true, "1.4.5.8", "native-hello-fixture", "fixture"));
        ServerApi.Hooks.ServerConnect.Register(plugin, Native, 0);
        ServerApi.Hooks.ServerConnect.Register(plugin, Root, -1000);
        try
        {
            f.Native.State = 0;
            Hello(); var firstActor = ServerTShock.Players[Fixture.Slot]; var firstKey = Key();
            // Target native Hello advances to1 after TSAPI returns. This is explicit native-state
            // preparation; the assertion below exercises the genuine subsequent TSAPI predicate.
            f.Native.State = 1;
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "actual-hello-fixture");
            var first = controls.CapturePhase(firstKey)!; Assert.That(first.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
            f.Clock.Advance(2.9); Hello(); var secondKey = Key();
            Assert.That(ServerTShock.Players[Fixture.Slot], Is.SameAs(firstActor));
            Assert.That(secondKey, Is.EqualTo(firstKey));
            Assert.That(f.Native.Socket, Is.SameAs(f.Provider));
            Assert.That(controls.CapturePhase(firstKey), Is.Not.Null);
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "actual-hello-fixture");
            Assert.That(controls.CapturePhase(secondKey)!.PhaseEnteredAt, Is.EqualTo(first.PhaseEnteredAt), "Target InvokeServerConnect rejects repeated Hello before invoking TShock/root handlers.");
            f.Clock.Advance(.2);
            for (int i = 0; i < 16; i++) Call("MaintainNetworkConnections", "actual-hello-fixture");
            Assert.That(engine.CanWrite(secondKey), Is.False, "Same TCP expires at its first accepted phase origin.");
            await f.Pair.AssertClosed(); Assert.That(store.Bans, Is.Zero);
        }
        finally
        {
            ServerApi.Hooks.ServerConnect.Deregister(plugin, Native); ServerApi.Hooks.ServerConnect.Deregister(plugin, Root);
            plugin.Dispose(); await plugin.ShutdownCompletion;
            ServerTShock.Bans = oldBans; ServerTShock.Whitelist = oldWhitelist; ServerTShock.Geo = oldGeo; ServerTShock.ShuttingDown = oldShuttingDown;
        }
    }
}
