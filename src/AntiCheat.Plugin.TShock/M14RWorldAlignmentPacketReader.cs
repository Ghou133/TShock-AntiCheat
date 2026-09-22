using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M14RWorldAlignmentReadResult(PacketReadKind Kind, M14RWorldAlignmentObservation? Packet);

public static class M14RWorldAlignmentPacketReader
{
    public static M14RWorldAlignmentReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 57) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(57, buffer.AsSpan(args.Index, length));
    }

    public static M14RWorldAlignmentReadResult ReadPayload(int messageId, ReadOnlySpan<byte> body)
    {
        if (messageId != 57) return new(PacketReadKind.Unrelated, null);
        return body.Length == 3
            ? new(PacketReadKind.Parsed, new(body[0], body[1], body[2]))
            : new(PacketReadKind.Malformed, null);
    }

    /// <summary>The raw root supplies the current engine-owned connection binding and verified runtime.</summary>
    public static BusinessRuleResult Evaluate(M14RWorldAlignmentObservation state, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        return M14RWorldAlignmentRules.Evaluate(state,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host,
            scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
    }
}
