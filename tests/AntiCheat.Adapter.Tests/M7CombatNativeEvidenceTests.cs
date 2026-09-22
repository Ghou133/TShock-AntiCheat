using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

/// <summary>Native method experiments only. No GUI, synthetic frame is labelled a natural player action,
/// or client-side attack-source observation is claimed to exist on the server.</summary>
[TestFixture, NonParallelizable]
public sealed partial class M7CombatNativeEvidenceTests
{
    private const int Actor = 7, TargetSlot = 11;
    private Player[] oldPlayers = null!;
    private NPC[] oldNpcs = null!;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldProjectileMap = null!;
    private int[] oldProjectileGenerations = null!;
    private CombatText[] oldText = null!;
    private Dust[] oldDust = null!;
    private WorldItem oldItem = null!;
    private RemoteClient oldClient = null!;
    private RemoteServer oldConnection = null!;
    private MessageBuffer oldBuffer = null!;
    private MessageBuffer oldActorBuffer = null!;
    private int oldMode, oldLocal;
    private bool oldDedicated;
    private readonly List<byte[]> sent = [];

    [SetUp]
    public void SetUp()
    {
        oldPlayers = Main.player; oldNpcs = Main.npc; oldProjectiles = Main.projectile;
        oldProjectileMap = Projectile.keyToIndex; oldProjectileGenerations = Projectile.slotGenerations;
        oldText = Main.combatText; oldDust = Main.dust; oldItem = Main.item[7];
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldDedicated = Main.dedServ;
        oldClient = Netplay.Clients[Actor]; oldConnection = Netplay.Connection; oldBuffer = NetMessage.buffer[256];
        oldActorBuffer = NetMessage.buffer[Actor];
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
        Main.player[Actor].active = true; Main.player[Actor].position = new(400, 400); Main.player[Actor].direction = 1;
        Main.npc = Enumerable.Range(0, Main.maxNPCs).Select(i => new NPC { whoAmI = i }).ToArray();
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001]; Projectile.slotGenerations = new int[1001];
        Main.combatText = Enumerable.Range(0, 100).Select(_ => new CombatText { active = true }).ToArray();
        Main.dust = Enumerable.Range(0, 6001).Select(_ => new Dust { active = true }).ToArray();
        Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
        Main.netMode = 1; Main.myPlayer = Actor; Main.dedServ = true;
        Netplay.Clients[Actor] = new RemoteClient { State = 10, Socket = new Terraria.Net.Sockets.TcpSocket() };
        NetMessage.buffer[Actor] = new MessageBuffer();
        Netplay.Connection = new RemoteServer { PendingTermination = true }; NetMessage.buffer[256] = new MessageBuffer();
        HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
        HookEvents.Terraria.NetMessage.SendPacketToServer += Suppress;
        ResetTarget(); sent.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture;
        HookEvents.Terraria.NetMessage.SendPacketToServer -= Suppress;
        Main.player = oldPlayers; Main.npc = oldNpcs; Main.projectile = oldProjectiles;
        Projectile.keyToIndex = oldProjectileMap; Projectile.slotGenerations = oldProjectileGenerations;
        Main.combatText = oldText; Main.dust = oldDust; Main.item[7] = oldItem;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.dedServ = oldDedicated;
        Netplay.Clients[Actor].Socket.Close();
        Netplay.Clients[Actor] = oldClient; Netplay.Connection = oldConnection; NetMessage.buffer[256] = oldBuffer;
        NetMessage.buffer[Actor] = oldActorBuffer;
    }

    private void Capture(object? sender, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args) => sent.Add(args.ms.ToArray());
    private static void Suppress(object? sender, HookEvents.Terraria.NetMessage.SendPacketToServerEventArgs args) => args.ContinueExecution = false;
    private NPC ResetTarget()
    {
        var npc = new NPC { whoAmI = TargetSlot }; npc.SetDefaults(NPCID.BlueSlime);
        npc.active = true; npc.generation = 3; npc.life = npc.lifeMax = 5000; npc.defense = 0;
        npc.position = new(400, 400); npc.noTileCollide = true; npc.takenDamageMultiplier = 1; npc.knockBackResist = 0;
        Main.npc[TargetSlot] = npc; return npc;
    }
    private static void Receive(byte[] frame, int sender)
    {
        var buffer = new MessageBuffer { whoAmI = sender }; frame.CopyTo(buffer.readBuffer, 0); buffer.ResetReader();
        buffer.GetData(0, frame.Length, out _);
    }

    [Test]
    public void Item88IsClientOnlyCustomizationAndClientRequestsNeverAuthorizeOrRelayIt()
    {
        byte[] tweak = [88, 7, 0, 2, 255, 255];
        int canonical = Main.item[7].inner.damage;
        Main.netMode = 2; Main.myPlayer = 255;
        Receive(tweak, Actor);
        Assert.That(Main.item[7].inner.damage, Is.EqualTo(canonical)); Assert.That(sent, Is.Empty);
        Main.netMode = 1; Main.myPlayer = Actor;
        Receive(tweak, 256);
        Assert.That(Main.item[7].inner.damage, Is.EqualTo(65535)); Assert.That(sent, Is.Empty);
        TestContext.Out.WriteLine("The identical manually injected88 is a server-side C2S no-op with no relay, and changes client item.damage4->65535 only in netMode1. M6 used manual server-direction injection, not an identified administrator/plugin authorization producer; the counterexample remains valid under that explicit customization assumption.");
    }

    [Test]
    public void ActualServer88SerializationThenNativePickupAndShootProducesFirst1005WithoutACheatCalculation()
    {
        // This is an explicit test-host customization, not an inferred authorization or a natural vanilla item.
        Main.netMode = 2; Main.myPlayer = 255; Main.item[7].inner.damage = 1000;
        NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
        byte[] export = sent.Single(x => x.Length > 3 && x[2] == 88);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(export.AsSpan(6)), Is.EqualTo(1000));
        Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
        Main.netMode = 1; Main.myPlayer = Actor; Receive(export[2..], 256);
        var player = Main.player[Actor];
        Assert.That(player.GetItem(Main.item[7].inner, new GetItemSettings(NoText: true, NoSound: true)).IsAir, Is.True);
        int bowSlot = Array.FindIndex(player.inventory, x => x.type == ItemID.WoodenBow);
        player.selectedItemState.Select(bowSlot);
        player.inventory[54].SetDefaults(ItemID.WoodenArrow); player.inventory[54].stack = 100;
        var bow = player.inventory[bowSlot]; Assert.That(bow.damage, Is.EqualTo(1000));
        sent.Clear();
        player.ItemCheck_Shoot(Actor, bow, player.GetWeaponDamage(bow), withAudioVisualFeedback: false);
        byte[] declaration = sent.Single(x => x.Length > 3 && x[2] == 27);
        Assert.That(M4CombatProjectileReader.TryRead(new(M2PacketKind.ProjectileNew, declaration[3..]), out var parsed), Is.True);
        Assert.That(parsed!.Damage, Is.EqualTo(1005));
        Assert.That(Main.projectile.Single(x => x.active && x.type == 1).damage, Is.EqualTo(1005));
        TestContext.Out.WriteLine($"Actual native server88 serializer={Convert.ToHexString(export)} -> client decoder -> GetItem -> ItemCheck_Shoot -> first27 damage1005, frame={Convert.ToHexString(declaration)}. Host customization is explicit in this experiment; the wire27 itself does not identify its authorization. This closes the concrete first1005 customization producer, not the canonical all-source upper bound.");
    }

    [Test]
    public void NativeChannelledBowCreatesFirstOrdinaryArrowsLaterUsingEffectsAtEmission()
    {
        var actor = Main.player[Actor]; var bow = actor.inventory[0]; bow.SetDefaults(ItemID.Phantasm);
        actor.inventory[54].SetDefaults(ItemID.WoodenArrow); actor.inventory[54].stack = 100;
        actor.channel = true; actor.controlUseItem = true;
        var sources = new List<string>();
        void Source(Projectile entity, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
        { if (entity.type == 1) sources.Add(args.spawnSource.GetType().Name); }
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += Source;
        try
        {
            actor.ItemCheck_Shoot(Actor, bow, actor.GetWeaponDamage(bow), withAudioVisualFeedback: false);
            var held = Main.projectile.Single(x => x.active && x.type == ProjectileID.Phantasm);
            int initialParentDamage = held.damage;
            Assert.That(Main.projectile.Any(x => x.active && x.type == 1), Is.False);
            Assert.That(sources, Is.Empty);
            // A real native buff producer runs between use and emission. No item.damage or damage coefficient is assigned.
            actor.AddBuff(BuffID.Wrath, 3600); actor.UpdateBuffs(Actor);
            sent.Clear(); held.AI_075();
            var arrows = Main.projectile.Where(x => x.active && x.type == 1).ToArray();
            Assert.That(arrows, Has.Length.EqualTo(4));
            Assert.That(arrows.All(x => x.damage > initialParentDamage), Is.True);
            Assert.That(sources, Has.Count.EqualTo(4)); Assert.That(sources.All(x => x.Contains("ItemUse_WithAmmo")), Is.True);
            Assert.That(sent.Count(x => x.Length > 3 && x[2] == 27), Is.EqualTo(4));
            TestContext.Out.WriteLine($"Native Phantasm ItemCheck_Shoot creates only parent type{held.type}/damage{initialParentDamage}; native AddBuff(Wrath)+UpdateBuffs then native AI_075 emits four first type1 declarations damage{arrows[0].damage}, source={sources[0]}, without another ItemCheck_Shoot. This is a native delayed-creation branch experiment, not GUI or an exclusive first-damage bound.");
        }
        finally { HookEvents.Terraria.Projectile.ApplyStatsFromSource -= Source; }
    }

    [TestCase(9)] [TestCase(65540)] [TestCase(131081)] [TestCase(int.MaxValue)]
    public void ActualReflectingNpcGateAllowsExactlyOneNativeReflectionForAnOrdinaryArrow(int internalDamage)
    {
        var npc = Main.npc[TargetSlot]; var arrow = Main.projectile[0];
        arrow.SetDefaults(1); arrow.active = true; arrow.whoAmI = 0; arrow.owner = Actor;
        arrow.position = npc.position; arrow.oldVelocity = new(3, 0); arrow.velocity = new(3, 0); arrow.damage = internalDamage;
        Assert.That(arrow.CanBeReflected(), Is.True);
        // The actual native caller checks CanBeReflected on every pass, rather than calling ReflectProjectile directly.
        npc.ReflectProjectiles(npc.Hitbox);
        Assert.That(arrow.damage, Is.EqualTo(internalDamage / 2 / 2));
        Assert.That(arrow.friendly, Is.False); Assert.That(arrow.hostile, Is.True); Assert.That(arrow.CanBeReflected(), Is.False);
        int afterFirst = arrow.damage;
        npc.ReflectProjectiles(npc.Hitbox); npc.ReflectProjectiles(npc.Hitbox);
        Assert.That(arrow.damage, Is.EqualTo(afterFirst));
        Assert.That(M7ArrowProjectionRules.IsReachable(unchecked((short)internalDamage), unchecked((short)afterFirst)), Is.True);
        TestContext.Out.WriteLine($"Native ReflectProjectiles internal{internalDamage}->{afterFirst}; two later native caller passes do not divide again; wire{unchecked((short)internalDamage)}->{unchecked((short)afterFirst)} is admitted.");
    }

    [Test]
    public void CancelledNativeReflectionCanBeRetriedButCannotCauseTwoSuccessfulDivisions()
    {
        var npc = Main.npc[TargetSlot]; var arrow = Main.projectile[0];
        arrow.SetDefaults(1); arrow.active = true; arrow.owner = Actor; arrow.whoAmI = 0;
        arrow.position = npc.position; arrow.oldVelocity = new(3, 0); arrow.damage = 65540;
        void Cancel(NPC sender, HookEvents.Terraria.NPC.ReflectProjectileEventArgs args) => args.ContinueExecution = false;
        HookEvents.Terraria.NPC.ReflectProjectile += Cancel;
        try { npc.ReflectProjectiles(npc.Hitbox); }
        finally { HookEvents.Terraria.NPC.ReflectProjectile -= Cancel; }
        Assert.That(arrow.damage, Is.EqualTo(65540)); Assert.That(arrow.CanBeReflected(), Is.True);
        npc.ReflectProjectiles(npc.Hitbox); Assert.That(arrow.damage, Is.EqualTo(16385));
        npc.ReflectProjectiles(npc.Hitbox); Assert.That(arrow.damage, Is.EqualTo(16385));
    }

    [Test]
    public void ActualNormalMelee28HasNoProjectileAndManualReplayHasIdenticalServerObservation()
    {
        var actor = Main.player[Actor]; var weapon = actor.inventory[0]; weapon.SetDefaults(ItemID.WoodenSword);
        var target = Main.npc[TargetSlot]; actor.itemAnimation = actor.itemAnimationMax = 20;
        actor.ItemCheck_MeleeHitNPCs(weapon, target.Hitbox, actor.GetWeaponDamage(weapon), 0);
        byte[] raw = sent.Single(x => x.Length > 3 && x[2] == 28);
        Assert.That(sent.Any(x => x.Length > 3 && x[2] == 27), Is.False);
        var parsed = M6NpcStrikeReader.ReadPayload(raw.AsSpan(3)).Packet!;
        Assert.That(parsed.Damage, Is.GreaterThan(0)); Assert.That(parsed.TargetSlot, Is.EqualTo(TargetSlot));
        Main.netMode = 2; Main.myPlayer = 255;
        var serverTarget = ResetTarget();
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        Assert.That(M6NpcStrikeReader.Evaluate(parsed, session, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Unknown));
        // Exact native bytes replayed on a separate server target: this is replay, never reported as GUI.
        int outgoing = 0;
        void ServerSink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        { if (args.msgType == 28) outgoing++; args.ContinueExecution = false; }
        HookEvents.Terraria.NetMessage.SendData += ServerSink;
        try
        {
            Receive(raw[2..], Actor);
            Assert.That(serverTarget.life, Is.LessThan(5000)); Assert.That(outgoing, Is.EqualTo(1));
            TestContext.Out.WriteLine($"Native WoodenSword produces28={Convert.ToHexString(raw)} with zero27; exact replay damages actual server target{TargetSlot}/generation3 life5000->{serverTarget.life}. Weapon/collision cause and its reachable damage are absent from this identical frame.");
        }
        finally { HookEvents.Terraria.NetMessage.SendData -= ServerSink; }
    }
}
