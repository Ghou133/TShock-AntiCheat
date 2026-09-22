using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Protocol326 packet156 overwrites a kite/critter anchor's item and respawns its leashed entity.
/// Recheck current one-cell build permission before that write. This is BLOCK, never account proof.
/// </summary>
public static class M15LeashedAnchorItemSafety
{
    public const string ContractVersion = "terraria1.4.5.8-326-leashed-anchor-item-permission-v1";
    public const int Packet = 156, PayloadLength = 6;

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (int)args.MsgID != Packet) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length != PayloadLength || args.Index < 0 || args.Index > buffer.Length - length)
            return Denied("leashed-anchor-item-frame-size-or-bounds-invalid");
        var body = buffer.AsSpan(args.Index, length);
        int x = BinaryPrimitives.ReadInt16LittleEndian(body), y = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
            return Denied("leashed-anchor-item-position-outside-world");
        // Missing or replaced objects are native no-ops. Only exact native object classes are audited.
        if (!TileEntity.TryGetAt<TELeashedEntityAnchorWithItem>(x, y, out var anchor))
            return Allowed("leashed-anchor-item-existing-native-noop");
        if (anchor.GetType() != typeof(TEKiteAnchor) && anchor.GetType() != typeof(TECritterAnchor))
            return Allowed("leashed-anchor-item-unmodeled-object");
        int receivingSlot = args.Msg!.whoAmI;
        var players = TShockAPI.TShock.Players;
        var actor = (uint)receivingSlot < (uint)players.Length ? players[receivingSlot] : null;
        if (actor is null || actor.Index != receivingSlot)
            return Denied("leashed-anchor-item-current-receiving-player-unavailable");
        try
        {
            return actor.HasBuildPermission(x, y, false)
                ? Allowed("leashed-anchor-item-current-build-permission-allowed")
                : Denied("leashed-anchor-item-current-build-permission-denied");
        }
        catch { return Denied("leashed-anchor-item-build-permission-check-failed"); }
    }

    private static M5WorldWorkCost Allowed(string reason) => new(NetworkRequestKind.WorldMutation, 1, false, reason);
    private static M5WorldWorkCost Denied(string reason) => new(NetworkRequestKind.WorldMutation, 0, true, reason);
}
