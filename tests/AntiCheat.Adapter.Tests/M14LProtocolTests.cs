using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M14LProtocolTests
{
    private const int Slot = 21;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private int[] saved = [];
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private FixtureSocket socket = null!;
    private readonly List<int> sends = [];
    private bool serialize;
    private readonly SessionKey session = new(Guid.NewGuid(), 1, Slot, 1);
    private TSPlayer actor = null!;
    private static int[] State() => [NPC.ShieldStrengthTowerSolar, NPC.ShieldStrengthTowerVortex,
        NPC.ShieldStrengthTowerNebula, NPC.ShieldStrengthTowerStardust, NPC.MaxMoonLordCountdown, NPC.MoonLordCountdown];
    private static void State(int[] values)
    {
        NPC.ShieldStrengthTowerSolar = values[0]; NPC.ShieldStrengthTowerVortex = values[1];
        NPC.ShieldStrengthTowerNebula = values[2]; NPC.ShieldStrengthTowerStardust = values[3];
        NPC.MaxMoonLordCountdown = values[4]; NPC.MoonLordCountdown = values[5];
    }

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ; saved = State();
        oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true;
        socket = new FixtureSocket(); Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 10, Socket = socket };
        NetMessage.buffer[Slot] = new MessageBuffer();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, Group = new Group("m14l-native-contract"),
            Account = new UserAccount { ID = 1451, Name = "m14l-native-contract" } };
        sends.Clear(); serialize = false; HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture; State(saved);
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer;
    }
    private void Capture(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add(args.msgType); if (!serialize) args.ContinueExecution = false; }
    private byte[] Serialize(int message)
    {
        Main.netMode = 2; Main.myPlayer = 255; socket.Sent.Clear(); serialize = true;
        try { NetMessage.SendData(message, Slot); } finally { serialize = false; }
        var frame = socket.Sent.Single();
        Assert.That(frame[2], Is.EqualTo(message)); Assert.That(frame.Length, Is.EqualTo(11));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(frame.Length));
        return frame[3..];
    }
    private static void Receive(int message, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = (byte)message;
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out int actual);
        Assert.That(actual, Is.EqualTo(message));
    }

    [TestCase(101)] [TestCase(103)]
    public void ActualNativeSerializationPassiveReceiptAndCoreNoopAreDistinct(int id)
    {
        // Preserve the actual UInt16 projection and Int32 signs, not an assumed event magnitude range.
        State([65536, 65535, 1, 0, int.MinValue, int.MaxValue]);
        var body = Serialize(id); var read = M14LProtocolPacketReader.ReadPayload(id, body);
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(read.Packet!.Fields, Is.EqualTo(id == 101 ? new[] { 0, 65535, 1, 0 } : new[] { int.MinValue, int.MaxValue }));
        var serverInput = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true, false);
        Assert.That(M14LProtocolRules.Evaluate(read.Packet, serverInput, true).Verdict, Is.EqualTo(Verdict.Pass));
        State([10, 11, 12, 13, 14, 15]); sends.Clear(); var before = State(); Receive(id, body);
        Assert.That(State(), Is.EqualTo(before)); Assert.That(sends, Is.Empty, "Core already ignores these client publications.");
        Assert.That(M14LProtocolPacketReader.Evaluate(read.Packet, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.ProvenCheat), "No inventory/SSC completion is relevant to this literal publisher role.");
        Main.netMode = 1; Main.myPlayer = Slot; Receive(id, body);
        var projected = id == 101 ? read.Packet.Fields.Select(value => Math.Min(value, NPC.LunarShieldPowerMax)).ToArray()
            : read.Packet.Fields.ToArray();
        Assert.That(id == 101 ? State()[..4] : State()[4..], Is.EqualTo(projected),
            "Native client clamps tower shields after UInt16 receipt; countdown retains signed Int32.");
        Assert.That(sends, Is.Empty, "A passive client state receipt has no echo.");
    }

    [TestCase(101)] [TestCase(103)]
    public void ExactFramingVersionCurrentIdentityAndHostRemainNecessary(int id)
    {
        foreach (int length in new[] { 0, 1, 7, 9, 100 })
            Assert.That(M14LProtocolPacketReader.ReadPayload(id, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)id, new byte[8], Slot); args.Handled = true;
        Assert.That(M14LProtocolPacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        var parsed = M14LProtocolPacketReader.Read(args, true).Packet!;
        Assert.That(args.Handled, Is.True);
        Assert.That(M14LProtocolPacketReader.Evaluate(parsed, session, actor, TargetRuntime.Fingerprint, false).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false;
        Assert.That(M14LProtocolPacketReader.Evaluate(parsed, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Unknown));
        args.Index = args.Msg.readBuffer.Length;
        Assert.That(M14LProtocolPacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M14LProtocolPacketReader.ReadPayload(61, new byte[4]).Kind, Is.EqualTo(PacketReadKind.Unrelated));
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
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 17852);
    }
}
