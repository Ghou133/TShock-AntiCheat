using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using CompatibilityAudit;
using MonoMod.RuntimeDetour;
using NUnit.Framework;
using OTAPI;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI.Sockets;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M10ConnectionAdmissionTests
{
    private static readonly Type NetHooks = typeof(ServerApi).Assembly
        .GetType("TerrariaApi.Server.Hooking.NetHooks", throwOnError: true)!;
    private RemoteClient[] previousClients = null!;
    private MessageBuffer[] previousBuffers = null!;
    private Player[] previousPlayers = null!;
    private ISocket? previousListener;
    private int previousMaxPlayers, previousNetMode;
    private bool previousDisconnect, previousAutoShutdown, previousFullyConnected;
    private EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs> accept = null!;
    private EventHandler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs> update = null!;

    [SetUp]
    public void SetUp()
    {
        previousClients = (RemoteClient[])Netplay.Clients.Clone();
        previousBuffers = (MessageBuffer[])NetMessage.buffer.Clone();
        previousPlayers = (Player[])Main.player.Clone();
        previousListener = Netplay.TcpListener;
        previousMaxPlayers = Main.maxNetPlayers; previousNetMode = Main.netMode;
        previousDisconnect = Netplay.Disconnect; previousAutoShutdown = Main.autoShutdown;
        previousFullyConnected = Netplay.HasFullyConnectedClients;
        // Keep the actual native 256-client update loop. Only its isolated world inputs change.
        for (int slot = 0; slot < Netplay.Clients.Length; slot++)
        {
            Netplay.Clients[slot] = new RemoteClient { Id = slot, ReadBuffer = new byte[1024] };
            NetMessage.buffer[slot] = new MessageBuffer();
        }
        Main.player[0] = new Player { active = false };
        Main.maxNetPlayers = 32; Main.netMode = 2; Main.autoShutdown = false;
        Netplay.Disconnect = false; Netplay.TcpListener = null!;
        accept = Handler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs>("OnConnectionAccepted");
        update = Handler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs>("OnUpdateConnectedClients");
        Admission("DetachAdmission"); Admission("AttachAdmission");
        HookEvents.Terraria.Netplay.OnConnectionAccepted += accept;
        HookEvents.Terraria.Netplay.UpdateConnectedClients += update;
        Hooks.NetMessage.SendData += SuppressTransportOutput;
        Assert.That(Pending, Is.Zero);
    }

    [TearDown]
    public void TearDown()
    {
        Admission("DetachAdmission");
        Hooks.NetMessage.SendData -= SuppressTransportOutput;
        HookEvents.Terraria.Netplay.OnConnectionAccepted -= accept;
        HookEvents.Terraria.Netplay.UpdateConnectedClients -= update;
        for (int slot = 0; slot < previousClients.Length; slot++)
        {
            Netplay.Clients[slot] = previousClients[slot];
            NetMessage.buffer[slot] = previousBuffers[slot];
        }
        for (int slot = 0; slot < previousPlayers.Length; slot++) Main.player[slot] = previousPlayers[slot];
        Netplay.TcpListener = previousListener!; Main.maxNetPlayers = previousMaxPlayers;
        Main.netMode = previousNetMode; Netplay.Disconnect = previousDisconnect;
        Main.autoShutdown = previousAutoShutdown; Netplay.HasFullyConnectedClients = previousFullyConnected;
    }

    [Test]
    public async Task NormalRealSocketIsAdmittedByTheActualNativeNetworkUpdate()
    {
        using var pair = await Pair.Create();
        var retiring = new FixtureSocket { Connected = false };
        var client = Netplay.Clients[0]; client.Socket = retiring; client.State = 7;
        Netplay.OnConnectionAccepted(pair.Socket);
        Assert.That(client.Socket, Is.SameAs(retiring), "The accept callback must not reset or rebind a slot.");
        Assert.That(client.State, Is.EqualTo(7));
        Assert.That(Pending, Is.EqualTo(1));
        Netplay.UpdateConnectedClients();
        Assert.That(client.Socket, Is.SameAs(pair.Socket));
        Assert.That(client.State, Is.Zero);
        Assert.That(client.IsActive, Is.True, "The real native loop must run after admission.");
        Assert.That(client.PendingTermination, Is.False);
        Assert.That(retiring.CloseCalls, Is.EqualTo(1));
        Assert.That(((ISocket)pair.Socket).IsConnected(), Is.True);
        Assert.That(Pending, Is.Zero);
        Retain("normal-real-socket", new { pending = Pending, client.State, client.IsActive, retiring.CloseCalls });
    }

    [Test]
    public async Task AcceptDuringTheActualOldNativeResetWaitsForTheNextNetworkIteration()
    {
        using var old = await Pair.Create(); using var replacement = await Pair.Create();
        var client = Netplay.Clients[0]; client.Socket = old.Socket;
        client.IsActive = true; client.PendingTermination = true; client.PendingTerminationApproved = true;
        client.Kicked = true; client.IsAnnouncementCompleted = false;
        ((ISocket)old.Socket).Close();
        using var paused = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int resets = 0, oldResetThread = 0, admissionResetThread = 0;
        void PauseFirstReset(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        {
            if (!ReferenceEquals(target, client)) return;
            if (Interlocked.Increment(ref resets) != 1)
            { admissionResetThread = Environment.CurrentManagedThreadId; return; }
            oldResetThread = Environment.CurrentManagedThreadId; paused.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Native old reset was not released.");
        }
        HookEvents.Terraria.RemoteClient.Reset += PauseFirstReset;
        Task? retiring = null;
        try
        {
            retiring = Task.Run(Netplay.UpdateConnectedClients);
            Assert.That(paused.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Run(() => Netplay.OnConnectionAccepted(replacement.Socket)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.SameAs(old.Socket));
            Assert.That(Pending, Is.EqualTo(1));
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True);
            release.Set(); await retiring.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.Null, "Old native cleanup must finish before the new socket is assigned.");
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True);
            Assert.That(Pending, Is.EqualTo(1));
            int updateThread = Environment.CurrentManagedThreadId;
            Netplay.UpdateConnectedClients();
            Assert.That(client.Socket, Is.SameAs(replacement.Socket));
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True);
            Assert.That(client.PendingTermination, Is.False);
            Assert.That(client.IsActive, Is.True);
            Assert.That(Pending, Is.Zero);
            Assert.That(resets, Is.EqualTo(2));
            Assert.That(admissionResetThread, Is.EqualTo(updateThread));
            Retain("old-native-reset-defers-new-binding", new
            { oldResetThread, admissionResetThread, updateThread, resets, pending = Pending, replacementSurvived = true });
        }
        finally
        {
            release.Set();
            try { if (retiring is not null) await retiring.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { HookEvents.Terraria.RemoteClient.Reset -= PauseFirstReset; }
        }
    }

    [Test]
    public void QueueOverflowClosesOnlyUnownedExcessAndEachNativeUpdateHasABoundedAdmissionBatch()
    {
        Assert.That(Constant("AdmissionQueueCapacity"), Is.EqualTo(256));
        Assert.That(Constant("AdmissionsPerUpdate"), Is.EqualTo(16));
        var sockets = Enumerable.Range(0, 257).Select(_ => new FixtureSocket()).ToArray();
        foreach (var socket in sockets) Netplay.OnConnectionAccepted(socket);
        Assert.That(Pending, Is.EqualTo(256));
        Assert.That(sockets.Take(256).All(x => x.CloseCalls == 0), Is.True);
        Assert.That(sockets[256].CloseCalls, Is.EqualTo(1));
        Assert.That(Netplay.Clients.All(x => x.Socket is null), Is.True);
        Netplay.UpdateConnectedClients();
        Assert.That(Pending, Is.EqualTo(240));
        for (int slot = 0; slot < 16; slot++) Assert.That(Netplay.Clients[slot].Socket, Is.SameAs(sockets[slot]));
        Assert.That(Netplay.Clients.Skip(16).All(x => x.Socket is null), Is.True);
        Admission("DetachAdmission");
        Assert.That(Pending, Is.Zero);
        Assert.That(sockets.Take(16).All(x => x.CloseCalls == 0), Is.True, "Detach cannot close already transferred live sockets.");
        Assert.That(sockets.Skip(16).All(x => x.CloseCalls == 1), Is.True);
        Retain("bounded-queue-and-batch", new { capacity = 256, firstBatch = 16, remainingClosed = 240, excessClosed = 1 });
    }

    [Test]
    public void EarlierAcceptCancellationPreservesItsSocketAndNeverQueuesIt()
    {
        var socket = new FixtureSocket(); int canceled = 0;
        void Cancel(object? _, HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs args)
        { canceled++; args.ContinueExecution = false; }
        HookEvents.Terraria.Netplay.OnConnectionAccepted -= accept;
        HookEvents.Terraria.Netplay.OnConnectionAccepted += Cancel;
        HookEvents.Terraria.Netplay.OnConnectionAccepted += accept;
        try { Netplay.OnConnectionAccepted(socket); }
        finally { HookEvents.Terraria.Netplay.OnConnectionAccepted -= Cancel; }
        Assert.That(canceled, Is.EqualTo(1)); Assert.That(Pending, Is.Zero);
        Assert.That(socket.CloseCalls, Is.Zero);
        Netplay.UpdateConnectedClients();
        Assert.That(Netplay.Clients.All(x => x.Socket is null), Is.True);
    }

    [Test]
    public void EarlierUpdateCancellationPreservesQueuedSocketUntilARealUpdateRuns()
    {
        var socket = new FixtureSocket(); Netplay.OnConnectionAccepted(socket);
        void Cancel(object? _, HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs args)
        { args.ContinueExecution = false; }
        HookEvents.Terraria.Netplay.UpdateConnectedClients -= update;
        HookEvents.Terraria.Netplay.UpdateConnectedClients += Cancel;
        HookEvents.Terraria.Netplay.UpdateConnectedClients += update;
        try { Netplay.UpdateConnectedClients(); }
        finally { HookEvents.Terraria.Netplay.UpdateConnectedClients -= Cancel; }
        Assert.That(Pending, Is.EqualTo(1)); Assert.That(socket.CloseCalls, Is.Zero);
        Assert.That(Netplay.Clients.All(x => x.Socket is null), Is.True);
        Netplay.UpdateConnectedClients();
        Assert.That(Netplay.Clients[0].Socket, Is.SameAs(socket)); Assert.That(Pending, Is.Zero);
    }

    [Test]
    public void DetachClosesEveryPendingSocketDespiteCloseFailureAndRejectsLateAccept()
    {
        var first = new FixtureSocket { ThrowOnClose = true }; var second = new FixtureSocket();
        Netplay.OnConnectionAccepted(first); Netplay.OnConnectionAccepted(second);
        Assert.DoesNotThrow(() => Admission("DetachAdmission"));
        Assert.That(first.CloseCalls, Is.EqualTo(1)); Assert.That(second.CloseCalls, Is.EqualTo(1));
        Assert.That(Pending, Is.Zero);
        var late = new FixtureSocket(); Netplay.OnConnectionAccepted(late);
        Assert.That(late.CloseCalls, Is.EqualTo(1)); Assert.That(Pending, Is.Zero);
        Admission("DetachAdmission");
        Assert.That(first.CloseCalls, Is.EqualTo(1)); Assert.That(second.CloseCalls, Is.EqualTo(1));
        Admission("AttachAdmission");
        var fresh = new FixtureSocket(); Netplay.OnConnectionAccepted(fresh); Netplay.UpdateConnectedClients();
        Assert.That(Netplay.Clients[0].Socket, Is.SameAs(fresh));
        Assert.That(fresh.CloseCalls, Is.Zero);
    }

    [Test]
    public async Task ConcurrentDetachDuringNativeAdmissionResetPreventsLateSocketCommit()
    {
        var socket = new FixtureSocket(); Netplay.OnConnectionAccepted(socket);
        using var paused = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        void PauseReset(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        {
            if (!ReferenceEquals(target, Netplay.Clients[0])) return;
            paused.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Admission reset was not released.");
        }
        HookEvents.Terraria.RemoteClient.Reset += PauseReset;
        Task? draining = null;
        try
        {
            draining = Task.Run(Netplay.UpdateConnectedClients);
            Assert.That(paused.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(Pending, Is.EqualTo(1), "Dequeued in-flight socket remains owned and bounded until commit.");
            await Task.Run(() => Netplay.OnConnectionAccepted(socket)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Pending, Is.EqualTo(1)); Assert.That(socket.CloseCalls, Is.Zero);
            await Task.Run(() => Admission("DetachAdmission")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(socket.CloseCalls, Is.EqualTo(1)); Assert.That(Pending, Is.Zero);
            release.Set(); await draining.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Netplay.Clients.All(x => x.Socket is null), Is.True);
            Assert.That(socket.CloseCalls, Is.EqualTo(1));
            Retain("detach-versus-native-reset", new { lateCommit = false, socket.CloseCalls, pending = Pending });
        }
        finally
        {
            release.Set();
            try { if (draining is not null) await draining.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { HookEvents.Terraria.RemoteClient.Reset -= PauseReset; }
        }
    }

    [Test]
    public void OneAdmissionResetFailureClosesOnlyItsOwnedSocketAndKeepsTheNativeNetworkLoopRunning()
    {
        var first = new FixtureSocket(); var next = new FixtureSocket();
        Netplay.OnConnectionAccepted(first); Netplay.OnConnectionAccepted(next);
        int resets = 0;
        var failure = new InvalidOperationException("controlled-native-reset-failure");
        void ThrowReset(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        { if (ReferenceEquals(target, Netplay.Clients[0]) && ++resets == 1) throw failure; }
        HookEvents.Terraria.RemoteClient.Reset += ThrowReset;
        try { Assert.DoesNotThrow(Netplay.UpdateConnectedClients); }
        finally { HookEvents.Terraria.RemoteClient.Reset -= ThrowReset; }
        Assert.That(first.CloseCalls, Is.EqualTo(1)); Assert.That(next.CloseCalls, Is.Zero);
        Assert.That(Netplay.Clients[0].Socket, Is.SameAs(next));
        Assert.That(Netplay.Clients[0].IsActive, Is.True, "The actual native loop continues after an admission failure.");
        Assert.That(Pending, Is.Zero); Assert.That(next.CloseCalls, Is.Zero);
        Assert.That(resets, Is.EqualTo(2));
    }

    [Test]
    public void NativeMainLoopFailureStillPropagatesTheOriginalException()
    {
        var client = Netplay.Clients[0];
        client.Socket = new FixtureSocket { Connected = false };
        client.PendingTermination = true; client.PendingTerminationApproved = true; client.Kicked = true;
        var failure = new InvalidOperationException("controlled-original-loop-failure");
        void ThrowReset(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        { if (ReferenceEquals(target, client)) throw failure; }
        HookEvents.Terraria.RemoteClient.Reset += ThrowReset;
        try { Assert.That(Assert.Throws<InvalidOperationException>(Netplay.UpdateConnectedClients), Is.SameAs(failure)); }
        finally { HookEvents.Terraria.RemoteClient.Reset -= ThrowReset; }
        Assert.That(Pending, Is.Zero);
    }

    [Test]
    public void EarlierNativeResetCancellationCannotBeMistakenForACompletedCleanSlot()
    {
        var stale = new FixtureSocket { Connected = false }; var incoming = new FixtureSocket();
        var client = Netplay.Clients[0]; client.Socket = stale; client.State = 7;
        void CancelReset(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        { if (ReferenceEquals(target, client)) args.ContinueExecution = false; }
        HookEvents.Terraria.RemoteClient.Reset += CancelReset;
        try { Netplay.OnConnectionAccepted(incoming); Netplay.UpdateConnectedClients(); }
        finally { HookEvents.Terraria.RemoteClient.Reset -= CancelReset; }
        Assert.That(client.Socket, Is.SameAs(stale)); Assert.That(client.State, Is.EqualTo(7));
        Assert.That(stale.CloseCalls, Is.Zero); Assert.That(incoming.CloseCalls, Is.EqualTo(1));
        Assert.That(Pending, Is.Zero);
    }

    [Test]
    public void DuplicateQueuedAndBoundSocketNotificationsNeverCloseTheLegitimateSocket()
    {
        var socket = new FixtureSocket();
        Netplay.OnConnectionAccepted(socket); Netplay.OnConnectionAccepted(socket);
        Assert.That(Pending, Is.EqualTo(1)); Assert.That(socket.CloseCalls, Is.Zero);
        Netplay.UpdateConnectedClients();
        Netplay.OnConnectionAccepted(socket); Netplay.UpdateConnectedClients();
        Assert.That(Pending, Is.Zero); Assert.That(socket.CloseCalls, Is.Zero);
        Assert.That(Netplay.Clients.Count(x => ReferenceEquals(x.Socket, socket)), Is.EqualTo(1));
    }

    [Test]
    public void FullServerClosesOnlyTheUnassignedSocketAndPreservesTheExistingClient()
    {
        Main.maxNetPlayers = 1;
        var existing = new FixtureSocket(); var incoming = new FixtureSocket();
        var listener = new FixtureSocket(); Netplay.TcpListener = listener;
        Netplay.Clients[0].Socket = existing;
        Netplay.OnConnectionAccepted(incoming); Netplay.UpdateConnectedClients();
        Assert.That(Netplay.Clients[0].Socket, Is.SameAs(existing));
        Assert.That(existing.CloseCalls, Is.Zero); Assert.That(incoming.CloseCalls, Is.EqualTo(1));
        Assert.That(listener.StopCalls, Is.GreaterThanOrEqualTo(1)); Assert.That(Pending, Is.Zero);
    }

    [TestCase(0)]
    [TestCase(3)]
    public async Task M11_ControlledOldReadAcrossActualRetirementAndQueuedAdmissionRetainsItsActualTarget(int length)
    {
        // H1 only: the old real read has already completed at the socket layer and is
        // paused immediately before the native RemoteClient callback. Unlike the M10
        // manual Bind counterexample, both bindings below use the accepted H2 queue.
        using var old = await Pair.Create(); using var replacement = await Pair.Create();
        var client = Netplay.Clients[0];
        using var observer = new M10ConnectionLifecycleDiagnostics(() => Path.GetTempPath());
        observer.Install();
        Netplay.OnConnectionAccepted(old.Socket); Netplay.UpdateConnectedClients();
        Assert.That(client.Socket, Is.SameAs(old.Socket));
        var oldReadBuffer = client.ReadBuffer;
        var sharedMessageBuffer = NetMessage.buffer[0];
        using var paused = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackEntries = 0; bool barrierTimedOut = false;
        byte[]? callbackBuffer = null; ISocket? callbackSocket = null;
        var method = typeof(RemoteClient).GetMethod(nameof(RemoteClient.ServerReadCallBack), [typeof(object), typeof(int)])!;
        void Around(Action<RemoteClient, object, int> original, RemoteClient target, object state, int received)
        {
            if (!ReferenceEquals(target, client) || Interlocked.Increment(ref callbackEntries) != 1)
            { original(target, state, received); return; }
            paused.Set();
            try
            {
                barrierTimedOut = !release.Wait(TimeSpan.FromSeconds(5));
                callbackBuffer = target.ReadBuffer; callbackSocket = target.Socket;
                original(target, state, received);
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        }
        using var pause = new Hook(method, (Action<Action<RemoteClient, object, int>, RemoteClient, object, int>)Around);
        try
        {
            ((ISocket)old.Socket).AsyncReceive(oldReadBuffer, 0, oldReadBuffer.Length, client.ServerReadCallBack, null!);
            if (length == 0) old.Peer.Client.Shutdown(SocketShutdown.Send);
            else await old.Peer.GetStream().WriteAsync(new byte[] { 3, 0, 154 });
            Assert.That(paused.Wait(TimeSpan.FromSeconds(5)), Is.True, "The actual old callback must be suspended before retirement.");

            // A separately approved kick/retirement may complete while a read is in flight.
            // These are explicit fixture inputs, not an observed natural kick or exploit.
            ((ISocket)old.Socket).Close();
            client.PendingTermination = true; client.PendingTerminationApproved = true;
            client.Kicked = true; client.IsActive = true;
            Netplay.UpdateConnectedClients();
            Assert.That(client.Socket, Is.Null, "The actual old native Reset completed.");
            Netplay.OnConnectionAccepted(replacement.Socket);
            Assert.That(Pending, Is.EqualTo(1));
            Netplay.UpdateConnectedClients();
            Assert.That(client.Socket, Is.SameAs(replacement.Socket));
            Assert.That(client.PendingTermination, Is.False);
            Assert.That(Pending, Is.Zero);
            int totalBefore = NetMessage.buffer[0].totalData;
            release.Set(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(barrierTimedOut, Is.False);
            Assert.That(callbackEntries, Is.EqualTo(1));
            Assert.That(callbackSocket, Is.SameAs(replacement.Socket));
            Assert.That(Netplay.Clients[0], Is.SameAs(client));
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True, "H2 did not close the replacement socket.");
            if (length == 0) Assert.That(client.PendingTermination, Is.True, "Controlled H1 witness; this is not a repair.");
            else Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(totalBefore + length));
            Retain("m11-old-read-across-real-queue-" + length, new
            {
                evidenceLayer = "controlled-real-socket-native-callback-and-native-network-loop",
                oldLength = length, callbackEntries, barrierTimedOut,
                callbackTargetIsReusedClient = ReferenceEquals(Netplay.Clients[0], client),
                oldAndCallbackReadBufferSame = ReferenceEquals(oldReadBuffer, callbackBuffer),
                oldAndCurrentMessageBufferSame = ReferenceEquals(sharedMessageBuffer, NetMessage.buffer[0]),
                callbackSocketIsNew = ReferenceEquals(callbackSocket, replacement.Socket),
                totalBefore, totalAfter = NetMessage.buffer[0].totalData,
                receivedBytes = NetMessage.buffer[0].readBuffer.Take(NetMessage.buffer[0].totalData).ToArray(),
                client.PendingTermination, newSocketConnected = ((ISocket)replacement.Socket).IsConnected(),
                authenticatedSessionEstablished = false, historicalFailureAttribution = false,
                fixture = "old callback paused by barrier; separately approved retirement flags; no timing probability claim",
                snapshot = observer.Snapshot()
            });
        }
        finally
        {
            release.Set();
            if (paused.IsSet) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static int Pending => (int)NetHooks.GetProperty("PendingAdmissionCount", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
    private static int Constant(string name) => (int)NetHooks.GetField(name, BindingFlags.Static | BindingFlags.Public)!.GetRawConstantValue()!;
    private static void Admission(string method) => ((Action)NetHooks.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate(typeof(Action)))();
    private static EventHandler<T> Handler<T>(string name) =>
        (EventHandler<T>)NetHooks.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate(typeof(EventHandler<T>));
    private static void SuppressTransportOutput(object? _, Hooks.NetMessage.SendDataEventArgs args) => args.Result = HookResult.Cancel;
    private static void Retain(string name, object result)
    {
        string? root = Environment.GetEnvironmentVariable("ANTICHEAT_M10_LIFECYCLE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        string target = Path.Combine(root, "admission-" + name); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "result.json"), JsonSerializer.Serialize(new
        {
            name, utc = DateTimeOffset.UtcNow,
            scope = "actual-tsapi-handlers-native-update-and-reset; real-loopback-only-where-named-real-socket",
            runtime = Identity(typeof(RemoteClient).Assembly), tsapi = Identity(typeof(ServerApi).Assembly),
            socketProvider = Identity(typeof(LinuxTcpSocket).Assembly), result
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Identity(Assembly assembly) => new { path = assembly.Location,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))) };
    private sealed class FixtureSocket : ISocket
    {
        public bool Connected = true, ThrowOnClose;
        public int CloseCalls, StopCalls;
        public void Close()
        {
            CloseCalls++; Connected = false;
            if (ThrowOnClose) throw new IOException("controlled-close-failure");
        }
        public bool IsConnected() => Connected;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(IPAddress.Loopback, 17160);
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) { }
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() => StopCalls++;
    }
    private sealed class Pair(TcpClient peer, LinuxTcpSocket socket) : IDisposable
    {
        public TcpClient Peer { get; } = peer;
        public LinuxTcpSocket Socket { get; } = socket;
        public static async Task<Pair> Create()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var peer = new TcpClient();
            try
            {
                var pending = listener.AcceptTcpClientAsync();
                await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new(peer, new(await pending.WaitAsync(TimeSpan.FromSeconds(5))));
            }
            catch { peer.Dispose(); throw; }
            finally { listener.Stop(); }
        }
        public void Dispose() { Peer.Dispose(); ((ISocket)Socket).Close(); }
    }
}
