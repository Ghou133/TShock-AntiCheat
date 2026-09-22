using System.Net;
using System.Net.Sockets;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using OTAPI;
using Terraria;
using TShockAPI;
using TShockAPI.Sockets;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed partial class M16TimeoutTransportRetirementTests
{
    private TShockAPI.Configuration.TShockConfig? previousConfig;
    [OneTimeSetUp]
    public void InitializeTargetConfig()
    { previousConfig = ServerTShock.Config; ServerTShock.Config ??= new TShockAPI.Configuration.TShockConfig(); }
    [OneTimeTearDown]
    public void RestoreTargetConfig() => ServerTShock.Config = previousConfig;

    [TestCase(false)]
    [TestCase(true)]
    public async Task RealNativeSendDataCancellationOrThrowCannotPreventOwnedTcpClose(bool throws)
    {
        await using var f = await Fixture.Create();
        int calls = 0;
        void Stop(object? sender, Hooks.NetMessage.SendDataEventArgs e)
        {
            if (e.Event != HookEvent.Before || e.MsgType != 2) return;
            calls++;
            if (throws) throw new IOException("owned-senddata-before-fixture");
            e.Result = HookResult.Cancel;
        }
        Hooks.NetMessage.SendData += Stop;
        try
        {
            // Demonstrate the old notification-only gap against the genuine native dispatcher.
            if (throws) Assert.Throws<IOException>(() => f.Player.Disconnect("owned timeout baseline"));
            else f.Player.Disconnect("owned timeout baseline");
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(f.Native.PendingTermination, Is.False);
            await f.Pair.AssertLive();
            Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
            f.Queue.EnqueueAuthorized(f.Retirement);
            await f.Pair.AssertClosed();
            Assert.That(((Terraria.Net.Sockets.ISocket)f.Provider).IsConnected(), Is.False,
                "The real provider observes its disposed socket without private flag changes.");
            Assert.That(calls, Is.EqualTo(1), "Retirement never re-enters the failed/canceled native send dispatcher.");
            Assert.That(f.Native.PendingTermination, Is.False, "No native slot mutation is manufactured by the helper.");
            Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 1, 1, 0)));
        }
        finally { Hooks.NetMessage.SendData -= Stop; }
    }

    [TestCase("generation")]
    [TestCase("world")]
    [TestCase("player")]
    [TestCase("native-client")]
    [TestCase("provider")]
    [TestCase("provider-inner-connection")]
    [TestCase("tcp-client-inner-socket")]
    public async Task StaleAuthorizationNeverClosesOldOrReplacementTransport(string changed)
    {
        await using var f = await Fixture.Create();
        await using var other = await Pair.Create();
        SessionKey key = f.Key;
        if (changed == "generation") key = key with { Generation = key.Generation + 1 };
        else if (changed == "world") key = key with { WorldEpoch = key.WorldEpoch + 1 };
        else if (changed == "player") ServerTShock.Players[Fixture.Slot] = new TSPlayer(Fixture.Slot);
        else if (changed == "native-client") Netplay.Clients[Fixture.Slot] = new RemoteClient { Id = Fixture.Slot, Socket = f.Provider };
        else if (changed == "provider") f.Native.Socket = new LinuxTcpSocket(other.Subject);
        else if (changed == "provider-inner-connection") f.Provider._connection = other.Subject;
        else f.Pair.Subject.Client = other.Subject.Client;
        Assert.That(f.Retirement.TryAuthorize(key, f.Player), Is.False);
        f.Queue.EnqueueAuthorized(f.Retirement);
        Assert.That(f.Queue.Snapshot.Attempts, Is.Zero);
        await f.Pair.AssertLive(); await other.AssertLive();
    }

    [Test]
    public async Task ReplacementAfterAuthorizationAndDeferredRetryCannotRedirectCapturedClose()
    {
        await using var f = await Fixture.Create();
        await using var next = await Pair.Create();
        f.Pair.Subject.FailuresRemaining = 1;
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        f.Queue.EnqueueAuthorized(f.Retirement);
        Assert.That(f.Queue.Snapshot.Pending, Is.EqualTo(1));
        f.Provider._connection = next.Subject;
        Netplay.Clients[Fixture.Slot] = new RemoteClient { Id = Fixture.Slot, Socket = f.Provider, State = 10, IsActive = true };
        ServerTShock.Players[Fixture.Slot] = new TSPlayer(Fixture.Slot);
        f.Clock.Advance(.25); f.FullScan();
        await f.Pair.AssertClosed(); await next.AssertLive();
        Assert.That(next.Subject.CloseCalls, Is.Zero);
        Assert.That(Netplay.Clients[Fixture.Slot].PendingTermination, Is.False);
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 2, 1, 0)));
    }

    [Test]
    public async Task ReplacedTcpClientInnerSocketAfterAuthorizationCannotRedirectDeferredClose()
    {
        await using var f = await Fixture.Create();
        await using var next = await Pair.Create();
        f.Pair.Subject.FailuresRemaining = 1;
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        f.Queue.EnqueueAuthorized(f.Retirement);
        f.Pair.Subject.Client = next.Subject.Client;
        f.Clock.Advance(.25); f.FullScan();
        await f.Pair.AssertClosed(); await next.AssertLive();
        Assert.That(next.Subject.CloseCalls, Is.Zero);
        Assert.That(f.Native.PendingTermination, Is.False);
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 2, 1, 0)));
    }

    [Test]
    public async Task CapturedSocketDisposeCompletesOriginalStreamReadAndNativeResetDisposesItsWrappers()
    {
        await using var f = await Fixture.Create();
        var oldBuffer = NetMessage.buffer[Fixture.Slot];
        NetMessage.buffer[Fixture.Slot] = new MessageBuffer { whoAmI = Fixture.Slot };
        try
        {
            var originalStream = f.Pair.Subject.GetStream();
            var completed = new TaskCompletionSource<(object State, int Length)>(TaskCreationOptions.RunContinuationsAsynchronously);
            object owner = new(); int callbacks = 0; byte[] bytes = new byte[1];
            ((Terraria.Net.Sockets.ISocket)f.Provider).AsyncReceive(bytes, 0, 1, (state, length) =>
            { Interlocked.Increment(ref callbacks); completed.TrySetResult((state, length)); }, owner);
            Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
            f.Queue.EnqueueAuthorized(f.Retirement);
            var outcome = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(outcome.State, Is.SameAs(owner)); Assert.That(outcome.Length, Is.LessThanOrEqualTo(0));
            Assert.That(callbacks, Is.EqualTo(1)); Assert.That(bytes[0], Is.Zero);
            await f.Pair.AssertClosed();
            Assert.That(((Terraria.Net.Sockets.ISocket)f.Provider).IsConnected(), Is.False);
            Assert.That(f.Native.Socket, Is.SameAs(f.Provider), "Only the native reset owns this wrapper field.");
            // Actual target reset method, not an assignment of flags or a replacement cleanup implementation.
            // Automatic real-server dispatch remains a separate TCP acceptance requirement.
            f.Native.Reset();
            Assert.That(f.Native.Socket, Is.Null); Assert.That(f.Native.State, Is.Zero); Assert.That(f.Native.IsActive, Is.False);
            Assert.That(((Terraria.Net.Sockets.ISocket)f.Provider).GetRemoteAddress(), Is.Null);
            Assert.Throws<ObjectDisposedException>(() => f.Pair.Subject.GetStream());
            Assert.That(originalStream.CanRead, Is.False, "Native provider Close disposed the original TcpClient stream wrapper.");
            Assert.That(callbacks, Is.EqualTo(1)); Assert.That(f.Queue.Snapshot.Attempts, Is.EqualTo(1));
        }
        finally { NetMessage.buffer[Fixture.Slot] = oldBuffer; }
    }

    [Test]
    public async Task FailureRetriesAreSpacedBoundedAndFaultSinkCannotEscape()
    {
        await using var f = await Fixture.Create();
        f.Pair.Subject.FailuresRemaining = 10;
        int faults = 0;
        f.Queue.RetirementFailed = _ => { faults++; throw new IOException("owned-log-failure"); };
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        f.Queue.EnqueueAuthorized(f.Retirement);
        f.FullScan(); Assert.That(f.Pair.Subject.CloseCalls, Is.EqualTo(1));
        f.Clock.Advance(.25); f.FullScan();
        f.Clock.Advance(.25); f.FullScan();
        f.Clock.Advance(20); f.FullScan(); f.Queue.EnqueueAuthorized(f.Retirement);
        Assert.That(f.Pair.Subject.CloseCalls, Is.EqualTo(3));
        Assert.That(faults, Is.EqualTo(1));
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 3, 0, 1)));
        await f.Pair.AssertLive();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RetryClockFailureOrExpiredTtlNeverRetainsAnUnboundedReference(bool clockFailure)
    {
        await using var f = await Fixture.Create();
        f.Pair.Subject.FailuresRemaining = 10;
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        f.Queue.EnqueueAuthorized(f.Retirement);
        if (clockFailure) f.Clock.Throw = true; else f.Clock.Advance(5);
        f.FullScan();
        Assert.That(f.Pair.Subject.CloseCalls, Is.EqualTo(1));
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 1, 0, 1)));
        await f.Pair.AssertLive();
    }

    [Test]
    public async Task FirstCloseStillRunsOnceWhenTheRetryClockFails()
    {
        await using var f = await Fixture.Create();
        f.Clock.Throw = true;
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        f.Queue.EnqueueAuthorized(f.Retirement);
        await f.Pair.AssertClosed();
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 1, 1, 0)));
    }

    [Test]
    public async Task ConcurrentQueueAndMaintenanceDoNotDuplicateAnOwnedClose()
    {
        await using var f = await Fixture.Create();
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0) f.Queue.EnqueueAuthorized(f.Retirement);
            else f.FullScan();
        })));
        await f.Pair.AssertClosed();
        Assert.That(f.Pair.Subject.CloseCalls, Is.EqualTo(1));
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 1, 1, 0)));
    }

    [Test]
    public async Task DisposalMakesOneBoundedOwnedFinalAttemptAndReleasesQueueReferences()
    {
        await using var f = await Fixture.Create();
        f.Pair.Subject.FailuresRemaining = 1;
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        f.Queue.EnqueueAuthorized(f.Retirement);
        f.Queue.Dispose(); f.Queue.Dispose(); f.FullScan();
        await f.Pair.AssertClosed();
        Assert.That(f.Pair.Subject.CloseCalls, Is.EqualTo(2));
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 2, 1, 0)));
    }

    [Test]
    public async Task RetryQueueCapacityAndPerMaintenanceInspectionAreBounded()
    {
        await using var f = await Fixture.Create();
        f.Pair.Subject.FailuresRemaining = 10000;
        // Synthetic queue-capacity pressure, not 257 real simultaneous connections.
        // Each attempt still targets the one explicitly owned fixture connection.
        for (int i = 0; i < 257; i++)
        {
            var retirement = M16TimeoutTransportRetirement.Capture(f.Key, f.Player, f.Native, f.Provider)!;
            Assert.That(retirement.TryAuthorize(f.Key, f.Player), Is.True);
            f.Queue.EnqueueAuthorized(retirement);
        }
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(256, 257, 0, 1)));
        f.Clock.Advance(.25); f.Queue.Maintain();
        Assert.That(f.Queue.Snapshot.Attempts, Is.EqualTo(273), "Exactly the selected 16 pending entries were inspected.");
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Queue.Maintain(65));
        f.Clock.Advance(5); f.FullScan();
        Assert.That(f.Queue.Snapshot, Is.EqualTo(new M16TimeoutRetirementSnapshot(0, 273, 0, 257)));
        await f.Pair.AssertLive();
    }

    [TestCase(ConnectionPhase.Handshake, 0, 2)]
    [TestCase(ConnectionPhase.Authenticating, 1, 3)]
    [TestCase(ConnectionPhase.WorldSync, 3, 4)]
    [TestCase(ConnectionPhase.Playing, 10, 5)]
    public async Task LegalSlowPhaseStaysOpenBeforeDeadlineThenRepeatedTrafficCannotRenewIt(ConnectionPhase phase, int native, int seconds)
    {
        await using var f = await Fixture.Create();
        var controls = new NetworkControls(f.Clock, new NetworkControlOptions
        {
            HandshakeTimeout = TimeSpan.FromSeconds(2), AuthenticationTimeout = TimeSpan.FromSeconds(3),
            WorldSyncTimeout = TimeSpan.FromSeconds(4), PlayerUpdateTimeout = TimeSpan.FromSeconds(5)
        });
        Assert.That(controls.Open(f.Key, "127.0.0.1").Disposition, Is.EqualTo(NetworkDisposition.Allow));
        f.Native.State = native;
        var adapter = new M10ConnectionPhaseAdapter(f.Key, f.Player, f.Native, f.Provider);
        ConnectionPhase? Read(SessionKey key) => adapter.ReadAccepted(key, f.Player)?.Phase;
        for (int i = 0; i < 16; i++) controls.InspectTimeouts(16, Read);
        f.Clock.Advance(seconds - .01);
        controls.Consume(f.Key, 16); controls.EnterPhase(f.Key, phase);
        for (int i = 0; i < 16; i++) Assert.That(controls.InspectTimeouts(16, Read).Decisions, Is.Empty);
        await f.Pair.AssertLive();
        f.Clock.Advance(.01); controls.Consume(f.Key, 16);
        NetworkControlDecision? timeout = null;
        for (int i = 0; i < 16; i++) timeout ??= controls.InspectTimeouts(16, Read).Decisions.FirstOrDefault();
        Assert.That(timeout, Is.Not.Null);
        Assert.That(timeout!.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(timeout.Disposition, Is.EqualTo(NetworkDisposition.Disconnect));
        Assert.That(controls.CapturePhase(f.Key), Is.Null);
        Assert.That(f.Retirement.TryAuthorize(f.Key, f.Player), Is.True);
        // Root integration must isolate this exact session synchronously here.
        f.Queue.EnqueueAuthorized(f.Retirement);
        await f.Pair.AssertClosed();
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks = TimeSpan.TicksPerSecond;
        public bool Throw;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Throw ? throw new IOException("owned-clock-failure") : ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class FaultingSocket() : Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    {
        public int FailuresRemaining, CloseCalls, LastCloseThreadId;
        protected override void Dispose(bool disposing)
        {
            CloseCalls++; LastCloseThreadId = Environment.CurrentManagedThreadId;
            if (FailuresRemaining-- > 0) throw new IOException("owned-close-failure");
            base.Dispose(disposing);
        }
    }

    private sealed class FaultingTcpClient : TcpClient
    {
        public FaultingSocket OwnedSocket { get; } = new();
        public FaultingTcpClient() => Client = OwnedSocket;
        public int FailuresRemaining { get => OwnedSocket.FailuresRemaining; set => OwnedSocket.FailuresRemaining = value; }
        public int CloseCalls => OwnedSocket.CloseCalls;
        public int LastCloseThreadId => OwnedSocket.LastCloseThreadId;
    }

    private sealed class Pair(FaultingTcpClient subject, TcpClient peer) : IAsyncDisposable
    {
        public FaultingTcpClient Subject { get; } = subject;
        private TcpClient Peer { get; } = peer;
        public static async Task<Pair> Create()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var subject = new FaultingTcpClient();
            await subject.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            return new(subject, await listener.AcceptTcpClientAsync());
        }
        public async Task AssertLive()
        {
            await Peer.GetStream().WriteAsync(new byte[] { 72 });
            byte[] one = new byte[1]; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.That(await Subject.OwnedSocket.ReceiveAsync(one, SocketFlags.None, stop.Token), Is.EqualTo(1));
            Assert.That(one[0], Is.EqualTo(72));
        }
        public async Task AssertClosed()
        {
            byte[] one = new byte[1]; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { Assert.That(await Peer.GetStream().ReadAsync(one, stop.Token), Is.Zero); }
            catch (IOException) { /* Platform may report owned TCP reset instead of EOF. */ }
        }
        public ValueTask DisposeAsync()
        { Subject.FailuresRemaining = 0; Subject.OwnedSocket.Dispose(); Subject.Dispose(); Peer.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const int Slot = 14;
        public SessionKey Key = new(Guid.NewGuid(), 1, Slot, 1);
        public Clock Clock { get; } = new();
        public Pair Pair = null!;
        public TSPlayer Player = null!;
        public RemoteClient Native = null!;
        public LinuxTcpSocket Provider = null!;
        public M16TimeoutTransportRetirement Retirement = null!;
        public M16TimeoutRetirementQueue Queue = null!;
        private TSPlayer? oldActor;
        private Player oldPlayer = null!;
        private RemoteClient oldNative = null!;
        private int oldMode;
        private bool oldDedicated;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture
            {
                oldActor = ServerTShock.Players[Slot], oldPlayer = Main.player[Slot], oldNative = Netplay.Clients[Slot],
                oldMode = Main.netMode, oldDedicated = Main.dedServ, Pair = await Pair.Create()
            };
            Main.netMode = 2; Main.dedServ = true;
            Main.player[Slot] = new Player { whoAmI = Slot, active = true };
            f.Player = new TSPlayer(Slot); ServerTShock.Players[Slot] = f.Player;
            f.Provider = new LinuxTcpSocket(f.Pair.Subject);
            f.Native = new RemoteClient { Id = Slot, Socket = f.Provider, State = 1, IsActive = true };
            Netplay.Clients[Slot] = f.Native;
            f.Retirement = M16TimeoutTransportRetirement.Capture(f.Key, f.Player, f.Native, f.Provider)!;
            Assert.That(f.Retirement, Is.Not.Null);
            f.Queue = new(f.Clock); return f;
        }
        public void FullScan() { for (int i = 0; i < 16; i++) Queue.Maintain(); }
        public async ValueTask DisposeAsync()
        {
            await Pair.DisposeAsync();
            ServerTShock.Players[Slot] = oldActor; Main.player[Slot] = oldPlayer; Netplay.Clients[Slot] = oldNative;
            Main.netMode = oldMode; Main.dedServ = oldDedicated;
        }
    }
}
