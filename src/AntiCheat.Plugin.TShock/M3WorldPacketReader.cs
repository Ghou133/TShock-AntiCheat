using System.Buffers.Binary;
using AntiCheat.Rules;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public sealed record M3WorldPacket(WorldActionKind Kind, int X, int Y, int TargetId = -1,
    int Type = 0, int Style = 0, int Alternate = 0, int Sender = -1);
public readonly record struct M3WorldReadResult(PacketReadKind Kind, M3WorldPacket? Packet);

/// <summary>Protocol 326 object routes. Sign text is bounded and skipped, never retained as evidence.</summary>
public static class M3WorldPacketReader
{
    public static M3WorldReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (args.MsgID is not (PacketTypes.SignNew or PacketTypes.PlaceObject or PacketTypes.RequestTileEntityInteraction))
            return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || length > ushort.MaxValue - 3 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(args.MsgID, buffer.AsSpan(args.Index, length));
    }

    public static M3WorldReadResult ReadPayload(PacketTypes type, ReadOnlySpan<byte> bytes)
    {
        if (type == PacketTypes.PlaceObject)
            return bytes.Length == 11 ? new(PacketReadKind.Parsed, new(WorldActionKind.ObjectPlacement,
                I16(bytes, 0), I16(bytes, 2), Type: I16(bytes, 4), Style: I16(bytes, 6), Alternate: bytes[8]))
                : new(PacketReadKind.Malformed, null);
        if (type == PacketTypes.RequestTileEntityInteraction)
            return bytes.Length == 5 ? new(PacketReadKind.Parsed, new(WorldActionKind.DisplayEntityInteraction,
                0, 0, BinaryPrimitives.ReadInt32LittleEndian(bytes), Sender: bytes[4]))
                : new(PacketReadKind.Malformed, null);
        if (type != PacketTypes.SignNew) return new(PacketReadKind.Unrelated, null);
        if (bytes.Length is < 9 or > ushort.MaxValue - 3) return new(PacketReadKind.Malformed, null);
        int offset = 6;
        uint textLength = 0;
        bool terminated = false;
        for (int shift = 0; shift < 35 && offset < bytes.Length; shift += 7)
        {
            byte next = bytes[offset++];
            if (shift == 28 && next > 7) return new(PacketReadKind.Malformed, null);
            textLength |= (uint)(next & 127) << shift;
            if ((next & 128) == 0) { terminated = true; break; }
        }
        // The locked MessageBuffer consumes sender and a BitsByte after the BinaryWriter string.
        if (!terminated || textLength > ushort.MaxValue || offset + (long)textLength + 2 != bytes.Length)
            return new(PacketReadKind.Malformed, null);
        return new(PacketReadKind.Parsed, new(WorldActionKind.SignWrite, I16(bytes, 2), I16(bytes, 4), I16(bytes, 0),
            Sender: bytes[offset + (int)textLength]));
    }

    private static short I16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]);
}
