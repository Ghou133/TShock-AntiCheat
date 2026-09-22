using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M5WorldWorkCost(NetworkRequestKind Kind, int WorkUnits,
    bool RejectMalformed, string Reason);

/// <summary>
/// Constant-time admission cost before the existing TShock parsers allocate paths or tile grids.
/// This estimates requested work; it neither traverses circuits nor changes core permission decisions.
/// </summary>
public static class M5WorldCost
{
    public static M5WorldWorkCost Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int type = (byte)args.MsgID;
        var fallback = Default(type);
        if (!verifiedRuntime || !IsCovered(type)) return fallback;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || length > ushort.MaxValue - 3 || args.Index < 0 || args.Index > buffer.Length - length)
            return fallback with { RejectMalformed = true, Reason = "world-work-frame-bounds-invalid" };
        return ReadPayload(type, buffer.AsSpan(args.Index, length), Main.maxTilesX, Main.maxTilesY);
    }

    public static M5WorldWorkCost ReadPayload(int type, ReadOnlySpan<byte> body, int worldWidth, int worldHeight)
    {
        var result = Default(type);
        M5WorldWorkCost Bad(string reason) => result with { RejectMalformed = true, Reason = reason };
        bool InWorld(int x, int y) => x >= 0 && y >= 0 && x < worldWidth && y < worldHeight;
        if (!IsCovered(type)) return result;
        if (type == 109)
        {
            if (body.Length != 9) return Bad("mass-wire-frame-size-invalid");
            int x = I16(body, 0), y = I16(body, 2), endX = I16(body, 4), endY = I16(body, 6);
            // Target MassWireOperationInner visits two legs and the endpoint once. Facing changes
            // their order, never the count. Do not allocate or charge the whole bounding rectangle.
            int cells = Math.Abs(endX - x) + Math.Abs(endY - y) + 1;
            if (worldWidth > 0 && worldHeight > 0 && (!InWorld(x, y) || !InWorld(endX, endY)))
                return Bad("mass-wire-endpoint-outside-world");
            return result with { WorkUnits = cells, Reason = "native-mass-wire-l-path-cells" };
        }
        if (type == 20)
        {
            if (body.Length < 7) return Bad("tile-rectangle-header-incomplete");
            int x = I16(body, 0), y = I16(body, 2), width = body[4], height = body[5];
            int cells = width * height;
            // Every target cell begins with three BitsByte fields, even an empty cell. This lower
            // bound rejects an impossible grid before core allocates/reads it; the core still parses
            // optional color, frame, wall and liquid fields and retains its normal authorization.
            if (7 + cells * 3 > body.Length) return Bad("tile-rectangle-minimum-payload-incomplete");
            if (worldWidth > 0 && worldHeight > 0 &&
                (x < 0 || y < 0 || x > worldWidth - width || y > worldHeight - height))
                return Bad("tile-rectangle-outside-world");
            return result with { WorkUnits = cells, Reason = "native-tile-rectangle-cell-count" };
        }
        int exactLength = type switch { 48 => 6, 59 => 4, 87 => 5, 89 or 123 => 9, 124 => 11, 156 => 6, _ => -1 };
        // Display-doll121 has a native variable ReadData body. Seven bytes is only its fixed header;
        // entity-aware semantic validation remains in the existing core handler.
        if (exactLength >= 0 && body.Length != exactLength || type == 121 && body.Length < 7)
            return Bad("world-entity-or-switch-frame-size-invalid");
        if (type is 48 or 59 or 87 or 89 or 123 or 156 && worldWidth > 0 && worldHeight > 0 &&
            !InWorld(I16(body, 0), I16(body, 2)))
            return Bad("world-entity-or-switch-position-outside-world");
        return result;
    }

    private static short I16(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadInt16LittleEndian(body[offset..]);
    private static bool IsCovered(int type) => type is 20 or 48 or 59 or 87 or 89 or 109 or 121 or 123 or 124 or 156;
    private static M5WorldWorkCost Default(int type) => type switch
    {
        // A switch can activate an arbitrary existing circuit. Charge admission/broadcast work;
        // one input is not a claim that its entire wire graph has been measured or capped.
        59 => new(NetworkRequestKind.BroadcastAmplification, 0, false, "switch-activation-admission"),
        121 or 124 => new(NetworkRequestKind.BroadcastAmplification, 1, false, "display-entity-sync-admission"),
        20 or 109 => new(NetworkRequestKind.WorldMutation, 0, false, "world-shape-unparsed"),
        48 or 87 or 89 or 123 or 156 => new(NetworkRequestKind.WorldMutation, 1, false, "single-world-mutation-admission"),
        17 or 32 => new(NetworkRequestKind.WorldMutation, 0, false, "existing-world-mutation-admission"),
        5 or 27 => new(NetworkRequestKind.EntitySync, 0, false, "existing-entity-sync-admission"),
        _ => new(NetworkRequestKind.Ordinary, 0, false, "ordinary-packet-admission")
    };
}
