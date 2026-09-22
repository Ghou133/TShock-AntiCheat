using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// The target packet85 reader retains Item references for every supplied source slot. A repeated
/// slot can therefore survive the first Swap into a chest and be inserted a second time. Validate
/// the whole source list before that reader, any chest hook, transfer, or inventory restoration.
/// This is a request-local safety BLOCK, never an assertion about acquisition or an account verdict.
/// </summary>
public static class M10NaturalQuickStackSafety
{
    public const int MaximumSources = 400; // Locked QuickStacking's three source scratch arrays.
    public const int MaximumSlotDomain = short.MaxValue + 1;
    public const string Version = "1.0.0";
    private static long checks, rejected, duplicateRejected;
    public static long Checks => Interlocked.Read(ref checks);
    public static long Rejected => Interlocked.Read(ref rejected);
    public static long DuplicateRejected => Interlocked.Read(ref duplicateRejected);

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (byte)args.MsgID != 85) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        var result = Invalid("quick-stack-frame-bounds-invalid");
        if (buffer is not null && length >= 0 && length <= ushort.MaxValue - 3 &&
            args.Index >= 0 && args.Index <= buffer.Length - length)
        {
            var body = buffer.AsSpan(args.Index, length);
            result = ReadPayload(body, PlayerItemSlotID.Count);
            if (!result.RejectMalformed)
            {
                int sender = args.Msg!.whoAmI;
                var players = Main.player;
                var player = players is not null && (uint)sender < (uint)players.Length ? players[sender] : null;
                result = CheckMappedStorage(body, player, result);
            }
        }
        Increment(ref checks);
        if (result.RejectMalformed)
        {
            Increment(ref rejected);
            if (result.Reason == "quick-stack-repeated-source-slot") Increment(ref duplicateRejected);
        }
        return result;
    }

    public static M5WorldWorkCost ReadPayload(ReadOnlySpan<byte> body, int slotCount)
    {
        if (body.Length < 5) return Invalid("quick-stack-header-incomplete");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (count < 0 || count > MaximumSources) return Invalid("quick-stack-source-capacity-invalid");
        if (body.Length != 5 + count * 2) return Invalid("quick-stack-source-payload-size-invalid");
        bool layoutKnown = slotCount is > 0 and <= MaximumSlotDomain;
        // Fixed 4 KiB stack storage covers every nonnegative Int16 index, even if a host has
        // invalidated the runtime layout. Unknown layout cannot erase an independent duplicate.
        Span<ulong> seen = stackalloc ulong[MaximumSlotDomain / 64];
        seen.Clear();
        for (int i = 0; i < count; i++)
        {
            int slot = BinaryPrimitives.ReadInt16LittleEndian(body[(4 + i * 2)..]);
            if (slot < 0 || layoutKnown && slot >= slotCount) return Invalid("quick-stack-source-slot-outside-layout");
            int word = slot / 64;
            ulong bit = 1UL << (slot % 64);
            if ((seen[word] & bit) != 0) return Invalid("quick-stack-repeated-source-slot");
            seen[word] |= bit;
        }
        // ReadBoolean consumes the final byte. Its native nonzero=true semantics are preserved.
        // No item type, prefix, stack, history, world-progress flag, or current chest is consulted.
        return new(NetworkRequestKind.WorldMutation, count, false, layoutKnown
            ? "quick-stack-unique-source-admission" : "quick-stack-slot-layout-unknown");
    }

    private static M5WorldWorkCost Invalid(string reason) => new(NetworkRequestKind.WorldMutation, 0, true, reason);

    private static M5WorldWorkCost CheckMappedStorage(ReadOnlySpan<byte> completeBody, Player? player, M5WorldWorkCost result)
    {
        if (player is null) return result with { Reason = "quick-stack-sender-storage-unknown" };
        // Network IDs reserve 200 entries per bank, whereas the actual bank array may have only
        // 40. The native mapping is the authority for this array access, independently of assets.
        // Never dereference the item or use a current SSC value to prove acquisition legitimacy.
        int count = BinaryPrimitives.ReadInt32LittleEndian(completeBody);
        for (int i = 0; i < count; i++)
        {
            int id = BinaryPrimitives.ReadInt16LittleEndian(completeBody[(4 + i * 2)..]);
            if (id == PlayerItemSlotID.TrashItem) continue; // Native SlotReference handles this scalar separately.
            try
            {
                var reference = new PlayerItemSlotID.SlotReference(player, id);
                if (!reference.TryGetArraySlot(out var items, out int index) || items is null)
                    return result with { Reason = "quick-stack-sender-storage-unknown" };
                if ((uint)index >= (uint)items.Length) return Invalid("quick-stack-source-outside-mapped-array");
            }
            catch (Exception error) when (error is NullReferenceException or IndexOutOfRangeException)
            {
                // A missing/replaced host layout is an integrity gap, not player cheating or a
                // guessed block. The already-complete frame/duplicate checks remain independent.
                return result with { Reason = "quick-stack-sender-storage-unknown" };
            }
        }
        return result;
    }

    private static void Increment(ref long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref value); if (previous == long.MaxValue) return; }
        while (Interlocked.CompareExchange(ref value, previous + 1, previous) != previous);
    }
}
