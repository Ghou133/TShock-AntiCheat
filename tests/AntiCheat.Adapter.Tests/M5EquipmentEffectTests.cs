using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M5EquipmentEffectTests
{
    [Test]
    public void ProjectionMatchesNativeExpertRestrictionAndLockedStorageWithoutAssumingClientTransitionEnd()
    {
        int oldMode = Main.GameMode;
        try
        {
            Main.GameMode = 0;
            var player = new Player(); player.armor[3].SetDefaults(ItemID.WormScarf);
            Assert.That(player.armor[3].expertOnly, Is.False, "This target permits ordinary WormScarf use in a non-expert world.");
            var native = M5EquipmentContexts.Capture(new(Guid.NewGuid(), 1, 1, 1), player);
            Assert.That(native.Slots[3].AppliesFunctionalMethod, Is.True);
            // Exercise the real native flag gate with explicitly marked server/plugin state;
            // do not invent this flag for all expert-rarity items.
            player.armor[3].expertOnly = true;
            player.armor[8].SetDefaults(ItemID.AvengerEmblem);
            player.armor[13].SetDefaults(ItemID.AvengerEmblem);
            var snapshot = M5EquipmentContexts.Capture(new(Guid.NewGuid(), 1, 1, 1), player);
            Assert.That(snapshot.Complete, Is.True); Assert.That(snapshot.AtomicClientTransitionComplete, Is.False);
            Assert.That(snapshot.Slots[3].AppliesFunctionalMethod, Is.False);
            Assert.That(snapshot.Slots[8].AppliesFunctionalMethod, Is.False);
            Assert.That(snapshot.Slots[8].GrantsStatistics, Is.False);
            float before = player.endurance; player.ApplyEquipFunctional(3, player.armor[3]);
            Assert.That(player.endurance, Is.EqualTo(before), "Actual native method excludes expert-only item in this world.");
            Main.GameMode = 1;
            var expert = M5EquipmentContexts.Capture(snapshot.Session, player);
            Assert.That(expert.Slots[3].AppliesFunctionalMethod, Is.True);
            player.ApplyEquipFunctional(3, player.armor[3]);
            Assert.That(player.endurance, Is.GreaterThan(before), "Native expert-allowed method applies the actual benefit.");
        }
        finally { Main.GameMode = oldMode; }
    }

    [Test]
    public void DuplicateTransitionCanReallyGrantStatisticsButStillDoesNotProveClientCheating()
    {
        int oldMode = Main.GameMode;
        try
        {
            Main.GameMode = 0;
            var player = new Player();
            player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[4].SetDefaults(ItemID.AvengerEmblem);
            float before = player.meleeDamage;
            player.ApplyEquipFunctional(3, player.armor[3]); player.ApplyEquipFunctional(4, player.armor[4]);
            Assert.That(player.meleeDamage - before, Is.EqualTo(0.24f).Within(0.0001f));
            var snapshot = M5EquipmentContexts.Capture(new(Guid.NewGuid(), 1, 1, 1), player);
            Assert.That(snapshot.Slots[3].AppliesFunctionalMethod && snapshot.Slots[4].AppliesFunctionalMethod, Is.True);
            Assert.That(snapshot.AtomicClientTransitionComplete, Is.False,
                "Two accepted slot packets may be an original client's intermediate move, even when the server applies both.");
        }
        finally { Main.GameMode = oldMode; }
    }

    [Test]
    public void M6FullNativeEquipmentUpdateSkipsLockedAndVanityPrefixEffectsButKeepsStorage()
    {
        int oldMode = Main.GameMode, oldNet = Main.netMode, oldLocal = Main.myPlayer;
        try
        {
            Main.GameMode = 0; Main.netMode = 2; Main.myPlayer = 255;
            var player = new Player { whoAmI = 12 };
            player.armor[8].SetDefaults(ItemID.AvengerEmblem); player.armor[8].Prefix(PrefixID.Warding);
            player.armor[13].SetDefaults(ItemID.AvengerEmblem); player.armor[13].Prefix(PrefixID.Warding);
            int defense = player.statDefense; float damage = player.meleeDamage;
            player.UpdateEquips(12);
            var snapshot = M5EquipmentContexts.Capture(new(Guid.NewGuid(), 1, 12, 1), player);
            Assert.That(player.statDefense, Is.EqualTo(defense));
            Assert.That(player.meleeDamage, Is.EqualTo(damage));
            Assert.That(snapshot.Slots[8].AppliesPrefixMethod, Is.False);
            Assert.That(snapshot.Slots[8].AppliesFunctionalMethod, Is.False);
            Assert.That(player.armor[8].type, Is.EqualTo(ItemID.AvengerEmblem));
            Assert.That(player.armor[13].type, Is.EqualTo(ItemID.AvengerEmblem));
            Assert.That(snapshot.AtomicClientTransitionComplete, Is.False);
        }
        finally { Main.GameMode = oldMode; Main.netMode = oldNet; Main.myPlayer = oldLocal; }
    }

    [Test]
    public void M6FullNativeUpdateDoesNotMultiplyAccessoryBenefitsByMalformedStackOrGrantArmorPrefixes()
    {
        int oldMode = Main.GameMode, oldNet = Main.netMode, oldLocal = Main.myPlayer;
        try
        {
            Main.GameMode = 0; Main.netMode = 2; Main.myPlayer = 255;
            var player = new Player { whoAmI = 12 };
            player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[3].Prefix(PrefixID.Warding);
            player.armor[3].stack = short.MaxValue; // Explicit malformed server-state fixture, never holder attribution.
            player.armor[0].SetDefaults(ItemID.WoodHelmet); player.armor[0].prefix = PrefixID.Warding;
            int defense = player.statDefense; float damage = player.meleeDamage;
            player.UpdateEquips(12);
            var snapshot = M5EquipmentContexts.Capture(new(Guid.NewGuid(), 1, 12, 1), player);
            Assert.That(player.meleeDamage - damage, Is.EqualTo(0.12f).Within(0.0001f));
            Assert.That(player.statDefense - defense, Is.EqualTo(player.armor[0].defense + 4));
            Assert.That(snapshot.Slots[0].GrantsStatistics, Is.True);
            Assert.That(snapshot.Slots[0].AppliesPrefixMethod, Is.False);
            Assert.That(snapshot.Slots[3].AppliesPrefixMethod, Is.True);
            Assert.That(snapshot.AtomicClientTransitionComplete, Is.False);
        }
        finally { Main.GameMode = oldMode; Main.netMode = oldNet; Main.myPlayer = oldLocal; }
    }
}
