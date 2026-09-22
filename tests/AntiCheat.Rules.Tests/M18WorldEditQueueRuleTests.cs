using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18WorldEditQueueRuleTests
{
    private static readonly SessionKey Session = new(Guid.NewGuid(), 1, 7, 1);

    private static M18WorldEditQueueOptions Options => new()
    {
        Enabled = true,
        WindowTicks = 3,
        PerSessionWorkUnits = 4,
        EventCapacity = 4,
    };

    private static M18WorldEditObservation Observation(
        SessionKey? session = null,
        long accountId = 731,
        M18WorldEditKind kind = M18WorldEditKind.Tile,
        int x = 20,
        int y = 20,
        int operation = 1,
        int data = 1,
        int amount = 0,
        int workUnits = 1,
        bool parseComplete = true,
        bool clientOrigin = true,
        bool beforeSideEffects = true,
        bool attributionComplete = true)
        => new(session ?? Session, accountId, kind, x, y, operation, data, amount,
            workUnits, parseComplete, clientOrigin, beforeSideEffects, attributionComplete);

    [Test]
    public void TileWallLiquidAndReplacementUseTheActualBoundedWorkUnits()
    {
        var queue = new M18WorldEditQueue(Options);
        queue.AdvanceWorld(Session.WorldEpoch);

        var placeTile = queue.Observe(1, Observation(operation: 1));
        var killWall = queue.Observe(1, Observation(operation: 2, data: 0));
        var replaceWall = queue.Observe(1, Observation(operation: 22, data: 3, workUnits: 2));
        var liquid = queue.Observe(1, Observation(kind: M18WorldEditKind.Liquid, operation: 0,
            data: 0, amount: 255));

        Assert.Multiple(() =>
        {
            Assert.That(placeTile.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(killWall.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(replaceWall.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(replaceWall.SessionWorkUnits, Is.EqualTo(4));
            Assert.That(liquid.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(liquid.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(liquid.IsResourceBlock, Is.True);
            Assert.That(liquid.SessionWorkUnits, Is.EqualTo(4));
            Assert.That(liquid.EventsRetained, Is.EqualTo(3));
        });
    }

    [Test]
    public void WindowExpiryAndExactSessionForgetClearOnlyThatQueueState()
    {
        var queue = new M18WorldEditQueue(Options with { PerSessionWorkUnits = 1 });
        queue.AdvanceWorld(Session.WorldEpoch);

        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Block));
        var expired = queue.Observe(4, Observation());
        var otherSession = queue.Observe(4, Observation(session: Session with { Generation = 2 }));

        queue.Forget(Session);
        var forgotten = queue.Observe(4, Observation());

        Assert.Multiple(() =>
        {
            Assert.That(expired.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(expired.SessionWorkUnits, Is.EqualTo(1));
            Assert.That(otherSession.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(otherSession.SessionWorkUnits, Is.EqualTo(1));
            Assert.That(forgotten.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(forgotten.SessionWorkUnits, Is.EqualTo(1));
        });
    }

    [Test]
    public void IncompleteOrInvalidInputDoesNotConsumeTheWindow()
    {
        var queue = new M18WorldEditQueue(Options with { PerSessionWorkUnits = 1 });
        queue.AdvanceWorld(Session.WorldEpoch);

        var incomplete = queue.Observe(1, Observation(attributionComplete: false));
        var invalid = queue.Observe(1, Observation(kind: M18WorldEditKind.Liquid, data: 4, amount: 1));
        var valid = queue.Observe(1, Observation());
        var cancelled = queue.Observe(1, Observation());

        Assert.Multiple(() =>
        {
            Assert.That(incomplete.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(incomplete.Counted, Is.False);
            Assert.That(invalid.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(invalid.Counted, Is.False);
            Assert.That(valid.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(valid.SessionWorkUnits, Is.EqualTo(1));
            Assert.That(cancelled.Action, Is.EqualTo(ControlAction.Block));
        });
    }

    [Test]
    public void EventCapacityIsAResourceStopLossAndNotCheatingEvidence()
    {
        var queue = new M18WorldEditQueue(Options with { PerSessionWorkUnits = 8, EventCapacity = 2 });
        queue.AdvanceWorld(Session.WorldEpoch);
        queue.Observe(1, Observation());
        queue.Observe(1, Observation(x: 21));
        var exhausted = queue.Observe(1, Observation(x: 22));
        var result = M18WorldEditQueueRules.ResourceBlock(
            new(Session, "fixture", "fixture", true, true, true, true),
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            exhausted);

        Assert.Multiple(() =>
        {
            Assert.That(exhausted.IsResourceBlock, Is.True);
            Assert.That(exhausted.CapacityExhausted, Is.True);
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
        });
    }

    [Test]
    public void WorldEpochResetDoesNotCarryBrushBudgetIntoTheNextWorld()
    {
        var queue = new M18WorldEditQueue(Options with { PerSessionWorkUnits = 1 });
        queue.AdvanceWorld(Session.WorldEpoch);
        Assert.That(queue.Observe(1, Observation()).Action, Is.EqualTo(ControlAction.Unknown));

        queue.AdvanceWorld(Session.WorldEpoch + 1);
        var nextWorld = queue.Observe(1, Observation(session: Session with { WorldEpoch = Session.WorldEpoch + 1 }));

        Assert.Multiple(() =>
        {
            Assert.That(nextWorld.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(nextWorld.SessionWorkUnits, Is.EqualTo(1));
            Assert.That(nextWorld.EventsRetained, Is.EqualTo(1));
        });
    }
}
