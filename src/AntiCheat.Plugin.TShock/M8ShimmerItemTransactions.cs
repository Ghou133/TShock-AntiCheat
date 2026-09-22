using System.Collections.Immutable;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public sealed record M8ShimmerItemTransaction(long Operation, long WorldEpoch, int WorldId,
    int WorldItemSlot, bool BoundToWorldSlot, int InputType, int OutputType, int InputStack, int OutputStack,
    bool CurrentMoonLordDefeated, bool MoonLordObservedInEpoch, string Outcome,
    DateTimeOffset ObservedUtc);

/// <summary>
/// Natural transformation sub-contract for the three Moon Lord shimmer outputs also present in
/// MKLP PG-POL-012. This is an execution guard and a bounded manufacturing witness, NOT an item
/// acquisition/possession rule. No item owner/reservation field is treated as an authenticated actor.
/// </summary>
public sealed class M8ShimmerItemTransactions(TimeProvider clock, string fingerprint) : IDisposable
{
    public const string ContractId = "PG-NAT-M8.MoonLordShimmerExecution/1.0.0";
    public const int Capacity = 128;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    // The reverse bucket conversion has the SAME native lock and must remain in the contract.
    public static readonly ImmutableDictionary<int, int> Transformations =
        new Dictionary<int, int> { [1326] = 5335, [779] = 5134, [3031] = 5364, [5364] = 3031 }
            .ToImmutableDictionary();

    private delegate void OriginalShimmer(WorldItem item);
    private delegate void ShimmerDetour(OriginalShimmer original, WorldItem item);
    private sealed record Retained(M8ShimmerItemTransaction Transaction, long Timestamp);
    private readonly Retained?[] recent = new Retained?[Capacity];
    private Hook? hook;
    private int thread, worldId, cursor;
    private long epoch, operation;
    private bool failed, observedMoonLord, hostContractLost;
    public bool Healthy => hook is not null && !failed;
    public Action<Exception>? IntegrityFault { get; set; }
    public bool FaultObserverFailed { get; private set; }
    public long Allowed { get; private set; }
    public long Blocked { get; private set; }
    public long Unknown { get; private set; }
    public long Completed { get; private set; }
    public long Dropped { get; private set; }
    public long NativeFailures { get; private set; }
    public long PostconditionFailures { get; private set; }
    public string LastUnknownReason { get; private set; } = "not-yet-observed";

    public void Install()
    {
        if (hook is not null || failed) return;
        try
        {
            if (fingerprint != TargetRuntime.Fingerprint)
                throw new InvalidOperationException("The shimmer execution contract requires the audited target runtime.");
            var method = typeof(WorldItem).GetMethod(nameof(WorldItem.GetShimmered),
                BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
                ?? throw new MissingMethodException(typeof(WorldItem).FullName, nameof(WorldItem.GetShimmered));
            hook = new Hook(method, (ShimmerDetour)OnGetShimmered);
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>Called on the established game-update execution context, with Core's world epoch.</summary>
    public void Tick(long worldEpoch)
    {
        try { Refresh(worldEpoch); }
        catch (Exception error) { Fail(error); }
    }

    private void Refresh(long worldEpoch)
    {
        if (worldEpoch <= 0 || Main.netMode != 2) return;
        if (thread != 0 && thread != Environment.CurrentManagedThreadId)
        { Fail(new InvalidOperationException("The shimmer game-update execution context changed.")); return; }
        thread = Environment.CurrentManagedThreadId;
        if (!NativePluginsOnly()) hostContractLost = true;
        if (epoch != worldEpoch || worldId != Main.worldID)
        {
            Array.Clear(recent); cursor = 0; observedMoonLord = false;
            epoch = worldEpoch; worldId = Main.worldID;
        }
        // Positive history only. A false current flag never establishes a clean creation/import
        // baseline. This evidence does not survive world replacement or provide acquisition history.
        observedMoonLord |= NPC.downedMoonlord;
    }

    private void OnGetShimmered(OriginalShimmer original, WorldItem entity)
    {
        if (entity?.inner is null || !Transformations.TryGetValue(entity.type, out int output))
        { original(entity!); return; }
        bool applies;
        string reason;
        try { applies = CanApply(entity, output, out reason); }
        catch (Exception error) { Fail(error); original(entity); return; }
        if (!applies)
        {
            Unknown = Increment(Unknown); LastUnknownReason = reason;
            original(entity); return;
        }
        bool current = NPC.downedMoonlord;
        observedMoonLord |= current;
        int input = entity.type, stack = entity.stack;
        bool locked;
        try { locked = ShimmerTransforms.IsItemTransformLocked(input); }
        catch (Exception error) { Fail(error); original(entity); return; }
        if (locked)
        {
            // GetShimmered does not itself re-check CanShimmer. The ordinary UpdateShimmer
            // producer already obeys the gate; direct invocations get the same atomic check here,
            // before SetDefaults, stack changes, effect packet146, SyncItem or achievements.
            Blocked = Increment(Blocked);
            TryRecord(entity, input, stack, current, "blocked-native-transform-lock");
            return;
        }
        Allowed = Increment(Allowed);
        try { original(entity); }
        catch
        {
            NativeFailures = Increment(NativeFailures);
            TryRecord(entity, input, stack, current, "native-execution-failed");
            throw; // Preserve the original runtime failure and never attribute it to a player.
        }
        bool committed = entity.type == output && entity.stack == stack && entity.shimmered;
        if (committed) Completed = Increment(Completed);
        else
        {
            PostconditionFailures = Increment(PostconditionFailures);
            Fail(new InvalidOperationException("The selected native shimmer transformation changed its postcondition."));
        }
        TryRecord(entity, input, stack, current, committed ? "native-transform-committed" : "native-postcondition-failed");
    }

    private bool CanApply(WorldItem entity, int expectedOutput, out string reason)
    {
        reason = "runtime-world-or-execution-context-unavailable";
        if (!Healthy || Main.netMode != 2 || Main.myPlayer != 255 || Main.maxTilesX <= 0 ||
            thread != Environment.CurrentManagedThreadId || epoch <= 0 || worldId != Main.worldID) return false;
        reason = "unsupported-current-host-transform-contract";
        // This condition concerns a server-local operation NOW. It makes no claim that account
        // inventories have never contained host-customized items, and does not erase such history.
        if (!NativePluginsOnly()) hostContractLost = true;
        // An actually observed host extension may have registered callbacks that survive unload.
        // Waiting, unloading it or replacing a world must not recreate this local native contract.
        if (hostContractLost) return false;
        int input = entity.type;
        reason = "selected-transform-table-changed";
        if (ItemID.Sets.ShimmerTransformToItem[input] != expectedOutput ||
            !ItemID.Sets.ShimmerPostMoonlord[input] || ItemID.Sets.ShimmerCountsAsItem[input] != -1 ||
            entity.inner.GetShimmerEquivalentType() != input || ShimmerTransforms.GetTransformToItem(input) != expectedOutput)
            return false;
        reason = "invalid-world-item-stack";
        if (entity.stack <= 0 || entity.stack > entity.maxStack) return false;
        reason = "supported-native-transform";
        return true;
    }

    private void TryRecord(WorldItem entity, int input, int stack, bool current, string outcome)
    {
        try
        {
            var utc = clock.GetUtcNow();
            long timestamp = clock.GetTimestamp();
            if (recent[cursor] is { } old && IsRetained(old.Timestamp, timestamp)) Dropped = Increment(Dropped);
            bool bound = (uint)entity.whoAmI < Main.item.Length && ReferenceEquals(Main.item[entity.whoAmI], entity);
            operation = Increment(operation);
            recent[cursor] = new(new(operation, epoch, worldId, entity.whoAmI, bound, input, entity.type,
                stack, entity.stack, current, observedMoonLord, outcome, utc), timestamp);
            cursor = (cursor + 1) % Capacity;
        }
        catch (Exception error) { Fail(error); }
    }

    public ImmutableArray<M8ShimmerItemTransaction> CaptureRecent()
    {
        if (thread != Environment.CurrentManagedThreadId || worldId != Main.worldID) return [];
        try
        {
            long now = clock.GetTimestamp();
            return recent.Where(x => x is not null && x.Transaction.WorldEpoch == epoch && x.Transaction.WorldId == worldId &&
                    IsRetained(x.Timestamp, now))
                .Select(x => x!.Transaction).OrderBy(x => x.Operation).ToImmutableArray();
        }
        catch (Exception error) { Fail(error); return []; }
    }

    private bool IsRetained(long timestamp, long now) => timestamp <= now && clock.GetElapsedTime(timestamp, now) <= Retention;

    private void Fail(Exception error)
    {
        if (failed) return;
        failed = true;
        try { IntegrityFault?.Invoke(error); }
        catch (Exception) { FaultObserverFailed = true; }
    }

    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
    private static bool NativePluginsOnly() => ServerApi.Plugins.All(x =>
        x.Plugin.GetType() == typeof(TShockAPI.TShock) || x.Plugin.GetType() == typeof(AntiCheatPlugin));

    public void Dispose()
    {
        hook?.Dispose(); hook = null;
        Array.Clear(recent); epoch = 0; thread = 0; observedMoonLord = false;
    }
}
