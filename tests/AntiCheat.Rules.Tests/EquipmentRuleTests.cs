using System.Collections.Immutable;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class EquipmentRuleTests
{
    private static EquipmentCatalog Catalog => new(Fingerprint, "target-test-accessories", ImmutableHashSet.Create(10, 20, 30),
        ImmutableHashSet.Create(new EquipmentConflict(20, 30)), true, true);
    private static EquipmentSlot Proposed => new(4, 10, EquipmentSlotKind.FunctionalAccessory, 0);
    private static EquipmentContext Context => new(Input, Actor, 0,
        ImmutableArray.Create(new EquipmentSlot(3, 20, EquipmentSlotKind.FunctionalAccessory, 0)),
        ImmutableHashSet.Create(3, 4, 5, 6, 7, 8), true, false, false, true, EffectiveSnapshotComplete: true);

    [Test] public void DistinctFunctionalAccessoriesPass() => Pass(EquipmentRules.Evaluate(Proposed, Context, Catalog));
    [Test] public void ReplacingSameSlotIsNotDuplicate() => Pass(EquipmentRules.Evaluate(Proposed with { SlotId = 3, ItemId = 20 }, Context, Catalog));
    [Test] public void VanityDuplicateIsLegal() => Pass(EquipmentRules.Evaluate(Proposed with { Kind = EquipmentSlotKind.Vanity, ItemId = 20 }, Context, Catalog));
    [Test] public void InactiveUnsharedLoadoutIsLegal() =>
        Pass(EquipmentRules.Evaluate(Proposed with { LoadoutIndex = 1, ItemId = 20 }, Context with { ProposedSlotAffectsEffectiveEquipment = false }, Catalog));
    [Test] public void RawLoadoutSnapshotCannotProveEffectiveDuplicates() =>
        Unknown(EquipmentRules.Evaluate(Proposed with { ItemId = 20 }, Context with { EffectiveSnapshotComplete = false }, Catalog));
    [Test] public void FavoritedSharedAccessoryUsesEffectiveProjection() =>
        Candidate(EquipmentRules.Evaluate(Proposed with { LoadoutIndex = 1, ItemId = 20 }, Context, Catalog));
    [Test] public void StableDuplicateAndConflictAreIndependentCompletePredicates()
    {
        Candidate(EquipmentRules.Evaluate(Proposed with { ItemId = 20 }, Context, Catalog));
        Candidate(EquipmentRules.Evaluate(Proposed with { ItemId = 30 }, Context, Catalog));
    }
    [Test] public void LoadoutTransitionDoesNotBanIntermediateDuplicate() =>
        Unknown(EquipmentRules.Evaluate(Proposed with { ItemId = 20 }, Context with { TransitionInProgress = true }, Catalog));
    [Test] public void InitialSyncCannotProveAnEquipmentAction() =>
        Unknown(EquipmentRules.Evaluate(Proposed with { ItemId = 20 }, Context with { InitialSynchronization = true }, Catalog));
    [Test] public void DisabledWorldModeSlotMayKeepStoredAccessory() =>
        Pass(EquipmentRules.Evaluate(Proposed with { SlotId = 9 }, Context with { FunctionalApplicationVerified = false }, Catalog));
    [Test] public void LockedStorageDoesNotRequireOtherSlotsOrClientCompletion()
    {
        Pass(EquipmentRules.Evaluate(Proposed with { SlotId = 9 }, Context with
        {
            FunctionalApplicationVerified = false, EffectiveSnapshotComplete = false,
            ExistingSlots = default, InitialSynchronization = true, TransitionInProgress = true
        }, Catalog with { DuplicateRuleComplete = false, ConflictTableComplete = false }));
    }
    [Test] public void NonConflictingCurrentProjectionDoesNotRequireClientTransitionCompletion()
    {
        Pass(EquipmentRules.Evaluate(Proposed, Context with { InitialSynchronization = true }, Catalog));
        Pass(EquipmentRules.Evaluate(Proposed, Context with { TransitionInProgress = true }, Catalog));
    }
    [Test] public void VerifiedFunctionalUseRequiresActualUnlockContext()
    {
        Candidate(EquipmentRules.Evaluate(Proposed with { SlotId = 9 }, Context, Catalog));
        Unknown(EquipmentRules.Evaluate(Proposed with { SlotId = 9 }, Context with { UnlockContextComplete = false }, Catalog));
    }
    [Test] public void PreviousSessionEquipmentIsNotReused() =>
        Unknown(EquipmentRules.Evaluate(Proposed with { ItemId = 20 }, Context with { SnapshotSession = Actor with { Generation = 11 } }, Catalog));
    [Test] public void UnmodeledConflictTableCannotBan() =>
        Unknown(EquipmentRules.Evaluate(Proposed with { ItemId = 30 }, Context, Catalog with { ConflictTableComplete = false }));
    [Test] public void ScopedAuthorizedMutationAllowsCustomCombination() =>
        Pass(EquipmentRules.Evaluate(Proposed with { ItemId = 30 }, Context with { AuthorizedEquipmentMutation = true }, Catalog));
}
