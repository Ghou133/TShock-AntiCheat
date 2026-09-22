using AntiCheat.Rules;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>Admission before TShock's SSC swap and the native loadout/visibility transaction.</summary>
public static class M7LoadoutPacketSafety
{
    public const int PayloadBytes = 4; // sender byte + loadout byte + native UInt16 accessory visibility

    public static M5WorldWorkCost? Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (!verifiedRuntime || (byte)args.MsgID != 147) return null;
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        bool malformed = length != PayloadBytes || buffer is null || args.Index < 0 || args.Index > buffer.Length - PayloadBytes;
        // Sender normalization, target index bounds, disabled-player behavior, SSC swaps and
        // native effective equipment remain with their audited original handlers. No ban proof.
        return new(NetworkRequestKind.EntitySync, 1, malformed,
            malformed ? "loadout-transaction-frame-incomplete-or-trailing" : "complete-native-loadout-transaction-frame");
    }
}
