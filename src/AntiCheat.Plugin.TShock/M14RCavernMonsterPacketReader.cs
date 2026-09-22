using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M14RCavernMonsterReadResult(PacketReadKind Kind, M14RCavernMonsterObservation? Packet);

public static class M14RCavernMonsterPacketReader
{
    public static M14RCavernMonsterReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 136) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(136, buffer.AsSpan(args.Index, length));
    }

    public static M14RCavernMonsterReadResult ReadPayload(int messageId, ReadOnlySpan<byte> body)
    {
        if (messageId != 136) return new(PacketReadKind.Unrelated, null);
        if (body.Length != 12) return new(PacketReadKind.Malformed, null);
        return new(PacketReadKind.Parsed, new([
            BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), BinaryPrimitives.ReadUInt16LittleEndian(body[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[8..]), BinaryPrimitives.ReadUInt16LittleEndian(body[10..])]));
    }

    public static BusinessRuleResult Evaluate(M14RCavernMonsterObservation state, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        return M14RCavernMonsterRules.Evaluate(state,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host,
            scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
    }
}
