using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M15ProjectileReadResult(PacketReadKind Kind, M15CannonFiringObservation? Packet);

public static class M15ProjectilePacketReader
{
    public static M15ProjectileReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 108) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(108, buffer.AsSpan(args.Index, length));
    }

    public static M15ProjectileReadResult ReadPayload(int messageId, ReadOnlySpan<byte> body)
    {
        if (messageId != 108) return new(PacketReadKind.Unrelated, null);
        if (body.Length != 15) return new(PacketReadKind.Malformed, null);
        return new(PacketReadKind.Parsed, new(BinaryPrimitives.ReadInt16LittleEndian(body),
            BinaryPrimitives.ReadSingleLittleEndian(body[2..]), BinaryPrimitives.ReadInt16LittleEndian(body[6..]),
            BinaryPrimitives.ReadInt16LittleEndian(body[8..]), BinaryPrimitives.ReadInt16LittleEndian(body[10..]),
            BinaryPrimitives.ReadInt16LittleEndian(body[12..]), body[14]));
    }

    public static BusinessRuleResult Evaluate(M15CannonFiringObservation state, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        return M15ProjectileRules.Evaluate(state,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host,
            scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
    }
}
