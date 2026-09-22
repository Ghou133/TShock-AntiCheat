using System.Buffers.Binary;
using AntiCheat.Rules;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M4VitalReadResult(PacketReadKind Kind, M4VitalObservation? Packet);

public static class M4VitalPacketReader
{
    public static M4VitalReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int id = (int)args.MsgID;
        if (id is not (4 or 16 or 42 or 117)) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        byte[]? buffer = args.Msg?.readBuffer;
        int maximumLength = id == 117 ? ushort.MaxValue - 3 : 512;
        if (buffer is null || length < 0 || length > maximumLength || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(id, buffer.AsSpan(args.Index, length));
    }

    public static M4VitalReadResult ReadPayload(int id, ReadOnlySpan<byte> payload)
    {
        if (id == 117) return ReadHurt(payload);
        if (id is 16 or 42)
            return payload.Length == 5 ? new(PacketReadKind.Parsed, new(id == 16 ? M4VitalKind.Life : M4VitalKind.Mana,
                payload[0], BinaryPrimitives.ReadInt16LittleEndian(payload[1..]), BinaryPrimitives.ReadInt16LittleEndian(payload[3..])))
                : new(PacketReadKind.Malformed, null);
        if (id != 4) return new(PacketReadKind.Unrelated, null);
        // Fixed head: player, skin, voice, float pitch, hair; then BinaryWriter's bounded 7-bit UTF8 length.
        if (payload.Length is < 37 or > 512) return new(PacketReadKind.Malformed, null);
        int cursor = 8, length = 0, shift = 0;
        bool terminated = false;
        for (int index = 0; index < 5 && cursor < payload.Length; index++)
        {
            byte part = payload[cursor++];
            if (index == 4 && (part & 0xf8) != 0) return new(PacketReadKind.Malformed, null);
            length |= (part & 127) << shift;
            if ((part & 128) == 0) { terminated = true; break; }
            shift += 7;
        }
        // Tail: hair dye 1, accessory flags 2, misc 1, seven RGB colours 21, three permanent/info flag bytes.
        if (!terminated || length < 0 || length > 480 || payload.Length - cursor != length + 28)
            return new(PacketReadKind.Malformed, null);
        int flags = cursor + length + 25;
        return new(PacketReadKind.Parsed, new(M4VitalKind.PermanentUnlocks, payload[0],
            UnlockFlags: payload[flags + 2], TorchFlags: payload[flags + 1], DifficultyFlags: payload[flags]));
    }

    private static M4VitalReadResult ReadHurt(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 7 or > ushort.MaxValue - 3) return new(PacketReadKind.Malformed, null);
        // PlayerDeathReason has one presence byte, five Int16 fields plus other-index/prefix bytes,
        // followed by an optional BinaryWriter UTF8 string. Skip it without allocating or trusting attribution.
        byte fields = payload[1];
        int cursor = 2;
        for (int bit = 0; bit < 7; bit++)
            if ((fields & (1 << bit)) != 0) cursor += bit is 3 or 6 ? 1 : 2;
        if (cursor > payload.Length - 5) return new(PacketReadKind.Malformed, null);
        if ((fields & 128) != 0)
        {
            int length = 0, shift = 0;
            bool terminated = false;
            for (int index = 0; index < 5 && cursor < payload.Length - 5; index++)
            {
                byte part = payload[cursor++];
                if (index == 4 && (part & 0xf8) != 0) return new(PacketReadKind.Malformed, null);
                length |= (part & 127) << shift;
                if ((part & 128) == 0) { terminated = true; break; }
                shift += 7;
            }
            if (!terminated || length < 0 || length > payload.Length - cursor - 5)
                return new(PacketReadKind.Malformed, null);
            cursor += length;
        }
        if (payload.Length - cursor != 5) return new(PacketReadKind.Malformed, null);
        return new(PacketReadKind.Parsed, new(M4VitalKind.HurtDeclaration, payload[0],
            Pvp: (payload[cursor + 3] & 2) != 0,
            HurtDamage: BinaryPrimitives.ReadInt16LittleEndian(payload[cursor..]),
            HurtCooldown: unchecked((sbyte)payload[cursor + 4])));
    }
}
