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
public sealed class M14RCavernMonsterTests
{
    private const int Slot = 21;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private int[,] oldTypes = null!;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private FixtureSocket socket = null!;
    private readonly List<int> sends = [];
    private bool serialize;
    private readonly SessionKey session = new(Guid.NewGuid(), 1, Slot, 1);
    private TSPlayer actor = null!;

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ;
        oldTypes = NPC.cavernMonsterType; oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true; NPC.cavernMonsterType = new int[2, 3];
        socket = new FixtureSocket(); Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 10, Socket = socket };
        NetMessage.buffer[Slot] = new MessageBuffer();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, Group = new Group("m14r-cavern-contract"),
            Account = new UserAccount { ID = 1471, Name = "m14r-cavern-contract" } };
        sends.Clear(); serialize = false; HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture; NPC.cavernMonsterType = oldTypes;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer;
    }

    private void Capture(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add(args.msgType); if (!serialize) args.ContinueExecution = false; }

    private static void Receive(byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = 136;
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out int actual);
        Assert.That(actual, Is.EqualTo(136));
    }

    [Test]
    public void NativeServerProjectionAndRowMajorPassiveReceiptAreAllowedAndDoNotEcho()
    {
        NPC.cavernMonsterType = new[,] { { 65536, -1, 1 }, { 2, 3, 4 } };
        serialize = true;
        try { NetMessage.SendData(136, Slot); } finally { serialize = false; }
        var frame = socket.Sent.Single(); Assert.That(frame[2], Is.EqualTo(136)); Assert.That(frame.Length, Is.EqualTo(15));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(frame.Length));
        var parsed = M14RCavernMonsterPacketReader.ReadPayload(136, frame.AsSpan(3));
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(parsed.Packet!.Types, Is.EqualTo(new[] { 0, 65535, 1, 2, 3, 4 }));
        var input = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true, false);
        Assert.That(M14RCavernMonsterRules.Evaluate(parsed.Packet, input, true).Verdict, Is.EqualTo(Verdict.Pass));
        NPC.cavernMonsterType = new int[2, 3]; sends.Clear(); Main.netMode = 1; Main.myPlayer = Slot; Receive(frame[3..]);
        Assert.That(NPC.cavernMonsterType.Cast<int>(), Is.EqualTo(parsed.Packet.Types));
        Assert.That(sends, Is.Empty, "Passive matrix receipt has no native packet136 echo.");
    }

    [Test]
    public void NativeServerIgnoresTheAttemptButExactPublicationStillProvidesIndependentRoleProof()
    {
        NPC.cavernMonsterType = new[,] { { 1, 2, 3 }, { 4, 5, 6 } }; sends.Clear(); Receive(new byte[12]);
        Assert.That(NPC.cavernMonsterType.Cast<int>(), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6 }));
        Assert.That(sends, Is.Empty);
        var parsed = M14RCavernMonsterPacketReader.ReadPayload(136, new byte[12]);
        Assert.That(M14RCavernMonsterPacketReader.Evaluate(parsed.Packet!, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void CompleteFrameCurrentIdentityVerifiedRuntimeAndHostRemainNecessary()
    {
        foreach (int length in new[] { 0, 1, 11, 13, 100 })
            Assert.That(M14RCavernMonsterPacketReader.ReadPayload(136, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)136, new byte[12], Slot); args.Handled = true;
        Assert.That(M14RCavernMonsterPacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        var parsed = M14RCavernMonsterPacketReader.Read(args, true).Packet!; Assert.That(args.Handled, Is.True);
        Assert.That(M14RCavernMonsterPacketReader.Evaluate(parsed, session, actor, TargetRuntime.Fingerprint, false).Verdict,
            Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false;
        Assert.That(M14RCavernMonsterPacketReader.Evaluate(parsed, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Unknown));
        args.Index = args.Msg.readBuffer.Length;
        Assert.That(M14RCavernMonsterPacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M14RCavernMonsterPacketReader.ReadPayload(137, new byte[12]).Kind, Is.EqualTo(PacketReadKind.Unrelated));
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
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 17854);
    }
}
