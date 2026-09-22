using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// TestLab-only bounded observation of the user's narrow module-1 pattern:
/// server output of accepted one-point PVE damage, followed by the same
/// authenticated session's full-life SSC export. It has no sanction memory.
/// </summary>
public sealed record M18LockHealthOptions
{
    public bool Enabled { get; init; }
    public int WindowTicks { get; init; } = 120;
    public int ResponseWindowTicks { get; init; } = 12;
    public int RequiredPairs { get; init; } = 3;
    public int PendingCapacity { get; init; } = 32;
    public bool EnableServiceBlock { get; init; } = true;
    public bool EnableServiceKick { get; init; } = true;

    public static M18LockHealthOptions Disabled => new();

    public static M18LockHealthOptions TestLabCandidate => new()
    {
        Enabled = true,
    };

    public static M18LockHealthOptions ProductionCandidate => new()
    {
        Enabled = true,
        EnableServiceKick = false,
    };

    public static M18LockHealthOptions ForExecutionScope(
        ExecutionScope scope,
        M18CandidateMode mode = M18CandidateMode.Auto,
        bool recordObservations = true,
        bool enableBlocks = true,
        bool? enableServiceKick = null)
    {
        if (!recordObservations || !M18ExecutionModePolicy.RecordEnabled(scope, mode))
            return Disabled;

        bool controls = M18ExecutionModePolicy.CandidateControlsEnabled(scope, mode);
        var candidate = mode == M18CandidateMode.ProductionCandidate ? ProductionCandidate : TestLabCandidate;
        return candidate with
        {
            EnableServiceBlock = enableBlocks && controls,
            EnableServiceKick = controls && (enableServiceKick ?? candidate.EnableServiceKick),
        };
    }

    public void Validate()
    {
        if (WindowTicks is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(WindowTicks));
        if (ResponseWindowTicks is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(ResponseWindowTicks));
        if (RequiredPairs is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(RequiredPairs));
        if (PendingCapacity is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(PendingCapacity));
    }
}

public sealed record M18LockHealthDecision(
    bool Enabled,
    bool PairMatched,
    bool Kick,
    string Reason,
    int PairCount,
    int RequiredPairs,
    int PendingHurtCount,
    int DroppedPendingHurtCount,
    int LastPairGapTicks,
    BusinessRuleResult? RuleResult)
{
    public static M18LockHealthDecision Disabled => new(
        Enabled: false,
        PairMatched: false,
        Kick: false,
        Reason: "lock-health-service-rule-disabled",
        PairCount: 0,
        RequiredPairs: 0,
        PendingHurtCount: 0,
        DroppedPendingHurtCount: 0,
        LastPairGapTicks: 0,
        RuleResult: null);
}

public sealed class M18LockHealthContext : IDisposable
{
    private const int SessionCapacity = 256;

    private readonly TimeProvider clock;
    private readonly string fingerprint;
    private readonly M18LockHealthOptions options;
    private readonly State?[] states = new State?[SessionCapacity];
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? lookup;
    private bool installed;
    private bool failed;
    private long worldEpoch = long.MinValue;
    private long tick = long.MinValue;
    private int worldId;
    private int updateThread;

    public M18LockHealthContext(TimeProvider clock, string fingerprint, M18LockHealthOptions options)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
    }

    public bool Enabled => options.Enabled;
    public bool Healthy => installed && !failed;
    public long CurrentTick => tick;
    public Action<Exception>? IntegrityFault { get; set; }

    public void Install(Func<int, (SessionSnapshot? Session, TSPlayer? Player)> resolve)
    {
        if (!options.Enabled || installed || failed)
            return;

        Safe(() =>
        {
            if (fingerprint != TargetRuntime.Fingerprint)
                throw new InvalidOperationException("Lock-health candidate requires the audited protocol326 runtime.");
            lookup = resolve ?? throw new ArgumentNullException(nameof(resolve));
            HookEvents.Terraria.NetMessage.SendPlayerHurt += OnSendPlayerHurt;
            installed = true;
        });
    }

    public void Tick(long nextWorldEpoch)
    {
        if (!options.Enabled || failed || !installed || Main.netMode != 2 || nextWorldEpoch <= 0)
            return;

        Safe(() =>
        {
            if (updateThread != 0 && updateThread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Lock-health candidate update context changed.");
            updateThread = Environment.CurrentManagedThreadId;
            if (worldEpoch != nextWorldEpoch || worldId != Main.worldID)
            {
                Array.Clear(states);
                worldEpoch = nextWorldEpoch;
                worldId = Main.worldID;
            }

            tick = tick == long.MaxValue ? tick : tick == long.MinValue ? 1 : tick + 1;
            for (int slot = 0; slot < states.Length; slot++)
            {
                if (states[slot] is not { } state)
                    continue;
                if (!CurrentBinding(state.Session, state.AccountId, state.Actor))
                {
                    states[slot] = null;
                    continue;
                }
                Expire(state);
            }
        });
    }

    public M18LockHealthDecision ObserveLifeSync(
        M4VitalObservation observation,
        SessionKey session,
        TSPlayer actor,
        bool alreadyCancelled)
    {
        if (!options.Enabled)
            return M18LockHealthDecision.Disabled;

        M18LockHealthDecision result = M18LockHealthDecision.Disabled;
        Safe(() => result = ObserveLifeSyncCore(observation, session, actor, alreadyCancelled));
        return result;
    }

    public void Forget(SessionKey session)
    {
        if (!TrySessionSlot(session, out int slot))
            return;
        if (states[slot] is { } state && state.Session == session)
            states[slot] = null;
    }

    public void ResetWorld()
    {
        Array.Clear(states);
        worldEpoch = long.MinValue;
        tick = long.MinValue;
    }

    private void OnSendPlayerHurt(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
    {
        if (!options.Enabled || failed || !installed || !args.ContinueExecution || Main.netMode != 2)
            return;

        Safe(() =>
        {
            if (!CurrentThread() || args.damage != 1 || args.pvp || !AddressedTo(args.playerTargetIndex, args.remoteClient, args.ignoreClient))
                return;
            int slot = args.playerTargetIndex;
            if (!TryCurrentTarget(slot, out var session, out var actor) ||
                actor is null || session is null || !actor.IsLoggedIn || actor.Account is null ||
                actor.Account.ID <= 0 || actor.HasPermission(Permissions.ignorehp) ||
                actor.IgnoreSSCPackets || !actor.HasSentInventory || actor.TPlayer.dead || actor.TPlayer.ghost)
                return;

            int maximum = actor.TPlayer.statLifeMax2;
            int current = actor.TPlayer.statLife;
            if (maximum <= 0 || maximum > short.MaxValue || current < 0 || current >= maximum)
                return;

            var state = GetState(session.Key, actor);
            Expire(state);
            AddPending(state, new(tick, current, maximum));
        });
    }

    private M18LockHealthDecision ObserveLifeSyncCore(
        M4VitalObservation observation,
        SessionKey session,
        TSPlayer actor,
        bool alreadyCancelled)
    {
        if (!CurrentThread() || alreadyCancelled || observation.Kind != M4VitalKind.Life ||
            !Main.ServerSideCharacter || !actor.IsLoggedIn || actor.Account is null ||
            actor.Account.ID <= 0 || actor.HasPermission(Permissions.ignorehp) ||
            actor.IgnoreSSCPackets || !actor.HasSentInventory || actor.TPlayer.dead || actor.TPlayer.ghost ||
            observation.ClaimedSlot != session.Slot || observation.Current <= 0 ||
            observation.RawMaximum <= 0 || observation.Current != observation.RawMaximum ||
            !TryCurrentTarget(session.Slot, out var currentSession, out var currentActor) ||
            currentSession is null || currentActor is null || currentSession.Key != session ||
            !ReferenceEquals(currentActor, actor))
            return NoDecision("lock-health-life-sync-context-not-eligible");

        var state = GetState(session, actor);
        Expire(state);
        int pendingIndex = FindPending(state);
        if (pendingIndex < 0)
            return NoDecision("lock-health-no-matching-one-damage-output");

        var pending = state.Pending[pendingIndex];
        RemovePendingAt(state, pendingIndex);
        state.PendingConsumed++;
        int gap = checked((int)(tick - pending.Tick));
        if (state.LastPairTick == long.MinValue || tick - state.LastPairTick > options.WindowTicks)
            state.PairCount = 1;
        else
            state.PairCount = Increment(state.PairCount);
        state.LastPairTick = tick;

        bool threshold = !state.Triggered && state.PairCount >= options.RequiredPairs;
        if (!threshold)
            return new(
                Enabled: true,
                PairMatched: true,
                Kick: false,
                Reason: "lock-health-one-damage-full-life-pair-observed",
                PairCount: state.PairCount,
                RequiredPairs: options.RequiredPairs,
                PendingHurtCount: state.PendingCount,
                DroppedPendingHurtCount: state.DroppedPending,
                LastPairGapTicks: gap,
                RuleResult: null);

        state.Triggered = true;
        var rule = M18LockHealthRules.Evaluate(new M18LockHealthObservation(
            PairCount: state.PairCount,
            RequiredPairs: options.RequiredPairs,
            LastPairGapTicks: gap,
            WindowTicks: options.WindowTicks,
            ResponseWindowTicks: options.ResponseWindowTicks,
            PendingHurtCount: state.PendingCount,
            DamageExactlyOne: true,
            ServerOutputAttributed: true,
            ServerLifeWasReduced: pending.LifeAfterHurt < pending.LifeMaximum,
            FullLifeSync: true,
            ServerSideCharacter: Main.ServerSideCharacter,
            ActiveGameplay: !actor.TPlayer.dead && !actor.TPlayer.ghost,
            SynchronizationExcluded: actor.IsLoggedIn && actor.HasSentInventory && !actor.IgnoreSSCPackets,
            ScopedPermissionExcluded: !actor.HasPermission(Permissions.ignorehp),
            SessionComplete: currentSession.Key == session,
            ClientOrigin: true,
            BeforeCoreHandler: true));
        if (!options.EnableServiceBlock)
        {
            rule = rule with
            {
                Action = ControlAction.Unknown,
                Verdict = Verdict.Unknown,
                Reason = "lock-health-service-block-disabled",
                Facts = rule.Facts.SetItem("serviceBlockEnabled", "False")
            };
        }
        return new(
            Enabled: true,
            PairMatched: true,
            Kick: options.EnableServiceKick && rule.Action == ControlAction.Block && rule.Verdict == Verdict.UnsafeInput,
            Reason: rule.Reason,
            PairCount: state.PairCount,
            RequiredPairs: options.RequiredPairs,
            PendingHurtCount: state.PendingCount,
            DroppedPendingHurtCount: state.DroppedPending,
            LastPairGapTicks: gap,
            RuleResult: rule);
    }

    private int FindPending(State state)
    {
        for (int index = 0; index < state.PendingCount; index++)
        {
            int slot = (state.PendingStart + index) % state.Pending.Length;
            var pending = state.Pending[slot];
            if (pending.Tick != long.MinValue && pending.Tick <= tick && tick - pending.Tick <= options.ResponseWindowTicks)
                return slot;
        }
        return -1;
    }

    private void AddPending(State state, Pending pending)
    {
        if (state.PendingCount == state.Pending.Length)
        {
            state.Pending[state.PendingStart] = Pending.Empty;
            state.PendingStart = (state.PendingStart + 1) % state.Pending.Length;
            state.PendingCount--;
            state.DroppedPending = Increment(state.DroppedPending);
        }
        int slot = (state.PendingStart + state.PendingCount) % state.Pending.Length;
        state.Pending[slot] = pending;
        state.PendingCount++;
    }

    private static void RemovePendingAt(State state, int physicalIndex)
    {
        int relative = (physicalIndex - state.PendingStart + state.Pending.Length) % state.Pending.Length;
        for (int offset = relative; offset < state.PendingCount - 1; offset++)
        {
            int destination = (state.PendingStart + offset) % state.Pending.Length;
            int source = (state.PendingStart + offset + 1) % state.Pending.Length;
            state.Pending[destination] = state.Pending[source];
        }
        int last = (state.PendingStart + state.PendingCount - 1) % state.Pending.Length;
        state.Pending[last] = Pending.Empty;
        state.PendingCount--;
    }

    private void Expire(State state)
    {
        while (state.PendingCount > 0)
        {
            var pending = state.Pending[state.PendingStart];
            if (pending.Tick != long.MinValue && tick >= pending.Tick && tick - pending.Tick <= options.ResponseWindowTicks)
                break;
            state.Pending[state.PendingStart] = Pending.Empty;
            state.PendingStart = (state.PendingStart + 1) % state.Pending.Length;
            state.PendingCount--;
        }
        if (state.LastPairTick != long.MinValue && tick >= state.LastPairTick && tick - state.LastPairTick > options.WindowTicks)
        {
            state.PairCount = 0;
            state.LastPairTick = long.MinValue;
        }
    }

    private State GetState(SessionKey session, TSPlayer actor)
    {
        if (!TrySessionSlot(session, out int slot))
            throw new ArgumentOutOfRangeException(nameof(session));
        if (states[slot] is not { } state || state.Session != session || state.AccountId != actor.Account!.ID ||
            !ReferenceEquals(state.Actor, actor) || !ReferenceEquals(state.RuntimePlayer, actor.TPlayer))
            states[slot] = state = new(session, actor.Account!.ID, actor, options.PendingCapacity);
        return state;
    }

    private bool TryCurrentTarget(int slot, out SessionSnapshot? session, out TSPlayer? actor)
    {
        session = null;
        actor = null;
        if (lookup is null || slot is < 0 or >= SessionCapacity)
            return false;
        var target = lookup(slot);
        session = target.Session;
        actor = target.Player;
        return session is { Revoked: false } && actor is not null && actor.Index == slot;
    }

    private bool CurrentBinding(SessionKey session, long accountId, TSPlayer actor)
    {
        if (!TryCurrentTarget(session.Slot, out var currentSession, out var currentActor))
            return false;
        return currentSession!.Key == session && currentSession.AccountId == accountId &&
            ReferenceEquals(currentActor, actor) && actor.Index == session.Slot;
    }

    private bool CurrentThread() => Healthy && Main.netMode == 2 && updateThread != 0 &&
        Environment.CurrentManagedThreadId == updateThread && worldEpoch > 0 && tick >= 0;

    private bool TrySessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 && session.Generation > 0 &&
            slot >= 0 && slot < SessionCapacity;
    }

    private M18LockHealthDecision NoDecision(string reason)
        => new(true, false, false, reason, 0, options.RequiredPairs, 0, 0, 0, null);

    private static bool AddressedTo(int subject, int remote, int ignored)
        => remote == subject || remote == -1 && ignored != subject;

    private static int Increment(int value) => value == int.MaxValue ? value : value + 1;

    private void Safe(Action action)
    {
        if (failed)
            return;
        try
        {
            action();
        }
        catch (Exception error)
        {
            failed = true;
            try { IntegrityFault?.Invoke(error); } catch { }
        }
    }

    public void Dispose()
    {
        if (installed)
            HookEvents.Terraria.NetMessage.SendPlayerHurt -= OnSendPlayerHurt;
        installed = false;
        lookup = null;
        Array.Clear(states);
        tick = long.MinValue;
        worldEpoch = long.MinValue;
        updateThread = 0;
    }

    private sealed class State(SessionKey session, long accountId, TSPlayer actor, int pendingCapacity)
    {
        public SessionKey Session { get; } = session;
        public long AccountId { get; } = accountId;
        public TSPlayer Actor { get; } = actor;
        public Player RuntimePlayer { get; } = actor.TPlayer;
        public Pending[] Pending { get; } = new Pending[pendingCapacity];
        public int PendingStart;
        public int PendingCount;
        public int DroppedPending;
        public int PendingConsumed;
        public int PairCount;
        public long LastPairTick = long.MinValue;
        public bool Triggered;
    }

    private readonly record struct Pending(long Tick, int LifeAfterHurt, int LifeMaximum)
    {
        public static Pending Empty => new(long.MinValue, 0, 0);
    }
}
