using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M13NpcBuffReadResult(PacketReadKind Kind, M13NpcBuffObservation? Packet);

/// <summary>Fixed original-client AddBuff and NPC removal frames. No NPC state or client assertion is trusted.</summary>
public static class M13NpcBuffPacketReader
{
    public static M13NpcBuffReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        int id = (int)args.MsgID;
        if (id is not (53 or 137)) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(id, buffer.AsSpan(args.Index, length));
    }

    public static M13NpcBuffReadResult ReadPayload(int id, ReadOnlySpan<byte> payload)
    {
        if (id is not (53 or 137)) return new(PacketReadKind.Unrelated, null);
        if (payload.Length != (id == 53 ? 6 : 4)) return new(PacketReadKind.Malformed, null);
        return new(PacketReadKind.Parsed, new((M13NpcBuffOperation)id,
            BinaryPrimitives.ReadInt16LittleEndian(payload), BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            id == 53 ? BinaryPrimitives.ReadInt16LittleEndian(payload[4..]) : 0));
    }

    public static BusinessRuleResult Evaluate(M13NpcBuffObservation observation, SessionKey session, string fingerprint,
        bool? scopedClientExtensionAuthorized = false, bool? nativeHostContractComplete = null)
    {
        bool host = nativeHostContractComplete ?? NativeHostContractComplete();
        return M13NpcBuffRules.Evaluate(observation, new(session, fingerprint, fingerprint, true, true, true, host),
            Main.maxNPCs, observation.Operation == M13NpcBuffOperation.Add || RemovalTableIntact(),
            M13NpcBuffRules.ContractVersion, scopedClientExtensionAuthorized);
    }

    // The actual loaded plugin set determines whether synthetic inbound extension events have
    // been excluded. A plugin being able to mutate or replay client traffic is not player guilt.
    public static bool NativeHostContractComplete() => ServerApi.Plugins.All(entry =>
        entry.Plugin.GetType() == typeof(TShockAPI.TShock) || entry.Plugin.GetType() == typeof(AntiCheatPlugin));

    public static bool RemovalTableIntact()
    {
        var current = BuffID.Sets.CanBeRemovedByNetMessage;
        if (BuffID.Count != M13NpcBuffRules.BuffTypeCount || current is null || current.Length != M13NpcBuffRules.BuffTypeCount)
            return false;
        // Fixed 401-entry bounded integrity check also catches trusted host/client-extension
        // changes. Such changes suspend this rule instead of treating them as player evidence.
        foreach (bool removable in current) if (removable) return false;
        return true;
    }
}
