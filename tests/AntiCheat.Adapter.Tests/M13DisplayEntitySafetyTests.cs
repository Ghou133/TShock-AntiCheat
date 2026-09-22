using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.Graphics.Shaders;
using Terraria.ID;
using Terraria.Initializers;
using Terraria.UI;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

// Uses the already established isolated root hook/account/transport fixture; no new test platform.
public sealed partial class M6NpcStrikeTests
{
    // Headless fixtures do not run Main's graphics initialization, which registers dye IDs.
    // Run the original bounded initializer and restore all touched shared registry state.
    private sealed class M13OriginalDyes : IDisposable
    {
        private readonly List<ArmorShaderData> data = GameShaders.Armor._shaderData;
        private readonly Dictionary<int, int> lookup = GameShaders.Armor._shaderLookupDictionary;
        private readonly int count = GameShaders.Armor._shaderDataCount;

        public M13OriginalDyes()
        {
            GameShaders.Armor._shaderData = [];
            GameShaders.Armor._shaderLookupDictionary = [];
            GameShaders.Armor._shaderDataCount = 0;
            try { DyeInitializer.LoadBasicColorDyes(); }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            GameShaders.Armor._shaderData = data;
            GameShaders.Armor._shaderLookupDictionary = lookup;
            GameShaders.Armor._shaderDataCount = count;
        }
    }

    private static byte[] DollBody(byte command = 0, byte slot = 0, ushort type = (ushort)ItemID.WoodHelmet, int entityId = 321)
    {
        byte[] body = new byte[command == 2 ? 8 : 12]; body[0] = Slot;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), entityId); body[5] = slot; body[6] = command;
        if (command == 2) body[7] = 1;
        else { BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), type); BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), 1); }
        return body;
    }

    private GetDataEventArgs RootDisplay(int message, byte[] body, bool cancelled = false)
    {
        var args = M2ContractsTests.Packet((PacketTypes)message, body, Slot); args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args); return args;
    }

    private static void NativeDisplay(int message, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = (byte)message;
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out _);
    }

    private static bool TShockDoll(byte[] body)
    {
        var method = typeof(GetDataHandlers).GetMethod("HandleTileEntityDisplayDollItemSync", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var stream = new MemoryStream(body);
        return (bool)method.Invoke(null, [new GetDataHandlerArgs(ServerTShock.Players[Slot], stream)])!;
    }

    private static void WithDoll(Action<TEDisplayDoll> test)
    {
        bool existed = TileEntity.ByID.TryGetValue(321, out var old);
        var doll = new TEDisplayDoll { ID = 321 }; TileEntity.ByID[321] = doll;
        try { test(doll); }
        finally { if (existed) TileEntity.ByID[321] = old!; else TileEntity.ByID.Remove(321); }
    }

    [TestCase((byte)0, (byte)0, ItemID.WoodHelmet, 23)]
    [TestCase((byte)0, (byte)8, ItemID.SlimySaddle, 39)]
    [TestCase((byte)1, (byte)8, ItemID.RedDye, 25)]
    [TestCase((byte)3, (byte)0, ItemID.WoodenSword, 38)]
    public void M13Display_UiAdmissibleItemPassesAndNativeWriteActuallyRuns(byte command, byte slot, short type, int uiContext)
    {
        using var originalDyes = new M13OriginalDyes();
        WithDoll(doll =>
        {
            Assert.That(doll._equip.Length, Is.EqualTo(M13DisplayEntityPacketSafety.EquipmentSlots));
            Assert.That(doll._dyes.Length, Is.EqualTo(M13DisplayEntityPacketSafety.DyeSlots));
            Assert.That(doll._misc.Length, Is.EqualTo(M13DisplayEntityPacketSafety.MiscSlots));
            var candidate = new Item(); candidate.SetDefaults(type);
            var items = command switch { 1 => doll._dyes, 3 => doll._misc, _ => doll._equip };
            Assert.That(ItemSlot.ShouldHighlightSlotForMouseItem(uiContext, slot, candidate), Is.True,
                "Actual original UI predicate must accept this concrete item in this concrete slot.");
            Assert.That(ItemSlot.PickItemMovementAction(items, uiContext, slot, candidate), Is.EqualTo(command == 1 ? 2 : 1));
            var body = DollBody(command, slot, (ushort)type);
            Assert.That(M13DisplayEntityPacketSafety.ReadPayload(body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(121, body).Handled, Is.False);
            NativeDisplay(121, body);
            Assert.That(items[slot].type, Is.EqualTo(type)); Assert.That(items[slot].stack, Is.EqualTo(1));
            Assert.That(sent.Any(s => s.Id == 121), Is.True);
        });
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase((byte)0, (byte)9)] [TestCase((byte)1, (byte)9)] [TestCase((byte)3, (byte)1)]
    [TestCase((byte)0, (byte)255)]
    public void M13Display_RealTShockBoundsFailureIsBlockedAndSameSessionRecovers(byte command, byte slot)
    {
        WithDoll(doll =>
        {
            var bad = DollBody(command, slot);
            var exception = Assert.Throws<TargetInvocationException>(() => TShockDoll(bad));
            Assert.That(exception!.InnerException, Is.TypeOf<IndexOutOfRangeException>(),
                "The actual accepted TShock indexes before its existing item event.");
            Assert.That(RootDisplay(121, bad).Handled, Is.True);
            Assert.That(doll._equip.Concat(doll._dyes).Concat(doll._misc).All(item => item.IsAir), Is.True);
            Assert.That(sent, Is.Empty); Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            ushort expected = (ushort)(command switch { 1 => ItemID.RedDye, 3 => ItemID.WoodenSword, _ => ItemID.WoodHelmet });
            var good = DollBody(command, 0, expected);
            Assert.That(RootDisplay(121, good).Handled, Is.False); NativeDisplay(121, good);
            var items = command switch { 1 => doll._dyes, 3 => doll._misc, _ => doll._equip };
            Assert.That(items[0].type, Is.EqualTo(expected), "Normal follow-up must reach a real write after rejection.");
        });
    }

    [TestCase((byte)4)] [TestCase((byte)255)]
    public void M13Display_UndefinedCommandSkipsActualTShockCancellationThenWritesNativeEquipment(byte command)
    {
        WithDoll(doll =>
        {
            var prior = GetDataHandlers.DisplayDollItemSync;
            var handlers = new HandlerList<GetDataHandlers.DisplayDollItemSyncEventArgs>();
            int itemEvents = 0;
            void Deny(object? sender, GetDataHandlers.DisplayDollItemSyncEventArgs args) { itemEvents++; args.Handled = true; }
            handlers.Register(Deny); GetDataHandlers.DisplayDollItemSync = handlers;
            try
            {
                Assert.That(TShockDoll(DollBody()), Is.True); Assert.That(itemEvents, Is.EqualTo(1), "Existing item/region cancellation is live.");
                var alias = DollBody(command);
                Assert.That(TShockDoll(alias), Is.False); Assert.That(itemEvents, Is.EqualTo(1), "Undefined command bypassed the real event.");
                NativeDisplay(121, alias); Assert.That(doll._equip[0].type, Is.EqualTo(ItemID.WoodHelmet));
                doll._equip[0].TurnToAir(); sent.Clear();
                Assert.That(RootDisplay(121, alias).Handled, Is.True);
                Assert.That(doll._equip[0].IsAir, Is.True); Assert.That(sent, Is.Empty);
                Assert.That(engine.SanctionCount, Is.Zero); Assert.That(engine.CanWrite(session), Is.True);
            }
            finally { GetDataHandlers.DisplayDollItemSync = prior; }
        });
    }

    [Test]
    public void M13Display_PoseIgnoresSlotAndMissingEntityRemainsANormalNativeNoop()
    {
        WithDoll(doll =>
        {
            var pose = DollBody(2, 255);
            Assert.That(RootDisplay(121, pose).Handled, Is.False); NativeDisplay(121, pose);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            doll.WriteData(255, 2, writer);
            Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 1 }));
            var missing = DollBody(entityId: -1);
            Assert.That(RootDisplay(121, missing).Handled, Is.False); NativeDisplay(121, missing);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public void M13Display_ExactFramesUnknownRuntimeAndPreservedCoreCancellation()
    {
        foreach (int length in new[] { 0, 1, 6, 7, 8, 11, 13, 500 })
            Assert.That(RootDisplay(121, new byte[length]).Handled, Is.True);
        Assert.That(RootDisplay(121, DollBody(), true).Handled, Is.True);
        Assert.That(M13DisplayEntityPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)121, DollBody()), false), Is.Null);
        Assert.That(M13DisplayEntityPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)124, new byte[11]), true), Is.Null,
            "Existing safe hat-rack receiver is outside the new guard.");
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase((byte)0)] [TestCase((byte)1)] [TestCase((byte)2)] [TestCase((byte)3)]
    [TestCase((byte)4)] [TestCase((byte)255)]
    public void M13Display_HatRackExistingNativeSlotGuardAndAllFourLegalSlotsAreMaintained(byte wireSlot)
    {
        using var originalDyes = new M13OriginalDyes();
        var priorConfig = ServerTShock.Config; var priorRegions = ServerTShock.Regions;
        var actor = ServerTShock.Players[Slot]; var priorGroup = actor.Group;
        bool existed = TileEntity.ByID.TryGetValue(322, out var previous);
        var rack = new TEHatRack { ID = 322 }; TileEntity.ByID[322] = rack;
        try
        {
            // M14 now checks the existing build permission on every124 write. Supply the
            // ordinary allowed-world context omitted by the old native slot-only fixture.
            ServerTShock.Config = new TShockAPI.Configuration.TShockConfig();
            ServerTShock.Config.Settings.DisableBuild = false; ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Regions = (TShockAPI.DB.RegionManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TShockAPI.DB.RegionManager));
            ServerTShock.Regions.Regions = [];
            actor.Group = new Group("m13-hat-ordinary-build", permissions: Permissions.canbuild);
            byte[] body = new byte[11]; body[0] = Slot; body[5] = wireSlot;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), 322);
            ushort type = (ushort)(wireSlot < 2 || wireSlot >= 4 ? ItemID.WoodHelmet : ItemID.RedDye);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), type);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 1);
            Assert.That(M13DisplayEntityPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)124, body), true), Is.Null);
            Assert.That(RootDisplay(124, body).Handled, Is.False); NativeDisplay(124, body);
            if (wireSlot < 4)
            {
                var items = wireSlot < 2 ? rack._items : rack._dyes;
                var candidate = new Item(); candidate.SetDefaults(type);
                Assert.That(ItemSlot.ShouldHighlightSlotForMouseItem(wireSlot < 2 ? 26 : 27, wireSlot % 2, candidate), Is.True);
                Assert.That(items[wireSlot % 2].type, Is.EqualTo(type));
                Assert.That(sent.Any(s => s.Id == 124), Is.True);
            }
            else
            {
                Assert.That(rack._items.Concat(rack._dyes).All(item => item.IsAir), Is.True);
                Assert.That(sent, Is.Empty, "The existing receiver already discards this slot; no new guard is claimed.");
            }
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        }
        finally
        {
            if (existed) TileEntity.ByID[322] = previous!; else TileEntity.ByID.Remove(322);
            actor.Group = priorGroup; ServerTShock.Config = priorConfig; ServerTShock.Regions = priorRegions;
        }
    }

    [TestCase(ItemID.WoodHelmet, 0, 0)] [TestCase(ItemID.SlimySaddle, 8, 0)] [TestCase(ItemID.WoodenSword, 0, 3)]
    public void M13Display_ActualOriginalQuickPlacementProducesTheAuditedCommand(short type, int expectedSlot, int expectedCommand)
    {
        WithDoll(doll =>
        {
            int priorMode = Main.netMode, priorPlayer = Main.myPlayer;
            try
            {
                Main.netMode = 1; Main.myPlayer = Slot;
                var candidate = new Item(); candidate.SetDefaults(type); Item[] inventory = [candidate];
                Assert.That(TEDisplayDoll.CanQuickSwapIntoDisplayDoll(candidate), Is.True);
                Assert.That(doll.TryFitting(inventory, 0), Is.True);
                var actual = sent.Single(frame => frame.Id == 121);
                Assert.That(actual.Target, Is.EqualTo(Slot)); Assert.That(actual.Damage, Is.EqualTo(doll.ID));
                Assert.That(actual.Knockback, Is.EqualTo(expectedSlot)); Assert.That(actual.Direction, Is.EqualTo(expectedCommand));
                Assert.That(inventory[0].IsAir, Is.True, "Native placement transfers the actual input item.");
                var items = expectedCommand == 3 ? doll._misc : doll._equip;
                Assert.That(items[expectedSlot].type, Is.EqualTo(type));
                Assert.That(M13DisplayEntityPacketSafety.ReadPayload(DollBody((byte)expectedCommand, (byte)expectedSlot, (ushort)type)).RejectMalformed, Is.False);
            }
            finally { Main.netMode = priorMode; Main.myPlayer = priorPlayer; }
        });
    }
}
