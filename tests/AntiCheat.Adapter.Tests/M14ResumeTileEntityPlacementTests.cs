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
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    private const int M14ResumeX = 40, M14ResumeY = 40;

    private static byte[] M14ResumePlacement(byte type)
    {
        byte[] body = new byte[5];
        BinaryPrimitives.WriteInt16LittleEndian(body, M14ResumeX);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), M14ResumeY);
        body[4] = type;
        return body;
    }

    private static M5WorldWorkCost M14ResumeAdmission(byte[] body) =>
        M14ResumeTileEntityPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)87, body, Slot), true)!.Value;

    private void M14ResumeWithPlacement(int family, Action<byte, Region> action)
    {
        var oldManager = TileEntity.manager; int oldNext = TileEntity.TileEntitiesNextID;
        var oldById = TileEntity.ByID; var oldByPosition = TileEntity.ByPosition; var oldUpdates = TileEntity.UpdateEntities;
        var prototypes = new TileEntity[] { new TETrainingDummy(), new TEItemFrame(), new TELogicSensor(), new TEDisplayDoll(),
            new TEWeaponsRack(), new TEHatRack(), new TEFoodPlatter(), new TETeleportationPylon(), new TEDeadCellsDisplayJar(),
            new TEKiteAnchor(), new TECritterAnchor() };
        var ids = prototypes.SelectMany(p => p.GetType().GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy))
            .Where(f => !f.IsLiteral && !f.IsInitOnly && (f.FieldType == typeof(byte) || f.FieldType == typeof(int)))
            .Distinct().Select(f => (Field: f, Value: f.GetValue(null))).ToArray();
        var oldConfig = ServerTShock.Config; var oldRegions = ServerTShock.Regions;
        var actor = ServerTShock.Players[Slot]; var oldGroup = actor.Group;
        var tiles = new List<(int X, int Y, ITile Tile)>();
        try
        {
            TileEntity.ByID = []; TileEntity.ByPosition = []; TileEntity.UpdateEntities = []; TileEntity.InitializeAll();
            TileEntity.TileEntitiesNextID = 47000;
            ServerTShock.Config = new TShockConfig();
            ServerTShock.Config.Settings.DisableBuild = false; ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.RequireLogin = false; ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            actor.Group = new Group("m14-resume-object-ordinary", permissions: Permissions.canbuild);
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            // The anchor is deliberately outside this protected region; only the far footprint cell is protected.
            int width = family is 1 or 3 ? 2 : 3, height = family == 1 ? 2 : family == 5 ? 4 : 3;
            var region = new Region(1487, new Rectangle(M14ResumeX + width - 1, M14ResumeY + height - 1, 1, 1),
                "m14-resume-footprint", "another-owner", true, Main.worldID.ToString(), 0);
            region.AllowedIDs.Add(actor.Account.ID); ServerTShock.Regions.Regions = [region];
            ushort tileType = family switch { 1 => TileID.ItemFrame, 3 => TileID.DisplayDoll, 4 => TileID.WeaponsRack2, _ => TileID.HatRack };
            for (int dx = 0; dx < 3; dx++) for (int dy = 0; dy < 4; dy++)
            {
                int x = M14ResumeX + dx, y = M14ResumeY + dy; tiles.Add((x, y, Main.tile[x, y]));
                var tile = new Tile(); tile.active(true); tile.type = tileType;
                tile.frameX = (short)(dx * 18); tile.frameY = (short)(dy * 18); Main.tile[x, y] = tile;
            }
            Assert.That(actor.HasPermission("anticheat.bypass"), Is.False);
            Assert.That(actor.HasBuildPermission(M14ResumeX, M14ResumeY, false), Is.True);
            action((byte)family, region);
        }
        finally
        {
            foreach (var tile in tiles) Main.tile[tile.X, tile.Y] = tile.Tile;
            foreach (var id in ids) id.Field.SetValue(null, id.Value);
            TileEntity.manager = oldManager; TileEntity.TileEntitiesNextID = oldNext;
            TileEntity.ByID = oldById; TileEntity.ByPosition = oldByPosition; TileEntity.UpdateEntities = oldUpdates;
            actor.Group = oldGroup; ServerTShock.Config = oldConfig; ServerTShock.Regions = oldRegions;
        }
    }

    [TestCase(1)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
    public void M14ResumeEntity_NativeAfterPlacementSendsTopLeftAnchorAndRegisteredType(int family)
    {
        M14ResumeWithPlacement(family, (type, _) =>
        {
            int mode = Main.netMode, local = Main.myPlayer;
            try
            {
                Main.netMode = 1; Main.myPlayer = Slot;
                int result = family switch
                {
                    1 => TEItemFrame.Hook_AfterPlacement(M14ResumeX, M14ResumeY),
                    3 => TEDisplayDoll.Hook_AfterPlacement(M14ResumeX, M14ResumeY + 2),
                    4 => TEWeaponsRack.Hook_AfterPlacement(M14ResumeX, M14ResumeY),
                    _ => TEHatRack.Hook_AfterPlacement(M14ResumeX + 1, M14ResumeY + 3)
                };
                Assert.That(result, Is.EqualTo(-1));
                var frame = sent.Single(entry => entry.Id == 87);
                Assert.That(frame.Target, Is.EqualTo(M14ResumeX)); Assert.That(frame.Damage, Is.EqualTo(M14ResumeY));
                Assert.That(frame.Knockback, Is.EqualTo(type)); Assert.That(TileEntity.ByID, Is.Empty);
            }
            finally { Main.netMode = mode; Main.myPlayer = local; }
        });
    }

    [TestCase(1)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
    public void M14ResumeEntity_LegalCreationPublishes86AndDuplicateOrChangedTileAreNativeNoops(int family)
    {
        M14ResumeWithPlacement(family, (type, region) =>
        {
            byte[] body = M14ResumePlacement(type);
            Assert.That(M14ResumeAdmission(body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(87, body).Handled, Is.False); NativeDisplay(87, body);
            var entity = TileEntity.ByPosition[new Point16(M14ResumeX, M14ResumeY)];
            Assert.That(entity.type, Is.EqualTo(type)); Assert.That(sent.Single().Id, Is.EqualTo(86));
            region.AllowedIDs.Clear(); sent.Clear();
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-existing-native-noop"));
            Assert.That(RootDisplay(87, body).Handled, Is.False); NativeDisplay(87, body);
            Assert.That(TileEntity.ByID.Count, Is.EqualTo(1)); Assert.That(sent, Is.Empty);
            TileEntity.Remove(entity); Main.tile[M14ResumeX, M14ResumeY].active(false);
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-changed-tile-native-noop"));
            Assert.That(RootDisplay(87, body).Handled, Is.False); NativeDisplay(87, body);
            Assert.That(TileEntity.ByID, Is.Empty); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [TestCase(1)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
    public void M14ResumeEntity_FarCellRevocationBlocksBeforeCreationAndSameAccountRecovers(int family)
    {
        M14ResumeWithPlacement(family, (type, region) =>
        {
            byte[] body = M14ResumePlacement(type);
            region.AllowedIDs.Clear();
            Assert.That(ServerTShock.Players[Slot].HasBuildPermission(M14ResumeX, M14ResumeY, false), Is.True);
            NativeDisplay(87, body); // Same concrete request has a real unguarded entity write and publication.
            Assert.That(TileEntity.ByID.Count, Is.EqualTo(1)); Assert.That(sent.Single().Id, Is.EqualTo(86));
            TileEntity.Remove(TileEntity.ByID.Values.Single()); sent.Clear(); int beforeNext = TileEntity.TileEntitiesNextID;
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-current-footprint-denied"));
            Assert.That(RootDisplay(87, body).Handled, Is.True);
            Assert.That(TileEntity.ByID, Is.Empty); Assert.That(TileEntity.ByPosition, Is.Empty);
            Assert.That(TileEntity.TileEntitiesNextID, Is.EqualTo(beforeNext)); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            region.AllowedIDs.Add(ServerTShock.Players[Slot].Account.ID);
            Assert.That(RootDisplay(87, body).Handled, Is.False); NativeDisplay(87, body);
            Assert.That(TileEntity.ByID.Count, Is.EqualTo(1)); Assert.That(sent.Single().Id, Is.EqualTo(86));
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public void M14ResumeEntity_UnknownTypesRuntimeAndPriorCancellationDoNotBecomeAccountProof()
    {
        M14ResumeWithPlacement(4, (_, region) =>
        {
            region.AllowedIDs.Clear();
            var unregistered = M14ResumePlacement(255);
            Assert.That(M14ResumeAdmission(unregistered).RejectMalformed, Is.False);
            NativeDisplay(87, unregistered); Assert.That(TileEntity.ByID, Is.Empty); Assert.That(sent, Is.Empty);
            var unmodeled = M14ResumePlacement(TileEntityType<TEItemFrame>.EntityTypeID);
            Assert.That(M14ResumeAdmission(unmodeled).RejectMalformed, Is.False);
            var args = M2ContractsTests.Packet((PacketTypes)87, M14ResumePlacement(4), Slot);
            Assert.That(M14ResumeTileEntityPlacementSafety.Read(args, false), Is.Null);
            Assert.That(M14ResumeTileEntityPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)123, new byte[9], Slot), true), Is.Null);
            Assert.That(RootDisplay(87, unregistered, true).Handled, Is.True);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public void M14ResumeEntity_BadFrameAndCoordinatesBlockWithoutSanction()
    {
        M14ResumeWithPlacement(4, (_, _) =>
        {
            foreach (int size in new[] { 0, 1, 4, 6, 20 })
            {
                Assert.That(M14ResumeAdmission(new byte[size]).RejectMalformed, Is.True);
                Assert.That(RootDisplay(87, new byte[size]).Handled, Is.True);
            }
            byte[] body = M14ResumePlacement(4); BinaryPrimitives.WriteInt16LittleEndian(body, -1);
            Assert.That(RootDisplay(87, body).Handled, Is.True);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            Assert.That(TileEntity.ByID, Is.Empty); Assert.That(sent, Is.Empty);
        });
    }

    // These tests stop at local managed admission/root dispatch. They never call
    // NativeDisplay, native placement, networking, a client, or an injected hook.
    [TestCase(0)] [TestCase(36)] [TestCase(72)]
    public void M14ItemFrame_LegalStylesRemainAdmittedWithoutMutatingEntities(int frameX)
    {
        M14ResumeWithPlacement(1, (type, _) =>
        {
            for (int dx = 0; dx < 2; dx++) for (int dy = 0; dy < 2; dy++)
                Main.tile[M14ResumeX + dx, M14ResumeY + dy].frameX = (short)(frameX + dx * 18);
            byte[] body = M14ResumePlacement(type);
            Assert.That(M14ResumeAdmission(body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            M14ItemFrameAssertNoWritesOrSanctions();
        });
    }

    [Test]
    public void M14ItemFrame_LegalPermissionOutsideTwoByTwoFootprintDoesNotBlock()
    {
        M14ResumeWithPlacement(1, (type, region) =>
        {
            region.Area = new Rectangle(M14ResumeX + 2, M14ResumeY + 1, 1, 1);
            region.AllowedIDs.Clear();
            Assert.That(ServerTShock.Players[Slot].HasBuildPermission(M14ResumeX + 2, M14ResumeY + 1, false), Is.False);
            byte[] body = M14ResumePlacement(type);
            Assert.That(M14ResumeAdmission(body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            M14ItemFrameAssertNoWritesOrSanctions();
        });
    }

    [Test]
    public void M14ItemFrame_LegalOccupiedChangedTileAndUnmodeledPrototypeRemainNoops()
    {
        M14ResumeWithPlacement(1, (type, region) =>
        {
            region.AllowedIDs.Clear();
            byte[] body = M14ResumePlacement(type);
            var position = new Point16(M14ResumeX, M14ResumeY);
            var occupied = new TEItemFrame { ID = 47900, Position = position };
            TileEntity.ByID.Add(occupied.ID, occupied); TileEntity.ByPosition.Add(position, occupied);
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-existing-native-noop"));
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            Assert.That(TileEntity.ByPosition[position], Is.SameAs(occupied));
            TileEntity.ByID.Clear(); TileEntity.ByPosition.Clear();

            Main.tile[M14ResumeX, M14ResumeY].type = TileID.WeaponsRack2;
            Assert.That(M14ResumeAdmission(body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            Main.tile[M14ResumeX, M14ResumeY].type = TileID.ItemFrame;
            Main.tile[M14ResumeX, M14ResumeY].active(false);
            Assert.That(M14ResumeAdmission(body).RejectMalformed, Is.False);
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            Main.tile[M14ResumeX, M14ResumeY].active(true);

            // An extension subtype is not the exact audited native prototype.
            TileEntity.manager._types[type] = new M14ItemFrameExtension();
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-unmodeled-or-native-noop"));
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            Assert.That(M14ResumeAdmission(M14ResumePlacement(255)).RejectMalformed, Is.False);
            M14ItemFrameAssertNoWritesOrSanctions();
        });
    }

    [Test]
    public void M14ItemFrame_LegalUnknownRuntimeAndPriorCancellationKeepTheirBoundaries()
    {
        M14ResumeWithPlacement(1, (type, _) =>
        {
            byte[] body = M14ResumePlacement(type);
            Assert.That(M14ResumeTileEntityPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)87, body, Slot), false), Is.Null);
            Assert.That(M14ResumeTileEntityPlacementSafety.Read(M2ContractsTests.Packet((PacketTypes)89, body, Slot), true), Is.Null);
            Assert.That(RootDisplay(87, body, true).Handled, Is.True);
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            M14ItemFrameAssertNoWritesOrSanctions();
        });
    }

    [TestCase(0, 1)] [TestCase(1, 0)] [TestCase(1, 1)]
    public async Task M14ItemFrame_DeniedNonAnchorCellBlocksAndSameAccountRecovers(int dx, int dy)
    {
        M14ResumeWithPlacement(1, (type, region) =>
        {
            region.Area = new Rectangle(M14ResumeX + dx, M14ResumeY + dy, 1, 1);
            region.AllowedIDs.Clear();
            Assert.That(ServerTShock.Players[Slot].HasBuildPermission(M14ResumeX, M14ResumeY, false), Is.True);
            byte[] body = M14ResumePlacement(type);
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-current-footprint-denied"));
            Assert.That(RootDisplay(87, body).Handled, Is.True);
            M14ItemFrameAssertNoWritesOrSanctions();
            region.AllowedIDs.Add(ServerTShock.Players[Slot].Account.ID);
            Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-current-footprint-allowed"));
            Assert.That(RootDisplay(87, body).Handled, Is.False);
            M14ItemFrameAssertNoWritesOrSanctions();
        });
        await engine.PumpAsync(); Assert.That(store.Bans, Is.Zero);
    }

    [Test]
    public void M14ItemFrame_MissingReceivingActorBlocksWithoutRevokingAuthenticatedBinding()
    {
        M14ResumeWithPlacement(1, (type, _) =>
        {
            var actor = ServerTShock.Players[Slot];
            try
            {
                ServerTShock.Players[Slot] = null;
                byte[] body = M14ResumePlacement(type);
                Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-current-receiving-player-unavailable"));
                Assert.That(RootDisplay(87, body).Handled, Is.True);
                M14ItemFrameAssertNoWritesOrSanctions();
            }
            finally { ServerTShock.Players[Slot] = actor; }
            Assert.That(RootDisplay(87, M14ResumePlacement(type)).Handled, Is.False);
        });
    }

    [Test]
    public void M14ItemFrame_ExactFrameAndWorldFootprintRejectOnlyAtAdmission()
    {
        M14ResumeWithPlacement(1, (type, _) =>
        {
            foreach (int length in new[] { 0, 4, 6 })
                Assert.That(RootDisplay(87, new byte[length]).Handled, Is.True);
            int oldWidth = Main.maxTilesX;
            try
            {
                // The anchor still exists in the local array; no out-of-range native call is made.
                Main.maxTilesX = M14ResumeX + 1;
                byte[] body = M14ResumePlacement(type);
                Assert.That(M14ResumeAdmission(body).Reason, Is.EqualTo("display-entity-create-footprint-outside-world"));
                Assert.That(RootDisplay(87, body).Handled, Is.True);
                M14ItemFrameAssertNoWritesOrSanctions();
            }
            finally { Main.maxTilesX = oldWidth; }
        });
    }

    private void M14ItemFrameAssertNoWritesOrSanctions()
    {
        Assert.That(TileEntity.ByID, Is.Empty); Assert.That(TileEntity.ByPosition, Is.Empty);
        Assert.That(TileEntity.TileEntitiesNextID, Is.EqualTo(47000)); Assert.That(sent, Is.Empty);
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        Assert.That(store.Bans, Is.Zero);
    }

    private sealed class M14ItemFrameExtension : TEItemFrame { }
}
