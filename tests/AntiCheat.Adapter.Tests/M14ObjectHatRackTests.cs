using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.UI;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using TShockAPI.Handlers;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

// Reuses the serial real root-hook/account/native transport fixture from M6/M13.
public sealed partial class M6NpcStrikeTests
{
    private const int M14RackId = 14322, M14RackX = 20, M14RackY = 20;

    private static byte[] M14RackBody(byte wireSlot = 0, ushort type = (ushort)ItemID.WoodHelmet,
        ushort stack = 1, byte sender = Slot, int id = M14RackId)
    {
        byte[] body = new byte[11]; body[0] = sender;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), id); body[5] = wireSlot;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), type);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), stack);
        return body;
    }

    private M5WorldWorkCost M14Admission(byte[] body) =>
        M14ObjectPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)124, body, Slot), true)!.Value;

    private void M14WithRack(bool allowed, Action<TEHatRack, Region> action)
    {
        var priorConfig = ServerTShock.Config; var priorRegions = ServerTShock.Regions;
        var actor = ServerTShock.Players[Slot]; var priorGroup = actor.Group;
        bool hadId = TileEntity.ByID.TryGetValue(M14RackId, out var old);
        var priorTile = Main.tile[M14RackX, M14RackY];
        try
        {
            ServerTShock.Config = new TShockConfig();
            ServerTShock.Config.Settings.DisableBuild = false;
            ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.RequireLogin = false;
            ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            actor.Group = new Group("m14-object-ordinary", permissions: Permissions.canbuild);
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            var region = new Region(14, new Rectangle(M14RackX, M14RackY, 3, 4), "m14-owned-hat-rack", "other-owner", true, Main.worldID.ToString(), 0);
            if (allowed) region.AllowedIDs.Add(actor.Account.ID);
            ServerTShock.Regions.Regions = [region];
            var rack = new TEHatRack { ID = M14RackId, Position = new Point16(M14RackX, M14RackY) };
            TileEntity.ByID[M14RackId] = rack;
            var tile = new Tile(); tile.active(true); tile.type = TileID.HatRack;
            Main.tile[M14RackX, M14RackY] = tile;
            Assert.That(actor.HasPermission("anticheat.bypass"), Is.False);
            Assert.That(actor.HasPermission(Permissions.editregion), Is.False);
            Assert.That(actor.HasBuildPermissionForTileObject(M14RackX, M14RackY, 3, 4, false), Is.EqualTo(allowed));
            action(rack, region);
        }
        finally
        {
            Main.tile[M14RackX, M14RackY] = priorTile;
            if (hadId) TileEntity.ByID[M14RackId] = old!; else TileEntity.ByID.Remove(M14RackId);
            actor.Group = priorGroup; ServerTShock.Config = priorConfig; ServerTShock.Regions = priorRegions;
        }
    }

    [TestCase((byte)0)] [TestCase((byte)1)] [TestCase((byte)2)] [TestCase((byte)3)]
    public void M14Object_AllLegalHatAndDyeWritesReachNativeAndTakingItemsRemainsAllowed(byte wireSlot)
    {
        using var dyes = new M13OriginalDyes();
        M14WithRack(true, (rack, _) =>
        {
            ushort type = (ushort)(wireSlot < 2 ? ItemID.WoodHelmet : ItemID.RedDye);
            var item = new Item(); item.SetDefaults(type);
            Assert.That(ItemSlot.ShouldHighlightSlotForMouseItem(wireSlot < 2 ? 26 : 27, wireSlot % 2, item), Is.True);
            var body = M14RackBody(wireSlot, type);
            Assert.That(M14Admission(body).Reason, Is.EqualTo("hat-rack-current-build-permission-allowed"));
            Assert.That(RootDisplay(124, body).Handled, Is.False);
            NativeDisplay(124, body);
            var array = wireSlot < 2 ? rack._items : rack._dyes;
            Assert.That(array[wireSlot % 2].type, Is.EqualTo(type));
            Assert.That(array[wireSlot % 2].stack, Is.EqualTo(1));
            Assert.That(sent.Count(frame => frame.Id == 124), Is.EqualTo(1));
            var clear = M14RackBody(wireSlot, 0, 0);
            Assert.That(RootDisplay(124, clear).Handled, Is.False); NativeDisplay(124, clear);
            Assert.That(array[wireSlot % 2].IsAir, Is.True);
            Assert.That(sent.Count(frame => frame.Id == 124), Is.EqualTo(2));
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [TestCase((byte)0)] [TestCase((byte)2)]
    public void M14Object_CoreOpeningDenialDoesNotProtectRaw124AndGuardStopsActualWrite(byte wireSlot)
    {
        M14WithRack(false, (rack, region) =>
        {
            var open = new GetDataHandlers.RequestTileEntityInteractionEventArgs
                { Player = ServerTShock.Players[Slot], TileEntity = rack };
            // This is the actual existing TShock region callback for opening122.
            new RequestTileEntityInteractionHandler().OnReceive(null!, open);
            Assert.That(open.Handled, Is.True, "Existing opening permission is real, but is not on the124 write route.");
            ushort type = (ushort)(wireSlot < 2 ? ItemID.WoodHelmet : ItemID.RedDye);
            var body = M14RackBody(wireSlot, type);
            NativeDisplay(124, body); // Unprotected real target-method control; no socket or public world.
            var array = wireSlot < 2 ? rack._items : rack._dyes;
            Assert.That(array[wireSlot % 2].type, Is.EqualTo(type));
            Assert.That(sent.Count(frame => frame.Id == 124), Is.EqualTo(1));
            array[wireSlot % 2].TurnToAir(); sent.Clear();
            Assert.That(M14Admission(body).Reason, Is.EqualTo("hat-rack-current-build-permission-denied"));
            Assert.That(RootDisplay(124, body).Handled, Is.True);
            Assert.That(rack._items.Concat(rack._dyes).All(item => item.IsAir), Is.True); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            region.AllowedIDs.Add(ServerTShock.Players[Slot].Account.ID);
            Assert.That(RootDisplay(124, body).Handled, Is.False); NativeDisplay(124, body);
            Assert.That(array[wireSlot % 2].type, Is.EqualTo(type)); Assert.That(sent.Single().Id, Is.EqualTo(124));
        });
    }

    [Test]
    public void M14Object_OpeningGrantIsRecheckedAfterPermissionRevocationAndFullObjectFootprintMatters()
    {
        M14WithRack(true, (rack, region) =>
        {
            var open = new GetDataHandlers.RequestTileEntityInteractionEventArgs
                { Player = ServerTShock.Players[Slot], TileEntity = rack };
            new RequestTileEntityInteractionHandler().OnReceive(null!, open); Assert.That(open.Handled, Is.False);
            Main.player[Slot].tileEntityAnchor.Set(M14RackId, M14RackX, M14RackY);
            Assert.That(RootDisplay(124, M14RackBody()).Handled, Is.False);
            region.AllowedIDs.Clear();
            Assert.That(RootDisplay(124, M14RackBody()).Handled, Is.True,
                "An accepted opening/anchor cannot retain a stale region write grant.");
            // The anchor itself is outside this new corner-only region, but the rack spans it.
            var corner = new Region(15, new Rectangle(M14RackX + 2, M14RackY + 3, 1, 1), "m14-corner", "other-owner", true, Main.worldID.ToString(), 0);
            ServerTShock.Regions.Regions = [corner];
            Assert.That(ServerTShock.Players[Slot].HasBuildPermission(M14RackX, M14RackY, false), Is.True);
            Assert.That(RootDisplay(124, M14RackBody()).Handled, Is.True);
            Assert.That(rack._items.Concat(rack._dyes).All(item => item.IsAir), Is.True);
            Assert.That(sent, Is.Empty); Assert.That(engine.CanWrite(session), Is.True);
        });
    }

    [Test]
    public void M14Object_CurrentPhysicalPlayerAndCurrentTargetReplaceClientSenderAndOldObjectGrant()
    {
        M14WithRack(true, (rack, region) =>
        {
            // A changed registered object with the same ID is resolved anew, not from an opening cache.
            TileEntity.ByID[M14RackId] = new TEHatRack { ID = M14RackId, Position = new Point16(M14RackX + 10, M14RackY) };
            ServerTShock.Regions.Regions = [new Region(16, new Rectangle(M14RackX + 10, M14RackY, 3, 4), "m14-new-object", "other-owner", true, Main.worldID.ToString(), 0)];
            Assert.That(RootDisplay(124, M14RackBody(sender: 255)).Handled, Is.True);
            TileEntity.ByID[M14RackId] = rack; ServerTShock.Regions.Regions = [region];
            Assert.That(RootDisplay(124, M14RackBody(sender: 255)).Handled, Is.False,
                "Native124 ignores this sender field; do not falsely sanction a different declared sender.");
            var prior = ServerTShock.Players[Slot];
            try
            {
                ServerTShock.Players[Slot] = new TSPlayer(Slot) { Group = new Group("m14-replacement"), IsLoggedIn = true,
                    Account = new UserAccount { ID = 6217, Name = "m14-replacement" } };
                Assert.That(M14Admission(M14RackBody()).RejectMalformed, Is.True,
                    "Slot reuse must use the current receiving account's rights, never the previous account's grant.");
            }
            finally { ServerTShock.Players[Slot] = prior; }
            Assert.That(engine.SanctionCount, Is.Zero); Assert.That(engine.CanWrite(session), Is.True);
        });
    }

    [TestCase((byte)4)] [TestCase((byte)255)]
    public void M14Object_AlreadySafeInvalidHatSlotsStayNoopEvenInDeniedRegion(byte wireSlot)
    {
        M14WithRack(false, (rack, _) =>
        {
            var body = M14RackBody(wireSlot);
            Assert.That(M14Admission(body).Reason, Is.EqualTo("hat-rack-existing-native-noop"));
            Assert.That(RootDisplay(124, body).Handled, Is.False); NativeDisplay(124, body);
            Assert.That(rack._items.Concat(rack._dyes).All(item => item.IsAir), Is.True); Assert.That(sent, Is.Empty);
        });
    }

    [Test]
    public void M14Object_MissingOrDifferentEntityRemainsNoopAndCurrentCoreCancellationIsNeverUndone()
    {
        M14WithRack(false, (rack, _) =>
        {
            var missing = M14RackBody(id: -1);
            Assert.That(RootDisplay(124, missing).Handled, Is.False); NativeDisplay(124, missing);
            TileEntity.ByID[M14RackId] = new TEDisplayDoll { ID = M14RackId };
            var wrong = M14RackBody();
            Assert.That(RootDisplay(124, wrong).Handled, Is.False); NativeDisplay(124, wrong);
            Assert.That(RootDisplay(124, wrong, true).Handled, Is.True);
            Assert.That(sent, Is.Empty); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public void M14Object_ExactNativePayloadAndRuntimeGateDoNotBorrowAnotherPacket()
    {
        foreach (int size in new[] { 0, 1, 5, 6, 10, 12, 1024 })
            Assert.That(M14Admission(new byte[size]).RejectMalformed, Is.True);
        Assert.That(M14ObjectPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)124, M14RackBody()), false), Is.Null);
        Assert.That(M14ObjectPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)121, new byte[12]), true), Is.Null);
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void M14Object_ExistingScopedPermissionProviderIsKeptAndItsFaultDoesNotEscapeOrBan()
    {
        M14WithRack(false, (_, _) =>
        {
            int calls = 0;
            void Grant(PlayerHasBuildPermissionEventArgs args)
            {
                calls++;
                Assert.That(args.Player, Is.SameAs(ServerTShock.Players[Slot]));
                Assert.That(args.X, Is.InRange(M14RackX, M14RackX + 2));
                Assert.That(args.Y, Is.InRange(M14RackY, M14RackY + 3));
                args.Result = PermissionHookResult.Granted;
            }
            PlayerHooks.PlayerHasBuildPermission += Grant;
            try
            {
                Assert.That(RootDisplay(124, M14RackBody()).Handled, Is.False);
                Assert.That(calls, Is.EqualTo(12), "Only the native3x4 footprint is checked.");
            }
            finally { PlayerHooks.PlayerHasBuildPermission -= Grant; }

            void Fault(PlayerHasBuildPermissionEventArgs _) => throw new InvalidOperationException("owned permission-provider failure");
            PlayerHooks.PlayerHasBuildPermission += Fault;
            try
            {
                Assert.That(M14Admission(M14RackBody()).Reason, Is.EqualTo("hat-rack-build-permission-check-failed"));
                Assert.That(RootDisplay(124, M14RackBody()).Handled, Is.True);
                Assert.That(sent, Is.Empty); Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            }
            finally { PlayerHooks.PlayerHasBuildPermission -= Fault; }
        });
    }

    [TestCase(ItemID.WoodHelmet)] [TestCase(ItemID.BorealWoodHelmet)]
    public void M14Object_OriginalQuickPlacementTransfersRealHatAndProduces124(short type)
    {
        M14WithRack(true, (rack, _) =>
        {
            int mode = Main.netMode, local = Main.myPlayer, priorTarget = TEHatRack.hatTargetSlot;
            try
            {
                Main.netMode = 1; Main.myPlayer = Slot;
                var item = new Item(); item.SetDefaults(type); Item[] inventory = [item];
                Assert.That(TEHatRack.CanQuickSwapIntoHatRack(item), Is.True);
                Assert.That(rack.TryFitting(inventory, 0), Is.True);
                Assert.That(inventory[0].IsAir, Is.True); Assert.That(rack._items[0].type, Is.EqualTo(type));
                var frame = sent.Single(frame => frame.Id == 124);
                Assert.That(frame.Target, Is.EqualTo(Slot)); Assert.That(frame.Damage, Is.EqualTo(M14RackId));
                Assert.That(frame.Knockback, Is.Zero); Assert.That(frame.Direction, Is.Zero);
                Assert.That(M14Admission(M14RackBody(type: (ushort)type)).RejectMalformed, Is.False);
            }
            finally { Main.netMode = mode; Main.myPlayer = local; TEHatRack.hatTargetSlot = priorTarget; }
        });
    }

    [TestCase(26, 0, ItemID.WoodHelmet)] [TestCase(27, 1, ItemID.RedDye)]
    public void M14Object_OriginalMousePlacementAndTakeProduceHatAndDye124(int context, int itemSlot, short type)
    {
        using var dyes = new M13OriginalDyes();
        M14WithRack(true, (rack, _) =>
        {
            int mode = Main.netMode, local = Main.myPlayer, cursor = Main.cursorOverride;
            bool left = Main.mouseLeft, released = Main.mouseLeftRelease;
            var mouseItem = Main.mouseItem;
            try
            {
                Main.netMode = 1; Main.myPlayer = Slot; Main.cursorOverride = 0;
                Main.mouseLeft = Main.mouseLeftRelease = true;
                Main.player[Slot].tileEntityAnchor.Set(M14RackId, M14RackX, M14RackY);
                Main.mouseItem = new Item(); Main.mouseItem.SetDefaults(type);
                var items = context == 26 ? rack._items : rack._dyes;
                Assert.That(ItemSlot.PickItemMovementAction(items, context, itemSlot, Main.mouseItem), Is.EqualTo(context == 26 ? 1 : 2));
                ItemSlot.LeftClick(items, context, itemSlot);
                Assert.That(items[itemSlot].type, Is.EqualTo(type)); Assert.That(Main.mouseItem.IsAir, Is.True);
                var placed = sent.Single(frame => frame.Id == 124);
                Assert.That(placed.Target, Is.EqualTo(Slot)); Assert.That(placed.Damage, Is.EqualTo(M14RackId));
                Assert.That(placed.Knockback, Is.EqualTo(itemSlot)); Assert.That(placed.Direction, Is.EqualTo(context == 27 ? 1 : 0));
                byte wireSlot = (byte)(itemSlot + (context == 27 ? 2 : 0));
                Assert.That(M14Admission(M14RackBody(wireSlot, (ushort)type)).RejectMalformed, Is.False);
                sent.Clear();
                ItemSlot.LeftClick(items, context, itemSlot);
                Assert.That(items[itemSlot].IsAir, Is.True); Assert.That(Main.mouseItem.type, Is.EqualTo(type));
                var taken = sent.Single(frame => frame.Id == 124);
                Assert.That(taken.Knockback, Is.EqualTo(itemSlot)); Assert.That(taken.Direction, Is.EqualTo(context == 27 ? 1 : 0));
                Assert.That(M14Admission(M14RackBody(wireSlot, 0, 0)).RejectMalformed, Is.False);
            }
            finally
            {
                Main.netMode = mode; Main.myPlayer = local; Main.cursorOverride = cursor;
                Main.mouseLeft = left; Main.mouseLeftRelease = released; Main.mouseItem = mouseItem;
            }
        });
    }
}
