using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // Uses the existing console, account, world, path and queue checks. Supplies only a
    // one-time fixture batch; placement, storage, crafting and equipment changes stay in the GUI.
    private void PrepareM7Materials(string[] arguments)
    {
        Require(arguments.Length == 1, "Use qa_m7_materials <exact authenticated player>.");
        var player = ResolvePlayer(arguments[0]);
        Require(room is { State: "prepared" }, "Prepare the recorded isolated room first.");
        Require(!player.HasPermission(Permissions.editregion) && !player.HasPermission(Permissions.bypassssc)
            && !player.HasPermission("anticheat.bypass"), "Ordinary account without bypass is required.");
        string marker = Path.Combine(root!, "qa-m7-materials-issued.json");
        Require(!File.Exists(marker), "M7 materials already issued; existing player changes are retained.");
        var materials = new[]
        {
            (Type: (int)ItemID.PiggyBank, Stack: 1), (Type: (int)ItemID.Safe, Stack: 1),
            (Type: (int)ItemID.DefendersForge, Stack: 1), (Type: (int)ItemID.VoidVault, Stack: 1),
            (Type: (int)ItemID.WoodHelmet, Stack: 1), (Type: (int)ItemID.WoodBreastplate, Stack: 1),
            (Type: (int)ItemID.WoodGreaves, Stack: 1), (Type: (int)ItemID.CopperHelmet, Stack: 1),
            (Type: (int)ItemID.Aglet, Stack: 1), (Type: (int)ItemID.Torch, Stack: 20)
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, accountId = player.Account.ID, playerIndex = player.Index,
            materials = materials.Select(x => new { type = x.Type, stack = x.Stack }),
            note = "Isolated console setup only; no player action or natural acquisition claim. Marker written before issuing to prevent repeated supply after partial failure."
        }, jsonOptions));
        foreach (var material in materials) player.GiveItem(material.Type, material.Stack);
        Record("qa-m7-materials-issued", new { accountId = player.Account.ID, marker });
        Snapshot("qa-m7-materials-issued", true);
    }

    private static object DescribeM7EquipmentAndStorage(Player player)
    {
        object Items(Item[] items) => items.Take(60)
            .Select((item, slot) => new { slot, item.type, item.stack, item.prefix, item.favorited })
            .Where(item => item.type != 0 && item.stack != 0).ToArray();
        return new
        {
            player.CurrentLoadoutIndex, armor = Items(player.armor), dye = Items(player.dye),
            loadouts = player.Loadouts.Take(3).Select((loadout, index) => new
            { index, armor = Items(loadout.Armor), dye = Items(loadout.Dye) }).ToArray(),
            effectiveArmor = Enumerable.Range(0, 10).Select(slot => new
            {
                slot, type = player.GetEffectiveArmor(slot).type,
                usable = player.IsItemSlotUnlockedAndUsable(slot),
                statistics = player.UpdateEquips_CanItemGrantBenefits(slot, player.GetEffectiveArmor(slot))
            }).ToArray(),
            piggyBank = Items(player.bank.item), safe = Items(player.bank2.item),
            defendersForge = Items(player.bank3.item), voidVault = Items(player.bank4.item),
            player.statDefense,
            provenance = "accepted-server-player-state; snapshot does not establish client transaction completion, item legitimacy or responsibility"
        };
    }
}
