using NUnit.Framework;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M18ArrowSourceResetIntervalTests
{
    [Test]
    public void M18_ActualCanonicalSlotResetDoesNotFreezeLaterNativeBuffAndItemExportInputs()
    {
        using var language = new M18NativeLanguageScope();
        bool oldSsc = Main.ServerSideCharacter;
        var client = Main.player[Actor];
        try
        {
            Main.netMode = 2; Main.myPlayer = 255;
            var server = new Player { whoAmI = Actor };
            server.inventory[0].SetDefaults(ItemID.WoodenBow);
            server.inventory[54].SetDefaults(ItemID.WoodenArrow); server.inventory[54].stack = 100;
            Main.player[Actor] = server; sent.Clear();
            NetMessage.SendData(5, Actor, -1, number: Actor, number2: PlayerItemSlotID.Inventory0);
            NetMessage.SendData(5, Actor, -1, number: Actor, number2: PlayerItemSlotID.Inventory0 + 54);
            byte[][] reset = sent.Where(frame => frame[2] == 5).ToArray();
            Main.netMode = 1; Main.myPlayer = Actor; Main.ServerSideCharacter = true; Main.player[Actor] = client;
            foreach (byte[] frame in reset) Receive(frame[2..], 256);
            client.selectedItemState.Select(0);

            (int Damage, string Declaration, float Multiplier) Shoot()
            {
                Projectile.ClearAll(); client.ResetEffects(); client.UpdateBuffs(Actor); sent.Clear();
                var item = client.inventory[client.selectedItem];
                client.ItemCheck_Shoot(Actor, item, client.GetWeaponDamage(item), withAudioVisualFeedback: false);
                var arrow = Main.projectile.Single(projectile => projectile.active && projectile.type == ProjectileID.WoodenArrowFriendly);
                return (arrow.damage, Convert.ToHexString(sent.Single(frame => frame[2] == 27)), client.bowEffectiveDamage);
            }
            var baseline = Shoot(); Assert.That(baseline.Damage, Is.EqualTo(9));

            Main.netMode = 2; Main.myPlayer = 255; Main.player[Actor] = server;
            server.AddBuff(BuffID.Archery, 3600); server.AddBuff(BuffID.Wrath, 3600); sent.Clear();
            NetMessage.SendData(50, Actor, -1, number: Actor);
            byte[] naturalBuffs = sent.Single(frame => frame[2] == 50);
            Main.netMode = 1; Main.myPlayer = Actor; Main.player[Actor] = client;
            Receive(naturalBuffs[2..], 256); var buffed = Shoot();
            Assert.That(buffed.Damage, Is.GreaterThan(baseline.Damage));
            Assert.That(client.FindBuffIndex(BuffID.Archery), Is.GreaterThanOrEqualTo(0));
            Assert.That(client.FindBuffIndex(BuffID.Wrath), Is.GreaterThanOrEqualTo(0));

            // Preserve the first experiment's legal stacking result. This runtime allows
            // same-type bows to stack; a pickup into an existing canonical stack keeps4.
            Main.netMode = 2; Main.myPlayer = 255; Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
            Main.item[7].inner.damage = 1000; sent.Clear();
            NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
            byte[] stackedExport = sent.Single(frame => frame[2] == 88);
            Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
            Main.netMode = 1; Main.myPlayer = Actor; Receive(stackedExport[2..], 256);
            Assert.That(client.GetItem(Main.item[7].inner, new GetItemSettings(NoText: true, NoSound: true)).IsAir, Is.True);
            Assert.That(client.inventory[0].damage, Is.EqualTo(4));
            Assert.That(client.inventory[0].stack, Is.EqualTo(2));
            var stackedResult = Shoot(); Assert.That(stackedResult.Damage, Is.EqualTo(buffed.Damage));

            // The earlier canonical slot stream does not rule out a later authorized-host88.
            // Preserve the legal M7 customization mechanism on a different bow type, so native same-type stacking does not merge it into the already-held canonical bow. This is not a natural drop claim.
            Main.netMode = 2; Main.myPlayer = 255; Main.item[7].inner.SetDefaults(ItemID.GoldBow);
            Main.item[7].inner.damage = 1000; sent.Clear();
            NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
            byte[] itemExport = sent.Single(frame => frame[2] == 88);
            Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.GoldBow);
            Main.netMode = 1; Main.myPlayer = Actor; Receive(itemExport[2..], 256);
            Assert.That(client.GetItem(Main.item[7].inner, new GetItemSettings(NoText: true, NoSound: true)).IsAir, Is.True);
            TestContext.Out.WriteLine("EXPORT=" + Convert.ToHexString(itemExport) + "; inventory=" + string.Join(";", client.inventory.Select((item,index) => $"{index}:{item.type}:{item.damage}:{item.stack}:{item.maxStack}").Where(value => !value.Contains(":0:0:0:")))); int customSlot = Array.FindIndex(client.inventory, item => item.type == ItemID.GoldBow && item.damage == 1000);
            Assert.That(customSlot, Is.GreaterThanOrEqualTo(0)); client.selectedItemState.Select(customSlot); client.selectedItemState.Update(); Assert.That(client.selectedItem, Is.EqualTo(customSlot));
            var customAndBuffed = Shoot(); Assert.That(customAndBuffed.Damage, Is.GreaterThan(1005));

            // Direct valid host50 arrays preserve duplicates, unlike AddBuff's normalization.
            // This is a distinct authorized-host array export, not a stock potion operation.
            Main.netMode = 2; Main.myPlayer = 255; Main.player[Actor] = server;
            Array.Clear(server.buffType); Array.Clear(server.buffTime);
            server.buffType[0] = server.buffType[1] = BuffID.Archery;
            server.buffTime[0] = server.buffTime[1] = 3600; sent.Clear();
            NetMessage.SendData(50, Actor, -1, number: Actor);
            byte[] duplicateBuffs = sent.Single(frame => frame[2] == 50);
            Main.netMode = 1; Main.myPlayer = Actor; Main.player[Actor] = client;
            Receive(duplicateBuffs[2..], 256); var duplicateResult = Shoot();
            Assert.That(client.buffType.Count(buff => buff == BuffID.Archery), Is.EqualTo(2));
            Assert.That(duplicateResult.Multiplier, Is.EqualTo(1.21f).Within(0.00001f));

            Save("m18-current-session-exports.json", new
            {
                source = "actual SendData5 -> native client GetData5 -> native shot; actual AddBuff -> SendData50 -> GetData50 -> UpdateBuffs -> shot; authorized-host88 -> GetData88 -> native GetItem -> shot",
                baseline = new { baseline.Damage, baseline.Declaration, baseline.Multiplier },
                naturalBuffs = Convert.ToHexString(naturalBuffs),
                buffed = new { buffed.Damage, buffed.Declaration, buffed.Multiplier },
                stackedExport = Convert.ToHexString(stackedExport),
                stackedResult = new { stackedResult.Damage, stackedResult.Declaration, stackedResult.Multiplier, resultingWeaponDamage = 4, resultingStack = 2 },
                itemExport = Convert.ToHexString(itemExport), customSlot,
                customAndBuffed = new { customAndBuffed.Damage, customAndBuffed.Declaration, customAndBuffed.Multiplier },
                duplicateBuffs = Convert.ToHexString(duplicateBuffs),
                duplicateResult = new { duplicateResult.Damage, duplicateResult.Declaration, duplicateResult.Multiplier },
                conclusion = "A consumed canonical slot reset does not freeze later legal inputs. Current-session recipient88 and exact50 slot order/multiplicity remain necessary inputs; their disappearance or TTL is not proof that their effects disappeared.",
                limits = "Native method fixture and exact native serializers/receivers only. No full handshake or real TCP, no claim of stock natural1000 bow/duplicate-potion origin, no production completeness or sanction changes."
            });
        }
        finally { Main.player[Actor] = client; Main.ServerSideCharacter = oldSsc; }
    }
}



