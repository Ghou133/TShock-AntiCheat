using System.Collections.Immutable;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class InventoryRuleTests
{
    private static VersionedItemCatalog Catalog => new(Fingerprint, "target-test-table", -48, 10000, 256,
        new Dictionary<int, ItemTypeDefinition>
        {
            [1] = new(1, 9999, ImmutableHashSet.Create(0), true),
            [2] = new(2, 1, ImmutableHashSet.Create(0, 1, 2), true),
            [-1] = new(1, 1, ImmutableHashSet.Create(0, 1), true)
        }.ToImmutableDictionary(), true);
    private static ItemRuleContext Context => new(Input, 990, false, true, false);
    private static ItemObservation Item => new(1, 1, 0, 10, ItemLocation.Inventory);

    [Test] public void OrdinaryStackAndOneItemWeaponAreLegal()
    {
        Pass(InventoryRules.Evaluate(Item with { Stack = 9999 }, Context, Catalog));
        Pass(InventoryRules.Evaluate(Item with { NetId = 2, Prefix = 2 }, Context, Catalog));
    }
    [Test] public void NegativeWireNetIdUsesTargetDefinition() =>
        Pass(InventoryRules.Evaluate(Item with { NetId = -1, Prefix = 1 }, Context, Catalog));
    [Test] public void AirClearIgnoresUnusedFields() =>
        Pass(InventoryRules.Evaluate(Item with { NetId = 0, Stack = -1, Prefix = 999 }, Context, Catalog));
    [Test] public void OnlyAuditedZeroStackClearSemanticsBypassStructure() =>
        Pass(InventoryRules.Evaluate(Item with { Stack = 0, NetId = 99999 }, Context with { ZeroStackIsClear = true }, Catalog));
    [Test] public void ExistingUntrackedItemNeedsNoSourceLedger() =>
        Pass(InventoryRules.Evaluate(Item, Context, Catalog));
    [Test] public void InitialSynchronizationCannotAttributeImpossibleStack() =>
        Unknown(InventoryRules.Evaluate(Item with { Stack = 10000 }, Context with { InitialSynchronization = true }, Catalog));
    [Test] public void VersionMismatchDisablesOnlyStructureProof() =>
        Unknown(InventoryRules.Evaluate(Item with { Stack = 10000 }, Context, Catalog with { RuntimeFingerprint = "other" }));
    [Test] public void TableGapIsUnknown() => Unknown(InventoryRules.Evaluate(Item with { NetId = 900 }, Context, Catalog));
    [Test] public void PrefixNeedsPerItemCombinationNotJustGlobalRange()
    {
        Candidate(InventoryRules.Evaluate(Item with { Prefix = 2 }, Context, Catalog));
        Unknown(InventoryRules.Evaluate(Item with { Prefix = 2 }, Context,
            Catalog with { Definitions = Catalog.Definitions.SetItem(1, Catalog.Definitions[1] with { PrefixRulesComplete = false }) }));
    }
    [Test] public void SingleExcessStackIsCompletePredicate() =>
        Candidate(InventoryRules.Evaluate(Item with { Stack = 10000 }, Context, Catalog));
    [Test] public void MissingAttributionCannotBecomeStackProof() =>
        Unknown(InventoryRules.Evaluate(Item with { Stack = 10000 }, Context with { Input = Input with { AttributionComplete = false } }, Catalog));
    [Test] public void ScopedMutationCoversStackAndPrefixOnly() =>
        Pass(InventoryRules.Evaluate(Item with { Stack = 10000, Prefix = 2 }, Context with { AuthorizedItemMutation = true }, Catalog));
    [Test] public void ScopedMutationDoesNotPermitInvalidArraySlot() =>
        BlockOnly(InventoryRules.Evaluate(Item with { Slot = 990 }, Context with { AuthorizedItemMutation = true }, Catalog));
    [TestCase(-49, 0)] [TestCase(10000, 0)] [TestCase(1, 256)]
    public void InvalidTypeAndPrefixDomainAreSafetyRejections(int itemId, int prefix) =>
        BlockOnly(InventoryRules.Evaluate(Item with { NetId = itemId, Prefix = prefix }, Context, Catalog));
    [Test] public void InitialPrefixSnapshotCannotProvePlayerCreatedIt() =>
        Unknown(InventoryRules.Evaluate(Item with { Prefix = 2 }, Context with { InitialSynchronization = true }, Catalog));
}
