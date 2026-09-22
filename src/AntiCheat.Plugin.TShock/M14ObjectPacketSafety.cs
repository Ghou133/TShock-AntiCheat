using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Protocol326 hat-rack commits recheck the same bounded build/region permission as opening
/// the object. Opening122 is not a durable grant to write124 after permission changes.
/// This is a current-action BLOCK only, with no account proof, inventory ledger or rollback.
/// </summary>
public static class M14ObjectPacketSafety
{
    public const string ContractVersion = "terraria1.4.5.8-326-hat-rack-write-permission-v1";
    public const int Packet = 124, PayloadLength = 11;

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (int)args.MsgID != Packet) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length != PayloadLength || args.Index < 0 || args.Index > buffer.Length - length)
            return Denied("hat-rack-frame-size-or-bounds-invalid");

        var body = buffer.AsSpan(args.Index, length);
        int wireSlot = body[5];
        int id = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        // Native124 already consumes and discards all five item bytes for these cases.
        // Do not turn a missing/changed entity or the existing safe tail slots into a denial.
        if (wireSlot >= 4 || !TileEntity.TryGet<TEHatRack>(id, out var rack))
            return Allowed("hat-rack-existing-native-noop");

        // The body sender is overwritten by native124 and is not an identity root.
        // Resolve both the physical receiving player and current ID target for this call;
        // no opening-history, object-reference or account authorization is cached.
        int slot = args.Msg!.whoAmI;
        var actor = (uint)slot < (uint)TShockAPI.TShock.Players.Length ? TShockAPI.TShock.Players[slot] : null;
        if (actor is null || actor.Index != slot)
            return Denied("hat-rack-current-receiving-player-unavailable");

        // Keep TShock's native permission hook, global/spawn restrictions and per-cell
        // region semantics. A hat rack is exactly3x4: at most12 existing permission calls.
        try
        {
            return actor.HasBuildPermissionForTileObject(rack.Position.X, rack.Position.Y,
                TEHatRack.entityTileWidth, TEHatRack.entityTileHeight, false)
                ? Allowed("hat-rack-current-build-permission-allowed")
                : Denied("hat-rack-current-build-permission-denied");
        }
        catch
        {
            // A failed foundational permission provider cannot grant a world-object write.
            // Contain only this request, never convert the provider fault to account evidence.
            return Denied("hat-rack-build-permission-check-failed");
        }
    }

    private static M5WorldWorkCost Allowed(string reason) => new(NetworkRequestKind.BroadcastAmplification, 1, false, reason);
    private static M5WorldWorkCost Denied(string reason) => new(NetworkRequestKind.BroadcastAmplification, 0, true, reason);
}
