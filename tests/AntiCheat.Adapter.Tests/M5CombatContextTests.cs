using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M5CombatContextTests
{
    private const int Slot = 7;
    private M5CombatContexts contexts = null!;
    private TSPlayer actor = null!;
    private SessionKey session;
    private Player? oldPlayer, oldServerPlayer;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private bool[] oldHostile = null!;
    private NPC[] oldNpcs = null!;
    private bool oldDedServ;
    private int oldMode, oldWidth, oldMyPlayer;
    private RemoteClient? oldClient;
    private readonly List<(int Type, int Entity, int Damage)> sent = [];

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldServerPlayer = Main.player[255]; oldProjectiles = Main.projectile;
        oldMap = Projectile.keyToIndex; oldHostile = Main.projHostile;
        oldNpcs = Main.npc; oldDedServ = Main.dedServ;
        oldMode = Main.netMode; oldWidth = Main.maxTilesX; oldMyPlayer = Main.myPlayer;
        oldClient = Netplay.Clients[Slot];
        Main.netMode = 2; Main.maxTilesX = 4200; Main.myPlayer = 255;
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.player[255] = new Player { whoAmI = 255 };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Main.npc = Enumerable.Range(0, 200).Select(i => new NPC { whoAmI = i }).ToArray(); Main.dedServ = true;
        Projectile.keyToIndex = new int[256, 1001];
        Main.projHostile = new bool[ProjectileID.Count];
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Account = new UserAccount { ID = 37, Name = "m5-combat-fixture" } };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        contexts = new M5CombatContexts(TargetRuntime.Fingerprint);
        contexts.Install(); Tick();
        HookEvents.Terraria.NetMessage.SendData += Sink;
        sent.Clear();
        Assert.That(contexts.ContractHealthy, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts?.Dispose(); Main.player[Slot] = oldPlayer!; Main.player[255] = oldServerPlayer!;
        Main.projectile = oldProjectiles; Projectile.keyToIndex = oldMap; Main.projHostile = oldHostile;
        Main.npc = oldNpcs; Main.dedServ = oldDedServ;
        Main.netMode = oldMode; Main.maxTilesX = oldWidth; Main.myPlayer = oldMyPlayer;
        Netplay.Clients[Slot] = oldClient!;
    }

    private void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        sent.Add((args.msgType, args.number, args.msgType == 27 ? Main.projectile[args.number].damage : 0));
        args.ContinueExecution = false; // Record the real post-write export call without using a socket.
    }
    private void Tick() => contexts.Tick(session.WorldEpoch, slot => slot == Slot
        ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));
    private static M2Packet Shot(short damage = 9, int index = 3, int generation = 1, int type = 1, int spawner = Slot)
    {
        var body = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(spawner, index, generation).bits);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), (short)type); body[22] = 16;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(23), damage);
        return M2PacketReader.Read(M2ContractsTests.Packet(PacketTypes.ProjectileNew, body, Slot), true).Packet!;
    }
    private BusinessRuleResult Evaluate(M2Packet packet, bool cancelled = false) => contexts.Evaluate(packet, session, actor, cancelled).Single();
    private void VanillaAccept(M2Packet packet)
    {
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = 27; packet.Payload.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, packet.Payload.Length + 1, out int type);
        Assert.That(type, Is.EqualTo(27));
    }
    private Projectile Arrow(int identity = 3) => Main.projectile.Single(p => p.active && p.key == new ProjectileKey(Slot, identity, 1));
    private void Establish(short damage = 9, int index = 3)
    {
        Assert.That(Evaluate(Shot(damage, index)).Verdict, Is.EqualTo(Verdict.Unknown));
        VanillaAccept(Shot(damage, index));
        Assert.That(contexts.WitnessCount, Is.GreaterThan(0));
    }

    [Test]
    public void ActualMessageBufferWritesAndRelaysInitial1005WithoutPretendingThatItIsNpcHitDamage()
    {
        foreach (short damage in new short[] { 9, 1005 })
        {
            int index = damage == 9 ? 3 : 4;
            Assert.That(Evaluate(Shot(damage, index)).Reason, Is.EqualTo("arrow-initial-declaration-or-live-generation-unavailable"));
            VanillaAccept(Shot(damage, index));
            Assert.That(Arrow(index).damage, Is.EqualTo(damage));
            Assert.That(sent, Does.Contain((27, Arrow(index).whoAmI, (int)damage)));
            Assert.That(sent.Any(x => x.Type == 28), Is.False, "A declaration's receive path does not strike an NPC.");
        }
        TestContext.Out.WriteLine("Actual target MessageBuffer.GetData: packet27 damage9 and damage1005 created corresponding entity.damage and reached 27 export; no 28 export.");
    }

    [Test]
    public void ConfirmedArrowSyncPassesAfterWeaponBuffPrefixChangesAndBelowInitialBound()
    {
        Establish(500);
        actor.TPlayer.inventory[0].SetDefaults(ItemID.Tsunami);
        actor.TPlayer.rangedDamage = 3; actor.TPlayer.archery = true;
        foreach (short damage in new short[] { 500, 125, 400, 0 })
        {
            var result = Evaluate(Shot(damage));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(result.Facts["initialDamageLegitimacy"], Is.EqualTo("not-established"));
            VanillaAccept(Shot(damage));
        }
        Assert.That(Evaluate(Shot(501)).Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"));
    }

    [Test]
    public void RejectedInitialInputMissingRelayAndChangedGenerationCannotMakeAPredicate()
    {
        Evaluate(Shot(), true); VanillaAccept(Shot());
        Assert.That(contexts.WitnessCount, Is.Zero);
        Assert.That(Evaluate(Shot(1005)).Verdict, Is.EqualTo(Verdict.Unknown));
        Arrow().active = false;
        Evaluate(Shot(index: 4)); Tick(); VanillaAccept(Shot(index: 4));
        Assert.That(contexts.WitnessCount, Is.Zero, "No pending observation survives a different update.");
        Arrow(4).active = false; Establish(index: 5);
        Assert.That(Evaluate(Shot(1005, index: 5, generation: 2)).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(Shot(1005, spawner: 255)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ActualSourceAndOwnerExportCreateSessionScopedExceptionsIncludingDelayedEcho(bool source)
    {
        Establish();
        if (source) Arrow().ApplyStatsFromSource(new EntitySource_Parent(new NPC()));
        else NetMessage.SendData(27, Slot, -1, null, Arrow().whoAmI);
        Assert.That(Evaluate(Shot(1005)).Verdict, Is.EqualTo(Verdict.Unknown));
        Tick(); Arrow().active = false;
        Evaluate(Shot(9, index: 4)); VanillaAccept(Shot(9, index: 4));
        Assert.That(Evaluate(Shot(1005, index: 4)).Verdict, Is.EqualTo(Verdict.Unknown));
        session = session with { Generation = 2 }; Tick();
        Establish(9, index: 5);
        Assert.That(Evaluate(Shot(1005, index: 5)).Facts["sourceException"], Is.EqualTo("False"));
    }

    [Test]
    public void PluginMutationTypeChangeAndTtlWithdrawOnlyTheAffectedEvolutionProof()
    {
        Establish(); Arrow().damage = 1005;
        Assert.That(Evaluate(Shot(1005)).Verdict, Is.EqualTo(Verdict.Unknown));
        Tick(); Assert.That(contexts.WitnessCount, Is.Zero);
        session = session with { Generation = 2 }; Tick();
        Establish(9, index: 4);
        contexts.Evaluate(Shot(9, index: 4, type: 2), session, actor, false);
        Assert.That(Evaluate(Shot(1005, index: 4)).Verdict, Is.EqualTo(Verdict.Unknown));
        Establish(9, index: 5);
        for (int i = 0; i <= M5CombatContexts.WitnessTtlTicks; i++) Tick();
        Assert.That(Evaluate(Shot(1005, index: 5)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void M6UnknownPluginObservationsRemainExceptionsAfterRemoval()
    {
        Establish();
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnknownArrowPlugin(); var container = new PluginContainer(plugin); plugins.Add(container);
        try
        {
            // Presence is observed directly at evaluation, even when the plugin appears between game ticks.
            var result = Evaluate(Shot(1005));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(result.Facts["pluginCompositionException"], Is.EqualTo("True"));
        }
        finally { plugins.Remove(container); }
        for (int i = 0; i < 100; i++) Tick();
        Assert.That(Evaluate(Shot(1005)).Facts["pluginCompositionException"], Is.EqualTo("True"));
        session = session with { Generation = 2 }; Tick(); Establish(9, index: 4);
        var fresh = Evaluate(Shot(1005, index: 4));
        Assert.That(fresh.Facts["pluginCompositionException"], Is.EqualTo("False"));
        Assert.That(fresh.Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"));
    }

    private sealed class UnknownArrowPlugin() : TerrariaPlugin(null!)
    {
        public override void Initialize() { }
    }

    [Test]
    public void NativeReflectionReducesArrowDamageAndDelayedOriginalDeclarationStillPasses()
    {
        Establish(100);
        var oldDust = Main.dust;
        Main.dust = Enumerable.Range(0, oldDust.Length).Select(_ => new Dust()).ToArray();
        try
        {
            Arrow().position = new Microsoft.Xna.Framework.Vector2(400, 400);
            Arrow().oldVelocity = new Microsoft.Xna.Framework.Vector2(3, 0);
            new NPC().ReflectProjectile(Arrow());
            Assert.That(Arrow().damage, Is.EqualTo(25)); Assert.That(Arrow().reflected, Is.True);
            Assert.That(Evaluate(Shot(100)).Verdict, Is.EqualTo(Verdict.Pass), "Do not replace the initial bound with server reflected damage25.");
            Assert.That(Evaluate(Shot(101)).Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"));
        }
        finally { Main.dust = oldDust; }
    }

    [Test]
    public void OtherSessionCannotEraseVictimsWitnessUsingForeignDestroyOrTypeChange()
    {
        Establish();
        var other = session with { Slot = 8 };
        var otherActor = new TSPlayer(8);
        contexts.Evaluate(Shot(type: 2), other, otherActor, false);
        byte[] body = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Slot, 3, 1).bits);
        contexts.Evaluate(new(M2PacketKind.ProjectileDestroy, body), other, otherActor, false);
        Assert.That(Evaluate(Shot(1005)).Facts["initialDeclarationConfirmed"], Is.EqualTo("True"));
        session = session with { Generation = 2 }; Tick();
        Assert.That(Evaluate(Shot(1005)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void MissingInternalRangePremiseAllowsTheSecondDeclarationAndCannotBanOrRevoke()
    {
        var store = new Store();
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, [new(M5CombatRules.ArrowEvolutionRuleId, M5CombatRules.Version, TargetRuntime.Fingerprint, "m2.1", RuleQualification.TestLab, "docs/m5-combat.md")]);
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 37), Is.EqualTo(AuthenticationResult.Authenticated)); Tick();
        Establish(); int sentBefore = sent.Count;
        var result = Evaluate(Shot(1005));
        var decision = engine.ObserveBusiness(new(session, 27, result, new(TargetRuntime.Fingerprint, "m2.1", true, true, true, true)));
        Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Unknown));
        Assert.That(decision.Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"));
        Assert.That(engine.CanWrite(session), Is.True);
        Assert.That(Arrow().damage, Is.EqualTo(9)); Assert.That(sent.Count, Is.EqualTo(sentBefore));
        VanillaAccept(Shot(1005)); Assert.That(Arrow().damage, Is.EqualTo(1005));
        Assert.That(engine.PumpAsync().AsTask().GetAwaiter().GetResult().Applied, Is.EqualTo(0));
        Assert.That(store.Banned, Is.Empty);
        engine.Disconnect(session); session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 37), Is.EqualTo(AuthenticationResult.Authenticated));
    }

    [Test]
    public void NativeWeaponAndAmmoMethodsExplain9And1005AndRetainLegalPrefixAndBuffExamples()
    {
        var bow = actor.TPlayer.inventory[0]; bow.SetDefaults(ItemID.WoodenBow);
        var ammo = actor.TPlayer.inventory[54]; ammo.SetDefaults(ItemID.WoodenArrow); ammo.stack = 100;
        int Compute()
        {
            int projectile = bow.shoot, damage = actor.TPlayer.GetWeaponDamage(bow);
            float speed = bow.shootSpeed, knockback = bow.knockBack; bool canShoot = false;
            actor.TPlayer.PickAmmo(bow, ref projectile, ref speed, ref canShoot, ref damage, ref knockback, out int used, true);
            Assert.That(canShoot, Is.True); Assert.That(projectile, Is.EqualTo(1)); Assert.That(used, Is.EqualTo(ItemID.WoodenArrow));
            return damage;
        }
        Assert.That(Compute(), Is.EqualTo(9)); bow.damage = 1000;
        Assert.That(Compute(), Is.EqualTo(1005));
        bow.SetDefaults(ItemID.WoodenBow); bool prefixed = bow.Prefix(PrefixID.Demonic);
        actor.TPlayer.rangedDamage = 1.2f; actor.TPlayer.arrowDamage = 1.1f;
        TestContext.Out.WriteLine($"Native modifier counterexample: prefixApplied={prefixed}, bowDamage={bow.damage}, ranged={bow.ranged}, rangedMult={actor.TPlayer.rangedMultDamage}, effective={actor.TPlayer.bowEffectiveDamage}, ammoDamage={ammo.damage}, result={Compute()}");
        Assert.That(Compute(), Is.GreaterThan(9));
        Assert.That(ammo.stack, Is.EqualTo(100), "Audit uses native dontConsume argument.");
        TestContext.Out.WriteLine("Native GetWeaponDamage/PickAmmo: canonical wooden bow4 + wooden arrow5 =9; local bow.damage1000 + ammo5 =1005. Prefix/buffs give a distinct allowed value, so 9 is not a threshold.");
    }

    [TestCase((short)9, (short)9, false, 0, 9)]
    [TestCase((short)1005, (short)1005, false, 0, 1005)]
    [TestCase((short)9, (short)1000, false, 0, 1000)]
    [TestCase((short)1005, (short)1005, true, 10, 2000)]
    public void ActualActiveNpcDamageComesFromIndependent28AndItsDefenseCritSemantics(short declared, short strike,
        bool critical, int defense, int expectedLoss)
    {
        Establish(declared);
        var target = Main.npc[11]; target.SetDefaults(NPCID.BlueSlime);
        target.active = true; target.generation = 3; target.lifeMax = target.life = 5000;
        target.defense = defense; target.takenDamageMultiplier = 1f;
        var arrow = Arrow(); arrow.position = target.position;
        arrow.Damage();
        Assert.That(target.life, Is.EqualTo(5000), "Dedicated server does not execute a remote owner's projectile PVE collision.");
        var body = new byte[10]; body[0] = 11; body[1] = 3;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), strike); body[8] = 2; body[9] = critical ? (byte)1 : (byte)0;
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = 28; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out int type);
        Assert.That(type, Is.EqualTo(28)); Assert.That(target.life, Is.EqualTo(5000 - expectedLoss));
        Assert.That(target.active, Is.True); Assert.That(arrow.damage, Is.EqualTo(declared));
        Assert.That(sent.Any(x => x.Type == 28), Is.True);
        TestContext.Out.WriteLine($"Active native NPC slot11/gen3: arrow declaration={declared}; separate packet28 damage={strike}/crit={critical}/defense={defense}; actual life5000->{target.life}. No projectile key exists in packet28.");
    }

    [Test]
    public void NativeItemDefaultsEnumerateAllDirectArrowWeaponsWithoutCallingItACompleteDamageProof()
    {
        var items = new List<(int Type, int Damage)>();
        for (int type = 1; type < ItemID.Count; type++)
        {
            var item = new Item(); item.SetDefaults(type);
            if (item.useAmmo == AmmoID.Arrow || item.shoot == 1) items.Add((type, item.damage));
        }
        Assert.That(items.Any(x => x.Type == ItemID.WoodenBow), Is.True);
        TestContext.Out.WriteLine("Native direct arrow weapon/ammo superset (type:base damage): " + string.Join(", ", items.Select(x => $"{x.Type}:{x.Damage}")));
        TestContext.Out.WriteLine("Maximum raw item default=" + items.Max(x => x.Damage) + "; not an admissible projectile bound: prefix, effective ranged/arrow multiplier, post-PickAmmo transformations, sources and export exceptions are separate prerequisites.");
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        private readonly Dictionary<Guid, BanIntent> pending = [];
        internal HashSet<long> Banned { get; } = [];
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>(pending.Values.ToArray());
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) { pending.TryAdd(intent.IncidentId, intent); return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) { pending.Remove(incidentId); return ValueTask.CompletedTask; }
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Banned.Add(intent.AccountId); return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
