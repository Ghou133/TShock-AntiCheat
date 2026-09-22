using System.Buffers.Binary;
using Microsoft.Xna.Framework.Graphics;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.GameContent.Creative;
using Terraria.GameContent.Items;
using Terraria.Graphics;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7CombatNativeEvidenceTests
{
    [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
    public void M12SigilSscReplacementRechecksWorldGateAtProducerRegardlessOfAnimationStarter(bool hard, bool golem)
    {
        M12WithNativeAnimationWorld(() =>
        {
            Main.hardMode = hard; NPC.downedGolemBoss = golem;
            int time = M12StartRifleThenSscReplace(ItemID.CelestialSigil);
            for (int frame = 0; frame <= time; frame++) Main.player[Actor].ItemCheck();
            var requests = sent.Where(bytes => bytes[2] == 61).Select(bytes => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(5))).ToArray();
            Assert.That(requests, Is.EqualTo(hard && golem ? new short[] { -8 } : Array.Empty<short>()));
            TestContext.Out.WriteLine($"Full native rifle animation -> SSC selected0 replacement with Sigil -> full ItemCheck: hard={hard},golem={golem},requests=[{string.Join(',', requests)}]. Emission point itself rechecks both world facts; PG-NAT-121 does not infer starter identity.");
        });
    }

    [TestCase(false, false, false)] [TestCase(false, true, true)] [TestCase(true, true, true)]
    public void M12MechdusaSscReplacementRechecksNativeWorldAtNpcProducer(bool zenith, bool remix, bool worthy)
    {
        M12WithNativeAnimationWorld(() =>
        {
            Main.zenithWorld = zenith; Main.remixWorld = remix; Main.getGoodWorld = worthy; Main.dayTime = false;
            int time = M12StartRifleThenSscReplace(5334);
            for (int frame = 0; frame <= time; frame++) Main.player[Actor].ItemCheck();
            var requests = sent.Where(bytes => bytes[2] == 61).Select(bytes => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(5))).ToArray();
            Assert.That(requests, Is.EqualTo(SpecialSeedFeatures.Mechdusa ? new short[] { -16 } : Array.Empty<short>()));
            TestContext.Out.WriteLine($"Full native rifle animation -> SSC selected0 replacement with Razor -> full ItemCheck: zenith={zenith},remix={remix},worthy={worthy},Mechdusa={SpecialSeedFeatures.Mechdusa},requests=[{string.Join(',', requests)}]. NPC.SpawnMechQueen checks the native feature before emitting; PG-NAT-012 shares no Solar starter-identity assumption.");
        });
    }

    [TestCase(544, false)] [TestCase(556, false)] [TestCase(557, false)]
    [TestCase(544, true)] [TestCase(556, true)] [TestCase(557, true)]
    public void M12MechanicalSummonNativeVariantGroupRechecksEachCurrentItemAtEmission(int type, bool mechdusaWorld)
    {
        M12WithNativeAnimationWorld(() =>
        {
            Main.remixWorld = Main.getGoodWorld = mechdusaWorld; Main.dayTime = false;
            int time = M12StartRifleThenSscReplace(type);
            Assert.That(Main.player[Actor].HeldItem.Variant == ItemVariants.DisabledBossSummonVariant, Is.EqualTo(mechdusaWorld));
            for (int frame = 0; frame <= time; frame++) Main.player[Actor].ItemCheck();
            short[] expected = mechdusaWorld ? [] : type == 544 ? [125, 126] : type == 556 ? [134] : [127];
            var requests = sent.Where(bytes => bytes[2] == 61).Select(bytes => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(5))).ToArray();
            Assert.That(requests, Is.EqualTo(expected));
            TestContext.Out.WriteLine($"MKLP-OP-081 projection item={type},nativeMechdusaWorld={mechdusaWorld},SSC replacement selected native variant; actual full ItemCheck requests=[{string.Join(',', requests)}]. Source prehardmode possession policy was not adopted.");
        });
    }

    // Explicit bounded candidate-source experiment: world7 exports need not have reached the client.
    // This case does not feed a fixture Complete flag and does not authorize a new hard rule.
    [TestCase(544)] [TestCase(556)] [TestCase(557)]
    public void M12MechanicalSummonNativeStaleVariantNeedsItsOwnHistoryContract(int type)
    {
        M12WithNativeAnimationWorld(() =>
        {
            Main.dayTime = false; int time = M12StartRifleThenSscReplace(type);
            Assert.That(Main.player[Actor].HeldItem.Variant, Is.Null);
            Main.remixWorld = Main.getGoodWorld = true;
            Assert.That(ItemVariants.SelectVariant(type), Is.SameAs(ItemVariants.DisabledBossSummonVariant));
            // Changing the world flag is not a native inventory refresh or proof of client receipt.
            Assert.That(Main.player[Actor].HeldItem.Variant, Is.Null);
            for (int frame = 0; frame <= time; frame++) Main.player[Actor].ItemCheck();
            Assert.That(sent.Any(bytes => bytes[2] == 61), Is.True);
            TestContext.Out.WriteLine($"item={type}: existing native non-disabled Variant survives changed world fields and its continued full ItemCheck emits summon61. This is a world-change native-method counterexample to current-world-only variant proof, not a claim that a from-start immutable world session is unsafe.");
        });
    }

    private int M12StartRifleThenSscReplace(int itemType)
    {
        var player = Main.player[Actor]; player.inventory[0].SetDefaults(ItemID.ClockworkAssaultRifle);
        player.inventory[54].SetDefaults(ItemID.MusketBall); player.inventory[54].stack = 100;
        player.controlUseItem = player.releaseUseItem = true;
        player.ItemCheck();
        Assert.That(sent.Any(bytes => bytes[2] == 27), Is.True);
        Assert.That(player.itemAnimation, Is.GreaterThan(player.itemTime));
        int time = player.itemTime, animation = player.itemAnimation;
        byte[] replacement = [5, Actor, 0, 0, 1, 0, 0, (byte)itemType, (byte)(itemType >> 8), 0];
        Receive(replacement, 256);
        Assert.That(player.HeldItem.type, Is.EqualTo(itemType)); Assert.That(player.itemAnimation, Is.EqualTo(animation));
        Assert.That(player.itemTime, Is.EqualTo(time)); Assert.That(player.selectedItemState.HasBufferedChange, Is.False);
        sent.Clear(); return time;
    }

    private void M12WithNativeAnimationWorld(Action scenario)
    {
        var oldTiles = Main.tile; var oldItems = Main.item; var oldView = Main.GameViewMatrix;
        int oldScreenWidth = Main.screenWidth, oldScreenHeight = Main.screenHeight, oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY;
        bool oldHard = Main.hardMode, oldGolem = NPC.downedGolemBoss, oldDay = Main.dayTime, oldEclipse = Main.eclipse,
            oldSsc = Main.ServerSideCharacter, oldZenith = Main.zenithWorld, oldRemix = Main.remixWorld, oldWorthy = Main.getGoodWorld;
        try
        {
            Main.maxTilesX = Main.maxTilesY = 100; Main.screenWidth = 800; Main.screenHeight = 600;
            Main.GameViewMatrix = new SpriteViewMatrix(null!);
            Main.GameViewMatrix.SetViewportOverride(new Viewport { Width = 800, Height = 600 });
            Main.tile = new ModFramework.DefaultCollection<ITile>(100, 100); _ = Main.tile[0, 0];
            for (int x = 0; x < 100; x++) for (int y = 0; y < 100; y++) Main.tile[x, y] = new Tile();
            Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            if (CreativePowerManager.Instance.GetPower<CreativePowers.GodmodePower>() is null) CreativePowerManager.Initialize();
            if (ArmorSetBonuses.All.Count == 0) ArmorSetBonuses.Initialize();
            if (ArmorSetBonuses.SetsContaining is null || ArmorSetBonuses.SetsContaining.Length == 0 || ArmorSetBonuses.SetsContaining[0] is null) ArmorSetBonuses.BuildLookup();
            Main.hardMode = Main.eclipse = NPC.downedGolemBoss = Main.zenithWorld = Main.remixWorld = Main.getGoodWorld = false;
            Main.dayTime = Main.ServerSideCharacter = true; sent.Clear();
            scenario();
        }
        finally
        {
            Main.tile = oldTiles; Main.item = oldItems; Main.GameViewMatrix = oldView;
            Main.screenWidth = oldScreenWidth; Main.screenHeight = oldScreenHeight; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
            Main.hardMode = oldHard; NPC.downedGolemBoss = oldGolem; Main.dayTime = oldDay; Main.eclipse = oldEclipse;
            Main.ServerSideCharacter = oldSsc; Main.zenithWorld = oldZenith; Main.remixWorld = oldRemix; Main.getGoodWorld = oldWorthy;
        }
    }
}
