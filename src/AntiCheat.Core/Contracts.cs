using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AntiCheat.Core;

public enum ExecutionScope { ObserveOnly, TestLab, Production }
public enum RuleQualification { Unqualified, TestLab, ProductionQualified }
public enum ControlAction { Pass, Unknown, Block }
public enum Verdict { Pass, Unknown, UnsafeInput, ResourceAbuse, ProvenCheat, Fault }
public enum AuthenticationResult { Authenticated, StaleSession, AccountBlocked, Maintenance, InvalidAccount, IdentityChangeRejected }

public readonly record struct SessionKey(Guid ServerRunId, long WorldEpoch, int Slot, long Generation);
public sealed record SessionSnapshot(SessionKey Key, long? AccountId, bool Revoked, DateTimeOffset ConnectedUtc);

public sealed record EngineOptions
{
    public int MaxSessions { get; init; } = 256;
    public int MaxSanctions { get; init; } = 4096;
    public TimeSpan SessionIdleTtl { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan PersistenceDeadline { get; init; } = TimeSpan.FromMinutes(2);
    public ExecutionScope Scope { get; init; } = ExecutionScope.ObserveOnly;
}

/// <summary>Server-owned rule admission; never populated from packet fields. No live rule is qualified by this library.</summary>
public sealed record RulePolicy(string RuntimeFingerprint, RuleQualification Qualification,
    string AuditReference, ImmutableArray<byte> SelfOnlyPacketIds, string ContextVersion = "1")
{
    public static RulePolicy Disabled { get; } = new("unknown", RuleQualification.Unqualified, "", []);
}

/// <summary>Immutable server/adapter attestations. ClaimedSlot remains separately identified as untrusted input.</summary>
public sealed record ProofContext(string RuntimeFingerprint, string ContextVersion,
    bool ParseComplete, bool ClientOrigin, bool AttributionComplete, bool ExceptionsExcluded,
    bool KnownLegalException = false);

public sealed record SelfSlotObservation(SessionKey Session, byte PacketId, int ClaimedSlot, ProofContext Context);
public sealed record ProofPreconditions(bool VersionMatched, bool RuleQualified, bool ParserComplete,
    bool Authenticated, bool ClientOrigin, bool AttributionComplete, bool ExceptionsExcluded,
    bool PacketIsSelfOnly, bool ContextVersionKnown,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RuleContextComplete = null)
{
    public bool Complete => VersionMatched && RuleQualified && ParserComplete && Authenticated &&
        ClientOrigin && AttributionComplete && ExceptionsExcluded && (RuleContextComplete ?? PacketIsSelfOnly) && ContextVersionKnown;
}

public sealed record Evidence(Guid EventId, string RuleId, string RuleVersion, SessionKey Session,
    long AccountId, DateTimeOffset ObservedUtc, string RuntimeFingerprint, string ContextVersion,
    string AuditReference, RuleQualification Qualification, ProofPreconditions Preconditions,
    byte PacketId, int ServerSlot, int ClientClaimedSlot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PredicateFactsJson = null);

public sealed record BanIntent(Guid IncidentId, long AccountId, Evidence Evidence, DateTimeOffset CreatedUtc);
public sealed record ActionDecision(ControlAction Behavior, Verdict Verdict, string Reason, BanIntent? Incident = null);
public sealed record PumpResult(int Attempted, int Applied, int Failed, bool Maintenance);

/// <summary>
/// Local bounded durable journal. ReadAsync returns pending records only. Append and MarkApplied must be
/// idempotent. Pending records may never be expired/evicted. Corruption/capacity failures must throw.
/// Once applied, the account store is authoritative for permanent bans, including after process restart.
/// </summary>
public interface IEnforcementJournal
{
    ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default);
    ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Idempotent permanent server-authenticated account ban. The adapter must perform the actual durable ban,
/// throw on failure and run on the audited execution context. Success must mean durable, not merely queued.
/// Normal login must consult this durable store before Authenticate, including after a restart.
/// </summary>
public interface IAccountBanStore
{
    ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default);
}

public enum RunSafetyStatus { Ready, PreviousEnforcementRunUnclean, LegacyStateNeedsReview }
public sealed record RunSafetyRequest(Guid ServerRunId, ExecutionScope Scope, bool CanProduceProofs);

/// <summary>
/// Arm a durable marker before enabling proof production. A previous armed run without a confirmed
/// clean shutdown may have lost a memory-only intent; an empty journal cannot establish safety.
/// Test doubles can model this interface, but only an actual durable implementation proves restart safety.
/// Required for any host enabling qualified TestLab or Production rules; omission puts Core in maintenance.
/// </summary>
public interface IRunSafetyGuard
{
    ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default);
    ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default);
}
