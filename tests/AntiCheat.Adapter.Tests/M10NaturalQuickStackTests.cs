using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Net;
using Terraria.UI;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M10NaturalQuickStackTests
{
    private const int ActorSlot = 12;
    private Player[] oldPlayers = null!;
    private Chest[] oldChests = null!;
    private Dictionary<Point, Chest> oldIndex = null!;
    private readonly List<(Point Point, ITile Tile)> oldTiles = [];
    private readonly List<(int Type, int Number, float Number2)> sends = [];
    private int oldMode, oldLocal, oldWidth, oldHeight;
    private int[]? oldCategoryIndices;
    private int oldCategoryCount;
    private QuickStacking.DestinationHelper[] oldDestinationPool = null!;
    private int particleBroadcasts;
    private Player Actor => Main.player[ActorSlot];

    [SetUp]
    public void SetUp()
    {
        oldPlayers = Main.player; oldChests = Main.chest; oldIndex = Chest._chestsByCoords;
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY;
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 500; Main.maxTilesY = 500;
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i, chest = -1 }).ToArray();
        Actor.active = true; Actor.position = new(320, 320);
        Main.chest = new Chest[8000]; Chest._chestsByCoords = [];
        // The transfer tests contain only Wood. This explicit one-category fixture supplies the
        // native sorting lookup's content-initialization prerequisite, without replacing transfer,
        // reference assignment, permissions, or serialization. No general Smart Stack category
        // behavior is claimed by these same-type tests.
        oldCategoryIndices = ItemSorting._layerIndexForItemType; oldCategoryCount = ItemSorting._layerCount;
        ItemSorting._layerIndexForItemType = new int[ItemID.Count]; ItemSorting._layerCount = 1;
        oldDestinationPool = QuickStacking.destHelperPool; QuickStacking.destHelperPool = new QuickStacking.DestinationHelper[100];
        oldTiles.Clear(); sends.Clear(); particleBroadcasts = 0;
        HookEvents.Terraria.NetMessage.SendData += CaptureSend;
        HookEvents.Terraria.Net.NetManager.Broadcast_NetPacket_Int32 += CaptureParticleBroadcast;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= CaptureSend;
        HookEvents.Terraria.Net.NetManager.Broadcast_NetPacket_Int32 -= CaptureParticleBroadcast;
        foreach (var (point, tile) in oldTiles) Main.tile[point.X, point.Y] = tile;
        Main.player = oldPlayers; Main.chest = oldChests; Chest._chestsByCoords = oldIndex;
        ItemSorting._layerIndexForItemType = oldCategoryIndices!; ItemSorting._layerCount = oldCategoryCount;
        QuickStacking.destHelperPool = oldDestinationPool;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DistinctSameTypeSlotsAreAllowedAndNativeTransferConservesQuantity(bool smart)
    {
        var chest = ChestAt(0); Put(10, 7); Put(11, 8);
        var body = Body([10, 11], smart);
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(body, PlayerItemSlotID.Count).RejectMalformed, Is.False);
        NativeRequest(body);
        Assert.That(TotalWood(), Is.EqualTo(10014));
        Assert.That(Actor.inventory[10].IsAir && Actor.inventory[11].IsAir, Is.True);
        Assert.That(chest.item.Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(10014));
        Assert.That(sends.Any(s => s.Type == 32) && sends.Count(s => s.Type == 5) == 2, Is.True);
        Assert.That(particleBroadcasts, Is.GreaterThan(0), "The native transfer also emitted its visual NetModule to the isolated sink.");
    }

    [Test]
    public void ActualNativePackingAndWriterPreserveUniqueInventoryAndVoidBagSources()
    {
        Put(10, 7); Put(11, 8); Put(PlayerItemSlotID.Bank4_0, 9);
        Actor.inventory[0].SetDefaults(ItemID.VoidLens);
        var packed = QuickStacking.PackQuickStackableItems(Actor, includeVoidBag: true);
        Assert.That(packed.numItems, Is.EqualTo(3));
        var previous = QuickStacking.netInv;
        try
        {
            QuickStacking.netInv = packed;
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            QuickStacking.WriteNetInventorySlots(writer); writer.Write(false);
            var result = M10NaturalQuickStackSafety.ReadPayload(stream.ToArray(), PlayerItemSlotID.Count);
            Assert.That(result.RejectMalformed, Is.False);
            Assert.That(result.WorkUnits, Is.EqualTo(3));
            Assert.That(M10NaturalQuickStackSafety.Read(M2ContractsTests.Packet((PacketTypes)85, stream.ToArray(), ActorSlot), true)!.Value.RejectMalformed, Is.False);
            var chest = ChestAt(0); NativeRequest(stream.ToArray());
            Assert.That(TotalWood(), Is.EqualTo(10023));
            Assert.That(Actor.bank4.item[0].IsAir, Is.True);
            Assert.That(chest.item.Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(10023));
        }
        finally { QuickStacking.netInv = previous; }
    }

    [Test]
    public void UnguardedActualNativeRequestRepeatsReferenceIntoTwoChestSlots()
    {
        var chest = ChestAt(0); Put(10, 7);
        NativeRequest(Body([10, 10]));
        Assert.That(chest.item[1].type, Is.EqualTo(ItemID.Wood));
        Assert.That(chest.item[2].type, Is.EqualTo(ItemID.Wood));
        Assert.That(ReferenceEquals(chest.item[1], chest.item[2]), Is.True,
            "The target Swap leaves the second supplied source reference alive.");
        Assert.That(TotalWood(), Is.EqualTo(10013), "One input stack of seven appears in two independently serialized slots.");
        Assert.That(sends.Count(s => s.Type == 32), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RepeatedSourceIsRejectedBeforeAllNativeSideEffectsAndNextNormalRequestWorks(bool smart)
    {
        var chest = ChestAt(0); Put(10, 7);
        var body = Body([10, 10], smart);
        var result = M10NaturalQuickStackSafety.ReadPayload(body, PlayerItemSlotID.Count);
        Assert.That(result.RejectMalformed, Is.True); Assert.That(result.Reason, Is.EqualTo("quick-stack-repeated-source-slot"));
        if (!result.RejectMalformed) NativeRequest(body);
        Assert.That(TotalWood(), Is.EqualTo(10006));
        Assert.That(Actor.inventory[10].stack, Is.EqualTo(7)); Assert.That(chest.item[1].IsAir, Is.True);
        Assert.That(sends, Is.Empty);
        Assert.That(particleBroadcasts, Is.Zero);
        var next = Body([10]); Assert.That(M10NaturalQuickStackSafety.ReadPayload(next, PlayerItemSlotID.Count).RejectMalformed, Is.False);
        NativeRequest(next);
        Assert.That(TotalWood(), Is.EqualTo(10006)); Assert.That(Actor.inventory[10].IsAir, Is.True);
        Assert.That(chest.item[1].stack, Is.EqualTo(7));
    }

    [Test]
    public void EmptySourceAndDistinctFlatDomainEntriesPassTheFirstStructuralStage()
    {
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body([]), PlayerItemSlotID.Count).RejectMalformed, Is.False);
        // The flat-domain parser is followed by actual per-player array capacity checks in Read.
        // It does not impose a new gameplay whitelist on mapped slots.
        var slots = Enumerable.Range(0, M10NaturalQuickStackSafety.MaximumSources).Select(i => (short)i).ToArray();
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body(slots), PlayerItemSlotID.Count).RejectMalformed, Is.False);
    }

    [Test]
    public void BadCountTruncationTrailingBytesInvalidSlotsAndLastDuplicateRejectBeforeRead()
    {
        var valid = Body([10, 11]);
        for (int n = 0; n < valid.Length; n++)
            Assert.That(M10NaturalQuickStackSafety.ReadPayload(valid.AsSpan(0, n), PlayerItemSlotID.Count).RejectMalformed, Is.True, "truncated " + n);
        Assert.That(M10NaturalQuickStackSafety.ReadPayload([.. valid, 0], PlayerItemSlotID.Count).RejectMalformed, Is.True);
        foreach (int count in new[] { -1, 401, int.MaxValue })
        {
            var malformed = (byte[])valid.Clone(); BitConverter.GetBytes(count).CopyTo(malformed, 0);
            Assert.That(M10NaturalQuickStackSafety.ReadPayload(malformed, PlayerItemSlotID.Count).RejectMalformed, Is.True);
        }
        foreach (short slot in new[] { (short)-1, (short)PlayerItemSlotID.Count, short.MaxValue })
            Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body([slot]), PlayerItemSlotID.Count).RejectMalformed, Is.True);
        var full = Enumerable.Range(0, 400).Select(i => (short)i).ToArray(); full[^1] = full[0];
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body(full), PlayerItemSlotID.Count).Reason, Is.EqualTo("quick-stack-repeated-source-slot"));
    }

    [Test]
    public void UnknownHostLayoutDoesNotInventSlotRejectionOrEraseRepeatedIndexSafety()
    {
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body([10, 11]), 0).RejectMalformed, Is.False);
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body([10, 10]), 0).Reason, Is.EqualTo("quick-stack-repeated-source-slot"));
    }

    [Test]
    public void ActualRawEnvelopeRequiresVerified85AndNeverRestoresPriorCancellation()
    {
        var args = M2ContractsTests.Packet((PacketTypes)85, Body([10, 10]), ActorSlot);
        args.Handled = true;
        Assert.That(M10NaturalQuickStackSafety.Read(args, false), Is.Null);
        long before = M10NaturalQuickStackSafety.DuplicateRejected;
        Assert.That(M10NaturalQuickStackSafety.Read(args, true)!.Value.RejectMalformed, Is.True);
        Assert.That(args.Handled, Is.True);
        Assert.That(M10NaturalQuickStackSafety.DuplicateRejected, Is.EqualTo(before + 1));
        args.MsgID = (PacketTypes)84;
        Assert.That(M10NaturalQuickStackSafety.Read(args, true), Is.Null);
        args.MsgID = (PacketTypes)85; args.Index = -1;
        Assert.That(M10NaturalQuickStackSafety.Read(args, true)!.Value.Reason, Is.EqualTo("quick-stack-frame-bounds-invalid"));
    }

    [Test]
    public void ActualSenderBankArrayBoundaryIsCheckedBeyondTheReservedNetworkIdDomain()
    {
        int capacity = Actor.bank4.item.Length;
        Assert.That(capacity, Is.LessThan(200), "Fixture uses the native default bank capacity.");
        short valid = checked((short)(PlayerItemSlotID.Bank4_0 + capacity - 1));
        short outside = checked((short)(valid + 1));
        Assert.That(outside, Is.LessThan(PlayerItemSlotID.Loadout1_Armor_0));
        Assert.That(M10NaturalQuickStackSafety.ReadPayload(Body([outside]), PlayerItemSlotID.Count).RejectMalformed, Is.False,
            "Reserved network space alone does not prove an actual array element exists.");
        Assert.That(M10NaturalQuickStackSafety.Read(M2ContractsTests.Packet((PacketTypes)85, Body([valid]), ActorSlot), true)!.Value.RejectMalformed, Is.False);
        Assert.That(M10NaturalQuickStackSafety.Read(M2ContractsTests.Packet((PacketTypes)85, Body([outside]), ActorSlot), true)!.Value.Reason,
            Is.EqualTo("quick-stack-source-outside-mapped-array"));
    }

    [Test]
    public void MissingSenderStorageRemainsUnknownWhileCompleteDuplicateCheckStillRejects()
    {
        Main.player[ActorSlot] = null!;
        var unknown = M10NaturalQuickStackSafety.Read(M2ContractsTests.Packet((PacketTypes)85, Body([10]), ActorSlot), true)!.Value;
        Assert.That(unknown.RejectMalformed, Is.False);
        Assert.That(unknown.Reason, Is.EqualTo("quick-stack-sender-storage-unknown"));
        Assert.That(M10NaturalQuickStackSafety.Read(M2ContractsTests.Packet((PacketTypes)85, Body([10, 10]), ActorSlot), true)!.Value.Reason,
            Is.EqualTo("quick-stack-repeated-source-slot"));
    }

    private static byte[] Body(short[] slots, bool smart = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(slots.Length); foreach (short slot in slots) writer.Write(slot); writer.Write(smart); return stream.ToArray();
    }
    private static void NativeRequest(byte[] body)
    {
        using var stream = new MemoryStream(body); using var reader = new BinaryReader(stream);
        var source = QuickStacking.ReadNetInventory(Main.player[ActorSlot], reader);
        bool smart = reader.ReadBoolean();
        QuickStacking.QuickStackToNearbyChests(Main.player[ActorSlot], source, smart);
    }
    private void Put(int slot, int stack)
    {
        var item = new Item(); item.SetDefaults(ItemID.Wood); item.stack = stack;
        var target = new PlayerItemSlotID.SlotReference(Actor, slot); target.Item = item;
    }
    private Chest ChestAt(int id)
    {
        oldTiles.Add((new(20, 20), Main.tile[20, 20]));
        var tile = new Tile { type = TileID.Containers, frameX = 0, frameY = 0 }; tile.active(true); Main.tile[20, 20] = tile;
        var chest = Chest.CreateWorldChest(id, 20, 20);
        chest.item[0].SetDefaults(ItemID.Wood); chest.item[0].stack = chest.item[0].maxStack;
        Assert.That(chest.item[0].stack, Is.EqualTo(9999)); return chest;
    }
    private int TotalWood() => Main.chest.Where(c => c is not null).Sum(c => c.item.Where(i => i.type == ItemID.Wood).Sum(i => i.stack)) +
        Actor.inventory.Concat(Actor.bank4.item).Where(i => i.type == ItemID.Wood).Sum(i => i.stack);
    private void CaptureSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        sends.Add((args.msgType, args.number, args.number2)); args.ContinueExecution = false;
    }
    private void CaptureParticleBroadcast(NetManager _, HookEvents.Terraria.Net.NetManager.Broadcast_NetPacket_Int32EventArgs args)
    {
        // Native chest-transfer visualization uses a NetModule rather than SendData32/5.
        // Consume only its outbound packet at the transport boundary; no socket is opened and
        // no transfer/quantity/slot code is replaced. Recycle exactly as native Broadcast would.
        particleBroadcasts++; args.packet.Recycle(); args.ContinueExecution = false;
    }
}
