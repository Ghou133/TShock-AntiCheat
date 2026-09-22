using System.Text.Json;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

[TestFixture]
public sealed class ProgressionItemIdTests
{
    [Test]
    public void ReportsRealRuntimeItemIdRangesWithoutPromotingAcquisitionProof()
    {
        bool target = TargetRuntime.Inspect().Verified;
        Assert.That(target || BaselineRuntime.Inspect().ExactTemporaryBaseline, Is.True, "ID range checks require an actual locked runtime.");
        string dataDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "progression");
        using var candidates = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "candidates.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "mklp-operations.json")));
        int itemCount = ItemID.Count;
        var rules = candidates.RootElement.GetProperty("rules").EnumerateArray().Select(rule => new
        {
            id = rule.GetProperty("id").GetString(),
            items = rule.GetProperty("items").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
            productionEligible = rule.GetProperty("qualification").GetProperty("productionEligible").GetBoolean()
        }).ToArray();
        var rawDomains = inventory.RootElement.GetProperty("operations").EnumerateArray().Select(operation => new
        {
            id = operation.GetProperty("id").GetString(),
            items = operation.GetProperty("itemDomain").TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(item => item.GetInt32()).ToArray() : null
        }).ToArray();
        var rawItems = rawDomains.SelectMany(domain => domain.items ?? []).ToArray();
        var rawMissingIds = rawItems.Distinct().Where(item => item < 1 || item >= itemCount).Order().ToArray();
        var report = new
        {
            scope = Terraria.Main.versionNumber + " ID range only; not acquisition legality or production qualification",
            actualOtApiSha256 = target ? TargetRuntime.OtApiSha256 : BaselineRuntime.OtApiSha256,
            itemIdCount = itemCount,
            candidateRuleCount = rules.Length,
            candidateItemOccurrences = rules.Sum(rule => rule.items.Length),
            candidateDistinctItems = rules.SelectMany(rule => rule.items).Distinct().Count(),
            rawItemOccurrences = rawItems.Length,
            rawDistinctItems = rawItems.Distinct().Count(),
            rawSymbolicOrUnexpandedOperationIds = rawDomains.Where(domain => domain.items is null).Select(domain => domain.id).ToArray(),
            rawOutOfBaselineRangeIds = rawMissingIds,
            rawStatus = rawMissingIds.Length == 0 ? "all_expanded_integer_ids_within_baseline_range" : "version_gaps_preserved_in_source_inventory",
            sourceInventoryModified = false,
            acquisitionVerified = false,
            productionQualified = false
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        string reportPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "baseline-item-id-audit.json");
        File.WriteAllText(reportPath, json);
        TestContext.AddTestAttachment(reportPath, "Temporary baseline item ID audit; no acquisition proof.");
        Assert.That(rules, Is.Not.Empty);
        foreach (var rule in rules)
        {
            Assert.That(rule.items, Is.Not.Empty, rule.id);
            Assert.That(rule.productionEligible, Is.False, rule.id);
            Assert.That(rule.items.Where(item => item < 1 || item >= itemCount), Is.Empty, rule.id);
        }
    }
}
