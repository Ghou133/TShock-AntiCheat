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
public sealed class M3CombatContextTests
{
    private M3CombatContexts contexts = null!;
    private TSPlayer actor = null!;
    private SessionKey session;
    private Player? oldPlayer;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private int oldMode, oldWidth;
    private const int Slot = 7;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldProjectiles = Main.projectile; oldMap = Projectile.keyToIndex;
        oldMode = Main.netMode; oldWidth = Main.maxTilesX;
        Main.netMode = 2; Main.maxTilesX = 4200;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, maxMinions = 1 };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001];
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Account = new UserAccount { ID = 37, Name = "m3-context-test" } };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        contexts = new M3CombatContexts("actual-target-adapter-fixture");
        contexts.Install();
        for (int i = 0; i < 40 && !contexts.ProjectileTableReady; i++) Tick();
        Assert.That(contexts.ProjectileTableReady, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        contexts.Dispose(); Main.player[Slot] = oldPlayer!; Main.projectile = oldProjectiles;
        Projectile.keyToIndex = oldMap; Main.netMode = oldMode; Main.maxTilesX = oldWidth;
    }

    private void Tick() => contexts.Tick(session.WorldEpoch, slot => slot == Slot
        ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));

    private static M2Packet Packet(PacketTypes type, byte[] body) =>
        M2PacketReader.Read(M2ContractsTests.Packet(type, body, Slot), true).Packet!;

    private static M2Packet Shot(int type, int index = 3)
    {
        byte[] body = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Slot, index, 1).bits);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), (short)type);
        return Packet(PacketTypes.ProjectileNew, body);
    }

    private static M2Packet Use(int selected, bool usingItem = true)
    {
        byte[] body = new byte[14]; body[0] = Slot; body[1] = usingItem ? (byte)32 : (byte)0; body[5] = (byte)selected;
        return Packet(PacketTypes.PlayerUpdate, body);
    }

    private IReadOnlyList<BusinessRuleResult> Evaluate(M2Packet packet, bool cancelled = false) => contexts.Evaluate(packet, session, actor, cancelled);
    private static BusinessRuleResult Rule(IReadOnlyList<BusinessRuleResult> results, string id) => results.Single(x => x.RuleId == id);

    [Test]
    public void HeldItemAloneAndCancelledOrUnacceptedControlsDoNotCreateWeaponCause()
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WandofSparking);
        int shoot = actor.TPlayer.inventory[0].shoot;
        Assert.That(Rule(Evaluate(Shot(shoot)), CombatRules.WeaponRuleId).Action, Is.EqualTo(ControlAction.Unknown));
        Evaluate(Use(0), true); Tick();
        Assert.That(Rule(Evaluate(Shot(shoot)), CombatRules.WeaponRuleId).Action, Is.EqualTo(ControlAction.Unknown));
        Evaluate(Use(0)); Tick(); // Core has not accepted controlUseItem into the player.
        Assert.That(Rule(Evaluate(Shot(shoot)), CombatRules.WeaponRuleId).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void RawControlsConfirmedByNextRuntimeSnapshotProduceOnlyPositiveCompatibility()
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WandofSparking);
        int shoot = actor.TPlayer.inventory[0].shoot;
        Evaluate(Use(0)); actor.TPlayer.controlUseItem = true; Tick();
        var legal = Rule(Evaluate(Shot(shoot)), CombatRules.WeaponRuleId);
        Assert.That(legal.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(legal.Facts["exclusiveCause"], Is.EqualTo("false"));
        Assert.That(legal.PrerequisitesComplete, Is.False);
        var mismatch = Rule(Evaluate(Shot(ProjectileID.WoodenArrowFriendly)), CombatRules.WeaponRuleId);
        Assert.That(mismatch.Action, Is.EqualTo(ControlAction.Unknown));
        Evaluate(Use(1));
        Assert.That(Rule(Evaluate(Shot(shoot)), CombatRules.WeaponRuleId).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void ActualHalfSlotDefaultsAndRuntimePopulationAllowLegalTwinPairWithoutReplacementProof()
    {
        // Target SetDefaults(387/388) defines the two optic-staff entities as 0.5 slots each.
        var first = Rule(Evaluate(Shot(387, 3)), ProjectileRules.BudgetRuleId);
        var second = Rule(Evaluate(Shot(388, 4)), ProjectileRules.BudgetRuleId);
        Assert.That(first.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(second.Action, Is.EqualTo(ControlAction.Pass));
        var extra = Rule(Evaluate(Shot(387, 5)), ProjectileRules.BudgetRuleId);
        Assert.That(extra.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(extra.PredicateSatisfied, Is.False, "Unobserved legal replacement must not become a cheat.");
    }

    [Test]
    public void AcceptedFishingControlAndExistingMultipleBobbersHaveRuntimePositivePath()
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WoodFishingPole);
        int type = actor.TPlayer.inventory[0].shoot;
        Evaluate(Use(0)); actor.TPlayer.controlUseItem = true; Tick();
        Assert.That(Rule(Evaluate(Shot(type)), ProjectileRules.FishingRuleId).Action, Is.EqualTo(ControlAction.Pass));
        for (int i = 0; i < 5; i++) SetEntity(type, i, i + 20);
        Tick(); Evaluate(Use(0, false));
        var sync = Rule(Evaluate(Shot(type, 20)), ProjectileRules.FishingRuleId);
        Assert.That(sync.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(sync.Facts["activeBobbers"], Is.EqualTo("5"));
        Assert.That(sync.Facts["castBudgetEnforced"], Is.EqualTo("false"));
    }

    [Test]
    public void ActualApplyStatsHookCollectsItemAndParentSourceAndRejectsReusedEntityKey()
    {
        var weapon = new Item(); weapon.SetDefaults(ItemID.WandofSparking);
        var first = SetEntity(weapon.shoot, 0, 3);
        first.ApplyStatsFromSource(new EntitySource_ItemUse(actor.TPlayer, weapon));
        var legal = Rule(Evaluate(Shot(weapon.shoot, 3)), CombatRules.WeaponRuleId);
        Assert.That(legal.Facts["sourceHook"], Is.EqualTo("Projectile.ApplyStatsFromSource"));
        Assert.That(legal.Facts["sourceWeapon"], Is.EqualTo(ItemID.WandofSparking.ToString()));
        var child = SetEntity(ProjectileID.WoodenArrowFriendly, 1, 4);
        child.ApplyStatsFromSource(new EntitySource_Parent(first));
        Assert.That(Rule(Evaluate(Shot(child.type, 4)), CombatRules.WeaponRuleId).Facts["sourceParent"], Is.EqualTo(first.type.ToString()));
        first.type = ProjectileID.Bullet;
        Tick();
        Assert.That(contexts.SourceCount, Is.EqualTo(1));
        Assert.That(Rule(Evaluate(Shot(ProjectileID.Bullet, 3)), CombatRules.WeaponRuleId).Action, Is.EqualTo(ControlAction.Unknown));
        session = session with { Generation = 2 }; Tick();
        Assert.That(contexts.SourceCount, Is.Zero);
    }

    [Test]
    public void WorldChangeDisposalAndCapacityChangesWithdrawOnlyDependentContext()
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WandofSparking);
        var entity = SetEntity(actor.TPlayer.inventory[0].shoot, 0, 3);
        entity.ApplyStatsFromSource(new EntitySource_ItemUse(actor.TPlayer, actor.TPlayer.inventory[0]));
        Assert.That(contexts.SourceCount, Is.EqualTo(1));
        actor.TPlayer.maxMinions = 0;
        Assert.That(Rule(Evaluate(Shot(387, 9)), ProjectileRules.BudgetRuleId).Action, Is.EqualTo(ControlAction.Unknown));
        session = session with { WorldEpoch = 6 }; Tick();
        Assert.That(contexts.SourceCount, Is.Zero);
        contexts.Dispose();
        entity.ApplyStatsFromSource(new EntitySource_ItemUse(actor.TPlayer, actor.TPlayer.inventory[0]));
        Assert.That(contexts.SourceCount, Is.Zero);
    }

    [Test]
    public void RealSourceHookFaultDetachesOnceWithoutCancellingOriginalSourceApplication()
    {
        int calls = 0, faults = 0;
        contexts.IntegrityFault = _ => faults++;
        typeof(M3CombatContexts).GetField("targetAtSlot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(contexts, new Func<int, (SessionSnapshot?, TSPlayer?)>(_ =>
            { calls++; throw new InvalidOperationException("fixture source identity provider fault"); }));
        var weapon = new Item(); weapon.SetDefaults(ItemID.WandofSparking);
        var entity = SetEntity(weapon.shoot, 0, 3);
        Assert.DoesNotThrow(() => entity.ApplyStatsFromSource(new EntitySource_ItemUse(actor.TPlayer, weapon)));
        Assert.That(contexts.SourceCount, Is.Zero);
        Assert.DoesNotThrow(() => entity.ApplyStatsFromSource(new EntitySource_ItemUse(actor.TPlayer, weapon)));
        Assert.That(calls, Is.EqualTo(1)); Assert.That(faults, Is.EqualTo(1));
    }

    private static Projectile SetEntity(int type, int entityIndex, int identity)
    {
        var entity = Main.projectile[entityIndex]; entity.SetDefaults(type); entity.owner = Slot;
        entity.key = new ProjectileKey(Slot, identity, 1); entity.active = true;
        Projectile.keyToIndex[Slot, identity] = entityIndex;
        return entity;
    }
}
