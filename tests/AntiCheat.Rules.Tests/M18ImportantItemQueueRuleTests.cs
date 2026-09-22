using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18ImportantItemQueueRuleTests
{
    private static readonly SessionKey Session = new(Guid.NewGuid(), 1, 7, 1);
    private static readonly IReadOnlyDictionary<int, string> Catalog =
        new Dictionary<int, string> { [3332] = "MoonLordBossBag", [4957] = "QueenSlimeBossBag" };

    private static M18ImportantItemQueueOptions Options(int capacity = 8) => new()
    {
        Enabled = true,
        WindowTicks = 10,
        EventCapacity = capacity,
        InventorySlotCapacity = 8,
    };

    private static M18ImportantItemObservation Inventory(int itemId, int stack, int slot = 0,
        bool initialSynchronization = false, SessionKey? session = null, long accountId = 6207) =>
        new(session ?? Session, accountId, M18ImportantItemSource.Inventory, slot, itemId, stack,
            SessionComplete: true, SourceContextKnown: false, SourceAttributionComplete: false,
            initialSynchronization, ParseComplete: true, ClientOrigin: true, BeforeSideEffects: true);

    private static M18ImportantItemObservation Source(M18ImportantItemSource source, int itemId,
        int stack, int slot = 0) =>
        new(Session, 6207, source, slot, itemId, stack,
            SessionComplete: true, SourceContextKnown: false, SourceAttributionComplete: false,
            InitialSynchronization: false, ParseComplete: true, ClientOrigin: true,
            BeforeSideEffects: true);

    [Test]
    public void InventoryGrowthIsRecordedButNeverBecomesProof()
    {
        var queue = new M18ImportantItemQueue(Options(), Catalog);
        var baseline = queue.Observe(0, Inventory(3332, 1, initialSynchronization: true));
        var growth = queue.Observe(1, Inventory(3332, 9999));

        Assert.That(baseline.Important, Is.True);
        Assert.That(baseline.InitialSynchronization, Is.True);
        Assert.That(baseline.GrowthObserved, Is.False);
        Assert.That(baseline.RecentPositiveGrowth, Is.Zero);
        Assert.That(growth.GrowthObserved, Is.True);
        Assert.That(growth.Delta, Is.EqualTo(9998));
        Assert.That(growth.RecentPositiveGrowth, Is.EqualTo(9998));
        Assert.That(growth.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(growth.Verdict, Is.EqualTo(Verdict.Unknown));

        var result = M18ImportantItemQueueRules.Observe(
            new(Session, "fp", "fp", true, true, true, false), growth);
        Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(result.PrerequisitesComplete, Is.False);
        Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(result.Facts["passiveReceiptIsNotCreatorProof"], Is.EqualTo("True"));
    }

    [Test]
    public void ClearThenRefillAcrossSlotsIsMarkedAsPossibleSorting()
    {
        var queue = new M18ImportantItemQueue(Options(), Catalog);
        queue.Observe(0, Inventory(3332, 9999, slot: 0, initialSynchronization: true));
        var clear = queue.Observe(1, Inventory(0, 0, slot: 0));
        var refill = queue.Observe(1, Inventory(3332, 9999, slot: 1));

        Assert.Multiple(() =>
        {
            Assert.That(clear.Important, Is.True, "The old important slot remains part of the aggregate transition.");
            Assert.That(clear.ItemId, Is.EqualTo(3332));
            Assert.That(clear.Stack, Is.Zero);
            Assert.That(clear.PreviousSlotStack, Is.EqualTo(9999));
            Assert.That(clear.PreviousTotal, Is.EqualTo(9999));
            Assert.That(clear.CurrentTotal, Is.Zero);
            Assert.That(clear.Delta, Is.EqualTo(-9999));
            Assert.That(refill.Important, Is.True);
            Assert.That(refill.PreviousSlotStack, Is.Zero);
            Assert.That(refill.PreviousTotal, Is.Zero);
            Assert.That(refill.CurrentTotal, Is.EqualTo(9999));
            Assert.That(refill.Stack, Is.EqualTo(9999));
            Assert.That(refill.PossibleSorting, Is.True);
            Assert.That(refill.GrowthObserved, Is.False);
            Assert.That(refill.Action, Is.EqualTo(ControlAction.Unknown));
        });
    }

    [Test]
    public void ContainerAndWorldDropSourcesAreRecordedWithoutCreatorAttribution()
    {
        var queue = new M18ImportantItemQueue(Options(), Catalog);
        var chest = queue.Observe(0, Source(M18ImportantItemSource.Container, 3332, 1, slot: 4));
        var drop = queue.Observe(0, Source(M18ImportantItemSource.WorldDrop, 3332, 12, slot: 3));

        Assert.That(chest.Recorded, Is.True);
        Assert.That(drop.Recorded, Is.True);
        Assert.That(chest.SourceAttributionComplete, Is.False);
        Assert.That(drop.SourceAttributionComplete, Is.False);
        Assert.That(chest.GrowthObserved, Is.False);
        Assert.That(drop.GrowthObserved, Is.False);
        Assert.That(drop.Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void EventFifoIsFiniteAndOverflowDoesNotCancelOrSanction()
    {
        var queue = new M18ImportantItemQueue(Options(capacity: 2), Catalog);
        queue.Observe(0, Source(M18ImportantItemSource.WorldDrop, 3332, 1));
        queue.Observe(0, Source(M18ImportantItemSource.WorldDrop, 3332, 2));
        var overflow = queue.Observe(0, Source(M18ImportantItemSource.WorldDrop, 3332, 3));

        Assert.That(overflow.QueueOverflowed, Is.True);
        Assert.That(overflow.EventsRetained, Is.EqualTo(2));
        Assert.That(overflow.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(overflow.Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void InvalidIdentityOrBoundsNeverMutateAValidSession()
    {
        var queue = new M18ImportantItemQueue(Options(), Catalog);
        var badIdentity = queue.Observe(0, Inventory(3332, 1, accountId: 0));
        var badSlot = queue.Observe(0, Inventory(3332, 1, slot: 8));
        var valid = queue.Observe(0, Inventory(3332, 1));

        Assert.That(badIdentity.Important, Is.False);
        Assert.That(badSlot.Important, Is.False);
        Assert.That(valid.Recorded, Is.True);
        Assert.That(valid.PreviousTotal, Is.Zero);
        Assert.That(valid.Action, Is.EqualTo(ControlAction.Unknown));
    }
}
