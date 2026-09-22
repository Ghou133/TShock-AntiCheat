using System.Buffers.Binary;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M17CombatNativeProducerTests
{
    [Test]
    public void M17_ActualMeleeBuffAndRandomProducerOutputsRemainLegalAcrossCorePhaseBoundary()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var core = M16PrepareCore(true);
        Main.netMode = 1; Main.myPlayer = Actor;
        var player = Main.player[Actor]; var sword = player.inventory[0];
        sword.SetDefaults(ItemID.TitaniumSword);
        player.ResetEffects();
        int baselineDamage = player.GetWeaponDamage(sword), baselineCrit = player.GetWeaponCrit(sword);
        player.AddBuff(BuffID.Wrath, 3600); player.AddBuff(BuffID.Rage, 3600);
        player.UpdateBuffs(Actor);
        int nativeBuffDamage = player.GetWeaponDamage(sword), nativeBuffCrit = player.GetWeaponCrit(sword);
        Assert.That(nativeBuffDamage, Is.GreaterThan(baselineDamage));
        Assert.That(nativeBuffCrit, Is.GreaterThan(baselineCrit));
        Assert.That(player.FindBuffIndex(BuffID.Wrath) >= 0 && player.FindBuffIndex(BuffID.Rage) >= 0, Is.True);
        var outputs = new List<byte[]>();
        // Independent native method actions exercise ordinary native RNG; this is not a fire-rate proof.
        for (int index = 0; index < 64; index++)
        {
            sent.Clear(); int before = core.life;
            player.ProcessHitAgainstNPC(sword, core.Hitbox, nativeBuffDamage, player.GetWeaponKnockback(sword, sword.knockBack), TargetSlot);
            byte[] frame = sent.Single(x => x.Length > 3 && x[2] == 28);
            Assert.That(core.life, Is.LessThan(before));
            outputs.Add(frame);
        }
        var distinctAmounts = outputs.Select(Damage).Distinct().Order().ToArray();
        Assert.That(distinctAmounts.Length, Is.GreaterThan(2), "DamageVar actually produced varied results.");
        Assert.That(outputs.Any(Critical) && outputs.Any(x => !Critical(x)), Is.True, "Native crit RNG produced both outcomes.");
        byte[][] selected = [outputs.MinBy(Damage)!, outputs.MaxBy(Damage)!, outputs.First(Critical)];
        var evidence = selected.Select((frame, i) => Witness("native-buffed-melee-" + i, frame, new
        {
            item = sword.type, prefix = sword.prefix, buffs = new[] { BuffID.Wrath, BuffID.Rage },
            baselineDamage, nativeBuffDamage, baselineCrit, nativeBuffCrit,
            producer = "SetDefaults -> ResetEffects -> AddBuff(Wrath,Rage) -> UpdateBuffs -> GetWeaponDamage -> ProcessHitAgainstNPC -> DamageVar/native-crit -> StrikeNPC -> SendData28",
            randomSeed = 1704, independentNativeActions = outputs.Count, distinctAmounts
        })).ToArray();
        // The same legal producer has no hit/packet while the native core AI closes the target.
        Main.netMode = 2; Main.myPlayer = 255; core = M16PrepareCore(false);
        Main.netMode = 1; Main.myPlayer = Actor; int shieldLife = core.life;
        player.ProcessHitAgainstNPC(sword, core.Hitbox, nativeBuffDamage, player.GetWeaponKnockback(sword, sword.knockBack), TargetSlot);
        Assert.That(core.life, Is.EqualTo(shieldLife)); Assert.That(sent.Any(x => x.Length > 3 && x[2] == 28), Is.False);
        foreach (byte[] frame in selected) ReplayOpenClosedRecovery(frame);
        Save("native-melee-witnesses.json", evidence);
    }

    [Test]
    public void M17_ActualBeenadeParentKillProducesBeeChildHitWithoutInjectedDamage()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var core = M16PrepareCore(true);
        Main.netMode = 1; Main.myPlayer = Actor;
        var player = Main.player[Actor]; var item = player.inventory[0]; item.SetDefaults(ItemID.Beenade);
        player.ResetEffects();
        var sources = new List<(int Type, uint Key, string Source)>();
        void Source(Projectile projectile, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
            => sources.Add((projectile.type, projectile.key.bits, args.spawnSource.GetType().FullName!));
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += Source;
        byte[] frame; object chain;
        try
        {
            int weaponDamage = player.GetWeaponDamage(item);
            player.ItemCheck_Shoot(Actor, item, weaponDamage, withAudioVisualFeedback: false);
            var parent = Main.projectile.Single(p => p.active && p.type == ProjectileID.Beenade);
            int parentType = parent.type, parentDamage = parent.damage; uint parentKey = parent.key.bits;
            Assert.That(sources.Single(x => x.Key == parentKey).Source, Does.Contain("ItemUse"));
            // Only physical initial position is prepared. Native parent/child types and damage are untouched.
            parent.Center = core.Center; parent.Kill();
            var children = Main.projectile.Where(p => p.active && p.type == ProjectileID.Bee).ToArray();
            Assert.That(children.Length, Is.InRange(15, 24));
            Assert.That(children.All(p => p.owner == Actor), Is.True);
            Assert.That(children.All(p => sources.Single(x => x.Key == p.key.bits).Source.Contains("Parent")), Is.True);
            var child = children[0]; int childDamage = child.damage; uint childKey = child.key.bits;
            Assert.That(child.Hitbox.Intersects(core.Hitbox), Is.True);
            sent.Clear(); int before = core.life;
            child.Damage();
            frame = sent.Single(x => x.Length > 3 && x[2] == 28);
            Assert.That(core.life, Is.LessThan(before));
            chain = new
            {
                item = item.type, weaponDamage, parentType, parentDamage, parentKey,
                childType = child.type, childDamage, childKey, childCount = children.Length,
                sourceTypes = sources.Select(x => new { x.Type, x.Key, x.Source }).ToArray(),
                producer = "Beenade SetDefaults -> ItemCheck_Shoot -> NewProjectile(ItemUse) -> parent.Kill -> NewProjectile(EntitySource_Parent) -> beeDamage -> Bee.Damage -> Damage_PVE -> StrikeNPC -> SendData28",
                randomSeed = 1704, physicalSetup = "parent.Center = native core.Center before real Kill; no damage/type override"
            };
            Main.netMode = 2; Main.myPlayer = 255; core = M16PrepareCore(false);
            Main.netMode = 1; Main.myPlayer = Actor;
            var untouchedChild = children[1]; Assert.That(untouchedChild.active, Is.True);
            sent.Clear(); before = core.life; untouchedChild.Damage();
            Assert.That(core.life, Is.EqualTo(before)); Assert.That(sent.Any(x => x.Length > 3 && x[2] == 28), Is.False);
        }
        finally { HookEvents.Terraria.Projectile.ApplyStatsFromSource -= Source; }
        ReplayOpenClosedRecovery(frame);
        Save("native-child-witnesses.json", new[] { Witness("native-beenade-bee-child", frame, chain) });
    }

    private static short Damage(byte[] frame) => BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(5));
    private static bool Critical(byte[] frame) => frame[12] != 0;
    private static object Witness(string label, byte[] frame, object nativeSource) => new
    {
        label, source = "locked-native-method-producer-captured-wire", hex = Convert.ToHexString(frame),
        damage = Damage(frame), critical = Critical(frame), nativeSource,
        limits = "Native method fixture, not stock GUI or live TCP. The server does not receive the client IEntitySource. Future TCP replay changes target slot/generation only and retains result bytes."
    };

    private static void Save(string name, object witnesses)
    {
        string directory = Environment.GetEnvironmentVariable("ANTICHEAT_M17_NATIVE_WITNESS_OUTPUT") ??
            Path.Combine(TestContext.CurrentContext.TestDirectory, "m17-native-witnesses");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, target = "Terraria 1.4.5.8", evidenceTier = "locked-native-method-producer-and-receiver",
            pluginHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(M16CombatNpcImmunityGuard).Assembly.Location))),
            runtimeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(Main).Assembly.Location))),
            witnesses
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(path); TestContext.Out.WriteLine(path);
    }

    private void ReplayOpenClosedRecovery(byte[] nativeFrame)
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        var guard = new M16CombatNpcImmunityGuard(TargetRuntime.Fingerprint,
            slot => slot == Actor ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));
        guard.Tick(1); BusinessRuleResult? last = null; int strikes = 0, loot = 0;
        void Request(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor || args.Instance.readBuffer[0] != 28) return;
            var read = M6NpcStrikeReader.ReadPayload(args.Instance.readBuffer.AsSpan(1, 10));
            last = guard.Evaluate(read.Packet!, session, actor);
            if (last.Action == ControlAction.Block) args.Result = OTAPI.HookResult.Cancel;
        }
        void Strike(NPC _, HookEvents.Terraria.NPC.StrikeNPCEventArgs args) => strikes++;
        void Loot(NPC _, HookEvents.Terraria.NPC.NPCLootEventArgs args) => loot++;
        OTAPI.Hooks.MessageBuffer.GetData += Request; HookEvents.Terraria.NPC.StrikeNPC += Strike; HookEvents.Terraria.NPC.NPCLoot += Loot;
        try
        {
            foreach (bool open in new[] { true, false, true })
            {
                var core = M16PrepareCore(open); Array.Clear(core.playerInteraction); strikes = loot = 0; int before = core.life;
                Receive(nativeFrame.AsSpan(2).ToArray(), Actor);
                Assert.That(last!.PredicateSatisfied, Is.False);
                Assert.That(last.Action, Is.EqualTo(open ? ControlAction.Pass : ControlAction.Block));
                Assert.That(strikes, Is.EqualTo(open ? 1 : 0)); Assert.That(loot, Is.Zero);
                Assert.That(core.life < before, Is.EqualTo(open));
                Assert.That(core.justHit, Is.EqualTo(open));
                Assert.That(sent.Any(x => x.Length > 3 && x[2] == 28), Is.EqualTo(open));
                if (!open) Assert.That(core.playerInteraction[Actor], Is.False);
            }
        }
        finally
        {
            OTAPI.Hooks.MessageBuffer.GetData -= Request; HookEvents.Terraria.NPC.StrikeNPC -= Strike; HookEvents.Terraria.NPC.NPCLoot -= Loot;
        }
    }
}
