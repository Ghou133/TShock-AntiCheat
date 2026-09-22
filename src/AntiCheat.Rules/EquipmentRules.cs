using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum EquipmentSlotKind { FunctionalAccessory, Armor, Vanity, Dye, Inventory }
public sealed record EquipmentSlot(int SlotId, int ItemId, EquipmentSlotKind Kind, int LoadoutIndex);
public readonly record struct EquipmentConflict(int FirstItemId, int SecondItemId);
public sealed record EquipmentCatalog(string RuntimeFingerprint, string DataVersion,
    ImmutableHashSet<int> AccessoryTypes, ImmutableHashSet<EquipmentConflict> Conflicts,
    bool DuplicateRuleComplete, bool ConflictTableComplete);
public sealed record EquipmentContext(RuleInputContext Input, SessionKey SnapshotSession, int ActiveLoadout,
    ImmutableArray<EquipmentSlot> ExistingSlots, ImmutableHashSet<int> UsableFunctionalSlots,
    bool UnlockContextComplete, bool TransitionInProgress, bool InitialSynchronization,
    bool FunctionalApplicationVerified, bool AuthorizedEquipmentMutation = false,
    bool EffectiveSnapshotComplete = false, bool ProposedSlotAffectsEffectiveEquipment = true);

/// <summary>Evaluates a proposed slot against one bounded, versioned active-loadout snapshot.</summary>
public static class EquipmentRules
{
    public const string ConflictRuleId = "B2.ActiveAccessoryConflict";
    public const string UnlockRuleId = "B3.AccessoryUnlock";
    public static BusinessRuleResult Evaluate(EquipmentSlot proposed, EquipmentContext context, EquipmentCatalog catalog)
    {
        var facts = RuleResults.Facts(("slot", proposed.SlotId), ("itemId", proposed.ItemId),
            ("loadout", proposed.LoadoutIndex), ("activeLoadout", context.ActiveLoadout), ("dataVersion", catalog.DataVersion));
        if (!context.Input.ParserComplete) return RuleResults.Unknown(ConflictRuleId, "equipment-packet-incomplete", facts);
        if (proposed.ItemId == 0) return RuleResults.Pass(ConflictRuleId, "normal-equipment-removal", facts);
        if (proposed.Kind != EquipmentSlotKind.FunctionalAccessory || !context.ProposedSlotAffectsEffectiveEquipment)
            return RuleResults.Pass(ConflictRuleId, "vanity-armor-or-inactive-loadout-not-effective-accessory", facts);
        if (!context.Input.VersionMatched || catalog.RuntimeFingerprint != context.Input.RuntimeFingerprint ||
            string.IsNullOrWhiteSpace(catalog.DataVersion) || context.SnapshotSession != context.Input.Session)
            return RuleResults.Unknown(ConflictRuleId, "equipment-snapshot-version-or-session-mismatch", facts);
        // Unlock/storage is a different predicate from an accessory combination. The native
        // absence of a locked-slot effect needs neither other-slot completeness nor item history.
        if (context.UnlockContextComplete && !context.UsableFunctionalSlots.Contains(proposed.SlotId) &&
            !context.FunctionalApplicationVerified)
            return RuleResults.Pass(UnlockRuleId, "stored-item-in-disabled-slot-has-no-verified-effect", facts);
        if (context.ExistingSlots.IsDefault || context.ExistingSlots.Length > 64)
            return RuleResults.Unknown(ConflictRuleId, "equipment-snapshot-incomplete-or-over-capacity", facts);
        // Target GetEffectiveArmor can share favorited armor from another loadout. The adapter must
        // project effective slots, including those shares, before supplying any conflict snapshot.
        if (!context.EffectiveSnapshotComplete)
            return RuleResults.Unknown(ConflictRuleId, "effective-loadout-projection-incomplete", facts);
        if (context.AuthorizedEquipmentMutation) return RuleResults.Pass(ConflictRuleId, "scoped-authorized-equipment-mutation", facts);
        if (!context.UnlockContextComplete) return RuleResults.Unknown(UnlockRuleId, "unlock-world-context-unverified", facts);
        if (!context.UsableFunctionalSlots.Contains(proposed.SlotId))
        {
            // Items may legitimately remain stored in a slot disabled by a different world mode.
            if (context.TransitionInProgress || context.InitialSynchronization)
                return RuleResults.Unknown(UnlockRuleId, "loadout-or-equipment-transition", facts);
            return RuleResults.Candidate(UnlockRuleId, "verified-functional-use-of-locked-slot", context.Input, facts);
        }
        if (!catalog.AccessoryTypes.Contains(proposed.ItemId)) return RuleResults.Unknown(ConflictRuleId, "accessory-type-not-modeled", facts);
        if (!catalog.DuplicateRuleComplete || !catalog.ConflictTableComplete)
            return RuleResults.Unknown(ConflictRuleId, "accessory-conflict-table-incomplete", facts);
        foreach (var other in context.ExistingSlots)
        {
            if (other.SlotId == proposed.SlotId || other.ItemId == 0 || other.Kind != EquipmentSlotKind.FunctionalAccessory ||
                other.LoadoutIndex != context.ActiveLoadout || !context.UsableFunctionalSlots.Contains(other.SlotId)) continue;
            if (other.ItemId == proposed.ItemId)
                return ConflictCandidate("duplicate-effective-accessory",
                    facts.Add("conflictingSlot", other.SlotId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (catalog.Conflicts.Contains(new(proposed.ItemId, other.ItemId)) || catalog.Conflicts.Contains(new(other.ItemId, proposed.ItemId)))
                return ConflictCandidate("incompatible-effective-accessories",
                    facts.Add("conflictingItemId", other.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return RuleResults.Pass(ConflictRuleId, "valid-active-accessory-combination", facts);

        BusinessRuleResult ConflictCandidate(string reason, ImmutableDictionary<string, string> conflictFacts) =>
            context.TransitionInProgress || context.InitialSynchronization
                ? RuleResults.Unknown(ConflictRuleId, "loadout-or-equipment-transition", conflictFacts)
                : RuleResults.Candidate(ConflictRuleId, reason, context.Input, conflictFacts);
    }
}
