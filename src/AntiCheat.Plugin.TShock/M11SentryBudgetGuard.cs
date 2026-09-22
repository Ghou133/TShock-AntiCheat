using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace AntiCheat.Plugin.TShock;

public sealed record M11SentryState(SessionKey Session, int KnownActiveEntities, long NativeCommits,
    M10SummonDecision? Decision);

/// <summary>Consumes the existing M10 native capacity and receive/allocation witnesses.
/// Positive live-entity references stay bounded and are never released by a missing29, timeout,
/// core cancellation or cache pressure. Only actual lifecycle change releases their resource cost.</summary>
public sealed class M11SentryBudgetGuard
{
    public const int Hydra = 308;
    public const int EntitiesPerSession = M11SentryBudgetRules.AbsoluteNativeCapacity + 2;
    private sealed record EntityWitness(SessionKey Session, Projectile Entity, uint Key);
    private sealed class SlotState(SessionKey session)
    {
        public readonly SessionKey Session = session;
        public readonly List<EntityWitness> Entities = new(EntitiesPerSession);
        public long Commits;
        public M10SummonDecision? Decision;
    }
    private readonly SlotState?[] slots = new SlotState?[256];
    public long NativeCommits { get; private set; }
    public bool TargetValidated { get; private set; }
    public string? FailureReason { get; private set; }

    public void ValidateTarget()
    {
        try
        {
            var entity = new Projectile(); entity.SetDefaults(Hydra);
            var staff = new Item(); staff.SetDefaults(ItemID.StaffoftheFrostHydra);
            TargetValidated = entity.sentry && !entity.minion && entity.minionSlots == 0 &&
                !ProjectileID.Sets.IsADD2Turret[Hydra] && staff.shoot == Hydra && Player.maxBuffs == 44;
            if (!TargetValidated) FailureReason = "Ordinary single-body Frost Hydra definition changed.";
        }
        catch (Exception error) { TargetValidated = false; FailureReason = error.GetType().Name; }
    }

    public void ResetWorld() => Array.Clear(slots);
    public void ClearSlot(int slot) { if ((uint)slot < slots.Length) slots[slot] = null; }
    public void ValidateSlot(int slot, SessionKey? current)
    {
        if (slots[slot] is not { } state) return;
        if (state.Session != current) { slots[slot] = null; return; }
        Prune(state);
    }
    private SlotState Bind(SessionKey session)
    {
        if (slots[session.Slot]?.Session != session) slots[session.Slot] = new(session);
        return slots[session.Slot]!;
    }

    public M11SentryState? Capture(int slot, SessionKey? current)
    {
        if ((uint)slot >= slots.Length || slots[slot] is not { } state || state.Session != current) return null;
        return new(state.Session, state.Entities.Count(entry => SameActive(entry)), state.Commits, state.Decision);
    }

    public void RecordCommit(SessionKey session, Projectile entity, uint key)
    {
        if (!TargetValidated || !IsHydra(entity, session, key)) return;
        var state = Bind(session); Prune(state);
        // An observed native allocation establishes a new entity generation, even if a pool slot,
        // object and protocol key were all reused. Never count an existing27 update as allocation.
        state.Entities.RemoveAll(entry => ReferenceEquals(entry.Entity, entity) || entry.Key == key);
        if (state.Entities.Count >= EntitiesPerSession) return; // Active witnesses are not evicted.
        state.Entities.Add(new(session, entity, key));
        if (state.Commits < long.MaxValue) state.Commits++;
        if (NativeCommits < long.MaxValue) NativeCommits++;
    }

    public BusinessRuleResult Evaluate(SessionKey session, uint key, SessionKey snapshotSession,
        bool healthy, bool capacityReturned, bool inputsStable, bool freshOwnHydra, bool cancelled,
        int currentCapacity, int upperBound, int pendingBuffAllowance, bool equipmentSettled, bool knownHost, bool actorBound)
    {
        var state = Bind(session); Prune(state);
        var result = M11SentryBudgetRules.Evaluate(new(session, snapshotSession, healthy && TargetValidated, capacityReturned,
            inputsStable, freshOwnHydra, cancelled, state.Entities.Count, upperBound, knownHost && actorBound));
        result = result with { Facts = result.Facts.Add("producer", nameof(M11SentryBudgetGuard))
            .Add("capacitySource", "shared-native-Player.Update-ResetEffects-UpdateBuffs-UpdateEquips-UpdateArmorSets")
            .Add("capacityCurrent", currentCapacity.ToString()).Add("pendingWarTableAllowance", pendingBuffAllowance.ToString())
            .Add("equipmentInputAcceptanceSettled", equipmentSettled.ToString()).Add("nativeHostKnown", knownHost.ToString())
            .Add("activeWitnessLimit", EntitiesPerSession.ToString()).Add("witnessExpiry", "actual-lifecycle-only")
            .Add("nativeCommits", state.Commits.ToString()) };
        var old = state.Decision;
        static long Increment(long value) => value < long.MaxValue ? value + 1 : value;
        state.Decision = new(session, Increment(old?.Sequence ?? 0), key, result.Action, result.Verdict, result.Reason,
            result.Action == ControlAction.Pass ? Increment(old?.Passes ?? 0) : old?.Passes ?? 0,
            result.Action == ControlAction.Block ? Increment(old?.Blocks ?? 0) : old?.Blocks ?? 0,
            result.Action == ControlAction.Unknown ? Increment(old?.Unknowns ?? 0) : old?.Unknowns ?? 0);
        return result;
    }

    private static void Prune(SlotState state) => state.Entities.RemoveAll(entry => !SameActive(entry));
    private static bool SameActive(EntityWitness entry) => IsHydra(entry.Entity, entry.Session, entry.Key);
    private static bool IsHydra(Projectile entity, SessionKey session, uint key) =>
        entity.active && entity.sentry && !entity.minion && entity.type == Hydra && entity.minionSlots == 0 &&
        entity.owner == session.Slot && entity.key.bits == key &&
        Main.projectile is not null && (uint)entity.whoAmI < Main.projectile.Length && ReferenceEquals(Main.projectile[entity.whoAmI], entity);
}
