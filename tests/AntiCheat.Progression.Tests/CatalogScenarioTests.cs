using System.Text.Json;
using AntiCheat.Progression;
using NUnit.Framework;

namespace AntiCheat.Progression.Tests;

[TestFixture]
public sealed class CatalogScenarioTests
{
    private static ProgressionCatalog Load() => ProgressionCatalog.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "candidates.json"));
    public static IEnumerable<string> RuleIds() => Load().Rules.Select(rule => rule.Id);

    private static IEnumerable<Condition> Leaves(Condition node)
    {
        if (node.Op == "fact") yield return node;
        if (node.Arg is not null) foreach (var child in Leaves(node.Arg)) yield return child;
        foreach (var arg in node.Args) foreach (var child in Leaves(arg)) yield return child;
    }

    private static Dictionary<string, JsonElement> PreProgressFacts(ProgressionRule rule)
    {
        var facts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        // Explicit scenario: fresh ordinary world, no boss killed, no same-event boss transition,
        // source policy opted in, vanity/music/rush opt-outs disabled, all attribution exceptions excluded.
        foreach (var fact in Leaves(rule.Condition).Concat(Leaves(rule.Exceptions)))
            facts[fact.Name!] = fact.Expected.ValueKind == JsonValueKind.String
                ? JsonSerializer.SerializeToElement("BossDType.NA")
                : JsonSerializer.SerializeToElement(false);
        facts["policy.mklpSubsetEnabled"] = JsonSerializer.SerializeToElement(true);
        return facts;
    }

    [Test] public void EntireCatalogStaysOutsideProductionAdmission()
    {
        var catalog = Load();
        Assert.That(catalog.Rules, Has.Length.EqualTo(16));
        foreach (var rule in catalog.Rules)
        {
            Assert.That(rule.Qualification.ProductionEligible, Is.False, rule.Id);
            Assert.That(rule.Qualification.Baseline1456, Is.EqualTo("acquisition_unverified"));
            Assert.That(rule.Qualification.Target1458, Is.EqualTo("blocked"));
            Assert.That(rule.Items.Distinct().Count(), Is.EqualTo(rule.Items.Length), "Curated items are distinct; raw repetitions stay in source inventory.");
        }
    }

    [TestCaseSource(nameof(RuleIds))]
    public void EachCandidateHasKnownSourceViolationWithoutAutomaticBan(string id)
    {
        var rule = Load().Rules.Single(x => x.Id == id);
        var result = ProgressionEvaluator.Assess(rule, rule.Items[0], PreProgressFacts(rule));
        Assert.That(result.Prohibition, Is.EqualTo(Truth.True), id);
        Assert.That(result.Exception, Is.EqualTo(Truth.False));
        Assert.That(result.Decision, Is.EqualTo(CandidateDecision.Unknown));
        Assert.That(result.AutomaticBanAllowed, Is.False);
        Assert.That(result.BlockCurrentAction, Is.False);
    }

    [TestCaseSource(nameof(RuleIds))]
    public void EachCandidatePreservesPassivePickupCounterexample(string id)
    {
        var rule = Load().Rules.Single(x => x.Id == id);
        var facts = PreProgressFacts(rule);
        facts["exception.passiveReceipt"] = JsonSerializer.SerializeToElement(true);
        var result = ProgressionEvaluator.Assess(rule, rule.Items[0], facts);
        Assert.That(result.Decision, Is.EqualTo(CandidateDecision.Pass));
        Assert.That(result.BlockCurrentAction, Is.False);
    }

    [TestCaseSource(nameof(RuleIds))]
    public void EachCandidateTreatsUnresolvedAuthorizationAsUnknown(string id)
    {
        var rule = Load().Rules.Single(x => x.Id == id);
        var facts = PreProgressFacts(rule);
        facts.Remove("exception.authorizedServerGrant");
        var result = ProgressionEvaluator.Assess(rule, rule.Items[0], facts);
        Assert.That(result.Exception, Is.EqualTo(Truth.Unknown));
        Assert.That(result.Reason, Is.EqualTo("incomplete-context"));
        Assert.That(result.AutomaticBanAllowed, Is.False);
    }

    [Test] public void SourceServerPoliciesAreNotImplicitlyOurWorldRules()
    {
        foreach (var rule in Load().Rules.Where(x => x.Classification == "server_policy"))
        {
            var facts = PreProgressFacts(rule);
            facts["policy.mklpSubsetEnabled"] = JsonSerializer.SerializeToElement(false);
            Assert.That(ProgressionEvaluator.Assess(rule, rule.Items[0], facts).Decision, Is.EqualTo(CandidateDecision.Pass), rule.Id);
            facts.Remove("policy.mklpSubsetEnabled");
            Assert.That(ProgressionEvaluator.Assess(rule, rule.Items[0], facts).Prohibition, Is.EqualTo(Truth.Unknown), rule.Id);
        }
    }
}
