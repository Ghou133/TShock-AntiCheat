using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M3CheatReadResult(PacketReadKind Kind, NpcServerAuthorityObservation? Packet);

/// <summary>Exact target-326 fixed frames; only the two audited NPC server-authority messages.</summary>
public static class M3CheatPacketReader
{
    public static M3CheatReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int id = (int)args.MsgID;
        if (id is not (100 or 153)) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(id, buffer.AsSpan(args.Index, length));
    }

    public static M3CheatReadResult ReadPayload(int messageId, ReadOnlySpan<byte> payload)
    {
        if (messageId == 153)
            return payload.Length == 3 ? new(PacketReadKind.Parsed,
                new(NpcServerAuthorityMessage.DebuffDamage, payload[0], BinaryPrimitives.ReadInt16LittleEndian(payload[1..])))
                : new(PacketReadKind.Malformed, null);
        if (messageId != 100) return new(PacketReadKind.Unrelated, null);
        return payload.Length == 20 ? new(PacketReadKind.Parsed,
            new(NpcServerAuthorityMessage.PortalTeleport, BinaryPrimitives.ReadUInt16LittleEndian(payload),
                PortalColorIndex: BinaryPrimitives.ReadInt16LittleEndian(payload[2..]),
                X: BinaryPrimitives.ReadSingleLittleEndian(payload[4..]), Y: BinaryPrimitives.ReadSingleLittleEndian(payload[8..]),
                VelocityX: BinaryPrimitives.ReadSingleLittleEndian(payload[12..]), VelocityY: BinaryPrimitives.ReadSingleLittleEndian(payload[16..])))
            : new(PacketReadKind.Malformed, null);
    }

    /// <summary>Call only after the existing raw-hook runtime/identity gates; no client-supplied completeness flags.</summary>
    public static BusinessRuleResult Evaluate(NpcServerAuthorityObservation observation, SessionKey session, string fingerprint) =>
        M3CheatAttemptRules.Evaluate(observation, new(session, fingerprint, fingerprint, true, true, true, true),
            Main.maxNPCs, M3CheatAttemptRules.ContractVersion);
}
