using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// A bounded post-native witness for the narrow P1-C important-item slice.
/// The baseline is captured immediately before a client-owned StrikeNPC call;
/// the post-native sample is accepted only when the same call entered NPCLoot.
/// This is a source/reward observation, never creator proof or a sanction input.
/// </summary>
public readonly record struct M18ImportantItemState(
    bool Active, int ItemId, int Stack);

public sealed record M18ImportantItemRewardItem(
    int ItemIndex, int ItemId, string ItemName, int Stack, int MaxStack,
    float X, float Y, int PreviousStack = 0, int Delta = 0);

public sealed record M18ImportantItemRewardObservation(
    SessionKey Session,
    long AccountId,
    int NpcSlot,
    int NpcGeneration,
    int NpcType,
    long Tick,
    bool NativeLootMethodObserved,
    ImmutableArray<M18ImportantItemRewardItem> Items,
    bool SourceContextKnown,
    bool SourceAttributionComplete,
    string Boundary)
{
    public bool HasItems => !Items.IsDefaultOrEmpty;
}

public sealed class M18ImportantItemRewardObserver
{
    private const int SessionSlotCapacity = 255;
    private const int MaximumItemSlots = 4096;
    private const int MaximumRewardItems = 16;
    public const int TtlTicks = 120;

    private readonly M18ImportantItemQueueOptions options;
    private readonly ImmutableDictionary<int, string> definitions;
    private readonly M18ImportantItemRewardObservation?[] latest = new M18ImportantItemRewardObservation?[SessionSlotCapacity];
    private long worldEpoch = long.MinValue;
    private long tick;
    private int observationFaulted;

    /// <summary>
    /// Reward observation is optional. A fault latches only this observer off;
    /// it must not be routed through the shared NPC-strike integrity callback.
    /// </summary>
    public bool ObservationHealthy => Volatile.Read(ref observationFaulted) == 0;
    public string? LastObservationFaultType { get; private set; }
    public string? LastObservationFaultMessage { get; private set; }
    public Action<Exception>? ObservationFault { get; set; }
    public Action<M18ImportantItemRewardObservation>? ObservationRecorded { get; set; }

    public M18ImportantItemRewardObserver(M18ImportantItemQueueOptions options,
        IReadOnlyDictionary<int, string> definitions)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
        if (definitions is null) throw new ArgumentNullException(nameof(definitions));
        if (definitions.Count > 64) throw new ArgumentOutOfRangeException(nameof(definitions));

        var names = ImmutableDictionary.CreateBuilder<int, string>();
        foreach (var pair in definitions)
        {
            if (pair.Key < 0 || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 128)
                throw new ArgumentException("Important item definitions must be bounded and named.", nameof(definitions));
            names.Add(pair.Key, pair.Value);
        }
        this.definitions = names.ToImmutable();
    }

    public bool Enabled => options.Enabled && definitions.Count > 0 && ObservationHealthy;

    public void ReportObservationFault(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.Exchange(ref observationFaulted, 1) != 0) return;
        LastObservationFaultType = error.GetType().Name;
        LastObservationFaultMessage = error.Message.Length <= 256 ? error.Message : error.Message[..256];
        try { ObservationFault?.Invoke(error); }
        catch { /* An optional diagnostic callback cannot affect native gameplay. */ }
    }

    public void Tick(long currentWorldEpoch)
    {
        if (worldEpoch != currentWorldEpoch)
        {
            Array.Clear(latest, 0, latest.Length);
            worldEpoch = currentWorldEpoch;
            tick = 0;
        }
        if (tick < long.MaxValue) tick++;
    }

    public void Reset()
    {
        Array.Clear(latest, 0, latest.Length);
        worldEpoch = long.MinValue;
        tick = 0;
    }

    public void Forget(SessionKey session)
    {
        if (!TryGetSessionSlot(session, out int slot)) return;
        if (latest[slot]?.Session == session) latest[slot] = null;
    }

    public void Begin(SessionKey session)
    {
        if (!TryGetSessionSlot(session, out int slot)) return;
        latest[slot] = null;
    }

    /// <summary>Capture only the bounded active/type/stack state needed to find
    /// an item that became active during the native loot call.</summary>
    public M18ImportantItemState[] CaptureBaseline()
    {
        if (!Enabled) return [];
        var items = Main.item;
        int count = Math.Min(items.Length, MaximumItemSlots);
        var baseline = new M18ImportantItemState[count];
        for (int index = 0; index < count; index++)
        {
            var item = items[index];
            baseline[index] = new(item.active, item.type, item.stack);
        }
        return baseline;
    }

    /// <summary>Capture the changed important items after native StrikeNPC has
    /// returned. The caller must have observed the same-generation NPCLoot hook.
    /// </summary>
    public M18ImportantItemRewardObservation? CaptureReward(
        SessionKey session,
        long accountId,
        NPC target,
        int targetGeneration,
        bool nativeLootMethodObserved,
        M18ImportantItemState[] baseline)
    {
        if (!Enabled || !nativeLootMethodObserved || accountId <= 0 ||
            !TryGetSessionSlot(session, out int sessionSlot) || worldEpoch != session.WorldEpoch ||
            target.whoAmI < 0 || target.whoAmI >= SessionSlotCapacity ||
            target.generation != targetGeneration || baseline is null)
            return null;

        var items = Main.item;
        int count = Math.Min(items.Length, Math.Min(MaximumItemSlots, baseline.Length));
        var captured = ImmutableArray.CreateBuilder<M18ImportantItemRewardItem>(MaximumRewardItems);
        for (int index = 0; index < count; index++)
        {
            var item = items[index];
            if (!item.active || !definitions.TryGetValue(item.type, out string? name)) continue;
            var prior = baseline[index];
            bool changed = !prior.Active || prior.ItemId != item.type || prior.Stack != item.stack;
            if (!changed) continue;
            if (captured.Count == MaximumRewardItems) break;
            int previousStack = prior.Active && prior.ItemId == item.type ? prior.Stack : 0;
            captured.Add(new M18ImportantItemRewardItem(index, item.type, name,
                item.stack, item.maxStack, item.position.X, item.position.Y,
                previousStack, item.stack - previousStack));
        }
        if (captured.Count == 0)
            return latest[sessionSlot]?.Session == session ? latest[sessionSlot] : null;

        var observation = new M18ImportantItemRewardObservation(
            session,
            accountId,
            target.whoAmI,
            targetGeneration,
            target.type,
            tick,
            NativeLootMethodObserved: true,
            // The builder is intentionally created with a maximum capacity.
            // MoveToImmutable requires Count == Capacity and therefore throws
            // for every valid 1..15-item native reward. ToImmutable preserves
            // the bounded allocation without turning optional observation into
            // a shared combat integrity fault.
            captured.ToImmutable(),
            SourceContextKnown: true,
            SourceAttributionComplete: false,
            Boundary: "same client StrikeNPC transaction entered native NPCLoot and changed bounded Main.item slots; creator proof and reward ownership remain unavailable");
        latest[sessionSlot] = observation;
        Publish(observation);
        return observation;
    }

    /// <summary>Capture a native per-client instanced item while its transient
    /// Main.item slot still exists. WorldItem.MakeInstanced sends packet90 to
    /// the matching client and then turns the server slot into air, so this is
    /// intentionally distinct from a persistent packet21 world drop.</summary>
    public M18ImportantItemRewardObservation? CaptureInstancedReward(
        SessionKey session,
        long accountId,
        NPC target,
        int targetGeneration,
        bool nativeLootMethodObserved,
        int itemIndex)
    {
        if (!Enabled || !nativeLootMethodObserved || accountId <= 0 ||
            !TryGetSessionSlot(session, out int sessionSlot) || worldEpoch != session.WorldEpoch ||
            target.whoAmI < 0 || target.whoAmI >= SessionSlotCapacity ||
            target.generation != targetGeneration || (uint)itemIndex >= (uint)Main.item.Length)
            return null;

        var item = Main.item[itemIndex];
        if (!item.active || item.stack <= 0 || !definitions.TryGetValue(item.type, out string? name))
            return null;

        var observation = new M18ImportantItemRewardObservation(
            session,
            accountId,
            target.whoAmI,
            targetGeneration,
            target.type,
            tick,
            NativeLootMethodObserved: true,
            ImmutableArray.Create(new M18ImportantItemRewardItem(itemIndex, item.type, name,
                item.stack, item.maxStack, item.position.X, item.position.Y,
                PreviousStack: 0, Delta: item.stack)),
            SourceContextKnown: true,
            SourceAttributionComplete: false,
            Boundary: "same client StrikeNPC transaction entered native NPCLoot and sent a bounded important item through native per-client packet90; server Main.item slot is transient and creator/reward ownership proof remains unavailable");
        latest[sessionSlot] = observation;
        Publish(observation);
        return observation;
    }

    public M18ImportantItemRewardObservation? CaptureLatest(SessionKey session)
    {
        if (!Enabled || !TryGetSessionSlot(session, out int slot) || worldEpoch != session.WorldEpoch)
            return null;
        var observation = latest[slot];
        return observation?.Session == session && tick >= observation.Tick &&
            tick - observation.Tick <= TtlTicks ? observation : null;
    }

    private static bool TryGetSessionSlot(SessionKey session, out int slot)
    {
        slot = session.Slot;
        return session.ServerRunId != Guid.Empty && session.WorldEpoch > 0 &&
            slot >= 0 && slot < SessionSlotCapacity && session.Generation > 0;
    }

    private void Publish(M18ImportantItemRewardObservation observation)
    {
        try { ObservationRecorded?.Invoke(observation); }
        catch (Exception error) { ReportObservationFault(error); }
    }
}
