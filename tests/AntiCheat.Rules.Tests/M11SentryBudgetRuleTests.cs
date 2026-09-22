using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

public sealed class M11SentryBudgetRuleTests
{
    private static readonly SessionKey Session = new(Guid.Parse("f63cdcb3-2129-490c-b728-9e848a4ae123"), 1, 4, 1);
    private static M11SentryBudgetInput Input(int active = 0, int capacity = 1) =>
        new(Session, Session, true, true, true, true, false, active, capacity);

    [TestCase(0, 1)] [TestCase(1, 1)] [TestCase(2, 2)] [TestCase(62, 62)]
    public void LegalInitialAndFullCapacityReplacementIncludeOneNativeTransient(int active, int capacity) =>
        Assert.That(M11SentryBudgetRules.Evaluate(Input(active, capacity)).Action, Is.EqualTo(ControlAction.Pass));

    [Test]
    public void FirstAdditionalCreationBeyondTransientBlocksResourceWithoutCheatClaim()
    {
        var result = M11SentryBudgetRules.Evaluate(Input(2));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(result.PredicateSatisfied || result.PrerequisitesComplete, Is.False);
    }

    [TestCase("capacity")] [TestCase("input")] [TestCase("update")] [TestCase("cancelled")]
    [TestCase("session")] [TestCase("version")] [TestCase("extended-capacity")]
    public void MissingOrUnsupportedPremiseDoesNotBecomeCheating(string missing)
    {
        var input = Input(64);
        input = missing switch
        {
            "capacity" => input with { NativeCapacityReturned = false },
            "input" => input with { InputsUnchanged = false },
            "update" => input with { FreshOwnHydra = false },
            "cancelled" => input with { RequestCancelled = true },
            "session" => input with { SnapshotSession = Session with { Generation = 2 } },
            "version" => input with { VersionMatched = false },
            _ => input with { CapacityUpperBound = 68 }
        };
        Assert.That(M11SentryBudgetRules.Evaluate(input).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void UnfinishedEquipmentOrMissingCapacityCannotPermitUnboundedNativeCreates()
    {
        var input = Input(M11SentryBudgetRules.AbsoluteNativeCapacity + 1) with { NativeCapacityReturned = false, InputsUnchanged = false, SnapshotSession = default };
        var result = M11SentryBudgetRules.Evaluate(input);
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block)); Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(result.Facts["absoluteNativeCapacity"], Is.EqualTo("67"));
        Assert.That(M11SentryBudgetRules.Evaluate(input with { NativeHostAndActorBound = false }).Action, Is.EqualTo(ControlAction.Unknown));
    }
}
