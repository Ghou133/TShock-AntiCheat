using System.Text.Json;

namespace AntiCheat.Core;

/// <summary>
/// Pure server-side coordinator. Observe is synchronous and performs no I/O: revoke precedes publication
/// of an intent. A host-owned, bounded pump performs persistence on its audited execution context.
/// </summary>
public sealed class AntiCheatEngine
{
    private sealed class Session(SessionKey key, DateTimeOffset connectedUtc, long timestamp)
    {
        public SessionKey Key { get; } = key;
        public DateTimeOffset ConnectedUtc { get; } = connectedUtc;
        public long LastActivity { get; set; } = timestamp;
        public long? AccountId { get; set; }
        public bool Revoked { get; set; }
        public SessionSnapshot Snapshot() => new(Key, AccountId, Revoked, ConnectedUtc);
    }

    private sealed class Enforcement(BanIntent intent, long timestamp, bool journaled = false)
    {
        public BanIntent Intent { get; } = intent;
        public long EnqueuedAt { get; } = timestamp;
        public bool Journaled { get; set; } = journaled;
        public bool BanApplied { get; set; }
        public bool Applied { get; set; }
    }

    private readonly object gate = new();
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly TimeProvider clock;
    private readonly EngineOptions options;
    private readonly RulePolicy policy;
    private readonly IReadOnlyDictionary<string, BusinessRulePolicy> businessPolicies;
    private readonly IEnforcementJournal journal;
    private readonly IAccountBanStore bans;
    private readonly Session?[] sessions;
    private readonly long[] generations;
    // Permanent account fences are security state, not an expiring suspicion cache. At capacity the
    // engine enters maintenance; it never evicts a pending sanction or forgets a revoked identity.
    private readonly Dictionary<long, Enforcement> sanctions = [];
    private readonly Guid serverRun = Guid.NewGuid();
    private long worldEpoch = 1;
    private bool recovered;
    private bool capacityReached;
    private bool recoveryFault;
    private bool persistenceFault;
    private long? oldestPendingAt;
    private bool stopping;
    private bool runGuardStarted;
    private string? runGuardFault;

    public AntiCheatEngine(TimeProvider clock, EngineOptions options, RulePolicy policy,
        IEnforcementJournal journal, IAccountBanStore bans, IReadOnlyList<BusinessRulePolicy>? businessRules = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(bans);
        if (options.MaxSessions is < 1 or > 256 || options.MaxSanctions is < 1 or > 1_000_000 ||
            options.SessionIdleTtl <= TimeSpan.Zero || options.PersistenceDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.clock = clock;
        this.options = options;
        this.policy = policy;
        if (businessRules is { Count: > 128 } || businessRules?.Any(x => x is null ||
            !ValidText(x.RuleId, 128) || !ValidText(x.Version, 64) || !ValidText(x.ContextVersion, 128) ||
            !ValidText(x.RuntimeFingerprint, 1024) || !ValidText(x.AuditReference, 1024)) == true)
            throw new ArgumentException("Invalid or oversized business rule registry.", nameof(businessRules));
        businessPolicies = (businessRules ?? []).ToDictionary(x => x.RuleId, StringComparer.Ordinal);
        this.journal = journal;
        this.bans = bans;
        sessions = new Session[options.MaxSessions];
        generations = new long[options.MaxSessions];
    }

    public bool IsMaintenanceMode { get { lock (gate) return MaintenanceUnsafe(); } }
    public string? MaintenanceReason
    {
        get
        {
            lock (gate)
            {
                _ = MaintenanceUnsafe();
                if (stopping) return "shutdown-in-progress";
                if (runGuardFault is not null) return runGuardFault;
                if (!recovered || recoveryFault) return "journal-recovery-required";
                if (capacityReached) return "permanent-account-fence-capacity";
                if (persistenceFault) return "sanction-persistence-unavailable";
                return null;
            }
        }
    }
    public int PendingCount { get { lock (gate) return sanctions.Values.Count(x => !x.Applied); } }
    public int SanctionCount { get { lock (gate) return sanctions.Count; } }

    public SessionKey? OpenSession(int slot)
    {
        lock (gate)
        {
            if ((uint)slot >= sessions.Length || MaintenanceUnsafe()) return null;
            var key = new SessionKey(serverRun, worldEpoch, slot, checked(++generations[slot]));
            sessions[slot] = new Session(key, clock.GetUtcNow(), clock.GetTimestamp());
            return key;
        }
    }

    /// <summary>Only after the server authenticates this account AND passes its durable ban checks.</summary>
    public AuthenticationResult Authenticate(SessionKey key, long serverAccountId)
    {
        lock (gate)
        {
            if (MaintenanceUnsafe()) return AuthenticationResult.Maintenance;
            var session = CurrentUnsafe(key);
            if (session is null) return AuthenticationResult.StaleSession;
            if (serverAccountId <= 0) return AuthenticationResult.InvalidAccount;
            if (sanctions.ContainsKey(serverAccountId))
            {
                session.Revoked = true;
                return AuthenticationResult.AccountBlocked;
            }
            if (session.AccountId is long bound && bound != serverAccountId)
            {
                session.Revoked = true;
                return AuthenticationResult.IdentityChangeRejected;
            }
            if (session.Revoked) return AuthenticationResult.AccountBlocked;
            session.AccountId = serverAccountId;
            session.LastActivity = clock.GetTimestamp();
            return AuthenticationResult.Authenticated;
        }
    }

    public void Disconnect(SessionKey key)
    {
        lock (gate)
            if ((uint)key.Slot < sessions.Length && sessions[key.Slot]?.Key == key)
                sessions[key.Slot] = null;
    }

    public void AdvanceWorld()
    {
        lock (gate)
        {
            worldEpoch = checked(worldEpoch + 1);
            Array.Clear(sessions);
        }
    }

    public long CurrentWorldEpoch { get { lock (gate) return worldEpoch; } }

    public SessionSnapshot? GetSession(SessionKey key)
    {
        lock (gate) return CurrentUnsafe(key)?.Snapshot();
    }

    public bool CanWrite(SessionKey key)
    {
        lock (gate)
        {
            var session = CurrentUnsafe(key);
            return !MaintenanceUnsafe() && session is { Revoked: false };
        }
    }

    public bool Touch(SessionKey key)
    {
        lock (gate)
        {
            var session = CurrentUnsafe(key);
            if (MaintenanceUnsafe() || session is not { Revoked: false }) return false;
            session.LastActivity = clock.GetTimestamp();
            return true;
        }
    }

    public ActionDecision Observe(SelfSlotObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Context);
        lock (gate)
        {
            if (MaintenanceUnsafe()) return new(ControlAction.Block, Verdict.Fault, "maintenance");
            var session = CurrentUnsafe(observation.Session);
            if (session is null) return Block("stale-or-expired-session");
            if (session.Revoked)
            {
                var old = session.AccountId is long account && sanctions.TryGetValue(account, out var previous)
                    ? previous.Intent : null;
                return new(ControlAction.Block, Verdict.UnsafeInput, "session-revoked", old);
            }
            session.LastActivity = clock.GetTimestamp();

            var context = observation.Context;
            if (!context.ParseComplete) return Unknown("incomplete-parse");
            if (observation.ClaimedSlot is < 0 or > 255) return Block("invalid-slot-representation");
            if (observation.ClaimedSlot == session.Key.Slot || context.KnownLegalException)
                return new(ControlAction.Pass, Verdict.Pass, "self-slot-or-known-legal-exception");
            var proof = new ProofPreconditions(
                !string.IsNullOrWhiteSpace(policy.RuntimeFingerprint) &&
                    policy.RuntimeFingerprint != "unknown" && policy.RuntimeFingerprint == context.RuntimeFingerprint,
                QualifiedUnsafe(), context.ParseComplete, session.AccountId is > 0,
                context.ClientOrigin, context.AttributionComplete, context.ExceptionsExcluded,
                !policy.SelfOnlyPacketIds.IsDefault && policy.SelfOnlyPacketIds.Contains(observation.PacketId),
                ValidText(context.ContextVersion, 128) && policy.ContextVersion == context.ContextVersion);
            if (!proof.Complete) return Unknown("proof-preconditions-incomplete");
            // This predicate proves only the narrowly admitted self-only protocol rule. No Bouncer
            // decision, delay, NaN, item provenance, rarity or repeated suspicion contributes to it.
            var accountId = session.AccountId!.Value;
            foreach (var active in sessions)
                if (active?.AccountId == accountId) active.Revoked = true;
            if (sanctions.TryGetValue(accountId, out var existing))
                return new(ControlAction.Block, Verdict.ProvenCheat, "account-already-sanctioned", existing.Intent);
            var incidentId = Guid.NewGuid();
            var now = clock.GetUtcNow();
            var evidence = new Evidence(incidentId, "A02.SelfSlot", "1.0.0", session.Key, accountId, now,
                context.RuntimeFingerprint, context.ContextVersion, policy.AuditReference, policy.Qualification,
                proof, observation.PacketId, session.Key.Slot, observation.ClaimedSlot);
            var intent = new BanIntent(incidentId, accountId, evidence, now);
            sanctions.Add(accountId, new Enforcement(intent, clock.GetTimestamp()));
            oldestPendingAt ??= clock.GetTimestamp();
            // Enter maintenance as soon as the bounded security state fills, before a further proven
            // incident could be observed without somewhere to retain its intent.
            capacityReached = sanctions.Count >= options.MaxSanctions;
            return new(ControlAction.Block, Verdict.ProvenCheat, "first-complete-proof", intent);
        }
    }

    /// <summary>Apply a registered business predicate without allowing it to choose identity or qualification.</summary>
    public ActionDecision ObserveBusiness(BusinessObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Result);
        ArgumentNullException.ThrowIfNull(observation.Context);
        lock (gate)
        {
            if (MaintenanceUnsafe()) return new(ControlAction.Block, Verdict.Fault, "maintenance");
            var session = CurrentUnsafe(observation.Session);
            if (session is null) return Block("stale-or-expired-session");
            if (session.Revoked)
            {
                var old = session.AccountId is long id && sanctions.TryGetValue(id, out var prior) ? prior.Intent : null;
                return new(ControlAction.Block, Verdict.UnsafeInput, "session-revoked", old);
            }
            session.LastActivity = clock.GetTimestamp();
            var result = observation.Result;
            var context = observation.Context;
            if (!businessPolicies.TryGetValue(result.RuleId, out var rule) || rule.Version != result.Version)
                return Unknown("business-rule-not-registered");
            var versionMatched = rule.RuntimeFingerprint != "unknown" && rule.RuntimeFingerprint == context.RuntimeFingerprint;
            if (!versionMatched || !context.ParseComplete || rule.ContextVersion != context.ContextVersion)
                return Unknown("business-input-version-or-parser-unverified");
            if (result.Facts is null || result.Facts.Count > 24 || result.Facts.Any(x =>
                !ValidText(x.Key, 64) || x.Value is null || x.Value.Length > 256))
                return Unknown("business-evidence-out-of-bounds");
            if (!ValidText(result.Reason, 256)) return Unknown("invalid-business-reason");
            if (result.Verdict == Verdict.Pass && result.Action == ControlAction.Pass)
                return new(ControlAction.Pass, Verdict.Pass, result.Reason);
            // Independent safety controls need a complete, versioned parse, never a cheating inference.
            if (result.Action == ControlAction.Block && result.Verdict is Verdict.UnsafeInput or Verdict.ResourceAbuse)
                return new(ControlAction.Block, result.Verdict, result.Reason);
            if (result.Verdict != Verdict.ProvenCheat || !result.PredicateSatisfied || context.KnownLegalException)
                return Unknown(result.Reason);
            var proof = new ProofPreconditions(versionMatched, QualificationMatches(rule.Qualification),
                context.ParseComplete, session.AccountId is > 0, context.ClientOrigin,
                context.AttributionComplete, context.ExceptionsExcluded, false, true, result.PrerequisitesComplete);
            if (!proof.Complete) return Unknown("business-proof-preconditions-incomplete");
            // String facts have stable value equality across journal serialization/recovery.
            var facts = JsonSerializer.Serialize(result.Facts.OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Value));
            if (facts.Length > 8192) return Unknown("business-evidence-out-of-bounds");
            var accountId = session.AccountId!.Value;
            foreach (var active in sessions)
                if (active?.AccountId == accountId) active.Revoked = true;
            if (sanctions.TryGetValue(accountId, out var existing))
                return new(ControlAction.Block, Verdict.ProvenCheat, "account-already-sanctioned", existing.Intent);
            var incidentId = Guid.NewGuid();
            var now = clock.GetUtcNow();
            var evidence = new Evidence(incidentId, rule.RuleId, rule.Version, session.Key, accountId, now,
                context.RuntimeFingerprint, context.ContextVersion, rule.AuditReference, rule.Qualification,
                proof, observation.PacketId, session.Key.Slot, session.Key.Slot, facts);
            var intent = new BanIntent(incidentId, accountId, evidence, now);
            sanctions.Add(accountId, new Enforcement(intent, clock.GetTimestamp()));
            oldestPendingAt ??= clock.GetTimestamp();
            capacityReached = sanctions.Count >= options.MaxSanctions;
            return new(ControlAction.Block, Verdict.ProvenCheat, "first-complete-business-proof", intent);
        }
    }

    public async ValueTask<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!await worker.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            IReadOnlyList<BanIntent> pending;
            try { pending = await journal.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                lock (gate) recoveryFault = true;
                return false;
            }
            lock (gate)
            {
                // Validate the complete bounded recovery set before importing anything.
                if (pending.Count > options.MaxSanctions || pending.Any(x => !ValidIntent(x) || !RecoveryScopeMatches(x)) ||
                    pending.GroupBy(x => x.AccountId).Any(x => x.Distinct().Count() > 1) ||
                    pending.GroupBy(x => x.IncidentId).Any(x => x.Select(y => y.AccountId).Distinct().Count() > 1) ||
                    sanctions.Keys.Union(pending.Select(x => x.AccountId)).Count() > options.MaxSanctions)
                {
                    recoveryFault = true;
                    return false;
                }
                foreach (var intent in pending)
                {
                    if (sanctions.TryGetValue(intent.AccountId, out var prior))
                    {
                        if (prior.Intent != intent) { recoveryFault = true; return false; }
                        prior.Journaled = true;
                    }
                    else sanctions.Add(intent.AccountId, new Enforcement(intent, clock.GetTimestamp(), true));
                    foreach (var active in sessions)
                        if (active?.AccountId == intent.AccountId) active.Revoked = true;
                }
                recovered = false; // Admissions stay closed until the durable run marker is armed.
                recoveryFault = false;
                capacityReached = sanctions.Count >= options.MaxSanctions;
                oldestPendingAt = sanctions.Values.FirstOrDefault(x => !x.Applied)?.EnqueuedAt;
            }
            if (journal is IRunSafetyGuard guard)
            {
                RunSafetyStatus status;
                try
                {
                    status = await guard.BeginRunAsync(new(serverRun, options.Scope, AnyQualifiedRuleUnsafe()), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    lock (gate) { runGuardFault = "run-safety-marker-unavailable"; recoveryFault = true; }
                    return false;
                }
                lock (gate)
                {
                    if (status != RunSafetyStatus.Ready)
                    {
                        runGuardFault = status == RunSafetyStatus.PreviousEnforcementRunUnclean
                            ? "previous-enforcement-run-unclean" : "legacy-journal-review-required";
                        recoveryFault = true;
                        return false;
                    }
                    runGuardStarted = true;
                    runGuardFault = null;
                }
            }
            else if (AnyQualifiedRuleUnsafe())
            {
                lock (gate) { runGuardFault = "durable-run-safety-guard-required"; recoveryFault = true; }
                return false;
            }
            lock (gate) recovered = true;
            return true;
        }
        finally { worker.Release(); }
    }

    /// <summary>
    /// Stop admissions/proofs first. Mark clean only when every known intent has durable structured evidence.
    /// A persisted journal may still await the account DB: its pending entries replay on the next run.
    /// A busy pump returns false; the host may retry after that single operation completes.
    /// </summary>
    public async ValueTask<bool> CompleteShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) stopping = true;
        if (!await worker.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            lock (gate)
            {
                if (!recovered || recoveryFault || sanctions.Values.Any(x => !x.Journaled)) return false;
            }
            if (journal is IRunSafetyGuard guard)
            {
                if (!runGuardStarted) return false;
                try { await guard.CompleteRunAsync(serverRun, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { return false; }
            }
            else if (AnyQualifiedRuleUnsafe()) return false;
            return true;
        }
        finally { worker.Release(); }
    }

    public async ValueTask<PumpResult> PumpAsync(int maxItems = 16, CancellationToken cancellationToken = default)
    {
        if (maxItems is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxItems));
        if (!await worker.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(0, 0, 0, IsMaintenanceMode);
        try
        {
            Enforcement[] batch;
            lock (gate)
            {
                if (!recovered || recoveryFault) return new(0, 0, 0, true);
                batch = sanctions.Values.Where(x => !x.Applied).Take(maxItems).ToArray();
            }
            var applied = 0;
            var failed = 0;
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var journalOk = item.Journaled;
                var banOk = item.BanApplied;
                if (!journalOk)
                {
                    try
                    {
                        await journal.AppendAsync(item.Intent, cancellationToken).ConfigureAwait(false);
                        lock (gate) item.Journaled = true;
                        journalOk = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { /* Primary durable store is an independent fallback. */ }
                }
                try
                {
                    if (!banOk)
                    {
                        await bans.EnsurePermanentlyBannedAsync(item.Intent, cancellationToken).ConfigureAwait(false);
                        lock (gate) item.BanApplied = true;
                        banOk = true;
                    }
                    if (journalOk)
                    {
                        await journal.MarkAppliedAsync(item.Intent.IncidentId, cancellationToken).ConfigureAwait(false);
                        lock (gate) item.Applied = true;
                        applied++;
                    }
                    else failed++; // Preserve the evidence intent and retry the journal even if the DB succeeded.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { failed++; }
                lock (gate)
                {
                    if (!journalOk && !banOk) persistenceFault = true;
                    // A durable journal alone preserves recovery. A slow/unavailable account DB never
                    // re-enables this account; retries contain exactly the same immutable intent.
                    if (!item.Applied && Expired(item.EnqueuedAt, options.PersistenceDeadline))
                        persistenceFault = true;
                }
            }
            lock (gate)
            {
                oldestPendingAt = sanctions.Values.FirstOrDefault(x => !x.Applied)?.EnqueuedAt;
                if (sanctions.Values.All(x => x.Applied ||
                    ((x.Journaled || x.BanApplied) && !Expired(x.EnqueuedAt, options.PersistenceDeadline)))) persistenceFault = false;
                return new(batch.Length, applied, failed, MaintenanceUnsafe());
            }
        }
        finally { worker.Release(); }
    }

    private bool QualifiedUnsafe() => ValidText(policy.AuditReference, 1024) && ValidText(policy.RuntimeFingerprint, 1024) &&
        QualificationMatches(policy.Qualification);

    private bool QualificationMatches(RuleQualification qualification) =>
        options.Scope == ExecutionScope.TestLab && qualification == RuleQualification.TestLab ||
        options.Scope == ExecutionScope.Production && qualification == RuleQualification.ProductionQualified;

    private bool AnyQualifiedRuleUnsafe() => QualifiedUnsafe() || businessPolicies.Values.Any(x => QualificationMatches(x.Qualification));

    private bool MaintenanceUnsafe()
    {
        // O(1) deadline check also works while an I/O provider is stalled inside the single pump.
        if (oldestPendingAt is long since && Expired(since, options.PersistenceDeadline)) persistenceFault = true;
        return stopping || !recovered || recoveryFault || runGuardFault is not null || capacityReached || persistenceFault;
    }
    private bool Expired(long since, TimeSpan ttl)
    {
        var elapsed = clock.GetElapsedTime(since, clock.GetTimestamp());
        return elapsed < TimeSpan.Zero || elapsed >= ttl;
    }
    private Session? CurrentUnsafe(SessionKey key)
    {
        if ((uint)key.Slot >= sessions.Length) return null;
        var session = sessions[key.Slot];
        if (session?.Key != key) return null;
        if (!Expired(session.LastActivity, options.SessionIdleTtl)) return session;
        sessions[key.Slot] = null;
        return null;
    }
    private static bool ValidIntent(BanIntent x) => x is not null && x.IncidentId != Guid.Empty && x.AccountId > 0 &&
        x.Evidence is { Preconditions.Complete: true } && x.Evidence.AccountId == x.AccountId &&
        x.Evidence.EventId == x.IncidentId && ValidRecordedPredicate(x.Evidence) &&
        x.Evidence.Session.ServerRunId != Guid.Empty && x.Evidence.Session.WorldEpoch > 0 &&
        x.Evidence.Session.Generation > 0 && x.Evidence.Session.Slot == x.Evidence.ServerSlot &&
        x.Evidence.ServerSlot is >= 0 and < 256 && x.Evidence.ClientClaimedSlot is >= 0 and < 256 &&
        ValidText(x.Evidence.RuntimeFingerprint, 1024) && ValidText(x.Evidence.ContextVersion, 128) &&
        ValidText(x.Evidence.AuditReference, 1024);
    private static bool ValidRecordedPredicate(Evidence evidence) => evidence.PredicateFactsJson is null
        ? evidence.RuleId == "A02.SelfSlot" && evidence.RuleVersion == "1.0.0" &&
            evidence.ClientClaimedSlot != evidence.ServerSlot && evidence.Preconditions.RuleContextComplete is null
        : ValidText(evidence.RuleId, 128) && ValidText(evidence.RuleVersion, 64) &&
            evidence.Preconditions.RuleContextComplete == true && ValidFacts(evidence.PredicateFactsJson);

    private static bool ValidFacts(string json)
    {
        if (json.Length > 8192) return false;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is { Count: <= 24 } && values.All(x => ValidText(x.Key, 64) && x.Value is { Length: <= 256 });
        }
        catch (JsonException) { return false; }
    }
    private bool RecoveryScopeMatches(BanIntent intent) => options.Scope == ExecutionScope.TestLab
        ? intent.Evidence.Qualification == RuleQualification.TestLab
        : intent.Evidence.Qualification == RuleQualification.ProductionQualified;
    private static bool ValidText(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit;
    private static ActionDecision Unknown(string reason) => new(ControlAction.Unknown, Verdict.Unknown, reason);
    private static ActionDecision Block(string reason) => new(ControlAction.Block, Verdict.UnsafeInput, reason);
}
