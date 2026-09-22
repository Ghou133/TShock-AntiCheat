using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum ConnectionPhase { Handshake, Authenticating, WorldSync, Playing }
public enum NetworkRequestKind { Ordinary, EntitySync, WorldMutation, BroadcastAmplification }
public enum NetworkDisposition { Allow, Block, Disconnect, Ignore }

public sealed record NetworkControlDecision(SessionKey Session, NetworkDisposition Disposition, Verdict Verdict,
    string Reason, string? NormalizedPeer = null, double ChargedCost = 0, FirewallDryRunEvent? SourceEvent = null);

public enum ConnectionPhaseReadStatus { Accepted, Unavailable, Faulted, Stale }

public sealed record ConnectionPhaseSnapshot(SessionKey Session, ConnectionPhase Phase, long PhaseEnteredAt,
    long LastPacketAt, long LastPlayerUpdateAt);

/// <summary>A bounded observation, with no exception text or mutable adapter/connection objects.</summary>
public sealed record ConnectionPhaseScanObservation(SessionKey Session, ConnectionPhase? AcceptedPhase,
    ConnectionPhaseReadStatus Status, ConnectionPhaseSnapshot Before, ConnectionPhaseSnapshot? After);

public sealed record NetworkTimeoutScanResult(ImmutableArray<NetworkControlDecision> Decisions,
    ImmutableArray<ConnectionPhaseScanObservation> Observations, int InspectedSlots);

public sealed record NetworkControlOptions
{
    public int MaxSessions { get; init; } = 256;
    public RateLimitOptions ConnectionBudget { get; init; } = new(256, 8192, 2048, TimeSpan.FromMinutes(15));
    public RateLimitOptions SourceBudget { get; init; } = new(2048, 262144, 65536, TimeSpan.FromMinutes(15));
    public RateLimitOptions GlobalBudget { get; init; } = new(1, 2097152, 524288, TimeSpan.FromMinutes(15));
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan WorldSyncTimeout { get; init; } = TimeSpan.FromSeconds(600);
    public TimeSpan PlayerUpdateTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan ConnectionIdleTtl { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan SourceEventCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public double ConnectionOpeningCost { get; init; } = 16;
    /// <summary>Admission weight per server-validated world cell/request unit; a cost estimate, not elapsed CPU time.</summary>
    public double WorldWorkUnitCost { get; init; } = 1d / 16;
    public bool DisconnectOnBudgetExhaustion { get; init; } = true;
}

/// <summary>
/// Bounded connection lifecycle and three independent cost budgets, reusing Core's limiter and dry-run source model.
/// SocketPeer and phase transitions must come from the server adapter, never packet names/text or client player IDs.
/// All decisions are availability controls; this class never emits ProvenCheat or chooses an account sanction.
/// </summary>
public sealed class NetworkControls
{
    public const int MaximumWorkUnitsPerPacket = 131072;
    private sealed class Connection(SessionKey session, IPAddress peer, IPAddress? verifiedClient, bool proxy,
        bool exclusive, DateTimeOffset openedUtc, long now)
    {
        public SessionKey Session { get; } = session;
        public string BudgetKey { get; } = SessionBudgetKey(session);
        public IPAddress Peer { get; } = peer;
        public IPAddress? VerifiedClient { get; } = verifiedClient;
        public bool Proxy { get; } = proxy;
        public bool Exclusive { get; } = exclusive;
        public DateTimeOffset OpenedUtc { get; } = openedUtc;
        public ConnectionPhase Phase { get; set; } = ConnectionPhase.Handshake;
        public long PhaseEnteredAt { get; set; } = now;
        public long LastPlayerUpdateAt { get; set; } = now;
        public long LastPacketAt { get; set; } = now;
        public long Bytes { get; set; }
        public long Cost { get; set; }
    }

    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly NetworkControlOptions options;
    private readonly BoundedRateLimiter connectionBudget;
    private readonly BoundedRateLimiter sourceBudget;
    private readonly BoundedRateLimiter globalBudget;
    private readonly BoundedRateLimiter sourceEventBudget;
    private readonly FirewallDryRun firewall;
    private readonly Connection?[] sessions;
    private int scanCursor;
    private Guid? serverRunId;

    public NetworkControls(TimeProvider clock, NetworkControlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.options = options ?? new();
        if (this.options.MaxSessions is < 1 or > 256 || this.options.ConnectionBudget.MaxKeys < this.options.MaxSessions
            || this.options.GlobalBudget.MaxKeys != 1 || this.options.HandshakeTimeout <= TimeSpan.Zero
            || this.options.AuthenticationTimeout <= TimeSpan.Zero || this.options.WorldSyncTimeout <= TimeSpan.Zero
            || this.options.PlayerUpdateTimeout <= TimeSpan.Zero || this.options.ConnectionIdleTtl <= TimeSpan.Zero
            || this.options.SourceEventCooldown <= TimeSpan.Zero || !double.IsFinite(this.options.ConnectionOpeningCost)
            || this.options.ConnectionOpeningCost <= 0 || !double.IsFinite(this.options.WorldWorkUnitCost)
            || this.options.WorldWorkUnitCost <= 0 || this.options.WorldWorkUnitCost > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.clock = clock;
        connectionBudget = new(clock, this.options.ConnectionBudget);
        sourceBudget = new(clock, this.options.SourceBudget);
        globalBudget = new(clock, this.options.GlobalBudget);
        sourceEventBudget = new(clock, new(this.options.SourceBudget.MaxKeys, 1,
            1 / this.options.SourceEventCooldown.TotalSeconds,
            this.options.SourceBudget.IdleTtl >= this.options.SourceEventCooldown ? this.options.SourceBudget.IdleTtl : this.options.SourceEventCooldown));
        firewall = new(clock);
        sessions = new Connection[this.options.MaxSessions];
    }

    public int Count { get { lock (gate) return sessions.Count(x => x is not null); } }
    public int SourceBucketCount => sourceBudget.Count;

    public NetworkControlDecision Open(SessionKey session, string socketPeer, bool trustedProxyPeer = false,
        string? verifiedClientAddress = null, bool exclusiveSourceVerified = false)
    {
        if (!ValidSession(session) || !TryNormalizeAddress(socketPeer, out var peer))
            return new(session, NetworkDisposition.Disconnect, Verdict.UnsafeInput, "invalid-session-or-server-socket-peer");
        IPAddress? verified = null;
        if (verifiedClientAddress is not null && (!trustedProxyPeer || !TryNormalizeAddress(verifiedClientAddress, out verified)))
            return new(session, NetworkDisposition.Disconnect, Verdict.UnsafeInput, "unverified-proxy-address-claim", peer!.ToString());
        lock (gate)
        {
            if (serverRunId.HasValue && serverRunId.Value != session.ServerRunId)
                return new(session, NetworkDisposition.Disconnect, Verdict.UnsafeInput, "network-server-run-mismatch", peer!.ToString());
            if (Current(session) is { } duplicate)
                return new(session, NetworkDisposition.Allow, Verdict.Pass, "connection-already-registered", duplicate.Peer.ToString());
            if (sessions[session.Slot] is { } previous)
            {
                if (session.WorldEpoch < previous.Session.WorldEpoch
                    || (session.WorldEpoch == previous.Session.WorldEpoch && session.Generation <= previous.Session.Generation))
                    return new(session, NetworkDisposition.Disconnect, Verdict.UnsafeInput, "stale-network-registration", peer!.ToString());
                connectionBudget.Forget(previous.BudgetKey);
            }
            serverRunId ??= session.ServerRunId;
            sessions[session.Slot] = null;
            var now = clock.GetTimestamp();
            var connection = new Connection(session, peer!, verified, trustedProxyPeer, exclusiveSourceVerified, clock.GetUtcNow(), now);
            var result = Charge(connection, this.options.ConnectionOpeningCost, 0, NetworkAbuseReason.ConnectionBudget);
            if (result.Disposition == NetworkDisposition.Allow) sessions[session.Slot] = connection;
            else connectionBudget.Forget(connection.BudgetKey);
            return result.Disposition == NetworkDisposition.Block ? result with { Disposition = NetworkDisposition.Disconnect } : result;
        }
    }

    /// <summary>Repeated or regressing phases do not reset timeout clocks.</summary>
    public bool EnterPhase(SessionKey session, ConnectionPhase phase)
    {
        lock (gate)
        {
            var connection = Current(session);
            if (connection is null || !Enum.IsDefined(phase) || phase <= connection.Phase) return false;
            AdvancePhase(connection, phase, clock.GetTimestamp());
            // Preserve the legacy explicit-transition API. Sampled transitions never manufacture a heartbeat.
            if (phase == ConnectionPhase.Playing) connection.LastPlayerUpdateAt = connection.PhaseEnteredAt;
            return true;
        }
    }

    public ConnectionPhaseSnapshot? CapturePhase(SessionKey session)
    {
        lock (gate) return Current(session) is { } connection ? CapturePhase(connection) : null;
    }

    /// <summary>Call only after parsing a real PlayerUpdate from this connection; arbitrary traffic is not a heartbeat.</summary>
    public bool RecordPlayerUpdate(SessionKey session)
    {
        lock (gate)
        {
            var connection = Current(session);
            if (connection is null) return false;
            connection.LastPlayerUpdateAt = clock.GetTimestamp();
            return true;
        }
    }

    public NetworkControlDecision Consume(SessionKey session, int packetBytes, NetworkRequestKind kind = NetworkRequestKind.Ordinary,
        int workUnits = 0)
    {
        if (packetBytes is < 1 or > 65535 || !Enum.IsDefined(kind) || workUnits is < 0 or > MaximumWorkUnitsPerPacket)
            return new(session, NetworkDisposition.Block, Verdict.UnsafeInput, "invalid-packet-cost-input");
        lock (gate)
        {
            var connection = Current(session);
            if (connection is null) return new(session, NetworkDisposition.Disconnect, Verdict.UnsafeInput, "network-session-stale-or-unregistered");
            connection.LastPacketAt = clock.GetTimestamp();
            double baseCost = kind switch
            {
                NetworkRequestKind.EntitySync => 2,
                NetworkRequestKind.WorldMutation => 4,
                NetworkRequestKind.BroadcastAmplification => 8,
                _ => 1
            };
            var cost = baseCost + packetBytes / 1024d + workUnits * options.WorldWorkUnitCost;
            return Charge(connection, cost, packetBytes, kind == NetworkRequestKind.BroadcastAmplification
                ? NetworkAbuseReason.BroadcastAmplification : NetworkAbuseReason.WeightedPacketCost);
        }
    }

    /// <summary>At most 64 slots inspected per call. Expired entries release their exact session budget immediately.</summary>
    public ImmutableArray<NetworkControlDecision> InspectTimeouts(int maximumToInspect = 16)
    {
        if (maximumToInspect is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumToInspect));
        lock (gate)
        {
            var decisions = ImmutableArray.CreateBuilder<NetworkControlDecision>();
            var now = clock.GetTimestamp();
            for (int i = 0; i < Math.Min(maximumToInspect, sessions.Length); i++)
            {
                var slot = scanCursor;
                scanCursor = (scanCursor + 1) % sessions.Length;
                var connection = sessions[slot];
                if (connection is null) continue;
                if (InspectTimeout(connection, now, phaseAvailable: true) is { } decision)
                    decisions.Add(decision);
            }
            return decisions.ToImmutable();
        }
    }

    /// <summary>
    /// Reads each selected connection once outside the network lock, then rechecks its exact object and key.
    /// The trusted adapter callback must be a bounded, synchronous pure read; it must not do I/O or game actions.
    /// Unavailable/failed reads suppress only this round's phase-dependent deadline, retaining independent idle expiry.
    /// </summary>
    public NetworkTimeoutScanResult InspectTimeouts(int maximumToInspect, Func<SessionKey, ConnectionPhase?> readAcceptedPhase)
    {
        if (maximumToInspect is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumToInspect));
        ArgumentNullException.ThrowIfNull(readAcceptedPhase);
        int inspectedSlots = Math.Min(maximumToInspect, sessions.Length), count = 0;
        var batch = new (Connection Connection, ConnectionPhaseSnapshot Before)[inspectedSlots];
        lock (gate)
        {
            for (int i = 0; i < inspectedSlots; i++)
            {
                var connection = sessions[scanCursor];
                scanCursor = (scanCursor + 1) % sessions.Length;
                if (connection is not null) batch[count++] = (connection, CapturePhase(connection));
            }
        }

        var decisions = ImmutableArray.CreateBuilder<NetworkControlDecision>(count);
        var observations = ImmutableArray.CreateBuilder<ConnectionPhaseScanObservation>(count);
        for (int i = 0; i < count; i++)
        {
            var (connection, before) = batch[i];
            ConnectionPhase? phase = null;
            var status = ConnectionPhaseReadStatus.Unavailable;
            try
            {
                phase = readAcceptedPhase(before.Session);
                if (phase.HasValue)
                {
                    status = Enum.IsDefined(phase.Value) ? ConnectionPhaseReadStatus.Accepted : ConnectionPhaseReadStatus.Faulted;
                    if (status == ConnectionPhaseReadStatus.Faulted) phase = null;
                }
            }
            catch (Exception)
            {
                // Fixed-size health facts only: no exception text, per-source accumulation, or global circuit breaker.
                status = ConnectionPhaseReadStatus.Faulted;
            }

            lock (gate)
            {
                if (!ReferenceEquals(Current(before.Session), connection))
                {
                    observations.Add(new(before.Session, phase, ConnectionPhaseReadStatus.Stale, before, null));
                    continue;
                }
                var now = clock.GetTimestamp();
                if (phase.HasValue) AdvancePhase(connection, phase.Value, now);
                var after = CapturePhase(connection);
                if (InspectTimeout(connection, now, status == ConnectionPhaseReadStatus.Accepted) is { } decision)
                    decisions.Add(decision);
                observations.Add(new(before.Session, phase, status, before, after));
            }
        }
        return new(decisions.ToImmutable(), observations.ToImmutable(), inspectedSlots);
    }

    public NetworkTimeoutScanResult InspectTimeouts(Func<SessionKey, ConnectionPhase?> readAcceptedPhase, int maximumToInspect = 16)
        => InspectTimeouts(maximumToInspect, readAcceptedPhase);

    private static ConnectionPhaseSnapshot CapturePhase(Connection connection) => new(connection.Session, connection.Phase,
        connection.PhaseEnteredAt, connection.LastPacketAt, connection.LastPlayerUpdateAt);

    private static void AdvancePhase(Connection connection, ConnectionPhase phase, long now)
    {
        if (phase <= connection.Phase) return;
        connection.Phase = phase;
        connection.PhaseEnteredAt = now;
    }

    // Caller holds gate and has verified the selected object is still current.
    private NetworkControlDecision? InspectTimeout(Connection connection, long now, bool phaseAvailable)
    {
        var phaseElapsed = clock.GetElapsedTime(connection.PhaseEnteredAt, now);
        var packetElapsed = clock.GetElapsedTime(connection.LastPacketAt, now);
        var updateElapsed = clock.GetElapsedTime(connection.LastPlayerUpdateAt, now);
        // Entering Playing starts its initial allowance without claiming a PlayerUpdate was received.
        var playingElapsed = phaseElapsed < updateElapsed ? phaseElapsed : updateElapsed;
        string? reason = phaseElapsed < TimeSpan.Zero || packetElapsed < TimeSpan.Zero || updateElapsed < TimeSpan.Zero
            ? "network-monotonic-clock-invalid"
            : packetElapsed >= options.ConnectionIdleTtl ? "connection-idle-ttl"
            : !phaseAvailable ? null
            : connection.Phase switch
            {
                ConnectionPhase.Handshake when phaseElapsed >= options.HandshakeTimeout => "handshake-phase-timeout",
                ConnectionPhase.Authenticating when phaseElapsed >= options.AuthenticationTimeout => "authentication-phase-timeout",
                ConnectionPhase.WorldSync when phaseElapsed >= options.WorldSyncTimeout => "world-sync-phase-timeout",
                ConnectionPhase.Playing when playingElapsed >= options.PlayerUpdateTimeout => "player-update-timeout",
                _ => null
            };
        if (reason is null) return null;
        // Timing is availability evidence, not cheating or an IP-block recommendation.
        var decision = new NetworkControlDecision(connection.Session, NetworkDisposition.Disconnect, Verdict.ResourceAbuse,
            reason, connection.Peer.ToString());
        connectionBudget.Forget(connection.BudgetKey);
        sessions[connection.Session.Slot] = null;
        return decision;
    }

    public bool Close(SessionKey session)
    {
        lock (gate)
        {
            var connection = Current(session);
            if (connection is null) return false;
            sessions[session.Slot] = null;
            connectionBudget.Forget(connection.BudgetKey);
            return true;
        }
    }

    public static bool TryNormalizeAddress(string? value, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IPAddress.TryParse(value, out var parsed)) return false;
        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    private NetworkControlDecision Charge(Connection connection, double cost, int bytes, NetworkAbuseReason reason)
    {
        // Charge all layers even when another one rejects; a depleted connection cannot hide source/global load.
        var own = connectionBudget.TryConsume(connection.BudgetKey, cost);
        var source = sourceBudget.TryConsume(connection.Peer.ToString(), cost);
        var global = globalBudget.TryConsume("global", cost);
        connection.Bytes = SaturatingAdd(connection.Bytes, bytes);
        connection.Cost = SaturatingAdd(connection.Cost, (long)Math.Ceiling(cost));
        var exceeded = own.Behavior == ControlAction.Block ? "connection" : source.Behavior == ControlAction.Block ? "source"
            : global.Behavior == ControlAction.Block ? "global" : null;
        if (exceeded is null) return new(connection.Session, NetworkDisposition.Allow, Verdict.Pass, "within-three-layer-budget", connection.Peer.ToString(), cost);
        var sourceEvent = CreateSourceEvent(connection, reason, "network-controls/m2.1:" + exceeded);
        return new(connection.Session, options.DisconnectOnBudgetExhaustion ? NetworkDisposition.Disconnect : NetworkDisposition.Block,
            Verdict.ResourceAbuse, exceeded + "-cost-budget-exhausted", connection.Peer.ToString(), cost, sourceEvent);
    }

    /// <summary>Uses the existing actual-socket provenance and shared log cooldown for application admission rejects.</summary>
    public NetworkControlDecision ReportApplicationRejection(SessionKey session)
    {
        lock (gate)
        {
            var connection = Current(session);
            if (connection is null) return new(session, NetworkDisposition.Block, Verdict.ResourceAbuse, "application-session-unregistered");
            return new(session, NetworkDisposition.Block, Verdict.ResourceAbuse, "application-request-budget-exhausted",
                connection.Peer.ToString(), 0, CreateSourceEvent(connection, NetworkAbuseReason.WeightedPacketCost,
                    "application-chat/admission-1.0.0"));
        }
    }

    private FirewallDryRunEvent? CreateSourceEvent(Connection connection, NetworkAbuseReason reason, string evidenceReference)
    {
        if (sourceEventBudget.TryConsume(connection.Peer.ToString()).Behavior == ControlAction.Pass)
        {
            var end = clock.GetUtcNow();
            return firewall.Create(connection.Peer,
                new(connection.OpenedUtc <= end ? connection.OpenedUtc : end, end, 1, connection.Bytes, connection.Cost),
                reason, TimeSpan.FromMinutes(1), connection.Session, proxyPeer: connection.Proxy,
                exclusiveSourceVerified: connection.Exclusive, verifiedClientAddress: connection.VerifiedClient,
                evidenceReference: evidenceReference);
        }
        return null;
    }

    private Connection? Current(SessionKey session) => ValidSession(session) && sessions[session.Slot]?.Session == session ? sessions[session.Slot] : null;
    private bool ValidSession(SessionKey session) => session.ServerRunId != Guid.Empty && session.WorldEpoch > 0
        && session.Generation > 0 && session.Slot >= 0 && session.Slot < sessions.Length;
    private static string SessionBudgetKey(SessionKey session) => session.ServerRunId.ToString("N") + ":"
        + session.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ":" + session.Slot.ToString(CultureInfo.InvariantCulture)
        + ":" + session.Generation.ToString(CultureInfo.InvariantCulture);
    private static long SaturatingAdd(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;
}
