using System.Reflection;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M16InventorySlotTests
{
    private const int Slot = 17;
    private Player oldPlayer = null!;
    private RemoteClient oldClient = null!;
    private int oldMode, oldLocal;
    private TShockConfig oldConfig = null!;
    private ServerSideConfig oldSsc = null!;
    private TSPlayer actor = null!;
    private readonly List<int> published = [];
    private int coreCalls, nativeCalls;
    private sealed class SlotPlayer(int slot) : TSPlayer(slot)
    {
        public readonly List<(PacketTypes Type, int Slot)> CorrectionAttempts = [];
        public override void SendData(PacketTypes msgType, string text = "", int number = 0, float number2 = 0,
            float number3 = 0, float number4 = 0, int number5 = 0)
        {
            CorrectionAttempts.Add((msgType, (int)number2));
            base.SendData(msgType, text, number, number2, number3, number4, number5);
        }
    }

    [SetUp]
    public void Setup()
    {
        oldPlayer = Main.player[Slot]; oldClient = Netplay.Clients[Slot]; oldMode = Main.netMode; oldLocal = Main.myPlayer;
        oldConfig = ServerTShock.Config; oldSsc = ServerTShock.ServerSideCharacterConfig;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        Main.netMode = 2; Main.myPlayer = 255;
        ServerTShock.Config = new TShockConfig(); ServerTShock.ServerSideCharacterConfig = new ServerSideConfig();
        actor = new SlotPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Account = new UserAccount { ID = 16017, Name = "m16-slot" }, Group = new Group("m16-slot-default"), PlayerData = new PlayerData(false) };
        actor.PlayerData.CopyCharacter(actor); coreCalls = nativeCalls = 0; published.Clear();
        HookEvents.Terraria.NetMessage.SendData += Sink;
    }
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { if (args.msgType == 5 && args.number == Slot) published.Add((int)args.number2); args.ContinueExecution = false; }
    [TearDown]
    public void Cleanup()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        Main.player[Slot] = oldPlayer; Netplay.Clients[Slot] = oldClient; Main.netMode = oldMode; Main.myPlayer = oldLocal;
        ServerTShock.Config = oldConfig; ServerTShock.ServerSideCharacterConfig = oldSsc;
    }
    private static byte[] Body(short slot, short type = ItemID.Wood, byte claimed = Slot)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(claimed); writer.Write(slot); writer.Write((short)(type == 0 ? 0 : 1)); writer.Write((byte)0); writer.Write(type); writer.Write((byte)0);
        return stream.ToArray();
    }
    private bool Dispatch(short slot, short type = ItemID.Wood)
    {
        byte[] body = Body(slot, type); var args = M2ContractsTests.Packet(PacketTypes.PlayerSlot, body, Slot);
        if (M16InventorySlotSafety.Read(args, true) is { RejectMalformed: true }) args.Handled = true;
        if (args.Handled) return false;
        coreCalls++;
        using var stream = new MemoryStream(body, writable: false);
        var method = typeof(GetDataHandlers).GetMethod("HandlePlayerSlot", BindingFlags.NonPublic | BindingFlags.Static)!;
        if ((bool)method.Invoke(null, [new GetDataHandlerArgs(actor, stream)])!) return false;
        var native = new MessageBuffer { whoAmI = Slot };
        native.readBuffer[0] = 5; body.CopyTo(native.readBuffer, 1); native.ResetReader();
        nativeCalls++;
        native.GetData(0, body.Length + 1, out int packet);
        Assert.That(packet, Is.EqualTo(5)); return true;
    }

    [Test]
    public void EveryAllocatedNativeInventorySlotAcceptsNormalWriteAndAirThroughActualCoreAndReceiver()
    {
        int accepted = 0;
        for (short id = 0; id < PlayerItemSlotID.Count; id++)
        {
            var reference = new PlayerItemSlotID.SlotReference(actor.TPlayer, id);
            if (id != PlayerItemSlotID.TrashItem && (!reference.TryGetArraySlot(out var array, out int index) || (uint)index >= (uint)array.Length)) continue;
            Assert.That(Dispatch(id), Is.True, $"actual native slot {id}");
            Assert.That(reference.Item.type, Is.EqualTo(ItemID.Wood));
            Assert.That(Dispatch(id, 0), Is.True, $"actual native air slot {id}");
            Assert.That(reference.Item.IsAir, Is.True); accepted++;
        }
        Assert.That(accepted, Is.EqualTo(350), "Target default arrays have350 actual slots despite990 reserved wire IDs.");
        Assert.That(coreCalls, Is.EqualTo(700)); Assert.That(nativeCalls, Is.EqualTo(700));
        Assert.That(actor.PlayerData.inventory.All(item => item.NetId == 0), Is.True, "Actual SSC clear commits stay consistent.");
    }

    [Test]
    public void LastNativeLoadoutSlotCompletesInitialInventoryAndSscIgnorePathStillRejectsNormally()
    {
        var previousLog = ServerTShock.Log;
        using var log = new TextLog(Path.Combine(Path.GetTempPath(), "m16-slot-" + Guid.NewGuid().ToString("N") + ".log"), false);
        ServerTShock.Log = log;
        try
        {
        actor.HasSentInventory = false;
        Assert.That(Dispatch((short)(PlayerItemSlotID.Count - 1), 0), Is.True);
        Assert.That(actor.HasSentInventory, Is.True);
        actor.IgnoreSSCPackets = true;
        Assert.That(Dispatch(10), Is.False);
        Assert.That(coreCalls, Is.EqualTo(2)); Assert.That(nativeCalls, Is.EqualTo(1));
        Assert.That(actor.TPlayer.inventory[10].IsAir, Is.True);
        Assert.That(((SlotPlayer)actor).CorrectionAttempts, Does.Contain((PacketTypes.PlayerSlot, 10)),
            "Actual core requests its normal SSC correction; this socketless fixture does not claim peer receipt.");
        }
        finally { ServerTShock.Log = previousLog; }
    }

    [TestCase(0)] [TestCase(ItemID.Wood)]
    public void ReservedBankAddressesAreBlockedBeforeCoreOrNativeWithoutExecutingTheUnsafeSetter(int type)
    {
        int[] starts = [PlayerItemSlotID.Bank1_0, PlayerItemSlotID.Bank2_0, PlayerItemSlotID.Bank3_0, PlayerItemSlotID.Bank4_0];
        foreach (int start in starts)
        {
            short outside = (short)(start + 40);
            var reference = new PlayerItemSlotID.SlotReference(actor.TPlayer, outside);
            Assert.That(reference.TryGetArraySlot(out var array, out int index), Is.True);
            Assert.That(index, Is.EqualTo(array.Length), "Read-only mapping proves the slot would be outside the real array; do not invoke its setter.");
            Assert.That(Dispatch(outside, (short)type), Is.False);
        }
        Assert.That(coreCalls, Is.Zero); Assert.That(nativeCalls, Is.Zero); Assert.That(published, Is.Empty);
        Assert.That(Dispatch(10), Is.True, "A normal same-account action after rejection succeeds.");
        Assert.That(actor.TPlayer.inventory[10].type, Is.EqualTo(ItemID.Wood));
    }

    [Test]
    public void ActualArrayLengthAllowsExpandedStorageAndMissingHostLayoutDoesNotBecomeCheatEvidence()
    {
        actor.TPlayer.bank4.item = Enumerable.Range(0, 200).Select(_ => new Item()).ToArray();
        short id = (short)(PlayerItemSlotID.Bank4_0 + 199);
        Assert.That(Dispatch(id), Is.True, "Actual native expanded array is supported; no hardcoded40-slot policy.");
        actor.TPlayer.bank4.item = null!;
        var unknown = M16InventorySlotSafety.Read(M2ContractsTests.Packet(PacketTypes.PlayerSlot, Body(id), Slot), true)!.Value;
        Assert.That(unknown.RejectMalformed, Is.False); Assert.That(unknown.Reason, Is.EqualTo("inventory-slot-player-layout-unavailable"));
        Assert.That(M16InventorySlotSafety.Read(M2ContractsTests.Packet(PacketTypes.PlayerSlot, Body(-1), Slot), true)!.Value.RejectMalformed, Is.True);
    }

    [Test]
    public void SenderMismatchIsLeftForExistingQualifiedIdentityRuleAndEnvelopeCancellationIsNeverRestored()
    {
        var spoofed = M2ContractsTests.Packet(PacketTypes.PlayerSlot, Body(short.MaxValue, claimed: Slot + 1), Slot);
        var deferred = M16InventorySlotSafety.Read(spoofed, true)!.Value;
        Assert.That(deferred.RejectMalformed, Is.False); Assert.That(deferred.Reason, Is.EqualTo("inventory-slot-sender-contract-deferred"));
        Assert.That(M16InventorySlotSafety.Read(spoofed, false), Is.Null);
        spoofed.Handled = true; M16InventorySlotSafety.Read(spoofed, true); Assert.That(spoofed.Handled, Is.True);
        spoofed.Length--; Assert.That(M16InventorySlotSafety.Read(spoofed, true)!.Value.RejectMalformed, Is.True);
        spoofed.MsgID = PacketTypes.Emoji; Assert.That(M16InventorySlotSafety.Read(spoofed, true), Is.Null);
    }

    [Test]
    public void SafetyKeepsTheExistingPacket5EntitySyncResourceCategoryForValidInvalidAndDeferredSenders()
    {
        foreach (var payload in new[] { Body(10), Body((short)(PlayerItemSlotID.Bank4_0 + 40)), Body(short.MaxValue, claimed: Slot + 1) })
        {
            var args = M2ContractsTests.Packet(PacketTypes.PlayerSlot, payload, Slot);
            var safety = M16InventorySlotSafety.Read(args, true)!.Value;
            Assert.That(safety.Kind, Is.EqualTo(M5WorldCost.Read(args, true).Kind));
            Assert.That(safety.Kind, Is.EqualTo(AntiCheat.Rules.NetworkRequestKind.EntitySync));
        }
    }
}
