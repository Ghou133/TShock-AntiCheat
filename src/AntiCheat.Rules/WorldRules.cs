using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum WorldActionKind { TileEdit, LiquidSet, SignWrite, ContainerOpen, ObjectPlacement, DisplayEntityInteraction, EnvironmentalChange }

/// <summary>Counts come from the actual accepted runtime; no old-version numeric tables are embedded here.</summary>
public sealed record WorldGeometrySnapshot(long WorldEpoch, string RuntimeFingerprint, string Revision,
    int Width, int Height, int TileTypeCount, int WallTypeCount, int SignCapacity, bool Complete);

/// <summary>Permission result for this exact event/target. Whole-object permission requires a complete footprint.</summary>
public sealed record WorldAuthorizationSnapshot(SessionKey Session, Guid EventId, WorldActionKind Kind, int X, int Y,
    int TargetId, long Revision, bool Complete, bool? Allowed, bool? InRange, bool FootprintComplete);

public sealed record WorldActionObservation(SessionKey Session, WorldActionKind Kind, int X, int Y)
{
    public SessionKey CurrentSession { get; init; }
    public Guid EventId { get; init; }
    public string RuntimeFingerprint { get; init; } = "";
    public bool ParseComplete { get; init; }
    public bool ClientOrigin { get; init; }
    public bool BeforeSideEffects { get; init; }
    public bool AlreadyCancelled { get; init; }
    public int EditAction { get; init; }
    public int EditData { get; init; }
    public int LiquidAmount { get; init; }
    public int LiquidType { get; init; }
    public int TargetId { get; init; } = -1;
    public bool? TargetBindingValid { get; init; }
    public bool? BudgetAllowed { get; init; }
    public WorldGeometrySnapshot? Geometry { get; init; }
    public WorldAuthorizationSnapshot? Authorization { get; init; }
}

/// <summary>Version-scoped safety and region controls. None of these decisions assert account cheating.</summary>
public static class WorldRules
{
    public const string Version = "m2.1";

    public static BusinessRuleResult Evaluate(WorldActionObservation observation)
    {
        var facts = ImmutableDictionary<string, string>.Empty.Add("kind", observation.Kind.ToString())
            .Add("x", observation.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("y", observation.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        BusinessRuleResult Result(string suffix, ControlAction action, Verdict verdict, string reason, bool matched = false)
            => new("WORLD-" + suffix, Version, action, verdict, reason, matched, false, facts);
        if (observation.AlreadyCancelled) return Result("CORE", ControlAction.Block, Verdict.Unknown, "preserve-core-cancellation");
        if (observation.Kind == WorldActionKind.EnvironmentalChange)
            return Result("CONTEXT", ControlAction.Unknown, Verdict.Unknown, "environment-has-no-direct-player-attribution");
        if (!Enum.IsDefined(observation.Kind) || !observation.ParseComplete || !observation.ClientOrigin
            || !observation.BeforeSideEffects || observation.CurrentSession != observation.Session)
            return Result("CONTEXT", ControlAction.Unknown, Verdict.Unknown, "event-context-incomplete");
        var geometry = observation.Geometry;
        if (geometry is null || !geometry.Complete || geometry.WorldEpoch != observation.Session.WorldEpoch
            || geometry.RuntimeFingerprint != observation.RuntimeFingerprint || string.IsNullOrWhiteSpace(observation.RuntimeFingerprint)
            || string.IsNullOrWhiteSpace(geometry.Revision) || geometry.Width <= 0 || geometry.Height <= 0)
            return Result("CONTEXT", ControlAction.Unknown, Verdict.Unknown, "geometry-version-or-generation-unknown");
        if (observation.X < 0 || observation.Y < 0 || observation.X >= geometry.Width || observation.Y >= geometry.Height)
            return Result("BOUNDS", ControlAction.Block, Verdict.UnsafeInput, "world-coordinate-outside-bounds", true);
        if (observation.Kind == WorldActionKind.TileEdit)
        {
            // Target GetDataHandlers.EditAction explicitly assigns 0..23. Unknown actions are safety-only.
            if (observation.EditAction is < 0 or > 23 || observation.EditData < 0)
                return Result("TILE-DOMAIN", ControlAction.Block, Verdict.UnsafeInput, "tile-action-or-data-outside-domain", true);
            if ((observation.EditAction is 1 or 21 && geometry.TileTypeCount <= 0)
                || (observation.EditAction is 3 or 22 && geometry.WallTypeCount <= 0))
                return Result("CONTEXT", ControlAction.Unknown, Verdict.Unknown, "required-placement-type-count-unknown");
            if ((observation.EditAction is 1 or 21 && observation.EditData >= geometry.TileTypeCount)
                || (observation.EditAction is 3 or 22 && observation.EditData >= geometry.WallTypeCount))
                return Result("TILE-DOMAIN", ControlAction.Block, Verdict.UnsafeInput, "tile-or-wall-type-outside-runtime", true);
        }
        if (observation.Kind == WorldActionKind.ObjectPlacement)
        {
            if (geometry.TileTypeCount <= 0) return Result("CONTEXT", ControlAction.Unknown, Verdict.Unknown, "object-type-count-unknown");
            if (observation.EditData < 0 || observation.EditData >= geometry.TileTypeCount)
                return Result("OBJECT-DOMAIN", ControlAction.Block, Verdict.UnsafeInput, "object-type-outside-runtime", true);
        }
        if (observation.Kind == WorldActionKind.LiquidSet && (observation.LiquidAmount is < 0 or > 255
            || (observation.LiquidType is < 0 or > 3 && !(observation.LiquidAmount == 0 && observation.LiquidType == 255))))
            return Result("LIQUID-DOMAIN", ControlAction.Block, Verdict.UnsafeInput, "liquid-value-outside-runtime-domain", true);
        if (observation.Kind == WorldActionKind.SignWrite)
        {
            if (geometry.SignCapacity <= 0) return Result("CONTEXT", ControlAction.Unknown, Verdict.Unknown, "sign-capacity-unknown");
            if (observation.TargetId < 0 || observation.TargetId >= geometry.SignCapacity)
                return Result("SIGN-DOMAIN", ControlAction.Block, Verdict.UnsafeInput, "sign-index-outside-runtime", true);
        }
        if (observation.BudgetAllowed == false)
            return Result("BUDGET", ControlAction.Block, Verdict.ResourceAbuse, "bounded-world-request-budget-exhausted", true);
        var authorization = observation.Authorization;
        bool authorizationMatches = authorization is { Complete: true } && authorization.Session == observation.Session
            && authorization.EventId == observation.EventId && observation.EventId != Guid.Empty
            && authorization.Kind == observation.Kind && authorization.X == observation.X && authorization.Y == observation.Y
            && authorization.TargetId == observation.TargetId && authorization.Revision >= 0;
        if (!authorizationMatches) return Result("AUTHORIZATION", ControlAction.Unknown, Verdict.Unknown, "exact-target-authorization-unknown");
        if (observation.Kind is WorldActionKind.ObjectPlacement or WorldActionKind.DisplayEntityInteraction
            && !authorization!.FootprintComplete)
            return Result("AUTHORIZATION", ControlAction.Unknown, Verdict.Unknown, "object-footprint-incomplete");
        if (authorization!.Allowed == false || authorization.InRange == false)
            return Result("AUTHORIZATION", ControlAction.Block, Verdict.UnsafeInput, "world-operation-permission-or-range-denied");
        if (observation.Kind is WorldActionKind.SignWrite or WorldActionKind.ContainerOpen or WorldActionKind.DisplayEntityInteraction)
        {
            if (observation.TargetBindingValid == false)
                return Result("TARGET", ControlAction.Block, Verdict.UnsafeInput, "object-target-binding-mismatch", true);
            if (observation.TargetBindingValid is null)
                return Result("TARGET", ControlAction.Unknown, Verdict.Unknown, "object-target-binding-unknown");
        }
        if (authorization.Allowed is null || authorization.InRange is null)
            return Result("AUTHORIZATION", ControlAction.Unknown, Verdict.Unknown, "permission-or-range-unknown");
        return Result("PASS", ControlAction.Pass, Verdict.Pass, "world-request-valid");
    }
}

public sealed record TileRollbackEvidence(Guid IncidentId, SessionKey CauseSession, int X, int Y,
    long BeforeRevision, long AfterRevision, string BeforeStateHash, string AfterStateHash,
    ImmutableArray<byte> BeforeState, DateTimeOffset ChangedUtc,
    bool CauseExclusive, bool SimpleTileOnly, bool NoItemOrEntitySideEffects, string RuntimeFingerprint);

public sealed record CurrentTileVersion(long WorldEpoch, int X, int Y, long Revision, string StateHash, string RuntimeFingerprint);
public sealed record TileRestorePlan(Guid IncidentId, SessionKey CauseSession, CurrentTileVersion ExpectedCurrent,
    string RestoreStateHash, ImmutableArray<byte> RestoreState);
public sealed record TileRollbackAssessment(bool CanRestore, string Reason, TileRestorePlan? Plan = null);

/// <summary>
/// Plans one causal tile restoration; no world writes or item refunds. The game-thread adapter must atomically compare
/// ExpectedCurrent and restore, with incident-id deduplication. It must not apply a plan after an unchecked await.
/// </summary>
public static class LimitedTileRollback
{
    public const int MaximumSnapshotBytes = 512;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);

    public static TileRollbackAssessment Plan(TileRollbackEvidence evidence, CurrentTileVersion current,
        SessionKey currentSession, DateTimeOffset nowUtc)
    {
        if (evidence.IncidentId == Guid.Empty || evidence.CauseSession != currentSession
            || currentSession.ServerRunId == Guid.Empty || currentSession.Generation <= 0 || currentSession.WorldEpoch <= 0
            || currentSession.Slot is < 0 or >= 256
            || evidence.CauseSession.WorldEpoch != current.WorldEpoch || evidence.RuntimeFingerprint != current.RuntimeFingerprint
            || string.IsNullOrWhiteSpace(evidence.RuntimeFingerprint))
            return new(false, "session-world-or-runtime-mismatch");
        if (!evidence.CauseExclusive || !evidence.SimpleTileOnly || !evidence.NoItemOrEntitySideEffects)
            return new(false, "causality-or-item-side-effects-unproven");
        if (evidence.X < 0 || evidence.Y < 0 || evidence.X != current.X || evidence.Y != current.Y
            || evidence.BeforeRevision < 0 || evidence.BeforeRevision == long.MaxValue
            || evidence.AfterRevision != evidence.BeforeRevision + 1 || current.Revision != evidence.AfterRevision
            || current.StateHash != evidence.AfterStateHash)
            return new(false, "tile-was-changed-or-target-mismatch");
        if (evidence.ChangedUtc > nowUtc || nowUtc - evidence.ChangedUtc > MaximumAge
            || evidence.BeforeState.IsDefaultOrEmpty || evidence.BeforeState.Length > MaximumSnapshotBytes
            || !IsSha256(evidence.BeforeStateHash) || !IsSha256(evidence.AfterStateHash)
            || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(evidence.BeforeState.AsSpan()))
                .Equals(evidence.BeforeStateHash, StringComparison.OrdinalIgnoreCase))
            return new(false, "snapshot-integrity-or-time-invalid");
        return new(true, "causal-tile-restore-planned", new(evidence.IncidentId, evidence.CauseSession, current,
            evidence.BeforeStateHash, evidence.BeforeState));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
