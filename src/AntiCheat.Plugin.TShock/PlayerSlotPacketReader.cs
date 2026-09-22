using System.Buffers.Binary;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public enum PacketReadKind { Unrelated, UnknownRuntime, Malformed, Parsed }
public sealed record PlayerSlotPacket(byte ClaimedPlayerSlot, short InventorySlot, short Stack, byte Prefix, short ItemType, byte Flags);
public readonly record struct PacketReadResult(PacketReadKind Kind, PlayerSlotPacket? Packet);

public static class PlayerSlotPacketReader
{
    // 1.4.5.6 HandlePlayerSlot reads 1+2+2+1+2+1 bytes, including favorited/blocked flags.
    public const int PayloadBytes = 9;

    public static PacketReadResult Read(PacketTypes type, byte[]? buffer, int index, int length, bool exactBaseline)
    {
        if (type != PacketTypes.PlayerSlot)
            return new(PacketReadKind.Unrelated, null);
        if (!exactBaseline)
            return new(PacketReadKind.UnknownRuntime, null);
        // TSAPI Length includes message type; Index already points after it.
        if (buffer is null || index < 0 || length != PayloadBytes + 1 || index > buffer.Length - PayloadBytes)
            return new(PacketReadKind.Malformed, null);
        var bytes = buffer.AsSpan(index, PayloadBytes);
        return new(PacketReadKind.Parsed, new(bytes[0], BinaryPrimitives.ReadInt16LittleEndian(bytes[1..]),
            BinaryPrimitives.ReadInt16LittleEndian(bytes[3..]), bytes[5], BinaryPrimitives.ReadInt16LittleEndian(bytes[6..]), bytes[8]));
    }

    public static void PreserveOrBlock(GetDataEventArgs args, bool block)
    {
        // No path writes false: other plugins and TShock retain their cancellation.
        if (block) args.Handled = true;
    }
}
