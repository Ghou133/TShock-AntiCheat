using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M14NpcBuffStateTests
{
    private const int Slot = 21, NpcSlot = 3;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private NPC[] oldNpcs = null!;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private readonly List<int> sends = [];
    private FixtureSocket socket = null!;
    private bool serialize;
    private TSPlayer actor = null!;
    private readonly SessionKey session = new(Guid.NewGuid(), 1, Slot, 1);
    private NPC Target => Main.npc[NpcSlot];

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ;
        oldNpcs = Main.npc; oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true;
        Main.npc = Enumerable.Range(0, Main.maxNPCs).Select(i => new NPC { whoAmI = i, active = true }).ToArray();
        socket = new FixtureSocket(); Netplay.Clients[Slot] = new RemoteClient { Id = Slot, State = 10, Socket = socket };
        NetMessage.buffer[Slot] = new MessageBuffer();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, ReceivedInfo = true, HasSentInventory = true,
            Group = new Group("m14-native-contract"), Account = new UserAccount { ID = 1421, Name = "m14-native-contract" } };
        sends.Clear(); serialize = false;
        HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Main.npc = oldNpcs; Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer;
    }

    private void Capture(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        sends.Add(args.msgType);
        if (!serialize) args.ContinueExecution = false;
    }
    private byte[] Serialize()
    {
        Main.netMode = 2; Main.myPlayer = 255; socket.Sent.Clear(); serialize = true;
        try { NetMessage.SendData(54, Slot, number: NpcSlot); }
        finally { serialize = false; }
        byte[] frame = socket.Sent.Single();
        Assert.That(frame[2], Is.EqualTo(54));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(frame.Length));
        return frame[3..];
    }
    private static void Receive(byte message, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = message; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out int actual); Assert.That(actual, Is.EqualTo(message));
    }
    private BusinessRuleResult Evaluate(M14NpcBuffStateObservation state, bool? host = true) =>
        M14NpcBuffStatePacketReader.Evaluate(state, session, actor, TargetRuntime.Fingerprint, host);

    [TestCase(BuffID.OnFire)] [TestCase(BuffID.ScytheWhipEnemyDebuff)] [TestCase(BuffID.EelWhipNPCDebuff)]
    public void OriginalClientAdditionProduces53AndLocalRemovalDoesNotPublishFullState(int type)
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        Target.AddBuff(type, 600);
        Assert.That(sends, Is.EqualTo(new[] { 53 }));
        Assert.That(Target.FindBuffIndex(type), Is.GreaterThanOrEqualTo(0));
        Assert.That(M13NpcBuffPacketReader.Evaluate(new(M13NpcBuffOperation.Add, NpcSlot, type, 600), session,
            TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(M14NpcBuffStatePacketReader.Read(M2ContractsTests.Packet((PacketTypes)53, new byte[6], Slot), true).Kind,
            Is.EqualTo(PacketReadKind.Unrelated));
        sends.Clear(); Target.DelBuff(Target.FindBuffIndex(type));
        Assert.That(Target.FindBuffIndex(type), Is.EqualTo(-1)); Assert.That(sends, Is.Empty);
    }

    [Test]
    public void ActualServerAdditionRemovalAndNaturalExpirationPublish54OnlyFromServer()
    {
        Target.AddBuff(BuffID.OnFire, 600);
        Assert.That(sends, Is.EqualTo(new[] { 54 })); Assert.That(Target.FindBuffIndex(BuffID.OnFire), Is.GreaterThanOrEqualTo(0));
        sends.Clear(); Target.DelBuff(Target.FindBuffIndex(BuffID.OnFire));
        Assert.That(sends, Is.EqualTo(new[] { 54 })); Assert.That(Target.FindBuffIndex(BuffID.OnFire), Is.EqualTo(-1));
        Target.buffType[0] = BuffID.OnFire; Target.buffTime[0] = 0; sends.Clear();
        Main.netMode = 1; Main.myPlayer = Slot; Target.UpdateNPC_BuffClearExpiredBuffs();
        Assert.That(sends, Is.Empty); Assert.That(Target.buffType[0], Is.EqualTo(BuffID.OnFire));
        Main.netMode = 2; Main.myPlayer = 255; Target.UpdateNPC_BuffClearExpiredBuffs();
        Assert.That(sends, Is.EqualTo(new[] { 54 })); Assert.That(Target.buffType[0], Is.Zero);
    }

    [TestCase(0, 600)] [TestCase(1, 600)] [TestCase(20, 600)] [TestCase(1, 65536)]
    public void ActualNativeSerializationAndPassiveClientReceiptAcceptTheFullDomainWithoutEcho(int count, int time)
    {
        for (int i = 0; i < count; i++) { Target.buffType[i] = i + 1; Target.buffTime[i] = time; }
        byte[] body = Serialize();
        Assert.That(body.Length, Is.EqualTo(4 + count * 4));
        var parsed = M14NpcBuffStatePacketReader.ReadPayload(body);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed)); Assert.That(parsed.Packet!.Entries.Length, Is.EqualTo(count));
        if (count > 0) Assert.That(parsed.Packet.Entries[0].Time, Is.EqualTo((ushort)time), "Original serialization uses UInt16, including65536->0.");
        var server = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true, false);
        Assert.That(M14NpcBuffStateRules.Evaluate(parsed.Packet, server, true).Verdict, Is.EqualTo(Verdict.Pass));
        Array.Fill(Target.buffType, BuffID.OnFire); Array.Fill(Target.buffTime, 500);
        Main.netMode = 1; Main.myPlayer = Slot; sends.Clear(); Receive(54, body);
        for (int i = 0; i < 20; i++)
        {
            Assert.That(Target.buffType[i], Is.EqualTo(i < count ? i + 1 : 0));
            Assert.That(Target.buffTime[i], Is.EqualTo(i < count ? (ushort)time : 0));
        }
        Assert.That(sends, Is.Empty, "Normal reception mutates local NPC arrays and cannot echo54 back to the server.");
    }

    [Test]
    public void RealServerReceiverAlreadyIgnores54SoTheProofDoesNotInventBlockedMutation()
    {
        Target.AddBuff(BuffID.OnFire, 600, quiet: true); byte[] body = Serialize();
        var parsed = M14NpcBuffStatePacketReader.ReadPayload(body).Packet!;
        Target.buffType[0] = BuffID.Poisoned; Target.buffTime[0] = 90;
        var beforeTypes = Target.buffType.ToArray(); var beforeTimes = Target.buffTime.ToArray(); sends.Clear();
        Receive(54, body);
        Assert.That(Target.buffType, Is.EqualTo(beforeTypes)); Assert.That(Target.buffTime, Is.EqualTo(beforeTimes)); Assert.That(sends, Is.Empty);
        Assert.That(Evaluate(parsed).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Target.active = false; Main.npc[NpcSlot] = new NPC { whoAmI = NpcSlot, generation = 255 };
        Assert.That(Evaluate(parsed).Verdict, Is.EqualTo(Verdict.ProvenCheat), "Receiver target lifetime is not the authenticated sender identity or a premise.");
    }

    [Test]
    public void ExactFramingUnknownRuntimeAndUnknownHostCannotBeUpgradedFromMalformedOrCoreCancellation()
    {
        byte[] valid = [NpcSlot, 0, 24, 0, 0, 0, 0, 0];
        foreach (int length in new[] { 0, 1, 2, 3, 5, 6, 7, 9, 83, 85, 88, 65535 })
            Assert.That(M14NpcBuffStatePacketReader.ReadPayload(new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M14NpcBuffStatePacketReader.ReadPayload(new byte[8]).Kind, Is.EqualTo(PacketReadKind.Malformed), "Early terminator plus trailing pair is not exact.");
        byte[] noTerminator = (byte[])valid.Clone(); noTerminator[^2] = 1;
        Assert.That(M14NpcBuffStatePacketReader.ReadPayload(noTerminator).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)54, valid, Slot); args.Handled = true;
        Assert.That(M14NpcBuffStatePacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        var parsed = M14NpcBuffStatePacketReader.Read(args, true).Packet!;
        Assert.That(parsed.Entries[0].Time, Is.Zero); Assert.That(args.Handled, Is.True);
        Assert.That(Evaluate(parsed, false).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false; Assert.That(Evaluate(parsed).Verdict, Is.EqualTo(Verdict.Unknown)); actor.IsLoggedIn = true;
        args.Index = args.Msg.readBuffer.Length;
        Assert.That(M14NpcBuffStatePacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var extension = new UnsupportedHost(); var container = new PluginContainer(extension); plugins.Add(container);
        try { Assert.That(Evaluate(parsed, null).Verdict, Is.EqualTo(Verdict.Unknown)); }
        finally { plugins.Remove(container); }
    }

    private sealed class UnsupportedHost() : TerrariaPlugin(null!) { public override void Initialize() { } }
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
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 14354);
    }
}
