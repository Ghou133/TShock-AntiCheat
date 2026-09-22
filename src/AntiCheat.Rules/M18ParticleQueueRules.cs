using System.Collections.Immutable;
using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// Candidate-only admission for the client NetParticles StormLightning route.
/// It is a resource stop-loss for the isolated lab, not proof that a client is
/// cheating and not a production rule.
/// </summary>
public sealed record M18ParticleQueueOptions
{
    public bool Enabled { get; init; }
    public int WindowTicks { get; init; } = 180;
    public int PerSessionEvents { get; init; } = 128;
    public int EventCapacity { get; init; } = 256;
    public bool EnablePreForwardBlocks { get; init; } = true;

    public static M18ParticleQueueOptions Disabled => new();

    public static M18ParticleQueueOptions TestLabCandidate => new()
    {
        Enabled = true,
    };

    public static M18ParticleQueueOptions ProductionCandidate => TestLabCandidate;

    public static M18ParticleQueueOptions ForExecutionScope(
        ExecutionScope scope,
        M18CandidateMode mode = M18CandidateMode.Auto,
        bool recordObservations = true,
        bool enableBlocks = true)
    {
        if (!recordObservations || !M18ExecutionModePolicy.RecordEnabled(scope, mode))
            return Disabled;

        bool controls = M18ExecutionModePolicy.CandidateControlsEnabled(scope, mode);
        return (mode == M18CandidateMode.ProductionCandidate ? ProductionCandidate : TestLabCandidate) with
        {
            EnablePreForwardBlocks = enableBlocks && controls,
        };
    }

    public void Validate()
    {
        if (WindowTicks is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(WindowTicks));
        if (PerSessionEvents is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(PerSessionEvents));
        if (EventCapacity is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(EventCapacity));
    }
}

public readonly record struct M18ParticleObservation(
    SessionKey Session,
    long AccountId,
    int ParticleType,
    byte InvokingPlayer,
    bool PositionFinite,
    bool MovementFinite,
    bool ParseComplete,
    bool ClientOrigin,
    bool BeforeSideEffects,
    bool AttributionComplete)
{
    public bool PayloadIdentityMatchesSession { get; init; } = true;
}

public sealed record M18ParticleQueueDecision(
    bool Enabled,
    ControlAction Action,
    Verdict Verdict,
    string Reason,
    bool Counted,
    bool CapacityExhausted,
    int SessionEvents,
    int EventsRetained,
    int ParticleType,
    byte InvokingPlayer)
{
    public bool IsResourceBlock => Action == ControlAction.Block && Verdict == Verdict.ResourceAbuse;
    public bool PayloadIdentityMatchesSession { get; init; } = true;

    public static M18ParticleQueueDecision Disabled => new(
        Enabled: false,
        Action: ControlAction.Unknown,
        Verdict: Verdict.Unknown,
        Reason: "particle-queue-disabled",
        Counted: false,
        CapacityExhausted: false,
        SessionEvents: 0,
        EventsRetained: 0,
        ParticleType: 0,
        InvokingPlayer: 0);
}

/// <summary>
/// Fixed-size per-session FIFO. Only the exact StormLightning type is scoped;
/// other registered particle types remain outside this candidate. Mismatched
/// invoking-player bytes are retained as payload evidence; budget ownership is
/// always the authenticated connection/session that sent the frame.
/// </summary>
public sealed class M18ParticleQueue
{
    public const int StormLightningType = 61;
    private const int SessionSlotCapacity = 256;
    private readonly M18ParticleQueueOptions _options;
    private readonly SessionState?[] _sessions = new SessionState?[SessionSlotCapacity];
    private long _worldEpoch = long.MinValue;

    public M18ParticleQueue(M18ParticleQueueOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public bool Enabled => _options.Enabled;

    public void AdvanceWorld(long worldEpoch)
    {
        if (_worldEpoch == worldEpoch)
            return;

        Array.Clear(_sessions, 0, _sessions.Length);
        _worldEpoch = worldEpoch;
    }

    public void Reset()
    {
        Array.Clear(_sessions, 0, _sessions.Length);
        _worldEpoch = long.MinValue;
    }

    public void Forget(SessionKey session)
    {
        if (!TryGetSessionSlot(session, out var slot))
            return;

        var state = _sessions[slot];
        if (state is not null && state.Session == session)
            _sessions[slot] = null;
    }

    public M18ParticleQueueDecision Observe(long tick, M18ParticleObservation observation)
    {
        if (!_options.Enabled)
            return M18ParticleQueueDecision.Disabled;

        if (observation.ParticleType != StormLightningType)
            return Incomplete(observation, "particle-queue-type-outside-candidate");
        if (tick < 0)
            return Incomplete(observation, "particle-queue-clock-unavailable");
        if (!TryGetSessionSlot(observation.Session, out var sessionSlot))
            return Incomplete(observation, "particle-queue-session-unavailable");
        if (observation.AccountId <= 0 ||
            !observation.PositionFinite || !observation.MovementFinite ||
            !observation.ParseComplete || !observation.ClientOrigin ||
            !observation.BeforeSideEffects || !observation.AttributionComplete)
            return Incomplete(observation, "particle-queue-attribution-or-input-incomplete");

        var state = _sessions[sessionSlot];
        if (state is null || state.Session != observation.Session || state.AccountId != observation.AccountId)
        {
            state = new SessionState(observation.Session, observation.AccountId, _options.EventCapacity);
            _sessions[sessionSlot] = state;
        }

        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            _sessions[sessionSlot] = new SessionState(observation.Session, observation.AccountId, _options.EventCapacity);
            return Incomplete(observation, "particle-queue-clock-regressed");
        }

        state.Expire(tick, _options.WindowTicks);
        state.LastTick = tick;
        if (state.EventCount >= _options.EventCapacity || state.EventCount >= _options.PerSessionEvents)
            return state.Decision(observation, counted: false, capacityExhausted: true,
                state.EventCount >= _options.EventCapacity
                    ? "particle-window-event-capacity-exhausted"
                    : "particle-window-session-budget-exhausted", _options.EnablePreForwardBlocks);

        state.Add(tick);
        return state.Decision(observation, counted: true, capacityExhausted: false,
            observation.PayloadIdentityMatchesSession
                ? "particle-window-observed"
                : "particle-window-observed-payload-identity-mismatch", _options.EnablePreForwardBlocks);
    }

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            slot >= 0 && slot < SessionSlotCapacity && session.Generation > 0;
    }

    private static M18ParticleQueueDecision Incomplete(M18ParticleObservation observation, string reason)
        => new(
            Enabled: true,
            Action: ControlAction.Unknown,
            Verdict: Verdict.Unknown,
            Reason: reason,
            Counted: false,
            CapacityExhausted: false,
            SessionEvents: 0,
            EventsRetained: 0,
            ParticleType: observation.ParticleType,
            InvokingPlayer: observation.InvokingPlayer)
        { PayloadIdentityMatchesSession = observation.PayloadIdentityMatchesSession };

    private sealed class SessionState
    {
        private readonly long[] _ticks;
        private int _start;
        private int _count;

        public SessionState(SessionKey session, long accountId, int capacity)
        {
            Session = session;
            AccountId = accountId;
            _ticks = new long[capacity];
            LastTick = long.MinValue;
        }

        public SessionKey Session { get; }
        public long AccountId { get; }
        public long LastTick { get; set; }
        public int EventCount => _count;

        public void Expire(long tick, int windowTicks)
        {
            while (_count > 0 && tick - _ticks[_start] >= windowTicks)
            {
                _start = (_start + 1) % _ticks.Length;
                _count--;
            }
        }

        public void Add(long tick)
        {
            int index = (_start + _count) % _ticks.Length;
            _ticks[index] = tick;
            _count++;
        }

        public M18ParticleQueueDecision Decision(M18ParticleObservation observation,
            bool counted, bool capacityExhausted, string reason, bool enablePreForwardBlocks)
        {
            bool block = capacityExhausted && enablePreForwardBlocks;
            return new(
                Enabled: true,
                Action: block ? ControlAction.Block : ControlAction.Unknown,
                Verdict: block ? Verdict.ResourceAbuse : Verdict.Unknown,
                Reason: capacityExhausted && !enablePreForwardBlocks ? reason + "-block-disabled" : reason,
                Counted: counted,
                CapacityExhausted: capacityExhausted,
                SessionEvents: _count,
                EventsRetained: _count,
                ParticleType: observation.ParticleType,
                InvokingPlayer: observation.InvokingPlayer)
            { PayloadIdentityMatchesSession = observation.PayloadIdentityMatchesSession };
        }
    }
}

public static class M18ParticleQueueRules
{
    public const string RuleId = "F08.LightningParticleDensityBudget";
    public const string Version = "1.1.0";
    public const string ContractVersion = "terraria1.4.5.8-326-netparticles-stormlightning-density-candidate-v2-connection-owned";

    public static BusinessRuleResult ResourceBlock(
        RuleInputContext input,
        ImmutableDictionary<string, string> facts,
        M18ParticleQueueDecision decision)
    {
        if (!decision.IsResourceBlock)
            throw new ArgumentException("The decision is not a resource block.", nameof(decision));

        var boundedFacts = facts
            .SetItem("particleQueue", FormatSummary(decision))
            .SetItem("particleQueueSamples", FormatSamples(decision));
        return new BusinessRuleResult(
            RuleId,
            Version,
            ControlAction.Block,
            Verdict.ResourceAbuse,
            decision.Reason,
            PredicateSatisfied: false,
            PrerequisitesComplete: false,
            boundedFacts);
    }

    private static string FormatSummary(M18ParticleQueueDecision decision)
        => string.Create(CultureInfo.InvariantCulture,
            $"type={decision.ParticleType};invoking={decision.InvokingPlayer};payloadMatchesSession={decision.PayloadIdentityMatchesSession};session={decision.SessionEvents}");

    private static string FormatSamples(M18ParticleQueueDecision decision)
        => string.Create(CultureInfo.InvariantCulture,
            $"events={decision.EventsRetained};capacity={decision.CapacityExhausted}");
}
