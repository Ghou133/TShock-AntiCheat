using System.Collections.Immutable;
using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// Candidate-only, bounded protection for the client packet151 SyncItemDespawn
/// clear shape emitted by protocol-326 after TerraAngel's WipeGroundItems tool
/// turns each local item into air. Packet21 type-zero remains supported for
/// older/alternate clients. A normal
/// pickup remains a valid native operation when its request is aligned with
/// the server-owned item. In the isolated TestLab, a complete multi-item
/// WipeGroundItems sequence is retained as bounded observation. Packet151's
/// item-id-only body and local pickups do not identify an illegal source, so
/// this queue does not turn that sequence into a block or account sanction.
/// </summary>
public sealed record M18GroundItemClearQueueOptions
{
    public bool Enabled { get; init; }
    public int WindowTicks { get; init; } = 60;
    public int PerSessionClearCapacity { get; init; } = 32;
    public int EventCapacity { get; init; } = 64;
    public float MaxRequestPositionDelta { get; init; } = 64f;
    public float ControlTargetTolerance { get; init; } = 4f;
    public float ControlRestoreTolerance { get; init; } = 16f;
    public bool EnablePreForwardBlocks { get; init; }
    public bool EnablePermanentSanctions { get; init; }

    public static M18GroundItemClearQueueOptions Disabled => new();

    public static M18GroundItemClearQueueOptions TestLabCandidate => new()
    {
        Enabled = true,
    };

    public static M18GroundItemClearQueueOptions ProductionCandidate => TestLabCandidate;

    public static M18GroundItemClearQueueOptions ForExecutionScope(
        ExecutionScope scope,
        M18CandidateMode mode = M18CandidateMode.Auto,
        bool recordObservations = true,
        bool enableBlocks = true,
        bool enablePermanentSanctions = false)
    {
        if (!recordObservations || !M18ExecutionModePolicy.RecordEnabled(scope, mode))
            return Disabled;

        return (mode == M18CandidateMode.ProductionCandidate ? ProductionCandidate : TestLabCandidate) with
        {
            EnablePreForwardBlocks = false,
            // The existing packet151 evidence has no exclusive illegal-source
            // predicate. Keep the option closed even in TestLab.
            EnablePermanentSanctions = false,
        };
    }

    public void Validate()
    {
        if (WindowTicks is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(WindowTicks));
        if (PerSessionClearCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(PerSessionClearCapacity));
        if (EventCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(EventCapacity));
        if (!float.IsFinite(MaxRequestPositionDelta) || MaxRequestPositionDelta < 1 || MaxRequestPositionDelta > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxRequestPositionDelta));
        if (!float.IsFinite(ControlTargetTolerance) || ControlTargetTolerance < 0 || ControlTargetTolerance > 128)
            throw new ArgumentOutOfRangeException(nameof(ControlTargetTolerance));
        if (!float.IsFinite(ControlRestoreTolerance) || ControlRestoreTolerance < 0 || ControlRestoreTolerance > 512)
            throw new ArgumentOutOfRangeException(nameof(ControlRestoreTolerance));
    }
}

/// <summary>One decoded packet13 position declaration. It is observation-only
/// and never mutates the native player position.</summary>
public readonly record struct M18GroundItemPlayerControlObservation(
    SessionKey Session,
    long AccountId,
    float PositionX,
    float PositionY,
    bool PositionSnapshotComplete,
    bool ParseComplete,
    bool ClientOrigin,
    bool BeforeSideEffects,
    bool AttributionComplete,
    bool AlreadyCancelled)
{
    // Server position before this packet13 is forwarded; it can only
    // confirm that the preceding declaration reached authoritative state.
    public float ServerActorX { get; init; }
    public float ServerActorY { get; init; }
    public bool ServerActorPositionSnapshotComplete { get; init; }
}

public readonly record struct M18GroundItemClearSequenceSnapshot(
    bool PositionContextComplete,
    bool PreControlAtTarget,
    bool ControlJumpDetected,
    bool BatchContinuationDetected,
    bool WipeSequenceDetected,
    int CompletedWipeSequences)
{
    // CompletedWipeSequences counts paired accepted control returns around
    // raw clear requests. The pre-native Rules layer cannot prove item removal.
    public static M18GroundItemClearSequenceSnapshot Empty => new(
        PositionContextComplete: false,
        PreControlAtTarget: false,
        ControlJumpDetected: false,
        BatchContinuationDetected: false,
        WipeSequenceDetected: false,
        CompletedWipeSequences: 0);
}

/// <summary>Only packet-time state available before the native item handler.</summary>
public readonly record struct M18GroundItemClearObservation(
    SessionKey Session,
    long AccountId,
    int TargetSlot,
    int TargetGeneration,
    int TargetType,
    int TargetStack,
    float TargetX,
    float TargetY,
    float RequestX,
    float RequestY,
    bool TargetSnapshotComplete,
    bool TargetActive,
    bool TargetBeingGrabbed,
    bool NormalPickupShape,
    bool ParseComplete,
    bool ClientOrigin,
    bool BeforeSideEffects,
    bool AttributionComplete)
{
    public byte PacketId { get; init; }
    public bool AlreadyCancelled { get; init; }
    // Packet151 carries only the item slot. It does not carry a client
    // position; false means RequestX/RequestY are intentionally unavailable,
    // not that the adapter may substitute server item coordinates.
    public bool RequestCoordinatesComplete { get; init; } = true;
    // Accepted server-side player position is separate evidence used only to
    // assess a packet151 request whose wire body has no coordinates.
    public float ActorX { get; init; }
    public float ActorY { get; init; }
    public bool ActorPositionSnapshotComplete { get; init; }
}

/// <summary>Finite per-session pairing of packet13 target positioning,
/// packet151 SyncItemDespawn (or packet21 type-zero clear), and the subsequent saved-position restore. It
/// deliberately has no item-wide ledger and no sanction memory.</summary>
public sealed class M18GroundItemClearSequenceTracker
{
    private const int SessionCapacity = 256;
    private const int CompletedCapacity = 8;
    private readonly int windowTicks;
    private readonly float targetTolerance;
    private readonly float restoreTolerance;
    private readonly SessionState?[] sessions = new SessionState?[SessionCapacity];
    private long worldEpoch = long.MinValue;

    private sealed record ControlPoint(long Tick, float X, float Y, bool Accepted, bool Cancelled);
    // This records an accepted control return around a raw clear request.
    // It does not prove the native item handler accepted or removed the item.
    private sealed record CompletedClear(int TargetSlot, int TargetGeneration, long Tick,
        float ReturnX, float ReturnY, float TargetX, float TargetY, byte PacketId);
    private sealed record PendingClear(int TargetSlot, int TargetGeneration, long Tick,
        ControlPoint? ReturnPoint, float TargetX, float TargetY, byte PacketId);
    private sealed record PendingCompletion(int TargetSlot, int TargetGeneration, long Tick,
        float ReturnX, float ReturnY, float TargetX, float TargetY, byte PacketId);
    private sealed class SessionState(SessionKey session, long accountId)
    {
        public SessionKey Session = session;
        public long AccountId = accountId;
        public long LastTick = long.MinValue;
        public ControlPoint? PreviousControl;
        public ControlPoint? LastControl;
        public PendingClear? Pending;
        public PendingCompletion? AwaitingAcceptedReturn;
        public readonly Queue<CompletedClear> Completed = new();
    }

    public M18GroundItemClearSequenceTracker(M18GroundItemClearQueueOptions options)
    {
        windowTicks = options.WindowTicks;
        targetTolerance = options.ControlTargetTolerance;
        restoreTolerance = options.ControlRestoreTolerance;
    }

    public void AdvanceWorld(long epoch)
    {
        if (worldEpoch == epoch) return;
        Array.Clear(sessions);
        worldEpoch = epoch;
    }

    public void Reset()
    {
        Array.Clear(sessions);
        worldEpoch = long.MinValue;
    }

    public void Forget(SessionKey session)
    {
        if (TryGetSlot(session, out int slot) && sessions[slot]?.Session == session)
            sessions[slot] = null;
    }

    public void CancelPendingClear(SessionKey session)
    {
        if (TryGetSlot(session, out int slot) && sessions[slot]?.Session == session)
            sessions[slot]!.Pending = null;
    }

    public void ObserveControl(long tick, M18GroundItemPlayerControlObservation observation)
    {
        if (!TryGetSlot(observation.Session, out int slot) ||
            observation.Session.WorldEpoch != worldEpoch || tick < 0 ||
            !observation.PositionSnapshotComplete || !observation.ParseComplete ||
            !observation.ClientOrigin || !observation.BeforeSideEffects ||
            !float.IsFinite(observation.PositionX) || !float.IsFinite(observation.PositionY))
            return;

        var state = GetState(slot, observation.Session, observation.AccountId);
        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            sessions[slot] = state = new(observation.Session, observation.AccountId);
            return;
        }
        Expire(state, tick);
        if (state.LastControl is { Cancelled: false } lastControl)
        {
            bool accepted = observation.ServerActorPositionSnapshotComplete &&
                float.IsFinite(observation.ServerActorX) && float.IsFinite(observation.ServerActorY) &&
                Near(observation.ServerActorX, observation.ServerActorY,
                    lastControl.X, lastControl.Y, restoreTolerance);
            state.LastControl = lastControl with { Accepted = lastControl.Accepted || accepted };
        }
        if (observation.AlreadyCancelled)
        {
            // Preserve the raw declaration as an attempted control, but break
            // the accepted-position chain. It cannot close a pending clear.
            state.Pending = null;
            state.AwaitingAcceptedReturn = null;
            state.PreviousControl = state.LastControl;
            state.LastControl = new(tick, observation.PositionX, observation.PositionY,
                Accepted: false, Cancelled: true);
            state.LastTick = tick;
            return;
        }
        if (state.AwaitingAcceptedReturn is { } awaiting)
        {
            if (state.LastControl is { Accepted: true } acceptedReturn &&
                Near(acceptedReturn.X, acceptedReturn.Y,
                    awaiting.ReturnX, awaiting.ReturnY, restoreTolerance))
            {
                state.Completed.Enqueue(new(awaiting.TargetSlot, awaiting.TargetGeneration,
                    awaiting.Tick, awaiting.ReturnX, awaiting.ReturnY,
                    awaiting.TargetX, awaiting.TargetY, awaiting.PacketId));
                while (state.Completed.Count > CompletedCapacity)
                    state.Completed.Dequeue();
            }
            state.AwaitingAcceptedReturn = null;
        }
        if (state.Pending is { } pending)
        {
            bool restore = pending.ReturnPoint is { Accepted: true } returnPoint &&
                state.LastControl is { Accepted: true } &&
                Near(observation.PositionX, observation.PositionY, returnPoint.X, returnPoint.Y, restoreTolerance);
            if (restore)
                state.AwaitingAcceptedReturn = new(pending.TargetSlot, pending.TargetGeneration,
                    tick, observation.PositionX, observation.PositionY,
                    pending.TargetX, pending.TargetY, pending.PacketId);
            state.Pending = null;
        }
        state.PreviousControl = state.LastControl;
        state.LastControl = new(tick, observation.PositionX, observation.PositionY,
            Accepted: false, Cancelled: false);
        state.LastTick = tick;
    }

    public M18GroundItemClearSequenceSnapshot ObserveClear(long tick,
        M18GroundItemClearObservation observation)
    {
        if (!TryGetSlot(observation.Session, out int slot) ||
            observation.Session.WorldEpoch != worldEpoch || tick < 0)
            return M18GroundItemClearSequenceSnapshot.Empty;

        var state = GetState(slot, observation.Session, observation.AccountId);
        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            sessions[slot] = state = new(observation.Session, observation.AccountId);
            return M18GroundItemClearSequenceSnapshot.Empty;
        }
        Expire(state, tick);
        state.LastTick = tick;
        // A later clear supersedes an earlier unconfirmed raw attempt. Do not
        // let an unrelated ordinary pickup close its control-return segment.
        state.Pending = null;
        state.AwaitingAcceptedReturn = null;

        var last = state.LastControl;
        if (last is { Cancelled: false })
        {
            bool accepted = observation.ActorPositionSnapshotComplete &&
                float.IsFinite(observation.ActorX) && float.IsFinite(observation.ActorY) &&
                Near(observation.ActorX, observation.ActorY, last.X, last.Y, restoreTolerance);
            last = last with { Accepted = last.Accepted || accepted };
            state.LastControl = last;
        }
        var previous = state.PreviousControl;
        // Packet151 has no position payload. For that shape the sequence can
        // be paired with the server item position for bounded event
        // association, but that position is never reported as client-declared
        // request coordinates or used as proof of a legal pickup.
        float sequenceX = observation.RequestCoordinatesComplete ? observation.RequestX : observation.TargetX;
        float sequenceY = observation.RequestCoordinatesComplete ? observation.RequestY : observation.TargetY;
        bool sequencePositionFinite = float.IsFinite(sequenceX) && float.IsFinite(sequenceY);
        bool preAtTarget = last is { Accepted: true, Cancelled: false } && observation.TargetSnapshotComplete && sequencePositionFinite &&
            Near(last.X, last.Y, sequenceX, sequenceY, targetTolerance);
        if (!preAtTarget)
            return new(
                PositionContextComplete: last is not null,
                PreControlAtTarget: false,
                ControlJumpDetected: false,
                BatchContinuationDetected: false,
                WipeSequenceDetected: false,
                CompletedWipeSequences: state.Completed.Count);

        // A control jump is player displacement between two declarations. The
        // old expression measured the previous declaration to the item, which
        // made a stationary local pickup look like a teleport.
        bool jump = previous is { Accepted: true, Cancelled: false } &&
            last is { Accepted: true, Cancelled: false } &&
            Distance(previous.X, previous.Y, last.X, last.Y) > restoreTolerance;
        var prior = state.Completed.LastOrDefault();
        bool continuation = prior is not null && previous is { Accepted: true, Cancelled: false } &&
            prior.PacketId == observation.PacketId &&
            (prior.TargetSlot != observation.TargetSlot || prior.TargetGeneration != observation.TargetGeneration) &&
            Near(previous.X, previous.Y, prior.ReturnX, prior.ReturnY, restoreTolerance) &&
            (jump || Distance(prior.TargetX, prior.TargetY, observation.TargetX, observation.TargetY) > targetTolerance);
        bool candidate = jump || continuation;
        if (candidate && !observation.AlreadyCancelled)
            state.Pending = new(observation.TargetSlot, observation.TargetGeneration, tick, previous,
                observation.TargetX, observation.TargetY, observation.PacketId);

        return new(
            PositionContextComplete: previous is not null,
            PreControlAtTarget: true,
            ControlJumpDetected: jump,
            BatchContinuationDetected: continuation,
            WipeSequenceDetected: candidate,
            CompletedWipeSequences: state.Completed.Count);
    }

    private SessionState GetState(int slot, SessionKey session, long accountId)
    {
        var state = sessions[slot];
        if (state is null || state.Session != session || state.AccountId != accountId)
            sessions[slot] = state = new(session, accountId);
        return state;
    }

    private void Expire(SessionState state, long tick)
    {
        if (state.LastControl is { } last && tick - last.Tick >= windowTicks)
        {
            state.LastControl = null;
            state.PreviousControl = null;
            state.Pending = null;
            state.AwaitingAcceptedReturn = null;
        }
        while (state.Completed.Count > 0 && tick - state.Completed.Peek().Tick >= windowTicks)
            state.Completed.Dequeue();
        if (state.Pending is { } pending && tick - pending.Tick >= windowTicks)
            state.Pending = null;
        if (state.AwaitingAcceptedReturn is { } awaiting && tick - awaiting.Tick >= windowTicks)
            state.AwaitingAcceptedReturn = null;
    }

    private static float Distance(float x1, float y1, float x2, float y2)
        => MathF.Sqrt(MathF.Pow(x1 - x2, 2) + MathF.Pow(y1 - y2, 2));

    private static bool Near(float x1, float y1, float x2, float y2, float tolerance)
        => MathF.Abs(x1 - x2) <= tolerance && MathF.Abs(y1 - y2) <= tolerance;

    private static bool TryGetSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            session.Generation > 0 && slot >= 0 && slot < SessionCapacity;
    }
}

public sealed record M18GroundItemClearQueueDecision(
    bool Enabled,
    ControlAction Action,
    Verdict Verdict,
    string Reason,
    bool Counted,
    bool RemoteOrMismatched,
    bool CapacityExhausted,
    int TargetSlot,
    int TargetGeneration,
    int TargetType,
    int TargetStack,
    int SessionClearCount,
    int EventsRetained)
{
    public bool IsResourceBlock => Action == ControlAction.Block && Verdict == Verdict.ResourceAbuse;
    public bool IsWipeSequenceBlock => IsResourceBlock && WipeSequenceDetected;
    public bool IsSanctionCandidate => Action == ControlAction.Block && Verdict == Verdict.ProvenCheat &&
        PermanentSanctionCandidate;
    public byte WirePacketId { get; init; }
    public bool WipeSequenceDetected { get; init; }
    public bool PositionContextComplete { get; init; }
    public bool PreControlAtTarget { get; init; }
    public bool ControlJumpDetected { get; init; }
    public bool BatchContinuationDetected { get; init; }
    public int CompletedWipeSequences { get; init; }
    public int Packet151DistinctTargetCount { get; init; }
    public bool RequestTargetAligned { get; init; }
    public bool RequestCoordinatesComplete { get; init; }
    public bool ServerPositionSnapshotComplete { get; init; }
    public bool ServerPositionAligned { get; init; }
    public bool IdentityComplete { get; init; }
    public int SuspiciousClearCount { get; init; }
    public bool PermanentSanctionCandidate { get; init; }
    public float TargetX { get; init; }
    public float TargetY { get; init; }
    public float RequestX { get; init; }
    public float RequestY { get; init; }

    public static M18GroundItemClearQueueDecision Disabled => new(
        Enabled: false,
        Action: ControlAction.Unknown,
        Verdict: Verdict.Unknown,
        Reason: "ground-item-clear-queue-disabled",
        Counted: false,
        RemoteOrMismatched: false,
        CapacityExhausted: false,
        TargetSlot: 0,
        TargetGeneration: 0,
        TargetType: 0,
        TargetStack: 0,
        SessionClearCount: 0,
        EventsRetained: 0)
    {
        WipeSequenceDetected = false,
    };
}

/// <summary>
/// Fixed per-session FIFO. It counts only complete client-originated packet151
/// SyncItemDespawn (or packet21 type-zero) requests that name a currently live
/// server item. Location mismatches and short position sequences are retained
/// for review, but neither is an exclusive illegal-source signal for packet151.
/// This queue therefore leaves native pickup admission unchanged.
/// </summary>
public sealed class M18GroundItemClearQueue
{
    private const int SessionSlotCapacity = 256;
    private readonly M18GroundItemClearQueueOptions _options;
    private readonly SessionState?[] _sessions = new SessionState?[SessionSlotCapacity];
    private readonly M18GroundItemClearSequenceTracker _sequence;
    private long _worldEpoch = long.MinValue;

    public M18GroundItemClearQueue(M18GroundItemClearQueueOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _sequence = new M18GroundItemClearSequenceTracker(_options);
    }

    public bool Enabled => _options.Enabled;

    public void AdvanceWorld(long worldEpoch)
    {
        if (_worldEpoch == worldEpoch)
            return;

        Array.Clear(_sessions, 0, _sessions.Length);
        _worldEpoch = worldEpoch;
        _sequence.AdvanceWorld(worldEpoch);
    }

    public void Reset()
    {
        Array.Clear(_sessions, 0, _sessions.Length);
        _worldEpoch = long.MinValue;
        _sequence.Reset();
    }

    public void Forget(SessionKey session)
    {
        if (!TryGetSessionSlot(session, out var slot))
            return;

        var state = _sessions[slot];
        if (state is not null && state.Session == session)
            _sessions[slot] = null;
        _sequence.Forget(session);
    }

    public void ObservePlayerControls(long tick, M18GroundItemPlayerControlObservation observation)
    {
        if (_options.Enabled)
            _sequence.ObserveControl(tick, observation);
    }

    public M18GroundItemClearQueueDecision Observe(long tick, M18GroundItemClearObservation observation)
    {
        if (!_options.Enabled)
            return M18GroundItemClearQueueDecision.Disabled;

        if (tick < 0)
            return Incomplete(observation, "ground-item-clear-queue-clock-unavailable");
        if (!TryGetSessionSlot(observation.Session, out var sessionSlot))
            return Incomplete(observation, "ground-item-clear-queue-session-unavailable");
        if (observation.AlreadyCancelled)
            return Incomplete(observation, "ground-item-clear-already-cancelled");
        if (observation.TargetSlot < 0 || observation.TargetSlot >= 4096 ||
            observation.TargetGeneration <= 0 || observation.TargetType <= 0 || observation.TargetStack <= 0 ||
            !observation.TargetSnapshotComplete || !observation.TargetActive || observation.TargetBeingGrabbed ||
            !float.IsFinite(observation.TargetX) || !float.IsFinite(observation.TargetY) ||
            observation.RequestCoordinatesComplete &&
                (!float.IsFinite(observation.RequestX) || !float.IsFinite(observation.RequestY)) ||
            !observation.ParseComplete || !observation.ClientOrigin || !observation.BeforeSideEffects)
            return Incomplete(observation, "ground-item-clear-target-context-incomplete");

        var state = _sessions[sessionSlot];
        if (state is null || state.Session != observation.Session || state.AccountId != observation.AccountId)
        {
            state = new SessionState(observation.Session, observation.AccountId, _options.EventCapacity);
            _sessions[sessionSlot] = state;
        }

        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            _sessions[sessionSlot] = new SessionState(observation.Session, observation.AccountId, _options.EventCapacity);
            return Incomplete(observation, "ground-item-clear-queue-clock-regressed");
        }

        state.Expire(tick, _options.WindowTicks);
        state.LastTick = tick;
        var sequence = _sequence.ObserveClear(tick, observation);
        bool requestShapeAligned = observation.RequestCoordinatesComplete &&
            observation.NormalPickupShape &&
            MathF.Abs(observation.RequestX - observation.TargetX) <= _options.MaxRequestPositionDelta &&
            MathF.Abs(observation.RequestY - observation.TargetY) <= _options.MaxRequestPositionDelta;
        bool serverPositionAligned = observation.ActorPositionSnapshotComplete &&
            Near(observation.ActorX, observation.ActorY, observation.TargetX, observation.TargetY,
                _options.MaxRequestPositionDelta);
        // For packet151, the only client wire field is the item id. Missing
        // coordinates are not a mismatch. A remote condition is admitted
        // only from the independently captured accepted server player
        // position, so a normal concentrated pickup/handoff stays legal.
        bool remoteOrMismatched = observation.RequestCoordinatesComplete
            ? !requestShapeAligned
            : observation.ActorPositionSnapshotComplete && !serverPositionAligned;
        bool capacityExhausted = state.EventCount >= _options.EventCapacity ||
            state.ClearCount >= _options.PerSessionClearCapacity;
        state.Add(tick, observation, remoteOrMismatched);
        bool wipe = sequence.WipeSequenceDetected;
        bool identityComplete = observation.AccountId > 0 && observation.AttributionComplete;
        bool stopLoss = false;
        string reason = remoteOrMismatched ? "ground-item-clear-target-mismatch-observed" :
            wipe ? "ground-item-clear-sequence-observed" :
            !identityComplete ? "ground-item-clear-identity-incomplete" :
            "normal-ground-item-pickup-observed";
        return state.Decision(observation, sequence, remoteOrMismatched, capacityExhausted,
            reason, _options.EnablePreForwardBlocks, identityComplete, stopLoss,
            _options.EnablePermanentSanctions, requestShapeAligned, serverPositionAligned);
    }

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            slot >= 0 && slot < SessionSlotCapacity && session.Generation > 0;
    }

    private static bool Near(float x1, float y1, float x2, float y2, float tolerance)
        => MathF.Abs(x1 - x2) <= tolerance && MathF.Abs(y1 - y2) <= tolerance;

    private static M18GroundItemClearQueueDecision Incomplete(
        M18GroundItemClearObservation observation, string reason)
        => new(
            Enabled: true,
            Action: ControlAction.Unknown,
            Verdict: Verdict.Unknown,
            Reason: reason,
            Counted: false,
            RemoteOrMismatched: false,
            CapacityExhausted: false,
            TargetSlot: observation.TargetSlot,
            TargetGeneration: observation.TargetGeneration,
            TargetType: observation.TargetType,
            TargetStack: observation.TargetStack,
            SessionClearCount: 0,
            EventsRetained: 0)
        {
            IdentityComplete = observation.AccountId > 0 && observation.AttributionComplete,
            WirePacketId = observation.PacketId,
            TargetX = observation.TargetX,
            TargetY = observation.TargetY,
            RequestX = observation.RequestX,
            RequestY = observation.RequestY,
            RequestCoordinatesComplete = observation.RequestCoordinatesComplete,
            ServerPositionSnapshotComplete = observation.ActorPositionSnapshotComplete,
        };

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
        }

        public SessionKey Session { get; }
        public long AccountId { get; }
        public long LastTick { get; set; } = long.MinValue;
        public int ClearCount { get; private set; }
        public int SuspiciousClearCount { get; private set; }
        public int EventCount => _count;
        public int Packet151DistinctTargetCount
        {
            get
            {
                HashSet<(int Slot, int Generation)> targets = [];
                for (int offset = 0; offset < _count; offset++)
                {
                    var sample = _samples[(_start + offset) % _samples.Length];
                    if (sample.Packet151)
                        targets.Add((sample.TargetSlot, sample.TargetGeneration));
                }
                return targets.Count;
            }
        }

        public void Expire(long tick, int windowTicks)
        {
            while (_count > 0 && tick - _samples[_start].Tick >= windowTicks)
            {
                bool removedSuspicious = _samples[_start].RemoteOrMismatched;
                _start = (_start + 1) % _samples.Length;
                _count--;
                ClearCount--;
                if (removedSuspicious)
                    SuspiciousClearCount--;
            }
        }

        public void Add(long tick, M18GroundItemClearObservation observation, bool remoteOrMismatched)
        {
            if (_count == _samples.Length)
            {
                bool removedSuspicious = _samples[_start].RemoteOrMismatched;
                _start = (_start + 1) % _samples.Length;
                _count--;
                ClearCount--;
                if (removedSuspicious)
                    SuspiciousClearCount--;
            }
            int index = (_start + _count) % _samples.Length;
            _samples[index] = new(tick, observation.TargetSlot, observation.TargetGeneration,
                remoteOrMismatched, observation.PacketId == 151);
            _count++;
            ClearCount++;
            if (remoteOrMismatched)
                SuspiciousClearCount++;
        }

        public M18GroundItemClearQueueDecision Decision(
            M18GroundItemClearObservation observation,
            M18GroundItemClearSequenceSnapshot sequence,
            bool remoteOrMismatched,
            bool capacityExhausted,
            string reason,
            bool enablePreForwardBlocks,
            bool identityComplete,
            bool stopLoss,
            bool enablePermanentSanctions,
            bool requestShapeAligned,
            bool serverPositionAligned)
        {
            bool normal = !remoteOrMismatched && identityComplete;
            return new(
                Enabled: true,
                Action: normal ? ControlAction.Pass : ControlAction.Unknown,
                Verdict: normal ? Verdict.Pass : Verdict.Unknown,
                Reason: reason,
                Counted: true,
                RemoteOrMismatched: remoteOrMismatched,
                CapacityExhausted: capacityExhausted,
                TargetSlot: observation.TargetSlot,
                TargetGeneration: observation.TargetGeneration,
                TargetType: observation.TargetType,
                TargetStack: observation.TargetStack,
                SessionClearCount: ClearCount,
                EventsRetained: _count)
            {
                WipeSequenceDetected = sequence.WipeSequenceDetected,
                PositionContextComplete = sequence.PositionContextComplete,
                PreControlAtTarget = sequence.PreControlAtTarget,
                ControlJumpDetected = sequence.ControlJumpDetected,
                BatchContinuationDetected = sequence.BatchContinuationDetected,
                CompletedWipeSequences = sequence.CompletedWipeSequences,
                Packet151DistinctTargetCount = Packet151DistinctTargetCount,
                RequestTargetAligned = observation.RequestCoordinatesComplete
                    ? requestShapeAligned : serverPositionAligned,
                RequestCoordinatesComplete = observation.RequestCoordinatesComplete,
                ServerPositionSnapshotComplete = observation.ActorPositionSnapshotComplete,
                ServerPositionAligned = serverPositionAligned,
                IdentityComplete = identityComplete,
                SuspiciousClearCount = SuspiciousClearCount,
                PermanentSanctionCandidate = false,
                WirePacketId = observation.PacketId,
                TargetX = observation.TargetX,
                TargetY = observation.TargetY,
                RequestX = observation.RequestX,
                RequestY = observation.RequestY,
            };
        }

        private readonly record struct Sample(long Tick, int TargetSlot, int TargetGeneration,
            bool RemoteOrMismatched, bool Packet151);
    }
}

public static class M18GroundItemClearQueueRules
{
    public const string RuleId = "F08.GroundItemClearBoundedGuard";
    public const string Version = "1.6.0";
    public const string ContractVersion = "terraria1.4.5.8-326-ground-item-clear-observation-v7";

    public static BusinessRuleResult Observe(RuleInputContext input,
        M18GroundItemClearQueueDecision decision)
    {
        var facts = ImmutableDictionary<string, string>.Empty
            .Add("contractVersion", ContractVersion)
            .Add("wirePacketId", decision.WirePacketId.ToString(CultureInfo.InvariantCulture))
            .Add("targetSlot", decision.TargetSlot.ToString(CultureInfo.InvariantCulture))
            .Add("targetGeneration", decision.TargetGeneration.ToString(CultureInfo.InvariantCulture))
            .Add("targetType", decision.TargetType.ToString(CultureInfo.InvariantCulture))
            .Add("targetStack", decision.TargetStack.ToString(CultureInfo.InvariantCulture))
            .Add("targetCoordinates", string.Create(CultureInfo.InvariantCulture,
                $"{decision.TargetX},{decision.TargetY}"))
            .Add("requestCoordinates", decision.RequestCoordinatesComplete
                ? string.Create(CultureInfo.InvariantCulture, $"{decision.RequestX},{decision.RequestY}")
                : "unavailable(packet151-body-id-only)")
            .Add("requestCoordinatesComplete", decision.RequestCoordinatesComplete.ToString())
            .Add("remoteOrMismatched", decision.RemoteOrMismatched.ToString())
            .Add("serverPosition", $"complete={decision.ServerPositionSnapshotComplete};aligned={decision.ServerPositionAligned}")
            .Add("capacityExhausted", decision.CapacityExhausted.ToString())
            .Add("sessionClearCount", decision.SessionClearCount.ToString(CultureInfo.InvariantCulture))
            .Add("suspiciousClearCount", decision.SuspiciousClearCount.ToString(CultureInfo.InvariantCulture))
            .Add("packet151DistinctTargetCount", decision.Packet151DistinctTargetCount.ToString(CultureInfo.InvariantCulture))
            .Add("eventsRetained", decision.EventsRetained.ToString(CultureInfo.InvariantCulture))
            .Add("positionSequence", string.Create(CultureInfo.InvariantCulture,
                $"context={decision.PositionContextComplete};preTarget={decision.PreControlAtTarget};" +
                $"jump={decision.ControlJumpDetected};continuation={decision.BatchContinuationDetected};" +
                $"completed={decision.CompletedWipeSequences}"))
            .Add("identityComplete", decision.IdentityComplete.ToString())
            .Add("actionContract", decision.Action == ControlAction.Pass ?
                "normal-pickup-native-path" : "record-only")
            .Add("nativeItemClearAccepted", "unverified-at-pre-native-boundary")
            .Add("sanctionContract", "none");
        return new BusinessRuleResult(RuleId, Version,
            decision.Action, decision.Verdict, decision.Reason,
            PredicateSatisfied: false,
            PrerequisitesComplete: false, facts);
    }
}
