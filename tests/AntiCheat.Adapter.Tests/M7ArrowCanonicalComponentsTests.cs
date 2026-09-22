using System.Reflection;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.Items;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

/// <summary>Exhaustive canonical item inputs and concrete buff producers. These component experiments
/// are deliberately not a certificate that all first-projectile creation paths are closed.</summary>
[TestFixture, NonParallelizable]
public sealed class M7ArrowCanonicalComponentsTests
{
    private Player[] oldPlayers = null!;
    private int oldMode, oldLocal;

    [SetUp]
    public void SetUp()
    {
        oldPlayers = Main.player; oldMode = Main.netMode; oldLocal = Main.myPlayer;
        // Canonical vanity defaults read the local player's appearance even when the
        // item is not an arrow candidate, so the exhaustive union needs a real slot.
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
        Main.netMode = 1; Main.myPlayer = 7;
    }

    [TearDown]
    public void TearDown()
    {
        Main.player = oldPlayers; Main.netMode = oldMode; Main.myPlayer = oldLocal;
    }

    [Test]
    public void NativeVariantAndPrefixUnionEnumeratesArrowWeaponAndAmmoComponents()
    {
        var variants = typeof(ItemVariants).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.FieldType == typeof(ItemVariant)).Select(x => (Name: x.Name, Value: (ItemVariant?)x.GetValue(null)))
            .Prepend((Name: "ambient-default", Value: (ItemVariant?)null)).ToArray();
        var candidates = new SortedDictionary<int, (int Maximum, int Prefix, string Variant, int Shoot, int Ammo)>();
        int ammoMaximum = 0;
        foreach (var variant in variants)
        {
            for (int type = 1; type < ItemID.Count; type++)
            {
                var canonical = new Item(); canonical.SetDefaults(type, variant.Value);
                if (canonical.ammo == AmmoID.Arrow && canonical.shoot == ProjectileID.WoodenArrowFriendly)
                    ammoMaximum = Math.Max(ammoMaximum, canonical.damage);
                if (canonical.useAmmo != AmmoID.Arrow && canonical.shoot != ProjectileID.WoodenArrowFriendly) continue;
                for (int prefix = 0; prefix < PrefixID.Count; prefix++)
                {
                    var item = new Item(); item.SetDefaults(type, variant.Value);
                    if (prefix != 0 && !item.Prefix(prefix)) continue;
                    if (!candidates.TryGetValue(type, out var previous) || item.damage > previous.Maximum)
                        candidates[type] = (item.damage, prefix, item.Variant is null ? "no-variant" : variant.Name, item.shoot, item.useAmmo);
                }
            }
        }
        Assert.That(variants.Length, Is.GreaterThan(1)); Assert.That(candidates, Is.Not.Empty);
        Assert.That(candidates.ContainsKey(ItemID.Phantasm), Is.True);
        Assert.That(candidates.ContainsKey(ItemID.WoodenBow), Is.True);
        Assert.That(ammoMaximum, Is.GreaterThan(0));
        foreach (var pair in candidates)
            TestContext.Out.WriteLine($"type={pair.Key} maximumPrefixedDamage={pair.Value.Maximum} prefix={pair.Value.Prefix} variant={pair.Value.Variant} shoot={pair.Value.Shoot} useAmmo={pair.Value.Ammo}");
        TestContext.Out.WriteLine($"Native all-exposed-variant + all-prefix item union: candidates={candidates.Count}; maximumWeapon={candidates.Values.Max(x => x.Maximum)}; maximumType1Ammo={ammoMaximum}; variantInputs={string.Join(',', variants.Select(x => x.Name))}. This is an unscaled component union, not a complete first-damage upper bound.");
    }

    [Test]
    public void NativePickAmmoEnumeratesCanonicalLauncherAmmoPairsIncludingSpecificMappings()
    {
        var variants = typeof(ItemVariants).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.FieldType == typeof(ItemVariant)).Select(x => (ItemVariant?)x.GetValue(null))
            .Prepend(null).ToArray();
        var found = new SortedSet<int>();
        var escaped = new SortedSet<string>();
        int attempted = 0, ordinaryOutputs = 0;
        foreach (var variant in variants)
        {
            var catalog = Enumerable.Range(1, ItemID.Count - 1).Select(type =>
            {
                var item = new Item(); item.SetDefaults(type, variant); item.stack = 999; return item;
            }).ToArray();
            // Native PickAmmo_IterateRange compares ammo/useAmmo and stack; it does not
            // consult notAmmo, so keep that larger actual receiver set in the experiment.
            var ammoByKind = catalog.Where(x => x.ammo > 0).ToLookup(x => x.ammo);
            foreach (var weapon in catalog.Where(x => x.useAmmo > 0))
            {
                var player = Main.player[7] = new Player { whoAmI = 7, active = true };
                player.inventory[0] = weapon; player.selectedItemState.Select(0);
                foreach (var ammo in ammoByKind[weapon.useAmmo])
                {
                    player.inventory[54] = ammo;
                    int projectile = weapon.shoot, damage = player.GetWeaponDamage(weapon);
                    float speed = weapon.shootSpeed, knockback = weapon.knockBack;
                    bool canShoot = false;
                    player.PickAmmo(weapon, ref projectile, ref speed, ref canShoot, ref damage, ref knockback,
                        out int actualAmmo, dontConsume: true);
                    attempted++;
                    Assert.That(canShoot, Is.True, $"canonical launcher{weapon.type}/ammo{ammo.type}");
                    Assert.That(actualAmmo, Is.EqualTo(ammo.type));
                    if (projectile != ProjectileID.WoodenArrowFriendly) continue;
                    ordinaryOutputs++; found.Add(weapon.type);
                    if (weapon.useAmmo != AmmoID.Arrow && weapon.shoot != ProjectileID.WoodenArrowFriendly)
                        escaped.Add($"weapon{weapon.type}/ammo{ammo.type}/useAmmo{weapon.useAmmo}");
                }
            }
        }
        Assert.That(attempted, Is.GreaterThan(0)); Assert.That(ordinaryOutputs, Is.GreaterThan(0));
        Assert.That(escaped, Is.Empty,
            "A native special launcher/ammo mapping producing type1 outside the canonical candidate union needs a new source component.");
        TestContext.Out.WriteLine($"Actual native PickAmmo pairs={attempted}; type1 outcomes={ordinaryOutputs}; unique type1 launcher types={string.Join(',', found)}; outside-candidate outcomes={escaped.Count}. Special launcher dictionaries and arithmetic projectile selectors were actually executed. This closes this item-pair component only; ItemCheck and delayed child creation are separate paths.");
    }

    [Test]
    public void ActualAddBuffDoesNotMultiplyArcheryDuplicatesAndNebulaLevelupReplacesItsGroup()
    {
        const int slot = 7;
        var previous = Main.player[slot]; int oldMode = Main.netMode, oldLocal = Main.myPlayer;
        void Suppress(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        HookEvents.Terraria.NetMessage.SendData += Suppress;
        try
        {
            Main.netMode = 1; Main.myPlayer = slot;
            var player = Main.player[slot] = new Player { whoAmI = slot, active = true };
            for (int i = 0; i < Player.maxBuffs + 1; i++) player.AddBuff(BuffID.Archery, 3600 + i);
            Assert.That(player.buffType.Count(x => x == BuffID.Archery), Is.EqualTo(1));
            player.UpdateBuffs(slot);
            Assert.That(player.arrowDamage, Is.EqualTo(1.1f).Within(0.000001f));
            for (int i = 0; i < 8; i++) player.NebulaLevelup(179);
            Assert.That(player.buffType.Count(x => x is >= 179 and <= 181), Is.EqualTo(1));
            Assert.That(player.buffType, Does.Contain(181));
            player.ResetEffects(); player.UpdateBuffs(slot);
            Assert.That(player.rangedDamage, Is.EqualTo(1.45f).Within(0.000001f));
            Assert.That(player.arrowDamage, Is.EqualTo(1.1f).Within(0.000001f));
            TestContext.Out.WriteLine("Actual AddBuff updates one Archery slot after more than maxBuffs repeated additions; eight actual NebulaLevelup calls keep one damage-group buff at level3, and ResetEffects->UpdateBuffs yields ranged1.45/arrow1.1. Direct SSC50 import and native player-load writers remain separate source obligations; this experiment does not assume accepted buff arrays are canonical.");
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendData -= Suppress;
            Main.player[slot] = previous; Main.netMode = oldMode; Main.myPlayer = oldLocal;
        }
    }
}
