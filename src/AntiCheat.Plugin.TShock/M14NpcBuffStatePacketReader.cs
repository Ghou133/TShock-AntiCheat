using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M14NpcBuffStateReadResult(PacketReadKind Kind, M14NpcBuffStateObservation? Packet);

public static class M14NpcBuffStatePacketReader
{
    public const int MaximumPayloadBytes = 4 + 4 * M14NpcBuffStateRules.BuffCapacity;

    public static M14NpcBuffStateReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != M14NpcBuffStateRules.MessageId) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(buffer.AsSpan(args.Index, length));
    }

    public static M14NpcBuffStateReadResult ReadPayload(ReadOnlySpan<byte> body)
    {
        // Int16 target, up to20 (UInt16 type, UInt16 time) pairs, UInt16 zero terminator.
        if (body.Length < 4 || body.Length > MaximumPayloadBytes || (body.Length - 4) % 4 != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[^2..]) != 0)
            return new(PacketReadKind.Malformed, null);
        int count = (body.Length - 4) / 4;
        var entries = ImmutableArray.CreateBuilder<M14NpcBuffStateEntry>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = 2 + index * 4;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
            if (type == 0) return new(PacketReadKind.Malformed, null); // An early terminator leaves unconsumed bytes.
            // Zero time is valid serialization of an original positive Int32 time such as65536.
            entries.Add(new(type, BinaryPrimitives.ReadUInt16LittleEndian(body[(offset + 2)..])));
        }
        return new(PacketReadKind.Parsed, new(BinaryPrimitives.ReadInt16LittleEndian(body), entries.MoveToImmutable()));
    }

    public static bool NativeLayoutIntact() => Main.maxNPCs == M14NpcBuffStateRules.NpcCapacity &&
        NPC.maxBuffs == M14NpcBuffStateRules.BuffCapacity && BuffID.Count == M14NpcBuffStateRules.BuffTypeCount;

    /// <summary>Called only from the existing verified runtime and current engine-owned binding.</summary>
    public static BusinessRuleResult Evaluate(M14NpcBuffStateObservation state, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity && NativeLayoutIntact();
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        return M14NpcBuffStateRules.Evaluate(state,
            new(session, fingerprint, fingerprint, true, complete, identity, host), host,
            scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
    }
}
