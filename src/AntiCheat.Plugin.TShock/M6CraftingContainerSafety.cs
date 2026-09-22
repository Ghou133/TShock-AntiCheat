using Terraria;
using Terraria.ID;

namespace AntiCheat.Plugin.TShock;

/// <summary>One request's bounded target normalization, with no inventory ledger or actor verdict.</summary>
public enum M6CraftFeasibility { Feasible, InsufficientMaterials, InvalidInput, InvalidContainerState, BudgetExceeded }

public static class M6CraftingContainerSafety
{
    public const int MaximumRequirements = 15;
    public const int MaximumTargets = 8192;
    public const int MaximumSlotsPerChest = 200;
    public const int MaximumSimulationSteps = 131072;
    public const int MaximumNativeSlotVisits = 131072;

    private readonly record struct SlotBalance(int Type, int Stack);

    public static bool IsValidRequirement(Recipe.RequiredItemEntry item) => item.stack > 0 &&
        (item.IsRecipeGroup
            ? RecipeGroup.recipeGroups.ContainsKey(item.itemIdOrRecipeGroup - RecipeGroup.FakeItemIdOffset)
            : item.itemIdOrRecipeGroup > 0 && item.itemIdOrRecipeGroup < ItemID.Count);

    /// <summary>
    /// Simulates native consumption in original requirement, chest and slot order against one local
    /// snapshot. Overlapping recipe groups deliberately share the same remaining quantities. There
    /// is no ingredient deduplication, world write, refund, actor verdict or retained inventory state.
    /// Each examined native slot and each simulated slot visit spends from one fixed work budget.
    /// </summary>
    public static M6CraftFeasibility CheckFeasibility(IReadOnlyList<Recipe.RequiredItemEntry> requirements,
        IReadOnlyList<Chest> targets, out int steps)
    {
        steps = 0;
        if (requirements is null || targets is null || requirements.Count is <= 0 or > MaximumRequirements ||
            targets.Count > MaximumTargets || requirements.Any(x => !IsValidRequirement(x)))
            return M6CraftFeasibility.InvalidInput;
        var balances = new List<SlotBalance>();
        var seenItems = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var seenChests = new HashSet<Chest>(ReferenceEqualityComparer.Instance);
        var uniqueTargets = new List<Chest>();
        long slots = 0;
        foreach (var chest in targets)
        {
            if (chest is null) continue;
            if (!seenChests.Add(chest)) continue;
            if (chest.maxItems is < 0 or > MaximumSlotsPerChest || chest.item is null || chest.item.Length < chest.maxItems)
                return M6CraftFeasibility.InvalidContainerState;
            slots += chest.maxItems;
            // Native CountMatches visits every slot for every requirement and its subsequent
            // Consume may visit them again. Reserve that full upper bound separately, even when
            // this simulation finds its requested items near the front. Cheap preflight success
            // must not undercharge the native work that follows.
            if (2L * slots * requirements.Count > MaximumNativeSlotVisits)
                return M6CraftFeasibility.BudgetExceeded;
            uniqueTargets.Add(chest);
        }
        foreach (var chest in uniqueTargets)
        {
            for (int slot = 0; slot < chest.maxItems; slot++)
            {
                if (++steps > MaximumSimulationSteps) return M6CraftFeasibility.BudgetExceeded;
                if (chest.item[slot] is not { } item) return M6CraftFeasibility.InvalidContainerState;
                if (item.IsAir) continue;
                if (item.type <= 0 || item.type >= ItemID.Count || item.stack <= 0 || !seenItems.Add(item))
                    return M6CraftFeasibility.InvalidContainerState;
                balances.Add(new(item.type, item.stack));
            }
        }
        foreach (var requirement in requirements)
        {
            int remaining = requirement.stack;
            for (int slot = 0; slot < balances.Count && remaining > 0; slot++)
            {
                if (++steps > MaximumSimulationSteps) return M6CraftFeasibility.BudgetExceeded;
                var balance = balances[slot];
                if (balance.Stack == 0 || !requirement.Matches(balance.Type)) continue;
                int consume = Math.Min(balance.Stack, remaining);
                remaining -= consume;
                balances[slot] = balance with { Stack = balance.Stack - consume };
            }
            if (remaining != 0) return M6CraftFeasibility.InsufficientMaterials;
        }
        return M6CraftFeasibility.Feasible;
    }

    public static int RemoveDuplicateReferences(List<Chest> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > MaximumTargets) throw new ArgumentOutOfRangeException(nameof(targets));
        var seen = new HashSet<Chest>(ReferenceEqualityComparer.Instance);
        int write = 0;
        for (int read = 0; read < targets.Count; read++)
        {
            var chest = targets[read];
            // Null targets are already discarded by native HandleRequest. Keep one in its original
            // position so the normal filtering contract remains observable to other hooks.
            if (seen.Add(chest)) targets[write++] = chest;
        }
        int removed = targets.Count - write;
        if (removed > 0) targets.RemoveRange(write, removed);
        return removed;
    }
}
