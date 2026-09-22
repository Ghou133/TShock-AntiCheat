using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M18LockHealthContextTests
{
    private const int Slot = 19;
    private Player oldPlayer = null!;
    private RemoteClient oldClient = null!;
    private int oldMode, oldLocal, oldWorld;
    private bool oldSsc, oldDedServ;
    private TSPlayer actor = null!;
    private SessionKey session;
    private M18LockHealthContext context = null!;
    private int hurtSendAttempts;
    private int lastHurtDamage;
    private bool lastHurtPvp;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot];
        oldClient = Netplay.Clients[Slot];
        oldMode = Main.netMode;
        oldLocal = Main.myPlayer;
        oldWorld = Main.worldID;
        oldSsc = Main.ServerSideCharacter;
        oldDedServ = Main.dedServ;

        Main.netMode = 2;
        Main.dedServ = true;
        Main.myPlayer = 255;
        Main.ServerSideCharacter = true;
        Main.player[Slot] = new Player
        {
            whoAmI = Slot,
            active = true,
            statLife = 100,
            statLifeMax = 100,
            statLifeMax2 = 100,
        };
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        actor = new TSPlayer(Slot)
        {
            IsLoggedIn = true,
            HasSentInventory = true,
            ReceivedInfo = true,
            Account = new UserAccount { ID = 19019, Name = "m18-lock-health" },
            Group = new Group("m18-lock-health-normal"),
        };
        session = new(Guid.NewGuid(), 19, Slot, 1);
        context = new(TimeProvider.System, TargetRuntime.Fingerprint, new()
        {
            Enabled = true,
            WindowTicks = 3,
            ResponseWindowTicks = 1,
            RequiredPairs = 3,
            PendingCapacity = 4,
        });
        context.Install(Lookup);
        context.Tick(session.WorldEpoch);
        hurtSendAttempts = 0;
        lastHurtDamage = 0;
        lastHurtPvp = false;
        HookEvents.Terraria.NetMessage.SendPlayerHurt += HurtSink;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendPlayerHurt -= HurtSink;
        context.Dispose();
        Main.player[Slot] = oldPlayer;
        Netplay.Clients[Slot] = oldClient;
        Main.netMode = oldMode;
        Main.myPlayer = oldLocal;
        Main.dedServ = oldDedServ;
        Main.ServerSideCharacter = oldSsc;
        Main.ActiveWorldFileData.WorldId = oldWorld;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, actor.Account!.ID, false, DateTimeOffset.UtcNow), actor)
        : (null, null);

    private void HurtSink(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
    {
        hurtSendAttempts++;
        lastHurtDamage = args.damage;
        lastHurtPvp = args.pvp;
        args.ContinueExecution = false;
    }

    private M18LockHealthDecision Pair(bool output = true, int damage = 1, bool pvp = false,
        bool cancelled = false, int lifeAfterHurt = 99, int lifeSync = 100)
    {
        context.Tick(session.WorldEpoch);
        actor.TPlayer.statLife = lifeAfterHurt;
        if (output)
            NetMessage.SendPlayerHurt(Slot, PlayerDeathReason.ByOther(0), damage, -1, false, pvp, 0);
        return context.ObserveLifeSync(new(M4VitalKind.Life, Slot, lifeSync, 100), session, actor, cancelled);
    }

    [Test]
    public void NativeSendPlayerHurtAttemptAndFullLifePacketFormThreeBoundedPairsThenKickOnce()
    {
        var first = Pair();
        var second = Pair();
        var third = Pair();

        Assert.Multiple(() =>
        {
            Assert.That(hurtSendAttempts, Is.EqualTo(3));
            Assert.That(first.PairMatched, Is.True);
            Assert.That(first.Kick, Is.False);
            Assert.That(second.PairCount, Is.EqualTo(2));
            Assert.That(second.Kick, Is.False);
            Assert.That(third.PairCount, Is.EqualTo(3));
            Assert.That(third.Kick, Is.True);
            Assert.That(third.RuleResult!.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(third.RuleResult.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(third.RuleResult.PredicateSatisfied, Is.False);
            Assert.That(third.RuleResult.PrerequisitesComplete, Is.False);
        });
        TestContext.Out.WriteLine("Native SendPlayerHurt -> SSC full-life pairs: 3 matched, one TestLab service kick request, no sanction predicate.");
    }

    [Test]
    public void ActualServerPlayerHurtProducerCanReduceLifeWithoutEmittingClientHurtOutputOnServer()
    {
        actor.TPlayer.statLife = 100;
        double loss = actor.TPlayer.Hurt(PlayerDeathReason.ByOther(0), 1, 0, quiet: true, dodgeable: false);

        Assert.Multiple(() =>
        {
            Assert.That(loss, Is.GreaterThan(0));
            Assert.That(actor.TPlayer.statLife, Is.LessThan(100));
            Assert.That(hurtSendAttempts, Is.Zero);
        });
        TestContext.Out.WriteLine($"Actual server Player.Hurt(1): life 100->{actor.TPlayer.statLife}, loss={loss}, observed SendPlayerHurt attempts={hurtSendAttempts}; server Hurt does not imply a client-output boundary in this runtime mode.");
    }

    [Test]
    public void DamageShapeSynchronizationAndAlreadyCancelledInputsNeverCreateAPair()
    {
        Assert.That(Pair(damage: 2).PairMatched, Is.False);
        TestContext.Out.WriteLine($"Observed SendPlayerHurt args after damage=2: damage={lastHurtDamage}, pvp={lastHurtPvp}.");
        Assert.That(Pair(pvp: true).PairMatched, Is.False);
        Assert.That(Pair(lifeAfterHurt: 100).PairMatched, Is.False);
        Assert.That(Pair(lifeSync: 99).PairMatched, Is.False);
        Assert.That(Pair(cancelled: true).PairMatched, Is.False);

        actor.IgnoreSSCPackets = true;
        Assert.That(Pair().PairMatched, Is.False);
        actor.IgnoreSSCPackets = false;
        Main.ServerSideCharacter = false;
        Assert.That(Pair().PairMatched, Is.False);
        Main.ServerSideCharacter = true;

        Assert.That(hurtSendAttempts, Is.GreaterThanOrEqualTo(6));
        TestContext.Out.WriteLine("Non-one-point, PVP, full-life, non-full-sync, pre-cancelled, IgnoreSSC and non-SSC cases remained outside the pair queue.");
    }

    [Test]
    public void FullLifeSyncWithoutObservedServerHurtOutputIsNonActionable()
    {
        var result = context.ObserveLifeSync(
            new(M4VitalKind.Life, Slot, 100, 100), session, actor, alreadyCancelled: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.PairMatched, Is.False);
            Assert.That(result.Kick, Is.False);
            Assert.That(result.RuleResult, Is.Null);
            Assert.That(result.Reason, Is.EqualTo("lock-health-no-matching-one-damage-output"));
            Assert.That(hurtSendAttempts, Is.Zero);
        });
        TestContext.Out.WriteLine("A full-life packet without a preceding observed server SendPlayerHurt output stayed non-actionable; AntiHurt/QTR no-message behavior is not inferred.");
    }

    [Test]
    public void PairWindowExpiryExactForgetAndWorldResetPreventStateCarryover()
    {
        Assert.That(Pair().PairCount, Is.EqualTo(1));
        context.Tick(session.WorldEpoch);
        context.Tick(session.WorldEpoch);
        context.Tick(session.WorldEpoch);
        context.Tick(session.WorldEpoch);
        Assert.That(Pair().PairCount, Is.EqualTo(1), "A pair older than the bounded window must not carry the sequence.");

        context.Forget(session);
        Assert.That(Pair().PairCount, Is.EqualTo(1));
        context.ResetWorld();
        Assert.That(Pair().PairCount, Is.EqualTo(1));
        Assert.That(context.Healthy, Is.True);
        TestContext.Out.WriteLine("Lock-health queue cleanup: response/window expiry, exact session forget and world reset all reopen at pair=1.");
    }

    [Test]
    public void MissingBindingOrWrongThreadCannotTurnAStaleOutputIntoAServiceKick()
    {
        context.Forget(session);
        actor.TPlayer.statLife = 99;
        NetMessage.SendPlayerHurt(Slot, PlayerDeathReason.ByOther(0), 1, -1, false, false, 0);
        session = session with { Generation = 2 };
        var result = context.ObserveLifeSync(new(M4VitalKind.Life, Slot, 100, 100), session, actor, false);
        Assert.That(result.Kick, Is.False);
        Assert.That(result.PairMatched, Is.False);
        Assert.That(context.Healthy, Is.True);

        var outside = Task.Run(() => context.ObserveLifeSync(
            new(M4VitalKind.Life, Slot, 100, 100), session, actor, false)).GetAwaiter().GetResult();
        Assert.That(outside.Kick, Is.False);
        Assert.That(outside.PairMatched, Is.False);
        TestContext.Out.WriteLine("Stale generation and foreign-thread observations stayed non-actionable; context remained healthy.");
    }
}
