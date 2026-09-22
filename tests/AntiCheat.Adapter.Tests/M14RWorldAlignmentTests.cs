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
public sealed class M14RWorldAlignmentTests
{
    private const int Slot = 21;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private byte[] saved = [];
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private FixtureSocket socket = null!;
    private readonly List<int> sends = [];
    private bool serialize;
    private readonly SessionKey session = new(Guid.NewGuid(), 1, Slot, 1);
    private TSPlayer actor = null!;
    private static byte[] State() => [WorldGen.tGood, WorldGen.tEvil, WorldGen.tBlood];
    private static void State(byte[] values)
    { WorldGen.tGood = values[0]; WorldGen.tEvil = values[1]; WorldGen.tBlood = values[2]; }

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ; saved = State();
        oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true;
        socket = new FixtureSocket(); Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 10, Socket = socket };
        NetMessage.buffer[Slot] = new MessageBuffer();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, Group = new Group("m14r-world-contract"),
            Account = new UserAccount { ID = 1461, Name = "m14r-world-contract" } };
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

    private byte[] Serialize()
    {
        socket.Sent.Clear(); serialize = true;
        try { NetMessage.SendData(57, Slot); } finally { serialize = false; }
        var frame = socket.Sent.Single();
        Assert.That(frame[2], Is.EqualTo(57)); Assert.That(frame.Length, Is.EqualTo(6));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(frame.Length));
        return frame[3..];
    }

    private static void Receive(byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = 57;
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out int actual);
        Assert.That(actual, Is.EqualTo(57));
    }

    [TestCase((byte)0, (byte)0, (byte)0)] [TestCase((byte)255, (byte)254, (byte)253)]
    public void NativeServerSerializationAndPassiveClientReceiptPreserveFullByteDomain(byte h, byte c, byte r)
    {
        State([h, c, r]); var body = Serialize();
        var parsed = M14RWorldAlignmentPacketReader.ReadPayload(57, body);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(parsed.Packet, Is.EqualTo(new M14RWorldAlignmentObservation(h, c, r)));
        var input = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true, false);
        Assert.That(M14RWorldAlignmentRules.Evaluate(parsed.Packet!, input, true).Verdict, Is.EqualTo(Verdict.Pass));
        State([7, 8, 9]); sends.Clear(); Main.netMode = 1; Main.myPlayer = Slot; Receive(body);
        Assert.That(State(), Is.EqualTo(body)); Assert.That(sends, Is.Empty, "Passive state reception never echoes packet57.");
    }

    [Test]
    public void NativeServerAlreadyIgnoresClientPublicationButIndependentRoleProofStillApplies()
    {
        State([7, 8, 9]); sends.Clear(); Receive([255, 254, 253]);
        Assert.That(State(), Is.EqualTo(new byte[] { 7, 8, 9 })); Assert.That(sends, Is.Empty);
        var state = M14RWorldAlignmentPacketReader.ReadPayload(57, new byte[] { 255, 254, 253 }).Packet!;
        Assert.That(M14RWorldAlignmentPacketReader.Evaluate(state, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ExactFramingRuntimeAuthenticatedSubjectAndHostAreRequired()
    {
        foreach (int length in new[] { 0, 1, 2, 4, 100 })
            Assert.That(M14RWorldAlignmentPacketReader.ReadPayload(57, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)57, new byte[3], Slot); args.Handled = true;
        Assert.That(M14RWorldAlignmentPacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        var parsed = M14RWorldAlignmentPacketReader.Read(args, true).Packet!; Assert.That(args.Handled, Is.True);
        Assert.That(M14RWorldAlignmentPacketReader.Evaluate(parsed, session, actor, TargetRuntime.Fingerprint, false).Verdict,
            Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false;
        Assert.That(M14RWorldAlignmentPacketReader.Evaluate(parsed, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Unknown));
        args.Index = args.Msg.readBuffer.Length;
        Assert.That(M14RWorldAlignmentPacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M14RWorldAlignmentPacketReader.ReadPayload(58, new byte[3]).Kind, Is.EqualTo(PacketReadKind.Unrelated));
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
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 17853);
    }
}
