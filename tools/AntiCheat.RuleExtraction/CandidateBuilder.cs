using System.Text.Json.Nodes;

namespace AntiCheat.RuleExtraction;

/// <summary>Small, explicit research selection. This does not turn reference dictionary entries into qualified rules.</summary>
public static class CandidateBuilder
{
    private sealed record Selection(string Id, string Title, int[] Operations, int[]? ExcludeItems = null);
    private static readonly Selection[] Selected =
    [
        new("PG-NAT-001", "源表无条件不可获得候选 603/766", [7, 8]),
        new("PG-NAT-002", "源表物品 678 的特殊世界例外候选", [10]),
        new("PG-POL-001", "史莱姆王阶段服规候选", [36]),
        new("PG-POL-002", "克苏鲁之眼阶段服规候选", [39]),
        new("PG-POL-003", "独眼巨鹿阶段服规候选", [60]),
        new("PG-POL-004", "史莱姆皇后阶段服规候选", [87]),
        new("PG-POL-005", "任意机械 Boss 前：保留六个前提的 AND", [88]),
        new("PG-POL-006", "全部机械 Boss 前：保留三个缺失分支的 OR", [93]),
        new("PG-POL-007", "世纪之花范围候选：保留 1158 排除", [101]),
        new("PG-POL-008", "石巨人范围阶段服规候选", [111]),
        new("PG-POL-009", "猪龙鱼公爵阶段候选：暂不选取存在武器交换 Remove 的 2623", [116], [2623]),
        new("PG-POL-010", "光之女皇阶段服规候选", [118]),
        new("PG-POL-011", "拜月教邪教徒阶段服规候选", [121]),
        new("PG-POL-012", "月亮领主阶段服规候选", [125]),
        new("PG-POL-013", "音乐盒的可配置服规候选", [77]),
        new("PG-POL-014", "独眼巨鹿外观的可配置服规候选", [59])
    ];

    public static JsonObject Build(JsonObject inventory)
    {
        var rules = new JsonArray();
        foreach (var selection in Selected)
        {
            var operations = selection.Operations.Select(n => inventory["operations"]!.AsArray().OfType<JsonObject>().Single(o => o["sourceOrdinal"]!.GetValue<int>() == n)).ToArray();
            var classification = selection.Id.StartsWith("PG-NAT-", StringComparison.Ordinal) ? "natural_impossibility_candidate" : "server_policy";
            var sourcePredicate = Predicate(operations[0]);
            if (operations.Skip(1).Any(o => Predicate(o).ToJsonString() != sourcePredicate.ToJsonString()))
                throw new InvalidDataException("Cannot merge operations with different source predicates.");
            var condition = classification == "server_policy"
                ? All(Fact("policy.mklpSubsetEnabled", true), sourcePredicate.DeepClone())
                : sourcePredicate.DeepClone();
            var sourceIds = operations.SelectMany(o => o["itemDomain"]!["items"]!.AsArray().Select(n => n!.GetValue<int>())).ToArray();
            var items = sourceIds.Except(selection.ExcludeItems ?? []).Distinct().ToArray();
            rules.Add(new JsonObject
            {
                ["id"] = selection.Id,
                ["title"] = selection.Title,
                ["items"] = new JsonArray(items.Select(i => JsonValue.Create(i)).ToArray()),
                ["classification"] = classification,
                ["condition"] = condition,
                ["sourcePredicate"] = sourcePredicate,
                ["exceptions"] = Any(
                    Fact("exception.sourceWhitelistMatchesItem", true),
                    Fact("exception.authorizedServerGrant", true),
                    Fact("exception.legacyOrImportedAsset", true),
                    Fact("exception.passiveReceipt", true),
                    Fact("exception.otherPlayerCausedState", true)),
                ["requiredContexts"] = Strings("version_manifest", "world_progress_and_world_generation", "authenticated_account_and_session_generation", "active_action_attribution", "applicable_policy_and_authorizations", "legal_acquisition_exceptions"),
                ["proofPreconditions"] = Strings(
                    "Validate each item and acquisition path on the exact approved version; an existing item ID or upstream label is not proof.",
                    "Bind the active action to the authenticated account and current server session/world generations.",
                    "Exclude passive pickup, actions caused by other players, imported history, authorized grants and configured whitelist.",
                    "Preserve currentbossdefeated as a same-event exception; do not wait for repeat cheating or warning counts.",
                    "For server_policy rules, establish an applicable explicit policy; source server preferences are not vanilla impossibility.",
                    "Prove current-action cancellation before affected writes and broadcasts, then qualify first-sanction behavior in isolation.",
                    "Until all admission requirements are validated, a matching predicate remains a research candidate and never bans or freezes assets."),
                ["sources"] = new JsonArray(operations.Select(o => (JsonNode)new JsonObject
                {
                    ["repository"] = "https://github.com/NightKLP/TShock-GSKLP-Moderation",
                    ["commit"] = SourceExtractor.Commit, ["path"] = SourceExtractor.SourcePath,
                    ["lineStart"] = o["source"]!["lineStart"]!.GetValue<int>(), ["lineEnd"] = o["source"]!["lineEnd"]!.GetValue<int>(),
                    ["operationIds"] = Strings(o["id"]!.GetValue<string>())
                }).ToArray()),
                ["sourceScope"] = "Selected source-operation predicate only; not the final ordered dictionary result or a complete acquisition model.",
                ["curation"] = new JsonObject
                {
                    ["removedDictionaryGuards"] = "ContainsKey is an ordered collection implementation guard, not a game-world proof; original remains in mklp-operations.json.",
                    ["deduplicatedSelectedItems"] = sourceIds.Length != sourceIds.Distinct().Count(),
                    ["excludedFromSelection"] = new JsonArray((selection.ExcludeItems ?? []).Select(i => JsonValue.Create(i)).ToArray()),
                    ["addedApplicability"] = classification == "server_policy" ? "policy.mklpSubsetEnabled must be explicitly true; missing configuration remains Unknown." : "No natural impossibility is established by extraction.",
                    ["additionalExceptions"] = "Shared attribution/history/authorization exclusions are newly required safeguards; they are not claimed to exist in the original implementation."
                },
                ["qualification"] = SourceExtractor.Qualification()
            });
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["catalogPurpose"] = "Non-production research candidates for independent three-valued predicate tests.",
            ["sourceInventory"] = "mklp-operations.json",
            ["productionEligible"] = false,
            ["rules"] = rules
        };
    }

    private static JsonNode Predicate(JsonObject operation)
    {
        var paths = operation["conditionPath"]!.AsArray().OfType<JsonObject>()
            .Where(p => !p["expression"]!.GetValue<string>().Contains("getillegalitems.ContainsKey", StringComparison.Ordinal))
            .Select(p => p["branch"]!.GetValue<string>() == "then" ? Convert(p["ast"]!) : Not(Convert(p["ast"]!))).ToArray();
        return paths.Length switch { 0 => Constant(true), 1 => paths[0], _ => All(paths) };
    }
    private static JsonNode Convert(JsonNode source)
    {
        switch (source["op"]!.GetValue<string>())
        {
            case "parenthesized": case "cast": return Convert(source["arg"]!);
            case "literal": return Constant(source["value"]!.GetValue<bool>());
            case "reference":
                var name = source["name"]!.GetValue<string>();
                name = name switch
                {
                    "allowvanity" => "MKLP.Config.Main.Progression.AllowVanityCloth",
                    "allowmusicbox" => "MKLP.Config.Main.Progression.AllowMusicBox",
                    "allowdungeonrush" => "MKLP.Config.Main.Progression.AllowDungeonRush",
                    "allowtemplerush" => "MKLP.Config.Main.Progression.AllowTempleRush",
                    _ => name
                };
                return Fact(name, true);
            case "unary" when source["operator"]!.GetValue<string>() == "!": return Not(Convert(source["arg"]!));
            case "binary":
                var op = source["operator"]!.GetValue<string>();
                if (op == "&&") return All(Convert(source["left"]!), Convert(source["right"]!));
                if (op == "||") return Any(Convert(source["left"]!), Convert(source["right"]!));
                if (op is "==" or "!=" && source["left"]!["op"]!.GetValue<string>() == "reference" && source["right"]!["op"]!.GetValue<string>() == "reference")
                {
                    var equal = new JsonObject { ["op"] = "fact", ["name"] = source["left"]!["name"]!.GetValue<string>(), ["equals"] = source["right"]!["name"]!.GetValue<string>() };
                    return op == "==" ? equal : Not(equal);
                }
                break;
        }
        throw new InvalidDataException("Selected predicate needs explicit curation; unsupported expression: " + source.ToJsonString());
    }
    private static JsonObject Fact(string name, bool value) => new() { ["op"] = "fact", ["name"] = name, ["equals"] = value };
    private static JsonObject Constant(bool value) => new() { ["op"] = "constant", ["value"] = value };
    private static JsonObject Not(JsonNode argument) => new() { ["op"] = "not", ["arg"] = argument };
    private static JsonObject All(params JsonNode[] arguments) => new() { ["op"] = "all", ["args"] = new JsonArray(arguments) };
    private static JsonObject Any(params JsonNode[] arguments) => new() { ["op"] = "any", ["args"] = new JsonArray(arguments) };
    private static JsonArray Strings(params string[] values) => new(values.Select(v => JsonValue.Create(v)).ToArray());
}
