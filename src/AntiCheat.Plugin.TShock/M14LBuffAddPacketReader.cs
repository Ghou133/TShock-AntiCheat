using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public readonly record struct M14LBuffAddReadResult(PacketReadKind Kind, M14LPlayerBuffAddObservation? Packet);

public static class M14LBuffAddPacketReader
{
    public static M14LBuffAddReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((int)args.MsgID != 55) return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime) return new(PacketReadKind.UnknownRuntime, null);
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 0 || args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);
        return ReadPayload(buffer.AsSpan(args.Index, length));
    }

    public static M14LBuffAddReadResult ReadPayload(ReadOnlySpan<byte> body) => body.Length != 7
        ? new(PacketReadKind.Malformed, null)
        : new(PacketReadKind.Parsed, new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[3..])));

    public static bool NativePvpTableIntact()
    {
        if (BuffID.Count != M14LBuffAddRules.BuffCount || Main.pvpBuff is not { Length: M14LBuffAddRules.BuffCount }) return false;
        for (int type = 0; type < M14LBuffAddRules.BuffCount; type++)
            if (Main.pvpBuff[type] != M14LBuffAddRules.IsNativePvpBuff(type)) return false;
        return true;
    }

    public static BusinessRuleResult Evaluate(M14LPlayerBuffAddObservation packet, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity && Main.maxPlayers == 255;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        var input = new RuleInputContext(session, fingerprint, fingerprint, true, complete, identity, host);
        var result = M14LBuffAddRules.EvaluatePlayer(packet, input,
            NativePvpTableIntact(), scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
        return WithProvenance(result, input, host, identity);
    }

    public static BusinessRuleResult EvaluateNpc(M13NpcBuffObservation packet, SessionKey session,
        TSPlayer actor, string fingerprint, bool? nativeHostContractComplete = null,
        bool? scopedClientExtensionAuthorized = false)
    {
        bool identity = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is { ID: > 0 };
        bool complete = Main.netMode == 2 && Main.myPlayer == 255 && identity && Main.maxNPCs == 200 &&
            BuffID.Count == M14LBuffAddRules.BuffCount;
        bool host = nativeHostContractComplete ?? M13NpcAuthorityPacketReader.NativeHostContractComplete();
        if (actor.HasPermission(Permissions.ignorenpcbuffdetection)) scopedClientExtensionAuthorized = true;
        var input = new RuleInputContext(session, fingerprint, fingerprint, true, complete, identity, host);
        var result = M14LBuffAddRules.EvaluateNpc(packet, input, scopedClientExtensionAuthorized: scopedClientExtensionAuthorized);
        return WithProvenance(result, input, host, identity);
    }

    internal static BusinessRuleResult WithProvenance(BusinessRuleResult result, RuleInputContext input,
        bool host, bool identity) => result with
        {
            Facts = result.Facts.SetItem("nativeHostContractComplete", host.ToString())
                .SetItem("currentAccountAndActorBound", identity.ToString())
                .SetItem("contextComplete", input.Complete.ToString())
        };
}
