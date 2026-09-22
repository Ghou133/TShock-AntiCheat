using System.Text.Json;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Net;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private bool m9CraftWitness, m9CraftMutationArmed;
    private int m9CraftInjected;

    private void PrepareM9Craft(string[] names)
    {
        Require(!m9CraftWitness, "M9 crafting fixture is prepared once per isolated run.");
        PrepareM6Safety(names);
        HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest += M9LateCraftMutation;
        m9CraftWitness = true;
        WriteM9CraftState();
    }

    private void ArmM9LateCraftMutation()
    {
        Require(m9CraftWitness && !m9CraftMutationArmed, "Prepare the isolated crafting witness first.");
        m9CraftMutationArmed = true;
        WriteM9CraftState();
    }

    // Controlled host callback, registered after the product preflight. This is explicitly not
    // a claim that an ordinary client can mutate server hook arguments or bypass shimmer gates.
    private void M9LateCraftMutation(object? _, HookEvents.Terraria.GameContent.CraftingRequests.HandleRequestEventArgs args)
    {
        if (!m9CraftMutationArmed || !args.ContinueExecution || args.whoAmI != m6Actor ||
            args.chests.Count != 1 || !ReferenceEquals(args.chests[0], m6CraftChest)) return;
        m9CraftMutationArmed = false; m9CraftInjected++;
        int halfPlusOne = m6CraftChest!.item.Where(x => x.type == ItemID.Wood).Sum(x => x.stack) / 2 + 1;
        args.items = [new(ItemID.Wood, halfPlusOne), new(ItemID.Wood, halfPlusOne)];
        Record("m9-late-craft-host-mutation", new { args.whoAmI, requirements = new[] { halfPlusOne, halfPlusOne },
            source = "owned-scaffold-callback-after-product-preflight-before-native-body", accountVerdict = false });
    }

    private void WriteM9CraftState()
    {
        Require(m9CraftWitness && m6CraftChest is not null, "Prepare the isolated M9 craft fixture first.");
        var chest = m6CraftChest!; var actor = TShock.Players[m6Actor]; var plugin = M5Plugin();
        Require(ReferenceEquals(Main.chest[chest.index], chest), "Owned chest identity changed.");
        var inventory = plugin.GetType().GetField("_inventory", PrivateM5)!.GetValue(plugin)!;
        object? Counter(string name) => inventory.GetType().GetProperty(name)?.GetValue(inventory);
        var payload = new { utc = DateTimeOffset.UtcNow, chest = new { id = chest.index, chest.x, chest.y,
            items = chest.item.Take(chest.maxItems).Select((item, slot) => new { slot, item.type, item.stack, item.prefix }).ToArray() },
            module = NetManager.Instance.GetId<CraftingRequests.NetCraftingRequestsModule>(),
            actor = new { actor.Index, actor.Name, account = actor.Account?.ID, actor.IsLoggedIn,
                group = actor.Group.Name, bypass = actor.HasPermission("anticheat.bypass"),
                sscBypass = actor.HasPermission(Permissions.bypassssc), nativeChest = actor.TPlayer.chest },
            finalGuardHealthy = Counter("FinalCraftGuardHealthy"), finalChecks = Counter("FinalCraftChecks"),
            finalRejections = Counter("FinalCraftRejections"), finalSteps = Counter("LastFinalCraftSimulationSteps"),
            earlyRejections = Counter("InfeasibleCraftRequestsRejected"),
            m9CraftMutationArmed, m9CraftInjected,
            consumeEntries = m6SafetyEffects.ToArray(), effectsDropped = m6SafetyEffectsDropped,
            support = "real native post-filter quantity safety; controlled late host callback; no manufacture attribution" };
        File.WriteAllText(Path.Combine(output!, "m9-craft-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void DisposeM9CraftWitness()
    {
        HookEvents.Terraria.GameContent.CraftingRequests.HandleRequest -= M9LateCraftMutation;
        m9CraftWitness = false; m9CraftMutationArmed = false;
    }
}
