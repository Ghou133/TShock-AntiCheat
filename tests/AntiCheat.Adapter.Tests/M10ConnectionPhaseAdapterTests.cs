using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.Localization;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.Testing;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

// Actual locked native Hello/password methods and real target objects. The in-memory socket,
// controlled clock and direct root callbacks do not claim a running server, TCP or SSC delivery.
[TestFixture, NonParallelizable]
public sealed class M10ConnectionPhaseAdapterTests
{
    private const int Slot = 15;
    private const string Fingerprint = "target326-m10-phase-fixture";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private AntiCheatPlugin plugin = null!;
    private AntiCheatEngine engine = null!;
    private Store store = null!;
    private Clock clock = null!;
    private NetworkControls network = null!;
    private SessionKey session;
    private CapturingPlayer actor = null!;
    private RemoteClient client = null!, oldClient = null!;
    private FixtureSocket socket = null!;
    private MessageBuffer buffer = null!, oldBuffer = null!;
    private TSPlayer? oldActor;
    private Player oldPlayer = null!;
    private LanguageManager oldLanguage = null!;
    private string oldPassword = null!, oldBanFile = null!;
    private bool oldDedicated, oldAssertions, oldConnected;
    private int oldMode;
    private TextWriter oldOut = null!;
    private StringWriter output = null!;
    private readonly List<Action> restoreLegacyLanguage = [];

    [SetUp]
    public async Task SetUp()
    {
        oldActor = ServerTShock.Players[Slot]; oldPlayer = Main.player[Slot];
        oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot];
        oldPassword = Netplay.ServerPassword; oldBanFile = Netplay.BanFilePath;
        oldMode = Main.netMode; oldDedicated = Main.dedServ; oldAssertions = Invariant.assertionsEnabled;
        oldConnected = Netplay.HasFullyConnectedClients; oldLanguage = LanguageManager.Instance;
        oldOut = Console.Out; output = new StringWriter(); Console.SetOut(output);
        Main.netMode = 2; Main.dedServ = true; Invariant.assertionsEnabled = false;
        Netplay.HasFullyConnectedClients = false; Netplay.ServerPassword = "";
        // The native ban-file read is scoped to a unique absent fixture path; no ban policy is disabled.
        Netplay.BanFilePath = Path.Combine(Path.GetTempPath(), "anticheat-m10-absent-ban-" + Guid.NewGuid().ToString("N"));
        LanguageManager.Instance = new LanguageManager();
        LanguageManager.Instance.LoadLanguage(GameCulture.FromName("en-US"));
        PreserveLegacyLanguage();
        // LanguageManager resources alone do not fill Lang.mp, which the actual native
        // version/password rejection branches dereference before sending packet 2.
        Lang.InitializeLegacyLocalization();
        Assert.That(Lang.mp[1], Is.Not.Null); Assert.That(Lang.mp[4], Is.Not.Null);
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        actor = new CapturingPlayer(Slot);
        ServerTShock.Players[Slot] = actor;
        socket = new FixtureSocket();
        client = new RemoteClient { Id = Slot, Socket = socket, State = 0 };
        Netplay.Clients[Slot] = client;
        buffer = new MessageBuffer { whoAmI = Slot };
        NetMessage.buffer[Slot] = buffer;
        clock = new Clock();
        network = new NetworkControls(clock, new NetworkControlOptions
        {
            MaxSessions = 16, HandshakeTimeout = TimeSpan.FromSeconds(2),
            AuthenticationTimeout = TimeSpan.FromSeconds(3), WorldSyncTimeout = TimeSpan.FromSeconds(4),
            PlayerUpdateTimeout = TimeSpan.FromSeconds(5), ConnectionIdleTtl = TimeSpan.FromSeconds(30)
        });
        store = new Store();
        engine = new AntiCheatEngine(clock, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(Fingerprint, ExecutionScope.TestLab));
        Assert.That(await engine.RecoverAsync(), Is.True);
        plugin = new AntiCheatPlugin(null!);
        Set("_engine", engine); Set("_network", network); Set("_scope", ExecutionScope.TestLab);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", Fingerprint, "explicit-native-phase-fixture"));
        var connect = new ConnectEventArgs();
        typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
        Call<HookHandler<ConnectEventArgs>>("OnConnect")(connect);
        Assert.That(connect.Handled, Is.False);
        session = CurrentKey();
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            if (plugin is not null) { plugin.Dispose(); await plugin.ShutdownCompletion; }
        }
        finally
        {
            Console.SetOut(oldOut); output.Dispose();
            ServerTShock.Players[Slot] = oldActor; Main.player[Slot] = oldPlayer;
            Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer;
            Netplay.ServerPassword = oldPassword; Netplay.BanFilePath = oldBanFile;
            Main.netMode = oldMode; Main.dedServ = oldDedicated; Invariant.assertionsEnabled = oldAssertions;
            Netplay.HasFullyConnectedClients = oldConnected; LanguageManager.Instance = oldLanguage;
            for (int i = restoreLegacyLanguage.Count - 1; i >= 0; i--) restoreLegacyLanguage[i]();
            restoreLegacyLanguage.Clear();
        }
    }

    [Test]
    public void NativeHelloCommitIsSampledBeforeTheOldDeadlineWithoutAnotherInboundPacket()
    {
        clock.Advance(1.9);
        Native(1, "Terraria326");
        Assert.That(client.State, Is.EqualTo(1));
        Assert.That(socket.Sent.Select(frame => frame[2]), Is.EqualTo(new byte[] { 3 }));
        var before = network.CapturePhase(session)!;
        Assert.That(before.Phase, Is.EqualTo(ConnectionPhase.Handshake), "The raw pre-hook cannot see a later native commit.");
        clock.Advance(.2); Maintain();
        var after = network.CapturePhase(session)!;
        Assert.Multiple(() =>
        {
            Assert.That(after.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
            Assert.That(after.PhaseEnteredAt, Is.EqualTo(clock.GetTimestamp()));
            Assert.That(after.LastPacketAt, Is.EqualTo(before.LastPacketAt));
            Assert.That(after.LastPlayerUpdateAt, Is.EqualTo(before.LastPlayerUpdateAt));
            Assert.That(actor.Disconnects, Is.Zero);
            Assert.That(engine.GetSession(session)!.AccountId, Is.Null, "State 1 is not an authenticated account.");
        });
        clock.Advance(2.9); Maintain();
        Assert.That(actor.Disconnects, Is.Zero);
        clock.Advance(.1); Maintain(); Maintain();
        Assert.That(actor.Disconnects, Is.EqualTo(1));
        Assert.That(actor.LastReason, Does.EndWith("authentication-phase-timeout"));
        AssertNoSanctions();
    }

    [Test]
    public void NativePasswordHandshakeAdvancesOnlyWhenTheServerAcceptsThePassword()
    {
        Netplay.ServerPassword = "isolated-native-fixture";
        Native(1, "Terraria326");
        Assert.That(client.State, Is.EqualTo(-1));
        Maintain();
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        Assert.That(socket.Sent.Single()[2], Is.EqualTo(37));
        clock.Advance(1.9); Native(38, Netplay.ServerPassword);
        Assert.That(client.State, Is.EqualTo(1));
        clock.Advance(.2); Maintain();
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
        Assert.That(socket.Sent.Select(frame => frame[2]), Is.EqualTo(new byte[] { 37, 3 }));
        Assert.That(actor.Disconnects, Is.Zero);
        AssertNoSanctions();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeRejectedVersionOrPasswordNeverCreatesAnAcceptedPhase(bool password)
    {
        if (password)
        {
            Netplay.ServerPassword = "isolated-native-fixture";
            Native(1, "Terraria326");
            Native(38, "incorrect-fixture-value");
        }
        else Native(1, "Terraria325");
        Maintain();
        Assert.That(client.State, Is.EqualTo(password ? -1 : 0));
        Assert.That(socket.Sent.Last()[2], Is.EqualTo(2), "The actual native rejection reply remains intact.");
        Assert.That(client.PendingTermination, Is.True);
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        AssertNoSanctions();
    }

    [TestCase(1, 0, ConnectionPhase.Handshake)]
    [TestCase(8, 2, ConnectionPhase.Authenticating)]
    [TestCase(12, 3, ConnectionPhase.WorldSync)]
    public void EarlierNativeGetDataCancellationCannotBeInferredAsACommit(int packet, int initialState, ConnectionPhase phase)
    {
        client.State = initialState; Maintain();
        var before = network.CapturePhase(session)!;
        int canceled = 0;
        void Cancel(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (ReferenceEquals(args.Instance, buffer)) { args.Result = OTAPI.HookResult.Cancel; canceled++; }
        }
        OTAPI.Hooks.MessageBuffer.GetData += Cancel;
        try
        {
            client.TimeOutTimer = 500;
            Native(packet, packet == 1 ? "Terraria326" : null);
            clock.Advance(.5); Maintain();
            Assert.That(canceled, Is.EqualTo(1));
            Assert.That(client.State, Is.EqualTo(initialState));
            Assert.That(client.TimeOutTimer, Is.Zero, "The native pre-hook receipt timer reset is not claimed to be rolled back.");
            Assert.That(socket.Sent, Is.Empty);
            Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(phase));
            Assert.That(network.CapturePhase(session)!.PhaseEnteredAt, Is.EqualTo(before.PhaseEnteredAt));
            AssertNoSanctions();
        }
        finally { OTAPI.Hooks.MessageBuffer.GetData -= Cancel; }
    }

    [TestCase(-1, ConnectionPhase.Handshake)]
    [TestCase(0, ConnectionPhase.Handshake)]
    [TestCase(1, ConnectionPhase.Authenticating)]
    [TestCase(2, ConnectionPhase.Authenticating)]
    [TestCase(3, ConnectionPhase.WorldSync)]
    [TestCase(10, ConnectionPhase.Playing)]
    public void RealTargetObjectInputMapsOnlyAuditedServerStatesWithoutSocketCalls(int state, ConnectionPhase phase)
    {
        client.State = state;
        socket.RejectQueries = true;
        var accepted = Read();
        Assert.That(accepted, Is.EqualTo(new M10AcceptedConnectionPhase(state, phase)));
        Maintain();
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(phase));
        Assert.That(socket.Sent, Is.Empty);
        AssertNoSanctions();
    }

    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(9)]
    [TestCase(11)]
    [TestCase(99)]
    public void UnknownNativeStateSkipsOnlyThePhaseDeadlineAndRetainsIndependentIdleExpiry(int state)
    {
        client.State = state;
        Assert.That(Read(), Is.Null);
        clock.Advance(2); Maintain();
        Assert.That(actor.Disconnects, Is.Zero);
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        clock.Advance(28); Maintain();
        Assert.That(actor.Disconnects, Is.EqualTo(1));
        Assert.That(actor.LastReason, Does.EndWith("connection-idle-ttl"));
        AssertNoSanctions();
    }

    [Test]
    public void AccountPostLoginDoesNotAssertWorldOrSscCompletionAndLogoutGetsANewGeneration()
    {
        client.State = 1;
        actor.Account = new UserAccount { ID = 1010, Name = "m10-phase-fixture" };
        actor.IsLoggedIn = true; actor.HasSentInventory = true;
        Call<Action<PlayerPostLoginEventArgs>>("OnLogin")(new PlayerPostLoginEventArgs(actor));
        Assert.That(engine.GetSession(session)!.AccountId, Is.EqualTo(1010));
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        Maintain();
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
        var oldTimeout = new NetworkControlDecision(session, NetworkDisposition.Disconnect, Verdict.ResourceAbuse, "fixture-old-timeout");
        client.State = 10; Maintain();
        actor.IsLoggedIn = false;
        Call<Action<PlayerLogoutEventArgs>>("OnLogout")(new PlayerLogoutEventArgs(actor));
        var next = CurrentKey();
        Assert.That(next, Is.Not.EqualTo(session));
        Assert.That(next.Generation, Is.GreaterThan(session.Generation));
        Assert.That(network.CapturePhase(session), Is.Null);
        Assert.That(network.CapturePhase(next)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        Assert.That(engine.GetSession(next)!.AccountId, Is.Null);
        Call<Action<NetworkControlDecision>>("DisconnectTimedOutConnection")(oldTimeout);
        Assert.That(actor.Disconnects, Is.Zero);
        Maintain();
        Assert.That(network.CapturePhase(next)!.Phase, Is.EqualTo(ConnectionPhase.Playing));
        AssertNoSanctions();
    }

    [TestCase("player")]
    [TestCase("client")]
    [TestCase("socket")]
    [TestCase("world")]
    [TestCase("generation")]
    [TestCase("run")]
    public void ReusedIdentityCannotBeSampledOrReceiveTheOldTimeout(string changed)
    {
        var timeout = new NetworkControlDecision(session, NetworkDisposition.Disconnect, Verdict.ResourceAbuse, "fixture-old-timeout");
        if (changed == "player") ServerTShock.Players[Slot] = new CapturingPlayer(Slot);
        else if (changed == "client") Netplay.Clients[Slot] = new RemoteClient { Id = Slot, Socket = socket, State = 10 };
        else if (changed == "socket") client.Socket = new FixtureSocket();
        else
        {
            var next = changed switch
            {
                "world" => session with { WorldEpoch = session.WorldEpoch + 1 },
                "generation" => session with { Generation = session.Generation + 1 },
                _ => session with { ServerRunId = Guid.NewGuid() }
            };
            var bindingType = CurrentBinding().GetType();
            var nextBinding = Activator.CreateInstance(bindingType, next, actor)!;
            bindingType.GetProperty("NetworkRegistered")!.SetValue(nextBinding, true);
            bindingType.GetProperty("NetworkTransport")!.SetValue(nextBinding, new M10ConnectionPhaseAdapter(next, actor, client, socket));
            Bindings().SetValue(nextBinding, Slot);
        }
        Assert.That(Read(), Is.Null);
        Call<Action<NetworkControlDecision>>("DisconnectTimedOutConnection")(timeout);
        Assert.That(actor.Disconnects, Is.Zero);
        if (ServerTShock.Players[Slot] is CapturingPlayer replacement) Assert.That(replacement.Disconnects, Is.Zero);
        Assert.That(socket.Sent, Is.Empty);
        AssertNoSanctions();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ActualIdleAndUpdateMaintenanceCallbacksObserveWithoutAnotherPacket(bool active)
    {
        client.State = 1; clock.Advance(2.1);
        Netplay.HasFullyConnectedClients = active;
        if (active) Call<Action<EventArgs>>("OnUpdate")(EventArgs.Empty);
        else Call<Action>("OnIdleMaintenance")();
        Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
        Assert.That(actor.Disconnects, Is.Zero);
        AssertNoSanctions();
    }

    [Test]
    public void RawTrafficDoesNotAdvancePhaseAndOnlyAParsedPlayerUpdateRefreshesHeartbeat()
    {
        client.State = 10; clock.Advance(1);
        var packet = M2ContractsTests.Packet((PacketTypes)1, [], Slot);
        Call<HookHandler<GetDataEventArgs>>("OnGetData")(packet);
        Assert.That(packet.Handled, Is.False);
        var receipt = network.CapturePhase(session)!;
        Assert.That(receipt.Phase, Is.EqualTo(ConnectionPhase.Handshake));
        Assert.That(receipt.LastPacketAt, Is.EqualTo(clock.GetTimestamp()));
        Maintain();
        Assert.That(network.CapturePhase(session)!.LastPlayerUpdateAt, Is.EqualTo(receipt.LastPlayerUpdateAt));
        clock.Advance(1);
        byte[] movement = new byte[14]; movement[0] = Slot;
        var update = M2ContractsTests.Packet(PacketTypes.PlayerUpdate, movement, Slot);
        Call<HookHandler<GetDataEventArgs>>("OnGetData")(update);
        Assert.That(update.Handled, Is.False);
        Assert.That(network.CapturePhase(session)!.LastPlayerUpdateAt, Is.EqualTo(clock.GetTimestamp()));
        AssertNoSanctions();
    }

    [Test]
    public void PhaseSamplingCannotRestoreAnAlreadyRevokedAuthenticatedSession()
    {
        Assert.That(engine.Authenticate(session, 1010), Is.EqualTo(AuthenticationResult.Authenticated));
        var proof = ProtocolRules.EvaluateIdentity(SelfIdentityMessage.Emoji, Slot, Slot + 1, true, true, true);
        engine.ObserveBusiness(new(session, 120, proof, new(Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
        Assert.That(engine.CanWrite(session), Is.False);
        client.State = 10; Maintain();
        var packet = M2ContractsTests.Packet((PacketTypes)1, [], Slot);
        Call<HookHandler<GetDataEventArgs>>("OnGetData")(packet);
        Assert.That(packet.Handled, Is.True);
        Assert.That(engine.CanWrite(session), Is.False);
        Assert.That(engine.SanctionCount, Is.EqualTo(1));
    }

    [Test]
    public void UnverifiedRuntimeCannotInterpretNativePhases()
    {
        client.State = 10;
        Set("_targetRuntime", new TargetRuntimeStatus(false, "unknown", "unknown", "fixture-unverified"));
        Assert.That(Read(), Is.Null);
        clock.Advance(2); Maintain();
        Assert.That(actor.Disconnects, Is.Zero);
        AssertNoSanctions();
    }

    [TestCase("players", false)]
    [TestCase("players", true)]
    [TestCase("clients", false)]
    [TestCase("clients", true)]
    public void MissingNativeTableCannotFailInfrastructureEvenWhenIndependentIdleExpires(string missing, bool expireIdle)
    {
        var players = ServerTShock.Players;
        var clients = Netplay.Clients;
        try
        {
            if (missing == "players") ServerTShock.Players = null!;
            else Netplay.Clients = null!;
            clock.Advance(2.1);
            Assert.DoesNotThrow(Maintain);
            Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Handshake));
            Assert.That(actor.Disconnects, Is.Zero);
            Assert.That(typeof(AntiCheatPlugin).GetField("_infrastructureFailed", Private)!.GetValue(plugin), Is.False);
            if (expireIdle)
            {
                clock.Advance(27.9);
                Assert.DoesNotThrow(() => Call<Action>("OnIdleMaintenance")());
                Assert.That(network.CapturePhase(session), Is.Null, "Independent idle expiry still releases its exact bucket.");
                Assert.That(actor.Disconnects, Is.Zero, "The absent table cannot authorize a disconnect of an unverified transport.");
                Assert.That(typeof(AntiCheatPlugin).GetField("_infrastructureFailed", Private)!.GetValue(plugin), Is.False);
                Assert.That(typeof(AntiCheatPlugin).GetField("_maintenanceCallbackFailed", Private)!.GetValue(plugin), Is.Zero);
            }
        }
        finally { ServerTShock.Players = players; Netplay.Clients = clients; }
        if (!expireIdle)
        {
            client.State = 1; Maintain();
            Assert.That(network.CapturePhase(session)!.Phase, Is.EqualTo(ConnectionPhase.Authenticating));
        }
        AssertNoSanctions();
    }

    [Test]
    public void ANewSocketOnTheSamePlayerObjectRequiresANewConnectionGeneration()
    {
        var oldTimeout = new NetworkControlDecision(session, NetworkDisposition.Disconnect, Verdict.ResourceAbuse, "fixture-old-timeout");
        client.Socket = new FixtureSocket();
        var connect = new ConnectEventArgs();
        typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
        Call<HookHandler<ConnectEventArgs>>("OnConnect")(connect);
        var next = CurrentKey();
        Assert.That(connect.Handled, Is.False);
        Assert.That(next.Generation, Is.GreaterThan(session.Generation));
        Assert.That(network.CapturePhase(session), Is.Null);
        Assert.That(network.CapturePhase(next), Is.Not.Null);
        Call<Action<NetworkControlDecision>>("DisconnectTimedOutConnection")(oldTimeout);
        Assert.That(actor.Disconnects, Is.Zero);
        AssertNoSanctions();
    }

    private void Native(int packet, string? value)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true)) if (value is not null) writer.Write(value);
        var bytes = payload.ToArray();
        var receipt = M2ContractsTests.Packet((PacketTypes)packet, bytes, Slot);
        Call<HookHandler<GetDataEventArgs>>("OnGetData")(receipt);
        Assert.That(receipt.Handled, Is.False);
        buffer.readBuffer[0] = (byte)packet;
        bytes.CopyTo(buffer.readBuffer, 1);
        buffer.GetData(0, bytes.Length + 1, out int actual);
        Assert.That(actual, Is.EqualTo(packet));
    }

    private void Maintain() => Call<Action<string>>("MaintainNetworkConnections")("AdapterFixture");
    private M10AcceptedConnectionPhase? Read() => Call<Func<SessionKey, M10AcceptedConnectionPhase?>>("ReadAcceptedConnectionPhase")(session);
    private Array Bindings() => (Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!;
    private object CurrentBinding() => Bindings().GetValue(Slot)!;
    private SessionKey CurrentKey() => (SessionKey)CurrentBinding().GetType().GetProperty("Key")!.GetValue(CurrentBinding())!;
    private T Call<T>(string method) where T : Delegate => typeof(AntiCheatPlugin).GetMethod(method, Private)!.CreateDelegate<T>(plugin);
    private void Set(string field, object value) => typeof(AntiCheatPlugin).GetField(field, Private)!.SetValue(plugin, value);
    private void AssertNoSanctions()
    {
        Assert.That(engine.SanctionCount, Is.Zero);
        Assert.That(store.Bans, Is.Zero);
        Assert.That(store.Intents, Is.Zero);
    }

    private void PreserveLegacyLanguage()
    {
        foreach (var field in typeof(Lang).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (field.IsLiteral) continue;
            object? original = field.GetValue(null);
            if (original is Array array)
            {
                var contents = (Array)array.Clone();
                restoreLegacyLanguage.Add(() =>
                {
                    Array.Copy(contents, array, array.Length);
                    if (!field.IsInitOnly) field.SetValue(null, array);
                });
            }
            else if (original is IDictionary dictionary)
            {
                var contents = dictionary.Keys.Cast<object>().Select(key => new DictionaryEntry(key, dictionary[key])).ToArray();
                restoreLegacyLanguage.Add(() =>
                {
                    dictionary.Clear();
                    foreach (var entry in contents) dictionary.Add(entry.Key, entry.Value);
                    if (!field.IsInitOnly) field.SetValue(null, dictionary);
                });
            }
            else if (!field.IsInitOnly) restoreLegacyLanguage.Add(() => field.SetValue(null, original));
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => now;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(now);
        public void Advance(double seconds) => now += (long)Math.Round(seconds * 1000);
    }
    private sealed class CapturingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public string? LastReason;
        public override void Disconnect(string reason) { Disconnects++; LastReason = reason; }
    }
    private sealed class FixtureSocket : ISocket
    {
        public readonly List<byte[]> Sent = [];
        public bool RejectQueries;
        public void Close() { }
        public bool IsConnected() => RejectQueries ? throw new InvalidOperationException("phase-read-must-not-query-socket") : true;
        public RemoteAddress GetRemoteAddress() => RejectQueries ? throw new InvalidOperationException("phase-read-must-not-query-address") : new TcpAddress(IPAddress.Loopback, 17150);
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) => Sent.Add(data.AsSpan(offset, size).ToArray());
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() { }
    }
    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
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
