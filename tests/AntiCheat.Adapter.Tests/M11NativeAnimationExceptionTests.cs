using System.Buffers.Binary;
using Microsoft.Xna.Framework.Graphics;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Creative;
using Terraria.Graphics;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7CombatNativeEvidenceTests
{
    [Test]
    public void M11_NativeSscSelectedSlotReplacementCanContinueAnotherItemsAnimationIntoSolarRequest()
    {
        var oldTiles = Main.tile; var oldItems = Main.item;
        var oldView = Main.GameViewMatrix; int oldScreenWidth = Main.screenWidth, oldScreenHeight = Main.screenHeight;
        int oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY;
        bool oldHard = Main.hardMode, oldDay = Main.dayTime, oldEclipse = Main.eclipse, oldSsc = Main.ServerSideCharacter;
        try
        {
            Main.maxTilesX = Main.maxTilesY = 100;
            // PlayInstruments reads the camera size even for a rifle; use the native headless
            // viewport override instead of creating a graphics device or intercepting ItemCheck.
            Main.screenWidth = 800; Main.screenHeight = 600;
            Main.GameViewMatrix = new SpriteViewMatrix(null!);
            Main.GameViewMatrix.SetViewportOverride(new Viewport { Width = 800, Height = 600 });
            Main.tile = new ModFramework.DefaultCollection<ITile>(100, 100); _ = Main.tile[0, 0];
            for (int x = 0; x < 100; x++) for (int y = 0; y < 100; y++) Main.tile[x, y] = new Tile();
            Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            if (CreativePowerManager.Instance.GetPower<CreativePowers.GodmodePower>() is null) CreativePowerManager.Initialize();
            if (ArmorSetBonuses.All.Count == 0) ArmorSetBonuses.Initialize();
            if (ArmorSetBonuses.SetsContaining is null || ArmorSetBonuses.SetsContaining.Length == 0 || ArmorSetBonuses.SetsContaining[0] is null)
                ArmorSetBonuses.BuildLookup();
            Main.hardMode = Main.eclipse = false; Main.dayTime = Main.ServerSideCharacter = true;
            var player = Main.player[Actor];
            player.inventory[0].SetDefaults(ItemID.ClockworkAssaultRifle);
            player.inventory[54].SetDefaults(ItemID.MusketBall); player.inventory[54].stack = 100;
            player.controlUseItem = player.releaseUseItem = true;
            Assert.That(player.selectedItem, Is.Zero);
            Assert.That(player.ItemCheck_TryStartUse(player.HeldItem), Is.True);
            player.ItemCheck(); // Entire native caller starts the rifle animation and performs its first shot.
            Assert.That(player.itemAnimation, Is.GreaterThan(player.itemTime));
            Assert.That(sent.Any(frame => frame[2] == 27), Is.True, "The real rifle producer shot before SSC replacement.");
            int animation = player.itemAnimation, time = player.itemTime;

            // Native SSC receiver replaces the same index, so no selected-index change is buffered.
            byte[] replacement = [5, Actor, 0, 0, 1, 0, 0, 207, 10, 0]; // ordinary type2767, stack1, prefix0
            Receive(replacement, 256);
            Assert.That(player.HeldItem.type, Is.EqualTo(ItemID.SolarTablet));
            Assert.That(player.itemAnimation, Is.EqualTo(animation));
            Assert.That(player.itemTime, Is.EqualTo(time));
            Assert.That(player.selectedItemState.HasBufferedChange, Is.False);
            Assert.That(player.ItemCheck_TryStartUse(player.HeldItem), Is.False, "A fresh Tablet animation would be rejected.");
            sent.Clear();
            for (int frame = 0; frame <= time && !sent.Any(bytes => bytes[2] == 61); frame++) player.ItemCheck();
            var request = sent.Single(bytes => bytes[2] == 61);
            Assert.That(BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(3)), Is.EqualTo(Actor));
            Assert.That(BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(5)), Is.EqualTo(-6));
            Assert.That(Main.hardMode, Is.False);
            TestContext.Out.WriteLine($"Real native ItemCheck starts Clockwork Assault Rifle, animation={animation}/time={time}; ordinary S2C SSC5 replaces selected0 with2767 without clearing either or buffering selection; continued full ItemCheck emits {Convert.ToHexString(request)} before hardmode. Start-gate history cannot prove this sender cheated. Headless native experiment, not original GUI execution.");
        }
        finally
        {
            Main.tile = oldTiles; Main.item = oldItems; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
            Main.GameViewMatrix = oldView; Main.screenWidth = oldScreenWidth; Main.screenHeight = oldScreenHeight;
            Main.hardMode = oldHard; Main.dayTime = oldDay; Main.eclipse = oldEclipse; Main.ServerSideCharacter = oldSsc;
        }
    }
}
