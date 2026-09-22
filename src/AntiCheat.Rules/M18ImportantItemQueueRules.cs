using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// A deliberately small, record-only queue for the explicitly listed boss-bag
/// items. It is not an asset ledger and it never turns an observed quantity into
/// acquisition or creator proof.
/// </summary>
public sealed record M18ImportantItemQueueOptions
{
    public bool Enabled { get; init; }
    public int WindowTicks { get; init; } = 180;
    public int EventCapacity { get; init; } = 128;
    public int InventorySlotCapacity { get; init; } = 256;

    public static M18ImportantItemQueueOptions Disabled => new();

    public static M18ImportantItemQueueOptions TestLabCandidate => new()
    {
        Enabled = true,
    };

    public static M18ImportantItemQueueOptions ProductionCandidate => TestLabCandidate;

    public static M18ImportantItemQueueOptions ForExecutionScope(
        ExecutionScope scope,
        M18CandidateMode mode = M18CandidateMode.Auto,
        bool recordObservations = true)
    {
        if (!recordObservations || !M18ExecutionModePolicy.RecordEnabled(scope, mode))
            return Disabled;
        return mode == M18CandidateMode.ProductionCandidate ? ProductionCandidate : TestLabCandidate;
    }

    public void Validate()
    {
        if (WindowTicks is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(WindowTicks));
        if (EventCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(EventCapacity));
        if (InventorySlotCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(InventorySlotCapacity));
    }
}

public enum M18ImportantItemSource
{
    Inventory,
    Container,
    WorldDrop,
}

/// <summary>Only packet-time facts available before the native inventory handler.</summary>
public readonly record struct M18ImportantItemObservation(
    SessionKey Session,
    long AccountId,
    M18ImportantItemSource Source,
    int Slot,
    int ItemId,
    int Stack,
    bool SessionComplete,
    bool SourceContextKnown,
    bool SourceAttributionComplete,
    bool InitialSynchronization,
    bool ParseComplete,
    bool ClientOrigin,
    bool BeforeSideEffects);

public sealed record M18ImportantItemQueueDecision(
    bool Enabled,
    bool Important,
    bool Recorded,
    bool GrowthObserved,
    bool PossibleSorting,
    bool InitialSynchronization,
    bool SourceContextKnown,
    bool SourceAttributionComplete,
    bool QueueOverflowed,
    ControlAction Action,
    Verdict Verdict,
    string Reason,
    int ItemId,
    string ItemName,
    M18ImportantItemSource Source,
    int Slot,
    int Stack,
    int PreviousTotal,
    int CurrentTotal,
    int Delta,
    int EventsRetained,
    int RecentPositiveGrowth,
    int PreviousSlotStack = 0)
{
    public static M18ImportantItemQueueDecision Disabled => new(
        Enabled: false,
        Important: false,
        Recorded: false,
        GrowthObserved: false,
        PossibleSorting: false,
        InitialSynchronization: false,
        SourceContextKnown: false,
        SourceAttributionComplete: false,
        QueueOverflowed: false,
        Action: ControlAction.Unknown,
        Verdict: Verdict.Unknown,
        Reason: "important-item-queue-disabled",
        ItemId: 0,
        ItemName: "",
        Source: M18ImportantItemSource.Inventory,
        Slot: 0,
        Stack: 0,
        PreviousTotal: 0,
        CurrentTotal: 0,
        Delta: 0,
        EventsRetained: 0,
        RecentPositiveGrowth: 0);
}

/// <summary>
/// Fixed session table plus a fixed inventory-slot snapshot and event FIFO.
/// Inventory updates maintain an aggregate for the selected item IDs only;
/// container/drop observations are recorded without assigning a creator.
/// </summary>
public sealed class M18ImportantItemQueue
{
    private const int SessionSlotCapacity = 256;
    private readonly M18ImportantItemQueueOptions _options;
    private readonly ImmutableDictionary<int, string> _definitions;
    private readonly ImmutableDictionary<int, int> _definitionIndexes;
    private readonly SessionState?[] _sessions = new SessionState?[SessionSlotCapacity];
    private long _worldEpoch = long.MinValue;

    public M18ImportantItemQueue(M18ImportantItemQueueOptions options,
        IReadOnlyDictionary<int, string> definitions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        if (definitions is null) throw new ArgumentNullException(nameof(definitions));
        if (definitions.Count > 64) throw new ArgumentOutOfRangeException(nameof(definitions));

        var names = ImmutableDictionary.CreateBuilder<int, string>();
        foreach (var pair in definitions)
        {
            if (pair.Key < 0 || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 128)
                throw new ArgumentException("Important item definitions must be bounded and named.", nameof(definitions));
            names.Add(pair.Key, pair.Value);
        }
        _definitions = names.ToImmutable();
        _definitionIndexes = _definitions.Keys
            .Select((id, index) => (id, index))
            .ToImmutableDictionary(x => x.id, x => x.index);
    }

    public bool Enabled => _options.Enabled;
    public bool CatalogReady => _definitions.Count > 0;
    public int DefinitionCount => _definitions.Count;

    public void AdvanceWorld(long worldEpoch)
    {
        if (_worldEpoch == worldEpoch) return;
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
        if (!TryGetSessionSlot(session, out int slot)) return;
        var state = _sessions[slot];
        if (state is not null && state.Session == session) _sessions[slot] = null;
    }

    public M18ImportantItemQueueDecision Observe(long tick, M18ImportantItemObservation observation)
    {
        if (!_options.Enabled) return M18ImportantItemQueueDecision.Disabled;
        if (_definitions.Count == 0) return Incomplete(observation, "important-item-catalog-unavailable");
        if (tick < 0) return Incomplete(observation, "important-item-queue-clock-unavailable");
        if (!TryGetSessionSlot(observation.Session, out int sessionSlot))
            return Incomplete(observation, "important-item-queue-session-unavailable");
        if (!Enum.IsDefined(observation.Source) || observation.AccountId <= 0 ||
            !observation.SessionComplete || !observation.ParseComplete ||
            !observation.ClientOrigin || !observation.BeforeSideEffects ||
            observation.ItemId < 0 || observation.Stack < 0 ||
            observation.Source == M18ImportantItemSource.Inventory &&
                (observation.Slot < 0 || observation.Slot >= _options.InventorySlotCapacity))
            return Incomplete(observation, "important-item-observation-incomplete");

        var state = _sessions[sessionSlot];
        if (state is null || state.Session != observation.Session || state.AccountId != observation.AccountId)
        {
            state = new SessionState(observation.Session, observation.AccountId,
                _options.InventorySlotCapacity, _definitions.Count, _options.EventCapacity);
            _sessions[sessionSlot] = state;
        }
        if (state.LastTick != long.MinValue && tick < state.LastTick)
        {
            _sessions[sessionSlot] = new SessionState(observation.Session, observation.AccountId,
                _options.InventorySlotCapacity, _definitions.Count, _options.EventCapacity);
            return Incomplete(observation, "important-item-queue-clock-regressed");
        }

        state.Expire(tick, _options.WindowTicks);
        state.LastTick = tick;
        if (observation.Source == M18ImportantItemSource.Inventory)
        {
            var update = state.UpdateInventory(observation.Slot, observation.ItemId, observation.Stack,
                _definitionIndexes, tick, observation.InitialSynchronization);
            if (!update.Important) return OutsideCandidate(observation);
            return state.Decision(observation, update.ItemId, _definitions[update.ItemId], update.PreviousTotal,
                update.CurrentTotal, update.Delta, update.GrowthObserved, update.PossibleSorting,
                update.Important, update.InitialSynchronization || observation.InitialSynchronization,
                "important-item-inventory-recorded", update.PreviousSlotStack);
        }

        if (!_definitions.TryGetValue(observation.ItemId, out string? name))
            return OutsideCandidate(observation);

        state.AddEvent(tick, observation.ItemId, observation.Stack, observation.InitialSynchronization,
            observation.Source);
        return state.Decision(observation, observation.ItemId, name, 0, observation.Stack, observation.Stack,
            growthObserved: false, possibleSorting: false, important: true,
            initialSynchronization: observation.InitialSynchronization,
            reason: "important-item-source-recorded");
    }

    private M18ImportantItemQueueDecision OutsideCandidate(M18ImportantItemObservation observation)
        => new(
            Enabled: true,
            Important: false,
            Recorded: false,
            GrowthObserved: false,
            PossibleSorting: false,
            InitialSynchronization: observation.InitialSynchronization,
            SourceContextKnown: observation.SourceContextKnown,
            SourceAttributionComplete: observation.SourceAttributionComplete,
            QueueOverflowed: false,
            Action: ControlAction.Unknown,
            Verdict: Verdict.Unknown,
            Reason: "important-item-outside-explicit-list",
            ItemId: observation.ItemId,
            ItemName: "",
            Source: observation.Source,
            Slot: observation.Slot,
            Stack: observation.Stack,
            PreviousTotal: 0,
            CurrentTotal: 0,
            Delta: 0,
            EventsRetained: 0,
            RecentPositiveGrowth: 0);

    private static M18ImportantItemQueueDecision Incomplete(
        M18ImportantItemObservation observation, string reason)
        => new(
            Enabled: true,
            Important: false,
            Recorded: false,
            GrowthObserved: false,
            PossibleSorting: false,
            InitialSynchronization: observation.InitialSynchronization,
            SourceContextKnown: observation.SourceContextKnown,
            SourceAttributionComplete: observation.SourceAttributionComplete,
            QueueOverflowed: false,
            Action: ControlAction.Unknown,
            Verdict: Verdict.Unknown,
            Reason: reason,
            ItemId: observation.ItemId,
            ItemName: "",
            Source: observation.Source,
            Slot: observation.Slot,
            Stack: observation.Stack,
            PreviousTotal: 0,
            CurrentTotal: 0,
            Delta: 0,
            EventsRetained: 0,
            RecentPositiveGrowth: 0);

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            slot >= 0 && slot < SessionSlotCapacity && session.Generation > 0;
    }

    private sealed class SessionState
    {
        private readonly SlotState[] _slots;
        private readonly int[] _totals;
        private readonly Sample[] _events;
        private int _eventStart;
        private int _eventCount;
        private int _recentPositiveGrowth;
        private bool _queueOverflowed;

        public SessionState(SessionKey session, long accountId, int slotCapacity,
            int definitionCount, int eventCapacity)
        {
            Session = session;
            AccountId = accountId;
            _slots = new SlotState[slotCapacity];
            _totals = new int[definitionCount];
            _events = new Sample[eventCapacity];
            LastTick = long.MinValue;
        }

        public SessionKey Session { get; }
        public long AccountId { get; }
        public long LastTick { get; set; }

        public void Expire(long tick, int windowTicks)
        {
            while (_eventCount > 0 && tick - _events[_eventStart].Tick >= windowTicks)
            {
                if (!_events[_eventStart].InitialSynchronization)
                    _recentPositiveGrowth -= Math.Max(0, _events[_eventStart].Delta);
                _eventStart = (_eventStart + 1) % _events.Length;
                _eventCount--;
            }
        }

        public InventoryUpdate UpdateInventory(int slot, int itemId, int stack,
            ImmutableDictionary<int, int> indexes, long tick, bool initialSynchronization)
        {
            var previous = _slots[slot];
            bool oldImportant = indexes.TryGetValue(previous.ItemId, out int oldIndex);
            bool newImportant = indexes.TryGetValue(itemId, out int newIndex);
            int previousTotal = newImportant ? _totals[newIndex] : oldImportant ? _totals[oldIndex] : 0;
            if (oldImportant) _totals[oldIndex] = Math.Max(0, _totals[oldIndex] - previous.Stack);
            if (newImportant) _totals[newIndex] = checked(_totals[newIndex] + stack);
            _slots[slot] = new(itemId, stack);

            if (!oldImportant && !newImportant)
                return new(false, itemId, false, false, false, 0, 0, 0, previous.Stack);

            int reportedId = newImportant ? itemId : previous.ItemId;
            int currentTotal = newImportant ? _totals[newIndex] : _totals[oldIndex];
            int delta = currentTotal - previousTotal;
            bool possibleSorting = newImportant && !initialSynchronization && delta > 0 &&
                HasRecentRemoval(itemId, tick);
            bool growth = newImportant && !initialSynchronization && delta > 0 && !possibleSorting;
            AddEvent(tick, reportedId, delta, initialSynchronization, M18ImportantItemSource.Inventory);
            return new(true, reportedId, growth, possibleSorting, initialSynchronization,
                previousTotal, currentTotal, delta, previous.Stack);
        }

        public void AddEvent(long tick, int itemId, int delta, bool initialSynchronization,
            M18ImportantItemSource source)
        {
            if (_eventCount == _events.Length)
            {
                if (!_events[_eventStart].InitialSynchronization)
                    _recentPositiveGrowth -= Math.Max(0, _events[_eventStart].Delta);
                _eventStart = (_eventStart + 1) % _events.Length;
                _eventCount--;
                _queueOverflowed = true;
            }
            int index = (_eventStart + _eventCount) % _events.Length;
            _events[index] = new(tick, itemId, delta, initialSynchronization, source);
            _eventCount++;
            if (!initialSynchronization)
                _recentPositiveGrowth += Math.Max(0, delta);
        }

        private bool HasRecentRemoval(int itemId, long tick)
        {
            for (int i = 0; i < _eventCount; i++)
            {
                var sample = _events[(_eventStart + i) % _events.Length];
                if (sample.Source == M18ImportantItemSource.Inventory && sample.Delta < 0 &&
                    tick >= sample.Tick && tick - sample.Tick < 3 &&
                    sample.ItemId == itemId)
                    return true;
            }
            return false;
        }

        public M18ImportantItemQueueDecision Decision(M18ImportantItemObservation observation,
            int itemId, string name, int previousTotal, int currentTotal, int delta,
            bool growthObserved, bool possibleSorting, bool important, bool initialSynchronization,
            string reason, int previousSlotStack = 0)
            => new(
                Enabled: true,
                Important: important,
                Recorded: important,
                GrowthObserved: growthObserved && !initialSynchronization,
                PossibleSorting: possibleSorting,
                InitialSynchronization: initialSynchronization,
                SourceContextKnown: observation.SourceContextKnown,
                SourceAttributionComplete: observation.SourceAttributionComplete,
                QueueOverflowed: _queueOverflowed,
                Action: ControlAction.Unknown,
                Verdict: Verdict.Unknown,
                Reason: reason,
                ItemId: itemId,
                ItemName: name,
                Source: observation.Source,
                Slot: observation.Slot,
                Stack: observation.Stack,
                PreviousTotal: previousTotal,
                CurrentTotal: currentTotal,
                Delta: delta,
                EventsRetained: _eventCount,
                RecentPositiveGrowth: _recentPositiveGrowth,
                PreviousSlotStack: previousSlotStack);

        private readonly record struct SlotState(int ItemId, int Stack);
        private readonly record struct Sample(long Tick, int ItemId, int Delta,
            bool InitialSynchronization, M18ImportantItemSource Source);
        public readonly record struct InventoryUpdate(bool Important, int ItemId, bool GrowthObserved,
            bool PossibleSorting, bool InitialSynchronization, int PreviousTotal, int CurrentTotal, int Delta,
            int PreviousSlotStack);
    }
}

public static class M18ImportantItemQueueRules
{
    public const string RuleId = "C02.ImportantItemGrowthObservation";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-explicit-boss-bag-short-window-record-v1";

    public static BusinessRuleResult Observe(RuleInputContext input,
        M18ImportantItemQueueDecision decision)
    {
        if (!decision.Important)
            return RuleResults.Unknown(RuleId, decision.Reason, ImmutableDictionary<string, string>.Empty);

        var facts = RuleResults.Facts(
            ("itemId", decision.ItemId),
            ("itemName", decision.ItemName),
            ("source", decision.Source),
            ("slot", decision.Slot),
            ("stack", decision.Stack),
            ("previousSlotStack", decision.PreviousSlotStack),
            ("previousTotal", decision.PreviousTotal),
            ("currentTotal", decision.CurrentTotal),
            ("delta", decision.Delta),
            ("growthObserved", decision.GrowthObserved),
            ("possibleSorting", decision.PossibleSorting),
            ("initialSynchronization", decision.InitialSynchronization),
            ("sourceContextKnown", decision.SourceContextKnown),
            ("sourceAttributionComplete", decision.SourceAttributionComplete),
            ("queueOverflowed", decision.QueueOverflowed),
            ("eventsRetained", decision.EventsRetained),
            ("recentPositiveGrowth", decision.RecentPositiveGrowth),
            ("ruleContextComplete", input.Complete),
            ("creatorProof", false),
            ("passiveReceiptIsNotCreatorProof", true),
            ("contract", ContractVersion));
        return RuleResults.Unknown(RuleId, decision.Reason, facts);
    }
}
