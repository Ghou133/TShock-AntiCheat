using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Protocol326 packet87 creates an empty tile entity after the object's tiles arrive.
/// Core checks only its anchor. Recheck the entire known display object's footprint before
/// entity registration and86 publication. This is current permission BLOCK, never account proof.
/// </summary>
public static class M14ResumeTileEntityPlacementSafety
{
    public const string ContractVersion = "terraria1.4.5.8-326-display-entity-creation-permission-v2";
    public const int Packet = 87, PayloadLength = 5;

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (int)args.MsgID != Packet) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length != PayloadLength || args.Index < 0 || args.Index > buffer.Length - length)
            return Denied("display-entity-create-frame-size-or-bounds-invalid");
        var body = buffer.AsSpan(args.Index, length);
        int x = BinaryPrimitives.ReadInt16LittleEndian(body);
        int y = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
            return Denied("display-entity-create-position-outside-world");

        // Native87 ignores occupied positions, invalid/unregistered types, and changed tiles.
        // Preserve those race/no-op paths; absence or type mismatch does not prove cheating.
        if (TileEntity.ByPosition.ContainsKey(new Point16(x, y)))
            return Allowed("display-entity-create-existing-native-noop");
        var manager = TileEntity.manager;
        if (manager is null || !manager._types.TryGetValue(body[4], out var prototype))
            return Allowed("display-entity-create-unmodeled-or-native-noop");
        var type = prototype.GetType();
        (int Width, int Height) size = type == typeof(TEDisplayDoll) ? (TEDisplayDoll.entityTileWidth, TEDisplayDoll.entityTileHeight)
            : type == typeof(TEHatRack) ? (TEHatRack.entityTileWidth, TEHatRack.entityTileHeight)
            : type == typeof(TEWeaponsRack) ? (3, 3)
            // Native TEItemFrame.Hook_AfterPlacement publishes87 at the top-left of its2x2 tiles.
            : type == typeof(TEItemFrame) ? (2, 2) : (0, 0);
        if (size.Width == 0 || manager.InvalidEntityID(body[4]))
            return Allowed("display-entity-create-unmodeled-or-native-noop");
        try
        {
            if (!prototype.IsTileValidForEntity(x, y))
                return Allowed("display-entity-create-changed-tile-native-noop");
            if (x > Main.maxTilesX - size.Width || y > Main.maxTilesY - size.Height)
                return Denied("display-entity-create-footprint-outside-world");
            int slot = args.Msg!.whoAmI;
            var players = TShockAPI.TShock.Players;
            var actor = (uint)slot < (uint)players.Length ? players[slot] : null;
            if (actor is null || actor.Index != slot)
                return Denied("display-entity-create-current-receiving-player-unavailable");
            return actor.HasBuildPermissionForTileObject(x, y, size.Width, size.Height, false)
                ? Allowed("display-entity-create-current-footprint-allowed")
                : Denied("display-entity-create-current-footprint-denied");
        }
        catch
        {
            return Denied("display-entity-create-current-permission-check-failed");
        }
    }

    private static M5WorldWorkCost Allowed(string reason) => new(NetworkRequestKind.WorldMutation, 1, false, reason);
    private static M5WorldWorkCost Denied(string reason) => new(NetworkRequestKind.WorldMutation, 0, true, reason);
}
