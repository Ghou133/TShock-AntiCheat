using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

/// <summary>Real locked client methods produce the packet role; these are native-method tests, not GUI input.</summary>
[TestFixture, NonParallelizable]
public sealed class M6VitalsNativeProducerTests
{
    private const int Actor = 7, Victim = 8;
    private Player[] oldPlayers = null!;
    private NPC[] oldNpcs = null!;
    private Projectile[] oldProjectiles = null!;
    private CombatText[] oldCombatText = null!;
    private Dust[] oldDust = null!;
    private Terraria.Graphics.Light.ILightingEngine? oldLighting;
    private int oldMode, oldLocal, oldWidth, oldHeight;
    private bool oldDedServ;
    private readonly List<(int X, int Y, ITile? Tile)> tiles = [];
    private readonly List<byte[]> produced = [];

    [SetUp]
    public void SetUp()
    {
        oldPlayers = Main.player; oldNpcs = Main.npc; oldProjectiles = Main.projectile;
        oldCombatText = Main.combatText; oldDust = Main.dust; oldLighting = Lighting._activeEngine;
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldDedServ = Main.dedServ;
        oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY;
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
        Main.npc = Enumerable.Range(0, 200).Select(_ => new NPC()).ToArray();
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        // Saturated visual pools avoid a graphics/font device in the native headless client fixture.
        // Attack, Hurt and SendPlayerHurt run unchanged; the fixture does not assert rendered output.
        Main.combatText = Enumerable.Range(0, 100).Select(_ => new CombatText { active = true }).ToArray();
        Main.dust = Enumerable.Range(0, 6001).Select(_ => new Dust { active = true }).ToArray();
        Lighting._activeEngine = new Terraria.Graphics.Light.LightingEngine();
        Main.netMode = 1; Main.myPlayer = Actor; Main.dedServ = true;
        Main.maxTilesX = 500; Main.maxTilesY = 500;
        foreach (int slot in new[] { Actor, Victim })
        {
            var player = Main.player[slot];
            player.active = true; player.hostile = true; player.direction = 1;
            player.position = new(1600, 1600); player.statLife = player.statLifeMax = player.statLifeMax2 = 100;
        }
        for (int x = 96; x <= 106; x++) for (int y = 96; y <= 106; y++)
        {
            tiles.Add((x, y, Main.tile[x, y])); Main.tile[x, y] = new Tile();
        }
        produced.Clear();
        HookEvents.Terraria.NetMessage.SendPlayerHurt += Capture;
        HookEvents.Terraria.NetMessage.SendData += TransportSink;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendPlayerHurt -= Capture;
        HookEvents.Terraria.NetMessage.SendData -= TransportSink;
        foreach (var tile in tiles) Main.tile[tile.X, tile.Y] = tile.Tile!;
        tiles.Clear();
        Main.player = oldPlayers; Main.npc = oldNpcs; Main.projectile = oldProjectiles;
        Main.combatText = oldCombatText; Main.dust = oldDust; Lighting._activeEngine = oldLighting!;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.dedServ = oldDedServ;
        Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
    }

    private void Capture(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)args.playerTargetIndex); args.reason.WriteSelfTo(writer);
        writer.Write((short)args.damage); writer.Write((byte)(args.direction + 1));
        writer.Write((byte)((args.critical ? 1 : 0) | (args.pvp ? 2 : 0))); writer.Write((sbyte)args.hitContext);
        produced.Add(stream.ToArray());
        args.ContinueExecution = false; // Observe native output without opening sockets.
    }

    private static void TransportSink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) =>
        args.ContinueExecution = false;

    [TestCase("melee")]
    [TestCase("wooden-arrow")]
    [TestCase("inferno-buff")]
    public void ActualCrossPlayerAttackProducersDamageTheirTargetAndAlwaysDeclarePvp(string mechanism)
    {
        var actor = Main.player[Actor]; var victim = Main.player[Victim];
        if (mechanism == "melee")
        {
            actor.inventory[0].SetDefaults(ItemID.WoodenSword);
            actor.ItemCheck_MeleeHitPVP(actor.inventory[0], victim.Hitbox, 9, 0);
        }
        else if (mechanism == "wooden-arrow")
        {
            var arrow = Main.projectile[0]; arrow.SetDefaults(ProjectileID.WoodenArrowFriendly);
            arrow.active = true; arrow.whoAmI = 0; arrow.owner = Actor; arrow.position = victim.position; arrow.damage = 9;
            arrow.Damage_PVP(victim.Hitbox, 1);
        }
        else
        {
            actor.buffType[0] = BuffID.Inferno; actor.buffTime[0] = 120; actor.infernoCounter = 0;
            actor.UpdateBuffs(Actor);
        }
        Assert.That(victim.statLife, Is.LessThan(100), "The real producer must reach a real hurt, not an empty loop.");
        Assert.That(produced, Is.Not.Empty);
        foreach (byte[] body in produced)
        {
            var parsed = M4VitalPacketReader.ReadPayload(117, body);
            Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
            Assert.That(parsed.Packet!.ClaimedSlot, Is.EqualTo(Victim));
            Assert.That(parsed.Packet.Pvp, Is.True);
            AssertNativeRole(parsed.Packet);
            TestContext.Out.WriteLine($"{mechanism}: actual native SendPlayerHurt; victimLife={victim.statLife}; body={Convert.ToHexString(body)}");
        }
    }

    [TestCase(0)]
    [TestCase(20)]
    public void ActualEnvironmentalAndSharedProtectionSelfHurtStaysNonPvp(int source)
    {
        Main.player[Actor].hostile = false;
        Main.player[Actor].Hurt(PlayerDeathReason.ByOther(source), 20, 0, dodgeable: false);
        Assert.That(Main.player[Actor].statLife, Is.LessThan(100));
        Assert.That(produced, Has.Count.EqualTo(1));
        var parsed = M4VitalPacketReader.ReadPayload(117, produced.Single());
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(parsed.Packet!.ClaimedSlot, Is.EqualTo(Actor));
        Assert.That(parsed.Packet.Pvp, Is.False);
        AssertNativeRole(parsed.Packet);
    }

    private static void AssertNativeRole(M4VitalObservation observation)
    {
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var input = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true);
        Assert.That(M5VitalsRules.EvaluateHurt(observation, input, true, false).Verdict, Is.EqualTo(Verdict.Pass));
    }
}
