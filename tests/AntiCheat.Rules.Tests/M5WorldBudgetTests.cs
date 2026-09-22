using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M5WorldBudgetTests
{
    private readonly Guid run = Guid.NewGuid();
    private SessionKey Key(int slot, long generation = 1) => new(run, 1, slot, generation);

    [Test]
    public void SharedNatLegitimateBuildingAndFarmActivationBurstRemainAllowed()
    {
        var clock = new NetworkTestClock(); var controls = new NetworkControls(clock);
        for (int slot = 0; slot < 32; slot++)
        {
            var key = Key(slot); Assert.That(controls.Open(key, "203.0.113.9").Disposition, Is.EqualTo(NetworkDisposition.Allow));
            // Two large L-shaped wire operations, one permitted large edit, normal liquid and entity
            // updates, and 60 switch requests. Native automatic timer ticks are server work, not packets.
            for (int wire = 0; wire < 2; wire++)
                Assert.That(controls.Consume(key, 12, NetworkRequestKind.WorldMutation, 5001).Disposition, Is.EqualTo(NetworkDisposition.Allow));
            Assert.That(controls.Consume(key, 30010, NetworkRequestKind.WorldMutation, 10000).Disposition, Is.EqualTo(NetworkDisposition.Allow));
            for (int i = 0; i < 60; i++)
            {
                Assert.That(controls.Consume(key, 7, NetworkRequestKind.BroadcastAmplification).Disposition, Is.EqualTo(NetworkDisposition.Allow));
                Assert.That(controls.Consume(key, 9, NetworkRequestKind.WorldMutation, 1).Disposition, Is.EqualTo(NetworkDisposition.Allow));
            }
        }
        Assert.That(controls.SourceBucketCount, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(controls.Consume(Key(0), 12, NetworkRequestKind.WorldMutation, 5001).Disposition, Is.EqualTo(NetworkDisposition.Allow));
    }
    [Test]
    public void ShapeWorkReusesAllThreeBudgetsAndNeverBecomesAnAccountBan()
    {
        var options = new NetworkControlOptions { ConnectionOpeningCost = 1, WorldWorkUnitCost = 1,
            ConnectionBudget = new(256, 200, 1, TimeSpan.FromMinutes(1)),
            SourceBudget = new(10, 15, 1, TimeSpan.FromMinutes(1)),
            DisconnectOnBudgetExhaustion = false };
        var controls = new NetworkControls(new NetworkTestClock(), options);
        controls.Open(Key(0), "203.0.113.9"); controls.Open(Key(1), "::ffff:203.0.113.9");
        var first = controls.Consume(Key(0), 1024, NetworkRequestKind.WorldMutation, 5);
        Assert.That(first.ChargedCost, Is.EqualTo(10)); Assert.That(first.Disposition, Is.EqualTo(NetworkDisposition.Allow));
        var second = controls.Consume(Key(1), 1024, NetworkRequestKind.WorldMutation, 5);
        Assert.That(second.Reason, Is.EqualTo("source-cost-budget-exhausted"));
        Assert.That(second.Disposition, Is.EqualTo(NetworkDisposition.Block)); Assert.That(second.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(second.SourceEvent?.ProposedTarget, Is.Null, "Shared NAT is not an OS ban target.");
        Assert.That(controls.Consume(Key(1), 4, workUnits: int.MaxValue).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(controls.Consume(Key(1), 4, workUnits: -1).Verdict, Is.EqualTo(Verdict.UnsafeInput));
    }
    [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)] [TestCase(0)] [TestCase(-1)] [TestCase(1000001)]
    public void InvalidWorkWeightsCannotDisableOrOverflowBudgets(double weight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkControls(new NetworkTestClock(), new() { WorldWorkUnitCost = weight }));
}
