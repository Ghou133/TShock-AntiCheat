using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    private const int M14LX = 30, M14LY = 30, M14LId = 14530;
    private static byte[] M14LPlacement(short stack = 1, short type = ItemID.WoodenSword)
    {
        byte[] body = new byte[9];
        BinaryPrimitives.WriteInt16LittleEndian(body, M14LX);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), M14LY);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(4), type);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(7), stack);
        return body;
    }

    private static M5WorldWorkCost M14LAdmission(int packet, byte[] body) =>
        M14LObjectPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)packet, body, Slot), true)!.Value;

    private void M14LWithObject(int packet, bool allowed, Action<TileEntity, Region> action)
    {
        var priorConfig = ServerTShock.Config; var priorRegions = ServerTShock.Regions;
        var actor = ServerTShock.Players[Slot]; var priorGroup = actor.Group;
        var position = new Point16(M14LX, M14LY);
        bool hadId = TileEntity.ByID.TryGetValue(M14LId, out var oldId);
        bool hadPosition = TileEntity.ByPosition.TryGetValue(position, out var oldPosition);
        var tiles = new List<(int X, int Y, ITile Tile)>();
        var framing = typeof(Framing).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(field => !field.IsLiteral && !field.IsInitOnly).Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
        TileEntity entity = packet switch { 89 => new TEItemFrame(), 123 => new TEWeaponsRack(), _ => new TEFoodPlatter() };
        int size = packet switch { 89 => 2, 123 => 3, _ => 1 };
        ushort tileType = packet switch { 89 => TileID.ItemFrame, 123 => TileID.WeaponsRack2, _ => TileID.FoodPlatter };
        try
        {
            Framing.Initialize();
            ServerTShock.Config = new TShockConfig();
            ServerTShock.Config.Settings.DisableBuild = false; ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.RequireLogin = false; ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            actor.Group = new Group("m14l-object-ordinary", permissions: Permissions.canbuild);
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            var region = new Region(145, new Rectangle(M14LX, M14LY, size, size), "m14l-display", "other-owner", true, Main.worldID.ToString(), 0);
            if (allowed) region.AllowedIDs.Add(actor.Account.ID);
            ServerTShock.Regions.Regions = [region];
            for (int dx = -1; dx <= 3; dx++) for (int dy = -1; dy <= 3; dy++)
            {
                int x = M14LX + dx, y = M14LY + dy; tiles.Add((x, y, Main.tile[x, y]));
                var tile = new Tile { wall = 1 };
                if (dx >= 0 && dx < size && dy >= 0 && dy < size)
                {
                    tile.active(true); tile.type = tileType; tile.frameX = (short)(dx * 18); tile.frameY = (short)(dy * 18);
                }
                Main.tile[x, y] = tile;
            }
            entity.ID = M14LId; entity.Position = position;
            TileEntity.ByID[M14LId] = entity; TileEntity.ByPosition[position] = entity;
            Assert.That(actor.HasPermission("anticheat.bypass"), Is.False);
            action(entity, region);
        }
        finally
        {
            foreach (var field in framing) field.Field.SetValue(null, field.Value);
            foreach (var tile in tiles) Main.tile[tile.X, tile.Y] = tile.Tile;
            if (hadId) TileEntity.ByID[M14LId] = oldId!; else TileEntity.ByID.Remove(M14LId);
            if (hadPosition) TileEntity.ByPosition[position] = oldPosition!; else TileEntity.ByPosition.Remove(position);
            actor.Group = priorGroup; ServerTShock.Config = priorConfig; ServerTShock.Regions = priorRegions;
        }
    }

    private static Item M14LItem(TileEntity entity) => entity switch
        { TEItemFrame frame => frame.item, TEWeaponsRack rack => rack.item, TEFoodPlatter platter => platter.item, _ => throw new InvalidOperationException() };

    [TestCase(89)] [TestCase(123)] [TestCase(133)]
    public void M14LObject_OriginalSenderTransfersOneAndPublishesTheExactPlacementMessage(int packet)
    {
        M14LWithObject(packet, true, (entity, _) =>
        {
            int mode = Main.netMode, local = Main.myPlayer; var mouse = Main.mouseItem; var projectiles = Main.projectile;
            try
            {
                Main.netMode = 1; Main.myPlayer = Slot;
                Main.projectile = Enumerable.Range(0, Main.maxProjectiles).Select(_ => new Projectile()).ToArray();
                var player = Main.player[Slot]; player.selectedItemState.Select(0);
                short type = packet == 133 ? ItemID.Apple : ItemID.WoodenSword;
                player.inventory[0].SetDefaults(type); player.inventory[0].stack = 2; player.itemTime = 0;
                switch (packet)
                {
                    case 89: TEItemFrame.PlaceItemInFrame(player, M14LX, M14LY); break;
                    case 123: TEWeaponsRack.PlaceItemInFrame(player, M14LX, M14LY); break;
                    case 133: TEFoodPlatter.PlaceItemInFrame(player, M14LX, M14LY); break;
                }
                var frame = sent.Single(frame => frame.Id == packet);
                Assert.That(frame.Target, Is.EqualTo(M14LX)); Assert.That(frame.Damage, Is.EqualTo(M14LY));
                Assert.That(frame.Knockback, Is.Zero); Assert.That(frame.Direction, Is.EqualTo(Slot));
                Assert.That(frame.Critical, Is.EqualTo(1), "Native sender number5 is the serialized Int16 stack, always one.");
                Assert.That(player.inventory[0].stack, Is.EqualTo(1)); Assert.That(M14LItem(entity).IsAir, Is.True);
            }
            finally { Main.netMode = mode; Main.myPlayer = local; Main.mouseItem = mouse; Main.projectile = projectiles; }
        });
    }

    [TestCase(89)] [TestCase(123)] [TestCase(133)]
    public void M14LObject_UnguardedNativeAcceptsTwoButGuardStopsThatExactRequest(int packet)
    {
        M14LWithObject(packet, true, (entity, _) =>
        {
            short type = packet == 133 ? ItemID.Apple : ItemID.WoodenSword;
            var body = M14LPlacement(2, type); NativeDisplay(packet, body);
            Assert.That(M14LItem(entity).type, Is.EqualTo(type)); Assert.That(M14LItem(entity).stack, Is.EqualTo(2));
            Assert.That(sent.Single().Id, Is.EqualTo(86));
            M14LItem(entity).TurnToAir(); sent.Clear();
            Assert.That(RootDisplay(packet, body).Handled, Is.True);
            Assert.That(M14LItem(entity).IsAir, Is.True); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [TestCase(89)] [TestCase(123)] [TestCase(133)]
    public void M14LObject_OriginalSinglePlacementIsAnEffectiveNativeWrite(int packet)
    {
        M14LWithObject(packet, true, (entity, _) =>
        {
            short type = packet == 133 ? ItemID.Apple : ItemID.WoodenSword;
            var item = new Item(); item.SetDefaults(type);
            Assert.That(packet switch { 89 => TEItemFrame.FitsItemFrame(item), 123 => TEWeaponsRack.FitsWeaponFrame(item), _ => TEFoodPlatter.FitsFoodPlatter(item) }, Is.True);
            var body = M14LPlacement(type: type);
            Assert.That(M14LAdmission(packet, body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(packet, body).Handled, Is.False);
            NativeDisplay(packet, body);
            Assert.That(M14LItem(entity).type, Is.EqualTo(type)); Assert.That(M14LItem(entity).stack, Is.EqualTo(1));
            Assert.That(sent.Count(frame => frame.Id == 86), Is.EqualTo(1));
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [TestCase(89, 0)] [TestCase(89, 2)] [TestCase(123, -1)] [TestCase(123, 32767)] [TestCase(133, -32768)] [TestCase(133, 2)]
    public void M14LObject_NonSingleTransferIsBlockedBeforeNativeWriteAndSameAccountRecovers(int packet, int stack)
    {
        M14LWithObject(packet, true, (entity, _) =>
        {
            short type = packet == 133 ? ItemID.Apple : ItemID.WoodenSword;
            var bad = M14LPlacement((short)stack, type);
            Assert.That(M14LAdmission(packet, bad).Reason, Is.EqualTo("display-placement-must-transfer-single-item"));
            Assert.That(RootDisplay(packet, bad).Handled, Is.True);
            Assert.That(M14LItem(entity).IsAir, Is.True); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            var legal = M14LPlacement(type: type);
            Assert.That(RootDisplay(packet, legal).Handled, Is.False); NativeDisplay(packet, legal);
            Assert.That(M14LItem(entity).type, Is.EqualTo(type)); Assert.That(M14LItem(entity).stack, Is.EqualTo(1));
            Assert.That(sent.Single().Id, Is.EqualTo(86));
        });
    }

    [Test]
    public void M14LObject_WeaponRackNativePreviouslyWritesDeniedRegionAndCurrentGuardBlocksThenRecovers()
    {
        M14LWithObject(123, false, (entity, region) =>
        {
            var body = M14LPlacement(); NativeDisplay(123, body);
            Assert.That(M14LItem(entity).type, Is.EqualTo(ItemID.WoodenSword)); Assert.That(sent.Single().Id, Is.EqualTo(86));
            M14LItem(entity).TurnToAir(); sent.Clear();
            Assert.That(M14LAdmission(123, body).Reason, Is.EqualTo("weapon-rack-current-build-permission-denied"));
            Assert.That(RootDisplay(123, body).Handled, Is.True);
            Assert.That(M14LItem(entity).IsAir, Is.True); Assert.That(sent, Is.Empty);
            region.AllowedIDs.Add(ServerTShock.Players[Slot].Account.ID);
            Assert.That(RootDisplay(123, body).Handled, Is.False); NativeDisplay(123, body);
            Assert.That(M14LItem(entity).type, Is.EqualTo(ItemID.WoodenSword)); Assert.That(sent.Single().Id, Is.EqualTo(86));
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [TestCase(89)] [TestCase(123)] [TestCase(133)]
    public void M14LObject_MissingOrDifferentTargetIsNotProofAndInvalidStacksStillBlock(int packet)
    {
        M14LWithObject(packet, false, (_, _) =>
        {
            TileEntity.ByPosition.Remove(new Point16(M14LX, M14LY));
            Assert.That(M14LAdmission(packet, M14LPlacement()).RejectMalformed, Is.False);
            TileEntity.ByPosition[new Point16(M14LX, M14LY)] = new TEDisplayDoll { ID = M14LId };
            Assert.That(M14LAdmission(packet, M14LPlacement()).RejectMalformed, Is.False);
            Assert.That(M14LAdmission(packet, M14LPlacement(2)).RejectMalformed, Is.True);
            Assert.That(engine.SanctionCount, Is.Zero); Assert.That(engine.CanWrite(session), Is.True);
        });
    }

    [TestCase(89)] [TestCase(123)] [TestCase(133)]
    public void M14LObject_ExactFrameRuntimeAndCoreCancellationRemainRequired(int packet)
    {
        foreach (int size in new[] { 0, 4, 7, 8, 10, 300 }) Assert.That(M14LAdmission(packet, new byte[size]).RejectMalformed, Is.True);
        Assert.That(M14LObjectPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)packet, M14LPlacement()), false), Is.Null);
        Assert.That(M14LObjectPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)124, new byte[11]), true), Is.Null);
        Assert.That(RootDisplay(packet, M14LPlacement(), true).Handled, Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase(89)] [TestCase(123)] [TestCase(133)]
    public void M14LObject_PreexistingPositionBoundsAreNotLostWhenTakingAdmissionOwnership(int packet)
    {
        var body = M14LPlacement(); BinaryPrimitives.WriteInt16LittleEndian(body, -1);
        Assert.That(M14LAdmission(packet, body).Reason, Is.EqualTo("display-placement-position-outside-world"));
        Assert.That(RootDisplay(packet, body).Handled, Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }
}
