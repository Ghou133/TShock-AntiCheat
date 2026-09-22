using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum ItemLocation { Inventory, WorldDrop, Container }
public sealed record ItemObservation(int NetId, int Stack, int Prefix, int Slot, ItemLocation Location);
public sealed record ItemTypeDefinition(int CanonicalId, int MaxStack, ImmutableHashSet<int> AllowedPrefixes,
    bool PrefixRulesComplete);
/// <summary>Definitions are indexed by wire net ID, including the target's supported negative net IDs.</summary>
public sealed record VersionedItemCatalog(string RuntimeFingerprint, string DataVersion, int MinimumNetId,
    int ItemIdExclusiveMax, int PrefixExclusiveMax, ImmutableDictionary<int, ItemTypeDefinition> Definitions,
    bool IdDomainComplete);
public sealed record ItemRuleContext(RuleInputContext Input, int SlotCount, bool InitialSynchronization,
    bool TypeZeroIsClear, bool ZeroStackIsClear, bool AuthorizedItemMutation = false);

/// <summary>
/// Independent predicates informed by target GetDataHandlers.HandlePlayerSlot/HandleChestItem and
/// Bouncer.OnItemDrop. No source body is copied. Unknown provenance never freezes a normal item.
/// </summary>
public static class InventoryRules
{
    public const string RuleId = "B1.ItemStructure";
    public static BusinessRuleResult Evaluate(ItemObservation item, ItemRuleContext context, VersionedItemCatalog catalog)
    {
        var facts = RuleResults.Facts(("netId", item.NetId), ("stack", item.Stack), ("prefix", item.Prefix),
            ("slot", item.Slot), ("location", item.Location), ("dataVersion", catalog.DataVersion));
        if (!context.Input.ParserComplete) return RuleResults.Unknown(RuleId, "incomplete-item-packet", facts);
        if (!context.Input.VersionMatched || catalog.RuntimeFingerprint != context.Input.RuntimeFingerprint ||
            string.IsNullOrWhiteSpace(catalog.DataVersion)) return RuleResults.Unknown(RuleId, "item-data-version-unverified", facts);
        if (context.SlotCount <= 0) return RuleResults.Unknown(RuleId, "slot-domain-unavailable", facts);
        if (item.Slot < 0 || item.Slot >= context.SlotCount) return RuleResults.Block(RuleId, "item-slot-out-of-range", facts);
        // Air and version-audited zero-stack clear packets can carry fields that are ignored by the game.
        if (item.NetId == 0)
            return context.TypeZeroIsClear ? RuleResults.Pass(RuleId, "normal-air-clear", facts)
                : RuleResults.Unknown(RuleId, "air-clear-semantics-unverified", facts);
        if (item.Stack == 0 && context.ZeroStackIsClear) return RuleResults.Pass(RuleId, "normal-zero-stack-clear", facts);
        if (!catalog.IdDomainComplete || catalog.MinimumNetId > 0 || catalog.ItemIdExclusiveMax <= 0 || catalog.PrefixExclusiveMax <= 0)
            return RuleResults.Unknown(RuleId, "item-domain-incomplete", facts);
        if (item.NetId < catalog.MinimumNetId || item.NetId >= catalog.ItemIdExclusiveMax)
            return RuleResults.Block(RuleId, "item-id-outside-version-domain", facts);
        if (item.Prefix < 0 || item.Prefix >= catalog.PrefixExclusiveMax)
            return RuleResults.Block(RuleId, "prefix-outside-version-domain", facts);
        if (!catalog.Definitions.TryGetValue(item.NetId, out var definition) || definition.MaxStack <= 0)
            return RuleResults.Unknown(RuleId, "item-definition-unavailable", facts);
        if (context.AuthorizedItemMutation) return RuleResults.Pass(RuleId, "scoped-authorized-item-mutation", facts);
        if (item.Stack <= 0 || item.Stack > definition.MaxStack)
        {
            if (context.InitialSynchronization) return RuleResults.Unknown(RuleId, "initial-sync-item-state-not-attributed", facts);
            return RuleResults.Candidate(RuleId, "stack-outside-item-definition", context.Input,
                facts.Add("maximumStack", definition.MaxStack.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (!definition.PrefixRulesComplete) return RuleResults.Unknown(RuleId, "prefix-combination-table-incomplete", facts);
        if (!definition.AllowedPrefixes.Contains(item.Prefix))
        {
            if (context.InitialSynchronization) return RuleResults.Unknown(RuleId, "initial-sync-prefix-not-attributed", facts);
            return RuleResults.Candidate(RuleId, "prefix-not-applicable-to-item", context.Input, facts);
        }
        return RuleResults.Pass(RuleId, "valid-item-structure", facts);
    }
}
