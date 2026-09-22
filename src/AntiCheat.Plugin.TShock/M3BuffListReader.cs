using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Core;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>Protocol 326 packet 50 contains buff IDs and a zero terminator. It has no duration field.</summary>
public sealed record M3BuffListPacket(byte ClaimedSender, ImmutableArray<int> BuffTypes);
public readonly record struct M3BuffListReadResult(PacketReadKind Kind, M3BuffListPacket? Packet, string Reason);

public static class M3BuffListReader
{
    public const string RuleId = "C5.BuffListSafety";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-buff-list-v1";
    public const int MaximumBuffs = 44; // Actual target Player.maxBuffs, independently checked at read time.
    public const int MaximumPayloadLength = 1 + 2 * (MaximumBuffs + 1);

    public static M3BuffListReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if (args.MsgID != PacketTypes.PlayerBuff) return new(PacketReadKind.Unrelated, null, "unrelated-message");
        if (!verifiedRuntime || Player.maxBuffs != MaximumBuffs)
            return new(PacketReadKind.UnknownRuntime, null, "buff-list-target-capacity-unverified");
        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 3 || length > MaximumPayloadLength || args.Index < 0 || args.Index > buffer.Length - length)
            return Malformed("buff-list-frame-length-or-buffer-invalid");
        return ReadPayload(buffer.AsSpan(args.Index, length));
    }

    public static M3BuffListReadResult ReadPayload(ReadOnlySpan<byte> payload)
    {
        if (Player.maxBuffs != MaximumBuffs)
            return new(PacketReadKind.UnknownRuntime, null, "buff-list-target-capacity-unverified");
        if (payload.Length < 3 || payload.Length > MaximumPayloadLength || (payload.Length & 1) == 0)
            return Malformed("buff-list-payload-length-invalid");
        var types = ImmutableArray.CreateBuilder<int>(MaximumBuffs);
        for (int offset = 1; offset < payload.Length; offset += 2)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            if (type == 0)
                return offset + 2 == payload.Length
                    ? new(PacketReadKind.Parsed, new(payload[0], types.ToImmutable()), "complete-buff-list-without-durations")
                    : Malformed("buff-list-data-after-zero-terminator");
            if (types.Count == MaximumBuffs) return Malformed("buff-list-count-exceeds-target-capacity");
            types.Add(type);
        }
        return Malformed("buff-list-zero-terminator-missing");
    }

    /// <summary>
    /// Called only by the existing verified raw-hook route. Structural/domain rejection is not a
    /// source or duration proof. Initialization, SSC and empty lists need no acquisition history.
    /// </summary>
    public static BusinessRuleResult Evaluate(M3BuffListPacket observation, SessionKey session, string fingerprint,
        bool clientOrigin = true, bool alreadyCancelled = false)
    {
        int count = observation.BuffTypes.IsDefault ? -1 : observation.BuffTypes.Length;
        var facts = ImmutableDictionary<string, string>.Empty
            .Add("messageId", "50").Add("actualSender", session.Slot.ToString())
            .Add("claimedSender", observation.ClaimedSender.ToString()).Add("buffCount", count.ToString())
            .Add("maximumBuffs", Player.maxBuffs.ToString()).Add("buffTypeExclusiveMax", BuffID.Count.ToString())
            .Add("durationField", "absent").Add("contractVersion", ContractVersion);
        if (alreadyCancelled) return Result(ControlAction.Unknown, "buff-list-core-already-cancelled", facts);
        if (string.IsNullOrWhiteSpace(fingerprint) || Player.maxBuffs != MaximumBuffs || BuffID.Count <= 0)
            return Result(ControlAction.Unknown, "buff-list-target-domain-unverified", facts);
        if (count < 0 || count > MaximumBuffs)
            return Result(ControlAction.Block, "buff-list-count-outside-target-domain", facts);
        if (observation.BuffTypes.Any(type => type <= 0 || type >= BuffID.Count))
            return Result(ControlAction.Block, "buff-list-type-outside-target-domain", facts);
        if (clientOrigin && observation.ClaimedSender != session.Slot)
            return Result(ControlAction.Block, "buff-list-client-claimed-another-sender", facts);
        // Duplicates are not a proven source contradiction; do not add an unverified uniqueness rule.
        return Result(ControlAction.Pass, clientOrigin ? "legal-self-buff-list-shape-and-domain"
            : "legal-server-buff-list-shape-and-domain", facts);
    }

    private static M3BuffListReadResult Malformed(string reason) => new(PacketReadKind.Malformed, null, reason);
    private static BusinessRuleResult Result(ControlAction action, string reason, ImmutableDictionary<string, string> facts) =>
        new(RuleId, Version, action, action == ControlAction.Block ? Verdict.UnsafeInput :
            action == ControlAction.Pass ? Verdict.Pass : Verdict.Unknown, reason, false, false, facts);
}
