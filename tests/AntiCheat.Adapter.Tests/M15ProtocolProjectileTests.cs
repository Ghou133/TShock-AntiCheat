using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Events;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M15ProtocolProjectileTests
{
    private const int Slot = 21;
    private int oldMode, oldMyPlayer, oldCredits;
    private bool oldDedicated;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private Player oldPlayer = null!;
    private FixtureSocket socket = null!;
    private readonly List<(int Id, int Number, float Number2)> sends = [];
    private bool serialize;
    private readonly SessionKey session = new(Guid.NewGuid(), 1, Slot, 1);
    private TSPlayer actor = null!;
    private static readonly FieldInfo CreditsField = typeof(CreditsRollEvent).GetField("_creditsRollRemainingTime", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static int Credits => (int)CreditsField.GetValue(null)!;

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ; oldCredits = Credits;
        oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot]; oldPlayer = Main.player[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        socket = new FixtureSocket(); Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 10, Socket = socket };
        NetMessage.buffer[Slot] = new MessageBuffer();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, Group = new Group("m15-native-contract"),
            Account = new UserAccount { ID = 1551, Name = "m15-native-contract" } };
        sends.Clear(); serialize = false; HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture; CreditsRollEvent.SetRemainingTimeDirect(oldCredits);
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer; Main.player[Slot] = oldPlayer;
    }

    private void Capture(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add((args.msgType, args.number, args.number2)); if (!serialize) args.ContinueExecution = false; }

    private byte[] Serialize(Action action, int id, int bodyLength)
    {
        socket.Sent.Clear(); serialize = true;
        try { action(); } finally { serialize = false; }
        byte[] frame = socket.Sent.Single();
        Assert.That(frame[2], Is.EqualTo(id)); Assert.That(frame.Length, Is.EqualTo(bodyLength + 3));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(frame.Length));
        return frame[3..];
    }

    private static void Receive(int id, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = (byte)id;
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out int actual);
        Assert.That(actual, Is.EqualTo(id));
    }

    [Test]
    public void M15Protocol_NativeServerCreditsProjectionAndPassiveClientReceiptDoNotEcho()
    {
        CreditsRollEvent.SetRemainingTimeDirect(28800);
        byte[] body = Serialize(() => CreditsRollEvent.SendCreditsRollRemainingTimeToPlayer(Slot), 140, 5);
        var state = M15ProtocolPacketReader.ReadPayload(140, body).Packet!;
        Assert.That(state, Is.EqualTo(new M15CreditsRollObservation(0, 28800)));
        Assert.That(M15ProtocolRules.Evaluate(state, new(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint,
            true, true, true, true, false), true).Verdict, Is.EqualTo(Verdict.Pass));
        CreditsRollEvent.SetRemainingTimeDirect(1); sends.Clear(); Main.netMode = 1; Main.myPlayer = Slot;
        Receive(140, body); Assert.That(Credits, Is.EqualTo(28800)); Assert.That(sends, Is.Empty);
        CreditsRollEvent.SendCreditsRollRemainingTimeToPlayer(Slot); Assert.That(sends, Is.Empty);
    }

    [Test]
    public void M15Protocol_RealCopperAndElderClientRequestsAreDifferentAllowedSuboperations()
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        NPC.TransformCopperSlime(11); NPC.TransformElderSlime(12);
        Assert.That(sends, Is.EqualTo(new[] { (140, 1, 11f), (140, 2, 12f) }));
        foreach (var message in sends)
            Assert.That(M15ProtocolRules.Evaluate(new((byte)message.Number, (int)message.Number2),
                new(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true), true).Verdict,
                Is.EqualTo(Verdict.Pass));
    }

    [TestCase(140)] [TestCase(108)]
    public void M15ProtocolProjectile_ServerReceivesPublicationAsNoopAndReaderIndependentlyProvesRole(int id)
    {
        CreditsRollEvent.SetRemainingTimeDirect(1234); var projectiles = Main.projectile;
        static (bool Present, bool Active, int Type, int Owner)[] ProjectileState() => Main.projectile
            .Select(p => p is null ? (false, false, 0, 0) : (true, p.active, p.type, p.owner)).ToArray();
        var beforeProjectiles = ProjectileState();
        var body = id == 140 ? new byte[5] : new byte[15]; Receive(id, body);
        Assert.That(Credits, Is.EqualTo(1234)); Assert.That(Main.projectile, Is.SameAs(projectiles)); Assert.That(sends, Is.Empty);
        Assert.That(ProjectileState(), Is.EqualTo(beforeProjectiles), "Native creation would mutate the array entries in place.");
        var result = id == 140
            ? M15ProtocolPacketReader.Evaluate(M15ProtocolPacketReader.ReadPayload(id, body).Packet!, session, actor, TargetRuntime.Fingerprint, true)
            : M15ProjectilePacketReader.Evaluate(M15ProjectilePacketReader.ReadPayload(id, body).Packet!, session, actor, TargetRuntime.Fingerprint, true);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [TestCase(4)] [TestCase(5)]
    public void M15Projectile_NativeServerDelegatesAndOnlyOwningClientCreatesCannonShotWithout108Echo(int ammo)
    {
        byte[] body = Serialize(() => WorldGen.ShootFromCannon(20, 30, 4, ammo, 50, 3, Slot, true), 108, 15);
        var state = M15ProjectilePacketReader.ReadPayload(108, body).Packet!;
        Assert.That(state, Is.EqualTo(new M15CannonFiringObservation(50, 3, 20, 30, 4, (short)ammo, Slot)));
        Assert.That(M15ProjectileRules.Evaluate(state, new(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint,
            true, true, true, true, false), true).Verdict, Is.EqualTo(Verdict.Pass));
        var savedProjectiles = Main.projectile; var savedMap = Projectile.keyToIndex;
        try
        {
            Main.projectile = Enumerable.Range(0, Main.maxProjectiles).Select(i => new Projectile { whoAmI = i }).ToArray();
            Projectile.keyToIndex = new int[256, 1001];
            Main.netMode = 1; Main.myPlayer = Slot; sends.Clear();
            var other = (byte[])body.Clone(); other[14] = Slot + 1; Receive(108, other);
            Assert.That(Main.projectile.Any(p => p.active), Is.False); Assert.That(sends, Is.Empty);
            Receive(108, body);
            var shot = Main.projectile.Single(p => p.active);
            Assert.That(shot.type, Is.EqualTo(601)); Assert.That(shot.owner, Is.EqualTo(Slot));
            Assert.That(shot.damage, Is.EqualTo(50)); Assert.That(shot.ai[0], Is.EqualTo(ammo == 5 ? 1f : 0f));
            Assert.That(shot.originatedFromActivableTile, Is.True);
            Assert.That(sends.Count(p => p.Id == 108), Is.Zero, "Owning client creates a normal projectile; it does not echo the server instruction.");
        }
        finally { Main.projectile = savedProjectiles; Projectile.keyToIndex = savedMap; }
    }

    [TestCase(140)] [TestCase(108)]
    public void M15ProtocolProjectile_ReadersRequireExactFrameVerifiedRuntimeAndCurrentIdentity(int id)
    {
        int size = id == 140 ? 5 : 15;
        foreach (int length in new[] { 0, size - 1, size + 1, 100 })
            Assert.That(id == 140 ? M15ProtocolPacketReader.ReadPayload(id, new byte[length]).Kind :
                M15ProjectilePacketReader.ReadPayload(id, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)id, new byte[size], Slot); args.Handled = true;
        Assert.That(id == 140 ? M15ProtocolPacketReader.Read(args, false).Kind : M15ProjectilePacketReader.Read(args, false).Kind,
            Is.EqualTo(PacketReadKind.UnknownRuntime));
        BusinessRuleResult Evaluate(bool host) => id == 140
            ? M15ProtocolPacketReader.Evaluate(M15ProtocolPacketReader.Read(args, true).Packet!, session, actor, TargetRuntime.Fingerprint, host)
            : M15ProjectilePacketReader.Evaluate(M15ProjectilePacketReader.Read(args, true).Packet!, session, actor, TargetRuntime.Fingerprint, host);
        Assert.That(Evaluate(false).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false; Assert.That(Evaluate(true).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(args.Handled, Is.True);
        args.Index = args.Msg.readBuffer.Length;
        Assert.That(id == 140 ? M15ProtocolPacketReader.Read(args, true).Kind : M15ProjectilePacketReader.Read(args, true).Kind,
            Is.EqualTo(PacketReadKind.Malformed));
    }

    private sealed class FixtureSocket : ISocket
    {
        public List<byte[]> Sent { get; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) => Sent.Add(data.AsSpan(offset, size).ToArray());
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() { }
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 17855);
    }
}
