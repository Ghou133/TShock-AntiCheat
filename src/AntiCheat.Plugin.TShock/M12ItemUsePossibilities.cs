using System.Collections.Immutable;
using AntiCheat.Core;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public enum M12PossibleUseOrigin { AcceptedServerItemAtClientUseIntent, SscInventoryExportAttempt, MessageArrivalCurrentItem }
public sealed record M12PossibleUseSource(int ItemType, int InventorySlot, M12PossibleUseOrigin Origin);
public sealed record M12ItemUsePossibilitySnapshot(ImmutableArray<M12PossibleUseSource> Observed, bool LostEntries)
{
    // The client does not acknowledge its animation starter or consumption of an SSC5 prefix.
    // The conservative union ALWAYS contains an unobserved native starter. Neither a full ring,
    // silence, expiry nor a queued output narrows that possibility into evidence of cheating.
    public bool Complete => false;
    public bool UnobservedStartPossible => true;
}

public sealed class M12ItemUsePossibilities(TimeProvider clock)
{
    public const int Capacity = 16;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    private sealed record Entry(M12PossibleUseSource Source, long Timestamp);
    private readonly Entry?[] entries = new Entry?[Capacity];
    private int cursor;
    private bool lost;
    public void Observe(int type, int slot, M12PossibleUseOrigin origin)
    {
        Expire(); var source = new M12PossibleUseSource(type, slot, origin);
        if (type < 0 || type >= Terraria.ID.ItemID.Count || slot is < 0 or >= 59) { lost = true; return; }
        for (int index = 0; index < entries.Length; index++)
            if (entries[index]?.Source == source) { entries[index] = new(source, clock.GetTimestamp()); return; }
        if (entries[cursor] is not null) lost = true;
        entries[cursor] = new(source, clock.GetTimestamp()); cursor = (cursor + 1) % Capacity;
    }
    public M12ItemUsePossibilitySnapshot Capture()
    { Expire(); return new(entries.Where(x => x is not null).Select(x => x!.Source).ToImmutableArray(), lost); }
    private void Expire()
    {
        long now = clock.GetTimestamp();
        for (int index = 0; index < entries.Length; index++)
            if (entries[index] is { } entry && clock.GetElapsedTime(entry.Timestamp, now) >= Retention)
            { entries[index] = null; lost = true; }
    }
}

public sealed partial class M5ProgressionContexts
{
    private readonly TimeProvider useClock = timeProvider ?? TimeProvider.System;
    private bool useSourceObservationFailed;

    private State? CurrentUseState(SessionKey session, TSPlayer actor)
    {
        if (useSourceObservationFailed || !installed || Main.netMode != 2 || updateThread != Environment.CurrentManagedThreadId ||
            (uint)session.Slot >= states.Length || states[session.Slot] is not { } state || state.Session != session) return null;
        var target = lookup?.Invoke(session.Slot);
        return target?.Session is { Revoked: false } current && current.Key == session && current.AccountId == actor.Account?.ID &&
            actor.IsLoggedIn && actor.Index == session.Slot && ReferenceEquals(target?.Player, actor) && ReferenceEquals(Main.player[session.Slot], actor.TPlayer)
            ? state : null;
    }

    private void ObserveUseIntent(GetDataEventArgs args, SessionKey session, TSPlayer actor)
    {
        if (args.MsgID != PacketTypes.PlayerUpdate || args.Handled) return;
        try
        {
            if (M2PacketReader.Read(args, true).Packet is not { } packet || packet.Payload[0] != session.Slot ||
                (packet.Payload[1] & 32) == 0 || packet.Payload[5] >= 59 || CurrentUseState(session, actor) is not { } state) return;
            int slot = packet.Payload[5];
            (state.UsePossibilities ??= new(useClock)).Observe(actor.TPlayer.inventory[slot].type, slot,
                M12PossibleUseOrigin.AcceptedServerItemAtClientUseIntent);
        }
        catch { useSourceObservationFailed = true; }
    }

    private void ObserveItemExport(HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.msgType != 5 || !args.ContinueExecution || !Main.ServerSideCharacter || args.number is < 0 or >= 256 ||
            args.number2 is < 0 or >= 59 || args.number2 != (int)args.number2 ||
            args.remoteClient >= 0 && args.remoteClient != args.number || args.ignoreClient == args.number) return;
        try
        {
            var target = lookup?.Invoke(args.number);
            if (target?.Session is not { } current || target?.Player is not { } actor || CurrentUseState(current.Key, actor) is not { } state) return;
            int slot = (int)args.number2;
            (state.UsePossibilities ??= new(useClock)).Observe(actor.TPlayer.inventory[slot].type, slot,
                M12PossibleUseOrigin.SscInventoryExportAttempt);
        }
        catch { useSourceObservationFailed = true; }
    }

    public M12ItemUsePossibilitySnapshot CaptureUsePossibilities(SessionKey session, TSPlayer actor, bool recordArrival = false)
    {
        try
        {
            if (CurrentUseState(session, actor) is not { } state) return new([], true);
            var sources = state.UsePossibilities ??= new(useClock);
            if (recordArrival && actor.TPlayer.selectedItem is >= 0 and < 59)
                sources.Observe(actor.TPlayer.HeldItem.type, actor.TPlayer.selectedItem, M12PossibleUseOrigin.MessageArrivalCurrentItem);
            return sources.Capture();
        }
        catch { useSourceObservationFailed = true; return new([], true); }
    }
}
