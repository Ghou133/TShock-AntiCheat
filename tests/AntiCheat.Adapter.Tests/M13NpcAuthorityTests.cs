using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M13NpcAuthorityTests
{
    private const int Slot = 14;
    private NPC[] oldNpcs = null!;
    private Player oldPlayer = null!;
    private int oldMode, oldLocal;
    private bool oldDay, oldZenith, oldRemix, oldWorthy;
    private readonly List<(int Message, int Player, int Type)> sends = [];
    private TSPlayer actor = null!;
    private SessionKey session;

    [SetUp]
    public void Setup()
    {
        oldNpcs = Main.npc; oldPlayer = Main.player[Slot]; oldMode = Main.netMode; oldLocal = Main.myPlayer;
        oldDay = Main.dayTime; oldZenith = Main.zenithWorld; oldRemix = Main.remixWorld; oldWorthy = Main.getGoodWorld;
        Main.npc = Enumerable.Range(0, Main.maxNPCs + 1).Select(_ => new NPC()).ToArray();
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.netMode = 2; Main.myPlayer = 255; Main.dayTime = false;
        Main.zenithWorld = Main.remixWorld = Main.getGoodWorld = false;
        actor = new TSPlayer(Slot) { IsLoggedIn = true, ReceivedInfo = true, HasSentInventory = true,
            Account = new UserAccount { ID = 714, Name = "m13-prime" }, Group = new Group("normal") };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        sends.Clear(); HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void Cleanup()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture;
        Main.npc = oldNpcs; Main.player[Slot] = oldPlayer; Main.netMode = oldMode; Main.myPlayer = oldLocal;
        Main.dayTime = oldDay; Main.zenithWorld = oldZenith; Main.remixWorld = oldRemix; Main.getGoodWorld = oldWorthy;
    }

    private void Capture(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add((args.msgType, args.number, (int)args.number2)); args.ContinueExecution = false; }

    private BusinessRuleResult Evaluate(int type, bool? host = true) => M13NpcAuthorityPacketReader.Evaluate(
        new(Slot, type), session, actor, TargetRuntime.Fingerprint, host);

    [TestCase(557, 127)] [TestCase(556, 134)]
    public void ActualMechanicalItemProducerEmitsTheWholeBossNotItsParts(int itemId, int expectedType)
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        var item = new Item(); item.SetDefaults(itemId);
        var player = Main.player[Slot]; player.itemTime = 0; player.itemAnimation = 30;
        player.ItemCheck_UseBossSpawners(Slot, item);
        Assert.That(sends.Where(x => x.Message == 61), Is.EqualTo(new[] { (61, Slot, expectedType) }));
        Assert.That(player.itemTime, Is.GreaterThan(0), "The legal producer must execute, not pass through missing state.");
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(Evaluate(expectedType).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void ActualTwinsAndCombinedSeedMechdusaRequestsRemainLegal()
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        var item = new Item(); item.SetDefaults(544);
        Main.player[Slot].itemTime = 0; Main.player[Slot].itemAnimation = 30;
        Main.player[Slot].ItemCheck_UseBossSpawners(Slot, item);
        Assert.That(sends.Where(x => x.Message == 61).Select(x => x.Type), Is.EqualTo(new[] { 125, 126 }));
        sends.Clear(); Main.remixWorld = Main.getGoodWorld = true;
        Assert.That(NPC.SpawnMechQueen(Slot), Is.True);
        Assert.That(sends.Where(x => x.Message == 61), Is.EqualTo(new[] { (61, Slot, -16) }));
        Main.netMode = 2; Main.myPlayer = 255;
        foreach (int type in new[] { 125, 126, -16 }) Assert.That(Evaluate(type).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [TestCase(-12, 637)] [TestCase(-13, 638)] [TestCase(-14, 656)] [TestCase(-15, 670)]
    public void ActualDynamicPetLicenseRouteUsesItsNegativeOperation(int operation, int npcType)
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        bool bought = false;
        NPC.UnlockOrExchangePet(ref bought, npcType, "unused-client-capture", operation);
        Assert.That(sends.Where(x => x.Message == 61), Is.EqualTo(new[] { (61, Slot, operation) }));
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(Evaluate(operation).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [TestCase(128)] [TestCase(129)] [TestCase(130)] [TestCase(131)]
    public void FirstParsedOwnPartRequestProvesItsRoleRegardlessOfNativeWhitelist(int type)
    {
        byte[] body = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(body, Slot);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), (short)type);
        var args = M2ContractsTests.Packet((PacketTypes)61, body, Slot);
        var read = M13NpcAuthorityPacketReader.Read(args, true);
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        var result = M13NpcAuthorityPacketReader.Evaluate(read.Packet!, session, actor, TargetRuntime.Fingerprint, true);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        TestContext.Out.WriteLine($"NPCID.Sets.MPAllowedEnemies[{type}]={NPCID.Sets.MPAllowedEnemies[type]}; not a proof input");
        Assert.That(Main.npc.All(npc => !npc.active), Is.True, "Reader/evaluator cannot instantiate the attempted part.");
        args.Handled = true;
        Assert.That(M13NpcAuthorityPacketReader.Read(args, true).Packet, Is.EqualTo(read.Packet),
            "An earlier core cancellation does not destroy independent raw evidence.");
        Assert.That(args.Handled, Is.True, "Reader cannot restore core-rejected actions.");
    }

    [Test]
    public void UnknownHostAuthenticationSynchronizationAndMalformedFramesDoNotProduceProof()
    {
        Assert.That(Evaluate(128, false).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false; Assert.That(Evaluate(128).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = true; actor.IgnoreSSCPackets = true;
        Assert.That(Evaluate(128).Verdict, Is.EqualTo(Verdict.Unknown));
        foreach (int length in new[] { 0, 1, 2, 3, 5, 64 })
            Assert.That(M13NpcAuthorityPacketReader.ReadPayload(61, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M13NpcAuthorityPacketReader.ReadPayload(60, new byte[4]).Kind, Is.EqualTo(PacketReadKind.Unrelated));
        Assert.That(M13NpcAuthorityPacketReader.Read(M2ContractsTests.Packet((PacketTypes)61, new byte[4], Slot), false).Kind,
            Is.EqualTo(PacketReadKind.UnknownRuntime));
    }
}
