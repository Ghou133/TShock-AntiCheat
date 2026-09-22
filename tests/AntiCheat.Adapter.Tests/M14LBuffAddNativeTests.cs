using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.Utilities;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M14LBuffAddNativeTests
{
    private const int ActorSlot = 21, TargetSlot = 22, NpcSlot = 3;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private Player[] oldPlayers = null!;
    private NPC[] oldNpcs = null!;
    private bool[] oldPvp = null!;
    private UnifiedRandom oldRandom = null!;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private FixtureSocket socket = null!;
    private bool serialize;
    private readonly List<(int Id, int Target, int Type, int Time)> calls = [];
    private readonly SessionKey session = new(Guid.NewGuid(), 1, ActorSlot, 1);
    private TSPlayer actor = null!;

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ;
        oldPlayers = Main.player; oldNpcs = Main.npc; oldPvp = Main.pvpBuff; oldRandom = Main.rand;
        oldClient = Netplay.Clients[ActorSlot]; oldBuffer = NetMessage.buffer[ActorSlot];
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i, active = true }).ToArray();
        Main.npc = Enumerable.Range(0, 200).Select(i => new NPC { whoAmI = i, active = true }).ToArray();
        Main.pvpBuff = Enumerable.Range(0, 401).Select(M14LBuffAddRules.IsNativePvpBuff).ToArray();
        Main.rand = new UnifiedRandom(Enumerable.Range(0, 100).First(seed => new UnifiedRandom(seed).Next(5, 11) == 10));
        Main.dedServ = true; Main.netMode = 1; Main.myPlayer = ActorSlot;
        socket = new FixtureSocket(); Netplay.Clients[ActorSlot] = new RemoteClient { Id = ActorSlot, State = 10, Socket = socket };
        NetMessage.buffer[ActorSlot] = new MessageBuffer();
        actor = new TSPlayer(ActorSlot) { IsLoggedIn = true, Group = new Group("m14l-a-native"),
            Account = new UserAccount { ID = 1421, Name = "m14l-a-native" } };
        calls.Clear(); serialize = false; HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Main.player = oldPlayers; Main.npc = oldNpcs; Main.pvpBuff = oldPvp; Main.rand = oldRandom;
        Netplay.Clients[ActorSlot] = oldClient; NetMessage.buffer[ActorSlot] = oldBuffer;
    }

    private void Capture(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        calls.Add((args.msgType, args.number, (int)args.number2, (int)args.number3));
        if (!serialize) args.ContinueExecution = false;
    }

    private byte[] Serialize(int message, int target, int type, int time)
    {
        Main.netMode = 2; Main.myPlayer = 255; socket.Sent.Clear(); serialize = true;
        try { NetMessage.SendData(message, ActorSlot, number: target, number2: type, number3: time); }
        finally { serialize = false; }
        byte[] frame = socket.Sent.Single();
        Assert.That(frame[2], Is.EqualTo(message));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(frame.Length));
        return frame[3..];
    }

    [Test]
    public void QtrNpcArgumentsPassThroughNativeAddBuffAndReallySerializeToSigned19392()
    {
        var npc = Main.npc[NpcSlot];
        npc.AddBuff(153, 216000);
        Assert.That(calls, Is.EqualTo(new[] { (53, NpcSlot, 153, 216000) }));
        Assert.That(npc.buffTime[npc.FindBuffIndex(153)], Is.EqualTo(216000));
        var body = Serialize(53, NpcSlot, 153, 216000);
        var packet = M13NpcBuffPacketReader.ReadPayload(53, body).Packet!;
        Assert.That(body.Length, Is.EqualTo(6)); Assert.That(packet.Time, Is.EqualTo(19392));
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.ProvenCheat));
        Main.netMode = 1; Main.myPlayer = ActorSlot; calls.Clear(); npc.buffImmune[153] = true;
        npc.AddBuff(153, 216000); Assert.That(calls, Is.Empty, "Immune target returns before client53 send.");
        TestContext.Out.WriteLine("Native method-level QTR argument replay: local216000, SendData arg216000, exact53 wire19392; not QTR GUI execution.");
    }

    [Test]
    public void NativeShimmerRequestSerializes100AndRepeatedAddsDoNotAmplifyRequest()
    {
        var npc = Main.npc[NpcSlot];
        npc.AddBuff(353, 100); npc.AddBuff(353, 100);
        Assert.That(calls, Is.EqualTo(new[] { (53, NpcSlot, 353, 100), (53, NpcSlot, 353, 100) }));
        Assert.That(npc.buffTime[npc.FindBuffIndex(353)], Is.EqualTo(100));
        var packet = M13NpcBuffPacketReader.ReadPayload(53, Serialize(53, NpcSlot, 353, 100)).Packet!;
        Assert.That(packet.Time, Is.EqualTo(100));
        Assert.That(M14RNpcShimmerAdapter.Evaluate(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Pass));
        calls.Clear(); Main.netMode = 1; Main.myPlayer = ActorSlot;
        npc.AddBuff(353, 100, quiet: true);
        Assert.That(calls, Is.Empty, "The raw53 receiver uses quiet:true, so passive receipt cannot echo a request.");
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(M14RNpcShimmerAdapter.Evaluate(packet with { Time = 101 }, session, actor, TargetRuntime.Fingerprint, false).Verdict,
            Is.EqualTo(Verdict.Unknown), "An unsupported host is not an enforcement-compatible legal control.");
    }

    [Test]
    public void QtrPlayerArgumentsSendOnlyForRemoteTargetsAndPreserveInt32Time()
    {
        Main.player[ActorSlot].AddBuff(44, 216000);
        Assert.That(calls, Is.Empty); Assert.That(Main.player[ActorSlot].FindBuffIndex(44), Is.GreaterThanOrEqualTo(0));
        Main.player[TargetSlot].AddBuff(44, 216000);
        Assert.That(calls, Is.EqualTo(new[] { (55, TargetSlot, 44, 216000) }));
        Assert.That(Main.player[TargetSlot].FindBuffIndex(44), Is.EqualTo(-1));
        var body = Serialize(55, TargetSlot, 44, 216000);
        var packet = M14LBuffAddPacketReader.ReadPayload(body).Packet!;
        Assert.That(body.Length, Is.EqualTo(7)); Assert.That(packet.Time, Is.EqualTo(216000));
        Assert.That(M14LBuffAddPacketReader.Evaluate(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Pass), "This rule does not pretend to detect QTR Burning44 by its large duration.");
        Main.netMode = 1; Main.myPlayer = ActorSlot; calls.Clear();
        Main.player[TargetSlot].buffImmune[44] = true; Main.player[TargetSlot].AddBuff(44, 216000);
        Main.player[TargetSlot].AddBuff(1, 600); Assert.That(calls, Is.Empty);
    }

    [Test]
    public void ActualProjectileMaximum600IsLegalAndRepeatedRequestsDoNotAccumulateProof()
    {
        var projectile = new Projectile { owner = ActorSlot, type = 585 };
        projectile.StatusNPC(NpcSlot);
        Assert.That(calls, Does.Contain((53, NpcSlot, 153, 600)));
        for (int i = 0; i < 3; i++) Main.npc[NpcSlot].AddBuff(153, 600);
        Assert.That(calls.Where(x => x.Type == 153).All(x => x.Time == 600), Is.True);
        Assert.That(Main.npc[NpcSlot].buffTime[Main.npc[NpcSlot].FindBuffIndex(153)], Is.EqualTo(600));
        var packet = M13NpcBuffPacketReader.ReadPayload(53, Serialize(53, NpcSlot, 153, 600)).Packet!;
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Pass));
        var other = M13NpcBuffPacketReader.ReadPayload(53, Serialize(53, NpcSlot, 24, 216000)).Packet!;
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(other, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Pass), "OnFire24 has a native TorchSlime216000 path and is outside the153 bound.");
    }

    [TestCase(114, 599)] [TestCase(585, 600)] [TestCase(495, 299)]
    [TestCase(497, 179)] [TestCase(496, 479)] [TestCase(46, 299)]
    public void AllSixNativeShadowFlameProducerBranchesStayWithinTheirSourceBounds(int projectileType, int maximum)
    {
        new Projectile { owner = ActorSlot, type = projectileType }.StatusNPC(NpcSlot);
        var call = calls.Single(x => x.Id == 53 && x.Type == 153);
        Assert.That(call.Time, Is.InRange(1, maximum));
        var packet = M13NpcBuffPacketReader.ReadPayload(53, Serialize(53, NpcSlot, 153, call.Time)).Packet!;
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void ExistingExplicitNpcBuffPermissionIsARestrictedAuthorization()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var packet = new M13NpcBuffObservation(M13NpcBuffOperation.Add, NpcSlot, 153, 19392);
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        actor.Group.AddPermission(Permissions.ignorenpcbuffdetection);
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(M14LBuffAddPacketReader.Evaluate(new(ActorSlot, 24, 180), session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ActualItem3054IsAnAmmoAndManaFreeNativeSourceOfAllowed497ShadowFlame()
    {
        var item = new Item(); item.SetDefaults(3054);
        Assert.That(item.shoot, Is.EqualTo(497)); Assert.That(item.useAmmo, Is.Zero); Assert.That(item.mana, Is.Zero);
        new Projectile { owner = ActorSlot, type = item.shoot }.StatusNPC(NpcSlot);
        var call = calls.Single(x => x.Id == 53 && x.Type == 153);
        Assert.That(call.Time, Is.InRange(60, 179));
        var packet = M13NpcBuffPacketReader.ReadPayload(53, Serialize(53, NpcSlot, 153, call.Time)).Packet!;
        Assert.That(M14LBuffAddPacketReader.EvaluateNpc(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Pass));
        TestContext.Out.WriteLine("Native Item.SetDefaults3054 supplies projectile497 with useAmmo0/mana0; its native StatusNPC emits53/153/60..179. UI aiming and hit execution are separate GUI evidence.");
    }

    [Test]
    public void ActualRemotePvpAdditionAndPassiveReceiptPassWithoutEchoOrTargetGuilt()
    {
        Main.player[TargetSlot].AddBuff(24, 180);
        Assert.That(calls, Is.EqualTo(new[] { (55, TargetSlot, 24, 180) }));
        var packet = M14LBuffAddPacketReader.ReadPayload(Serialize(55, TargetSlot, 24, 180)).Packet!;
        Assert.That(M14LBuffAddPacketReader.Evaluate(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Pass));
        var body = Serialize(55, ActorSlot, 24, 180);
        Main.netMode = 1; Main.myPlayer = ActorSlot; calls.Clear();
        var buffer = new MessageBuffer { whoAmI = 256 }; buffer.readBuffer[0] = 55; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out _);
        Assert.That(Main.player[ActorSlot].FindBuffIndex(24), Is.GreaterThanOrEqualTo(0)); Assert.That(calls, Is.Empty);
    }

    [Test]
    public void ExactReaderHostAndTableIntegrityCannotUpgradeMalformedOrUnknown()
    {
        foreach (int length in new[] { 0, 1, 6, 8, 100 }) Assert.That(M14LBuffAddPacketReader.ReadPayload(new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)55, new byte[7], ActorSlot); args.Handled = true;
        Assert.That(M14LBuffAddPacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        Assert.That(M14LBuffAddPacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Parsed)); Assert.That(args.Handled, Is.True);
        Main.netMode = 2; Main.myPlayer = 255;
        var self = new M14LPlayerBuffAddObservation(ActorSlot, 24, 180);
        Assert.That(M14LBuffAddPacketReader.Evaluate(self, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(M14LBuffAddPacketReader.Evaluate(self, session, actor, TargetRuntime.Fingerprint, false).Verdict, Is.EqualTo(Verdict.Unknown));
        Main.pvpBuff[1] = true; Assert.That(M14LBuffAddPacketReader.NativePvpTableIntact(), Is.False);
        Assert.That(M14LBuffAddPacketReader.Evaluate(self, session, actor, TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    private sealed class FixtureSocket : ISocket
    {
        public List<byte[]> Sent { get; } = [];
        public void Close() { } public bool IsConnected() => true; public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) => Sent.Add(data.AsSpan(offset, size).ToArray());
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false; public bool StartListening(SocketConnectionAccepted callback) => false; public void StopListening() { }
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 17851);
    }
}
