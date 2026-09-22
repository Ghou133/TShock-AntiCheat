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
public sealed class M5CombatResourceTests
{
    private const int Slot = 7;
    private M3CombatContexts contexts = null!;
    private TSPlayer actor = null!;
    private SessionKey session;
    private Player? oldPlayer;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private int oldMode, oldWidth, oldMyPlayer;
    private bool oldDedServ;
    private Dust[] oldDust = null!;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldProjectiles = Main.projectile; oldMap = Projectile.keyToIndex;
        oldMode = Main.netMode; oldWidth = Main.maxTilesX; oldMyPlayer = Main.myPlayer; oldDedServ = Main.dedServ;
        oldDust = Main.dust; Main.dust = Enumerable.Range(0, oldDust.Length).Select(_ => new Dust()).ToArray();
        Main.netMode = 2; Main.maxTilesX = 4200; Main.myPlayer = 255; Main.dedServ = true;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, maxMinions = 1, maxTurrets = 2 };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001];
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Account = new UserAccount { ID = 37, Name = "m5-resource-fixture" } };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        contexts = new M3CombatContexts(TargetRuntime.Fingerprint); contexts.Install();
        for (int i = 0; i < 40 && !contexts.ProjectileTableReady; i++) Tick();
        HookEvents.Terraria.NetMessage.SendData += Sink;
    }
    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        contexts.Dispose(); Main.player[Slot] = oldPlayer!; Main.projectile = oldProjectiles; Projectile.keyToIndex = oldMap;
        Main.netMode = oldMode; Main.maxTilesX = oldWidth; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedServ;
        Main.dust = oldDust;
    }
    private static void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
    private void Tick() => contexts.Tick(session.WorldEpoch, slot => slot == Slot
        ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));
    private static M2Packet Shot(int type, int index)
    {
        var body = new byte[23]; BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Slot, index, 1).bits);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), (short)type);
        return M2PacketReader.Read(M2ContractsTests.Packet(PacketTypes.ProjectileNew, body, Slot), true).Packet!;
    }
    private BusinessRuleResult Sentry(int type, int index) => contexts.Evaluate(Shot(type, index), session, actor, false)
        .Single(x => x.RuleId == M3CombatContexts.SentryBudgetRuleId);
    private static Projectile Entity(int type, int index)
    {
        var entity = Main.projectile[index]; entity.SetDefaults(type); entity.owner = Slot;
        entity.key = new ProjectileKey(Slot, index + 20, 1); Projectile.keyToIndex[Slot, index + 20] = index;
        entity.active = true; return entity;
    }

    [Test]
    public void ActualSentryDefaultsSameTickCandidatesAndExistingUpdatesHaveEffectivePositiveContext()
    {
        var probe = new Projectile(); probe.SetDefaults(ProjectileID.FrostHydra);
        Assert.That(probe.sentry, Is.True);
        var first = Sentry(probe.type, 3); var second = Sentry(probe.type, 4);
        Assert.That(first.Verdict, Is.EqualTo(Verdict.Pass)); Assert.That(second.Verdict, Is.EqualTo(Verdict.Pass));
        var replacement = Sentry(probe.type, 5);
        Assert.That(replacement.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(replacement.Facts["hardBudgetEnforced"], Is.EqualTo("false"));
        Entity(probe.type, 0); Entity(probe.type, 1); Tick();
        Assert.That(Sentry(probe.type, 20).Verdict, Is.EqualTo(Verdict.Pass), "An existing turret sync adds zero cost.");
        actor.TPlayer.maxTurrets = 1;
        Assert.That(Sentry(probe.type, 20).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void ActualVanillaSentryReplacementIsOwnerOnlyAndEliminatesOldestAfterSpawn()
    {
        actor.TPlayer.maxTurrets = 1;
        var first = Entity(ProjectileID.FrostHydra, 0); first.timeLeft = 100;
        var second = Entity(ProjectileID.FrostHydra, 1); second.timeLeft = 200;
        actor.TPlayer.UpdateMaxTurrets();
        Assert.That(first.active && second.active, Is.True, "Dedicated server's myPlayer255 is not the remote turret owner.");
        Main.myPlayer = Slot;
        Assert.That(first.WipableTurret, Is.True);
        actor.TPlayer.UpdateMaxTurrets();
        Assert.That(first.active, Is.False); Assert.That(second.active, Is.True);
        TestContext.Out.WriteLine("Native owner-only UpdateMaxTurrets: legitimate over-budget intermediate pair reduced by oldest timeLeft; server invocation did not replace remote owner's sentries.");
    }

    [Test]
    public void NativeBobberRecallAcceptsMultipleExistingBobbersAndSetsTheirReturnStates()
    {
        var pole = actor.TPlayer.inventory[0]; pole.SetDefaults(ItemID.WoodFishingPole);
        var bobbers = Enumerable.Range(0, 5).Select(i => Entity(pole.shoot, i)).ToArray();
        foreach (var bobber in bobbers) { Assert.That(bobber.bobber, Is.True); bobber.ai[0] = 0; }
        Main.myPlayer = Slot;
        actor.TPlayer.ItemCheck_PullFishingBobbers(pole);
        Assert.That(bobbers.All(x => x.ai[0] == 1f && x.netUpdate2), Is.True);
        Main.myPlayer = 255; Tick();
        var result = contexts.Evaluate(Shot(pole.shoot, 20), session, actor, false).Single(x => x.RuleId == ProjectileRules.FishingRuleId);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass)); Assert.That(result.Facts["activeBobbers"], Is.EqualTo("5"));
        TestContext.Out.WriteLine("Native ItemCheck_PullFishingBobbers processes all five existing bobbers; this tests legitimate recall/synchronization, not a claim that one cast naturally creates five.");
    }
}
