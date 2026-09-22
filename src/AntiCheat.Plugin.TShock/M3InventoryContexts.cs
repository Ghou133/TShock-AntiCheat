using System.Collections.Immutable;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Net;
using Terraria.UI;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Target-326 container grants and effective equipment observations. The stored chest reference is
/// an authorization object, never a statement about the origin or legality of its contents.
/// </summary>
public sealed class M3InventoryContexts : IDisposable
{
    private sealed record Lease(SessionKey Session, Chest Target, int Id, int X, int Y,
        long Revision, long ExpiresAt, bool Confirmed);
    private readonly string fingerprint;
    private readonly Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current;
    private readonly Func<SessionKey, TSPlayer, BusinessRuleResult, bool> processResult;
    private readonly Lease?[] leases = new Lease?[256];
    private readonly Dictionary<int, Item> accessories = [];
    private long tick;
    private long revision;
    private bool installed;
    private bool failed;
    private bool accessoryCatalogComplete;
    private ILHook? finalCraftHook;
    private bool finalCraftFailed;
    private sealed record PendingCraft(SessionKey Session, TSPlayer Actor, Player Player,
        List<Recipe.RequiredItemEntry> Requirements, List<Chest> Targets, int Thread, long Tick);
    private readonly PendingCraft?[] pendingCrafts = new PendingCraft?[256];
    private const long LeaseTicks = 1800;

    public M3InventoryContexts(string fingerprint,
        Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current,
        Func<SessionKey, TSPlayer, BusinessRuleResult, bool> processResult)
    {
        this.fingerprint = fingerprint;
        this.current = current;
        this.processResult = processResult;
        LoadoutTransactions = new(fingerprint, current);
        EquipmentExecutions = new(fingerprint, current, LoadoutTransactions);
    }

    public long QuickStackObservations { get; private set; }
    public long NearbyCraftObservations { get; private set; }
    public long ConfirmedContainerGrants { get; private set; }
    public long DuplicateCraftTargetsRemoved { get; private set; }
    public long OversizedCraftTargetListsRejected { get; private set; }
    public long InfeasibleCraftRequestsRejected { get; private set; }
    public long CraftSimulationBudgetRejections { get; private set; }
    public int LastCraftSimulationSteps { get; private set; }
    public bool FinalCraftGuardHealthy => finalCraftHook is not null && !failed && !finalCraftFailed;
    public string? FinalCraftGuardFailureType { get; private set; }
    public Action<Exception>? FinalCraftIntegrityFault { get; set; }
    public long FinalCraftChecks { get; private set; }
    public long FinalCraftRejections { get; private set; }
    public int LastFinalCraftSimulationSteps { get; private set; }
    public Action<Exception>? IntegrityFault { get; set; }
    public M7LoadoutTransactionObserver LoadoutTransactions { get; }
    public M8EquipmentExecutionObserver EquipmentExecutions { get; }
    public void ObserveLoadout(SessionKey session, TSPlayer actor, GetDataEventArgs args)
    {
        EquipmentExecutions.ObserveIncoming(session, args);
        LoadoutTransactions.ObserveIncoming(session, actor, args);
    }

    public void Install()
    {
        if (installed || failed) return;
        installed = true;
        OTAPI.Hooks.Chest.QuickStack += OnQuickStack;
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest += OnNearbyCraft;
        HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest += OnCraftRequest;
        LoadoutTransactions.Install();
        EquipmentExecutions.Install();
        InstallFinalCraftGuard();
    }

    public void Dispose()
    {
        if (!installed) return;
        OTAPI.Hooks.Chest.QuickStack -= OnQuickStack;
        HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChest -= OnNearbyCraft;
        HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest -= OnCraftRequest;
        var finalGuard = finalCraftHook; finalCraftHook = null;
        try { finalGuard?.Dispose(); }
        finally
        {
            try { LoadoutTransactions.Dispose(); }
            finally
            {
                try { EquipmentExecutions.Dispose(); }
                finally { installed = false; ResetWorld(); }
            }
        }
    }

    /// <summary>Called only during the existing bounded static netDefaults catalog construction.</summary>
    public void AddItemDefinition(Item item)
    {
        if (!accessoryCatalogComplete && item.accessory && item.type > 0 && accessories.Count < 4096)
            accessories.TryAdd(item.type, item.Clone());
    }

    public void CompleteItemCatalog() => accessoryCatalogComplete = true;

    public void Update()
    {
        LoadoutTransactions.Update();
        EquipmentExecutions.Update();
        tick++;
        // The fixed array is bounded at 256 connections; at most 16 entries are visited per update.
        for (int i = 0; i < 16; i++)
        {
            int slot = (int)((tick * 16 + i) % leases.Length);
            // A crafting scope is synchronous and must not survive a later world-update visit.
            pendingCrafts[slot] = null;
            if (leases[slot] is { } lease && (tick > lease.ExpiresAt || current(slot).Session != lease.Session))
                leases[slot] = null;
        }
    }

    public void ResetWorld() { Array.Clear(leases); Array.Clear(pendingCrafts); LoadoutTransactions.ResetWorld(); EquipmentExecutions.ResetWorld(); }
    public void Forget(SessionKey session, bool endSession = true)
    {
        LoadoutTransactions.Forget(session);
        EquipmentExecutions.Forget(session, endSession);
        if ((uint)session.Slot < pendingCrafts.Length && pendingCrafts[session.Slot]?.Session == session)
            pendingCrafts[session.Slot] = null;
        if ((uint)session.Slot < leases.Length && leases[session.Slot]?.Session == session)
            leases[session.Slot] = null;
    }

    private RuleInputContext Input(SessionKey session, bool complete, bool exceptions) =>
        new(session, fingerprint, fingerprint, true, complete, true, exceptions);

    /// <summary>
    /// Observe the actual raw GetContents request before core processing. A request does not grant a
    /// lease: core's matching chest/ActiveChest means only server acceptance. An ordered client32
    /// for that exact target must also be observed before later mismatches can prove a violation.
    /// Calling again while switching immediately invalidates the old grant, including rejected opens.
    /// </summary>
    public BusinessRuleResult ObserveOpen(SessionKey session, TSPlayer actor, int x, int y, bool alreadyCancelled)
    {
        leases[session.Slot] = null;
        int id = x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY ? Chest.FindChest(x, y) : -1;
        var chest = (uint)id < Main.chest.Length ? Main.chest[id] : null;
        bool? allowed = chest is null ? null : RegionAllowed(actor, chest);
        bool? inRange = chest is null ? null : actor.IsInRange(chest.x, chest.y);
        var result = ContainerRules.Evaluate(new(id, -1, ContainerOperation.Open),
            new(Input(session, chest is not null, true), session, session.WorldEpoch, revision, revision,
                chest is not null, chest?.maxItems ?? 0, null, false, false, allowed, inRange, [], false));
        if (!alreadyCancelled && result.Action == ControlAction.Pass && chest is not null)
            leases[session.Slot] = new(session, chest, id, chest.x, chest.y, ++revision, tick + LeaseTicks, false);
        return result;
    }

    /// <summary>Raw packet 33 is a client close/active/name transition; it never grants authority.</summary>
    public void ObserveActiveTransition(SessionKey session) => Forget(session, endSession: false);

    public BusinessRuleResult EvaluateContainerWrite(SessionKey session, TSPlayer actor, int id, int slot)
    {
        var chest = (uint)id < Main.chest.Length ? Main.chest[id] : null;
        var lease = leases[session.Slot];
        bool serverAligned = lease is not null && lease.Session == session && tick <= lease.ExpiresAt &&
            (uint)lease.Id < Main.chest.Length && ReferenceEquals(Main.chest[lease.Id], lease.Target) &&
            lease.Target.x == lease.X && lease.Target.y == lease.Y &&
            actor.TPlayer.chest == lease.Id && actor.ActiveChest == lease.Id;
        bool nativeProtocol = ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(TShockAPI.TShock) ||
            x.Plugin.GetType() == typeof(AntiCheatPlugin));
        // A normal client can keep using the previous chest after sending31B until33B arrives.
        // Its first32A must remain Unknown even when the server already openedB. A same-target32B
        // is a bounded causal acknowledgement in the ordered inbound stream; no extra UI step.
        if (serverAligned && nativeProtocol && lease is { Confirmed: false } && id == lease.Id &&
            chest is not null && (uint)slot < chest.maxItems)
        {
            lease = lease with { Confirmed = true };
            leases[session.Slot] = lease;
            ConfirmedContainerGrants++;
        }
        if (!nativeProtocol || !serverAligned && lease is { Confirmed: true })
        { leases[session.Slot] = null; lease = null; }
        bool confirmed = serverAligned && nativeProtocol && lease is { Confirmed: true };
        var result = ContainerRules.Evaluate(new(id, slot, ContainerOperation.SlotWrite),
            new(Input(session, confirmed, confirmed), session, session.WorldEpoch, lease?.Revision ?? revision,
                lease?.Revision ?? revision, chest is not null, chest?.maxItems ?? 0,
                confirmed ? lease!.Id : null, confirmed, lease is { Confirmed: false },
                chest is null ? null : RegionAllowed(actor, chest),
                chest is null ? null : actor.IsInRange(chest.x, chest.y), [], false));
        return result with
        {
            Facts = result.Facts.SetItem("producer", "raw-open+server-accepted+ordered-client-target-write")
            .SetItem("leaseTtlTicks", LeaseTicks.ToString()).SetItem("leaseConfirmed", confirmed.ToString())
            .SetItem("serverChestAligned", serverAligned.ToString()).SetItem("nativeContainerProtocol", nativeProtocol.ToString())
        };
    }

    public BusinessRuleResult EvaluateEquipment(SessionKey session, TSPlayer actor, int armorSlot, int type)
    {
        var player = actor.TPlayer;
        var effects = M5EquipmentContexts.Capture(session, player);
        var kind = armorSlot is >= 3 and <= 9 ? EquipmentSlotKind.FunctionalAccessory
            : armorSlot is >= 10 and <= 19 ? EquipmentSlotKind.Vanity : EquipmentSlotKind.Armor;
        var slots = ImmutableArray.CreateBuilder<EquipmentSlot>(20);
        bool projected = player.armor.Length >= 20 && player.Loadouts.Length == 3;
        if (projected)
            for (int i = 0; i < 20; i++)
                slots.Add(new(i, player.GetEffectiveArmor(i).type,
                    i is >= 3 and <= 9 ? EquipmentSlotKind.FunctionalAccessory : i >= 10 ? EquipmentSlotKind.Vanity : EquipmentSlotKind.Armor,
                    player.CurrentLoadoutIndex));
        var usable = effects.Slots.Where(x => x.Slot >= 3 && x.Usable).Select(x => x.Slot).ToImmutableHashSet();
        // A stored expert-only accessory has no functional effect in a non-expert world. It must
        // not become the other half of a conflict when a valid accessory is being changed.
        var effectSlots = effects.Slots.Where(x => x.AppliesFunctionalMethod).Select(x => x.Slot).ToImmutableHashSet();
        for (int index = 0; index < slots.Count; index++)
            if (slots[index].Kind == EquipmentSlotKind.FunctionalAccessory && !effectSlots.Contains(slots[index].SlotId))
                slots[index] = slots[index] with { ItemId = 0 };
        var conflicts = ImmutableHashSet.CreateBuilder<EquipmentConflict>();
        bool modeled = accessoryCatalogComplete;
        if (accessories.TryGetValue(type, out var proposed))
            foreach (var other in slots.Where(x => x.Kind == EquipmentSlotKind.FunctionalAccessory && x.ItemId != 0 && x.SlotId != armorSlot))
            {
                if (!accessories.TryGetValue(other.ItemId, out var existing)) { modeled = false; continue; }
                if (!ItemSlot.CanEquipBothAccessories(proposed, existing, false)) conflicts.Add(new(type, other.ItemId));
            }
        // A raw slot update has no end-of-multi-slot-switch marker. Do not convert an intermediate
        // duplicate into a ban. This specific missing premise is separate from item acquisition.
        var result = EquipmentRules.Evaluate(new(armorSlot, type, kind, player.CurrentLoadoutIndex),
            new(Input(session, projected, false), session, player.CurrentLoadoutIndex, slots.ToImmutable(), usable,
                true, false, !actor.HasSentInventory || actor.IgnoreSSCPackets, false,
                EffectiveSnapshotComplete: projected && effects.Complete,
                ProposedSlotAffectsEffectiveEquipment: proposed is null || !proposed.expertOnly || Main.expertMode),
            new(fingerprint, "runtime326-effective-armor+CanEquipBothAccessories", accessories.Keys.ToImmutableHashSet(),
                conflicts.ToImmutable(), accessoryCatalogComplete, modeled));
        return result with
        {
            Facts = result.Facts.SetItem("producer", "GetEffectiveArmor+IsItemSlotUnlockedAndUsable+CanEquipBothAccessories")
            .SetItem("hardConflictMissingPremise", "no-atomic-end-marker-for-multi-slot-equipment-transition")
            .SetItem("lockedSlotEffect", "target-UpdateEquips-skips-unusable-slots")
            .SetItem("effectProjectionComplete", effects.Complete.ToString())
            .SetItem("functionalEffectSlots", string.Join(",", effects.Slots.Where(x => x.AppliesFunctionalMethod).Select(x => x.Slot)))
            .SetItem("statisticsEffectSlots", string.Join(",", effects.Slots.Where(x => x.GrantsStatistics).Select(x => x.Slot)))
            .SetItem("prefixMethodSlots", string.Join(",", effects.Slots.Where(x => x.AppliesPrefixMethod).Select(x => x.Slot)))
            .SetItem("clientTransitionComplete", effects.AtomicClientTransitionComplete.ToString())
            .SetItem("nativeEquipmentCalculationObserved", (EquipmentExecutions.Capture(session)?.NativeCalculationReturned == true).ToString())
            .SetItem("nativeCalculationIsClientCompletion", "False")
        };
    }

    private static bool RegionAllowed(TSPlayer actor, Chest chest) =>
        !ServerTShock.Config.Settings.RegionProtectChests || actor.HasBuildPermission(chest.x, chest.y);

    private BusinessRuleResult EvaluateBulk(SessionKey session, TSPlayer actor, Chest? chest, int id, ContainerOperation operation)
    {
        bool bound = chest is not null && (uint)id < Main.chest.Length && ReferenceEquals(Main.chest[id], chest);
        // Exact target calculation from NearbyChests uses top-left player position and chest center,
        // not the ordinary open-chest rectangle. An already-open chest is also a native craft source.
        bool? range = chest is null ? null : (operation == ContainerOperation.NearbyCraft && actor.TPlayer.chest == id) ||
            Vector2.DistanceSquared(actor.TPlayer.position, new Vector2(chest.x * 16 + 16, chest.y * 16 + 16)) <= Chest.chestStackRange * Chest.chestStackRange;
        var result = ContainerRules.Evaluate(new(id, -1, operation),
            new(Input(session, bound, true), session, session.WorldEpoch, tick, tick, bound, chest?.maxItems ?? 0,
                null, false, false, chest is null ? null : RegionAllowed(actor, chest), range,
                bound ? ImmutableHashSet.Create(id) : [], bound));
        return result with
        {
            Facts = result.Facts.SetItem("producer", operation == ContainerOperation.QuickStack
            ? "OTAPI.Chest.QuickStack-before-transfer" : "CraftingRequests.CanCraftFromChest-before-consumption")
        };
    }

    private void OnQuickStack(object? sender, OTAPI.Hooks.Chest.QuickStackEventArgs args)
    {
        if (failed) return;
        SessionKey session;
        TSPlayer actor;
        BusinessRuleResult result;
        try
        {
            var binding = current(args.PlayerId);
            if (binding.Session is not { } key || binding.Player is not { } player) return;
            if (!binding.CanWrite) { args.Result = OTAPI.HookResult.Cancel; return; }
            session = key; actor = player;
            QuickStackObservations++;
            var chest = (uint)args.ChestIndex < Main.chest.Length ? Main.chest[args.ChestIndex] : null;
            result = EvaluateBulk(session, actor, chest, args.ChestIndex, ContainerOperation.QuickStack);
        }
        catch (Exception exception) { Fail(exception); return; }
        // Core cancellation remains cancellation; it is not upgraded to cheating.
        if (processResult(session, actor, result)) args.Result = OTAPI.HookResult.Cancel;
    }

    private void OnNearbyCraft(object? sender, HookEvents.Terraria.GameContent.CraftingRequests.CanCraftFromChestEventArgs args)
    {
        if (failed) return;
        SessionKey session;
        TSPlayer actor;
        BusinessRuleResult result;
        try
        {
            var binding = current(args.whoAmI);
            if (binding.Session is not { } key || binding.Player is not { } player) return;
            if (!binding.CanWrite) { args.ContinueExecution = false; args.HookReturnValue = false; return; }
            session = key; actor = player;
            NearbyCraftObservations++;
            int id = args.chest?.index ?? -1;
            result = EvaluateBulk(session, actor, args.chest, id, ContainerOperation.NearbyCraft);
        }
        catch (Exception exception) { Fail(exception); return; }
        if (processResult(session, actor, result)) { args.ContinueExecution = false; args.HookReturnValue = false; }
    }

    // The target counts every reference before consuming. Repeating the same world chest can
    // therefore approve a request whose actual inventory is insufficient. Canonicalize only the
    // references in this one request; the native permission, sufficiency and consumption paths
    // continue unchanged. This is never evidence against the holder of the items.
    private void OnCraftRequest(object? sender, HookEvents.Terraria.GameContent.CraftingRequests.HandleRequestEventArgs args)
    {
        if (failed || !args.ContinueExecution || Main.netMode != 2) return;
        if ((uint)args.whoAmI < pendingCrafts.Length) pendingCrafts[args.whoAmI] = null;
        LastCraftSimulationSteps = 0;
        try
        {
            var binding = current(args.whoAmI);
            if (binding.Session is not { } session || binding.Player is not { } actor) return;
            if (!binding.CanWrite)
            {
                args.ContinueExecution = false;
                NetManager.Instance.SendToClient(CraftingRequests.NetCraftingRequestsModule.WriteResponse(false), args.whoAmI);
                return;
            }
            // There are at most 8000 world chest identities in the locked target. The slightly
            // larger fixed bound admits every possible unique native target without unbounded
            // allocation here. This callback follows deserialization; it is not an allocation guard
            // for the earlier network reader.
            if (args.chests is null || args.chests.Count > M6CraftingContainerSafety.MaximumTargets)
            {
                OversizedCraftTargetListsRejected++;
                args.ContinueExecution = false;
                NetManager.Instance.SendToClient(CraftingRequests.NetCraftingRequestsModule.WriteResponse(false), args.whoAmI);
                return;
            }
            DuplicateCraftTargetsRemoved += M6CraftingContainerSafety.RemoveDuplicateReferences(args.chests);
            // Reuse the actual hook and native lock/occupancy checks before taking the local
            // quantity snapshot. Native HandleRequest checks again before it consumes, so a later
            // cancellation is never restored by this preflight.
            args.chests.RemoveAll(chest => chest is null || !CraftingRequests.CanCraftFromChest(chest, args.whoAmI));
            var again = current(args.whoAmI);
            int steps = 0;
            var feasibility = again.Session != session || !ReferenceEquals(again.Player, actor) || !again.CanWrite
                ? M6CraftFeasibility.InvalidContainerState
                : M6CraftingContainerSafety.CheckFeasibility(args.items, args.chests, out steps);
            LastCraftSimulationSteps = steps;
            if (feasibility != M6CraftFeasibility.Feasible)
            {
                InfeasibleCraftRequestsRejected++;
                if (feasibility == M6CraftFeasibility.BudgetExceeded) CraftSimulationBudgetRejections++;
                args.ContinueExecution = false;
                NetManager.Instance.SendToClient(CraftingRequests.NetCraftingRequestsModule.WriteResponse(false), args.whoAmI);
            }
            else if ((uint)args.whoAmI < pendingCrafts.Length && finalCraftHook is not null)
                pendingCrafts[args.whoAmI] = new(session, actor, actor.TPlayer, args.items, args.chests, Environment.CurrentManagedThreadId, tick);
        }
        catch (Exception exception) { args.ContinueExecution = false; Fail(exception); }
    }

    private void InstallFinalCraftGuard()
    {
        // Existing preflight remains available on historical test fixtures. Only the locked native
        // method body is eligible for the final, post-permission consumption boundary.
        if (fingerprint != TargetRuntime.Fingerprint) return;
        try
        {
            if (typeof(CraftingRequests).Assembly.GetName().Version != new Version(1, 4, 5, 8))
                throw new NotSupportedException("Final crafting guard requires the audited target.");
            var method = typeof(CraftingRequests).GetMethod("mfwh_HandleRequest",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(int), typeof(List<Recipe.RequiredItemEntry>), typeof(List<Chest>)])
                ?? throw new MissingMethodException("CraftingRequests.mfwh_HandleRequest");
            finalCraftHook = new(method, il =>
            {
                var cursor = new ILCursor(il);
                bool IsTargetFilter(Instruction instruction) =>
                    (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Call) &&
                    instruction.Operand is MethodReference { Name: "RemoveAll", DeclaringType: GenericInstanceType list } &&
                    list.ElementType.FullName == "System.Collections.Generic.List`1" &&
                    list.GenericArguments.Count == 1 && list.GenericArguments[0].FullName == typeof(Chest).FullName;
                if (il.Body.Instructions.Count(IsTargetFilter) != 1 ||
                    !cursor.TryGotoNext(MoveType.After, IsTargetFilter, instruction => instruction.OpCode == OpCodes.Pop))
                    throw new InvalidOperationException("Final native chest-filter boundary changed.");
                // Native RemoveAll has finished invoking permission hooks. No earlier preflight
                // can certify this resulting target list; re-check immediately before CountMatches.
                var resume = cursor.DefineLabel();
                cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1); cursor.Emit(OpCodes.Ldarg_2);
                cursor.EmitDelegate<Func<int, List<Recipe.RequiredItemEntry>, List<Chest>, bool>>(CheckFinalCraft);
                cursor.Emit(OpCodes.Brtrue, resume); cursor.Emit(OpCodes.Ret); cursor.MarkLabel(resume);
            });
        }
        catch (Exception error)
        {
            FailFinalCraft(error);
        }
    }

    private bool CheckFinalCraft(int slot, List<Recipe.RequiredItemEntry> requirements, List<Chest> targets)
    {
        if (failed || finalCraftFailed || Main.netMode != 2) return true;
        try
        {
            var binding = current(slot);
            var pending = (uint)slot < pendingCrafts.Length ? pendingCrafts[slot] : null;
            if ((uint)slot < pendingCrafts.Length) pendingCrafts[slot] = null;
            bool sourceMatched = pending is not null && ReferenceEquals(pending.Requirements, requirements) &&
                ReferenceEquals(pending.Targets, targets) && pending.Thread == Environment.CurrentManagedThreadId && pending.Tick == tick;
            if (FinalCraftChecks < long.MaxValue) FinalCraftChecks++;
            LastFinalCraftSimulationSteps = 0;
            int steps = 0;
            bool sourceLost = sourceMatched && (pending!.Session != binding.Session ||
                !ReferenceEquals(pending.Actor, binding.Player) || !binding.CanWrite ||
                !ReferenceEquals(Main.player[slot], pending.Player));
            // Quantity safety does not require acquisition history, a client completion marker,
            // or attributing an unbound host call. A captured request still may not cross sessions.
            var feasibility = sourceLost || binding.Session is not null && !binding.CanWrite
                ? M6CraftFeasibility.InvalidContainerState
                : M6CraftingContainerSafety.CheckFeasibility(requirements, targets, out steps);
            LastFinalCraftSimulationSteps = steps;
            // The bounded snapshot is immediately consumed by the native method on this stack.
            // It is neither an acquisition certificate nor an account-cheating verdict.
            if (feasibility == M6CraftFeasibility.Feasible) return true;
            if (FinalCraftRejections < long.MaxValue) FinalCraftRejections++;
            NetManager.Instance.SendToClient(CraftingRequests.NetCraftingRequestsModule.WriteResponse(false), slot);
            return false;
        }
        catch (Exception error)
        {
            // Do not partially consume after a failed safety preflight. No refund or replay.
            FailFinalCraft(error);
            return false;
        }
    }

    private void FailFinalCraft(Exception error)
    {
        if (finalCraftFailed) return;
        finalCraftFailed = true; FinalCraftGuardFailureType = error.GetType().FullName;
        Array.Clear(pendingCrafts);
        // The final hook has a separate fault domain from established container/quick-stack guards.
        try { FinalCraftIntegrityFault?.Invoke(error); } catch { }
    }

    private void Fail(Exception exception)
    {
        if (failed) return;
        failed = true;
        try { Dispose(); } catch (Exception cleanup) { exception = new AggregateException(exception, cleanup); }
        IntegrityFault?.Invoke(exception);
    }
}
