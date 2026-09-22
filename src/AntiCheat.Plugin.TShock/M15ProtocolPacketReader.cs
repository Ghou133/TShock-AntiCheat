using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M15ProtocolReadResult(PacketReadKind Kind, M15CreditsRollObservation? Packet);

public static class M15ProtocolPacketReader
{
    public static M15ProtocolReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 140) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(140, buffer.AsSpan(args.Index, length));
    }

    public static M15ProtocolReadResult ReadPayload(int messageId, ReadOnlySpan<byte> body)
    {
        if (messageId != 140) return new(PacketReadKind.Unrelated, null);
        return body.Length == 5
            ? new(PacketReadKind.Parsed, new(body[0], BinaryPrimitives.ReadInt32LittleEndian(body[1..])))
            : new(PacketReadKind.Malformed, null);
    }

    public static BusinessRuleResult Evaluate(M15CreditsRollObservation state, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        return M15ProtocolRules.Evaluate(state,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host,
            scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
    }
}
