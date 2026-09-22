using System.Collections.Immutable;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Progression;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Net;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed partial class M4InventoryBoundaryTests
{
    private const int Slot = 12;
    private SessionKey session;
    private M3InventoryContexts contexts = null!;
    private TSPlayer actor = null!;
    private Chest[] oldChests = null!;
    private Player[] oldPlayers = null!;
    private Dictionary<Point, Chest> oldIndex = null!;
    private TShockConfig oldConfig = null!;
    private int oldMode, oldLocal, oldWidth, oldHeight;
    private readonly List<(Point Position, ITile? Tile)> oldTiles = [];
    private readonly List<BusinessRuleResult> observed = [];
    private readonly List<bool> responses = [];
    private readonly List<(int Chest, int Slot)> changes = [];
    private bool writable;

    [SetUp]
    public void SetUp()
    {
        oldChests = Main.chest; oldIndex = Chest._chestsByCoords; oldPlayers = Main.player;
        oldConfig = ServerTShock.Config; oldMode = Main.netMode; oldLocal = Main.myPlayer;
        oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY;
        Main.maxTilesX = 500; Main.maxTilesY = 500; Main.netMode = 2; Main.myPlayer = 255;
        Main.chest = new Chest[8000]; Chest._chestsByCoords = [];
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i, chest = -1 }).ToArray();
        Main.player[Slot].active = true; Main.player[Slot].position = new(320, 320);
        ServerTShock.Config = new TShockConfig();
        ServerTShock.Config.Settings.RegionProtectChests = false;
        ServerTShock.Config.Settings.RangeChecks = true;
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ActiveChest = -1,
            Group = new Group("m4-inventory-default"), Account = new UserAccount { ID = 1212, Name = "m4-inventory" } };
        session = new(Guid.NewGuid(), 1, Slot, 1); writable = true;
        oldTiles.Clear(); observed.Clear(); responses.Clear(); changes.Clear();
        contexts = new("target326-m4-inventory", id => id == Slot ? (session, actor, writable) : (null, null, false),
            (_, _, result) => { observed.Add(result); return result.Action == ControlAction.Block; });
        contexts.Install();
        HookEvents.Terraria.Net.NetManager.SendToClient += CaptureResponse;
        HookEvents.Terraria.NetMessage.SendData += CaptureSlotChange;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.Net.NetManager.SendToClient -= CaptureResponse;
        HookEvents.Terraria.NetMessage.SendData -= CaptureSlotChange;
        contexts.Dispose();
        foreach (var (position, tile) in oldTiles) Main.tile[position.X, position.Y] = tile!;
        Main.chest = oldChests; Chest._chestsByCoords = oldIndex; Main.player = oldPlayers;
        ServerTShock.Config = oldConfig; Main.netMode = oldMode; Main.myPlayer = oldLocal;
        Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
    }

    private void CaptureResponse(NetManager sender, HookEvents.Terraria.Net.NetManager.SendToClientEventArgs args)
    {
        Assert.That(args.playerId, Is.EqualTo(Slot));
        args.packet.Reader.BaseStream.Position = 5; // Actual NetModule frame: UInt16 length, byte82, UInt16 module.
        responses.Add(args.packet.Reader.ReadBoolean());
        args.packet.Recycle(); args.ContinueExecution = false; // Fixture transport sink, not a live socket.
    }

    private void CaptureSlotChange(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.msgType == 32) changes.Add((args.number, (int)args.number2));
        args.ContinueExecution = false;
    }

    private Chest ChestAt(int id, int x = 20, int y = 20, int wood = 100)
    {
        oldTiles.Add((new(x, y), Main.tile[x, y]));
        Main.tile[x, y] = new Tile { type = TileID.Containers, frameX = 0, frameY = 0 };
        var chest = Chest.CreateWorldChest(id, x, y);
        chest.item[0].SetDefaults(ItemID.Wood); chest.item[0].stack = wood;
        return chest;
    }

    private static void NativeRequest(int count, params Chest[] chests)
        => NativeRequest([new(ItemID.Wood, count)], chests);

    private static void NativeRequest(List<Recipe.RequiredItemEntry> requirements, params Chest[] chests)
    {
        var packet = CraftingRequests.NetCraftingRequestsModule.WriteRequest(requirements, chests.ToList());
        try
        {
            packet.Reader.BaseStream.Position = 5;
            new CraftingRequests.NetCraftingRequestsModule().DeserializeRequest(packet.Reader, Slot);
        }
        finally { packet.Recycle(); }
    }

    [Test]
    public void NativeSerializedNearbyCraftActuallyConsumesClosedChestAndUsesRealGate()
    {
        var near = ChestAt(0);
        Assert.That(actor.TPlayer.chest, Is.EqualTo(-1));
        NativeRequest(10, near);
        Assert.Multiple(() =>
        {
            Assert.That(near.item[0].stack, Is.EqualTo(90));
            Assert.That(actor.TPlayer.inventory.Sum(x => x.stack), Is.Zero);
            Assert.That(responses, Is.EqualTo(new[] { true }));
            Assert.That(changes, Is.EqualTo(new[] { (0, 0) }));
            Assert.That(observed, Has.Count.EqualTo(2), "Preflight and native execution each keep the actual permission hook.");
            Assert.That(observed.All(x => x.Reason == "authorized-bulk-or-nearby-operation"), Is.True);
            Assert.That(observed.All(x => x.Facts["operation"] == "NearbyCraft"), Is.True);
            Assert.That(contexts.ConfirmedContainerGrants, Is.Zero);
        });
    }

    [Test]
    public void NativeNearbyCraftConsumesAcrossTwoChestsOnlyAfterAuthorization()
    {
        var first = ChestAt(0, wood: 6); var second = ChestAt(1, 22, wood: 6);
        NativeRequest(10, first, second);
        Assert.That(first.item[0].IsAir, Is.True);
        Assert.That(second.item[0].stack, Is.EqualTo(2));
        Assert.That(responses, Is.EqualTo(new[] { true }));
        Assert.That(changes, Is.EqualTo(new[] { (0, 0), (1, 0) }));
        Assert.That(observed.All(x => x.Action == ControlAction.Pass), Is.True);
    }

    [Test]
    public void M6NativeRepeatedChestReferencesInflateCountButCannotSupplyThatConsumption()
    {
        var chest = ChestAt(0, wood: 10);
        List<Chest> targets = [chest, chest];
        var request = new Recipe.RequiredItemEntry(ItemID.Wood, 20);
        Assert.That(CraftingRequests.CountMatches(request, targets), Is.EqualTo(20),
            "Actual locked target counts the same ten wood twice before approving a request.");
        int missing = CraftingRequests.Consume(request, targets, null, fromChests: true);
        Assert.That(missing, Is.EqualTo(10), "The actual Consume cannot supply the inflated count.");
        Assert.That(chest.item[0].IsAir, Is.True);
        Assert.That(changes, Is.EqualTo(new[] { (0, 0) }));
        Assert.That(responses, Is.Empty, "This is the count/consume counterexample, not an asserted client duplication.");
    }

    [Test]
    public void M6SerializedRepeatedChestCannotApproveOrPartiallyConsumeInsufficientMaterials()
    {
        var chest = ChestAt(0, wood: 10);
        NativeRequest(20, chest, chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(10));
        Assert.That(responses, Is.EqualTo(new[] { false }));
        Assert.That(changes, Is.Empty);
        Assert.That(contexts.DuplicateCraftTargetsRemoved, Is.EqualTo(1));
        Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
    }

    [Test]
    public void M6RepeatedTargetWithEnoughMaterialsPreservesNativeSuccessAndSingleConsumption()
    {
        var chest = ChestAt(0, wood: 10);
        NativeRequest(6, chest, chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(4));
        Assert.That(responses, Is.EqualTo(new[] { true }));
        Assert.That(changes, Is.EqualTo(new[] { (0, 0) }));
        Assert.That(contexts.DuplicateCraftTargetsRemoved, Is.EqualTo(1));
        Assert.That(observed.All(x => x.Action == ControlAction.Pass), Is.True);
    }

    [Test]
    public void M6RepeatedTargetsAcrossTwoChestsKeepFirstOccurrenceOrderAndNativePermissions()
    {
        var first = ChestAt(0, wood: 6); var second = ChestAt(1, 22, wood: 6);
        NativeRequest(10, first, second, first, second);
        Assert.That(first.item[0].IsAir, Is.True);
        Assert.That(second.item[0].stack, Is.EqualTo(2));
        Assert.That(responses, Is.EqualTo(new[] { true }));
        Assert.That(changes, Is.EqualTo(new[] { (0, 0), (1, 0) }));
        Assert.That(contexts.DuplicateCraftTargetsRemoved, Is.EqualTo(2));
        Assert.That(contexts.ConfirmedContainerGrants, Is.Zero);
    }

    [Test]
    public void M6NormalizationCannotAuthorizeRepeatedLockedOrRemoteTargets()
    {
        var locked = ChestAt(0, wood: 10); var far = ChestAt(1, 400, 400, wood: 10);
        Main.tile[locked.x, locked.y].frameX = 72;
        NativeRequest(10, locked, locked, far, far);
        Assert.That(locked.item[0].stack, Is.EqualTo(10));
        Assert.That(far.item[0].stack, Is.EqualTo(10));
        Assert.That(responses, Is.EqualTo(new[] { false }));
        Assert.That(changes, Is.Empty);
        Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
    }

    [Test]
    public void M6OneRequestNormalizationIsBoundedAndDoesNotRetainChestOrSessionState()
    {
        var chest = ChestAt(0, wood: 100);
        NativeRequest(1, Enumerable.Repeat(chest, M6CraftingContainerSafety.MaximumTargets + 1).ToArray());
        Assert.That(chest.item[0].stack, Is.EqualTo(100));
        Assert.That(responses, Is.EqualTo(new[] { false }));
        Assert.That(contexts.OversizedCraftTargetListsRejected, Is.EqualTo(1));
        Assert.That(observed, Is.Empty);
        NativeRequest(10, chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(90));
        Assert.That(responses, Is.EqualTo(new[] { false, true }));
        Assert.That(contexts.DuplicateCraftTargetsRemoved, Is.Zero);
    }

    [Test]
    public void M6CumulativeDuplicateRequirementsCannotPartiallyConsumeTheSameQuantityTwice()
    {
        var chest = ChestAt(0, wood: 20);
        NativeRequest([new(ItemID.Wood, 15), new(ItemID.Wood, 15)], chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(20));
        Assert.That(responses, Is.EqualTo(new[] { false }));
        Assert.That(changes, Is.Empty);
        Assert.That(contexts.InfeasibleCraftRequestsRejected, Is.EqualTo(1));
        Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
    }

    [Test]
    public void M6SufficientDuplicateRequirementsRemainSeparateNativeConsumes()
    {
        var chest = ChestAt(0, wood: 20);
        NativeRequest([new(ItemID.Wood, 10), new(ItemID.Wood, 5)], chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(5));
        Assert.That(responses, Is.EqualTo(new[] { true }));
        Assert.That(changes, Is.EqualTo(new[] { (0, 0), (0, 0) }), "Do not deduplicate or merge the ingredient sequence.");
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void M6OverlappingRecipeGroupFollowsNativeSlotOrderWithoutOptimisticReallocation(bool alternativeFirst, bool approved)
    {
        int oldNext = RecipeGroup.nextRecipeGroupIndex;
        var group = new RecipeGroup("ItemName.Wood", ItemID.Wood, ItemID.BorealWood).Register();
        try
        {
            var chest = ChestAt(0, wood: 6);
            chest.item[1].SetDefaults(ItemID.BorealWood); chest.item[1].stack = 6;
            if (alternativeFirst) (chest.item[0], chest.item[1]) = (chest.item[1], chest.item[0]);
            NativeRequest([new(group, 8), new(ItemID.Wood, 4)], chest);
            Assert.That(responses, Is.EqualTo(new[] { approved }));
            if (approved)
            {
                Assert.That(chest.item[0].IsAir && chest.item[1].IsAir, Is.True);
                Assert.That(changes, Is.EqualTo(new[] { (0, 0), (0, 1), (0, 1) }));
            }
            else
            {
                Assert.That(chest.item[0].stack, Is.EqualTo(6)); Assert.That(chest.item[1].stack, Is.EqualTo(6));
                Assert.That(changes, Is.Empty, "Each individual CountMatches passes, but ordered joint consumption cannot complete.");
            }
            Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
        }
        finally { RecipeGroup.recipeGroups.Remove(group.RegisteredId); RecipeGroup.nextRecipeGroupIndex = oldNext; }
    }

    [Test]
    public void M6NativeExecutionWorkIsReservedEvenWhenAllIngredientsAreInTheFirstChest()
    {
        var chests = Enumerable.Range(0, 110).Select(i => new Chest(i, maxItems: 200)).ToArray();
        foreach (var chest in chests) for (int slot = 0; slot < chest.maxItems; slot++) chest.item[slot] = new Item();
        chests[0].item[0].SetDefaults(ItemID.Wood); chests[0].item[0].stack = 100;
        var requirements = Enumerable.Range(0, 3).Select(_ => new Recipe.RequiredItemEntry(ItemID.Wood, 1)).ToArray();
        var result = M6CraftingContainerSafety.CheckFeasibility(requirements, chests, out int steps);
        Assert.That(result, Is.EqualTo(M6CraftFeasibility.BudgetExceeded));
        Assert.That(steps, Is.Zero, "Reserve native CountMatches+Consume worst-case cost before allocating the material snapshot.");
        Assert.That(chests[0].item[0].stack, Is.EqualTo(100));
    }

    [Test]
    public void NativeCoreLockedAndOtherPlayerOccupiedChestsRemainUnconsumed()
    {
        var chest = ChestAt(0);
        Main.tile[chest.x, chest.y].frameX = 72; // Target's actual locked golden chest frame.
        NativeRequest(10, chest);
        Assert.That(responses.Last(), Is.False);
        Main.tile[chest.x, chest.y].frameX = 0;
        Main.player[Slot + 1].active = true; Main.player[Slot + 1].chest = 0;
        NativeRequest(10, chest);
        Assert.Multiple(() =>
        {
            Assert.That(chest.item[0].stack, Is.EqualTo(100));
            Assert.That(responses, Is.EqualTo(new[] { false, false }));
            Assert.That(changes, Is.Empty);
            Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
        });
    }

    [Test]
    public void RemoteAndRevokedRequestsStopBeforeNativeMaterialConsumption()
    {
        var far = ChestAt(0, 400, 400); var near = ChestAt(1);
        NativeRequest(10, far);
        Assert.That(observed.Single().Verdict, Is.EqualTo(Verdict.UnsafeInput));
        writable = false;
        NativeRequest(10, near);
        Assert.Multiple(() =>
        {
            Assert.That(far.item[0].stack, Is.EqualTo(100));
            Assert.That(near.item[0].stack, Is.EqualTo(100));
            Assert.That(responses, Is.EqualTo(new[] { false, false }));
            Assert.That(changes, Is.Empty);
            Assert.That(observed, Has.Count.EqualTo(1));
        });
    }

    [TestCase(0)][TestCase(1)][TestCase(2)][TestCase(3)]
    public void NativePersonalStorageConsumptionStaysLocalWithoutWorldChestLease(int bank)
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        var player = actor.TPlayer;
        var chest = new[] { player.bank, player.bank2, player.bank3, player.bank4 }[bank];
        chest.item[0].SetDefaults(ItemID.Wood); chest.item[0].stack = 100;
        Assert.That(chest.bankChest, Is.True);
        Assert.That(CraftingRequests.IsLocallyAccessible(chest), Is.True);
        int remaining = CraftingRequests.Consume(new(ItemID.Wood, 10), [chest], null, fromChests: false);
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Is.Zero);
            Assert.That(chest.item[0].stack, Is.EqualTo(90));
            Assert.That(contexts.NearbyCraftObservations, Is.Zero);
            Assert.That(contexts.ConfirmedContainerGrants, Is.Zero);
            Assert.That(changes, Is.Empty);
        });
    }

    [Test]
    public void ContinuousThreeChestSwitchesRequireFreshConfirmationAndPreserveFirstMismatchProof()
    {
        var chests = new[] { ChestAt(0), ChestAt(1, 22), ChestAt(2, 24) };
        foreach (var chest in chests)
        {
            contexts.ObserveActiveTransition(session);
            Assert.That(contexts.ObserveOpen(session, actor, chest.x, chest.y, false).Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(contexts.EvaluateContainerWrite(session, actor, chest.index, 0).Verdict, Is.EqualTo(Verdict.Unknown));
            actor.TPlayer.chest = chest.index; actor.ActiveChest = chest.index;
            Assert.That(contexts.EvaluateContainerWrite(session, actor, chest.index, 0).Action, Is.EqualTo(ControlAction.Pass));
        }
        Assert.That(contexts.ConfirmedContainerGrants, Is.EqualTo(3));
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 0, 0).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        actor.TPlayer.chest = -2; actor.ActiveChest = -1; // Personal piggy storage cannot keep a world lease valid.
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 0, 0).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.TPlayer.chest = 2; actor.ActiveChest = 2;
        Assert.That(contexts.EvaluateContainerWrite(session, actor, 0, 0).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void ActualThreeLoadoutSwitchesSharedFavoritesVanityAndLockedStorageRemainCompatible()
    {
        var boots = new Item(); boots.SetDefaults(ItemID.HermesBoots); boots.favorited = true;
        var cloud = new Item(); cloud.SetDefaults(ItemID.CloudinaBottle);
        contexts.AddItemDefinition(boots); contexts.AddItemDefinition(cloud); contexts.CompleteItemCatalog();
        actor.TPlayer.Loadouts[1].Armor[3] = boots;
        Assert.That(actor.TPlayer.GetEffectiveArmor(3).type, Is.EqualTo(ItemID.HermesBoots));
        for (int loadout = 0; loadout < 3; loadout++)
        {
            actor.TPlayer.TrySwitchingLoadout(loadout);
            Assert.That(actor.TPlayer.CurrentLoadoutIndex, Is.EqualTo(loadout));
            Assert.That(contexts.EvaluateEquipment(session, actor, 4, cloud.type).Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(contexts.EvaluateEquipment(session, actor, 13, boots.type).Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(contexts.EvaluateEquipment(session, actor, 8, cloud.type).Reason,
                Is.EqualTo("stored-item-in-disabled-slot-has-no-verified-effect"));
        }
    }
}
