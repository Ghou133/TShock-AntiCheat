using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public readonly record struct ProjectileIdentity(int Spawner, int Index, int Generation);
public enum ProjectileOperation { Create, Update, Destroy }
public sealed record ProjectileObservation(ProjectileIdentity Identity, ProjectileOperation Operation, int Type);
public sealed record ProjectileState(ProjectileIdentity Identity, int OwnerSlot, SessionKey OwnerSession,
    int Type, bool Active);
public sealed record ProjectileAuthorityContext(RuleInputContext Input, SessionKey SnapshotSession,
    long SnapshotWorldEpoch, long SnapshotRevision, long CurrentRevision, ProjectileState? Existing,
    bool LookupComplete, bool IsFreshCreation, bool ServerOrigin, bool AuthorizedOwnershipTransfer,
    bool OwnershipTransitionInProgress, int MaximumIdentitiesPerSpawner, int ProjectileTypeExclusiveMax,
    SessionKey RetainedOwnerSession = default);

public enum ProjectileSourceConstraint { Allowed, ServerOnly, VerifiedParent, VerifiedWeapon, Unknown }
public sealed record ProjectileCategory(ProjectileSourceConstraint Constraint,
    ImmutableHashSet<int> AllowedParentTypes, ImmutableHashSet<int> AllowedWeaponTypes);
public sealed record ProjectileCatalog(string RuntimeFingerprint, string DataVersion,
    ImmutableDictionary<int, ProjectileCategory> Types, bool CategoryRulesComplete);
/// <summary>A correlated source is an independently observed creation cause, never the current held item alone.</summary>
public sealed record ProjectileSourceContext(RuleInputContext Input, bool FreshCreation,
    bool ScopedServerOrPluginCreation, bool SourceCorrelationComplete, int? CorrelatedParentType,
    int? CorrelatedWeaponType, bool TransitionInProgress);
public sealed record SummonBudgetContext(RuleInputContext Input, SessionKey SnapshotSession,
    double ExistingSlots, double IncomingSlots, double ReplacedSlots, double MaximumSlots,
    bool BudgetDefinitionComplete, bool ReplacementCausalityComplete, bool DespawnOrEquipmentTransition);
public sealed record FishingContext(RuleInputContext Input, SessionKey SnapshotSession,
    int ExistingBobbersForCast, int IncomingBobbers, int ReplacedBobbers, int MaximumBobbersForCast,
    bool CastCorrelationComplete, bool MechanismDefinitionComplete, bool DespawnOrCastTransition);

/// <summary>Target projectile key, actual owner and versioned mechanism checks; no universal summon/bobber limit.</summary>
public static class ProjectileRules
{
    public const string AuthorityRuleId = "C1.ProjectileAuthority";
    public const string AuthorityVersion = "1.2.0";
    public const string CategoryRuleId = "C2.ProjectileSource";
    public const string BudgetRuleId = "C3.SummonBudget";
    public const string FishingRuleId = "C4.FishingMechanism";

    public static BusinessRuleResult EvaluateAuthority(ProjectileObservation observation, ProjectileAuthorityContext context)
        => EvaluateAuthorityCore(observation, context) with { Version = AuthorityVersion };

    private static BusinessRuleResult EvaluateAuthorityCore(ProjectileObservation observation, ProjectileAuthorityContext context)
    {
        var facts = RuleResults.Facts(("operation", observation.Operation), ("spawner", observation.Identity.Spawner),
            ("index", observation.Identity.Index), ("generation", observation.Identity.Generation),
            ("type", observation.Type), ("existingType", context.Existing?.Type),
            ("senderSlot", context.Input.Session.Slot), ("ownerSlot", context.Existing?.OwnerSlot));
        if (!context.Input.ParserComplete) return RuleResults.Unknown(AuthorityRuleId, "projectile-parser-incomplete", facts);
        if (context.ServerOrigin || !context.Input.ClientOrigin)
            return RuleResults.Pass(AuthorityRuleId, "server-origin-projectile-operation", facts);
        if (!context.Input.VersionMatched || context.SnapshotSession != context.Input.Session ||
            context.SnapshotWorldEpoch != context.Input.Session.WorldEpoch || context.SnapshotRevision != context.CurrentRevision)
            return RuleResults.Unknown(AuthorityRuleId, "projectile-snapshot-stale", facts);
        if (context.MaximumIdentitiesPerSpawner <= 0 || context.ProjectileTypeExclusiveMax <= 0)
            return RuleResults.Unknown(AuthorityRuleId, "projectile-domain-unverified", facts);
        if (observation.Identity.Spawner is < 0 or > 255 || observation.Identity.Index < 0 ||
            observation.Identity.Index >= context.MaximumIdentitiesPerSpawner || observation.Identity.Generation < 0)
            return RuleResults.Block(AuthorityRuleId, "projectile-key-out-of-range", facts);
        if (observation.Operation != ProjectileOperation.Destroy &&
            (observation.Type <= 0 || observation.Type >= context.ProjectileTypeExclusiveMax))
            return RuleResults.Block(AuthorityRuleId, "projectile-type-out-of-range", facts);
        if (context.OwnershipTransitionInProgress) return RuleResults.Unknown(AuthorityRuleId, "ownership-transition", facts);
        if (context.AuthorizedOwnershipTransfer) return RuleResults.Pass(AuthorityRuleId, "scoped-authorized-projectile-transfer", facts);
        if (!context.LookupComplete) return RuleResults.Unknown(AuthorityRuleId, "projectile-key-lookup-incomplete", facts);
        if (observation.Operation == ProjectileOperation.Create && context.IsFreshCreation && context.Existing is null)
        {
            return observation.Identity.Spawner == context.Input.Session.Slot
                ? RuleResults.Pass(AuthorityRuleId, "own-fresh-projectile-key", facts)
                : RuleResults.Candidate(AuthorityRuleId, "foreign-spawner-new-projectile", context.Input, facts);
        }
        var existing = context.Existing;
        if (observation.Operation == ProjectileOperation.Destroy && (existing is null || !existing.Active))
        {
            // MessageBuffer29 still forwards an absent/inactive key to peers. The server's
            // own Kill of a remote-owned projectile does not send that owner's packet29.
            // Keep exact negative ownership evidence, but never turn an unavailable local
            // entity or an expired cache into proof that peers have already cleaned up.
            bool exact = existing is not null && existing.Identity == observation.Identity;
            if (exact && existing!.OwnerSlot != context.Input.Session.Slot)
                return RuleResults.Block(AuthorityRuleId, "foreign-owner-inactive-projectile-cleanup", facts);
            var retained = exact && existing!.OwnerSession.ServerRunId != Guid.Empty
                ? existing.OwnerSession : context.RetainedOwnerSession;
            if (retained.ServerRunId != Guid.Empty && retained != context.Input.Session)
                return RuleResults.Block(AuthorityRuleId, "projectile-belongs-to-previous-session", facts);
            if (exact && existing!.OwnerSlot == context.Input.Session.Slot)
                return RuleResults.Unknown(AuthorityRuleId, "native-cleanup-server-inactive", facts);
            if (existing is null && observation.Identity.Spawner == context.Input.Session.Slot)
                return RuleResults.Unknown(AuthorityRuleId, "native-cleanup-key-missing", facts);
            return RuleResults.Block(AuthorityRuleId, "projectile-key-stale-or-inactive", facts);
        }
        if (existing is null || !existing.Active || existing.Identity != observation.Identity)
            return RuleResults.Block(AuthorityRuleId, "projectile-key-stale-or-inactive", facts);
        if (existing.OwnerSlot != context.Input.Session.Slot)
        {
            // Native Projectile.Update can emit packet29 for an out-of-bounds remote-owned
            // entity, without an owner guard. MessageBuffer29 ignores such client requests.
            // Keep the same no-side-effect rejection; foreign destruction alone is no proof.
            if (observation.Operation == ProjectileOperation.Destroy)
                return RuleResults.Block(AuthorityRuleId, "foreign-owner-projectile-destroy-native-noop", facts);
            return RuleResults.Candidate(AuthorityRuleId, "foreign-owner-projectile-mutation", context.Input, facts);
        }
        if (existing.OwnerSession.ServerRunId == Guid.Empty)
            return RuleResults.Unknown(AuthorityRuleId, "projectile-owner-session-provenance-unavailable", facts);
        if (existing.OwnerSession != context.Input.Session)
            return RuleResults.Block(AuthorityRuleId, "projectile-belongs-to-previous-session", facts);
        // A transferred projectile can retain another spawner's key. Destruction is authorized by actual owner.
        return RuleResults.Pass(AuthorityRuleId, "current-session-owns-projectile", facts);
    }

    public static BusinessRuleResult EvaluateSource(ProjectileObservation observation, ProjectileSourceContext context,
        ProjectileCatalog catalog)
    {
        var facts = RuleResults.Facts(("type", observation.Type), ("operation", observation.Operation),
            ("parentType", context.CorrelatedParentType), ("weaponType", context.CorrelatedWeaponType), ("dataVersion", catalog.DataVersion));
        if (observation.Operation != ProjectileOperation.Create || !context.FreshCreation)
            return RuleResults.Pass(CategoryRuleId, "existing-projectile-is-not-independent-creation", facts);
        if (context.ScopedServerOrPluginCreation || !context.Input.ClientOrigin)
            return RuleResults.Pass(CategoryRuleId, "scoped-authorized-projectile-source", facts);
        if (!context.Input.VersionMatched || catalog.RuntimeFingerprint != context.Input.RuntimeFingerprint ||
            string.IsNullOrWhiteSpace(catalog.DataVersion) || !catalog.CategoryRulesComplete)
            return RuleResults.Unknown(CategoryRuleId, "projectile-source-table-unverified", facts);
        if (!catalog.Types.TryGetValue(observation.Type, out var category) || category.Constraint == ProjectileSourceConstraint.Unknown)
            return RuleResults.Unknown(CategoryRuleId, "projectile-source-mechanism-unmodeled", facts);
        if (context.TransitionInProgress) return RuleResults.Unknown(CategoryRuleId, "projectile-source-transition", facts);
        if (category.Constraint == ProjectileSourceConstraint.Allowed)
            return RuleResults.Pass(CategoryRuleId, "client-projectile-type-allowed", facts);
        if (category.Constraint == ProjectileSourceConstraint.ServerOnly)
            return RuleResults.Candidate(CategoryRuleId, "client-created-verified-server-only-projectile", context.Input, facts);
        if (!context.SourceCorrelationComplete)
            return RuleResults.Unknown(CategoryRuleId, "independent-projectile-cause-unavailable", facts);
        if (category.Constraint == ProjectileSourceConstraint.VerifiedParent)
        {
            if (context.CorrelatedParentType is not int parent)
                return RuleResults.Unknown(CategoryRuleId, "correlated-parent-cause-missing", facts);
            return category.AllowedParentTypes.Contains(parent)
                ? RuleResults.Pass(CategoryRuleId, "verified-parent-projectile-source", facts)
                : RuleResults.Candidate(CategoryRuleId, "projectile-incompatible-with-verified-parent", context.Input, facts);
        }
        if (context.CorrelatedWeaponType is not int weapon)
            return RuleResults.Unknown(CategoryRuleId, "correlated-weapon-cause-missing", facts);
        return category.AllowedWeaponTypes.Contains(weapon)
            ? RuleResults.Pass(CategoryRuleId, "verified-weapon-projectile-source", facts)
            : RuleResults.Candidate(CategoryRuleId, "projectile-incompatible-with-verified-weapon", context.Input, facts);
    }

    public static BusinessRuleResult EvaluateSummonBudget(SummonBudgetContext context)
    {
        var facts = RuleResults.Facts(("existingSlots", context.ExistingSlots), ("incomingSlots", context.IncomingSlots),
            ("replacedSlots", context.ReplacedSlots), ("maximumSlots", context.MaximumSlots));
        if (!context.Input.ParserComplete || !context.Input.VersionMatched || context.SnapshotSession != context.Input.Session ||
            !context.Input.SnapshotComplete || !context.BudgetDefinitionComplete || !context.ReplacementCausalityComplete)
            return RuleResults.Unknown(BudgetRuleId, "summon-budget-context-incomplete", facts);
        if (!double.IsFinite(context.ExistingSlots) || !double.IsFinite(context.IncomingSlots) ||
            !double.IsFinite(context.ReplacedSlots) || !double.IsFinite(context.MaximumSlots) ||
            context.ExistingSlots < 0 || context.IncomingSlots < 0 || context.ReplacedSlots < 0 ||
            context.MaximumSlots < 0 || context.ReplacedSlots > context.ExistingSlots)
            return RuleResults.Unknown(BudgetRuleId, "invalid-server-budget-snapshot", facts);
        if (context.DespawnOrEquipmentTransition) return RuleResults.Unknown(BudgetRuleId, "summon-replacement-or-equipment-transition", facts);
        var proposed = context.ExistingSlots + context.IncomingSlots - context.ReplacedSlots;
        return proposed <= context.MaximumSlots + 0.000001
            ? RuleResults.Pass(BudgetRuleId, "within-versioned-summon-budget", facts)
            : RuleResults.Block(BudgetRuleId, "summon-resource-budget-exceeded", facts, resource: true);
    }

    public static BusinessRuleResult EvaluateFishing(FishingContext context)
    {
        var facts = RuleResults.Facts(("existingBobbers", context.ExistingBobbersForCast), ("incomingBobbers", context.IncomingBobbers),
            ("replacedBobbers", context.ReplacedBobbers), ("maximumForCast", context.MaximumBobbersForCast));
        if (!context.Input.ParserComplete || !context.Input.VersionMatched || context.SnapshotSession != context.Input.Session ||
            !context.Input.SnapshotComplete || !context.CastCorrelationComplete || !context.MechanismDefinitionComplete)
            return RuleResults.Unknown(FishingRuleId, "fishing-mechanism-or-cast-incomplete", facts);
        if (context.ExistingBobbersForCast < 0 || context.IncomingBobbers < 0 || context.ReplacedBobbers < 0 ||
            context.ReplacedBobbers > context.ExistingBobbersForCast || context.MaximumBobbersForCast <= 0)
            return RuleResults.Unknown(FishingRuleId, "invalid-server-fishing-snapshot", facts);
        if (context.DespawnOrCastTransition) return RuleResults.Unknown(FishingRuleId, "fishing-cast-or-despawn-transition", facts);
        var proposed = (long)context.ExistingBobbersForCast + context.IncomingBobbers - context.ReplacedBobbers;
        return proposed <= context.MaximumBobbersForCast
            ? RuleResults.Pass(FishingRuleId, "legal-versioned-bobber-mechanism", facts)
            : RuleResults.Block(FishingRuleId, "cast-bobber-resource-budget-exceeded", facts, resource: true);
    }
}
