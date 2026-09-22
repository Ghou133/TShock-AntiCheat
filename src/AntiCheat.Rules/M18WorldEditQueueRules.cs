using System.Collections.Immutable;
using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// Candidate-only bounded work admission for the direct single-cell world-edit
/// packets used by the TerraAngel brush. This is a resource stop-loss, not a
/// construction legality or account-cheating predicate.
/// </summary>
public sealed record M18WorldEditQueueOptions
{
    public bool Enabled { get; init; }
    public int WindowTicks { get; init; } = 120;
    public int PerSessionWorkUnits { get; init; } = 2048;
    public int EventCapacity { get; init; } = 2048;
    public bool EnablePreForwardBlocks { get; init; } = true;

    public static M18WorldEditQueueOptions Disabled => new();

    public static M18WorldEditQueueOptions TestLabCandidate => new()
    {
        Enabled = true,
    };

    public static M18WorldEditQueueOptions ProductionCandidate => TestLabCandidate;

    public static M18WorldEditQueueOptions ForExecutionScope(
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
        if (PerSessionWorkUnits is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(PerSessionWorkUnits));
        if (EventCapacity is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(EventCapacity));
    }
}

public enum M18WorldEditKind
{
    Tile,
    Liquid,
}

/// <summary>Only fields available before TShock's native world handler runs.</summary>
public readonly record struct M18WorldEditObservation(
    SessionKey Session,
    long AccountId,
    M18WorldEditKind Kind,
    int X,
    int Y,
    int Operation,
    int Data,
    int Amount,
    int WorkUnits,
    bool ParseComplete,
    bool ClientOrigin,
    bool BeforeSideEffects,
    bool AttributionComplete);

public sealed record M18WorldEditQueueDecision(
    bool Enabled,
    ControlAction Action,
    Verdict Verdict,
    string Reason,
    bool Counted,
    bool CapacityExhausted,
    int WorkUnits,
    int SessionWorkUnits,
    int EventsRetained,
    M18WorldEditKind Kind,
    int Operation)
{
    public bool IsResourceBlock => Action == ControlAction.Block && Verdict == Verdict.ResourceAbuse;

    public static M18WorldEditQueueDecision Disabled => new(
        Enabled: false,
        Action: ControlAction.Unknown,
        Verdict: Verdict.Unknown,
        Reason: "world-edit-queue-disabled",
        Counted: false,
        CapacityExhausted: false,
        WorkUnits: 0,
        SessionWorkUnits: 0,
        EventsRetained: 0,
        Kind: M18WorldEditKind.Tile,
        Operation: 0);
}

/// <summary>
/// Fixed-size per-session FIFO. It counts the actual incoming single-cell
/// mutation messages, not a guessed rectangle or a client-side brush setting.
/// Replacement is two work units because the native operation can destroy and
/// place in one request. No sanction state is retained here.
/// </summary>
public sealed class M18WorldEditQueue
{
    private const int SessionSlotCapacity = 256;
    private readonly M18WorldEditQueueOptions _options;
    private readonly SessionState?[] _sessions = new SessionState?[SessionSlotCapacity];
    private long _worldEpoch = long.MinValue;

    public M18WorldEditQueue(M18WorldEditQueueOptions options)
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

    public M18WorldEditQueueDecision Observe(long tick, M18WorldEditObservation observation)
    {
        if (!_options.Enabled)
            return M18WorldEditQueueDecision.Disabled;

        if (tick < 0)
            return Incomplete(observation, "world-edit-queue-clock-unavailable");
        if (!TryGetSessionSlot(observation.Session, out var sessionSlot))
            return Incomplete(observation, "world-edit-queue-session-unavailable");
        if (observation.AccountId <= 0 || observation.X < 0 || observation.Y < 0 ||
            observation.WorkUnits is < 1 or > 2 ||
            !Enum.IsDefined(observation.Kind) ||
            !observation.ParseComplete || !observation.ClientOrigin ||
            !observation.BeforeSideEffects || !observation.AttributionComplete ||
            !IsOperationValid(observation))
            return Incomplete(observation, "world-edit-queue-input-incomplete");

        var state = _sessions[sessionSlot];
        if (state is null || state.Session != observation.Session || state.AccountId != observation.AccountId)
        {
            state = new SessionState(observation.Session, observation.AccountId, _options.EventCapacity);
            _sessions[sessionSlot] = state;
        }

        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            _sessions[sessionSlot] = new SessionState(observation.Session, observation.AccountId, _options.EventCapacity);
            return Incomplete(observation, "world-edit-queue-clock-regressed");
        }

        state.Expire(tick, _options.WindowTicks);
        state.LastTick = tick;
        if (state.EventCount >= _options.EventCapacity ||
            state.WorkUnits > _options.PerSessionWorkUnits - observation.WorkUnits)
            return state.Decision(observation, false, true,
                state.EventCount >= _options.EventCapacity
                    ? "world-edit-window-event-capacity-exhausted"
                    : "world-edit-window-work-budget-exhausted", _options.EnablePreForwardBlocks);

        state.Add(tick, observation);
        return state.Decision(observation, true, false, "world-edit-window-observed", _options.EnablePreForwardBlocks);
    }

    private static bool IsOperationValid(M18WorldEditObservation observation)
    {
        if (observation.Kind == M18WorldEditKind.Tile)
            return observation.Operation is >= 0 and <= 23 && observation.Data >= 0;

        return observation.Operation == 0 && observation.Amount is >= 0 and <= 255 &&
            (observation.Data is >= 0 and <= 3 || observation.Amount == 0 && observation.Data == 255);
    }

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            slot >= 0 && slot < SessionSlotCapacity && session.Generation > 0;
    }

    private static M18WorldEditQueueDecision Incomplete(M18WorldEditObservation observation, string reason)
        => new(
            Enabled: true,
            Action: ControlAction.Unknown,
            Verdict: Verdict.Unknown,
            Reason: reason,
            Counted: false,
            CapacityExhausted: false,
            WorkUnits: observation.WorkUnits,
            SessionWorkUnits: 0,
            EventsRetained: 0,
            Kind: observation.Kind,
            Operation: observation.Operation);

    private sealed class SessionState
    {
        private readonly Sample[] _samples;
        private int _start;
        private int _count;

        public SessionState(SessionKey session, long accountId, int capacity)
        {
            Session = session;
            AccountId = accountId;
            _samples = new Sample[capacity];
            LastTick = long.MinValue;
        }

        public SessionKey Session { get; }
        public long AccountId { get; }
        public long LastTick { get; set; }
        public int WorkUnits { get; private set; }
        public int EventCount => _count;

        public void Expire(long tick, int windowTicks)
        {
            while (_count > 0 && tick - _samples[_start].Tick >= windowTicks)
            {
                WorkUnits -= _samples[_start].WorkUnits;
                _start = (_start + 1) % _samples.Length;
                _count--;
            }
        }

        public void Add(long tick, M18WorldEditObservation observation)
        {
            int index = (_start + _count) % _samples.Length;
            _samples[index] = new(tick, observation.WorkUnits, observation.Kind, observation.Operation);
            _count++;
            WorkUnits += observation.WorkUnits;
        }

        public M18WorldEditQueueDecision Decision(M18WorldEditObservation observation,
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
                WorkUnits: observation.WorkUnits,
                SessionWorkUnits: WorkUnits,
                EventsRetained: _count,
                Kind: observation.Kind,
                Operation: observation.Operation);
        }

        private readonly record struct Sample(long Tick, int WorkUnits,
            M18WorldEditKind Kind, int Operation);
    }
}

public static class M18WorldEditQueueRules
{
    public const string RuleId = "F07.WorldEditDensityBudget";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-direct-world-edit-density-candidate-v1";

    public static BusinessRuleResult ResourceBlock(
        RuleInputContext input,
        ImmutableDictionary<string, string> facts,
        M18WorldEditQueueDecision decision)
    {
        if (!decision.IsResourceBlock)
            throw new ArgumentException("The decision is not a resource block.", nameof(decision));

        var boundedFacts = facts
            .SetItem("worldEditQueue", FormatSummary(decision))
            .SetItem("worldEditQueueSamples", FormatSamples(decision));
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

    private static string FormatSummary(M18WorldEditQueueDecision decision)
        => string.Create(CultureInfo.InvariantCulture,
            $"kind={decision.Kind};operation={decision.Operation};cost={decision.WorkUnits};session={decision.SessionWorkUnits}");

    private static string FormatSamples(M18WorldEditQueueDecision decision)
        => string.Create(CultureInfo.InvariantCulture,
            $"events={decision.EventsRetained};capacity={decision.CapacityExhausted}");
}
