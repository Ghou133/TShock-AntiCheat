using System;
using System.Collections.Immutable;
using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum M18NpcStrikeStage
{
    Unknown,
    PreHardmode,
    Hardmode,
    PostPlantera,
    PostMoonlord,
}

/// <summary>
/// Candidate-only bounded state for the TerraAngel NPC-strike route.
/// The limits in this type are resource budgets for a lab experiment, not
/// proof predicates and not production qualifications.
/// </summary>
public sealed record M18NpcStrikeQueueOptions
{
    public bool Enabled { get; init; }
    public int WindowTicks { get; init; } = 60;
    public int LowDamageMaximum { get; init; } = 1;
    public int PerTargetLowDamageLimit { get; init; } = 64;
    public int PerSessionLowDamageLimit { get; init; } = 192;
    public int LowDamageRingCapacity { get; init; } = 256;
    public int HighSampleCapacity { get; init; } = 16;
    // This is an explicit candidate stop-loss for the ordinary packet28 path.
    // It is not a universal legal damage maximum: weapon/effect provenance and
    // server-side extensions are deliberately outside this queue's contract.
    public int ExtremeDamageThreshold { get; init; } = 9999;
    public int HardmodeExtremeDamageThreshold { get; init; } = 15000;
    public int PostPlanteraExtremeDamageThreshold { get; init; } = 22000;
    public int PostMoonlordExtremeDamageThreshold { get; init; } = 30000;
    public bool EnableExtremeDamageStopLoss { get; init; } = true;
    // Reserved for a future exclusive source proof. Sequence shape alone is
    // observed because server-tick batching can mimic a rapid return cycle.
    public bool EnableButcherSequenceStopLoss { get; init; } = true;
    // The target can advance between the packet13 declaration and the next
    // packet28 callback. Keep this a small, finite motion window (four tiles),
    // not a generic attack-range or player exemption.
    public float ButcherPositionTolerance { get; init; } = 64f;
    public float ButcherRestoreTolerance { get; init; } = 16f;
    public bool EnablePreForwardBlocks { get; init; } = true;
    public bool EnablePermanentSanctions { get; init; }

    public static M18NpcStrikeQueueOptions Disabled => new();

    public static M18NpcStrikeQueueOptions TestLabCandidate => new()
    {
        Enabled = true,
        ExtremeDamageThreshold = 9999,
        HardmodeExtremeDamageThreshold = 15000,
        PostPlanteraExtremeDamageThreshold = 22000,
        PostMoonlordExtremeDamageThreshold = 30000,
        EnableExtremeDamageStopLoss = true,
    };

    public static M18NpcStrikeQueueOptions ProductionCandidate => TestLabCandidate;

    public static M18NpcStrikeQueueOptions ForExecutionScope(
        ExecutionScope scope,
        M18CandidateMode mode = M18CandidateMode.Auto,
        bool recordObservations = true,
        bool enableBlocks = true,
        bool enablePermanentSanctions = false)
    {
        if (!recordObservations || !M18ExecutionModePolicy.RecordEnabled(scope, mode))
            return Disabled;

        bool controls = M18ExecutionModePolicy.CandidateControlsEnabled(scope, mode);
        return (mode == M18CandidateMode.ProductionCandidate ? ProductionCandidate : TestLabCandidate) with
        {
            EnablePreForwardBlocks = enableBlocks && controls,
            // The legacy switch remains readable for configuration
            // compatibility. This queue has no exclusive attack-source proof.
            EnablePermanentSanctions = false,
        };
    }

    public void Validate()
    {
        if (WindowTicks is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(WindowTicks));
        if (LowDamageMaximum is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(LowDamageMaximum));
        if (PerTargetLowDamageLimit is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(PerTargetLowDamageLimit));
        if (PerSessionLowDamageLimit is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(PerSessionLowDamageLimit));
        if (LowDamageRingCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(LowDamageRingCapacity));
        if (HighSampleCapacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(HighSampleCapacity));
        if (ExtremeDamageThreshold is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ExtremeDamageThreshold));
        if (HardmodeExtremeDamageThreshold is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(HardmodeExtremeDamageThreshold));
        if (PostPlanteraExtremeDamageThreshold is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PostPlanteraExtremeDamageThreshold));
        if (PostMoonlordExtremeDamageThreshold is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PostMoonlordExtremeDamageThreshold));
        if (!float.IsFinite(ButcherPositionTolerance) || ButcherPositionTolerance < 0 || ButcherPositionTolerance > 128)
            throw new ArgumentOutOfRangeException(nameof(ButcherPositionTolerance));
        if (!float.IsFinite(ButcherRestoreTolerance) || ButcherRestoreTolerance < 0 || ButcherRestoreTolerance > 512)
            throw new ArgumentOutOfRangeException(nameof(ButcherRestoreTolerance));
    }
}

/// <summary>One decoded packet13 control declaration, retained only as short-lived
/// context for the ordinary TerraAngel Butcher sequence.</summary>
public readonly record struct M18NpcPlayerControlObservation(
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
    // Captured before this packet13 is forwarded. It confirms whether the
    // preceding declaration actually became the server's player position.
    public float ServerActorX { get; init; }
    public float ServerActorY { get; init; }
    public bool ServerActorPositionSnapshotComplete { get; init; }
}

/// <summary>Finite evidence produced by the position/strike sequence tracker.
/// It is a stop-loss input, never an account-sanction proof.</summary>
public readonly record struct M18NpcStrikeBehaviorSnapshot(
    bool PositionContextComplete,
    bool PreControlAtTarget,
    bool ControlJumpDetected,
    bool ReturnPositionStable,
    bool RepeatedAttackSignature,
    bool CrossTargetContinuation,
    bool PatternDetected,
    int CompletedTargetSequences,
    int CompletedTargetDeathSequences,
    int CurrentTargetStrikes,
    bool SummonContextComplete,
    bool SummonMaintenanceBuffObserved,
    bool MatchingSummonEntityObserved,
    int MatchingSummonEntityCount)
{
    // Aggregate completed deaths remain available for bounded diagnostics, but
    // sanction predicates must use the exact completed segment matched by the
    // current continuation.
    public int MatchingCompletedTargetDeathSequences { get; init; }
    // Local identity of the exact active strike segment. A later native
    // completion may only add death evidence to this segment.
    public Guid PendingSequenceId { get; init; }
    public bool RawCancelledControlAttemptObserved { get; init; }

    public static M18NpcStrikeBehaviorSnapshot Empty => new(
        PositionContextComplete: false,
        PreControlAtTarget: false,
        ControlJumpDetected: false,
        ReturnPositionStable: false,
        RepeatedAttackSignature: false,
        CrossTargetContinuation: false,
        PatternDetected: false,
        CompletedTargetSequences: 0,
        CompletedTargetDeathSequences: 0,
        CurrentTargetStrikes: 0,
        SummonContextComplete: false,
        SummonMaintenanceBuffObserved: false,
        MatchingSummonEntityObserved: false,
        MatchingSummonEntityCount: 0);
}

/// <summary>Small auxiliary source context shared from the native summon
/// observer. It can explain a legal candidate, but it never grants a whole
/// player an exemption from the Butcher sequence.</summary>
public readonly record struct M18NpcSummonAuxiliaryContext(
    bool SnapshotComplete,
    bool MaintenanceBuffObserved,
    bool MatchingObservedEntity,
    int MatchingObservedEntityCount);

/// <summary>
/// The facts available at the candidate queue boundary. A queue observation
/// is intentionally rejected without state mutation when attribution or the
/// target generation is incomplete.
/// </summary>
public readonly record struct M18NpcStrikeObservation(
    SessionKey Session,
    long AccountId,
    int TargetSlot,
    int TargetGeneration,
    int TargetType,
    int WireDamage,
    int ReceiverDamage,
    bool TargetSnapshotComplete,
    bool TargetActive,
    bool TargetGenerationMatchesCurrent,
    bool ClientOrigin,
    bool AttributionComplete,
    bool LegalExceptionsExcluded)
{
    // These are init-only extensions so existing packet-28 construction sites
    // retain their old wire contract. The adapter replaces the defaults with
    // the current server world/NPC snapshot before queue admission.
    public int WorldId { get; init; }
    public M18NpcStrikeStage Stage { get; init; } = M18NpcStrikeStage.PreHardmode;
    public bool StageSnapshotComplete { get; init; } = true;
    public int TargetLife { get; init; } = 100;
    public int TargetLifeMax { get; init; } = 100;
    public bool TargetFriendly { get; init; }
    public bool TargetDummy { get; init; }
    public float TargetX { get; init; }
    public float TargetY { get; init; }
    public bool TargetPositionSnapshotComplete { get; init; }
    public float WireKnockback { get; init; }
    public int WireDirection { get; init; }
    public int WireCriticalFlag { get; init; }
    public bool AlreadyCancelled { get; init; }
    // Authoritative player position at the packet28 callback, after the
    // preceding packet13 has had an opportunity to reach the native receiver.
    public float ActorX { get; init; }
    public float ActorY { get; init; }
    public bool ActorPositionSnapshotComplete { get; init; }
    public bool SummonContextComplete { get; init; }
    public bool SummonMaintenanceBuffObserved { get; init; }
    public bool MatchingSummonEntityObserved { get; init; }
    public int MatchingSummonEntityCount { get; init; }
}

/// <summary>Post-native evidence for one client packet28. This record is
/// intentionally separate from the packet-time observation: a dead target is
/// no longer a valid live snapshot, but its closed transaction still retains
/// the pre-hit identity, native relay and account attribution.</summary>
public readonly record struct M18NpcStrikePostNativeObservation(
    SessionKey Session,
    long AccountId,
    int TargetSlot,
    int TargetGeneration,
    int TargetType,
    int WireDamage,
    int ReceiverDamage,
    int TargetLifeMax,
    int LifeBefore,
    int LifeAfter,
    bool TargetFriendly,
    bool TargetDummy,
    M18NpcStrikeStage Stage,
    bool StageSnapshotComplete,
    bool PositionContextComplete,
    bool PreControlAtTarget,
    bool ControlJumpDetected,
    bool ClientOrigin,
    bool AttributionComplete,
    bool LegalExceptionsExcluded,
    bool NativeStrikeEntryObserved,
    bool LootMethodEntryObserved,
    bool RelayAttemptObserved)
{
    public bool ObservedDeath => LifeBefore > 0 && LifeAfter <= 0;
    public bool OverkillBeyondTargetMaximum => TargetLifeMax > 0 && ReceiverDamage > TargetLifeMax;
}

public sealed record M18NpcStrikeQueueDecision(
    bool Enabled,
    ControlAction Action,
    Verdict Verdict,
    string Reason,
    bool Counted,
    bool LowDamage,
    bool ExtremeDamage,
    bool CapacityExhausted,
    int TargetLowDamageCount,
    int SessionLowDamageCount,
    int LowSamplesRetained,
    int HighSamplesRetained,
    int HighSamplesDropped,
    int MaxWireDamage,
    int MaxTargetType)
{
    // The historical member names are retained for source compatibility with
    // the M18 reports. Their bounded ring now represents positive-damage
    // density, not only the one-damage band; use the aliases in new evidence.
    public int TargetPositiveDamageCount => TargetLowDamageCount;
    public int SessionPositiveDamageCount => SessionLowDamageCount;
    public int PositiveDamageSamplesRetained => LowSamplesRetained;
    public M18NpcStrikeStage Stage { get; init; } = M18NpcStrikeStage.Unknown;
    public bool StageSnapshotComplete { get; init; }
    public int WorldId { get; init; }
    public int TargetLife { get; init; }
    public int TargetLifeMax { get; init; }
    public bool TargetMaxLifeExceeded { get; init; }
    public int StageExtremeDamageThreshold { get; init; }
    public bool ExtremeGateSatisfied { get; init; }
    public bool PermanentSanctionCandidate { get; init; }
    public bool ButcherPatternDetected { get; init; }
    public bool ButcherPositionContextComplete { get; init; }
    public bool ButcherPreControlAtTarget { get; init; }
    public bool ButcherControlJumpDetected { get; init; }
    public bool ButcherReturnPositionStable { get; init; }
    public bool ButcherRepeatedAttackSignature { get; init; }
    public bool ButcherCrossTargetContinuation { get; init; }
    public int ButcherCompletedTargetSequences { get; init; }
    public int ButcherCompletedTargetDeathSequences { get; init; }
    public int ButcherMatchingTargetDeathSequences { get; init; }
    public Guid ButcherPendingSequenceId { get; init; }
    public bool ButcherRawCancelledControlAttemptObserved { get; init; }
    public int ButcherCurrentTargetStrikes { get; init; }
    public bool ButcherSequenceSanctionCandidate { get; init; }
    public bool SummonContextComplete { get; init; }
    public bool SummonMaintenanceBuffObserved { get; init; }
    public bool MatchingSummonEntityObserved { get; init; }
    public int MatchingSummonEntityCount { get; init; }
    public bool PostNativeOneShotDeathEvidence { get; init; }
    public bool PostNativeSanctionCandidate { get; init; }
    public int WorldHighMaxDamage { get; init; }
    public int StageHighMaxDamage { get; init; }
    public int WorldHighSamplesRetained { get; init; }
    public int WorldHighSamplesDropped { get; init; }
    public bool IsResourceBlock => Action == ControlAction.Block && Verdict == Verdict.ResourceAbuse;
    public bool IsExtremeDamageBlock => Action == ControlAction.Block && Verdict == Verdict.UnsafeInput && ExtremeDamage;
    public bool IsButcherPatternBlock => Action == ControlAction.Block &&
        Verdict == Verdict.UnsafeInput && Reason == "npc-strike-ordinary-butcher-sequence-preforward-stop";
    public bool IsButcherSequenceSanctionCandidate => Action == ControlAction.Block &&
        Verdict == Verdict.ProvenCheat && ButcherSequenceSanctionCandidate;
    public bool IsPostNativeSanctionCandidate => Action == ControlAction.Block &&
        Verdict == Verdict.ProvenCheat && PostNativeSanctionCandidate;

    public static M18NpcStrikeQueueDecision Disabled => new(
        Enabled: false,
        Action: ControlAction.Unknown,
        Verdict: Verdict.Unknown,
        Reason: "npc-strike-queue-disabled",
        Counted: false,
        LowDamage: false,
        ExtremeDamage: false,
        CapacityExhausted: false,
        TargetLowDamageCount: 0,
        SessionLowDamageCount: 0,
        LowSamplesRetained: 0,
        HighSamplesRetained: 0,
        HighSamplesDropped: 0,
        MaxWireDamage: 0,
        MaxTargetType: 0)
    {
        Stage = M18NpcStrikeStage.Unknown,
    };
}

/// <summary>
/// Small source-sequence tracker for the ordinary TerraAngel Butcher path.
/// It recognizes a target-position control declaration followed by repeated
/// identical packet28 inputs and/or a restored control position before a new
/// target. The retained history is deliberately smaller than the old damage
/// budgets and is not itself an account-sanction store.
/// </summary>
public sealed class M18NpcStrikeBehaviorTracker
{
    private const int SessionSlotCapacity = 256;
    private const int CompletedSequenceCapacity = 8;
    private readonly int windowTicks;
    private readonly float positionTolerance;
    private readonly float restoreTolerance;
    private readonly SessionState?[] sessions = new SessionState?[SessionSlotCapacity];
    private long worldEpoch = long.MinValue;

    private sealed record ControlPoint(long Tick, float X, float Y, bool Accepted);
    private sealed record RawControlPoint(long Tick, float X, float Y, bool CancelledAtObservation);
    private sealed record StrikeSignature(int Damage, float Knockback, int Direction, int Critical);
    private sealed class PendingSequence
    {
        public PendingSequence(int targetSlot, int targetGeneration, int targetType, StrikeSignature signature,
            long tick, long targetControlTick, ControlPoint? returnPoint, float targetX, float targetY, bool targetPositionComplete,
            bool cancelled)
        {
            SequenceId = Guid.NewGuid();
            TargetSlot = targetSlot;
            TargetGeneration = targetGeneration;
            TargetType = targetType;
            Signature = signature;
            StartedTick = tick;
            TargetControlTick = targetControlTick;
            ReturnPoint = returnPoint;
            TargetX = targetX;
            TargetY = targetY;
            TargetPositionComplete = targetPositionComplete;
            AnyCancelled = cancelled;
            StrikeCount = 1;
        }

        public int TargetSlot { get; }
        public Guid SequenceId { get; }
        public int TargetGeneration { get; }
        public int TargetType { get; }
        public StrikeSignature Signature { get; }
        public long StartedTick { get; }
        public long TargetControlTick { get; }
        public ControlPoint? ReturnPoint { get; }
        public float TargetX { get; }
        public float TargetY { get; }
        public bool TargetPositionComplete { get; }
        public int StrikeCount { get; set; }
        public bool AnyCancelled { get; set; }
        public bool Blocked { get; set; }
        public bool PostNativeDeathEvidence { get; set; }
    }
    private sealed record CompletedSequence(int TargetSlot, int TargetGeneration, int TargetType,
        StrikeSignature Signature, long Tick, long StartedTick, long TargetControlTick,
        float ReturnX, float ReturnY, bool DeathObserved);
    private sealed record PendingCompletion(int TargetSlot, int TargetGeneration, int TargetType,
        StrikeSignature Signature, long Tick, long StartedTick, long TargetControlTick,
        float ReturnX, float ReturnY, bool DeathObserved);
    private sealed class SessionState(SessionKey session, long accountId)
    {
        public SessionKey Session = session;
        public long AccountId = accountId;
        public long LastTick = long.MinValue;
        public ControlPoint? PreviousControl;
        public ControlPoint? LastControl;
        public RawControlPoint? LastRawControl;
        public PendingSequence? Pending;
        public PendingCompletion? AwaitingAcceptedReturn;
        public readonly Queue<CompletedSequence> Completed = new();
    }

    public M18NpcStrikeBehaviorTracker(M18NpcStrikeQueueOptions options)
    {
        windowTicks = options.WindowTicks;
        positionTolerance = options.ButcherPositionTolerance;
        restoreTolerance = options.ButcherRestoreTolerance;
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
        if (TryGetSessionSlot(session, out int slot) && sessions[slot]?.Session == session)
            sessions[slot] = null;
    }

    public void Cancel(SessionKey session)
    {
        if (TryGetSessionSlot(session, out int slot) && sessions[slot]?.Session == session)
            sessions[slot]!.Pending = null;
    }

    /// <summary>Keeps the current source-shaped burst suppressed until its
    /// following packet13 restore. TerraAngel can emit more than one packet28
    /// for a target; clearing the pending state after the first stop would
    /// allow the tail of the same finite burst through the native receiver.</summary>
    public void Suppress(SessionKey session)
    {
        if (TryGetSessionSlot(session, out int slot) && sessions[slot]?.Session == session &&
            sessions[slot]!.Pending is { } pending)
        {
            pending.Blocked = true;
            pending.AnyCancelled = true;
        }
    }

    public M18NpcStrikeBehaviorSnapshot ObserveControl(long tick,
        M18NpcPlayerControlObservation observation)
    {
        if (!TryGetSessionSlot(observation.Session, out int slot) ||
            observation.Session.WorldEpoch != worldEpoch || tick < 0 ||
            !observation.PositionSnapshotComplete || !observation.ParseComplete ||
            !observation.ClientOrigin || !observation.BeforeSideEffects ||
            !float.IsFinite(observation.PositionX) || !float.IsFinite(observation.PositionY))
            return M18NpcStrikeBehaviorSnapshot.Empty;

        var state = GetState(slot, observation.Session, observation.AccountId);
        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            sessions[slot] = state = new(observation.Session, observation.AccountId);
            return M18NpcStrikeBehaviorSnapshot.Empty;
        }
        Expire(state, tick);
        // Keep the latest raw declaration for bounded diagnostics. This bit is
        // only the cancellation status at this hook, not final native receipt.
        // It never enters accepted movement, completion, or sanction proof.
        state.LastRawControl = new(tick, observation.PositionX, observation.PositionY,
            observation.AlreadyCancelled);
        if (observation.AlreadyCancelled)
        {
            state.LastTick = tick;
            return M18NpcStrikeBehaviorSnapshot.Empty;
        }

        // A pre-forward declaration is only an attempt. The server position
        // seen before the next control establishes whether the previous one
        // was actually accepted. A later cancellation cannot supply proof.
        if (state.LastControl is { } lastControl)
        {
            bool accepted = observation.ServerActorPositionSnapshotComplete &&
                float.IsFinite(observation.ServerActorX) && float.IsFinite(observation.ServerActorY) &&
                Near(observation.ServerActorX, observation.ServerActorY,
                    lastControl.X, lastControl.Y, restoreTolerance);
            state.LastControl = lastControl with { Accepted = lastControl.Accepted || accepted };
        }
        if (state.AwaitingAcceptedReturn is { } awaiting)
        {
            if (state.LastControl is { Accepted: true } acceptedReturn &&
                Near(acceptedReturn.X, acceptedReturn.Y,
                    awaiting.ReturnX, awaiting.ReturnY, restoreTolerance))
            {
                state.Completed.Enqueue(new(awaiting.TargetSlot, awaiting.TargetGeneration,
                    awaiting.TargetType, awaiting.Signature, awaiting.Tick,
                    awaiting.StartedTick, awaiting.TargetControlTick,
                    awaiting.ReturnX, awaiting.ReturnY, awaiting.DeathObserved));
                while (state.Completed.Count > CompletedSequenceCapacity)
                    state.Completed.Dequeue();
            }
            state.AwaitingAcceptedReturn = null;
        }

        if (state.Pending is { } pending)
        {
            bool restore = pending.ReturnPoint is { } returnPoint &&
                returnPoint.Accepted && state.LastControl is { Accepted: true } &&
                Near(observation.PositionX, observation.PositionY, returnPoint.X, returnPoint.Y, restoreTolerance);
            // A native death closes the target segment even when the client
            // restore is slightly displaced by server movement reconciliation.
            // This is only a completion marker; the later sanction path still
            // requires the closed native transaction and all account gates.
            if (restore)
            {
                // Completion is committed only when the next packet sees this
                // return as the accepted server position.
                state.AwaitingAcceptedReturn = new(pending.TargetSlot, pending.TargetGeneration,
                    pending.TargetType, pending.Signature, tick, pending.StartedTick,
                    pending.TargetControlTick,
                    observation.PositionX, observation.PositionY, pending.PostNativeDeathEvidence);
            }
            // A control declaration that is not the saved return position ends
            // this finite attempt; it must not be reinterpreted as a later hit.
            state.Pending = null;
        }

        state.PreviousControl = state.LastControl;
        state.LastControl = new(tick, observation.PositionX, observation.PositionY, false);
        state.LastTick = tick;
        return M18NpcStrikeBehaviorSnapshot.Empty;
    }

    public M18NpcStrikeBehaviorSnapshot ObserveStrike(long tick,
        M18NpcStrikeObservation observation)
    {
        if (!TryGetSessionSlot(observation.Session, out int slot) ||
            observation.Session.WorldEpoch != worldEpoch || tick < 0)
            return M18NpcStrikeBehaviorSnapshot.Empty;

        var state = GetState(slot, observation.Session, observation.AccountId);
        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            sessions[slot] = state = new(observation.Session, observation.AccountId);
            return M18NpcStrikeBehaviorSnapshot.Empty;
        }
        Expire(state, tick);
        state.LastTick = tick;

        var auxiliary = Auxiliary(observation);
        if (observation.AlreadyCancelled || observation.ReceiverDamage <= 0 ||
            observation.TargetFriendly || observation.TargetDummy ||
            !observation.TargetPositionSnapshotComplete ||
            !float.IsFinite(observation.TargetX) || !float.IsFinite(observation.TargetY) ||
            !observation.TargetActive || !observation.TargetSnapshotComplete)
            return Snapshot(state, auxiliary);

        var last = state.LastControl;
        if (last is not null)
        {
            bool accepted = observation.ActorPositionSnapshotComplete &&
                float.IsFinite(observation.ActorX) && float.IsFinite(observation.ActorY) &&
                Near(observation.ActorX, observation.ActorY, last.X, last.Y, restoreTolerance);
            last = last with { Accepted = last.Accepted || accepted };
            state.LastControl = last;
        }
        var previous = state.PreviousControl;
        bool preAtTarget = last is { Accepted: true } && Near(last.X, last.Y, observation.TargetX,
            observation.TargetY, positionTolerance);
        if (!preAtTarget)
            return Snapshot(state, auxiliary, positionContextComplete: last is not null,
                preControlAtTarget: false);

        // The locked Butcher producer moves the client to the target before
        // sending packet28. A normal close-range hit has no such declaration
        // jump; retain this as a separate fact so post-death proof cannot be
        // reduced to damage magnitude alone.
        // A control jump is player displacement between two declarations. The
        // old expression measured the previous declaration to the NPC, which
        // made an unchanged player at a target 70px away look like a jump.
        bool controlJump = previous is { Accepted: true } && last is { Accepted: true } &&
            Distance(previous.X, previous.Y, last.X, last.Y) > positionTolerance;

        var signature = new StrikeSignature(observation.WireDamage, observation.WireKnockback,
            observation.WireDirection, observation.WireCriticalFlag);
        if (state.Pending is { } pending && pending.TargetSlot == observation.TargetSlot &&
            pending.TargetGeneration == observation.TargetGeneration &&
            pending.TargetType == observation.TargetType)
        {
            if (pending.Blocked)
            {
                pending.StrikeCount = pending.StrikeCount == int.MaxValue ? int.MaxValue : pending.StrikeCount + 1;
                return Snapshot(state, auxiliary, true, true,
                    ReturnStable(state, pending.ReturnPoint), pending.StrikeCount >= 2, false, true,
                    pending.StrikeCount, controlJump);
            }
            bool sameSignature = pending.Signature == signature;
            if (sameSignature)
            {
                pending.StrikeCount = pending.StrikeCount == int.MaxValue ? int.MaxValue : pending.StrikeCount + 1;
                bool repeated = pending.StrikeCount >= 2 && pending.ReturnPoint is not null;
                return Snapshot(state, auxiliary, true, true,
                    ReturnStable(state, pending.ReturnPoint), repeated, false, false,
                    pending.StrikeCount, controlJump);
            }

            state.Pending = null;
        }

        var prior = state.Completed.LastOrDefault();
        bool returnStable = previous is { Accepted: true } && prior is not null &&
            Near(previous.X, previous.Y, prior.ReturnX, prior.ReturnY, restoreTolerance);
        bool crossTarget = prior is { } completed && completed.Signature == signature &&
            (completed.TargetSlot != observation.TargetSlot || completed.TargetGeneration != observation.TargetGeneration) &&
            returnStable;
        // The source-shaped burst completes both target controls, the first
        // hit, the return, and the next hit inside one server update. Ordinary
        // movement across multiple updates remains a diagnostic continuation.
        bool rapidCycle = crossTarget && prior is not null &&
            prior.TargetControlTick == tick && prior.StartedTick == tick && prior.Tick == tick &&
            previous is { Tick: var previousTick } && previousTick == tick &&
            last is { Tick: var lastTick } && lastTick == tick;
        state.Pending = new(observation.TargetSlot, observation.TargetGeneration, observation.TargetType, signature,
            tick, last!.Tick, previous, observation.TargetX, observation.TargetY,
            observation.TargetPositionSnapshotComplete, false);
        int matchingDeathSequences = crossTarget && prior is { DeathObserved: true } ? 1 : 0;
        // Repeated inputs and a stationary continuation are diagnostics. The
        // bounded stop-loss requires a completed cross-target segment and a
        // real accepted control displacement on the current target.
        return Snapshot(state, auxiliary, true, true, returnStable, false, crossTarget,
            rapidCycle && controlJump, 1, controlJump, matchingDeathSequences);
    }

    public void ObservePostNative(long tick, M18NpcStrikePostNativeObservation observation,
        Guid pendingSequenceId)
    {
        if (!TryGetSessionSlot(observation.Session, out int slot) ||
            observation.Session.WorldEpoch != worldEpoch || tick < 0 ||
            !observation.ObservedDeath || !observation.NativeStrikeEntryObserved ||
            !observation.RelayAttemptObserved || !observation.PositionContextComplete ||
            !observation.PreControlAtTarget || !observation.AttributionComplete ||
            !observation.LegalExceptionsExcluded)
            return;

        var state = sessions[slot];
        if (state is null || state.Session != observation.Session ||
             state.AccountId != observation.AccountId || state.Pending is not { } pending ||
             pendingSequenceId == Guid.Empty || pending.SequenceId != pendingSequenceId ||
            pending.TargetSlot != observation.TargetSlot ||
            pending.TargetGeneration != observation.TargetGeneration ||
            pending.TargetType != observation.TargetType)
            return;

        pending.PostNativeDeathEvidence = true;
    }

    private SessionState GetState(int slot, SessionKey session, long accountId)
    {
        var state = sessions[slot];
        if (state is null || state.Session != session || state.AccountId != accountId)
            sessions[slot] = state = new(session, accountId);
        return state!;
    }

    private static M18NpcStrikeBehaviorSnapshot Snapshot(SessionState state,
        M18NpcStrikeBehaviorSnapshot auxiliary, bool positionContextComplete = false,
        bool preControlAtTarget = false, bool returnStable = false,
        bool repeated = false, bool crossTarget = false, bool pattern = false,
        int currentStrikes = 0, bool controlJumpDetected = false,
        int matchingDeathSequences = 0)
        => auxiliary with
        {
            PositionContextComplete = positionContextComplete,
            PreControlAtTarget = preControlAtTarget,
            ControlJumpDetected = controlJumpDetected,
            ReturnPositionStable = returnStable,
            RepeatedAttackSignature = repeated,
            CrossTargetContinuation = crossTarget,
            PatternDetected = pattern,
            CompletedTargetSequences = state.Completed.Count,
            CompletedTargetDeathSequences = state.Completed.Count(entry => entry.DeathObserved),
            MatchingCompletedTargetDeathSequences = matchingDeathSequences,
            PendingSequenceId = positionContextComplete ? state.Pending?.SequenceId ?? Guid.Empty : Guid.Empty,
            RawCancelledControlAttemptObserved = state.LastRawControl?.CancelledAtObservation == true,
            CurrentTargetStrikes = currentStrikes,
        };

    private static M18NpcStrikeBehaviorSnapshot Auxiliary(M18NpcStrikeObservation observation)
        => new(
            PositionContextComplete: false,
            PreControlAtTarget: false,
            ControlJumpDetected: false,
            ReturnPositionStable: false,
            RepeatedAttackSignature: false,
            CrossTargetContinuation: false,
            PatternDetected: false,
            CompletedTargetSequences: 0,
            CompletedTargetDeathSequences: 0,
            CurrentTargetStrikes: 0,
            SummonContextComplete: observation.SummonContextComplete,
            SummonMaintenanceBuffObserved: observation.SummonMaintenanceBuffObserved,
            MatchingSummonEntityObserved: observation.MatchingSummonEntityObserved,
            MatchingSummonEntityCount: observation.MatchingSummonEntityCount);

    private bool ReturnStable(SessionState state, ControlPoint? returnPoint)
    {
        var prior = state.Completed.LastOrDefault();
        return prior is not null && returnPoint is not null &&
            Near(returnPoint.X, returnPoint.Y, prior.ReturnX, prior.ReturnY, restoreTolerance);
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
        if (state.LastRawControl is { } raw && tick - raw.Tick >= windowTicks)
            state.LastRawControl = null;
        while (state.Completed.Count > 0 && tick - state.Completed.Peek().Tick >= windowTicks)
            state.Completed.Dequeue();
        if (state.Pending is { } pending && tick - pending.StartedTick >= windowTicks)
            state.Pending = null;
        if (state.AwaitingAcceptedReturn is { } awaiting && tick - awaiting.Tick >= windowTicks)
            state.AwaitingAcceptedReturn = null;
    }

    private static bool Near(float x1, float y1, float x2, float y2, float tolerance)
        => MathF.Abs(x1 - x2) <= tolerance && MathF.Abs(y1 - y2) <= tolerance;

    private static float Distance(float x1, float y1, float x2, float y2)
        => MathF.Sqrt(MathF.Pow(x1 - x2, 2) + MathF.Pow(y1 - y2, 2));

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            session.Generation > 0 && slot >= 0 && slot < SessionSlotCapacity;
    }
}

/// <summary>
/// Fixed-size per-session queue. It has no sanction memory and never treats
/// high damage as an automatic block; the only blocks are a bounded positive-
/// damage resource stop-loss and the explicitly configured extreme-damage
/// candidate line after complete request attribution.
/// </summary>
public sealed class M18NpcStrikeQueue
{
    private const int SessionSlotCapacity = 256;
    private readonly M18NpcStrikeQueueOptions _options;
    private readonly SessionState?[] _sessions = new SessionState?[SessionSlotCapacity];
    private readonly WorldState _worldState;
    private readonly M18NpcStrikeBehaviorTracker _butcherBehavior;
    private long _worldEpoch = long.MinValue;

    public M18NpcStrikeQueue(M18NpcStrikeQueueOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _worldState = new WorldState(_options.HighSampleCapacity);
        _butcherBehavior = new M18NpcStrikeBehaviorTracker(_options);
    }

    public bool Enabled => _options.Enabled;
    public Action<M18NpcStrikeObservation, M18NpcStrikeQueueDecision>? ObservationRecorded { get; set; }

    public void AdvanceWorld(long worldEpoch)
    {
        if (_worldEpoch == worldEpoch)
            return;

        Array.Clear(_sessions, 0, _sessions.Length);
        _worldEpoch = worldEpoch;
        _worldState.Reset(worldEpoch);
        _butcherBehavior.AdvanceWorld(worldEpoch);
    }

    public void Reset()
    {
        Array.Clear(_sessions, 0, _sessions.Length);
        _worldEpoch = long.MinValue;
        _worldState.Reset(long.MinValue);
        _butcherBehavior.Reset();
    }

    public void Forget(SessionKey session)
    {
        if (!TryGetSessionSlot(session, out var slot))
            return;

        var state = _sessions[slot];
        if (state is not null && state.Session == session)
            _sessions[slot] = null;
        _butcherBehavior.Forget(session);
    }

    /// <summary>Feeds the exact decoded packet13 position declaration to the
    /// finite Butcher sequence tracker. This is context only and does not
    /// change the server's accepted player position.</summary>
    public void ObservePlayerControls(long tick, M18NpcPlayerControlObservation observation)
    {
        if (!_options.Enabled) return;
        _butcherBehavior.ObserveControl(tick, observation);
    }

    /// <summary>Ends an uncommitted position/strike attempt when another rule
    /// or this queue stopped the packet before the native receiver.</summary>
    public void CancelPendingBehavior(SessionKey session) => _butcherBehavior.Cancel(session);

    /// <summary>Keeps the remaining packet28 tail of a detected Butcher burst
    /// stopped until the source emits its packet13 restore.</summary>
    public void SuppressPendingBehavior(SessionKey session) => _butcherBehavior.Suppress(session);

    public M18NpcStrikeQueueDecision Observe(long tick, M18NpcStrikeObservation observation)
    {
        if (!_options.Enabled)
            return M18NpcStrikeQueueDecision.Disabled;

        if (tick < 0)
            return Incomplete(observation, "npc-strike-queue-clock-unavailable");

        if (!TryGetSessionSlot(observation.Session, out var sessionSlot))
            return Incomplete(observation, "npc-strike-queue-session-unavailable");

        if (observation.AlreadyCancelled)
            return Incomplete(observation, "npc-strike-already-cancelled");

        if (observation.AccountId <= 0 ||
            observation.TargetSlot < 0 || observation.TargetSlot >= SessionSlotCapacity ||
            observation.TargetGeneration <= 0 || observation.TargetType < 0 ||
            observation.WireDamage is < short.MinValue or > short.MaxValue ||
            observation.ReceiverDamage < 0 ||
            !observation.TargetSnapshotComplete ||
            !observation.TargetActive ||
            !observation.TargetGenerationMatchesCurrent ||
            !observation.ClientOrigin ||
            !observation.AttributionComplete ||
            !observation.LegalExceptionsExcluded)
        {
            return Incomplete(observation, "npc-strike-queue-attribution-or-snapshot-incomplete");
        }

        var state = _sessions[sessionSlot];
        if (state is null || state.Session != observation.Session || state.AccountId != observation.AccountId)
        {
            state = new SessionState(observation.Session, observation.AccountId, _options);
            _sessions[sessionSlot] = state;
        }

        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            _sessions[sessionSlot] = new SessionState(observation.Session, observation.AccountId, _options);
            return Incomplete(observation, "npc-strike-queue-clock-regressed");
        }

        state.Expire(tick, _options.WindowTicks);
        state.LastTick = tick;
        _worldState.Observe(observation);
        var behavior = _butcherBehavior.ObserveStrike(tick, observation);
        int stageThreshold = ExtremeDamageThresholdFor(observation);
        bool extremeGate = IsExtremeGateSatisfied(observation, stageThreshold);

        // A same-server-tick movement/return cycle is a useful observation,
        // but packet batching makes it insufficient even for a selective
        // movement-based block. Continue into the independent request-density
        // and extreme-damage stop-loss checks below.

        bool lowDamage = observation.ReceiverDamage <= _options.LowDamageMaximum;
        if (!lowDamage)
        {
            state.AddHighSample(tick, observation.TargetSlot, observation.TargetGeneration,
                observation.TargetType, observation.WireDamage, observation.ReceiverDamage);

            if (_options.EnableExtremeDamageStopLoss && extremeGate)
            {
                bool block = _options.EnablePreForwardBlocks;
                return Publish(observation, EnrichDecision(state.Decision(
                    Action: block ? ControlAction.Block : ControlAction.Unknown,
                    Verdict: block ? Verdict.UnsafeInput : Verdict.Unknown,
                    Reason: block ? "npc-strike-extreme-damage-preforward-stop" :
                        "npc-strike-extreme-damage-record-only-block-disabled",
                    counted: false,
                    lowDamage: false,
                    extremeDamage: true,
                    capacityExhausted: false,
                        targetSlot: observation.TargetSlot,
                    targetGeneration: observation.TargetGeneration), observation, stageThreshold, extremeGate, behavior));
            }
        }

        // Every positive non-extreme receiver damage is a finite work/request
        // unit. Previously only damage <= LowDamageMaximum reached this ring,
        // so an adjustable Butcher damage of 2/5/9 bypassed both target and
        // session density budgets while remaining record-only.
        if (observation.ReceiverDamage <= 0)
        {
            return Publish(observation, EnrichDecision(state.Decision(
                Action: ControlAction.Unknown,
                Verdict: Verdict.Unknown,
                Reason: "npc-strike-zero-damage-record-only",
                counted: false,
                lowDamage: false,
                extremeDamage: false,
                capacityExhausted: false,
                targetSlot: observation.TargetSlot,
                targetGeneration: observation.TargetGeneration), observation, stageThreshold, extremeGate, behavior));
        }

        if (state.LowCount >= _options.LowDamageRingCapacity)
        {
            bool block = _options.EnablePreForwardBlocks;
            return Publish(observation, EnrichDecision(state.Decision(
                Action: block ? ControlAction.Block : ControlAction.Unknown,
                Verdict: block ? Verdict.ResourceAbuse : Verdict.Unknown,
                Reason: block
                    ? lowDamage ? "npc-strike-low-damage-window-capacity-exhausted" :
                        "npc-strike-positive-damage-window-capacity-exhausted"
                    : lowDamage ? "npc-strike-low-damage-window-capacity-observed-block-disabled" :
                        "npc-strike-positive-damage-window-capacity-observed-block-disabled",
                counted: false,
                lowDamage: lowDamage,
                extremeDamage: false,
                capacityExhausted: true,
                targetSlot: observation.TargetSlot,
                targetGeneration: observation.TargetGeneration), observation, stageThreshold, extremeGate, behavior));
        }

        state.AddLowSample(tick, observation.TargetSlot, observation.TargetGeneration,
            observation.TargetType, observation.WireDamage, observation.ReceiverDamage);

        var targetCount = state.CountLowSamples(observation.TargetSlot, observation.TargetGeneration);
        var sessionCount = state.LowCount;
        var targetBudgetExhausted = targetCount >= _options.PerTargetLowDamageLimit;
        var sessionBudgetExhausted = sessionCount >= _options.PerSessionLowDamageLimit;
        var overBudget = targetBudgetExhausted || sessionBudgetExhausted;
        bool blockForBudget = overBudget && _options.EnablePreForwardBlocks;
        string budgetReason = sessionBudgetExhausted && !targetBudgetExhausted
            ? "npc-strike-positive-damage-session-budget-exhausted"
            : "npc-strike-positive-damage-window-budget-exhausted";
        string observedBudgetReason = sessionBudgetExhausted && !targetBudgetExhausted
            ? "npc-strike-positive-damage-session-budget-observed-block-disabled"
            : "npc-strike-positive-damage-window-budget-observed-block-disabled";

        return Publish(observation, EnrichDecision(state.Decision(
            Action: blockForBudget ? ControlAction.Block : ControlAction.Unknown,
            Verdict: blockForBudget ? Verdict.ResourceAbuse : Verdict.Unknown,
            Reason: overBudget
                ? blockForBudget ? (lowDamage ? budgetReason.Replace("positive-damage", "low-damage") : budgetReason) :
                    (lowDamage ? observedBudgetReason.Replace("positive-damage", "low-damage") : observedBudgetReason)
                : "npc-strike-queue-record-only",
            counted: true,
            lowDamage: lowDamage,
            extremeDamage: false,
            capacityExhausted: false,
            targetSlot: observation.TargetSlot,
            targetGeneration: observation.TargetGeneration), observation, stageThreshold, extremeGate, behavior));
    }

    /// <summary>Closes the post-native side of the Butcher path. Native death
    /// and overkill are retained as typed evidence, never account proof.</summary>
    public M18NpcStrikeQueueDecision ObservePostNative(long tick,
        M18NpcStrikePostNativeObservation observation,
        M18NpcStrikeQueueDecision initial)
    {
        _butcherBehavior.ObservePostNative(tick, observation, initial.ButcherPendingSequenceId);
        if (!_options.Enabled || !initial.Enabled || tick < 0 ||
            !TryGetSessionSlot(observation.Session, out _) ||
            observation.AccountId <= 0 ||
            observation.TargetSlot < 0 || observation.TargetSlot >= SessionSlotCapacity ||
            observation.TargetGeneration <= 0 || observation.TargetType <= 0 ||
            observation.ReceiverDamage <= 0 || observation.LifeBefore <= 0 ||
            observation.TargetLifeMax <= 0 || observation.TargetFriendly || observation.TargetDummy ||
            !observation.StageSnapshotComplete || !observation.ClientOrigin ||
            !observation.AttributionComplete || !observation.LegalExceptionsExcluded ||
            !observation.NativeStrikeEntryObserved || !observation.RelayAttemptObserved ||
            !observation.PositionContextComplete || !observation.PreControlAtTarget ||
            observation.LifeAfter > 0)
            return initial;

        bool oneShotDeathEvidence = observation.ObservedDeath &&
            observation.OverkillBeyondTargetMaximum;
        return initial with
        {
            PostNativeOneShotDeathEvidence = oneShotDeathEvidence,
            PermanentSanctionCandidate = false,
            ButcherSequenceSanctionCandidate = false,
            PostNativeSanctionCandidate = false,
        };
    }

    private M18NpcStrikeQueueDecision Publish(M18NpcStrikeObservation observation,
        M18NpcStrikeQueueDecision decision)
    {
        try { ObservationRecorded?.Invoke(observation, decision); }
        catch { /* Durable observation is optional and cannot affect admission. */ }
        return decision;
    }

    private int ExtremeDamageThresholdFor(M18NpcStrikeObservation observation)
        => !observation.StageSnapshotComplete ? 0 : observation.Stage switch
        {
            M18NpcStrikeStage.PreHardmode => _options.ExtremeDamageThreshold,
            M18NpcStrikeStage.Hardmode => _options.HardmodeExtremeDamageThreshold,
            M18NpcStrikeStage.PostPlantera => _options.PostPlanteraExtremeDamageThreshold,
            M18NpcStrikeStage.PostMoonlord => _options.PostMoonlordExtremeDamageThreshold,
            _ => 0,
        };

    private static bool IsExtremeGateSatisfied(M18NpcStrikeObservation observation, int threshold)
        => threshold > 0 &&
           observation.ReceiverDamage >= threshold &&
           observation.TargetLife > 0 &&
           observation.TargetLifeMax > 0 &&
           !observation.TargetFriendly &&
           !observation.TargetDummy;

    private M18NpcStrikeQueueDecision EnrichDecision(
        M18NpcStrikeQueueDecision decision,
        M18NpcStrikeObservation observation,
        int stageThreshold,
        bool extremeGate,
        M18NpcStrikeBehaviorSnapshot behavior = default)
        => decision with
        {
            Stage = observation.Stage,
            StageSnapshotComplete = observation.StageSnapshotComplete,
            WorldId = observation.WorldId,
            TargetLife = observation.TargetLife,
            TargetLifeMax = observation.TargetLifeMax,
            TargetMaxLifeExceeded = observation.TargetLifeMax > 0 &&
                observation.ReceiverDamage > observation.TargetLifeMax,
            StageExtremeDamageThreshold = stageThreshold,
            ExtremeGateSatisfied = extremeGate,
            // The stage threshold is a connection stop-loss only.  It is not
            // promoted to a permanent sanction candidate without the active
            // sequence proof above.
            PermanentSanctionCandidate = decision.PermanentSanctionCandidate,
            ButcherPatternDetected = behavior.PatternDetected,
            ButcherPositionContextComplete = behavior.PositionContextComplete,
            ButcherPreControlAtTarget = behavior.PreControlAtTarget,
            ButcherControlJumpDetected = behavior.ControlJumpDetected,
            ButcherReturnPositionStable = behavior.ReturnPositionStable,
            ButcherRepeatedAttackSignature = behavior.RepeatedAttackSignature,
            ButcherCrossTargetContinuation = behavior.CrossTargetContinuation,
            ButcherCompletedTargetSequences = behavior.CompletedTargetSequences,
            ButcherCompletedTargetDeathSequences = behavior.CompletedTargetDeathSequences,
            ButcherMatchingTargetDeathSequences = behavior.MatchingCompletedTargetDeathSequences,
            ButcherPendingSequenceId = behavior.PendingSequenceId,
            ButcherRawCancelledControlAttemptObserved = behavior.RawCancelledControlAttemptObserved,
            ButcherCurrentTargetStrikes = behavior.CurrentTargetStrikes,
            ButcherSequenceSanctionCandidate = decision.ButcherSequenceSanctionCandidate,
            SummonContextComplete = behavior.SummonContextComplete,
            SummonMaintenanceBuffObserved = behavior.SummonMaintenanceBuffObserved,
            MatchingSummonEntityObserved = behavior.MatchingSummonEntityObserved,
            MatchingSummonEntityCount = behavior.MatchingSummonEntityCount,
            WorldHighMaxDamage = _worldState.WorldHighMaxDamage,
            StageHighMaxDamage = _worldState.StageHighMaxDamage,
            WorldHighSamplesRetained = _worldState.HighSamplesRetained,
            WorldHighSamplesDropped = _worldState.HighSamplesDropped,
        };

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty &&
               session.WorldEpoch > 0 &&
               slot >= 0 &&
               slot < SessionSlotCapacity &&
               session.Generation > 0;
    }

    private static M18NpcStrikeQueueDecision Incomplete(
        M18NpcStrikeObservation observation, string reason)
        => new(
            Enabled: true,
            Action: ControlAction.Unknown,
            Verdict: Verdict.Unknown,
            Reason: reason,
            Counted: false,
            LowDamage: false,
            ExtremeDamage: false,
            CapacityExhausted: false,
            TargetLowDamageCount: 0,
            SessionLowDamageCount: 0,
            LowSamplesRetained: 0,
            HighSamplesRetained: 0,
            HighSamplesDropped: 0,
            MaxWireDamage: 0,
            MaxTargetType: 0)
        {
            Stage = observation.Stage,
            StageSnapshotComplete = observation.StageSnapshotComplete,
            WorldId = observation.WorldId,
            TargetLife = observation.TargetLife,
            TargetLifeMax = observation.TargetLifeMax,
        };

    private sealed class SessionState
    {
        private readonly LowSample[] _lowSamples;
        private readonly HighSample[] _highSamples;
        private int _lowStart;
        private int _highStart;
        private int _highCount;
        private int _highSamplesDropped;

        public SessionState(SessionKey session, long accountId, M18NpcStrikeQueueOptions options)
        {
            Session = session;
            AccountId = accountId;
            _lowSamples = new LowSample[options.LowDamageRingCapacity];
            _highSamples = new HighSample[options.HighSampleCapacity];
            LastTick = long.MinValue;
        }

        public SessionKey Session { get; }
        public long AccountId { get; }
        public long LastTick { get; set; }
        public int LowCount { get; private set; }

        public void Expire(long tick, int windowTicks)
        {
            while (LowCount > 0 && tick - _lowSamples[_lowStart].Tick >= windowTicks)
            {
                _lowStart = (_lowStart + 1) % _lowSamples.Length;
                LowCount--;
            }

            while (_highCount > 0 && tick - _highSamples[_highStart].Tick >= windowTicks)
            {
                _highStart = (_highStart + 1) % _highSamples.Length;
                _highCount--;
            }
        }

        public void AddLowSample(long tick, int targetSlot, int targetGeneration,
            int targetType, int wireDamage, int receiverDamage)
        {
            var index = (_lowStart + LowCount) % _lowSamples.Length;
            _lowSamples[index] = new LowSample(tick, targetSlot, targetGeneration,
                targetType, wireDamage, receiverDamage);
             LowCount++;
        }

        public void AddHighSample(long tick, int targetSlot, int targetGeneration,
            int targetType, int wireDamage, int receiverDamage)
        {
            if (_highCount < _highSamples.Length)
            {
                var index = (_highStart + _highCount) % _highSamples.Length;
                _highSamples[index] = new HighSample(tick, targetSlot, targetGeneration,
                    targetType, wireDamage, receiverDamage);
                _highCount++;
                return;
            }

            _highSamples[_highStart] = new HighSample(tick, targetSlot, targetGeneration,
                targetType, wireDamage, receiverDamage);
            _highStart = (_highStart + 1) % _highSamples.Length;
            _highSamplesDropped = SaturatingIncrement(_highSamplesDropped);
        }

        public int CountLowSamples(int targetSlot, int targetGeneration)
        {
            var count = 0;
            for (var i = 0; i < LowCount; i++)
            {
                var sample = _lowSamples[(_lowStart + i) % _lowSamples.Length];
                if (sample.TargetSlot == targetSlot && sample.TargetGeneration == targetGeneration)
                    count++;
            }

            return count;
        }

        public M18NpcStrikeQueueDecision Decision(
            ControlAction Action,
            Verdict Verdict,
            string Reason,
        bool counted,
        bool lowDamage,
        bool extremeDamage,
        bool capacityExhausted,
            int targetSlot,
            int targetGeneration)
        {
            var targetCount = CountLowSamples(targetSlot, targetGeneration);
            var maxDamage = 0;
            var maxTargetType = 0;
            for (var i = 0; i < _highCount; i++)
            {
                var sample = _highSamples[(_highStart + i) % _highSamples.Length];
                if (sample.ReceiverDamage > maxDamage)
                {
                    maxDamage = sample.ReceiverDamage;
                    maxTargetType = sample.TargetType;
                }
            }

            return new M18NpcStrikeQueueDecision(
                Enabled: true,
                Action: Action,
                Verdict: Verdict,
                Reason: Reason,
            Counted: counted,
            LowDamage: lowDamage,
            ExtremeDamage: extremeDamage,
            CapacityExhausted: capacityExhausted,
                TargetLowDamageCount: targetCount,
                SessionLowDamageCount: LowCount,
                LowSamplesRetained: LowCount,
                HighSamplesRetained: _highCount,
                HighSamplesDropped: _highSamplesDropped,
                MaxWireDamage: maxDamage,
                MaxTargetType: maxTargetType);
        }

        private static int SaturatingIncrement(int value)
            => value == int.MaxValue ? value : value + 1;

        private readonly record struct LowSample(
            long Tick,
            int TargetSlot,
            int TargetGeneration,
            int TargetType,
            int WireDamage,
            int ReceiverDamage);

        private readonly record struct HighSample(
            long Tick,
            int TargetSlot,
            int TargetGeneration,
            int TargetType,
            int WireDamage,
            int ReceiverDamage);
    }

    /// <summary>
    /// Finite world-level projection of high-damage observations. It keeps no
    /// account history beyond the bounded ring and per-stage maxima; it is a
    /// staging summary, not a cross-session proof or a sanction cache.
    /// </summary>
    private sealed class WorldState
    {
        private readonly HighSample[] _samples;
        private readonly int[] _stageMaxDamage = new int[Enum.GetValues<M18NpcStrikeStage>().Length];
        private int _start;
        private int _count;
        private int _dropped;

        public WorldState(int capacity) => _samples = new HighSample[capacity];

        public long WorldEpoch { get; private set; } = long.MinValue;
        public int WorldId { get; private set; }
        public M18NpcStrikeStage CurrentStage { get; private set; } = M18NpcStrikeStage.Unknown;
        public int WorldHighMaxDamage { get; private set; }
        public int HighSamplesRetained => _count;
        public int HighSamplesDropped => _dropped;
        public int StageHighMaxDamage => GetStageMax(CurrentStage);

        public void Reset(long worldEpoch, int worldId = 0)
        {
            Array.Clear(_samples, 0, _samples.Length);
            Array.Clear(_stageMaxDamage, 0, _stageMaxDamage.Length);
            _start = 0;
            _count = 0;
            _dropped = 0;
            WorldEpoch = worldEpoch;
            WorldId = worldId;
            CurrentStage = M18NpcStrikeStage.Unknown;
            WorldHighMaxDamage = 0;
        }

        public void Observe(M18NpcStrikeObservation observation)
        {
            if (WorldEpoch != observation.Session.WorldEpoch ||
                WorldId != 0 && observation.WorldId != 0 && WorldId != observation.WorldId)
                Reset(observation.Session.WorldEpoch, observation.WorldId);

            if (WorldId == 0 && observation.WorldId != 0)
                WorldId = observation.WorldId;
            CurrentStage = observation.Stage;
            if (observation.ReceiverDamage <= 1 || !observation.StageSnapshotComplete)
                return;

            WorldHighMaxDamage = Math.Max(WorldHighMaxDamage, observation.ReceiverDamage);
            int stageIndex = StageIndex(observation.Stage);
            if (stageIndex >= 0)
                _stageMaxDamage[stageIndex] = Math.Max(_stageMaxDamage[stageIndex], observation.ReceiverDamage);

            var sample = new HighSample(observation.Session, observation.AccountId,
                observation.TargetSlot, observation.TargetGeneration, observation.TargetType,
                observation.WireDamage, observation.ReceiverDamage, observation.Stage);
            if (_count < _samples.Length)
            {
                _samples[(_start + _count) % _samples.Length] = sample;
                _count++;
            }
            else
            {
                _samples[_start] = sample;
                _start = (_start + 1) % _samples.Length;
                _dropped = SaturatingIncrement(_dropped);
            }
        }

        private int GetStageMax(M18NpcStrikeStage stage)
        {
            int index = StageIndex(stage);
            return index < 0 ? 0 : _stageMaxDamage[index];
        }

        private static int StageIndex(M18NpcStrikeStage stage)
            => Enum.IsDefined(stage) ? (int)stage : -1;

        private static int SaturatingIncrement(int value)
            => value == int.MaxValue ? value : value + 1;

        private readonly record struct HighSample(
            SessionKey Session,
            long AccountId,
            int TargetSlot,
            int TargetGeneration,
            int TargetType,
            int WireDamage,
            int ReceiverDamage,
            M18NpcStrikeStage Stage);
    }
}

public static class M18NpcStrikeQueueRules
{
    public const string RuleId = "F06.NpcStrikeDensityBudget";
    public const string Version = "2.10.0";
    public const string ContractVersion = "terraria1.4.5.8-326-npc-strike-sequence-observe-capacity-stoploss-v12";

    // AntiCheatEngine accepts at most 24 facts and the plugin appends one
    // cancellation fact before admission. Keep this projection explicit so
    // the candidate remains observable instead of being downgraded by the
    // core evidence-size guard when the upstream M6/M7 context is present.
    private static readonly string[] CandidateFactKeys =
    [
        "npcSlot", "npcGeneration", "currentGeneration", "damage", "knockback",
        "encodedDirection", "criticalFlag", "mechanismVersion", "damageSourceCompleteness",
        "targetAtRequest", "targetFactProvenance", "actorPositionProvenance",
        "clientAllowedDamageResults", "exclusiveAttackCauseAvailable", "recentServerNativeCause",
        "nativeCauseIsClientAuthorization", "strikeLegalExceptionGate",
        "strikeExtremeLineProof",
        "priorClientStrikeCompletion"
    ];

    private static readonly string[] QueueFactKeys =
    [
        "strikeQueue", "strikeQueueSamples", "strikeQueueClassification", "strikeQueueActionContract",
        "postNativeEvidence"
    ];

    /// <summary>
    /// Combines the bounded queue observation with the already authoritative
    /// packet-28 structural result. The queue is an additional candidate
    /// observer: it may add a stop-loss to an Unknown structural result, but
    /// it must never downgrade an existing Pass/Block or replace its reason.
    /// </summary>
    public static BusinessRuleResult MergeWithPrior(
        BusinessRuleResult prior,
        BusinessRuleResult queueResult)
    {
        var facts = MergeQueueFacts(prior.Facts, queueResult.Facts);
        return prior.Action == ControlAction.Unknown
            ? queueResult with { Facts = facts }
            : prior with { Facts = facts };
    }

    private static ImmutableDictionary<string, string> MergeQueueFacts(
        ImmutableDictionary<string, string> prior,
        ImmutableDictionary<string, string> queue)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in prior)
        {
            // ApplyBusiness appends alreadyCancelledBeforeRule before the
            // core's 24-fact validation.
            if (builder.Count >= 23) break;
            builder[pair.Key] = pair.Value;
        }

        foreach (var key in QueueFactKeys)
        {
            if (!queue.TryGetValue(key, out var value)) continue;
            if (builder.ContainsKey(key) || builder.Count < 23)
                builder[key] = value;
        }

        return builder.ToImmutable();
    }

    public static BusinessRuleResult Observe(
        RuleInputContext input,
        ImmutableDictionary<string, string> facts,
        M18NpcStrikeQueueDecision decision)
    {
        var action = decision.Action;
        var verdict = decision.Verdict == Verdict.ProvenCheat
            ? action == ControlAction.Block ? Verdict.UnsafeInput : Verdict.Unknown
            : decision.Verdict;
        var boundedFacts = BoundedContextFacts(facts)
            .SetItem("strikeQueue", BoundedFactValue(FormatSummary(decision)))
            .SetItem("strikeQueueSamples", BoundedFactValue(FormatSamples(decision)))
            .SetItem("strikeQueueClassification", decision.IsResourceBlock ?
                "positive-damage-resource-stop" :
                decision.ExtremeDamage ? "extreme-damage-preforward-stop" :
                decision.ButcherPatternDetected ? "ordinary-butcher-sequence-observed" :
                decision.PostNativeOneShotDeathEvidence ? "butcher-one-shot-death-post-native" :
                decision.LowDamage ? "low-damage-window" : "high-damage-record")
            .SetItem("strikeQueueActionContract", decision.IsButcherPatternBlock ?
                "block-before-native-receiver-no-sanction" :
                decision.IsExtremeDamageBlock ? "block-before-native-receiver-no-sanction" :
                decision.IsResourceBlock ? "resource-block-before-native-receiver-no-sanction" : "record-only");

        return new BusinessRuleResult(
            RuleId,
            Version,
            action,
            verdict,
            decision.Reason,
            PredicateSatisfied: false,
            PrerequisitesComplete: false,
            boundedFacts);
    }

    public static BusinessRuleResult PostNativeObserve(
        RuleInputContext input,
        M18NpcStrikePostNativeObservation observation,
        M18NpcStrikeQueueDecision decision)
    {
        var facts = ImmutableDictionary<string, string>.Empty
            .Add("npcSlot", observation.TargetSlot.ToString(CultureInfo.InvariantCulture))
            .Add("npcGeneration", observation.TargetGeneration.ToString(CultureInfo.InvariantCulture))
            .Add("damage", observation.ReceiverDamage.ToString(CultureInfo.InvariantCulture))
            .Add("targetAtRequest", string.Create(CultureInfo.InvariantCulture,
                $"type={observation.TargetType};life={observation.LifeBefore}->{observation.LifeAfter};max={observation.TargetLifeMax}"))
            .Add("targetFactProvenance", "post-native-NPC-reference-and-generation")
            .Add("actorPositionProvenance", "packet13-target-control-context")
            .Add("clientAllowedDamageResults", string.Create(CultureInfo.InvariantCulture,
                $"wire={observation.WireDamage};receiver={observation.ReceiverDamage};nativeCauseIsAuthorization=False"))
            .Add("exclusiveAttackCauseAvailable", "False")
            .Add("recentServerNativeCause", "client-originated-strike-relay")
            .Add("nativeCauseIsClientAuthorization", "False")
            .Add("strikeLegalExceptionGate", observation.LegalExceptionsExcluded ?
                "closed;source=M7NpcStrikeCauseContexts:TargetDummy-or-anticheat.bypass-or-bypassssc" :
                "open;source=M7NpcStrikeCauseContexts:TargetDummy-or-anticheat.bypass-or-bypassssc")
            .Add("strikeExtremeLineProof", "post-native-one-shot-death-plus-active-butcher-sequence")
            .Add("postNativeEvidence", string.Create(CultureInfo.InvariantCulture,
                $"native={observation.NativeStrikeEntryObserved};relay={observation.RelayAttemptObserved};" +
                $"death={observation.ObservedDeath};overkill={observation.OverkillBeyondTargetMaximum};" +
                $"position={observation.PositionContextComplete}/{observation.PreControlAtTarget}/{observation.ControlJumpDetected}"));
        var boundedFacts = BoundedContextFacts(facts)
            .SetItem("postNativeEvidence", BoundedFactValue(facts["postNativeEvidence"]))
            .SetItem("strikeQueue", BoundedFactValue(FormatSummary(decision)))
            .SetItem("strikeQueueSamples", BoundedFactValue(FormatSamples(decision)))
            .SetItem("strikeQueueClassification", "post-native-completion-observed")
            .SetItem("strikeQueueActionContract", "post-native-observation-no-sanction");

        return new BusinessRuleResult(
            RuleId,
            Version,
            ControlAction.Unknown,
            Verdict.Unknown,
            "npc-strike-post-native-completion-observed",
            PredicateSatisfied: false,
            PrerequisitesComplete: false,
            boundedFacts);
    }

    private static ImmutableDictionary<string, string> BoundedContextFacts(
        ImmutableDictionary<string, string> facts)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var key in CandidateFactKeys)
            if (facts.TryGetValue(key, out var value) && value.Length <= 256)
                builder[key] = value;
        return builder.ToImmutable();
    }

    private static string BoundedFactValue(string value)
        => value.Length <= 256 ? value : value[..256];

    public static BusinessRuleResult ResourceBlock(
        RuleInputContext input,
        ImmutableDictionary<string, string> facts,
        M18NpcStrikeQueueDecision decision)
    {
        if (!decision.IsResourceBlock)
            throw new ArgumentException("The decision is not a resource block.", nameof(decision));

        var boundedFacts = facts
            .SetItem("strikeQueue", BoundedFactValue(FormatSummary(decision)))
            .SetItem("strikeQueueSamples", BoundedFactValue(FormatSamples(decision)));

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

    public static ImmutableDictionary<string, string> AddFacts(
        ImmutableDictionary<string, string> facts,
        M18NpcStrikeQueueDecision decision)
        => facts
            .SetItem("strikeQueue", BoundedFactValue(FormatSummary(decision)))
            .SetItem("strikeQueueSamples", BoundedFactValue(FormatSamples(decision)));

    private static string FormatSummary(M18NpcStrikeQueueDecision decision)
        // Business facts are capped at 256 characters by AntiCheatEngine. Keep
        // this compact projection under that boundary; the journal retains the
        // same fields as typed properties, so this is not the sole evidence.
        => string.Create(
            CultureInfo.InvariantCulture,
            $"reason={decision.Reason};stage={decision.Stage};world={decision.WorldId};" +
            $"life={decision.TargetLife}/{decision.TargetLifeMax};d={decision.MaxWireDamage};type={decision.MaxTargetType};" +
            $"target={decision.TargetLowDamageCount};session={decision.SessionLowDamageCount};" +
            $"butcher={decision.ButcherPatternDetected}/{decision.ButcherPositionContextComplete}/" +
             $"{decision.ButcherPreControlAtTarget}/{decision.ButcherControlJumpDetected}/" +
             $"{decision.ButcherReturnPositionStable}/" +
              $"{decision.ButcherRepeatedAttackSignature}/{decision.ButcherCrossTargetContinuation};" +
              $"segments={decision.ButcherCompletedTargetSequences};deaths={decision.ButcherCompletedTargetDeathSequences};" +
              $"matchingDeaths={decision.ButcherMatchingTargetDeathSequences};" +
              $"current={decision.ButcherCurrentTargetStrikes};" +
             $"summon={decision.SummonContextComplete}/{decision.SummonMaintenanceBuffObserved}/" +
             $"{decision.MatchingSummonEntityObserved}/{decision.MatchingSummonEntityCount};" +
             $"postNative={decision.PostNativeOneShotDeathEvidence};sanction={decision.PermanentSanctionCandidate}");

    private static string FormatSamples(M18NpcStrikeQueueDecision decision)
        => string.Create(
            CultureInfo.InvariantCulture,
             $"low={decision.LowSamplesRetained};high={decision.HighSamplesRetained};dropped={decision.HighSamplesDropped};" +
             $"worldHigh={decision.WorldHighSamplesRetained};worldDropped={decision.WorldHighSamplesDropped};capacity={decision.CapacityExhausted};" +
             $"behaviorContract=pre-target-control-jump-repeat-or-cross-target-restore;" +
             $"sanction={decision.PermanentSanctionCandidate}");
}
