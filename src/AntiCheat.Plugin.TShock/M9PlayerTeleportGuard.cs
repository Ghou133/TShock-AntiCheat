using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Core;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public sealed record M9PlayerTeleportRequest(int MessageId, byte Flags, int ClaimedSlot,
    float X, float Y, float VelocityX, float VelocityY, int Style, int ExtraInfo)
{
    public int Mode => Flags & 3;
    public bool TargetPosition => (Flags & 4) != 0;
}

public readonly record struct M9PlayerTeleportReadResult(PacketReadKind Kind, M9PlayerTeleportRequest? Packet);

/// <summary>Protocol326 player teleport parameter safety at the pre-core raw hook. This does
/// not attest a teleport cause, destination authorization, client receipt or account misconduct.</summary>
public static class M9PlayerTeleportGuard
{
    public const string RuleId = "G05.PlayerTeleportParameters";
    public const string Version = "1.0.0";

    public static M9PlayerTeleportReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int id = (int)args.MsgID;
        if (id is not (65 or 96)) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(id, buffer.AsSpan(args.Index, length));
    }

    public static M9PlayerTeleportReadResult ReadPayload(int messageId, ReadOnlySpan<byte> payload)
    {
        if (messageId == 65)
        {
            if (payload.Length < 12 || payload.Length != 12 + ((payload[0] & 8) != 0 ? 4 : 0))
                return new(PacketReadKind.Malformed, null);
            return new(PacketReadKind.Parsed, new(65, payload[0], BinaryPrimitives.ReadInt16LittleEndian(payload[1..]),
                BinaryPrimitives.ReadSingleLittleEndian(payload[3..]), BinaryPrimitives.ReadSingleLittleEndian(payload[7..]),
                0, 0, payload[11], payload.Length == 16 ? BinaryPrimitives.ReadInt32LittleEndian(payload[12..]) : 0));
        }
        if (messageId != 96) return new(PacketReadKind.Unrelated, null);
        return payload.Length != 19 ? new(PacketReadKind.Malformed, null) : new(PacketReadKind.Parsed,
            new(96, 0, payload[0], BinaryPrimitives.ReadSingleLittleEndian(payload[3..]),
                BinaryPrimitives.ReadSingleLittleEndian(payload[7..]), BinaryPrimitives.ReadSingleLittleEndian(payload[11..]),
                BinaryPrimitives.ReadSingleLittleEndian(payload[15..]), 4, BinaryPrimitives.ReadInt16LittleEndian(payload[1..])));
    }

    public static BusinessRuleResult Evaluate(M9PlayerTeleportRequest request, SessionKey session, string fingerprint)
    {
        var facts = ImmutableDictionary<string, string>.Empty.Add("producer", nameof(M9PlayerTeleportGuard))
            .Add("messageId", request.MessageId.ToString()).Add("mode", request.Mode.ToString())
            .Add("targetPosition", request.TargetPosition.ToString()).Add("claimedSlot", request.ClaimedSlot.ToString())
            .Add("senderSlot", session.Slot.ToString()).Add("authorityProof", "not-established")
            .Add("contract", "terraria-1.4.5.8-protocol326-player-teleport-consumed-parameters-v1");
        BusinessRuleResult Result(ControlAction action, Verdict verdict, string reason) =>
            new(RuleId, Version, action, verdict, reason, false, false, facts);
        if (fingerprint != TargetRuntime.Fingerprint || Main.netMode != 2 || session.WorldEpoch <= 0)
            return Result(ControlAction.Unknown, Verdict.Unknown, "player-teleport-runtime-unavailable");
        // TShock HandleTeleport dereferences the claimed id when bit2 is set, before the
        // native receiver normalizes id to whoAmI. This applies even to an ack payload.
        if (request.MessageId == 65 && request.TargetPosition &&
            ((uint)request.ClaimedSlot >= Main.player.Length || Main.player[request.ClaimedSlot] is null))
            return Result(ControlAction.Block, Verdict.UnsafeInput, "teleport-target-position-index-out-of-range");
        bool playerDestinationConsumed = request.MessageId == 96 ||
            request.MessageId == 65 && request.Mode is 0 or 2 && !request.TargetPosition;
        if (playerDestinationConsumed && (!float.IsFinite(request.X) || !float.IsFinite(request.Y)))
            return Result(ControlAction.Block, Verdict.UnsafeInput, "player-teleport-nonfinite-consumed-destination");
        if (request.MessageId == 96 && (!float.IsFinite(request.VelocityX) || !float.IsFinite(request.VelocityY)))
            return Result(ControlAction.Block, Verdict.UnsafeInput, "player-portal-nonfinite-consumed-velocity");
        // Mode3 acknowledges an earlier server teleport: coordinates are ignored. For
        // bit2, native uses the sender's current position, not the raw coordinate fields.
        // NPC mode1 remains outside this player contract; core permission handlers remain.
        return Result(ControlAction.Pass, Verdict.Pass, "player-teleport-consumed-parameters-safe");
    }
}
