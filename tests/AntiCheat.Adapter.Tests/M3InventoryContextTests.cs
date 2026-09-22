using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M3InventoryContextTests
{
    private const int Slot = 12;
    private readonly SessionKey session = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), 1, Slot, 1);
    private M3InventoryContexts contexts = null!;
    private TSPlayer actor = null!;
    private Chest[] previousChests = null!;
    private Dictionary<Point, Chest> previousChestIndex = null!;
    private Player previousPlayer = null!;
    private TSPlayer previousTsPlayer = null!;
    private int previousWidth, previousHeight;
    private TShockConfig previousConfig = null!;
    private SessionKey? currentSession;
    private bool writable;
    private readonly List<BusinessRuleResult> observed = [];

    [SetUp]
    public void SetUp()
    {
        previousChests = Main.chest; previousPlayer = Main.player[Slot]; previousTsPlayer = ServerTShock.Players[Slot];
        previousWidth = Main.maxTilesX; previousHeight = Main.maxTilesY;
        previousConfig = ServerTShock.Config; ServerTShock.Config = new TShockConfig();
        Main.chest = new Chest[8000]; previousChestIndex = Chest._chestsByCoords; Chest._chestsByCoords = [];
        Main.maxTilesX = 500; Main.maxTilesY = 500;
        Main.player[Slot] = new Player { whoAmI = Slot, position = new Vector2(320, 320), chest = -1 };
        actor = new TSPlayer(Slot) { Account = new UserAccount { ID = 1212, Name = "ordinary-inventory-context-test" },
            IsLoggedIn = true, HasSentInventory = true, ActiveChest = -1 };
        ServerTShock.Players[Slot] = actor;
        // These scenarios exercise the local object/lease invariant. Region permission has its own
        // live TShock hook and remains configurable; no player bypass permission is assigned.
        ServerTShock.Config.Settings.RegionProtectChests = false; ServerTShock.Config.Settings.RangeChecks = true;
        currentSession = session; writable = true; observed.Clear();
        contexts = new("target326-adapter-test", i => i == Slot ? (currentSession, actor, writable) : (null, null, false),
            (_, _, result) => { observed.Add(result); return result.Action == ControlAction.Block; });
    }

    [TearDown]
    public void TearDown()
    {
        contexts?.Dispose(); Main.chest = previousChests; Main.player[Slot] = previousPlayer; ServerTShock.Players[Slot] = previousTsPlayer;
        Chest._chestsByCoords = previousChestIndex;
        Main.maxTilesX = previousWidth; Main.maxTilesY = previousHeight;
        ServerTShock.Config = previousConfig;
    }

    private static Chest PutChest(int id, int x = 20, int y = 20)
    {
        return Chest.CreateWorldChest(id, x, y);
    }

    private void OpenAndConfirm(int id)
    {
        var chest = Main.chest[id];
        Assert.That(contexts.ObserveOpen(session, actor, chest.x, chest.y, false).Action, Is.EqualTo(ControlAction.Pass));
        actor.TPlayer.chest = id; actor.ActiveChest = id;
        Assert.That(contexts.EvaluateContainerWrite(session, actor, id, 0).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void OpenRequestAloneCannotGrantLeaseAndConfirmedGrantProvesOnlyWrongTarget()
    {
        PutChest(0); PutChest(1, 22);
        contexts.ObserveOpen(session, actor, 20, 20, false);
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.TPlayer.chest = 0; actor.ActiveChest = 0;
        var legal = contexts.EvaluateContainerWrite(session, actor, 0, 0);
        Assert.That(legal.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(contexts.ConfirmedContainerGrants, Is.EqualTo(1));
        var mismatch = contexts.EvaluateContainerWrite(session, actor, 1, 0);
        Assert.That(mismatch.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(mismatch.Reason, Is.EqualTo("stable-container-write-target-mismatch"));
        Assert.That(mismatch.Facts["producer"], Is.EqualTo("raw-open+server-accepted+ordered-client-target-write"));
    }

    [Test]
    public void CloseSwitchCoreCancellationAndChestReplacementDoNotReuseGrant()
    {
        PutChest(0); PutChest(1, 22);
        OpenAndConfirm(0);
        contexts.ObserveActiveTransition(session);
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        OpenAndConfirm(0);
        contexts.ObserveOpen(session, actor, 22, 20, false);
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 0, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.TPlayer.chest = 1; actor.ActiveChest = 1;
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Action, Is.EqualTo(ControlAction.Pass));
        contexts.ObserveOpen(session, actor, 20, 20, true);
        actor.TPlayer.chest = 0; actor.ActiveChest = 0;
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        OpenAndConfirm(0);
        PutChest(0); // Same id and coordinates, different world object.
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void ExpiryAndSessionGenerationNeverCarryContainerEvidenceToAnotherActor()
    {
        PutChest(0); PutChest(1, 22); OpenAndConfirm(0);
        var next = session with { Generation = session.Generation + 1 };
        Assert.That(contexts.EvaluateContainerWrite(next, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        OpenAndConfirm(0);
        for (int i = 0; i <= 1800; i++) contexts.Update();
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        OpenAndConfirm(0); contexts.ResetWorld();
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void UnknownContainerPluginWithdrawsOnlyTheLeaseAndCannotReviveItAfterRemoval()
    {
        PutChest(0); PutChest(1, 22); OpenAndConfirm(0);
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnknownContainerPlugin();
        var container = new PluginContainer(plugin);
        plugins.Add(container);
        try
        {
            var unknown = contexts.EvaluateContainerWrite(session, actor, 1, 0);
            Assert.That(unknown.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(unknown.Facts["nativeContainerProtocol"], Is.EqualTo("False"));
        }
        finally { plugins.Remove(container); }
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        OpenAndConfirm(0);
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, 0).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    private sealed class UnknownContainerPlugin() : TerrariaPlugin(null!)
    {
        public override void Initialize() { }
    }

    [Test]
    public void RealQuickStackHookEstablishesDistinctBulkAuthorityWithoutOpeningChest()
    {
        PutChest(0); contexts.Install();
        Assert.That(OTAPI.Hooks.Chest.InvokeQuickStack(Slot, new Item(), 0), Is.True);
        Assert.That(observed.Single().Reason, Is.EqualTo("authorized-bulk-or-nearby-operation"));
        Assert.That(observed.Single().Facts["operation"], Is.EqualTo("QuickStack"));
        Assert.That(actor.TPlayer.chest, Is.EqualTo(-1));
        PutChest(1, 400, 400);
        Assert.That(OTAPI.Hooks.Chest.InvokeQuickStack(Slot, new Item(), 1), Is.False);
        Assert.That(observed.Last().Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(observed.Last().Reason, Is.EqualTo("container-interaction-out-of-range"));
        Assert.That(contexts.QuickStackObservations, Is.EqualTo(2));
    }

    [Test]
    public void RealQuickStackHookPreservesPriorCancellationAndRevocationStopsTransfer()
    {
        PutChest(0);
        void CoreCancel(object? _, OTAPI.Hooks.Chest.QuickStackEventArgs args) => args.Result = OTAPI.HookResult.Cancel;
        OTAPI.Hooks.Chest.QuickStack += CoreCancel;
        try
        {
            contexts.Install();
            Assert.That(OTAPI.Hooks.Chest.InvokeQuickStack(Slot, new Item(), 0), Is.False);
            Assert.That(observed.Single().Verdict, Is.EqualTo(Verdict.Pass), "Cancellation itself is not cheating.");
            writable = false;
            Assert.That(OTAPI.Hooks.Chest.InvokeQuickStack(Slot, new Item(), 0), Is.False);
            Assert.That(observed.Count, Is.EqualTo(1), "Revoked session must stop before any new rule action.");
        }
        finally { OTAPI.Hooks.Chest.QuickStack -= CoreCancel; }
    }

    [Test]
    public void RealCraftHookUsesItsReturnValueToStopRemoteConsumptionAndAllowsNearbyAuthority()
    {
        var near = PutChest(0); var far = PutChest(1, 400, 400);
        // A prior target hook supplies the original return without requiring a running world tile map.
        // This exercises the real generated wrapper and cancellation return contract, not TCP.
        void AcceptedTarget(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        { args.ContinueExecution = false; args.HookReturnValue = true; }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += AcceptedTarget;
        try
        {
            contexts.Install();
            Assert.That(CraftingRequests.CanCraftFromChest(near, Slot), Is.True);
            Assert.That(observed.Single().Reason, Is.EqualTo("authorized-bulk-or-nearby-operation"));
            Assert.That(CraftingRequests.CanCraftFromChest(far, Slot), Is.False);
            Assert.That(observed.Last().Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(contexts.NearbyCraftObservations, Is.EqualTo(2));
            writable = false;
            Assert.That(CraftingRequests.CanCraftFromChest(near, Slot), Is.False);
        }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= AcceptedTarget; }
    }

    [Test]
    public void TargetEffectiveEquipmentAndUnlockMethodsEstablishLegalContextWithoutItemHistory()
    {
        var boots = new Item(); boots.netDefaults(ItemID.HermesBoots);
        var cloud = new Item(); cloud.netDefaults(ItemID.CloudinaBottle);
        contexts.AddItemDefinition(boots); contexts.AddItemDefinition(cloud); contexts.CompleteItemCatalog();
        actor.TPlayer.armor[3] = boots;
        var distinct = contexts.EvaluateEquipment(session, actor, 4, cloud.type);
        Assert.That(distinct.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(distinct.Reason, Is.EqualTo("valid-active-accessory-combination"));
        Assert.That(contexts.EvaluateEquipment(session, actor, 13, boots.type).Action, Is.EqualTo(ControlAction.Pass));
        var duplicate = contexts.EvaluateEquipment(session, actor, 4, boots.type);
        Assert.That(duplicate.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(duplicate.Facts["hardConflictMissingPremise"], Does.Contain("multi-slot-equipment-transition"));
        actor.TPlayer.extraAccessory = false;
        var storedInLocked = contexts.EvaluateEquipment(session, actor, 8, cloud.type);
        Assert.That(storedInLocked.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(storedInLocked.Reason, Is.EqualTo("stored-item-in-disabled-slot-has-no-verified-effect"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealQuickStackProducerFaultPreservesCoreResultAndDetachesOnce(bool coreCancels)
    {
        PutChest(0);
        int calls = 0, faults = 0;
        contexts.Dispose();
        contexts = new("target326-adapter-test", _ => { calls++; throw new InvalidOperationException("fixture source fault"); },
            (_, _, _) => throw new AssertionException("No result may be fabricated from a producer fault"))
            { IntegrityFault = _ => faults++ };
        void CoreResult(object? _, OTAPI.Hooks.Chest.QuickStackEventArgs args)
        { if (coreCancels) args.Result = OTAPI.HookResult.Cancel; }
        OTAPI.Hooks.Chest.QuickStack += CoreResult;
        try
        {
            contexts.Install();
            Assert.That(OTAPI.Hooks.Chest.InvokeQuickStack(Slot, new Item(), 0), Is.EqualTo(!coreCancels));
            coreCancels = !coreCancels;
            Assert.That(OTAPI.Hooks.Chest.InvokeQuickStack(Slot, new Item(), 0), Is.EqualTo(!coreCancels));
            Assert.That(calls, Is.EqualTo(1)); Assert.That(faults, Is.EqualTo(1));
        }
        finally { OTAPI.Hooks.Chest.QuickStack -= CoreResult; }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealCraftProducerFaultPreservesCoreReturnAndDetachesOnce(bool coreAccepts)
    {
        var near = PutChest(0);
        int calls = 0, faults = 0;
        contexts.Dispose();
        contexts = new("target326-adapter-test", _ => { calls++; throw new InvalidOperationException("fixture source fault"); },
            (_, _, _) => throw new AssertionException("No result may be fabricated from a producer fault"))
            { IntegrityFault = _ => faults++ };
        void CoreResult(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        { args.ContinueExecution = false; args.HookReturnValue = coreAccepts; }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += CoreResult;
        try
        {
            contexts.Install();
            Assert.That(CraftingRequests.CanCraftFromChest(near, Slot), Is.EqualTo(coreAccepts));
            coreAccepts = !coreAccepts;
            Assert.That(CraftingRequests.CanCraftFromChest(near, Slot), Is.EqualTo(coreAccepts));
            Assert.That(calls, Is.EqualTo(1)); Assert.That(faults, Is.EqualTo(1));
        }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= CoreResult; }
    }
}
