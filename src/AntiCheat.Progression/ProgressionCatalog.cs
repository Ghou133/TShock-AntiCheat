using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntiCheat.Progression;

public enum Truth { Unknown, False, True }
public enum CandidateDecision { Pass, Unknown }

public sealed record Condition
{
    public string Op { get; init; } = "";
    public string? Name { get; init; }
    [JsonPropertyName("equals")]
    public JsonElement Expected { get; init; }
    public bool? Value { get; init; }
    public Condition? Arg { get; init; }
    public ImmutableArray<Condition> Args { get; init; } = [];
}

public sealed record Qualification
{
    public bool ProductionEligible { get; init; }
    public string Baseline1456 { get; init; } = "acquisition_unverified";
    public string Target1458 { get; init; } = "blocked";
}

public sealed record ProgressionRule
{
    public string Id { get; init; } = "";
    public ImmutableArray<int> Items { get; init; } = [];
    public string Classification { get; init; } = "";
    public Condition Condition { get; init; } = new();
    public Condition Exceptions { get; init; } = new();
    public ImmutableArray<string> RequiredContexts { get; init; } = [];
    public ImmutableArray<string> ProofPreconditions { get; init; } = [];
    public ImmutableArray<JsonElement> Sources { get; init; } = [];
    public Qualification Qualification { get; init; } = new();
}

public sealed record CandidateAssessment(string RuleId, Truth Prohibition, Truth Exception,
    CandidateDecision Decision, string Reason)
{
    // Data extraction is not admission. This layer has no ban API and never blocks native interactions.
    public bool AutomaticBanAllowed => false;
    public bool BlockCurrentAction => false;
}

public sealed record ProgressionCatalog
{
    public int SchemaVersion { get; init; }
    public ImmutableArray<ProgressionRule> Rules { get; init; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ProgressionCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Candidate catalog exceeds 4 MiB budget.");
        var catalog = JsonSerializer.Deserialize<ProgressionCatalog>(stream, JsonOptions)
            ?? throw new InvalidDataException("Empty catalog.");
        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Rules.IsDefault || Rules.Length > 4096) throw new InvalidDataException("Unsupported catalog schema/capacity.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 || !ids.Add(rule.Id)
                || rule.Items.IsDefaultOrEmpty || rule.Items.Length > 10000 || rule.Items.Any(x => x <= 0)
                || rule.Classification is not ("natural_impossibility_candidate" or "server_policy")
                || rule.Sources.IsDefaultOrEmpty || rule.RequiredContexts.IsDefaultOrEmpty || rule.ProofPreconditions.IsDefaultOrEmpty)
                throw new InvalidDataException("Invalid candidate rule.");
            if (rule.Qualification is null || rule.Qualification.ProductionEligible)
                throw new InvalidDataException("A candidate data file cannot admit production hard rules.");
            ValidateCondition(rule.Condition, 0);
            ValidateCondition(rule.Exceptions, 0);
        }
    }

    private static void ValidateCondition(Condition condition, int depth)
    {
        if (condition is null || depth > 24) throw new InvalidDataException("Invalid condition depth.");
        switch (condition.Op)
        {
            case "constant" when condition.Value.HasValue: return;
            case "fact" when !string.IsNullOrWhiteSpace(condition.Name) && condition.Name.Length <= 256 &&
                condition.Expected.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String or JsonValueKind.Number: return;
            case "not" when condition.Arg is not null: ValidateCondition(condition.Arg, depth + 1); return;
            case "all" or "any" when !condition.Args.IsDefaultOrEmpty && condition.Args.Length <= 128:
                foreach (var child in condition.Args) ValidateCondition(child, depth + 1);
                return;
            default: throw new InvalidDataException("Unsupported or incomplete condition.");
        }
    }
}

public static class ProgressionEvaluator
{
    public static Truth Evaluate(Condition condition, IReadOnlyDictionary<string, JsonElement> facts, int depth = 0)
    {
        if (condition is null || depth > 24) return Truth.Unknown;
        switch (condition.Op)
        {
            case "constant": return condition.Value switch { true => Truth.True, false => Truth.False, _ => Truth.Unknown };
            case "fact":
                if (condition.Name is null || !facts.TryGetValue(condition.Name, out var actual) || actual.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return Truth.Unknown;
                var expected = condition.Expected;
                if (actual.ValueKind != expected.ValueKind && !(IsBool(actual) && IsBool(expected))) return Truth.Unknown;
                return expected.ValueKind switch
                {
                    JsonValueKind.True or JsonValueKind.False => actual.GetBoolean() == expected.GetBoolean() ? Truth.True : Truth.False,
                    JsonValueKind.String => actual.GetString() == expected.GetString() ? Truth.True : Truth.False,
                    JsonValueKind.Number when actual.TryGetDecimal(out var a) && expected.TryGetDecimal(out var b) => a == b ? Truth.True : Truth.False,
                    _ => Truth.Unknown
                };
            case "not": return Evaluate(condition.Arg!, facts, depth + 1) switch { Truth.True => Truth.False, Truth.False => Truth.True, _ => Truth.Unknown };
            case "all":
            case "any":
                if (condition.Args.IsDefaultOrEmpty || condition.Args.Length > 128) return Truth.Unknown;
                bool unknown = false;
                foreach (var arg in condition.Args)
                {
                    var result = Evaluate(arg, facts, depth + 1);
                    if (condition.Op == "all" && result == Truth.False) return Truth.False;
                    if (condition.Op == "any" && result == Truth.True) return Truth.True;
                    unknown |= result == Truth.Unknown;
                }
                return unknown ? Truth.Unknown : condition.Op == "all" ? Truth.True : Truth.False;
            default: return Truth.Unknown;
        }
    }

    public static CandidateAssessment Assess(ProgressionRule rule, int itemId, IReadOnlyDictionary<string, JsonElement> facts)
    {
        if (!rule.Items.Contains(itemId)) return new(rule.Id, Truth.False, Truth.Unknown, CandidateDecision.Pass, "item-outside-rule");
        var condition = Evaluate(rule.Condition, facts);
        var exception = Evaluate(rule.Exceptions, facts);
        if (condition == Truth.False || exception == Truth.True)
            return new(rule.Id, condition, exception, CandidateDecision.Pass, exception == Truth.True ? "legal-exception" : "source-predicate-false");
        return new(rule.Id, condition, exception, CandidateDecision.Unknown,
            condition == Truth.Unknown || exception == Truth.Unknown ? "incomplete-context" : "candidate-not-admitted");
    }

    private static bool IsBool(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
