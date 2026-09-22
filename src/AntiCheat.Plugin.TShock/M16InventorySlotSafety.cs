using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>Packet5 reserves 200 wire IDs per bank, but the native receiver writes to the
/// currently allocated array. Check that actual address before the core or native setter.
/// Air still performs a slot assignment. No item value, prefix, origin or account verdict.</summary>
public static class M16InventorySlotSafety
{
    public const string ContractVersion = "terraria1.4.5.8-326-inventory-mapped-slot-v1";
    private static long checks, rejected;
    public static long Checks => Interlocked.Read(ref checks);
    public static long Rejected => Interlocked.Read(ref rejected);

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || args.MsgID != PacketTypes.PlayerSlot) return null;
        var parsed = PlayerSlotPacketReader.Read(args.MsgID, args.Msg?.readBuffer, args.Index, args.Length, true);
        var result = parsed.Packet is { } packet ? CheckAddress(args.Msg?.whoAmI ?? -1, packet) : Bad("inventory-slot-frame-size-invalid");
        Increment(ref checks); if (result.RejectMalformed) Increment(ref rejected);
        return result;
    }

    private static M5WorldWorkCost CheckAddress(int sender, PlayerSlotPacket packet)
    {
        // Keep the existing independently-qualified sender predicate reachable, even if the
        // spoofed packet also names a bad inventory address. Never relabel that proof as safety.
        if (packet.ClaimedPlayerSlot != sender) return Safe("inventory-slot-sender-contract-deferred");
        if (packet.InventorySlot < 0 || packet.InventorySlot >= PlayerItemSlotID.Count)
            return Bad("inventory-slot-outside-wire-layout");
        if (Main.player is not { } players || (uint)sender >= (uint)players.Length || players[sender] is not { } player)
            return Safe("inventory-slot-player-layout-unavailable");
        // Trash is a scalar property, not an array. Its air and non-air assignments are native.
        if (packet.InventorySlot == PlayerItemSlotID.TrashItem) return Safe("inventory-slot-trash-scalar");
        try
        {
            var reference = new PlayerItemSlotID.SlotReference(player, packet.InventorySlot);
            if (!reference.TryGetArraySlot(out var array, out int index) || array is null)
                return Safe("inventory-slot-player-layout-unavailable");
            return (uint)index < (uint)array.Length ? Safe("inventory-slot-native-address-valid") :
                Bad("inventory-slot-outside-mapped-array");
        }
        catch (Exception error) when (error is NullReferenceException or IndexOutOfRangeException)
        {
            // A replaced/broken host layout is an integrity gap, not a guessed index policy.
            // The independent verified frame/wire domain checks remain available.
            return Safe("inventory-slot-player-layout-unavailable");
        }
    }

    private static M5WorldWorkCost Safe(string reason) => new(NetworkRequestKind.EntitySync, 0, false, reason);
    private static M5WorldWorkCost Bad(string reason) => new(NetworkRequestKind.EntitySync, 0, true, reason);
    private static void Increment(ref long value)
    {
        long prior;
        do { prior = Interlocked.Read(ref value); if (prior == long.MaxValue) return; }
        while (Interlocked.CompareExchange(ref value, prior + 1, prior) != prior);
    }
}
