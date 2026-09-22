using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

// Reuses the established isolated native world, actual serialization and transport sink.
public sealed partial class M4InventoryBoundaryTests
{
    private void EnableFinalCraftGuard()
    {
        contexts.Dispose();
        contexts = new(TargetRuntime.Fingerprint, id => id == Slot ? (session, actor, writable) : (null, null, false),
            (_, _, result) => { observed.Add(result); return result.Action == ControlAction.Block; });
        contexts.Install();
        Assert.That(contexts.FinalCraftGuardHealthy, Is.True, contexts.FinalCraftGuardFailureType);
    }

    [Test]
    public void M9FinalNativeBoundaryAllowsRealOrdinaryAndRepeatedRequirementConsumption()
    {
        EnableFinalCraftGuard();
        var first = ChestAt(0, wood: 8); var second = ChestAt(1, 22, wood: 8);
        NativeRequest([new(ItemID.Wood, 6), new(ItemID.Wood, 6)], first, second);
        Assert.That(first.item[0].IsAir, Is.True);
        Assert.That(second.item[0].stack, Is.EqualTo(4));
        Assert.That(responses, Is.EqualTo(new[] { true }));
        Assert.That(changes, Is.EqualTo(new[] { (0, 0), (0, 0), (1, 0) }));
        Assert.That(contexts.FinalCraftChecks, Is.EqualTo(1));
        Assert.That(contexts.FinalCraftRejections, Is.Zero);
        Assert.That(contexts.LastFinalCraftSimulationSteps, Is.GreaterThan(0));
        Assert.That(observed.All(x => x.Action == ControlAction.Pass), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void M9LateNativePermissionRemovalCannotPartiallyConsumeAndApprove(bool guarded)
    {
        if (guarded) EnableFinalCraftGuard();
        var first = ChestAt(0, wood: 8); var second = ChestAt(1, 22, wood: 8);
        int visits = 0;
        void LatePermission(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        {
            if (ReferenceEquals(args.chest, second) && ++visits == 2)
            { args.ContinueExecution = false; args.HookReturnValue = false; }
        }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += LatePermission;
        try { NativeRequest([new(ItemID.Wood, 6), new(ItemID.Wood, 6)], first, second); }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= LatePermission; }
        Assert.That(visits, Is.EqualTo(2));
        Assert.That(second.item[0].stack, Is.EqualTo(8), "Final core permission remains authoritative.");
        Assert.That(contexts.InfeasibleCraftRequestsRejected, Is.Zero, "M6 preflight genuinely passed.");
        if (guarded)
        {
            Assert.That(first.item[0].stack, Is.EqualTo(8));
            Assert.That(responses, Is.EqualTo(new[] { false }));
            Assert.That(changes, Is.Empty);
            Assert.That(contexts.FinalCraftChecks, Is.EqualTo(1));
            Assert.That(contexts.FinalCraftRejections, Is.EqualTo(1));
            NativeRequest(2, first);
            Assert.That(first.item[0].stack, Is.EqualTo(6));
            Assert.That(responses, Is.EqualTo(new[] { false, true }));
            Assert.That(contexts.FinalCraftChecks, Is.EqualTo(2));
            Assert.That(contexts.FinalCraftRejections, Is.EqualTo(1));
        }
        else
        {
            Assert.That(first.item[0].IsAir, Is.True);
            Assert.That(responses, Is.EqualTo(new[] { true }));
            Assert.That(changes, Is.EqualTo(new[] { (0, 0), (0, 0) }));
            TestContext.Out.WriteLine("Actual serialized target request: preflight passed with two chests; second native permission removed B; A8 was consumed for requested 6+6 and native approved=true. This controlled host callback is not a native-only client exploit.");
        }
        Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
    }

    [Test]
    public void M9FinalNativeFilterMayRemoveAChestWhenRemainingMaterialsAreSufficient()
    {
        EnableFinalCraftGuard();
        var first = ChestAt(0, wood: 20); var second = ChestAt(1, 22, wood: 8);
        int visits = 0;
        void LatePermission(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        {
            if (ReferenceEquals(args.chest, second) && ++visits == 2)
            { args.ContinueExecution = false; args.HookReturnValue = false; }
        }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += LatePermission;
        try { NativeRequest([new(ItemID.Wood, 6), new(ItemID.Wood, 6)], first, second); }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= LatePermission; }
        Assert.That(first.item[0].stack, Is.EqualTo(8));
        Assert.That(second.item[0].stack, Is.EqualTo(8));
        Assert.That(contexts.FinalCraftChecks, Is.EqualTo(1));
        Assert.That(contexts.FinalCraftRejections, Is.Zero);
        Assert.That(responses, Is.EqualTo(new[] { true }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void M9FinalCraftStopsLateSessionRevocationOrReplacement(bool replace)
    {
        EnableFinalCraftGuard();
        var chest = ChestAt(0, wood: 20);
        int visits = 0;
        void LateSession(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        {
            if (++visits != 2) return;
            if (replace) session = session with { Generation = session.Generation + 1 };
            else writable = false;
        }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += LateSession;
        try { NativeRequest(5, chest); }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= LateSession; }
        Assert.That(chest.item[0].stack, Is.EqualTo(20));
        Assert.That(changes, Is.Empty);
        Assert.That(responses, Is.EqualTo(new[] { false }));
        Assert.That(contexts.FinalCraftRejections, Is.EqualTo(1));
        writable = true; NativeRequest(2, chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(18));
        Assert.That(responses, Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void M9LaterRequestHookCanReplaceRequirementsWhenTheActualFinalSequenceIsFeasible()
    {
        EnableFinalCraftGuard(); var chest = ChestAt(0, wood: 20);
        void ReplaceRequirements(object? _, HookEvents.Terraria.GameContent.CraftingRequests.HandleRequestEventArgs args)
            => args.items = [new(ItemID.Wood, 3), new(ItemID.Wood, 4)];
        HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest += ReplaceRequirements;
        try { NativeRequest(5, chest); }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest -= ReplaceRequirements; }
        Assert.That(chest.item[0].stack, Is.EqualTo(13));
        Assert.That(responses, Is.EqualTo(new[] { true }));
        Assert.That(contexts.FinalCraftChecks, Is.EqualTo(1));
        Assert.That(contexts.FinalCraftRejections, Is.Zero);
    }

    [Test]
    public void M9FinalCraftProtectsLateRequirementMutationWithoutInferringPlayerManufacture()
    {
        EnableFinalCraftGuard(); var chest = ChestAt(0, wood: 10);
        void ReplaceRequirements(object? _, HookEvents.Terraria.GameContent.CraftingRequests.HandleRequestEventArgs args)
            => args.items = [new(ItemID.Wood, 6), new(ItemID.Wood, 6)];
        HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest += ReplaceRequirements;
        try { NativeRequest(5, chest); }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest -= ReplaceRequirements; }
        Assert.That(chest.item[0].stack, Is.EqualTo(10));
        Assert.That(responses, Is.EqualTo(new[] { false }));
        Assert.That(changes, Is.Empty);
        Assert.That(contexts.FinalCraftChecks, Is.EqualTo(1));
        Assert.That(contexts.FinalCraftRejections, Is.EqualTo(1));
        Assert.That(observed.All(x => x.Verdict != Verdict.ProvenCheat), Is.True);
    }

    [Test]
    public void M9FinalGuardFaultWithdrawsOnlyThisBoundaryAndRetainsEstablishedPreflight()
    {
        contexts.Dispose(); bool throwAtFinal = false; int finalFaults = 0, oldDomainFaults = 0;
        contexts = new(TargetRuntime.Fingerprint, id =>
        {
            if (throwAtFinal) throw new InvalidOperationException("Controlled final-binding producer fault");
            return id == Slot ? (session, actor, writable) : (null, null, false);
        }, (_, _, result) => { observed.Add(result); return result.Action == ControlAction.Block; })
        { IntegrityFault = _ => oldDomainFaults++, FinalCraftIntegrityFault = _ => finalFaults++ };
        contexts.Install(); Assert.That(contexts.FinalCraftGuardHealthy, Is.True);
        var chest = ChestAt(0, wood: 20); int visits = 0;
        void LateFault(object? _, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
        { if (++visits == 2) throwAtFinal = true; }
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += LateFault;
        try { NativeRequest(5, chest); }
        finally { HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= LateFault; throwAtFinal = false; }
        Assert.That(chest.item[0].stack, Is.EqualTo(20)); Assert.That(changes, Is.Empty);
        Assert.That(contexts.FinalCraftGuardHealthy, Is.False);
        Assert.That(finalFaults, Is.EqualTo(1)); Assert.That(oldDomainFaults, Is.Zero);
        NativeRequest([new(ItemID.Wood, 15), new(ItemID.Wood, 15)], chest);
        Assert.That(contexts.InfeasibleCraftRequestsRejected, Is.EqualTo(1));
        Assert.That(chest.item[0].stack, Is.EqualTo(20));
        NativeRequest(2, chest);
        Assert.That(chest.item[0].stack, Is.EqualTo(18));
        Assert.That(responses, Is.EqualTo(new[] { false, true }));
        Assert.That(finalFaults, Is.EqualTo(1));
    }
}
