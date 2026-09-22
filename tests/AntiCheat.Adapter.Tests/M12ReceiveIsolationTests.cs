using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
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
public sealed class M12ReceiveIsolationTests
{
    private static readonly Type NetHooks = typeof(ServerApi).Assembly.GetType("TerrariaApi.Server.Hooking.NetHooks", true)!;
    private RemoteClient[] clients = null!;
    private MessageBuffer[] buffers = null!;
    private Player[] players = null!;
    private ISocket? listener;
    private bool disconnect, autoShutdown, fullyConnected, dedicated;
    private int maxPlayers, netMode;
    private EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs> accept = null!;
    private EventHandler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs> update = null!;

    [SetUp]
    public void SetUp()
    {
        if (NetHooks.GetMethod("AttachReceiveIsolation") is null)
            Assert.Fail("M12 repair tests require the newly built runtime; old defect witness is a separate test.");
        clients = (RemoteClient[])Netplay.Clients.Clone(); buffers = (MessageBuffer[])NetMessage.buffer.Clone();
        players = (Player[])Main.player.Clone(); listener = Netplay.TcpListener;
        disconnect = Netplay.Disconnect; autoShutdown = Main.autoShutdown; fullyConnected = Netplay.HasFullyConnectedClients;
        maxPlayers = Main.maxNetPlayers; netMode = Main.netMode; dedicated = Main.dedServ;
        for (int i = 0; i < Netplay.Clients.Length; i++)
        {
            Netplay.Clients[i] = new RemoteClient { Id = i, ReadBuffer = new byte[1024] };
            NetMessage.buffer[i] = new MessageBuffer { whoAmI = i };
        }
        Main.player[0] = new Player(); Main.maxNetPlayers = 32; Main.netMode = 2;
        Main.dedServ = true; Main.autoShutdown = false; Netplay.Disconnect = false; Netplay.TcpListener = null!;
        accept = Handler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs>("OnConnectionAccepted");
        update = Handler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs>("OnUpdateConnectedClients");
        Call("DetachAdmission"); Call("AttachReceiveIsolation"); Call("AttachAdmission");
        HookEvents.Terraria.Netplay.OnConnectionAccepted += accept;
        HookEvents.Terraria.Netplay.UpdateConnectedClients += update;
        Hooks.NetMessage.SendData += Suppress;
    }

    [TearDown]
    public void TearDown()
    {
        Call("DetachAdmission"); Call("DetachReceiveIsolation");
        HookEvents.Terraria.Netplay.OnConnectionAccepted -= accept;
        HookEvents.Terraria.Netplay.UpdateConnectedClients -= update;
        Hooks.NetMessage.SendData -= Suppress;
        for (int i = 0; i < clients.Length; i++) { Netplay.Clients[i] = clients[i]; NetMessage.buffer[i] = buffers[i]; }
        for (int i = 0; i < players.Length; i++) Main.player[i] = players[i];
        Netplay.TcpListener = listener!; Netplay.Disconnect = disconnect; Main.autoShutdown = autoShutdown;
        Netplay.HasFullyConnectedClients = fullyConnected; Main.maxNetPlayers = maxPlayers; Main.netMode = netMode; Main.dedServ = dedicated;
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(-1)]
    public async Task OldActualReadCrossesNativeResetAndAdmissionWithoutMutatingTheNewBinding(int outcome)
    {
        using var old = await SocketPair.Create(); using var next = await SocketPair.Create();
        var client = Netplay.Clients[0];
        Admit(old.Socket); old.Socket.ForceRead = true;
        client.TryRead();
        Assert.That(old.Socket.ReadSubmitted, Is.True);
        Assert.That(old.Socket.IoBuffer, Is.Not.SameAs(client.ReadBuffer), "I/O must not write the reused array even before callback entry.");
        if (outcome > 0) await old.Peer.GetStream().WriteAsync(new byte[] { 3, 0, 154 });
        else if (outcome == 0) old.Peer.Client.Shutdown(SocketShutdown.Send);
        else ((ISocket)old.Socket.Inner).Close();
        await old.Socket.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(old.Socket.CompletedLength, Is.EqualTo(outcome));
        client.PendingTermination = true; client.PendingTerminationApproved = true; client.Kicked = true;
        Netplay.UpdateConnectedClients(); Assert.That(client.Socket, Is.Null);
        Admit(next.Socket); Assert.That(client.PendingTermination, Is.False);
        Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        int staleHookEntries = 0;
        void ObserveCallback(RemoteClient target, HookEvents.Terraria.RemoteClient.ServerReadCallBackEventArgs args)
        { if (ReferenceEquals(target, client)) staleHookEntries++; }
        HookEvents.Terraria.RemoteClient.ServerReadCallBack += ObserveCallback;
        try { await old.Socket.ReleaseAsync(); }
        finally { HookEvents.Terraria.RemoteClient.ServerReadCallBack -= ObserveCallback; }
        Assert.That(staleHookEntries, Is.Zero, "Stale transport results must be rejected before outer slot/account observers.");
        Assert.That(client.Socket, Is.SameAs(next.Socket)); Assert.That(client.PendingTermination, Is.False);
        Assert.That(client._isReading, Is.False); Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        Assert.That(((ISocket)next.Socket).IsConnected(), Is.True);
        next.Socket.Pause = false;
        await next.Peer.GetStream().WriteAsync(new byte[] { 3, 0, 154 });
        await next.Socket.ReadAvailable(); client.TryRead();
        await next.Socket.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(NetMessage.buffer[0].readBuffer.Take(3), Is.EqualTo(new byte[] { 3, 0, 154 }));
        Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(3));
        Retain("old-operation-" + outcome, new { outcome, newPendingTermination = client.PendingTermination,
            newTotalData = NetMessage.buffer[0].totalData, privateIoBuffer = true,
            source = "real-loopback-EndRead; actual Reset and M10 queued admission; provider release barrier",
            snapshot = Snapshot() });
    }

    [Test, Order(-1000)]
    public void FailedDiagnosticWriterCannotStrandAFailedSubmissionOrPreventRetirement()
    {
        var writer = Console.Error;
        try
        {
            Console.SetError(new ThrowingWriter());
            var socket = new ThrowingReceiveSocket();
            Assert.DoesNotThrow(() => Admit(socket));
            Assert.That(Netplay.Clients[0].PendingTermination, Is.True);
            Assert.That(Netplay.Clients[0]._isReading, Is.False);
            var snapshot = Snapshot();
            Assert.That(snapshot.GetProperty("Outstanding").GetInt32(), Is.Zero);
            Assert.That(snapshot.GetProperty("FirstFailureType").GetString(), Is.EqualTo(typeof(IOException).FullName));
            Assert.That(snapshot.GetProperty("DiagnosticFailures").GetInt64(), Is.EqualTo(1));
            Assert.That(snapshot.GetProperty("FirstDiagnosticFailureType").GetString(), Is.EqualTo(typeof(IOException).FullName));
        }
        finally { Console.SetError(writer); }
        Retire(); var next = new ManualSocket(); Admit(next); next.Complete([3, 0, 154]);
        Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(3));
        Assert.That(Netplay.Clients[0].PendingTermination, Is.False);
        Retain("broken-diagnostic-does-not-strand-operation", new { recovered = true, snapshot = Snapshot() });
    }

    [Test, Order(-900)]
    public async Task EndReadFaultInjectionCompletesTheRealOperationEvenWhenTheTShockLogSinkThrows()
    {
        var previousLog = TShockAPI.TShock.Log;
        var log = DispatchProxy.Create<TShockAPI.ILog, ThrowingLogProxy>();
        var proxy = (ThrowingLogProxy)(object)log;
        using var pair = await SocketPair.Create();
        pair.Socket.Pause = false;
        var ownedStream = ((LinuxTcpSocket)pair.Socket.Inner)._connection.GetStream();
        int Fault(Func<NetworkStream, IAsyncResult, int> original, NetworkStream stream, IAsyncResult result)
        {
            int length = original(stream, result); // Consume the real async result first.
            if (ReferenceEquals(stream, ownedStream)) throw new IOException("owned deterministic EndRead fault injection");
            return length;
        }
        using var endReadFault = new Hook(typeof(NetworkStream).GetMethod(nameof(NetworkStream.EndRead))!,
            (Func<Func<NetworkStream, IAsyncResult, int>, NetworkStream, IAsyncResult, int>)Fault);
        try
        {
            TShockAPI.TShock.Log = log;
            Admit(pair.Socket); pair.Socket.ForceRead = true; Netplay.Clients[0].TryRead();
            Assert.That(pair.Socket.ReadSubmitted, Is.True);
            await pair.Peer.GetStream().WriteAsync(new byte[] { 1 });
            await pair.Socket.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await proxy.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(SpinWait.SpinUntil(() => LinuxTcpSocket.ReceiveFaults.DiagnosticFailures > 0, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(pair.Socket.CompletedLength, Is.EqualTo(-1));
            Assert.That(Snapshot().GetProperty("Outstanding").GetInt32(), Is.Zero);
            Assert.That(Netplay.Clients[0]._isReading, Is.False);
            Assert.That(Netplay.Clients[0].PendingTermination, Is.True);
            Assert.That(LinuxTcpSocket.ReceiveFaults.FirstIoFailure, Is.EqualTo(typeof(IOException).FullName));
            Assert.That(LinuxTcpSocket.ReceiveFaults.FirstDiagnosticFailure, Is.EqualTo(typeof(IOException).FullName));
            Retain("endread-fault-with-broken-tshock-log", new { evidenceLayer = "real-loopback-BeginRead-EndRead-plus-controlled-method-fault-injection",
                provider = LinuxTcpSocket.ReceiveFaults, receive = Snapshot() });
        }
        finally { TShockAPI.TShock.Log = previousLog; }
    }

    [Test]
    public void OldInFlightIoCannotOverwriteNewBytesAndCannotSpliceAnOldPartialFrame()
    {
        var old = new ManualSocket(); var next = new ManualSocket(); var client = Netplay.Clients[0];
        Admit(old); client.TryRead();
        old.Complete([5, 0]);
        NetMessage.CheckBytes(0); Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(2));
        client.TryRead(); var oldIo = old.IoBuffer;
        Retire(); Admit(next); client.TryRead();
        next.Complete([3, 0, 154]);
        old.Complete([1, 2, 3]);
        Assert.That(oldIo, Is.Not.SameAs(next.IoBuffer));
        Assert.That(NetMessage.buffer[0].readBuffer.Take(3), Is.EqualTo(new byte[] { 3, 0, 154 }));
        var types = new List<int>();
        void Capture(object? _, Hooks.MessageBuffer.GetDataEventArgs args)
        { types.Add(args.PacketId); args.Result = HookResult.Cancel; }
        Hooks.MessageBuffer.GetData += Capture;
        try { NetMessage.CheckBytes(0); }
        finally { Hooks.MessageBuffer.GetData -= Capture; }
        Assert.That(types, Is.EqualTo(new[] { 154 }));
        Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        Assert.That(client.PendingTermination, Is.False);
        Retain("private-io-and-no-cross-binding-framing", new { dispatched = types, oldPartialDiscarded = true, snapshot = Snapshot() });
    }

    [Test]
    public async Task ResetDuringNativeReceiveCommitDefersWithoutHoldingAMetadataLockAcrossHooks()
    {
        var old = new ManualSocket(); var next = new ManualSocket(); var client = Netplay.Clients[0];
        Admit(old); client.TryRead();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        void Pause(object? _, HookEvents.Terraria.NetMessage.ReceiveBytesEventArgs args)
        { if (args.i == 0) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } }
        HookEvents.Terraria.NetMessage.ReceiveBytes += Pause;
        Task? completion = null;
        try
        {
            completion = Task.Run(() => old.Complete([3, 0, 154]));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Run(client.Reset).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.SameAs(old)); Assert.That(old.CloseCalls, Is.Zero);
            Assert.That(client.PendingTerminationApproved, Is.True);
            release.Set(); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Netplay.UpdateConnectedClients(); Assert.That(client.Socket, Is.Null);
            Admit(next); Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
            Assert.That(client.PendingTermination, Is.False); Assert.That(next.CloseCalls, Is.Zero);
        }
        finally { release.Set(); if (completion is not null) await completion.WaitAsync(TimeSpan.FromSeconds(5)); HookEvents.Terraria.NetMessage.ReceiveBytes -= Pause; }
        Retain("reset-during-native-buffer-commit", new { noNewBindingBeforeCommitReturns = true, snapshot = Snapshot() });
    }

    [Test]
    public async Task NativeFrameDispatchRetainsItsSourceWhileResetAndAdmissionAreRequested()
    {
        var old = new ManualSocket(); var next = new ManualSocket(); var client = Netplay.Clients[0];
        Admit(old); client.TryRead(); old.Complete([3, 0, 154, 3, 0, 154]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var seen = new List<ISocket>();
        void Consume(object? _, Hooks.MessageBuffer.GetDataEventArgs args)
        {
            args.Result = HookResult.Cancel; seen.Add(client.Socket);
            if (seen.Count == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
        }
        Hooks.MessageBuffer.GetData += Consume;
        Task? dispatch = null;
        try
        {
            dispatch = Task.Run(() => NetMessage.CheckBytes(0));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Run(client.Reset).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.SameAs(old));
            old.Connected = false;
            Netplay.OnConnectionAccepted(next);
            release.Set(); await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(seen, Is.EqualTo(new ISocket[] { old, old }));
            Netplay.UpdateConnectedClients();
            Assert.That(client.Socket, Is.SameAs(next));
            Assert.That(client.PendingTermination, Is.False); Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        }
        finally { release.Set(); if (dispatch is not null) await dispatch.WaitAsync(TimeSpan.FromSeconds(5)); Hooks.MessageBuffer.GetData -= Consume; }
        Retain("native-dispatch-source-lease", new { oldFrames = seen.Count, newBindingSurvived = true, snapshot = Snapshot() });
    }

    [Test]
    public void LegalFragmentationAndCoalescingRetainOrderAndOwnEofStillTerminates()
    {
        var socket = new ManualSocket(); var client = Netplay.Clients[0]; Admit(socket);
        var types = new List<int>();
        void Capture(object? _, Hooks.MessageBuffer.GetDataEventArgs args) { types.Add(args.PacketId); args.Result = HookResult.Cancel; }
        Hooks.MessageBuffer.GetData += Capture;
        try
        {
            foreach (byte[] fragment in new byte[][] { [3], [0], [154, 3, 0, 155] })
            { client.TryRead(); socket.Complete(fragment); NetMessage.CheckBytes(0); }
        }
        finally { Hooks.MessageBuffer.GetData -= Capture; }
        Assert.That(types, Is.EqualTo(new[] { 154, 155 })); Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        client.TryRead(); socket.Complete([]); Assert.That(client.PendingTermination, Is.True);
        Assert.That(Snapshot().GetProperty("Outstanding").GetInt32(), Is.Zero);
    }

    [Test]
    public void CapacityIncludesRetiredInFlightOperationsAndOnlyCompletionReleasesThem()
    {
        var pending = new List<ManualSocket>(); var client = Netplay.Clients[0];
        for (int i = 0; i < 512; i++)
        {
            var socket = new ManualSocket(); pending.Add(socket); Admit(socket); client.TryRead(); Retire();
        }
        Assert.That(Snapshot().GetProperty("Outstanding").GetInt32(), Is.EqualTo(512));
        var last = new ManualSocket(); Admit(last); client.TryRead();
        Assert.That(last.IoBuffer, Is.Null); Assert.That(client.PendingTermination, Is.True);
        foreach (var socket in pending) socket.Complete([3, 0, 154]);
        Assert.That(Snapshot().GetProperty("Outstanding").GetInt32(), Is.Zero);
        Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        Retire(); var recovered = new ManualSocket(); Admit(recovered); client.TryRead();
        recovered.Complete([3, 0, 154]); Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(3));
        Assert.That(client.PendingTermination, Is.False);
        Retain("bounded-retired-operations", new { capacity = 512, completionReleasesOnlyOwnBuffer = true, snapshot = Snapshot() });
    }

    [Test]
    public void DisposeAndReinstallCannotRouteAnOldOperationBackToNativeOrNewHooks()
    {
        var old = new ManualSocket(); var next = new ManualSocket(); var client = Netplay.Clients[0];
        Admit(old); client.TryRead();
        Call("DetachReceiveIsolation");
        // The old stable callback executes while the runtime detour is absent.
        client.Reset(); client.Socket = next; client.PendingTermination = false;
        old.Complete([3, 0, 154]);
        Assert.That(client.PendingTermination, Is.False); Assert.That(NetMessage.buffer[0].totalData, Is.Zero);
        client.Socket = null!; Call("AttachReceiveIsolation"); Admit(next);
        client.TryRead(); next.Complete([3, 0, 154]);
        Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(3));
        Retain("dispose-inflight-stable-completion", new { oldDelivery = false, newDelivery = true, snapshot = Snapshot() });
    }

    [Test]
    public void EarlierResetCancellationRetainsCurrentReceiveOwnership()
    {
        var socket = new ManualSocket(); var client = Netplay.Clients[0]; Admit(socket);
        void Cancel(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        { if (ReferenceEquals(target, client)) args.ContinueExecution = false; }
        HookEvents.Terraria.RemoteClient.Reset += Cancel;
        try { client.Reset(); }
        finally { HookEvents.Terraria.RemoteClient.Reset -= Cancel; }
        Assert.That(client.Socket, Is.SameAs(socket)); Assert.That(socket.CloseCalls, Is.Zero);
        client.TryRead(); socket.Complete([3, 0, 154]);
        Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(3)); Assert.That(client.PendingTermination, Is.False);
    }

    [Test]
    public async Task H2AcceptDuringActualOldResetRemainsQueuedWithTheReceiveRepairInstalled()
    {
        var old = new ManualSocket(); var next = new ManualSocket(); var client = Netplay.Clients[0];
        Admit(old); old.Complete([3, 0, 154]); old.Connected = false;
        client.PendingTermination = true; client.PendingTerminationApproved = true; client.Kicked = true;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int resets = 0;
        void Pause(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        {
            if (!ReferenceEquals(target, client) || Interlocked.Increment(ref resets) != 1) return;
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }
        HookEvents.Terraria.RemoteClient.Reset += Pause;
        Task? retirement = null;
        try
        {
            retirement = Task.Run(Netplay.UpdateConnectedClients);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Run(() => Netplay.OnConnectionAccepted(next)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.SameAs(old)); Assert.That(next.CloseCalls, Is.Zero);
            release.Set(); await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.Null); Assert.That(next.CloseCalls, Is.Zero);
            Netplay.UpdateConnectedClients(); Assert.That(client.Socket, Is.SameAs(next));
            next.Complete([3, 0, 154]);
            Assert.That(NetMessage.buffer[0].totalData, Is.EqualTo(3)); Assert.That(client.PendingTermination, Is.False);
        }
        finally { release.Set(); if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(5)); HookEvents.Terraria.RemoteClient.Reset -= Pause; }
        Retain("h2-actual-reset-retains-queued-new-socket", new { fixedPacing = 0, newCloseCalls = next.CloseCalls, snapshot = Snapshot() });
    }

    private static void Admit(ISocket socket) { Netplay.OnConnectionAccepted(socket); Netplay.UpdateConnectedClients(); Assert.That(Netplay.Clients[0].Socket, Is.SameAs(socket)); }
    private static void Retire() { var c = Netplay.Clients[0]; c.PendingTermination = true; c.PendingTerminationApproved = true; c.Kicked = true; Netplay.UpdateConnectedClients(); Assert.That(c.Socket, Is.Null); }
    private static void Call(string method) => NetHooks.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    private static EventHandler<T> Handler<T>(string name) => (EventHandler<T>)NetHooks.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate(typeof(EventHandler<T>));
    private static void Suppress(object? _, Hooks.NetMessage.SendDataEventArgs args) => args.Result = HookResult.Cancel;
    private static JsonElement Snapshot() => JsonSerializer.SerializeToElement(NetHooks.GetProperty("ReceiveSnapshot")!.GetValue(null));
    private static void Retain(string name, object result)
    {
        string root = Environment.GetEnvironmentVariable("ANTICHEAT_M12_R_EVIDENCE") ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "m12-r");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(new { name, utc = DateTimeOffset.UtcNow,
            tsapi = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(ServerApi).Assembly.Location))),
            provider = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(LinuxTcpSocket).Assembly.Location))), result }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private class ManualSocket : ISocket
    {
        public byte[]? IoBuffer; public SocketReceiveCallback? Callback; public object? State;
        public bool Connected = true; public int CloseCalls;
        public void Complete(byte[] bytes) { Array.Copy(bytes, IoBuffer!, bytes.Length); var cb = Callback!; var state = State!; Callback = null; cb(state, bytes.Length); }
        public void Close() { Connected = false; CloseCalls++; }
        public bool IsConnected() => Connected;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(IPAddress.Loopback, 17162);
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) { }
        public virtual void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { IoBuffer = data; Callback = callback; State = state; }
        public bool IsDataAvailable() => true;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() { }
    }

    private sealed class ThrowingReceiveSocket : ManualSocket
    {
        public override void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) =>
            throw new IOException("owned controlled submit failure");
    }
    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("owned controlled diagnostic writer failure");
    }
    public class ThrowingLogProxy : DispatchProxy
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "Error")
            { Attempted.TrySetResult(); throw new IOException("owned controlled TShock log failure"); }
            return null;
        }
    }

    private sealed class SocketPair(TcpClient peer, ControlledSocket socket) : IDisposable
    {
        public TcpClient Peer { get; } = peer; public ControlledSocket Socket { get; } = socket;
        public static async Task<SocketPair> Create()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var peer = new TcpClient();
            try
            {
                var accepted = listener.AcceptTcpClientAsync();
                await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new(peer, new(new LinuxTcpSocket(await accepted.WaitAsync(TimeSpan.FromSeconds(5)))));
            }
            catch { peer.Dispose(); throw; } finally { listener.Stop(); }
        }
        public void Dispose() { Peer.Dispose(); Socket.Close(); }
    }

    private sealed class ControlledSocket(ISocket inner) : ISocket
    {
        public ISocket Inner { get; } = inner;
        public bool ForceRead, Pause = true, ReadSubmitted;
        public byte[]? IoBuffer; public int CompletedLength;
        public TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SocketReceiveCallback? callback; private object? callbackState;
        public Task ReleaseAsync() { callback!(callbackState!, CompletedLength); Delivered.TrySetResult(); return Delivered.Task; }
        public async Task ReadAvailable()
        { await Task.Run(() => { if (!SpinWait.SpinUntil(Inner.IsDataAvailable, TimeSpan.FromSeconds(5))) throw new TimeoutException(); }); }
        public void Close() => Inner.Close(); public bool IsConnected() => Inner.IsConnected();
        public RemoteAddress GetRemoteAddress() => Inner.GetRemoteAddress(); public void Connect(RemoteAddress address) => Inner.Connect(address);
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) => Inner.AsyncSend(data, offset, size, callback, state);
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback completion, object state = null!)
        {
            IoBuffer = data; ReadSubmitted = true; callback = completion; callbackState = state;
            Inner.AsyncReceive(data, offset, size, (_, length) =>
            {
                CompletedLength = length; Arrived.TrySetResult();
                if (!Pause) { completion(state, length); Delivered.TrySetResult(); }
            }, state);
        }
        public bool IsDataAvailable() { if (ForceRead) { ForceRead = false; return true; } return Inner.IsDataAvailable(); }
        public bool StartListening(SocketConnectionAccepted callback) => false; public void StopListening() { }
    }
}
