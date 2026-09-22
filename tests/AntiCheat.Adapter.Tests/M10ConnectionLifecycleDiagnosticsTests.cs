using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AntiCheat.Core;
using CompatibilityAudit;
using NUnit.Framework;
using Terraria;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI.Sockets;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M10ConnectionLifecycleDiagnosticsTests
{
    private const int Slot = 0;
    private string directory = null!;
    private RemoteClient previousClient = null!;
    private MessageBuffer previousBuffer = null!;
    private Player previousPlayer = null!;
    private bool previousDisconnect;
    private M10ConnectionLifecycleDiagnostics observer = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "anticheat-m10-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        previousClient = Netplay.Clients[Slot]; previousBuffer = NetMessage.buffer[Slot];
        previousPlayer = Main.player[Slot]; previousDisconnect = Netplay.Disconnect;
        Netplay.Disconnect = false;
        NetMessage.buffer[Slot] = new MessageBuffer();
        Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 0, ReadBuffer = new byte[1024] };
        observer = new(() => directory); observer.Install();
        using var result = Snapshot();
        Assert.That(result.RootElement.GetProperty("installed").GetBoolean(), Is.True);
        Assert.That(result.RootElement.GetProperty("observerFailed").GetBoolean(), Is.False);
    }

    [TearDown]
    public void TearDown()
    {
        observer?.Dispose();
        Netplay.Clients[Slot] = previousClient; NetMessage.buffer[Slot] = previousBuffer;
        Main.player[Slot] = previousPlayer; Netplay.Disconnect = previousDisconnect;
        Directory.Delete(directory, true);
    }

    [Test]
    public async Task LegalNativeResetClosesExactlyItsEntrySocketAndPreservesRootBindingMetadata()
    {
        using var pair = await Pair.Create(); var client = Bind(pair.Socket, 1);
        client.State = 10; client.IsActive = true;
        client.Reset();
        Assert.That(client.Socket, Is.Null);
        Assert.That(client.State, Is.Zero);
        Assert.That(client.PendingTermination, Is.False);
        Assert.That(((ISocket)pair.Socket).IsConnected(), Is.False);
        using var snapshot = Snapshot();
        var row = SocketRow(snapshot, pair.Port);
        var close = row.GetProperty("FirstClose");
        Assert.That(close.GetProperty("ParentBoundary").GetString(), Is.EqualTo("reset"));
        Assert.That(close.GetProperty("SourceMatchesResetEntry").GetBoolean(), Is.True);
        Assert.That(close.GetProperty("SourceSocketIdentity").GetInt64(),
            Is.EqualTo(close.GetProperty("ResetEntrySocketIdentity").GetInt64()));
        Assert.That(close.GetProperty("RootSession").GetProperty("Generation").GetInt64(), Is.EqualTo(1));
        Assert.That(close.GetProperty("Stack").GetString(), Does.Contain("Reset"));
        Assert.That(Events(snapshot, "reset-return").Single().GetProperty("Current").GetProperty("SocketIdentity").ValueKind,
            Is.EqualTo(JsonValueKind.Null));
        Retain("legal-native-reset", snapshot);
    }

    [Test]
    public async Task EarlierNativeResetCancellationStillSkipsCloseAndStateWrites()
    {
        using var pair = await Pair.Create(); var client = Bind(pair.Socket, 1); client.State = 10;
        void Cancel(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        { if (ReferenceEquals(target, client)) args.ContinueExecution = false; }
        HookEvents.Terraria.RemoteClient.Reset += Cancel;
        try { client.Reset(); }
        finally { HookEvents.Terraria.RemoteClient.Reset -= Cancel; }
        Assert.That(client.State, Is.EqualTo(10));
        Assert.That(client.Socket, Is.SameAs(pair.Socket));
        Assert.That(((ISocket)pair.Socket).IsConnected(), Is.True);
        using var snapshot = Snapshot();
        Assert.That(SocketRow(snapshot, pair.Port).GetProperty("FirstClose").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(Events(snapshot, "reset-return").Length, Is.EqualTo(1), "Wrapper return does not claim native body execution.");
        Retain("earlier-reset-canceled", snapshot);
    }

    [Test]
    public async Task LegalActualLinuxReadDeliversOneFrameAndItsOwnEofWithoutCrossBindingAttribution()
    {
        using var pair = await Pair.Create(); var client = Bind(pair.Socket, 1);
        byte[] frame = [3, 0, 154];
        ((ISocket)pair.Socket).AsyncReceive(client.ReadBuffer, 0, client.ReadBuffer.Length, client.ServerReadCallBack, null!);
        await pair.Peer.GetStream().WriteAsync(frame);
        await WaitForEvent("remote-read-return", frame.Length);
        Assert.That(NetMessage.buffer[Slot].totalData, Is.EqualTo(frame.Length));
        Assert.That(NetMessage.buffer[Slot].readBuffer.Take(frame.Length), Is.EqualTo(frame));
        Assert.That(client.PendingTermination, Is.False);
        ((ISocket)pair.Socket).AsyncReceive(client.ReadBuffer, 0, client.ReadBuffer.Length, client.ServerReadCallBack, null!);
        pair.Peer.Client.Shutdown(SocketShutdown.Send);
        await WaitForEvent("remote-read-return", 0);
        using var snapshot = Snapshot();
        var eof = Events(snapshot, "remote-read-return").Single(x => x.GetProperty("Length").GetInt32() == 0);
        Assert.That(eof.GetProperty("SourceMatchesCurrent").GetBoolean(), Is.True);
        Assert.That(eof.GetProperty("Current").GetProperty("PendingTermination").GetBoolean(), Is.True);
        Assert.That(client.PendingTermination, Is.True, "The observer preserves the native EOF action.");
        Retain("legal-native-read-eof", snapshot);
    }

    [Test]
    public async Task DelayedRealEofRecordsOldSocketAndTheReusedRemoteClientItActuallyMutates()
    {
        using var old = await Pair.Create(); var client = Bind(old.Socket, 1);
        ((ISocket)old.Socket).AsyncReceive(client.ReadBuffer, 0, client.ReadBuffer.Length, client.ServerReadCallBack, null!);
        using var replacement = await Pair.Create(); Bind(replacement.Socket, 2);
        Assert.That(client.PendingTermination, Is.False);
        old.Peer.Client.Shutdown(SocketShutdown.Send);
        await WaitForEvent("remote-read-return", 0);
        using var snapshot = Snapshot();
        var entry = Events(snapshot, "remote-read-entry").Single(x => x.GetProperty("Length").GetInt32() == 0);
        var after = Events(snapshot, "remote-read-return").Single(x => x.GetProperty("Length").GetInt32() == 0);
        Assert.That(entry.GetProperty("SourceMatchesCurrent").GetBoolean(), Is.False);
        Assert.That(entry.GetProperty("SourceRemotePort").GetInt32(), Is.EqualTo(old.Port));
        Assert.That(entry.GetProperty("SourceBinding").GetProperty("Generation").GetInt64(),
            Is.Not.EqualTo(entry.GetProperty("Current").GetProperty("Generation").GetInt64()));
        Assert.That(entry.GetProperty("Current").GetProperty("PendingTermination").GetBoolean(), Is.False);
        Assert.That(after.GetProperty("Current").GetProperty("PendingTermination").GetBoolean(), Is.True);
        Assert.That(after.GetProperty("Current").GetProperty("SocketIdentity").GetInt64(),
            Is.EqualTo(SocketRow(snapshot, replacement.Port).GetProperty("Identity").GetInt64()));
        Assert.That(client.PendingTermination, Is.True, "This is a controlled defect witness, not a repair or historical-root-cause assertion.");
        Retain("controlled-old-eof-new-binding", snapshot);
        for (int index = 0; index < 300; index++) ((ISocket)replacement.Socket).Close();
        using var overflow = Snapshot();
        Assert.That(overflow.RootElement.GetProperty("droppedEvents").GetInt64(), Is.GreaterThan(0));
        var retained = SocketRow(overflow, old.Port);
        Assert.That(retained.GetProperty("FirstForeignReadEntry").GetProperty("Order").GetInt64(), Is.EqualTo(entry.GetProperty("Order").GetInt64()));
        Assert.That(retained.GetProperty("FirstForeignReadReturn").GetProperty("Order").GetInt64(), Is.EqualTo(after.GetProperty("Order").GetInt64()));
        Assert.That(retained.GetProperty("FirstForeignReadEntry").GetProperty("Current").GetProperty("PendingTermination").GetBoolean(), Is.False);
        Assert.That(retained.GetProperty("FirstForeignReadReturn").GetProperty("Current").GetProperty("PendingTermination").GetBoolean(), Is.True);
        Retain("controlled-old-eof-first-pair-survives-fifo", overflow);
    }

    [Test]
    public async Task ControlledOldResetAndActualTsapiAdmissionKeepTheNewSocketUnboundAndAliveUntilCleanupReturns()
    {
        using var old = await Pair.Create(); var client = Bind(old.Socket, 1);
        ((ISocket)old.Socket).Close(); // Actual disconnected slot, as required by TSAPI selection.
        using var replacement = await Pair.Create();
        using var paused = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int entries = 0;
        void PauseFirstReset(RemoteClient target, HookEvents.Terraria.RemoteClient.ResetEventArgs args)
        {
            if (!ReferenceEquals(target, client) || Interlocked.Increment(ref entries) != 1) return;
            paused.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled reset release was not signaled.");
        }
        var actualAccept = TsapiAcceptance();
        var netHooks = typeof(ServerApi).Assembly.GetType("TerrariaApi.Server.Hooking.NetHooks", true)!;
        var actualUpdate = (EventHandler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs>)netHooks
            .GetMethod("OnUpdateConnectedClients", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(EventHandler<HookEvents.Terraria.Netplay.UpdateConnectedClientsEventArgs>));
        void Admission(string method) => netHooks.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
        Admission("DetachAdmission"); Admission("AttachAdmission");
        int priorMax = Main.maxNetPlayers; var priorListener = Netplay.TcpListener;
        Main.maxNetPlayers = 1; Netplay.TcpListener = null!;
        HookEvents.Terraria.RemoteClient.Reset += PauseFirstReset;
        HookEvents.Terraria.Netplay.OnConnectionAccepted += actualAccept;
        HookEvents.Terraria.Netplay.UpdateConnectedClients += actualUpdate;
        Task? retiring = null;
        try
        {
            retiring = Task.Run(client.Reset); // One bounded, barrier-controlled original native invocation.
            Assert.That(paused.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Netplay.OnConnectionAccepted(replacement.Socket);
            Assert.That(client.Socket, Is.SameAs(old.Socket));
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True);
            release.Set(); await retiring.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.Socket, Is.Null);
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True);
            // The same actual TSAPI update handler dispatches admission. Its full native
            // update body is covered independently by M10ConnectionAdmissionTests.
            HookEvents.Terraria.Netplay.InvokeUpdateConnectedClients(null!, () => { });
            Assert.That(client.Socket, Is.SameAs(replacement.Socket));
            Assert.That(((ISocket)replacement.Socket).IsConnected(), Is.True);
            using var snapshot = Snapshot();
            var closed = SocketRow(snapshot, replacement.Port).GetProperty("FirstClose");
            Assert.That(closed.ValueKind, Is.EqualTo(JsonValueKind.Null));
            var resets = Events(snapshot, "reset-entry");
            Assert.That(resets.Length, Is.EqualTo(2));
            Assert.That(resets[0].GetProperty("SourceSocketIdentity").GetInt64(),
                Is.EqualTo(SocketRow(snapshot, old.Port).GetProperty("Identity").GetInt64()));
            Assert.That(resets[1].GetProperty("SourceSocketIdentity").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(Events(snapshot, "accept-return").Length, Is.EqualTo(1));
            Assert.That(snapshot.RootElement.GetProperty("rootCauseProven").GetBoolean(), Is.False);
            // Pre-fix failure TRX/JSON and the independent real 200ms failure are retained.
            Retain("fixed-old-reset-new-accept-deferral", snapshot);
        }
        finally
        {
            release.Set();
            if (retiring is not null) await retiring.WaitAsync(TimeSpan.FromSeconds(5));
            Admission("DetachAdmission");
            HookEvents.Terraria.Netplay.UpdateConnectedClients -= actualUpdate;
            HookEvents.Terraria.Netplay.OnConnectionAccepted -= actualAccept;
            HookEvents.Terraria.RemoteClient.Reset -= PauseFirstReset;
            Main.maxNetPlayers = priorMax; Netplay.TcpListener = priorListener;
        }
    }

    [Test]
    public void FirstExceptionIsRetainedPerSocketAfterMoreThanEightOtherSocketsFail()
    {
        var read = (Action<LinuxTcpSocket, IAsyncResult>)typeof(LinuxTcpSocket)
            .GetMethod("ReadCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<LinuxTcpSocket, IAsyncResult>));
        var values = Enumerable.Range(0, 12).Select(_ => new LinuxTcpSocket()).ToArray();
        try
        {
            foreach (var socket in values)
            {
                Assert.Throws<InvalidCastException>(() => read(socket, new WrongShapeResult()));
                Assert.Throws<InvalidCastException>(() => read(socket, new WrongShapeResult()));
            }
            using var snapshot = Snapshot();
            var rows = snapshot.RootElement.GetProperty("sockets").EnumerateArray().ToArray();
            Assert.That(rows.Length, Is.EqualTo(12));
            Assert.That(rows.All(x => x.GetProperty("FirstException").ValueKind == JsonValueKind.Object), Is.True);
            Assert.That(Events(snapshot, "first-exception").Length, Is.EqualTo(12));
            Assert.That(JsonSerializer.Serialize(observer.Snapshot()), Does.Not.Contain("secret-name-password-payload"));
            Assert.That(snapshot.RootElement.GetProperty("observerFailed").GetBoolean(), Is.False);
            Retain("first-error-per-socket", snapshot);
        }
        finally { foreach (var socket in values) ((ISocket)socket).Close(); }
    }

    [Test]
    public async Task RoutineEventOverflowKeepsFirstCloseAndDisposeRemovesRealDetours()
    {
        using var pair = await Pair.Create(); Bind(pair.Socket, 1);
        ((ISocket)pair.Socket).Close();
        using var first = Snapshot();
        long firstCloseOrder = SocketRow(first, pair.Port).GetProperty("FirstClose").GetProperty("Order").GetInt64();
        for (int i = 0; i < 300; i++) ((ISocket)pair.Socket).Close();
        using var snapshot = Snapshot();
        Assert.That(snapshot.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(512));
        Assert.That(snapshot.RootElement.GetProperty("droppedEvents").GetInt64(), Is.GreaterThan(0));
        Assert.That(SocketRow(snapshot, pair.Port).GetProperty("FirstClose").GetProperty("Order").GetInt64(), Is.EqualTo(firstCloseOrder));
        observer.Flush();
        using var disk = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "m10-connection-lifecycle.json")));
        Assert.That(disk.RootElement.GetProperty("order").GetInt64(), Is.EqualTo(snapshot.RootElement.GetProperty("order").GetInt64()));
        observer.Dispose();
        using var detached = Snapshot();
        ((ISocket)pair.Socket).Close();
        using var final = Snapshot();
        Assert.That(final.RootElement.GetProperty("order").GetInt64(), Is.EqualTo(detached.RootElement.GetProperty("order").GetInt64()));
        Assert.That(((ISocket)pair.Socket).IsConnected(), Is.False);
        Retain("bounded-history-and-detach", snapshot);
    }

    [Test]
    public void SocketHistoryCapacityEvictsWithExplicitLossCounters()
    {
        for (int i = 0; i < 270; i++) ((ISocket)new LinuxTcpSocket()).Close();
        using var snapshot = Snapshot();
        Assert.That(snapshot.RootElement.GetProperty("sockets").GetArrayLength(), Is.EqualTo(256));
        Assert.That(snapshot.RootElement.GetProperty("evictedSockets").GetInt64(), Is.EqualTo(14));
        Assert.That(snapshot.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(512));
        Assert.That(snapshot.RootElement.GetProperty("sockets").EnumerateArray()
            .All(x => x.GetProperty("FirstClose").ValueKind == JsonValueKind.Object), Is.True);
    }

    [Test]
    public async Task DirectRemoteCallbackWithoutLinuxScopeDoesNotInventAnOriginSocket()
    {
        using var pair = await Pair.Create(); var client = Bind(pair.Socket, 1);
        client.ServerReadCallBack(null!, 0);
        using var snapshot = Snapshot();
        var callback = Events(snapshot, "remote-read-return").Single();
        Assert.That(callback.GetProperty("SourceSocketIdentity").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(callback.GetProperty("SourceBinding").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(callback.GetProperty("SourceMatchesCurrent").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(client.PendingTermination, Is.True);
    }

    [Test]
    public async Task ObserverMetadataFailureCannotReplaceOrSwallowTheOriginalNativeException()
    {
        using var pair = await Pair.Create(); Bind(pair.Socket, 1);
        var read = (Action<LinuxTcpSocket, IAsyncResult>)typeof(LinuxTcpSocket)
            .GetMethod("ReadCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<LinuxTcpSocket, IAsyncResult>));
        var failure = new InvalidDataException("secret-name-password-payload");
        Assert.That(Assert.Throws<InvalidDataException>(() => read(pair.Socket, new ThrowingResult(failure))), Is.SameAs(failure));
        using var snapshot = Snapshot();
        Assert.That(snapshot.RootElement.GetProperty("observerFailed").GetBoolean(), Is.True);
        Assert.That(JsonSerializer.Serialize(observer.Snapshot()), Does.Not.Contain("secret-name-password-payload"));
    }

    private RemoteClient Bind(LinuxTcpSocket socket, long generation)
    {
        var client = Netplay.Clients[Slot]; client.Socket = socket;
        observer.ObserveRootBinding(new SessionKey(Guid.Parse("a3a54d57-0684-486b-93de-08ad229cad07"), 1, Slot, generation), client, socket);
        return client;
    }
    private JsonDocument Snapshot() => JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
    private static JsonElement SocketRow(JsonDocument snapshot, int port) => snapshot.RootElement
        .GetProperty("sockets").EnumerateArray().Single(x => x.GetProperty("RemotePort").ValueKind == JsonValueKind.Number && x.GetProperty("RemotePort").GetInt32() == port);
    private static JsonElement[] Events(JsonDocument snapshot, string kind) => snapshot.RootElement
        .GetProperty("events").EnumerateArray().Where(x => x.GetProperty("Kind").GetString() == kind).ToArray();
    private async Task WaitForEvent(string kind, int length)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var value = Snapshot();
            if (Events(value, kind).Any(x => x.GetProperty("Length").ValueKind == JsonValueKind.Number && x.GetProperty("Length").GetInt32() == length)) return;
            await Task.Delay(10);
        }
        Assert.Fail("Actual native callback did not reach its recorded boundary: " + kind);
    }
    private static EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs> TsapiAcceptance()
    {
        var type = typeof(ServerApi).Assembly.GetType("TerrariaApi.Server.Hooking.NetHooks", throwOnError: true)!;
        return (EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs>)type
            .GetMethod("OnConnectionAccepted", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(EventHandler<HookEvents.Terraria.Netplay.OnConnectionAcceptedEventArgs>));
    }
    private static void Retain(string name, JsonDocument snapshot)
    {
        string? root = Environment.GetEnvironmentVariable("ANTICHEAT_M10_LIFECYCLE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        string target = Path.Combine(root, name); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "result.json"), JsonSerializer.Serialize(new
        {
            name, utc = DateTimeOffset.UtcNow,
            scope = "locked-native-methods-and-loopback-socket; controlled-interleavings-are-not-historical-failure-attribution",
            runtime = Identity(typeof(RemoteClient).Assembly), socketProvider = Identity(typeof(LinuxTcpSocket).Assembly),
            observer = Identity(typeof(M10ConnectionLifecycleDiagnostics).Assembly), snapshot = snapshot.RootElement
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Identity(Assembly assembly) => new { path = assembly.Location,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))) };
    private sealed class WrongShapeResult : IAsyncResult
    {
        public object AsyncState => "secret-name-password-payload";
        public WaitHandle AsyncWaitHandle => throw new NotSupportedException();
        public bool CompletedSynchronously => false;
        public bool IsCompleted => true;
    }
    private sealed class ThrowingResult(Exception error) : IAsyncResult
    {
        public object AsyncState => throw error;
        public WaitHandle AsyncWaitHandle => throw new NotSupportedException();
        public bool CompletedSynchronously => false;
        public bool IsCompleted => false;
    }
    private sealed class Pair(TcpClient peer, LinuxTcpSocket socket, int port) : IDisposable
    {
        public TcpClient Peer { get; } = peer;
        public LinuxTcpSocket Socket { get; } = socket;
        public int Port { get; } = port;
        public static async Task<Pair> Create()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var peer = new TcpClient();
            try
            {
                var accepted = listener.AcceptTcpClientAsync();
                await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
                return new(peer, new(server), ((IPEndPoint)peer.Client.LocalEndPoint!).Port);
            }
            catch { peer.Dispose(); throw; }
            finally { listener.Stop(); }
        }
        public void Dispose() { Peer.Dispose(); ((ISocket)Socket).Close(); }
    }
}
