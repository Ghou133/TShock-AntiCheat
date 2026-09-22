using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;
using Subject = AntiCheat.Plugin.TShock.M2BusinessAdapter;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M17ProgressionWorldFactTests
{
    private const int Slot = 17;
    private static readonly (Type Type, string Name)[] Fields =
    [
        (typeof(Main), "hardMode"), (typeof(Main), "drunkWorld"), (typeof(Main), "getGoodWorld"),
        (typeof(Main), "zenithWorld"), (typeof(Main), "tenthAnniversaryWorld"), (typeof(Main), "remixWorld"),
        (typeof(NPC), "downedSlimeKing"), (typeof(NPC), "downedBoss1"), (typeof(NPC), "downedBoss3"),
        (typeof(NPC), "downedDeerclops"), (typeof(NPC), "downedQueenSlime"), (typeof(NPC), "downedMechBoss1"),
        (typeof(NPC), "downedMechBoss2"), (typeof(NPC), "downedMechBoss3"), (typeof(NPC), "downedPlantBoss"),
        (typeof(NPC), "downedGolemBoss"), (typeof(NPC), "downedAncientCultist"), (typeof(NPC), "downedMoonlord"),
        (typeof(NPC), "downedFishron"), (typeof(NPC), "downedEmpressOfLight")
    ];
    private readonly List<(FieldInfo Field, object? Value)> saved = [];
    private Player oldPlayer = null!, oldLocalPlayer = null!;
    private Terraria.Utilities.UnifiedRandom oldRandom = null!;
    private int oldMode, oldLocal, oldWorld;
    private Subject adapter = null!;
    private TSPlayer actor = null!;
    private SessionKey session;

    [SetUp] public void Setup()
    {
        saved.Clear();
        foreach (var (type, name) in Fields)
        { var field = type.GetField(name)!; saved.Add((field, field.GetValue(null))); field.SetValue(null, false); }
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWorld = Main.worldID;
        oldPlayer = Main.player[Slot]; oldLocalPlayer = Main.player[255]; oldRandom = Main.rand;
        Main.netMode = 2; Main.myPlayer = 255; Main.rand = new(0x173E01);
        Main.player[255] = new Player { whoAmI = 255 };
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.player[Slot].inventory[0].SetDefaults(ItemID.RedPotion);
        Main.drunkWorld = true;
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true,
            Account = new UserAccount { ID = 1717, Name = "e01-normal-input" }, Group = new Group("e01-ordinary") };
        session = new(Guid.NewGuid(), 7, Slot, 1);
        adapter = new(TargetRuntime.Fingerprint, Path.Combine(AppContext.BaseDirectory, "data", "progression"));
        Settle();
    }

    [TearDown] public void Cleanup()
    {
        Main.player[Slot] = oldPlayer; Main.player[255] = oldLocalPlayer; Main.rand = oldRandom;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.ActiveWorldFileData.WorldId = oldWorld;
        foreach (var (field, value) in saved) field.SetValue(null, value);
    }

    // Exercise the exact existing fact producer without constructing unrelated item tables.
    private void Capture() => typeof(Subject).GetMethod("CaptureWorldFacts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(adapter, [session.WorldEpoch]);
    private void Settle() { Capture(); Capture(); Capture(); }
    private BusinessRuleResult Use(bool syncing = false, SessionKey? key = null)
    {
        actor.IgnoreSSCPackets = syncing;
        byte[] body = new byte[14]; body[0] = Slot; body[1] = 32;
        return adapter.Evaluate(new(M2PacketKind.PlayerUpdate, body), key ?? session, actor, _ => (null, null), false)
            .Single(result => result.RuleId == "PG-NAT-002");
    }

    [Test] public void StableNativeSpecialWorldAllowsItsExactNormalCounterexample()
    { Assert.That(Use().Reason, Is.EqualTo("progress-condition-false")); Assert.That(Use().Action, Is.EqualTo(ControlAction.Pass)); }

    [Test] public void SameTickNativeWorldChangeWithdrawsTheOldSettledSnapshot()
    {
        Assert.That(Use().Action, Is.EqualTo(ControlAction.Pass));
        Main.drunkWorld = false;
        Assert.That(Use().Reason, Is.EqualTo("world-snapshot-incomplete-stale-or-transitioning"));
        Assert.That(Use().Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test] public void ObservedTransitionCannotBecomeSettledByChangingBackBeforeUpdate()
    {
        Main.drunkWorld = false; _ = Use(); Main.drunkWorld = true;
        Assert.That(Use().Reason, Is.EqualTo("world-snapshot-incomplete-stale-or-transitioning"));
        Settle(); Assert.That(Use().Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test] public void WorldAndSessionEpochChangesCannotUseThePreviousSnapshot()
    {
        Main.ActiveWorldFileData.WorldId++;
        Assert.That(Use().Reason, Is.EqualTo("world-snapshot-incomplete-stale-or-transitioning"));
        Main.ActiveWorldFileData.WorldId = oldWorld;
        Assert.That(Use(key: session with { WorldEpoch = 8 }).Reason, Is.EqualTo("world-snapshot-incomplete-stale-or-transitioning"));
    }

    [Test] public void PassiveSynchronizationStillPassesDuringAWorldTransition()
    { Main.drunkWorld = false; Assert.That(Use(syncing: true).Reason, Is.EqualTo("passive-sync-or-authorized-action")); }

    [Test] public void ResetWorldRequiresANewCaptureAndTheExistingSettlingWindow()
    {
        adapter.ResetWorld();
        Assert.That(Use().Reason, Is.EqualTo("world-snapshot-incomplete-stale-or-transitioning"));
        Capture(); Assert.That(Use().Verdict, Is.EqualTo(Verdict.Unknown));
        Capture(); Assert.That(Use().Verdict, Is.EqualTo(Verdict.Unknown));
        Capture(); Assert.That(Use().Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test] public void RecapturedOrdinaryWorldDoesNotInheritTheOldSpecialSeedExemption()
    {
        Main.drunkWorld = false; _ = Use(); Settle();
        Assert.That(Use().Reason, Is.EqualTo("predicate-or-exceptions-unknown"));
        Assert.That(Use().Verdict, Is.EqualTo(Verdict.Unknown), "A current world does not prove acquisition or imported-asset exceptions.");
    }

    public static IEnumerable<int> FieldIndices() => Enumerable.Range(0, Fields.Length);
    [TestCaseSource(nameof(FieldIndices))]
    public void EveryCapturedNativeFlagParticipatesInImmediateValidation(int index)
    {
        var (type, name) = Fields[index]; var field = type.GetField(name)!;
        field.SetValue(null, !(bool)field.GetValue(null)!);
        Assert.That(Use().Reason, Is.EqualTo("world-snapshot-incomplete-stale-or-transitioning"), type.Name + "." + name);
    }
}
