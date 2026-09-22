using System.Collections.Immutable;
using System.Text.Json;
using AntiCheat.Progression;
using NUnit.Framework;

namespace AntiCheat.Progression.Tests;

[TestFixture]
public sealed class PredicateTests
{
    private static Condition Fact(string name, object expected) => new() { Op = "fact", Name = name, Expected = JsonSerializer.SerializeToElement(expected) };
    private static Condition Not(Condition arg) => new() { Op = "not", Arg = arg };
    private static Condition All(params Condition[] args) => new() { Op = "all", Args = [.. args] };
    private static Condition Any(params Condition[] args) => new() { Op = "any", Args = [.. args] };
    private static Condition Constant(bool value) => new() { Op = "constant", Value = value };
    private static Dictionary<string, JsonElement> Facts(params (string Name, object Value)[] facts) =>
        facts.ToDictionary(x => x.Name, x => JsonSerializer.SerializeToElement(x.Value));

    private static ProgressionRule Rule() => new()
    {
        Id = "fixture.progression", Classification = "server_policy", Items = [100],
        Condition = All(Not(Fact("hardMode", true)), Not(Fact("currentBoss", "WallOfFlesh"))),
        Exceptions = Fact("authorizedImport", true),
        RequiredContexts = ["authenticated-account", "world-epoch"], ProofPreconditions = ["active-attribution"],
        Sources = [JsonSerializer.SerializeToElement(new { repository = "test-fixture" })]
    };

    [TestCase(false, false, Truth.False, Truth.False)]
    [TestCase(false, true, Truth.False, Truth.True)]
    [TestCase(true, false, Truth.False, Truth.True)]
    [TestCase(true, true, Truth.True, Truth.True)]
    public void AndOrAreNotMerged(bool a, bool b, Truth andExpected, Truth orExpected)
    {
        var facts = Facts(("a", a), ("b", b));
        Assert.That(ProgressionEvaluator.Evaluate(All(Fact("a", true), Fact("b", true)), facts), Is.EqualTo(andExpected));
        Assert.That(ProgressionEvaluator.Evaluate(Any(Fact("a", true), Fact("b", true)), facts), Is.EqualTo(orExpected));
    }

    [Test] public void MissingFactsAreUnknownWithSoundBooleanShortCircuit()
    {
        var missing = Fact("missing", true);
        var facts = Facts();
        Assert.That(ProgressionEvaluator.Evaluate(missing, facts), Is.EqualTo(Truth.Unknown));
        Assert.That(ProgressionEvaluator.Evaluate(Not(missing), facts), Is.EqualTo(Truth.Unknown));
        Assert.That(ProgressionEvaluator.Evaluate(All(Constant(false), missing), facts), Is.EqualTo(Truth.False));
        Assert.That(ProgressionEvaluator.Evaluate(All(Constant(true), missing), facts), Is.EqualTo(Truth.Unknown));
        Assert.That(ProgressionEvaluator.Evaluate(Any(Constant(true), missing), facts), Is.EqualTo(Truth.True));
        Assert.That(ProgressionEvaluator.Evaluate(Any(Constant(false), missing), facts), Is.EqualTo(Truth.Unknown));
    }

    [Test] public void BossTransitionExceptionIsPreserved()
    {
        var result = ProgressionEvaluator.Assess(Rule(), 100, Facts(("hardMode", false), ("currentBoss", "WallOfFlesh"), ("authorizedImport", false)));
        Assert.That(result.Decision, Is.EqualTo(CandidateDecision.Pass));
        Assert.That(result.AutomaticBanAllowed, Is.False);
    }

    [Test] public void KnownProhibitedSourceStateDoesNotPromoteUnverifiedRule()
    {
        var result = ProgressionEvaluator.Assess(Rule(), 100, Facts(("hardMode", false), ("currentBoss", "NA"), ("authorizedImport", false)));
        Assert.That(result.Prohibition, Is.EqualTo(Truth.True));
        Assert.That(result.Exception, Is.EqualTo(Truth.False));
        Assert.That(result.Reason, Is.EqualTo("candidate-not-admitted"));
        Assert.That(result.AutomaticBanAllowed, Is.False);
        Assert.That(result.BlockCurrentAction, Is.False);
    }

    [Test] public void LegalImportOrMissingExceptionNeverCreatesProof()
    {
        var allowed = ProgressionEvaluator.Assess(Rule(), 100, Facts(("hardMode", false), ("currentBoss", "NA"), ("authorizedImport", true)));
        var unknown = ProgressionEvaluator.Assess(Rule(), 100, Facts(("hardMode", false), ("currentBoss", "NA")));
        Assert.That(allowed.Decision, Is.EqualTo(CandidateDecision.Pass));
        Assert.That(unknown.Reason, Is.EqualTo("incomplete-context"));
        Assert.That(unknown.BlockCurrentAction, Is.False);
    }

    [Test] public void WrongFactTypesAreUnknownRatherThanFalse()
    {
        Assert.That(ProgressionEvaluator.Evaluate(Fact("boss", true), Facts(("boss", "true"))), Is.EqualTo(Truth.Unknown));
    }

    [Test] public void CandidateFileCannotSetProductionEligibility()
    {
        var catalog = new ProgressionCatalog { SchemaVersion = 1, Rules = [Rule() with { Qualification = new() { ProductionEligible = true } }] };
        Assert.Throws<InvalidDataException>(() => catalog.Validate());
    }

    [Test] public void CorruptOrUnknownSyntaxIsRejected()
    {
        var catalog = new ProgressionCatalog { SchemaVersion = 1, Rules = [Rule() with { Condition = new() { Op = "shell" } }] };
        Assert.Throws<InvalidDataException>(() => catalog.Validate());
        Assert.That(ProgressionEvaluator.Evaluate(new() { Op = "all" }, Facts()), Is.EqualTo(Truth.Unknown));
    }

    [Test] public void ContradictoryCurrentBossNotEqualsOrIsTautology()
    {
        // Upstream bug stays visible in the source audit; it is not silently corrected into a hard rule.
        var upstream = Any(Not(Fact("currentBoss", "TheTwins")), Not(Fact("currentBoss", "SkeletronPrime")));
        foreach (var boss in new[] { "NA", "TheTwins", "SkeletronPrime", "TheDestroyer", "MoonLord" })
            Assert.That(ProgressionEvaluator.Evaluate(upstream, Facts(("currentBoss", boss))), Is.EqualTo(Truth.True));
    }

    [Test] public void ImmutableRuleCanBeEvaluatedConcurrently()
    {
        var rule = Rule();
        var facts = Facts(("hardMode", false), ("currentBoss", "NA"), ("authorizedImport", false)).ToImmutableDictionary();
        Parallel.For(0, 1000, _ => Assert.That(ProgressionEvaluator.Assess(rule, 100, facts).Reason, Is.EqualTo("candidate-not-admitted")));
    }
}
