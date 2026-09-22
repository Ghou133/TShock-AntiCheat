using System.Text.Json.Nodes;
using AntiCheat.RuleExtraction;
using NUnit.Framework;

namespace AntiCheat.RuleExtraction.Tests;

[TestFixture]
public class ExtractionTests
{
    private string _source = null!;
    private string _config = null!;
    private JsonObject _inventory = null!;

    [OneTimeSetUp]
    public void ReadReferenceAsTextOnly()
    {
        _source = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures/SurvivalManager.source.txt"));
        _config = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures/Config.source.txt"));
        _inventory = SourceExtractor.Extract(_source, _config);
    }
    private JsonObject At(int line) => _inventory["operations"]!.AsArray().OfType<JsonObject>()
        .Single(o => o["source"]!["lineStart"]!.GetValue<int>() == line);

    [Test]
    public void InventoryCoversEntireFunctionAndEveryMutationInOrder()
    {
        Assert.That(_inventory["source"]!["lineStart"]!.GetValue<int>(), Is.EqualTo(45));
        Assert.That(_inventory["source"]!["lineEnd"]!.GetValue<int>(), Is.EqualTo(1564));
        Assert.That(_inventory["summary"]!["mutationStatements"]!.GetValue<int>(), Is.EqualTo(149));
        Assert.That(_inventory["summary"]!["addStatements"]!.GetValue<int>(), Is.EqualTo(135));
        Assert.That(_inventory["summary"]!["removeStatements"]!.GetValue<int>(), Is.EqualTo(14));
        var ids = _inventory["operations"]!.AsArray().Select(o => o!["id"]!.GetValue<string>()).ToArray();
        Assert.That(ids, Is.EqualTo(Enumerable.Range(1, 149).Select(i => $"MKLP-OP-{i:D3}")));
        Assert.That(_inventory["controlFlow"]!.ToJsonString(), Does.Not.Contain("MKLP-OP-000"));
    }

    [TestCase(584, 364, 391, 370)]
    [TestCase(1022, 1155, 1162, 1158)]
    public void InclusiveRangesRetainBoundsAndUnconditionalContinueExceptions(int line, int first, int last, int skipped)
    {
        var domain = At(line)["itemDomain"]!;
        Assert.That(domain["first"]!.GetValue<int>(), Is.EqualTo(first));
        Assert.That(domain["last"]!.GetValue<int>(), Is.EqualTo(last));
        var ids = domain["items"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        Assert.That(ids, Does.Not.Contain(skipped));
        Assert.That(ids, Does.Contain(first).And.Contain(last));
        Assert.That(At(line)["priorContinuePaths"]!.AsArray(), Has.Count.EqualTo(1));
    }

    [Test]
    public void ConditionalContinueRetainsItsFactAndArrayCondition()
    {
        var remove = At(1128);
        Assert.That(remove["itemDomain"]!["items"]!.AsArray(), Has.Count.EqualTo(13));
        Assert.That(remove["priorContinuePaths"]!.ToJsonString(), Does.Contain("allowvanity").And.Contain("vanityids.Contains"));
    }

    [Test]
    public void ElseBranchesAndSourceBugsArePreservedRatherThanSilentlyFixed()
    {
        Assert.That(At(411)["conditionPath"]![0]!["branch"]!.GetValue<string>(), Is.EqualTo("else"));
        Assert.That(At(101)["conditionPath"]!.ToJsonString(), Does.Contain("TheTwins").And.Contain("!=").And.Contain("||"));
        Assert.That(At(403)["dictionaryGuard"]!.GetValue<string>(), Is.EqualTo("!getillegalitems.ContainsKey(remove)"));
        Assert.That(At(1428)["dictionaryGuard"]!.GetValue<string>(), Is.EqualTo("!getillegalitems.ContainsKey(1724)"));
    }

    [Test]
    public void CommentedDifficultyCodeIsNotActivatedButFalseBranchesRemainAuditable()
    {
        Assert.That(_inventory["commentedOutCode"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(_inventory["commentedOutCode"]![0]!["executable"]!.GetValue<bool>(), Is.False);
        Assert.That(At(360)["conditionPath"]!.ToJsonString(), Does.Contain("false"));
        Assert.That(_inventory["operations"]!.AsArray().Any(o => o!["source"]!["lineStart"]!.GetValue<int>() == 1376), Is.False);
    }

    [Test]
    public void ArrayResolutionUsesLexicalScopeAndPreservesGolemRepeatedAddin()
    {
        Assert.That(At(1185)["itemDomain"]!["items"]!.AsArray(), Has.Count.EqualTo(82));
        Assert.That(At(1194)["itemDomain"]!["items"]!.ToJsonString(), Is.EqualTo(At(1185)["itemDomain"]!["items"]!.ToJsonString()));
        Assert.That(At(1194)["itemDomain"]!["expression"]!.GetValue<string>(), Is.EqualTo("addin"));
    }

    [Test]
    public void WhitelistAndNpcLoopsRemainSymbolicWithoutInventingRuntimeFacts()
    {
        Assert.That(At(1559)["itemDomain"]!["kind"]!.GetValue<string>(), Is.EqualTo("external_collection"));
        Assert.That(At(1559)["itemDomain"]!["items"], Is.Null);
        Assert.That(At(984)["loopPath"]!.ToJsonString(), Does.Contain("Main.npc"));
        Assert.That(_inventory["controlFlow"]!.ToJsonString(), Does.Contain("BreakStatement"));
    }

    [Test]
    public void ConfigurationDefaultsAreMetadataAndBannersHardcodeIsVisible()
    {
        Assert.That(_inventory["relevantConfiguration"]!.AsArray(), Has.Count.EqualTo(10));
        Assert.That(_inventory["relevantConfiguration"]!.AsArray().All(c => !c!["defaultIsDeploymentFact"]!.GetValue<bool>()), Is.True);
        var banner = _inventory["localDeclarations"]!.AsArray().Single(d => d!["name"]!.GetValue<string>() == "allowbanners");
        Assert.That(banner!["initializer"]!.GetValue<string>(), Is.EqualTo("false"));
        Assert.That(_inventory["operations"]!.AsArray().All(o => !o!["qualification"]!["productionEligible"]!.GetValue<bool>()), Is.True);
    }

    [Test]
    public void InvalidSourceAndUnsupportedControlFlowFailExplicitly()
    {
        Assert.Throws<InvalidDataException>(() => SourceExtractor.Extract(_source + "{", _config));
        Assert.Throws<InvalidDataException>(() => SourceExtractor.Extract(_source.Replace("return getillegalitems;", "while (true) { break; } return getillegalitems;"), _config));
        Assert.Throws<InvalidDataException>(() => SourceExtractor.Extract(_source.Replace("i <= 4048", "i <= Main.maxItems"), _config));
    }

    [Test]
    public async Task ConcurrentExtractionsHaveNoSharedMutableState()
    {
        var expected = _inventory.ToJsonString();
        var copies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => SourceExtractor.Extract(_source, _config).ToJsonString())));
        Assert.That(copies.All(copy => copy == expected), Is.True);
    }

    [Test]
    public void CuratedSelectionRetainsSourceLinksAndCannotBeProductionEligible()
    {
        var before = _inventory.ToJsonString();
        var rules = CandidateBuilder.Build(_inventory)["rules"]!.AsArray();
        Assert.That(rules, Has.Count.EqualTo(16));
        Assert.That(rules.Count(r => r!["classification"]!.GetValue<string>() == "natural_impossibility_candidate"), Is.EqualTo(2));
        Assert.That(rules.All(r => r!["sources"]!.AsArray().Count > 0 && !r["qualification"]!["productionEligible"]!.GetValue<bool>()), Is.True);
        Assert.That(rules.All(r => r!["exceptions"]!["op"]!.GetValue<string>() == "any"), Is.True);
        Assert.That(_inventory.ToJsonString(), Is.EqualTo(before));
    }

    [Test]
    public void CuratedMechPredicatesKeepAllVersusAnyRatherThanUnioningLists()
    {
        var rules = CandidateBuilder.Build(_inventory)["rules"]!.AsArray();
        var anyMech = rules.Single(r => r!["id"]!.GetValue<string>() == "PG-POL-005")!;
        var allMech = rules.Single(r => r!["id"]!.GetValue<string>() == "PG-POL-006")!;
        Assert.That(anyMech["sourcePredicate"]!["op"]!.GetValue<string>(), Is.EqualTo("all"));
        Assert.That(allMech["sourcePredicate"]!["op"]!.GetValue<string>(), Is.EqualTo("any"));
        Assert.That(anyMech["condition"]!.ToJsonString(), Does.Contain("policy.mklpSubsetEnabled"));
        Assert.That(allMech["sourcePredicate"]!.ToJsonString(), Does.Contain("BossDType.TheTwins"));
    }

    [Test]
    public void CuratedSubsetsDocumentDeduplicationAndExcludedLaterRemoval()
    {
        var rules = CandidateBuilder.Build(_inventory)["rules"]!.AsArray();
        var eye = rules.Single(r => r!["id"]!.GetValue<string>() == "PG-POL-002")!;
        Assert.That(eye["curation"]!["deduplicatedSelectedItems"]!.GetValue<bool>(), Is.True);
        var fishron = rules.Single(r => r!["id"]!.GetValue<string>() == "PG-POL-009")!;
        Assert.That(fishron["items"]!.AsArray().Select(n => n!.GetValue<int>()), Does.Not.Contain(2623));
        Assert.That(fishron["curation"]!["excludedFromSelection"]![0]!.GetValue<int>(), Is.EqualTo(2623));
    }
}
