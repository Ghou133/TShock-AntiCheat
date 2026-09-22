using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Fresh owned C05 room and permission lifecycle. Reuses the passive M16
/// storage witnesses; grants and policy changes are setup, never a detection premise.</summary>
public sealed partial class GameplayScaffold
{
    private string? m17SortDeniedRegion;
    private bool m17SortPrepared;
    private bool m17SortPendingAdded;
    private bool m17SortPreviousRegionProtection;
    private int m17SortPolicyRevision;
    private string m17SortPolicyAction = "unprepared";
    private void PrepareM17ContainerSort(string[] names)
    {
        Require(!m17SortPrepared, "Prepare C05 sorting only once in its owned run.");
        PrepareM16ItemStructure(names);
        var actor = m16ItemActor!; var chest = m16ItemChest!;
        // This isolated acceptance run explicitly exercises native chest protection. Its
        // default config can be false; a denied region alone then cannot revoke access.
        m17SortPreviousRegionProtection = TShock.Config.Settings.RegionProtectChests;
        TShock.Config.Settings.RegionProtectChests = true;
        Require(actor.HasBuildPermission(chest.x, chest.y, false), "The initial protected chest must remain normally accessible.");
        Require(chest.maxItems == 40 && chest.item.All(item => item.IsAir) && actor.TPlayer.inventory[10].IsAir,
            "Fresh 40-slot chest and empty source slot required; existing assets are not erased.");
        void Grant(int slot, int type, int stack, byte prefix = 0)
        { var item = chest.item[slot]; item.SetDefaults(type); item.Prefix(prefix); item.stack = stack; NetMessage.SendData(32, number: chest.index, number2: slot); }
        Grant(2, ItemID.Wood, 7000); Grant(17, ItemID.Wood, 6000);
        Grant(33, ItemID.WoodenSword, 1, PrefixID.Legendary); Grant(30, ItemID.WoodenSword, 1); Grant(23, ItemID.Gel, 300);
        actor.TPlayer.inventory[10].SetDefaults(ItemID.Wood); actor.TPlayer.inventory[10].stack = 7;
        actor.PlayerData.CopyCharacter(actor);
        NetMessage.SendData(5, actor.Index, number: actor.Index, number2: 10);
        m17SortPrepared = true;
        m17SortPolicyRevision = 1; m17SortPolicyAction = "prepare";
        Record("m17-container-sort-fixture", new { actor = actor.Name, chest = chest.index, sourceSlot = 10, sourceStack = 7,
            oneTimeArtificialGrant = true, clientSortInvoked = false, productHealthOrPermissionsBypassed = false,
            previousRegionProtectChests = m17SortPreviousRegionProtection, regionProtectChests = TShock.Config.Settings.RegionProtectChests,
            policyPreparation = "enable native chest protection in this owned process only; not persisted to config" });
        WriteM17ContainerSortState();
    }
    private void SetM17ContainerSortPolicy(string[] arguments)
    {
        Require(m17SortPrepared && arguments.Length == 1 && arguments[0] is "deny" or "restore", "Use qa_m17_sort_policy deny|restore after preparation.");
        var chest = m16ItemChest!;
        if (arguments[0] == "deny")
        {
            Require(m17SortDeniedRegion is null && TShock.Config.Settings.RegionProtectChests, "A fresh native chest-protection policy transition is required.");
            string name = "m17_sort_" + Guid.NewGuid().ToString("N");
            Require(TShock.Regions.AddRegion(chest.x, chest.y, 2, 2, name, "m17-owned-fixture", Main.worldID.ToString(), 2000000), "Owned deny region creation failed.");
            m17SortDeniedRegion = name;
            Require(!m16ItemActor!.HasBuildPermission(chest.x, chest.y, false), "Native effective permission must actually be denied.");
        }
        else
        {
            Require(m17SortDeniedRegion is not null && TShock.Regions.GetRegionByName(m17SortDeniedRegion) is not null, "Only the fixture-owned deny region can be restored.");
            Require(TShock.Regions.DeleteRegion(m17SortDeniedRegion!), "Owned region removal failed."); m17SortDeniedRegion = null;
            Require(m16ItemActor!.HasBuildPermission(chest.x, chest.y, false), "Actual normal chest permission must return.");
        }
        m17SortPolicyRevision++; m17SortPolicyAction = arguments[0];
        Record("m17-container-sort-policy", new { action = arguments[0], chest = chest.index, region = m17SortDeniedRegion,
            effectiveAllowed = m16ItemActor!.HasBuildPermission(chest.x, chest.y, false), ownership = "single fixture-created region only" });
        WriteM17ContainerSortState();
    }
    private void PrepareM17PendingSort()
    {
        Require(m17SortPrepared && !m17SortPendingAdded && m16ItemChest!.item[21].IsAir, "Only one explicit new pending-sort fixture grant into empty slot21 is allowed.");
        m17SortPendingAdded = true;
        var item = m16ItemChest!.item[21]; item.SetDefaults(ItemID.Wood); item.stack = 500;
        NetMessage.SendData(32, number: m16ItemChest.index, number2: 21);
        Record("m17-container-sort-pending-fixture", new { chest = m16ItemChest.index, slot = 21, type = item.type, stack = item.stack,
            oneTimeArtificialGrant = true, rollbackOrRefund = false });
        WriteM17ContainerSortState();
    }
    private void WriteM17ContainerSortState()
    {
        Require(m17SortPrepared, "Prepare the C05 sorting fixture first."); WriteM16ItemStructureState();
        var actor = m16ItemActor!; var chest = m16ItemChest!;
        WriteM16ItemSnapshotFile(Path.Combine(output!, "m17-container-sort-policy-latest.json"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow,
            actor = actor.Name, chest = chest.index, actualNativeChest = actor.TPlayer.chest, actualTShockChest = actor.ActiveChest,
            allowed = actor.HasBuildPermission(chest.x, chest.y, false), regionProtectChests = TShock.Config.Settings.RegionProtectChests,
            previousRegionProtectChests = m17SortPreviousRegionProtection, denyRegion = m17SortDeniedRegion,
            policyRevision = m17SortPolicyRevision, policyAction = m17SortPolicyAction, preparationOnly = true }, jsonOptions));
    }
}
