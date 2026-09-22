using System.Buffers.Binary;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public enum M2PacketKind { Emoji, PlayerSlot, Tile, Liquid, ChestOpen, ChestItem, WorldItemDrop, WorldItemDespawn, PlayerUpdate, ProjectileNew, ProjectileDestroy, Buff, Heal }
public sealed record M2Packet(M2PacketKind Kind, byte[] Payload, byte MessageId = 0);
public readonly record struct M2ReadResult(PacketReadKind Kind, M2Packet? Packet);

/// <summary>Target protocol 326 framing. Index is past message id; Length includes that id.</summary>
public static class M2PacketReader
{
    public static M2ReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        var kind = args.MsgID switch
        {
            PacketTypes.Emoji => M2PacketKind.Emoji, PacketTypes.PlayerSlot => M2PacketKind.PlayerSlot,
            PacketTypes.Tile => M2PacketKind.Tile, PacketTypes.LiquidSet => M2PacketKind.Liquid,
            PacketTypes.ChestGetContents => M2PacketKind.ChestOpen, PacketTypes.ChestItem => M2PacketKind.ChestItem,
            PacketTypes.ItemDrop => M2PacketKind.WorldItemDrop, PacketTypes.UpdateItemDrop => M2PacketKind.WorldItemDrop,
            PacketTypes.SyncItemDespawn => M2PacketKind.WorldItemDespawn,
            PacketTypes.PlayerUpdate => M2PacketKind.PlayerUpdate, PacketTypes.ProjectileNew => M2PacketKind.ProjectileNew,
            PacketTypes.ProjectileDestroy => M2PacketKind.ProjectileDestroy,
            PacketTypes.PlayerAddBuff => M2PacketKind.Buff, PacketTypes.PlayerHealOther => M2PacketKind.Heal,
            _ => (M2PacketKind?)null
        };
        if (kind is null) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int count = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || count < 0 || count > 128 || args.Index < 0 || args.Index > buffer.Length - count)
            return new(PacketReadKind.Malformed, null);
        var bytes = buffer.AsSpan(args.Index, count);
        int expected = kind switch
        {
            M2PacketKind.Emoji => 2, M2PacketKind.PlayerSlot => 9, M2PacketKind.Tile => 8,
            M2PacketKind.Liquid => 6, M2PacketKind.ChestOpen => 4, M2PacketKind.ChestItem => 8,
            M2PacketKind.WorldItemDrop => WorldItemDropSize(bytes),
            M2PacketKind.WorldItemDespawn => 2,
            M2PacketKind.ProjectileDestroy => 12, M2PacketKind.Buff => 7, M2PacketKind.Heal => 3,
            M2PacketKind.PlayerUpdate => PlayerUpdateSize(bytes),
            M2PacketKind.ProjectileNew => ProjectileSize(bytes), _ => -1
        };
        return expected < 0 || count != expected ? new(PacketReadKind.Malformed, null)
            : new(PacketReadKind.Parsed, new(kind.Value, bytes.ToArray(), (byte)args.MsgID));
    }

    private static int PlayerUpdateSize(ReadOnlySpan<byte> bytes) => bytes.Length < 14 ? -1
        : 14 + ((bytes[2] & 4) != 0 ? 8 : 0) + ((bytes[2] & 128) != 0 ? 2 : 0)
            + ((bytes[3] & 64) != 0 ? 16 : 0) + ((bytes[4] & 32) != 0 ? 8 : 0);

    private static int ProjectileSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 23) return -1;
        byte first = bytes[22];
        bool secondPresent = (first & 4) != 0;
        if (secondPresent && bytes.Length < 24) return -1;
        int count = 23 + (secondPresent ? 1 : 0);
        if ((first & 1) != 0) count += 4;
        if ((first & 2) != 0) count += 4;
        if ((first & 8) != 0) count += 2;
        if ((first & 16) != 0) count += 2;
        if ((first & 32) != 0) count += 4;
        if ((first & 64) != 0) count += 2;
        if (secondPresent && (bytes[23] & 1) != 0) count += 4;
        return count;
    }

    /// <summary>
    /// Validates the exact projectile body selected by the current GetData
    /// request and returns an immutable copy. A larger backing buffer or a
    /// trailing byte is not part of the frame and must not be admitted.
    /// </summary>
    public static bool TryReadProjectilePayload(ReadOnlySpan<byte> body, out byte[] payload)
    {
        int expected = ProjectileSize(body);
        if (expected < 0 || expected != body.Length)
        {
            payload = [];
            return false;
        }
        payload = body.ToArray();
        return true;
    }

    // Target GetDataHandlers.HandleItemDrop consumes id(2), position(8), velocity(8),
    // stacks(2), prefix(1), flags(1), type(2), then conditionally shimmer(5) and
    // enemyGrabDelayTime(1). Unknown flag bits are ignored by the locked target and
    // therefore do not change its read length.
    private static int WorldItemDropSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24) return -1;
        byte flags = bytes[21];
        return 24 + ((flags & 4) != 0 ? 5 : 0) + ((flags & 8) != 0 ? 1 : 0);
    }

    public static short Int16(byte[] payload, int offset) => BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset, 2));
    public static int Int32(byte[] payload, int offset) => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
    public static float Single(byte[] payload, int offset) => BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(offset, 4));
}
