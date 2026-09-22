using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

public sealed class M10SummonBudgetRuleTests
{
    private static readonly SessionKey Session = new(Guid.Parse("fd14c73a-040f-422a-a52d-9073fb63030c"), 1, 7, 1);
    private static M10SummonBudgetInput Input(double slots = 0, double maximum = 1) =>
        new(Session, Session, true, true, true, true, false, slots, 1, maximum, 1, 0);

    [TestCase(0d, 1d)] [TestCase(0.5d, 2d)] [TestCase(1d, 2d)]
    public void LegalCreationUsesFractionalSlotCostAndObservedCapability(double slots, double maximum)
    { Assert.That(M10SummonBudgetRules.Evaluate(Input(slots, maximum)).Action, Is.EqualTo(ControlAction.Pass)); }

    [Test]
    public void FirstClosedExcessIsOnlyAResourceBlock()
    {
        var result = M10SummonBudgetRules.Evaluate(Input(1));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(result.PredicateSatisfied || result.PrerequisitesComplete, Is.False);
    }

    [Test]
    public void RetirementUncertaintyMayRemoveCostButNeverInventCompletion()
    {
        var result = M10SummonBudgetRules.Evaluate(Input() with { RetirementUncertainEntities = 1 });
        Assert.That(result.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(result.Facts["retirementUncertainEntities"], Is.EqualTo("1"));
    }

    [TestCase("version")] [TestCase("capacity")] [TestCase("inputs")]
    [TestCase("existing-or-other")] [TestCase("cancelled")] [TestCase("generation")] [TestCase("world")]
    public void MissingPremiseCannotBlock(string missing)
    {
        var input = Input(64);
        input = missing switch
        {
            "version" => input with { VersionMatched = false },
            "capacity" => input with { NativeCapacityReturned = false },
            "inputs" => input with { InputsUnchanged = false },
            "existing-or-other" => input with { FreshOwnSlime = false },
            "cancelled" => input with { RequestCancelled = true },
            "generation" => input with { SnapshotSession = Session with { Generation = 2 } },
            _ => input with { SnapshotSession = Session with { WorldEpoch = 2 } }
        };
        Assert.That(M10SummonBudgetRules.Evaluate(input).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)] [TestCase(-1d)]
    public void BrokenNumericContextIsNotAbuse(double slots)
    { Assert.That(M10SummonBudgetRules.Evaluate(Input(slots)).Verdict, Is.EqualTo(Verdict.Unknown)); }

    [Test]
    public void CapacityDeclineUsesConservativePreviousCeiling()
    { Assert.That(M10SummonBudgetRules.Evaluate(Input(1, 2)).Action, Is.EqualTo(ControlAction.Pass)); }
}
