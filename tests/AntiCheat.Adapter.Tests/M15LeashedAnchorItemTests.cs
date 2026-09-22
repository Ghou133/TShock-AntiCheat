using System.Buffers.Binary;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    private static byte[] M15LeashedBody(short item = 0)
    {
        byte[] body = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(body, M14ResumeX);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), M14ResumeY);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(4), item); return body;
    }

    private void M15WithLeashed(bool critter, Action<TELeashedEntityAnchorWithItem, TShockAPI.DB.Region> action)
    {
        M14ResumeWithPlacement(1, (_, region) =>
        {
            region.Area = new Rectangle(M14ResumeX, M14ResumeY, 1, 1);
            TELeashedEntityAnchorWithItem target = critter ? new TECritterAnchor() : new TEKiteAnchor();
            target.ID = 47000; target.Position = new Point16(M14ResumeX, M14ResumeY); target.itemType = 1;
            TileEntity.ByID[target.ID] = target; TileEntity.ByPosition[target.Position] = target;
            Main.tile[M14ResumeX, M14ResumeY].type = (ushort)(critter ? 724 : 723);
            action(target, region);
        });
    }

    [TestCase(false)] [TestCase(true)]
    public void M15Leashed_LegalNativePlacementProducerSendsItsHeldItem(bool critter)
    {
        M15WithLeashed(critter, (target, _) =>
        {
            int mode = Main.netMode, local = Main.myPlayer;
            var player = Main.player[Slot]; var held = player.inventory[0];
            try
            {
                Main.netMode = 1; Main.myPlayer = Slot; player.selectedItemState.Select(0);
                player.inventory[0] = new Item(); player.inventory[0].SetDefaults(critter ? ItemID.Bunny : ItemID.KiteBlue);
                player.inventory[0].stack = 2;
                int itemType = player.inventory[0].type;
                int result = critter ? TECritterAnchor.Hook_AfterPlacement(M14ResumeX, M14ResumeY, 724, 0, 1, 0)
                    : TEKiteAnchor.Hook_AfterPlacement(M14ResumeX, M14ResumeY, 723, 0, 1, 0);
                Assert.That(result, Is.EqualTo(-1));
                var frame = sent.Single(s => s.Id == 156);
                Assert.That(frame.Target, Is.EqualTo(M14ResumeX)); Assert.That(frame.Damage, Is.EqualTo(M14ResumeY));
                Assert.That(frame.Knockback, Is.EqualTo(itemType));
                Assert.That(target.itemType, Is.EqualTo(1));
            }
            finally { Main.netMode = mode; Main.myPlayer = local; player.inventory[0] = held; }
        });
    }

    [TestCase(false)] [TestCase(true)]
    public void M15Leashed_CurrentPermissionBlocksAndSameAccountRecovers(bool critter)
    {
        M15WithLeashed(critter, (target, region) =>
        {
            var body = M15LeashedBody();
            Assert.That(RootDisplay(156, body).Handled, Is.False);
            region.AllowedIDs.Clear();
            Assert.That(RootDisplay(156, body).Handled, Is.True);
            Assert.That(target.itemType, Is.EqualTo(1)); Assert.That(target.leashedEntity, Is.Null);
            Assert.That(sent, Is.Empty); Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero); Assert.That(store.Bans, Is.Zero);
            region.AllowedIDs.Add(ServerTShock.Players[Slot].Account.ID);
            Assert.That(RootDisplay(156, body).Handled, Is.False);
            NativeDisplay(156, body);
            Assert.That(target.itemType, Is.Zero, "Actual receiver executes InsertItem only after admission.");
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(store.Bans, Is.Zero);
        });
    }

    [TestCase(false)] [TestCase(true)]
    public void M15Leashed_AdjacentDeniedCellAndMissingObjectRemainNativeCompatible(bool critter)
    {
        M15WithLeashed(critter, (target, region) =>
        {
            region.Area = new Rectangle(M14ResumeX + 1, M14ResumeY, 1, 1); region.AllowedIDs.Clear();
            Assert.That(RootDisplay(156, M15LeashedBody()).Handled, Is.False);
            TileEntity.ByPosition.Remove(target.Position); TileEntity.ByID.Remove(target.ID);
            Assert.That(RootDisplay(156, M15LeashedBody()).Handled, Is.False);
            NativeDisplay(156, M15LeashedBody()); Assert.That(sent, Is.Empty); Assert.That(store.Bans, Is.Zero);
        });
    }

    [Test]
    public void M15Leashed_UnknownRuntimeSubtypeAndPriorCancellationKeepBoundaries()
    {
        M15WithLeashed(false, (target, region) =>
        {
            region.AllowedIDs.Clear(); var body = M15LeashedBody();
            Assert.That(M15LeashedAnchorItemSafety.Read(M2ContractsTests.Packet((PacketTypes)156, body, Slot), false), Is.Null);
            Assert.That(M15LeashedAnchorItemSafety.Read(M2ContractsTests.Packet((PacketTypes)87, body, Slot), true), Is.Null);
            TileEntity.ByPosition[target.Position] = new M15KiteExtension { ID = target.ID, Position = target.Position };
            Assert.That(RootDisplay(156, body).Handled, Is.False);
            Assert.That(RootDisplay(156, body, true).Handled, Is.True);
            Assert.That(store.Bans, Is.Zero);
        });
    }

    [Test]
    public void M15Leashed_InvalidFramesAndMissingCurrentActorOnlyBlock()
    {
        M15WithLeashed(false, (_, _) =>
        {
            foreach (int length in new[] { 0, 5, 7 }) Assert.That(RootDisplay(156, new byte[length]).Handled, Is.True);
            var body = M15LeashedBody(); BinaryPrimitives.WriteInt16LittleEndian(body, -1);
            Assert.That(RootDisplay(156, body).Handled, Is.True);
            var actor = ServerTShock.Players[Slot];
            try { ServerTShock.Players[Slot] = null; Assert.That(RootDisplay(156, M15LeashedBody()).Handled, Is.True); }
            finally { ServerTShock.Players[Slot] = actor; }
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(store.Bans, Is.Zero); Assert.That(sent, Is.Empty);
        });
    }

    private sealed class M15KiteExtension : TEKiteAnchor { }
}
