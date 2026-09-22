using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7CombatNativeEvidenceTests
{
    [Test]
    public void M9_ActualDamageOnly88LauncherAndAmmoCombineBeforeNativePickupAndShot()
    {
        var oldAmmo = Main.item[8];
        try
        {
            Main.netMode = 2; Main.myPlayer = 255;
            var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
            var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true,
                Account = new UserAccount { ID = 37 } };
            using var context = new M6ArrowCandidateContexts(TargetRuntime.Fingerprint);
            context.Install(); context.Connected(session);
            context.Tick(1, slot => slot == Actor ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null), true);
            // Explicit host customization is serialized on the existing real88 writer.
            Main.item[7].inner.damage = 1200;
            Main.item[8] = new WorldItem(); Main.item[8].inner.SetDefaults(ItemID.WoodenArrow); Main.item[8].inner.damage = 700;
            NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
            NetMessage.SendData(88, Actor, -1, number: 8, number2: 2);
            var exports = sent.Where(x => x[2] == 88).Select(x => x.ToArray()).ToArray();
            Assert.That(exports, Has.Length.EqualTo(2));
            Assert.That(exports.All(x => x[5] == 2), Is.True, "Only damage flags are present; ammo classification cannot require a repeated ammo field.");
            byte[] payload = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(payload, new ProjectileKey(Actor, 99, 1).bits);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(20), 1); payload[22] = 16;
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(23), 1900);
            var envelope = context.Observe(new(M2PacketKind.ProjectileNew, payload), session, actor)!;
            Assert.That(envelope.CombatExports, Has.Length.EqualTo(2));
            Assert.That(envelope.CombatExports.All(x => x.WireAmmo is null && x.WireUseAmmo is null), Is.True);
            Assert.That(envelope.DamageResults!.SupportedResults.IsFull, Is.False);
            Assert.That(envelope.DamageResults.SupportedResults.Contains(1900), Is.True,
                "Both real exported components must combine; either paired with canonical-only counterpart misses1900.");
            Assert.That(envelope.DamageResults.AllowedResults.IsFull, Is.True);
            Assert.That(envelope.DamageResults.CanExclude(1900), Is.False);
            Assert.That(envelope.DamageResults.MissingPremises,
                Does.Contain("pre-observation-item-and-effect-history-not-established"));
            context.Dispose();

            // Consume those exact serialized exports with native decoder, pickup and shooting.
            Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
            Main.item[8] = new WorldItem(); Main.item[8].inner.SetDefaults(ItemID.WoodenArrow);
            Main.netMode = 1; Main.myPlayer = Actor;
            foreach (var export in exports) Receive(export[2..], 256);
            var player = Main.player[Actor];
            Assert.That(player.GetItem(Main.item[7].inner, new GetItemSettings(NoText: true, NoSound: true)).IsAir, Is.True);
            Assert.That(player.GetItem(Main.item[8].inner, new GetItemSettings(NoText: true, NoSound: true)).IsAir, Is.True);
            int bowSlot = Array.FindIndex(player.inventory, x => x.type == ItemID.WoodenBow);
            player.selectedItemState.Select(bowSlot);
            sent.Clear();
            var bow = player.inventory[bowSlot];
            player.ItemCheck_Shoot(Actor, bow, player.GetWeaponDamage(bow), withAudioVisualFeedback: false);
            var declaration = sent.Single(x => x[2] == 27);
            Assert.That(M4CombatProjectileReader.TryRead(new(M2PacketKind.ProjectileNew, declaration[3..]), out var shot), Is.True);
            Assert.That(shot!.Damage, Is.EqualTo(1900));
            Assert.That(envelope.DamageResults.SupportedResults.Contains(shot.Damage), Is.True);
            TestContext.Out.WriteLine($"Actual damage-only88 host bow1200 and ammo700 -> native client decoding/GetItem/ItemCheck_Shoot -> first27={shot.Damage}; previously missing simultaneous component branch is now supported. Overall allowed domain remains full-int16 because import/effect/delayed histories remain unknown. Frame={Convert.ToHexString(declaration)}");
        }
        finally { Main.item[8] = oldAmmo; }
    }
}
