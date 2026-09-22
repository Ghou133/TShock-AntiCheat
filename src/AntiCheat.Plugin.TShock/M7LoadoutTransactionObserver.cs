using AntiCheat.Core;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record M7LoadoutCompletion(SessionKey Session, string RuntimeFingerprint, long Revision, int PreviousLoadout, int AppliedLoadout,
    bool ServerPermutationObserved, bool SscPermutationObserved, bool VisibilityObserved,
    bool AttributionComplete, string Reason, M5EquipmentEffectSnapshot EffectiveEquipment)
{
    // This explicit server transaction boundary does not complete a separate client multi-slot edit.
    public bool ClientEquipmentTransitionComplete => false;
}

/// <summary>One pending/final bounded observation per server session. It never changes equipment,
/// cancels a packet or supplies a cheat verdict. Payload acceptance is not item-origin trust.</summary>
public sealed class M7LoadoutTransactionObserver : IDisposable
{
    private sealed record Pending(SessionKey Session, TSPlayer Actor, Player Player, PlayerData Store,
        int Previous, int Target, ushort Visibility, Item[][] Armor, Item[][] Dye, NetItem[][] SscArmor, NetItem[][] SscDye,
        int Thread, long Deadline, bool NativePlugins, int AccountId);
    private readonly Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current;
    private readonly TimeProvider clock;
    private readonly string fingerprint;
    private readonly Pending?[] pending = new Pending?[256];
    private readonly M7LoadoutCompletion?[] completed = new M7LoadoutCompletion?[256];
    private readonly long[] completedDeadline = new long[256];
    private const int LifetimeSeconds = 10;
    private bool installed, failed;
    private long revision;
    private int expiryCursor;
    public long Observed { get; private set; }
    public long Completed { get; private set; }
    public long Mismatched { get; private set; }
    public bool Healthy => installed && !failed;
    public string? FailureType { get; private set; }

    public M7LoadoutTransactionObserver(string fingerprint, Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current,
        TimeProvider? clock = null) { this.fingerprint = fingerprint; this.current = current; this.clock = clock ?? TimeProvider.System; }

    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.NetMessage.SendData += OnSendData; installed = true;
    }
    public void Dispose()
    {
        if (installed) HookEvents.Terraria.NetMessage.SendData -= OnSendData;
        installed = false; ResetWorld();
    }
    public void ResetWorld() { Array.Clear(pending); Array.Clear(completed); Array.Clear(completedDeadline); }
    public void Update()
    {
        long now = clock.GetTimestamp();
        for (int count = 0; count < 16; count++)
        {
            int slot = expiryCursor++ % pending.Length;
            if (expiryCursor == pending.Length) expiryCursor = 0;
            if (pending[slot] is { } candidate && now > candidate.Deadline) pending[slot] = null;
            if (now > completedDeadline[slot]) completed[slot] = null;
        }
    }
    public void Forget(SessionKey session)
    {
        if ((uint)session.Slot >= 256) return;
        if (pending[session.Slot]?.Session == session) pending[session.Slot] = null;
        if (completed[session.Slot]?.Session == session) completed[session.Slot] = null;
    }

    public M7LoadoutCompletion? Capture(SessionKey session)
    {
        if ((uint)session.Slot >= 256) return null;
        var result = completed[session.Slot];
        if (result?.Session != session || clock.GetTimestamp() > completedDeadline[session.Slot]) return null;
        return result;
    }

    /// <summary>Observe each incoming packet before core dispatch. A new packet ends an older
    /// uncompleted correlation; timeout only bounds storage, never proves transaction completion.</summary>
    public void ObserveIncoming(SessionKey session, TSPlayer actor, GetDataEventArgs args)
    {
        if (!Healthy || (uint)session.Slot >= 256) return;
        pending[session.Slot] = null;
        try
        {
            if ((byte)args.MsgID != 147 || args.Handled || args.Length - 1 != M7LoadoutPacketSafety.PayloadBytes ||
                args.Msg?.readBuffer is not { } buffer || args.Index < 0 || args.Index > buffer.Length - 4 || Main.netMode != 2) return;
            var binding = current(session.Slot); var player = actor.TPlayer;
            if (binding.Session != session || !ReferenceEquals(binding.Player, actor) || !binding.CanWrite ||
                !actor.IsLoggedIn || actor.Account is null || actor.PlayerData is not { } store ||
                actor.Index != session.Slot || player.whoAmI != session.Slot || !ReferenceEquals(Main.player[session.Slot], player) ||
                player.Loadouts is not { Length: 3 } || player.CurrentLoadoutIndex is < 0 or > 2 ||
                !Valid(player.armor, player.dye) || player.Loadouts.Any(l => !Valid(l.Armor, l.Dye))) return;
            int target = buffer[args.Index + 1]; if (target >= 3) return;
            ushort visibility = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(args.Index + 2));
            var armor = new[] { player.armor.ToArray(), player.Loadouts[0].Armor.ToArray(), player.Loadouts[1].Armor.ToArray(), player.Loadouts[2].Armor.ToArray() };
            var dye = new[] { player.dye.ToArray(), player.Loadouts[0].Dye.ToArray(), player.Loadouts[1].Dye.ToArray(), player.Loadouts[2].Dye.ToArray() };
            var sscArmor = SscArrays(store, ArmorStarts, 20); var sscDye = SscArrays(store, DyeStarts, 10);
            if (sscArmor is null || sscDye is null) return;
            pending[session.Slot] = new(session, actor, player, store, player.CurrentLoadoutIndex, target, visibility,
                armor, dye, sscArmor, sscDye, Environment.CurrentManagedThreadId,
                clock.GetTimestamp() + LifetimeSeconds * clock.TimestampFrequency, NativePluginsOnly(), actor.Account.ID);
            Observed = Increment(Observed);
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnSendData(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!Healthy || !args.ContinueExecution || Main.netMode != 2 || args.msgType != 147 || (uint)args.number >= 256 ||
            args.remoteClient != -1 || args.ignoreClient != args.number) return;
        try
        {
            var before = pending[args.number]; pending[args.number] = null;
            if (before is null || args.number2 != before.Target || before.Thread != Environment.CurrentManagedThreadId ||
                clock.GetTimestamp() > before.Deadline) return;
            var binding = current(args.number); var player = before.Player;
            if (binding.Session != before.Session || !ReferenceEquals(binding.Player, before.Actor) || !binding.CanWrite ||
                !ReferenceEquals(Main.player[args.number], player) || !ReferenceEquals(before.Actor.PlayerData, before.Store)) return;
            bool native = player.CurrentLoadoutIndex == before.Target &&
                MatchNative(before.Armor, player.armor, player.Loadouts.Select(l => l.Armor).ToArray(), before) &&
                MatchNative(before.Dye, player.dye, player.Loadouts.Select(l => l.Dye).ToArray(), before);
            var sscArmor = SscArrays(before.Store, ArmorStarts, 20); var sscDye = SscArrays(before.Store, DyeStarts, 10);
            bool ssc = sscArmor is not null && sscDye is not null && MatchSsc(before.SscArmor, sscArmor, before) && MatchSsc(before.SscDye, sscDye, before);
            bool visibility = player.hideVisibleAccessory.Length == 10 && player.hideVisibleAccessory.Select((hidden, slot) =>
                hidden == ((before.Visibility & (1 << slot)) != 0)).All(x => x);
            bool attributed = native && ssc && visibility && before.NativePlugins && NativePluginsOnly() &&
                before.Actor.IsLoggedIn && before.Actor.Account?.ID == before.AccountId;
            string reason = native && ssc && visibility ? "native-forward-boundary-matching-loadout-permutation" : "loadout-forward-does-not-match-complete-transaction";
            if (native && ssc && visibility) Completed = Increment(Completed); else Mismatched = Increment(Mismatched);
            completed[args.number] = new(before.Session, fingerprint, revision = Increment(revision), before.Previous, before.Target,
                native, ssc, visibility, attributed, reason, M5EquipmentContexts.Capture(before.Session, player));
            completedDeadline[args.number] = before.Deadline;
        }
        catch (Exception error) { Fail(error); }
    }

    private static bool MatchNative(Item[][] before, Item[] active, Item[][] loadouts, Pending transaction) =>
        active.SequenceEqual(Expected(before, 0, transaction), ReferenceEqualityComparer.Instance) &&
        Enumerable.Range(0, 3).All(i => loadouts[i].SequenceEqual(Expected(before, i + 1, transaction), ReferenceEqualityComparer.Instance));
    private static bool MatchSsc(NetItem[][] before, NetItem[][] after, Pending transaction) =>
        Enumerable.Range(0, 4).All(i => after[i].SequenceEqual(Expected(before, i, transaction)));
    private static T[] Expected<T>(T[][] before, int position, Pending transaction)
    {
        if (transaction.Previous == transaction.Target) return before[position];
        if (position == 0) return before[transaction.Target + 1];
        if (position == transaction.Previous + 1) return before[0];
        if (position == transaction.Target + 1) return before[transaction.Previous + 1];
        return before[position];
    }
    private static bool Valid(Item[]? armor, Item[]? dye) => armor is { Length: 20 } && dye is { Length: 10 } &&
        armor.All(x => x is not null) && dye.All(x => x is not null);
    private static readonly int[] ArmorStarts = [NetItem.ArmorIndex.Item1, NetItem.Loadout1Armor.Item1, NetItem.Loadout2Armor.Item1, NetItem.Loadout3Armor.Item1];
    private static readonly int[] DyeStarts = [NetItem.DyeIndex.Item1, NetItem.Loadout1Dye.Item1, NetItem.Loadout2Dye.Item1, NetItem.Loadout3Dye.Item1];
    private static NetItem[][]? SscArrays(PlayerData store, int[] starts, int count) => store.inventory is { } data &&
        starts.All(start => start >= 0 && start <= data.Length - count) ? starts.Select(start => data.AsSpan(start, count).ToArray()).ToArray() : null;
    private static bool NativePluginsOnly() => ServerApi.Plugins.All(p => p.Plugin.GetType() == typeof(TShockAPI.TShock) || p.Plugin.GetType() == typeof(AntiCheatPlugin));
    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
    private void Fail(Exception error)
    { failed = true; FailureType = error.GetType().FullName; Dispose(); } // Local observation only; no protection subscriptions removed.
}
