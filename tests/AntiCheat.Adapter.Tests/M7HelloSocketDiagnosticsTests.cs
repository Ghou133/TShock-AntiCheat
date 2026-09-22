using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using CompatibilityAudit;
using NUnit.Framework;
using Terraria;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI.Sockets;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7HelloSocketDiagnosticsTests
{
    private const int Slot = 15;
    private string directory = null!;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private bool oldDisconnect;
    private M7ConnectionDiagnostics observer = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "anticheat-m7-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot]; oldDisconnect = Netplay.Disconnect;
        Netplay.Disconnect = false;
        NetMessage.buffer[Slot] = new MessageBuffer();
        Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 0, ReadBuffer = new byte[1024] };
        observer = new(() => directory, observeSocketCallbacks: true);
        observer.Install();
        using var result = Snapshot();
        Assert.That(result.RootElement.GetProperty("linuxSocketDetoursInstalled").GetBoolean(), Is.True,
            "The real runtime detours must install; a synthetic observer invocation cannot substitute.");
        Assert.That(result.RootElement.GetProperty("observerFailed").GetBoolean(), Is.False);
    }

    [TearDown]
    public void TearDown()
    {
        observer?.Dispose();
        Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer; Netplay.Disconnect = oldDisconnect;
        Directory.Delete(directory, true);
    }

    [Test]
    public async Task ActualLinuxReadDeliversBytesOnceAndEofFromOldSocketKeepsItsExactSource()
    {
        using var pair = await Pair.Create();
        var socket = pair.Socket; var client = Netplay.Clients[Slot]; client.Socket = socket;
        HookEvents.Terraria.RemoteClient.InvokeTryRead(client, () => { });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int readCallbacks = 0;
        void Seen(RemoteClient target, HookEvents.Terraria.RemoteClient.ServerReadCallBackEventArgs args)
        {
            if (!ReferenceEquals(target, client)) return;
            Interlocked.Increment(ref readCallbacks); received.TrySetResult();
        }
        HookEvents.Terraria.RemoteClient.ServerReadCallBack += Seen;
        try
        {
            byte[] frame = [3, 0, 154];
            ((ISocket)socket).AsyncReceive(client.ReadBuffer, 0, client.ReadBuffer.Length, client.ServerReadCallBack, null!);
            await pair.Peer.GetStream().WriteAsync(frame);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForEvent("linux-read-return");
            Assert.That(NetMessage.buffer[Slot].totalData, Is.EqualTo(frame.Length));
            Assert.That(NetMessage.buffer[Slot].readBuffer.Take(frame.Length), Is.EqualTo(frame));
            Assert.That(readCallbacks, Is.EqualTo(1));
            Assert.That(client.PendingTermination, Is.False);
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int sentCallbacks = 0;
            ((ISocket)socket).AsyncSend(frame, 0, frame.Length, _ =>
            { Interlocked.Increment(ref sentCallbacks); sent.TrySetResult(); }, null!);
            var reply = new byte[frame.Length];
            await pair.Peer.GetStream().ReadExactlyAsync(reply);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForEvent("linux-send-return");
            Assert.That(reply, Is.EqualTo(frame));
            Assert.That(sentCallbacks, Is.EqualTo(1));

            // The actual outstanding read retains socket A; the shared RemoteClient is now on B.
            using var replacement = await Pair.Create(); client.Socket = replacement.Socket;
            HookEvents.Terraria.RemoteClient.InvokeTryRead(client, () => { });
            received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ((ISocket)socket).AsyncReceive(client.ReadBuffer, 0, client.ReadBuffer.Length, client.ServerReadCallBack, null!);
            pair.Peer.Client.Shutdown(SocketShutdown.Send);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForEvent("linux-read-return", minimum: 2);
            Assert.That(readCallbacks, Is.EqualTo(2));
            Assert.That(client.PendingTermination, Is.True,
                "Native EOF still sets the shared RemoteClient flag; the observer must not repair or suppress it.");
            using var result = Snapshot();
            var eof = result.RootElement.GetProperty("events").EnumerateArray().Last(x =>
                x.GetProperty("Kind").GetString() == "read-callback-entry" && x.GetProperty("Value").GetInt32() == 0);
            Assert.That(eof.GetProperty("Attribution").GetString(), Is.EqualTo("actual-linux-read-source-socket-and-callback-target"));
            Assert.That(eof.GetProperty("SourceMatchesCurrent").GetBoolean(), Is.False);
            Assert.That(eof.GetProperty("SourceSocketIdentity").GetInt64(), Is.Not.EqualTo(eof.GetProperty("CurrentSocketIdentity").GetInt64()));
            Assert.That(eof.GetProperty("SourceRemotePort").GetInt32(), Is.EqualTo(((IPEndPoint)pair.Peer.Client.LocalEndPoint!).Port));
            Assert.That(eof.GetProperty("CurrentRemotePort").GetInt32(), Is.EqualTo(((IPEndPoint)replacement.Peer.Client.LocalEndPoint!).Port));
        }
        finally { HookEvents.Terraria.RemoteClient.ServerReadCallBack -= Seen; }
    }

    [Test]
    public async Task ActualCloseRecordsFirstAttemptAndRealClosureThenDisposalRemovesDetours()
    {
        using var pair = await Pair.Create(); Netplay.Clients[Slot].Socket = pair.Socket;
        ((ISocket)pair.Socket).Close();
        ((ISocket)pair.Socket).Close();
        Assert.That(((ISocket)pair.Socket).IsConnected(), Is.False);
        using var result = Snapshot();
        var closes = result.RootElement.GetProperty("events").EnumerateArray()
            .Where(x => x.GetProperty("Kind").GetString() == "linux-close-entry").ToArray();
        Assert.That(closes.Length, Is.EqualTo(2));
        Assert.That(closes.Select(x => x.GetProperty("Value").GetInt32()), Is.EqualTo(new[] { 1, 0 }));
        Assert.That(closes[0].GetProperty("Stack").GetString(), Does.Contain(nameof(ActualCloseRecordsFirstAttemptAndRealClosureThenDisposalRemovesDetours)));
        for (int i = 0; i < 700; i++)
        {
            Netplay.Clients[Slot].State = i % 2;
            HookEvents.Terraria.RemoteClient.InvokeTryRead(Netplay.Clients[Slot], () => { });
        }
        using (var overflow = Snapshot())
        {
            Assert.That(overflow.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(512));
            Assert.That(overflow.RootElement.GetProperty("events").EnumerateArray().Any(x =>
                x.GetProperty("Kind").GetString() == "linux-close-entry" && x.GetProperty("Value").GetInt32() == 1), Is.True,
                "Routine state churn cannot evict the first close before its 120-second retention window.");
        }
        observer.Dispose();
        using var detached = Snapshot(); long sequence = detached.RootElement.GetProperty("sequence").GetInt64();
        using var after = await Pair.Create(); ((ISocket)after.Socket).Close();
        using var final = Snapshot();
        Assert.That(final.RootElement.GetProperty("sequence").GetInt64(), Is.EqualTo(sequence));
        Assert.That(((ISocket)after.Socket).IsConnected(), Is.False);
    }

    [Test]
    public async Task ActualAcceptanceWrapperKeepsEarlierCancellationAndRecordsQueuedAdmissionBeforeBinding()
    {
        using var pair = await Pair.Create();
        int entries = 0;
        void Cancel(object? _, HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs args)
        { entries++; args.ContinueExecution = false; }
        HookEvents.Terraria.Netplay.OnConnectionAccepted += Cancel;
        try { Netplay.OnConnectionAccepted(pair.Socket); }
        finally { HookEvents.Terraria.Netplay.OnConnectionAccepted -= Cancel; }
        Assert.That(entries, Is.EqualTo(1));
        Assert.That(Netplay.Clients.Any(client => client is not null && ReferenceEquals(client.Socket, pair.Socket)), Is.False);
        using (var canceled = Snapshot())
        {
            var after = canceled.RootElement.GetProperty("events").EnumerateArray().Last(x =>
                x.GetProperty("Kind").GetString() == "connection-accepted-return");
            Assert.That(after.GetProperty("Slot").GetInt32(), Is.EqualTo(-1));
        }

        var netHooks = typeof(ServerApi).Assembly.GetType("TerrariaApi.Server.Hooking.NetHooks", throwOnError: true)!;
        var method = netHooks.GetMethod("OnConnectionAccepted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = (EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs>)method.CreateDelegate(
            typeof(EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs>));
        var actualUpdate = (EventHandler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs>)netHooks
            .GetMethod("OnUpdateConnectedClients", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(EventHandler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs>));
        void Admission(string methodName) => netHooks.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
        Admission("DetachAdmission"); Admission("AttachAdmission");
        int priorMax = Main.maxNetPlayers; var prior = Netplay.Clients[0]; var priorPlayer = Main.player[0];
        var priorBuffer = NetMessage.buffer[0]; var priorListener = Netplay.TcpListener;
        var initial = new LinuxTcpSocket();
        Netplay.Clients[0] = new RemoteClient { Id = 0, Socket = initial };
        NetMessage.buffer[0] = new MessageBuffer(); Main.maxNetPlayers = 1; Netplay.TcpListener = null!;
        HookEvents.Terraria.Netplay.OnConnectionAccepted += actual;
        HookEvents.Terraria.Netplay.UpdateConnectedClients += actualUpdate;
        try
        {
            Netplay.OnConnectionAccepted(pair.Socket);
            Assert.That(Netplay.Clients[0].Socket, Is.SameAs(initial));
            using var accepted = Snapshot();
            var after = accepted.RootElement.GetProperty("events").EnumerateArray().Last(x =>
                x.GetProperty("Kind").GetString() == "connection-accepted-return");
            Assert.That(after.GetProperty("Slot").GetInt32(), Is.EqualTo(-1), "Accepted socket is queued and has no slot yet.");
            HookEvents.Terraria.Netplay.InvokeUpdateConnectedClients(null!, () => { });
            Assert.That(Netplay.Clients[0].Socket, Is.SameAs(pair.Socket));
            Assert.That(Netplay.Clients[0].PendingTermination, Is.False);
            Assert.That(((ISocket)pair.Socket).IsConnected(), Is.True);
        }
        finally
        {
            Admission("DetachAdmission");
            HookEvents.Terraria.Netplay.UpdateConnectedClients -= actualUpdate;
            HookEvents.Terraria.Netplay.OnConnectionAccepted -= actual;
            Netplay.Clients[0] = prior; Main.player[0] = priorPlayer; NetMessage.buffer[0] = priorBuffer;
            Main.maxNetPlayers = priorMax; Netplay.TcpListener = priorListener; ((ISocket)initial).Close();
        }
    }

    [Test]
    public async Task ThrowingObserverMetadataNeverReplacesTheNativeException()
    {
        using var pair = await Pair.Create(); Netplay.Clients[Slot].Socket = pair.Socket;
        var method = typeof(LinuxTcpSocket).GetMethod("ReadCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var invoke = (Action<LinuxTcpSocket, IAsyncResult>)method.CreateDelegate(typeof(Action<LinuxTcpSocket, IAsyncResult>));
        var failure = new InvalidDataException("secret-name-password-payload");
        var result = new ThrowingResult(failure);
        for (int i = 0; i < 12; i++)
            Assert.That(Assert.Throws<InvalidDataException>(() => invoke(pair.Socket, result)), Is.SameAs(failure));
        using var snapshot = Snapshot();
        // Reading AsyncState for attribution also throws: the observer latches once and the native
        // method independently throws the same object. Diagnostic failure never replaces it.
        Assert.That(snapshot.RootElement.GetProperty("observerFailed").GetBoolean(), Is.True);
        Assert.That(JsonSerializer.Serialize(observer.Snapshot()), Does.Not.Contain("secret-name-password-payload"));
    }

    [Test]
    public async Task ActualLinuxFirstChanceIsBoundedAndAttributesSourceWithoutPayloadOrExceptionMessage()
    {
        using var pair = await Pair.Create(); Netplay.Clients[Slot].Socket = pair.Socket;
        var method = typeof(LinuxTcpSocket).GetMethod("ReadCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var invoke = (Action<LinuxTcpSocket, IAsyncResult>)method.CreateDelegate(typeof(Action<LinuxTcpSocket, IAsyncResult>));
        // Actual provider casts AsyncState before its native catch; the wrong shape escapes.
        // The observer retains no AsyncState contents and does not turn this into a clean return.
        var result = new WrongShapeResult();
        for (int i = 0; i < 12; i++) Assert.Throws<InvalidCastException>(() => invoke(pair.Socket, result));
        using var snapshot = Snapshot();
        Assert.That(snapshot.RootElement.GetProperty("observerFailed").GetBoolean(), Is.False);
        var exceptions = snapshot.RootElement.GetProperty("firstExceptions");
        Assert.That(exceptions.GetArrayLength(), Is.EqualTo(8));
        Assert.That(exceptions.EnumerateArray().All(x => x.GetProperty("exactSourceSocketAttribution").GetBoolean()), Is.True);
        Assert.That(exceptions.EnumerateArray().All(x => x.GetProperty("sourceBoundary").GetString() == "linux-read"), Is.True);
        Assert.That(exceptions.EnumerateArray().All(x => x.GetProperty("selection").GetString() == "actual-installed-socket-scope"), Is.True);
        TestContext.Progress.WriteLine("M7_ACTUAL_FIRST_CHANCE_STACK " + exceptions[0].GetProperty("stack").GetString());
        Assert.That(JsonSerializer.Serialize(observer.Snapshot()), Does.Not.Contain("secret-name-password-payload"));
    }

    [Test]
    public void FixedRootReasonsAreBoundedAndNeverSetTermination()
    {
        foreach (var reason in Enum.GetValues<RootConnectBoundary>()) observer.ObserveRootConnect(Slot, reason);
        observer.ObserveRootConnect(Slot, (RootConnectBoundary)int.MaxValue);
        using var result = Snapshot();
        Assert.That(result.RootElement.GetProperty("events").EnumerateArray().Count(x =>
            x.GetProperty("Kind").GetString()!.StartsWith("root-connect-", StringComparison.Ordinal)), Is.EqualTo(8));
        Assert.That(Netplay.Clients[Slot].PendingTermination, Is.False);
    }

    private JsonDocument Snapshot() => JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
    private async Task WaitForEvent(string kind, int minimum = 1)
    {
        for (int i = 0; i < 100; i++)
        {
            using var value = Snapshot();
            if (value.RootElement.GetProperty("events").EnumerateArray().Count(x =>
                x.GetProperty("Kind").GetString() == kind) >= minimum) return;
            await Task.Delay(10);
        }
        Assert.Fail("Actual callback did not reach diagnostic return boundary: " + kind);
    }
    private sealed class ThrowingResult(Exception error) : IAsyncResult
    {
        public object AsyncState => throw error;
        public WaitHandle AsyncWaitHandle => throw new NotSupportedException();
        public bool CompletedSynchronously => false;
        public bool IsCompleted => false;
    }
    private sealed class WrongShapeResult : IAsyncResult
    {
        public object AsyncState => "secret-name-password-payload";
        public WaitHandle AsyncWaitHandle => throw new NotSupportedException();
        public bool CompletedSynchronously => false;
        public bool IsCompleted => true;
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
                var accepted = listener.AcceptTcpClientAsync();
                await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new(peer, new(await accepted));
            }
            catch { peer.Dispose(); throw; }
            finally { listener.Stop(); }
        }
        public void Dispose() { Peer.Dispose(); ((ISocket)Socket).Close(); }
    }
}
