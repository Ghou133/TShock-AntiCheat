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
public sealed class M4CombatContextTests
{
    private const int Slot = 7;
    private M4CombatContexts contexts = null!;
    private TSPlayer actor = null!;
    private SessionKey session;
    private Player? oldPlayer;
    private Player? oldServerPlayer;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private bool[] oldHostile = null!;
    private int oldMode, oldWidth, oldMyPlayer;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldProjectiles = Main.projectile; oldMap = Projectile.keyToIndex;
        oldServerPlayer = Main.player[255];
        oldHostile = Main.projHostile; oldMode = Main.netMode; oldWidth = Main.maxTilesX; oldMyPlayer = Main.myPlayer;
        Main.netMode = 2; Main.maxTilesX = 4200; Main.myPlayer = 255;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.player[255] = new Player { whoAmI = 255 };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001];
        Main.projHostile = new bool[ProjectileID.Count];
        var ritual = new Projectile(); ritual.SetDefaults(M4CombatRules.RitualType);
        Main.projHostile[M4CombatRules.RitualType] = ritual.hostile;
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Account = new UserAccount { ID = 37, Name = "m4-combat-fixture" } };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        contexts = new M4CombatContexts(TargetRuntime.Fingerprint);
        contexts.Install(); Tick();
        Assert.That(contexts.ContractHealthy, Is.True);
        Assert.That(contexts.PortalContractHealthy, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        contexts.Dispose(); Main.player[Slot] = oldPlayer!; Main.projectile = oldProjectiles;
        Main.player[255] = oldServerPlayer!;
        Projectile.keyToIndex = oldMap; Main.netMode = oldMode; Main.maxTilesX = oldWidth;
        Main.projHostile = oldHostile; Main.myPlayer = oldMyPlayer;
    }

    private void Tick() => contexts.Tick(session.WorldEpoch, slot => slot == Slot
        ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));

    private static M2Packet Shot(int type, int spawner = Slot, int index = 3, short damage = 0,
        float ai0 = 0, float ai1 = 0, float ai2 = 0, float knockback = 0)
    {
        byte[] body = new byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(spawner, index, 1).bits);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), (short)type);
        body[22] = 127; body[23] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(24), ai0);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(28), ai1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(32), 40000);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(34), damage);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(36), knockback);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(40), damage);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(42), ai2);
        return M2PacketReader.Read(M2ContractsTests.Packet(PacketTypes.ProjectileNew, body, Slot), true).Packet!;
    }

    private IReadOnlyList<BusinessRuleResult> Evaluate(M2Packet packet, bool cancelled = false) => contexts.Evaluate(packet, session, actor, cancelled);
    private BusinessRuleResult Ritual(M2Packet packet, bool cancelled = false) => Evaluate(packet, cancelled).Single(x => x.RuleId == M4CombatRules.RitualRuleId);
    private BusinessRuleResult Portal(M2Packet packet, bool cancelled = false) => Evaluate(packet, cancelled).Single(x => x.RuleId == M4CombatRules.PortalDamageRuleId);

    [TestCase(1)] // Arrow; source may include ammunition and parent effects.
    [TestCase(387)] // Legal 0.5-slot twin minion.
    [TestCase(388)]
    [TestCase(360)] // Bobber: multiple bobbers must remain a separate mechanism.
    [TestCase(600)] // Portal gun holdout and bolt have legitimate negative base damage in this runtime.
    [TestCase(601)]
    public void LegalProjectileTypesAndFiniteHighDamageNeverBecomeRitualOrDamageProof(int type)
    {
        Assert.That(Evaluate(Shot(type, damage: short.MaxValue)), Is.Empty);
        Assert.That(Evaluate(Shot(type, damage: -1)), Is.Empty, "A finite number is not by itself a cheat proof.");
    }

    [Test]
    public void ExactOptionalFieldOffsetsIncludeUnsignedBannerAndDoNotInferDamageCausality()
    {
        Assert.That(M4CombatProjectileReader.TryRead(Shot(1, damage: 12345, ai0: 2, ai1: 3, ai2: 4, knockback: 5), out var shot), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(shot!.Ai0, Is.EqualTo(2)); Assert.That(shot.Ai1, Is.EqualTo(3)); Assert.That(shot.Ai2, Is.EqualTo(4));
            Assert.That(shot.Banner, Is.EqualTo(40000)); Assert.That(shot.Damage, Is.EqualTo(12345));
            Assert.That(shot.OriginalDamage, Is.EqualTo(12345)); Assert.That(shot.Knockback, Is.EqualTo(5));
        });
        for (int first = 0; first < 128; first++)
        for (int second = 0; second < 2; second++)
        {
            bool hasSecond = (first & 4) != 0;
            int length = 23 + (hasSecond ? 1 : 0) + ((first & 1) != 0 ? 4 : 0) + ((first & 2) != 0 ? 4 : 0) +
                ((first & 8) != 0 ? 2 : 0) + ((first & 16) != 0 ? 2 : 0) + ((first & 32) != 0 ? 4 : 0) +
                ((first & 64) != 0 ? 2 : 0) + (hasSecond && second == 1 ? 4 : 0);
            byte[] body = new byte[length]; body[22] = (byte)first; if (hasSecond) body[23] = (byte)second;
            var packet = new M2Packet(M2PacketKind.ProjectileNew, body);
            Assert.That(M4CombatProjectileReader.TryRead(packet, out _), Is.True);
            Assert.That(M4CombatProjectileReader.TryRead(packet with { Payload = body[..^1] }, out _), Is.False);
            Assert.That(M4CombatProjectileReader.TryRead(packet with { Payload = [.. body, 0] }, out _), Is.False);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void NonfiniteAiOrKnockbackIsSafetyOnly(int component)
    {
        var result = Evaluate(Shot(1, ai0: component == 0 ? float.NaN : 0, ai1: component == 1 ? float.PositiveInfinity : 0,
            ai2: component == 2 ? float.NegativeInfinity : 0, knockback: component == 3 ? float.NaN : 0)).Single();
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.PredicateSatisfied, Is.False);
    }

    [Test]
    public void NonfiniteRitualKeepsNumericSafetySeparateFromIndependentlyProvenNpcRoleViolation()
    {
        var results = Evaluate(Shot(490, ai0: float.NaN));
        Assert.That(results.Single(x => x.RuleId == M4CombatContexts.NumericRuleId).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        var role = results.Single(x => x.RuleId == M4CombatRules.RitualRuleId);
        Assert.That(role.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(role.Reason, Is.EqualTo("client-created-npc-only-cultist-ritual"));
        Assert.That(Evaluate(Shot(490, spawner: 255, ai0: float.NaN)).All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
    }

    [Test]
    public void LegalPortalPlacementAndBothFormsHaveAnEffectivePassWithoutHeldWeaponOrObservedParent()
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.PortalGun);
        Assert.That(actor.TPlayer.inventory[0].shoot, Is.EqualTo(600));
        Assert.That(actor.TPlayer.GetWeaponDamage(actor.TPlayer.inventory[0]), Is.EqualTo(-1),
            "The child placement sets zero independently; do not generalize its invariant to the parent chain.");
        for (int form = 0; form < 2; form++)
            Assert.That(Portal(Shot(602, ai0: -MathF.PI / 2, ai1: form)).Reason, Is.EqualTo("portal-placement-canonical-zero-damage"));
        actor.TPlayer.inventory[0].SetDefaults(ItemID.LastPrism); // A delayed child can arrive after a weapon switch.
        Assert.That(Portal(Shot(602)).Action, Is.EqualTo(ControlAction.Pass));
        var portal = SetEntity(Slot, 3, 602); Tick(); portal.active = false;
        Assert.That(Portal(Shot(602, index: 4, damage: 1)).Verdict, Is.EqualTo(Verdict.ProvenCheat),
            "Normal zero-damage runtime portals do not invalidate later independent constructor checks.");
    }

    [TestCase((short)1)]
    [TestCase((short)123)]
    [TestCase(short.MaxValue)]
    [TestCase((short)-1)]
    public void PortalFreshCreationHasAnExactZeroDamageInvariantNotAnArbitraryUpperBound(short damage)
    {
        var result = Portal(Shot(602, damage: damage));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(result.Facts["constructorDamage"], Is.EqualTo("0"));
        Assert.That(result.Facts["mechanismVersion"], Is.EqualTo(M4CombatRules.PortalMechanismVersion));
        Assert.That(Portal(Shot(602, spawner: 255, damage: damage)).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Portal(Shot(602, damage: damage, ai0: float.NaN)).Verdict, Is.EqualTo(Verdict.Unknown));
        SetEntity(Slot, 3, 602);
        Assert.That(Portal(Shot(602, damage: damage)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void ActualPortalSourceHookAndPreexistingNoncanonicalStateExcludeOnlyThisMechanismAndSession()
    {
        var portal = SetEntity(Slot, 3, 602);
        portal.ApplyStatsFromSource(new EntitySource_Parent(new Projectile { type = 601, damage = 123 }));
        portal.active = false;
        Assert.That(Portal(Shot(602, index: 4, damage: 123)).Reason, Is.EqualTo("portal-placement-server-plugin-source-exception"));
        Assert.That(Ritual(Shot(490, index: 4)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        session = session with { Generation = 2 }; Tick();
        Assert.That(Portal(Shot(602, index: 4, damage: 123)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        portal = SetEntity(Slot, 3, 602); portal.damage = 321; Tick(); portal.active = false;
        Assert.That(Portal(Shot(602, index: 4, damage: 321)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ActualServerExportHookDistinguishesSelfExportFromRelayAndNormalZeroDamage(bool broadcast)
    {
        static void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        HookEvents.Terraria.NetMessage.SendData += Sink;
        try
        {
            var portal = SetEntity(Slot, 3, 602); portal.damage = 0;
            NetMessage.SendData(27, broadcast ? -1 : Slot, -1, null, 0);
            portal.active = false;
            Assert.That(Portal(Shot(602, index: 4, damage: 123)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
            portal = SetEntity(Slot, 3, 602); portal.damage = 123;
            NetMessage.SendData(27, -1, Slot, null, 0); // C2S relay excludes the owner.
            portal.active = false;
            Assert.That(Portal(Shot(602, index: 4, damage: 123)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
            portal = SetEntity(Slot, 3, 602); portal.damage = 123;
            NetMessage.SendData(27, broadcast ? -1 : Slot, -1, null, 0);
            portal.active = false;
            Assert.That(Portal(Shot(602, index: 4, damage: 123)).Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(Ritual(Shot(490, index: 4)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        }
        finally { HookEvents.Terraria.NetMessage.SendData -= Sink; }
    }

    [Test]
    public void NormalServerRitualKeyAndExistingClientKeyDoNotProduceFreshCreationProof()
    {
        Assert.That(Ritual(Shot(490, spawner: 255)).Action, Is.EqualTo(ControlAction.Unknown));
        SetEntity(Slot, 3);
        Assert.That(Ritual(Shot(490)).Action, Is.EqualTo(ControlAction.Unknown));
        Tick(); Main.projectile[0].active = false;
        Assert.That(Ritual(Shot(490, index: 4)).Action, Is.EqualTo(ControlAction.Unknown), "Plugin/source ambiguity lasts for this session, not a guessed delay TTL.");
    }

    [Test]
    public void ActualSourceHookPreservesLocalExceptionAfterEntityRemovalAndExpiresAtSessionOrWorldChange()
    {
        var entity = SetEntity(Slot, 3);
        entity.ApplyStatsFromSource(new EntitySource_Parent(new NPC { type = NPCID.CultistBoss }));
        entity.active = false;
        Assert.That(Ritual(Shot(490, index: 4)).Reason, Is.EqualTo("cultist-ritual-server-plugin-source-exception"));
        for (int i = 0; i < 130; i++) Tick();
        Assert.That(Ritual(Shot(490, index: 4)).Action, Is.EqualTo(ControlAction.Unknown));
        session = session with { Generation = 2 }; Tick();
        Assert.That(Ritual(Shot(490, index: 4)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        SetEntity(Slot, 3).ApplyStatsFromSource(new EntitySource_Parent(new NPC()));
        Main.projectile[0].active = false;
        session = session with { WorldEpoch = 6 }; Tick();
        Assert.That(Ritual(Shot(490, index: 4)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ServerRitualSourceDoesNotGrantAPlayerAScopeException()
    {
        var entity = SetEntity(255, 3);
        entity.ApplyStatsFromSource(new EntitySource_Parent(new NPC { type = NPCID.CultistBoss }));
        Assert.That(Ritual(Shot(490)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void NoInventoryReadinessTableMutationOrStaleSessionCanBeHardEvidence()
    {
        actor.HasSentInventory = false;
        Assert.That(Ritual(Shot(490)).Verdict, Is.EqualTo(Verdict.Unknown)); actor.HasSentInventory = true;
        Main.projHostile[490] = false;
        Assert.That(Ritual(Shot(490)).Verdict, Is.EqualTo(Verdict.Unknown)); Main.projHostile[490] = true;
        actor.Account.ID = 38;
        Assert.That(Ritual(Shot(490)).Verdict, Is.EqualTo(Verdict.Unknown)); actor.Account.ID = 37;
        contexts.Dispose();
        Assert.That(Ritual(Shot(490)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void SourceHookFailureWithdrawsOnlyItsHardRuleAndDoesNotCancelOriginalMethod()
    {
        int failures = 0;
        contexts.IntegrityFault = _ => failures++;
        typeof(M4CombatContexts).GetField("targetAtSlot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(contexts, new Func<int, (SessionSnapshot?, TSPlayer?)>(_ => throw new InvalidOperationException("source provider failure")));
        var entity = SetEntity(Slot, 3);
        Assert.DoesNotThrow(() => entity.ApplyStatsFromSource(new EntitySource_Parent(new NPC())));
        Assert.That(contexts.ContractHealthy, Is.False); Assert.That(failures, Is.EqualTo(1));
        entity.active = false;
        Assert.That(Ritual(Shot(490, index: 4)).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(Shot(1, ai0: float.NaN)).Single().Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.DoesNotThrow(() => entity.ApplyStatsFromSource(new EntitySource_Parent(new NPC())));
        Assert.That(failures, Is.EqualTo(1));
    }

    [TestCase(490, (short)0, M4CombatRules.RitualRuleId)]
    [TestCase(602, (short)123, M4CombatRules.PortalDamageRuleId)]
    public async Task FirstParsedProvenEventRevokesSynchronouslyAndPreservesUpstreamCancellation(int type, short damage, string ruleId)
    {
        var store = new Store();
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, [new(ruleId, "1.0.0", TargetRuntime.Fingerprint, "m2.1", RuleQualification.TestLab, "docs/m4-combat-inputs.md")]);
        Assert.That(await engine.RecoverAsync(), Is.True);
        session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 37), Is.EqualTo(AuthenticationResult.Authenticated)); Tick();
        Assert.That(Evaluate(Shot(ProjectileID.WoodenArrowFriendly, damage: 32767)), Is.Empty);
        var result = Evaluate(Shot(type, damage: damage), cancelled: true).Single(x => x.RuleId == ruleId);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(result.Facts["upstreamCancelled"], Is.EqualTo("True"));
        var decision = engine.ObserveBusiness(new(session, 27, result, new(TargetRuntime.Fingerprint, "m2.1", true, true, true, true)));
        Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Block)); Assert.That(engine.CanWrite(session), Is.False);
        Assert.That(store.Banned, Is.Empty); Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(store.Banned, Is.EquivalentTo(new long[] { 37 }));
        engine.Disconnect(session); session = engine.OpenSession(Slot)!.Value;
        Assert.That(engine.Authenticate(session, 37), Is.EqualTo(AuthenticationResult.AccountBlocked));
    }

    [TestCase(490)]
    [TestCase(602)]
    public void TargetItemDefaultsDoNotProvideADirectWeaponOrAmmoCreationRoute(int projectileType)
    {
        var matches = new List<int>();
        for (int type = 1; type < ItemID.Count; type++)
        {
            var item = new Item();
            try { item.SetDefaults(type); }
            catch (Exception error) { throw new InvalidOperationException("Target Item.SetDefaults failed for type " + type, error); }
            if (item.shoot == projectileType) matches.Add(type);
        }
        Assert.That(matches, Is.Empty, "This audits item/ammo defaults only; NPC/source code and runtime scope checks are independently required.");
    }

    private static Projectile SetEntity(int spawner, int identity, int type = 490)
    {
        var entity = Main.projectile[0]; entity.SetDefaults(type); entity.owner = spawner;
        entity.key = new ProjectileKey(spawner, identity, 1); entity.active = true;
        Projectile.keyToIndex[spawner, identity] = 0; return entity;
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
