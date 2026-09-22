using AntiCheat.Rules;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>Protocol326 mannequin framing and slot/command admission before TShock indexes its arrays.
/// This is input safety only; it never establishes account misconduct or item provenance.</summary>
public static class M13DisplayEntityPacketSafety
{
    public const int EquipmentSlots = 9, DyeSlots = 9, MiscSlots = 1;
    public const string ContractVersion = "terraria1.4.5.8-326-display-doll-slots-v1";

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (int)args.MsgID != 121) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return Bad("display-doll-frame-bounds-invalid");
        return ReadPayload(buffer.AsSpan(args.Index, length));
    }

    public static M5WorldWorkCost ReadPayload(ReadOnlySpan<byte> body)
    {
        // sender:byte, entity:int32, slot:byte, command:byte.
        if (body.Length < 7) return Bad("display-doll-frame-size-invalid");
        int slot = body[5], command = body[6];
        if (command == 2)
            // Native pose reads a byte and ignores itemIndex. A large ignored slot is legitimate.
            return body.Length == 8 ? Safe() : Bad("display-doll-pose-frame-size-invalid");
        if (body.Length != 12) return Bad("display-doll-item-frame-size-invalid");
        int capacity = command switch { 0 => EquipmentSlots, 1 => DyeSlots, 3 => MiscSlots, _ => 0 };
        // The native fallback treats undefined commands as equipment, but TShock's switch
        // returns before the item/region handler. Do not allow that alias to bypass protection.
        if (capacity == 0) return Bad("display-doll-command-bypasses-item-handler");
        if (slot >= capacity) return Bad("display-doll-slot-out-of-range");
        return Safe();
    }

    private static M5WorldWorkCost Safe() => new(NetworkRequestKind.WorldMutation, 1, false, "display-doll-structure-safe");
    private static M5WorldWorkCost Bad(string reason) => new(NetworkRequestKind.WorldMutation, 0, true, reason);
}
