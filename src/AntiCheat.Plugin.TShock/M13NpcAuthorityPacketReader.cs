using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M13NpcAuthorityReadResult(PacketReadKind Kind, M13NpcAuthorityObservation? Packet);

/// <summary>Reads only the fixed packet61 payload before TShock and native spawning can run.</summary>
public static class M13NpcAuthorityPacketReader
{
    public static M13NpcAuthorityReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 61) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(61, buffer.AsSpan(args.Index, length));
    }

    public static M13NpcAuthorityReadResult ReadPayload(int messageId, ReadOnlySpan<byte> payload)
    {
        if (messageId != 61) return new(PacketReadKind.Unrelated, null);
        return payload.Length == 4
            ? new(PacketReadKind.Parsed, new(BinaryPrimitives.ReadInt16LittleEndian(payload),
                BinaryPrimitives.ReadInt16LittleEndian(payload[2..])))
            : new(PacketReadKind.Malformed, null);
    }

    // Current plugin provenance is sufficient for this stateless literal-send contract: previous
    // world/item exports cannot change the native client's literal 61/127 into 61/128..131.
    // A current unknown host extension can inject synthetic receive events and therefore stops proof.
    public static bool NativeHostContractComplete() => ServerApi.Plugins.All(entry =>
        entry.Plugin.GetType() == typeof(TShockAPI.TShock) || entry.Plugin.GetType() == typeof(AntiCheatPlugin));

    /// <summary>Invoke only from the existing runtime-verified, engine-owned current binding.</summary>
    public static BusinessRuleResult Evaluate(M13NpcAuthorityObservation request, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity &&
            actor.ReceivedInfo && actor.HasSentInventory && !actor.IgnoreSSCPackets;
        bool host = nativeHostContractComplete ?? NativeHostContractComplete();
        return M13NpcAuthorityRules.Evaluate(request,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host);
    }
}
