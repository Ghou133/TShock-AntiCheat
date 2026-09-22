using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M14LProtocolReadResult(PacketReadKind Kind, M14LEventStateObservation? Packet);

public static class M14LProtocolPacketReader
{
    public static M14LProtocolReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int id = (int)args.MsgID;
        if (id is not (101 or 103)) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(id, buffer.AsSpan(args.Index, length));
    }

    public static M14LProtocolReadResult ReadPayload(int messageId, ReadOnlySpan<byte> body)
    {
        if (messageId is not (101 or 103)) return new(PacketReadKind.Unrelated, null);
        if (body.Length != 8) return new(PacketReadKind.Malformed, null);
        ImmutableArray<int> fields = messageId == 101
            ? [BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
               BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), BinaryPrimitives.ReadUInt16LittleEndian(body[6..])]
            : [BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..])];
        return new(PacketReadKind.Parsed, new(messageId, fields));
    }

    /// <summary>Current raw connection binding and verified runtime are supplied by the existing root hook.</summary>
    public static BusinessRuleResult Evaluate(M14LEventStateObservation state, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        return M14LProtocolRules.Evaluate(state,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host,
            scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
    }
}
