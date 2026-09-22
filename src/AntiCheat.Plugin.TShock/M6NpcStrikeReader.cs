using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M6NpcStrikeReadResult(PacketReadKind Kind, NpcStrikeObservation? Packet);

/// <summary>Constant-cost pre-write parsing of the actual protocol326 NPC strike request.</summary>
public static class M6NpcStrikeReader
{
    public static M6NpcStrikeReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 28) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var bytes = args.Msg?.readBuffer;
        if (bytes is null || length != 10 || args.Index < 0 || args.Index > bytes.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(bytes.AsSpan(args.Index, length));
    }

    public static M6NpcStrikeReadResult ReadPayload(ReadOnlySpan<byte> payload) => payload.Length != 10
        ? new(PacketReadKind.Malformed, null)
        : new(PacketReadKind.Parsed, new(payload[0], payload[1], BinaryPrimitives.ReadInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[4..]), payload[8], payload[9]));

    /// <summary>Call from the existing verified-runtime raw hook; accepted NPC state is used only
    /// for the native generation no-op, never to attest the legitimacy of the requested damage.</summary>
    public static BusinessRuleResult Evaluate(NpcStrikeObservation observation, SessionKey session, string fingerprint)
    {
        bool complete = Main.netMode == 2 && Main.npc is { Length: > 0 } &&
            (uint)observation.TargetSlot < Main.npc.Length && Main.npc[observation.TargetSlot] is not null;
        var target = complete ? Main.npc[observation.TargetSlot] : null;
        var result = M6CombatRules.EvaluateStrike(observation,
            new(session, fingerprint, fingerprint, true, complete, true, true), Main.maxNPCs,
            complete, target?.active == true, target?.generation ?? -1);
        return result with { Facts = result.Facts.Add("producer", "M6NpcStrikeReader") };
    }
}
