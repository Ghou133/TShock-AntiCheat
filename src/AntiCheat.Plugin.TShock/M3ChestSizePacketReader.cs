using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M3ChestSizeReadResult(PacketReadKind Kind, ChestResizeObservation? Packet);

public static class M3ChestSizePacketReader
{
    public static M3ChestSizeReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 155) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length != 4 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        var payload = buffer.AsSpan(args.Index, length);
        return new(PacketReadKind.Parsed, new(BinaryPrimitives.ReadInt16LittleEndian(payload),
            BinaryPrimitives.ReadInt16LittleEndian(payload[2..])));
    }

    public static BusinessRuleResult Evaluate(ChestResizeObservation observation, SessionKey session,
        TSPlayer actor, string fingerprint) => M3CheatAttemptRules.EvaluateChestResize(observation,
            new(session, fingerprint, fingerprint, true, true, true, true), Main.maxChests,
            (uint)observation.ChestIndex < Main.chest.Length && Main.chest[observation.ChestIndex] is not null,
            actor.HasPermission(Permissions.resizechests), M3CheatAttemptRules.ContractVersion);
}
