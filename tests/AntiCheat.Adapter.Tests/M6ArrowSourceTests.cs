using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Adapter.Tests;

/// <summary>Exercise real creation and modifier producers, rather than giving a fixture a sourceComplete flag.</summary>
[TestFixture, NonParallelizable]
public sealed class M6ArrowSourceTests
{
    private const int Slot = 7;
    private Player oldPlayer = null!;
    private NPC[] oldNpcs = null!;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private int[] oldGenerations = null!;
    private RemoteClient oldClient = null!;
    private int oldMode, oldMyPlayer, oldMouseX, oldMouseY;
    private bool oldDedicated;
    private Vector2 oldScreen;
    private readonly List<(uint Key, int Type, int Damage, Vector2 Position, Vector2 Velocity)> declarations = [];
    private readonly List<string> sources = [];

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldNpcs = Main.npc; oldProjectiles = Main.projectile; oldMap = Projectile.keyToIndex;
        oldGenerations = Projectile.slotGenerations; oldClient = Netplay.Clients[Slot];
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ;
        oldMouseX = Main.mouseX; oldMouseY = Main.mouseY; oldScreen = Main.screenPosition;
        Main.netMode = 1; Main.myPlayer = Slot; Main.dedServ = true;
        Main.mouseX = 800; Main.mouseY = 400; Main.screenPosition = Vector2.Zero;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, position = new(320, 320), direction = 1 };
        Main.npc = Enumerable.Range(0, Main.maxNPCs).Select(i => new NPC { whoAmI = i }).ToArray();
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001]; Projectile.slotGenerations = new int[1001];
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += Source;
        HookEvents.Terraria.NetMessage.SendData += Send;
        declarations.Clear(); sources.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.Projectile.ApplyStatsFromSource -= Source;
        HookEvents.Terraria.NetMessage.SendData -= Send;
        Main.player[Slot] = oldPlayer; Main.npc = oldNpcs; Main.projectile = oldProjectiles; Projectile.keyToIndex = oldMap;
        Projectile.slotGenerations = oldGenerations; Netplay.Clients[Slot] = oldClient;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Main.mouseX = oldMouseX; Main.mouseY = oldMouseY; Main.screenPosition = oldScreen;
    }

    private void Source(Projectile projectile, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
    {
        if (projectile.type == 1) sources.Add(args.spawnSource.GetType().FullName!);
    }
    private void Send(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.msgType == 27)
        {
            var p = Main.projectile[args.number];
            declarations.Add((p.key.bits, p.type, p.damage, p.position, p.velocity));
        }
        args.ContinueExecution = false;
    }

    [Test]
    public void ActualArmorPrefixAccessoryAndBuffProducersChangeFirstArrowWithoutManualDamageCoefficients()
    {
        var player = Main.player[Slot]; var bow = player.inventory[0];
        bow.SetDefaults(ItemID.WoodenBow); bow.Prefix(PrefixID.Demonic);
        player.inventory[54].SetDefaults(ItemID.WoodenArrow); player.inventory[54].stack = 100;
        // Canonical existing item data and actual target effect methods, never rangedDamage = guessed constant.
        player.armor[0].SetDefaults(1546); player.GrantArmorBenefits(player.armor[0]);
        player.armor[1].SetDefaults(1549); player.GrantArmorBenefits(player.armor[1]);
        player.armor[2].SetDefaults(1550); player.GrantArmorBenefits(player.armor[2]);
        player.armor[3].SetDefaults(ItemID.RangerEmblem); player.armor[3].Prefix(PrefixID.Menacing);
        player.ApplyEquipFunctional(3, player.armor[3]); player.GrantPrefixBenefits(player.armor[3]);
        player.buffType[0] = BuffID.Archery; player.buffTime[0] = 3600;
        player.buffType[1] = BuffID.Wrath; player.buffTime[1] = 3600;
        player.UpdateBuffs(Slot);
        player.ItemCheck_Shoot(Slot, bow, player.GetWeaponDamage(bow), withAudioVisualFeedback: false);
        var declaration = declarations.Single(x => x.Type == ProjectileID.WoodenArrowFriendly);
        Assert.That(declaration.Damage, Is.GreaterThan(9));
        Assert.That(sources.Single(), Does.Contain("ItemUse_WithAmmo"));
        Assert.That(((ProjectileKey)declaration.Key).Spawner, Is.EqualTo(Slot));
        TestContext.Out.WriteLine($"Native first arrow key={declaration.Key}, damage={declaration.Damage}, source={sources.Single()}, armor=1546/1549/1550, accessory={player.armor[3].type}/{player.armor[3].prefix}, buffs=Archery/Wrath, ranged={player.rangedDamage}, arrow={player.arrowDamage}. This is a legal source example, not a global upper bound.");
    }

    [TestCase(false)] [TestCase(true)]
    public void ActualClientSourceIsAbsentFromServerReceiveEvenWhenCurrentHeldItemChanges(bool boosted)
    {
        var player = Main.player[Slot]; var bow = player.inventory[0]; bow.SetDefaults(ItemID.WoodenBow);
        player.inventory[54].SetDefaults(ItemID.WoodenArrow); player.inventory[54].stack = 100;
        if (boosted) bow.damage = 1000; // Controlled tool-like method input, never a legal example.
        player.ItemCheck_Shoot(Slot, bow, player.GetWeaponDamage(bow), withAudioVisualFeedback: false);
        var first = declarations.Single(x => x.Type == 1);
        Assert.That(first.Damage, Is.EqualTo(boosted ? 1005 : 9)); Assert.That(sources, Has.Count.EqualTo(1));
        // An independent server has only the wire declaration. It never sees the client IEntitySource.
        player.inventory[0].SetDefaults(ItemID.CopperShortsword);
        Main.netMode = 2; Main.myPlayer = 255;
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001]; sources.Clear(); declarations.Clear();
        byte[] payload = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(payload, first.Key);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), first.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8), first.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(12), first.Velocity.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(16), first.Velocity.Y);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(20), 1); payload[22] = 16;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(23), (short)first.Damage);
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = 27; payload.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, payload.Length + 1, out int packet);
        Assert.That(packet, Is.EqualTo(27)); Assert.That(sources, Is.Empty);
        Assert.That(declarations.Single().Damage, Is.EqualTo(first.Damage));
        Assert.That(Main.projectile.Single(p => p.active).damage, Is.EqualTo(first.Damage));
        TestContext.Out.WriteLine($"Actual client ItemCheck_Shoot -> source -> packet27 damage{first.Damage}; actual server GetData27 retained damage but produced zero ApplyStatsFromSource callbacks; current held item={player.inventory[0].type}.");
    }

    [Test]
    public void ActualOrdinaryArrowUpdateExportsOnlyTheLocalOwnerWithItsExistingKey()
    {
        var oldOther = Main.player[8];
        try
        {
            Main.player[8] = new Player { whoAmI = 8, active = true };
            foreach (int owner in new[] { Slot, 8 })
            {
                var key = new ProjectileKey(owner, 3, 1);
                var arrow = new Projectile(); arrow.SetDefaults(ProjectileID.WoodenArrowFriendly);
                arrow.whoAmI = 0; arrow.key = key; arrow.owner = owner; arrow.active = true;
                arrow.position = new(400, 400); arrow.velocity = new(1, 0); arrow.damage = 9;
                arrow.tileCollide = false; arrow.netUpdate2 = true; arrow.timeLeft = 500;
                Main.projectile[0] = arrow; Projectile.keyToIndex[owner, 3] = 0; declarations.Clear(); sources.Clear();
                arrow.Update(0);
                Assert.That(arrow.active, Is.True);
                Assert.That(declarations.Count, Is.EqualTo(owner == Slot ? 1 : 0));
                if (owner == Slot)
                {
                    Assert.That(declarations.Single().Key, Is.EqualTo(key.bits));
                    Assert.That(declarations.Single().Damage, Is.EqualTo(9));
                }
                Assert.That(sources, Is.Empty, "An ordinary update does not manufacture another item creation cause.");
            }
        }
        finally { Main.player[8] = oldOther; }
    }

    [Test]
    public void NativeItemPrefixAndEquipCatalogMeasuresPrimitiveEnvelopeWithoutAssumingSourceClosure()
    {
        int oldGameMode = Main.GameMode;
        var oldWingStats = ArmorIDs.Wing.Sets.Stats;
        var oldDust = Main.dust;
        var oldRandom = Main.rand;
        try
        {
            Main.GameMode = 2;
            Terraria.Initializers.WingStatsInitializer.Load();
            Lighting.Initialize();
            // Headless Dust.NewDust returns sentinel6000. The real GlassSlipper effect still
            // writes that object; prepare the native visual prerequisite and restore it below.
            Main.dust = Enumerable.Range(0, oldDust.Length).Select(_ => new Dust()).ToArray();
            Main.rand = new Terraria.Utilities.UnifiedRandom(35); // Native Next(60)==0 branch.
            Assert.DoesNotThrow(() => new Player { whoAmI = 254, position = new(320, 320) }.DoGlassSlipperSparkles());
            Main.rand = new Terraria.Utilities.UnifiedRandom(1704); // Owned deterministic catalog RNG.
            var armor = new (int Item, float Ranged, float Arrow)[3];
            for (int slot = 0; slot < 3; slot++) armor[slot] = (0, 1, 1);
            float accessoryRanged = 1, accessoryArrowAdd = 0; int accessoryRangedItem = 0, accessoryArrowItem = 0;
            int greatestWeapon = 0, weaponItem = 0, weaponPrefix = 0, greatestAmmo = 0;
            int armorCount = 0, accessoryCount = 0, weaponCount = 0;
            for (int type = 1; type < ItemID.Count; type++)
            {
                var item = new Item(); item.SetDefaults(type);
                for (int slot = 0; slot < 3; slot++)
                {
                    bool matchingSlot = slot switch { 0 => item.headSlot >= 0, 1 => item.bodySlot >= 0, _ => item.legSlot >= 0 };
                    if (!matchingSlot) continue;
                    var player = new Player { whoAmI = 254, position = new(320, 320) };
                    player.GrantArmorBenefits(item); armorCount++;
                    armor[slot] = (player.rangedDamage > armor[slot].Ranged ? type : armor[slot].Item,
                        Math.Max(armor[slot].Ranged, player.rangedDamage), Math.Max(armor[slot].Arrow, player.arrowDamage));
                }
                if (item.accessory)
                {
                    var player = new Player { whoAmI = 254, position = new(320, 320) };
                    player.GrantArmorBenefits(item); player.ApplyEquipFunctional(3, item); accessoryCount++;
                    if (player.rangedDamage > accessoryRanged) { accessoryRanged = player.rangedDamage; accessoryRangedItem = type; }
                    if (player.arrowDamageAdditiveStack > accessoryArrowAdd) { accessoryArrowAdd = player.arrowDamageAdditiveStack; accessoryArrowItem = type; }
                }
                if (item.ammo == AmmoID.Arrow && item.shoot == ProjectileID.WoodenArrowFriendly)
                    greatestAmmo = Math.Max(greatestAmmo, item.damage);
                if (item.useAmmo != AmmoID.Arrow && item.shoot != ProjectileID.WoodenArrowFriendly) continue;
                weaponCount++;
                for (int prefix = 0; prefix < PrefixID.Count; prefix++)
                {
                    var prefixed = new Item(); prefixed.SetDefaults(type);
                    if (prefix > 0 && !prefixed.Prefix(prefix)) continue;
                    if (prefixed.damage > greatestWeapon)
                    {
                        greatestWeapon = prefixed.damage; weaponItem = type; weaponPrefix = prefix;
                    }
                }
            }
            Assert.That(weaponCount, Is.GreaterThan(0)); Assert.That(armorCount, Is.GreaterThan(0)); Assert.That(accessoryCount, Is.GreaterThan(0));
            string armorSummary = string.Join(';', armor.Select(x => $"item{x.Item}:ranged{x.Ranged}:arrow{x.Arrow}"));
            TestContext.Out.WriteLine($"Native canonical primitive maxima: prefixed arrow source weapon={weaponItem}/{weaponPrefix}/damage{greatestWeapon}, unmodified type1 ammo={greatestAmmo}; armor slot maxima={armorSummary}; accessory ranged={accessoryRanged} at {accessoryRangedItem}, arrow additive={accessoryArrowAdd} at {accessoryArrowItem}; enumerated weapons={weaponCount}, armor={armorCount}, accessories={accessoryCount}. Buff multiplicity, set/stealth effects, first-shot caller closure and exports remain separate requirements.");
        }
        finally
        {
            Main.GameMode = oldGameMode; ArmorIDs.Wing.Sets.Stats = oldWingStats;
            Main.dust = oldDust; Main.rand = oldRandom;
        }
    }

    [Test]
    public void NativeAuthorizedItem88AndReflectionDisproveWireDamageMonotonicityWithoutNoWrapPremise()
    {
        var oldItem = Main.item[7]; var oldDust = Main.dust;
        try
        {
            Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
            Main.dust = Enumerable.Range(0, oldDust.Length).Select(_ => new Dust()).ToArray();
            // A server-to-client customization, not a fabricated client damage declaration.
            byte[] modification = [88, 7, 0, 2, 255, 255];
            var buffer = new MessageBuffer { whoAmI = 256 }; modification.CopyTo(buffer.readBuffer, 0); buffer.ResetReader();
            buffer.GetData(0, modification.Length, out int packet);
            Assert.That(packet, Is.EqualTo(88)); Assert.That(Main.item[7].inner.damage, Is.EqualTo(65535));
            var player = Main.player[Slot];
            var remainder = player.GetItem(Main.item[7].inner, new GetItemSettings(NoText: true, NoSound: true));
            Assert.That(remainder.IsAir, Is.True);
            int bowSlot = Array.FindIndex(player.inventory, x => x.type == ItemID.WoodenBow);
            player.selectedItemState.Select(bowSlot); var bow = player.inventory[bowSlot];
            Assert.That(bow.damage, Is.EqualTo(65535), "Native pickup preserves the authorized customized field.");
            player.inventory[54].SetDefaults(ItemID.WoodenArrow); player.inventory[54].stack = 100;
            player.ItemCheck_Shoot(Slot, bow, player.GetWeaponDamage(bow), withAudioVisualFeedback: false);
            var arrow = Main.projectile.Single(x => x.active && x.type == 1);
            Assert.That(arrow.damage, Is.EqualTo(65540));
            var first = SerializeNativeArrow(arrow.whoAmI);
            arrow.position = new(400, 400); arrow.oldVelocity = new(3, 0);
            new NPC().ReflectProjectile(arrow);
            Assert.That(arrow.damage, Is.EqualTo(16385));
            var reflected = SerializeNativeArrow(arrow.whoAmI);
            Assert.That(first.Damage, Is.EqualTo(4)); Assert.That(reflected.Damage, Is.EqualTo(16385));
            Assert.That(reflected.Damage, Is.GreaterThan(first.Damage));
            TestContext.Out.WriteLine("Actual target client GetData88 damage65535 -> native GetItem -> ItemCheck_Shoot internal65540 -> native27 wire4; native NPC.ReflectProjectile reduces internal to16385 -> native27 wire16385. This is an authorized-server-customization counterexample, not a claim canonical vanilla equipment reaches65540. Initial nonnegative wire damage cannot independently certify no wrap.");
        }
        finally { Main.item[7] = oldItem; Main.dust = oldDust; }
    }

    private M4CombatProjectile SerializeNativeArrow(int index)
    {
        var priorConnection = Netplay.Connection; var priorBuffer = NetMessage.buffer[256];
        byte[]? captured = null;
        void Capture(object? _, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args)
        { if (args.msgType == 27) captured = args.ms.ToArray(); }
        void Suppress(object? _, HookEvents.Terraria.NetMessage.SendPacketToServerEventArgs args) => args.ContinueExecution = false;
        HookEvents.Terraria.NetMessage.SendData -= Send;
        try
        {
            Netplay.Connection = new RemoteServer { PendingTermination = true }; NetMessage.buffer[256] = new MessageBuffer();
            HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
            HookEvents.Terraria.NetMessage.SendPacketToServer += Suppress;
            NetMessage.SendData(27, number: index);
            Assert.That(captured, Is.Not.Null);
            Assert.That(M4CombatProjectileReader.TryRead(new(M2PacketKind.ProjectileNew, captured![3..]), out var parsed), Is.True);
            return parsed!;
        }
        finally
        {
            HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture;
            HookEvents.Terraria.NetMessage.SendPacketToServer -= Suppress;
            HookEvents.Terraria.NetMessage.SendData += Send;
            Netplay.Connection = priorConnection; NetMessage.buffer[256] = priorBuffer;
        }
    }
}
