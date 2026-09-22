using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// The three protocol326 display-placement requests transfer exactly one item. Invalid stack
/// claims are blocked before both native writes and native missing-object return-item branches.
/// Weapon-rack123 additionally lacks a core permission handler, so its present target gets the
/// ordinary bounded build/region check. Neither rejection is account-cheating evidence.
/// </summary>
public static class M14LObjectPlacementSafety
{
    public const string ContractVersion = "terraria1.4.5.8-326-single-display-placement-v1";
    public const int PayloadLength = 9;

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int packet = (int)args.MsgID;
        if (!verifiedRuntime || packet is not (89 or 123 or 133)) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length != PayloadLength || args.Index < 0 || args.Index > buffer.Length - length)
            return Denied("display-placement-frame-size-or-bounds-invalid");
        var body = buffer.AsSpan(args.Index, length);
        int x = BinaryPrimitives.ReadInt16LittleEndian(body);
        int y = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
        // Preserve the existing89/123 pre-parser bounds gate when taking ownership of their
        // admission result; apply the same safe check to newly covered133.
        if (Main.maxTilesX > 0 && Main.maxTilesY > 0 &&
            (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY))
            return Denied("display-placement-position-outside-world");
        if (BinaryPrimitives.ReadInt16LittleEndian(body[7..]) != 1)
            return Denied("display-placement-must-transfer-single-item");

        // Item frames89 and food platters133 retain their existing TShock/Bouncer handlers.
        // A disappeared or different target is a real concurrent-removal return-item case;
        // never turn current object absence into a cheating proof or suppress that native path.
        if (packet != 123 || !TileEntity.TryGetAt<TEWeaponsRack>(x, y, out var rack))
            return Allowed("display-single-placement-existing-core-path");
        int slot = args.Msg!.whoAmI;
        var players = TShockAPI.TShock.Players;
        var actor = (uint)slot < (uint)players.Length ? players[slot] : null;
        if (actor is null || actor.Index != slot)
            return Denied("weapon-rack-current-receiving-player-unavailable");
        try
        {
            return actor.HasBuildPermissionForTileObject(rack.Position.X, rack.Position.Y, 3, 3, false)
                ? Allowed("weapon-rack-current-build-permission-allowed")
                : Denied("weapon-rack-current-build-permission-denied");
        }
        catch
        {
            return Denied("weapon-rack-build-permission-check-failed");
        }
    }

    private static M5WorldWorkCost Allowed(string reason) => new(NetworkRequestKind.WorldMutation, 1, false, reason);
    private static M5WorldWorkCost Denied(string reason) => new(NetworkRequestKind.WorldMutation, 0, true, reason);
}
