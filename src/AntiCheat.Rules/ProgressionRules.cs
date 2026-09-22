using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntiCheat.Core;
using AntiCheat.Progression;

namespace AntiCheat.Rules;

public enum ProgressionActionKind { InventorySync, PassiveReceipt, Equip, UseItem, CreateProjectile, PlaceTile, PlaceWall }

[JsonConverter(typeof(JsonStringEnumConverter<ProgressionSubjectKind>))]
public enum ProgressionSubjectKind { Item, Projectile, Tile, Wall }
public sealed record ProgressionEntityTarget(int TypeId, int? Style = null);
public sealed record ProgressionEntityRule(ProgressionSubjectKind SubjectKind,
    ImmutableArray<ProgressionEntityTarget> Targets, ProgressionRule Rule);

public sealed record ProgressionEntityCatalog
{
    public int SchemaVersion { get; init; }
    public ImmutableArray<ProgressionEntityRule> Rules { get; init; } = [];
    public static ProgressionEntityCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Entity progression catalog exceeds 1 MiB.");
        var catalog = JsonSerializer.Deserialize<ProgressionEntityCatalog>(stream, ProgressionCatalog.JsonOptions)
            ?? throw new InvalidDataException("Empty entity progression catalog.");
        if (catalog.SchemaVersion != 1 || catalog.Rules.IsDefaultOrEmpty || catalog.Rules.Length > 128)
            throw new InvalidDataException("Invalid entity progression catalog.");
        foreach (var entry in catalog.Rules)
            if (entry is null || !Enum.IsDefined(entry.SubjectKind) || entry.SubjectKind == ProgressionSubjectKind.Item
                || entry.Targets.IsDefaultOrEmpty || entry.Targets.Length > 1024 || entry.Rule is null
                || entry.Targets.Any(x => x.TypeId <= 0 || x.Style < 0 || (entry.SubjectKind != ProgressionSubjectKind.Tile && x.Style.HasValue))
                || !entry.Targets.Select(x => x.TypeId).ToHashSet().SetEquals(entry.Rule.Items))
                throw new InvalidDataException("Invalid entity progression target scope.");
        new ProgressionCatalog { SchemaVersion = 1, Rules = catalog.Rules.Select(x => x.Rule).ToImmutableArray() }.Validate();
        return catalog;
    }
}

/// <summary>Server-owned immutable snapshot. Accepted inventory data alone cannot establish any of these facts.</summary>
public sealed record TrustedProgressionWorld(
    long WorldEpoch, string RuntimeFingerprint, string Revision, string SeedSignature,
    DateTimeOffset CapturedUtc, DateTimeOffset ValidUntilUtc,
    bool Complete, bool TransitionSettled, ImmutableDictionary<string, JsonElement> Facts);

/// <summary>
/// Per-rule mechanism evidence supplied by an audited adapter or an isolated test fixture, never a packet/config flag.
/// It is deliberately separate from production admission, which belongs to the Core engine.
/// </summary>
public sealed record ProgressionMechanismEvidence(
    string RuleId, string RuleVersion, string TerrariaVersion, string RuntimeFingerprint,
    string WorldSeedSignature, string AuditReference, ImmutableHashSet<int> CoveredItems,
    ImmutableHashSet<ProgressionActionKind> CoveredActions,
    bool AcquisitionPathsVerified, bool LegalExceptionsComplete, bool PolicyApplicable)
{
    public ProgressionSubjectKind SubjectKind { get; init; } = ProgressionSubjectKind.Item;
    public ImmutableHashSet<ProgressionEntityTarget> CoveredTargets { get; init; } = [];
}

public sealed record ProgressionActionObservation(SessionKey Session, int ItemId, ProgressionActionKind Action)
{
    // ItemId is the type ID in SubjectKind's namespace; item callers retain the original API.
    public ProgressionSubjectKind SubjectKind { get; init; } = ProgressionSubjectKind.Item;
    public int? Style { get; init; }
    public SessionKey CurrentSession { get; init; }
    public string RuntimeFingerprint { get; init; } = "";
    public string TerrariaVersion { get; init; } = "";
    public DateTimeOffset NowUtc { get; init; }
    public bool ParseComplete { get; init; }
    public bool Authenticated { get; init; }
    public bool ClientOrigin { get; init; }
    public bool ActiveActionAttributed { get; init; }
    public bool BeforeSideEffects { get; init; }
    public bool IsSynchronization { get; init; }
    public bool AuthorizedServerAction { get; init; }
    public bool OtherPlayerCausedAction { get; init; }
    public TrustedProgressionWorld? World { get; init; }
    public ProgressionMechanismEvidence? Mechanism { get; init; }
}

/// <summary>
/// Executes the existing AND/OR candidate predicates with actual action, world, version and exception prerequisites.
/// A proven result is a business proof request; callers MUST submit it to the Core qualification gate.
/// No rule in the source catalog is production admitted by this evaluator.
/// </summary>
public static class ProgressionBusinessRules
{
    public const string Version = "m2.1";
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(2);
    private static readonly ImmutableHashSet<string> KnownBossEvents = new[]
    {
        "NA", "KingSlime", "EyeOfCthulhu", "EvilBoss", "Skeletron", "QueenBee", "Deerclops", "WallOfFlesh",
        "QueenSlime", "TheDestroyer", "TheTwins", "SkeletronPrime", "Plantera", "Golem", "DukeFishron",
        "EmpressOfLight", "LunaticCultist", "MoonLord"
    }.Select(x => "BossDType." + x).ToImmutableHashSet(StringComparer.Ordinal);

    public static BusinessRuleResult Evaluate(ProgressionRule rule, ProgressionActionObservation observation)
        => observation.SubjectKind == ProgressionSubjectKind.Item
            ? EvaluateCore(rule, observation)
            : ScopeResult(rule.Id, "different-type-namespace", ControlAction.Pass, Verdict.Pass);

    public static BusinessRuleResult Evaluate(ProgressionEntityRule entityRule, ProgressionActionObservation observation)
    {
        if (entityRule.SubjectKind != observation.SubjectKind)
            return ScopeResult(entityRule.Rule.Id, "different-type-namespace", ControlAction.Pass, Verdict.Pass);
        if (entityRule.SubjectKind == ProgressionSubjectKind.Tile && observation.Style is null
            && entityRule.Targets.Any(x => x.TypeId == observation.ItemId && x.Style.HasValue))
            return ScopeResult(entityRule.Rule.Id, "tile-style-unknown", ControlAction.Unknown, Verdict.Unknown);
        if (!entityRule.Targets.Any(x => x.TypeId == observation.ItemId && (!x.Style.HasValue || x.Style == observation.Style)))
            return ScopeResult(entityRule.Rule.Id, "entity-type-or-style-outside-rule", ControlAction.Pass, Verdict.Pass);
        var expectedAction = entityRule.SubjectKind switch
        {
            ProgressionSubjectKind.Projectile => ProgressionActionKind.CreateProjectile,
            ProgressionSubjectKind.Tile => ProgressionActionKind.PlaceTile,
            ProgressionSubjectKind.Wall => ProgressionActionKind.PlaceWall,
            _ => ProgressionActionKind.InventorySync
        };
        if (observation.Action != expectedAction)
            return ScopeResult(entityRule.Rule.Id, "entity-action-not-in-rule", ControlAction.Pass, Verdict.Pass);
        return EvaluateCore(entityRule.Rule, observation);
    }

    private static BusinessRuleResult ScopeResult(string id, string reason, ControlAction action, Verdict verdict)
        => new(id, Version, action, verdict, reason, false, false, ImmutableDictionary<string, string>.Empty);

    private static BusinessRuleResult EvaluateCore(ProgressionRule rule, ProgressionActionObservation observation)
    {
        var facts = ImmutableDictionary<string, string>.Empty
            .Add("classification", rule.Classification)
            .Add("subjectKind", observation.SubjectKind.ToString())
            .Add("typeId", observation.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("action", observation.Action.ToString())
            .Add("productionAdmission", "not-granted-by-candidate-data");
        BusinessRuleResult Result(ControlAction action, Verdict verdict, string reason, bool hit = false, bool complete = false)
            => new(rule.Id, Version, action, verdict, reason, hit, complete, facts);
        if (!rule.Items.Contains(observation.ItemId)) return Result(ControlAction.Pass, Verdict.Pass, "item-outside-rule");
        if (observation.Action is ProgressionActionKind.InventorySync or ProgressionActionKind.PassiveReceipt
            || observation.IsSynchronization || observation.AuthorizedServerAction || observation.OtherPlayerCausedAction)
            return Result(ControlAction.Pass, Verdict.Pass, "passive-sync-or-authorized-action");
        if (!Enum.IsDefined(observation.Action)) return Result(ControlAction.Unknown, Verdict.Unknown, "unknown-action");
        if (observation.CurrentSession != observation.Session || observation.Session.ServerRunId == Guid.Empty
            || observation.Session.WorldEpoch <= 0 || observation.Session.Generation <= 0 || observation.Session.Slot is < 0 or >= 256
            || !observation.Authenticated || !observation.ParseComplete
            || !observation.ClientOrigin || !observation.ActiveActionAttributed || !observation.BeforeSideEffects)
            return Result(ControlAction.Unknown, Verdict.Unknown, "action-attribution-or-cancellation-incomplete");
        var world = observation.World;
        if (world is null || !world.Complete || !world.TransitionSettled || world.WorldEpoch != observation.Session.WorldEpoch
            || string.IsNullOrWhiteSpace(world.Revision) || world.Revision.Length > 256 || string.IsNullOrWhiteSpace(world.SeedSignature)
            || world.RuntimeFingerprint != observation.RuntimeFingerprint || string.IsNullOrWhiteSpace(observation.RuntimeFingerprint)
            || world.CapturedUtc > observation.NowUtc || world.ValidUntilUtc < observation.NowUtc
            || observation.NowUtc - world.CapturedUtc > MaximumSnapshotAge || world.Facts is null || world.Facts.Count > 256)
            return Result(ControlAction.Unknown, Verdict.Unknown, "world-snapshot-incomplete-stale-or-transitioning");
        if (world.Facts.TryGetValue("currentbossdefeated", out var bossEvent)
            && (bossEvent.ValueKind != JsonValueKind.String || !KnownBossEvents.Contains(bossEvent.GetString()!)))
            return Result(ControlAction.Unknown, Verdict.Unknown, "same-event-boss-value-unrecognized");
        var prohibition = ProgressionEvaluator.Evaluate(rule.Condition, world.Facts);
        var exception = ProgressionEvaluator.Evaluate(rule.Exceptions, world.Facts);
        facts = facts.Add("worldRevision", world.Revision).Add("prohibition", prohibition.ToString()).Add("exception", exception.ToString());
        if (prohibition == Truth.False || exception == Truth.True)
            return Result(ControlAction.Pass, Verdict.Pass, exception == Truth.True ? "legal-exception" : "progress-condition-false");
        if (prohibition != Truth.True || exception != Truth.False)
            return Result(ControlAction.Unknown, Verdict.Unknown, "predicate-or-exceptions-unknown");
        var mechanism = observation.Mechanism;
        if (mechanism is null || mechanism.RuleId != rule.Id || mechanism.RuleVersion != Version
            || mechanism.SubjectKind != observation.SubjectKind
            || mechanism.TerrariaVersion != observation.TerrariaVersion
            || observation.TerrariaVersion is not ("1.4.5.6" or "1.4.5.8")
            || mechanism.RuntimeFingerprint != observation.RuntimeFingerprint || mechanism.WorldSeedSignature != world.SeedSignature
            || string.IsNullOrWhiteSpace(mechanism.AuditReference) || mechanism.AuditReference.Length > 256
            || mechanism.CoveredItems is null || !mechanism.CoveredItems.Contains(observation.ItemId)
            || mechanism.CoveredActions is null || !mechanism.CoveredActions.Contains(observation.Action)
            || !mechanism.LegalExceptionsComplete)
            return Result(ControlAction.Unknown, Verdict.Unknown, "exact-version-mechanism-or-exception-evidence-missing", true);
        if (observation.SubjectKind == ProgressionSubjectKind.Tile
            && (mechanism.CoveredTargets is null || !mechanism.CoveredTargets.Contains(new(observation.ItemId, observation.Style))))
            return Result(ControlAction.Unknown, Verdict.Unknown, "exact-tile-style-mechanism-unverified", true);
        if (rule.Classification == "natural_impossibility_candidate" && !mechanism.AcquisitionPathsVerified)
            return Result(ControlAction.Unknown, Verdict.Unknown, "natural-acquisition-paths-unverified", true);
        if (rule.Classification == "server_policy" && !mechanism.PolicyApplicable)
            return Result(ControlAction.Unknown, Verdict.Unknown, "explicit-policy-not-applicable", true);
        if (rule.Classification is not ("natural_impossibility_candidate" or "server_policy"))
            return Result(ControlAction.Unknown, Verdict.Unknown, "classification-unsupported", true);
        facts = facts.Add("mechanismAudit", mechanism.AuditReference).Add("terrariaVersion", observation.TerrariaVersion);
        return Result(ControlAction.Block, Verdict.ProvenCheat,
            rule.Classification == "server_policy" ? "proven-active-progress-policy-violation" : "proven-active-natural-progress-violation", true, true);
    }
}
