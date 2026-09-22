using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum SelfIdentityMessage { Emoji, InventorySlot, GolfCup }

/// <summary>Only the individually audited self-only messages are admitted; target-bearing messages are excluded.</summary>
public static class ProtocolRules
{
    public const string Version = "m2.1";
    public static string RuleId(SelfIdentityMessage message) => message switch
    {
        SelfIdentityMessage.Emoji => "A01.EmojiSenderMismatch",
        SelfIdentityMessage.InventorySlot => "A02.InventorySenderMismatch",
        SelfIdentityMessage.GolfCup => "A02.GolfSenderMismatch",
        _ => "A02.UnrecognizedMessage"
    };

    public static BusinessRuleResult EvaluateIdentity(SelfIdentityMessage message, int serverSlot, int claimedSlot,
        bool completeParse, bool verifiedMessageContract, bool clientOrigin, bool knownServerRelay = false)
    {
        var facts = ImmutableDictionary<string, string>.Empty.Add("message", message.ToString())
            .Add("serverSlot", serverSlot.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("claimedSlot", claimedSlot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        BusinessRuleResult Result(ControlAction action, Verdict verdict, string reason, bool proof = false) =>
            new(RuleId(message), Version, action, verdict, reason, proof, proof, facts);
        if (!Enum.IsDefined(message) || !completeParse || !verifiedMessageContract || !clientOrigin)
            return Result(ControlAction.Unknown, Verdict.Unknown, "identity-message-contract-incomplete");
        if (knownServerRelay) return Result(ControlAction.Pass, Verdict.Pass, "known-server-relay");
        if (serverSlot is < 0 or > 255 || claimedSlot is < 0 or > 255)
            return Result(ControlAction.Block, Verdict.UnsafeInput, "identity-byte-outside-domain");
        if (serverSlot == claimedSlot) return Result(ControlAction.Pass, Verdict.Pass, "self-identity");
        return Result(ControlAction.Block, Verdict.ProvenCheat, "audited-self-message-sender-mismatch", true);
    }

    public static BusinessRuleResult EvaluateDirection(byte packetId, bool verifiedContract, bool clientOrigin)
    {
        // 3 assigns the connection slot; 7 publishes world metadata; 49 tells the client to spawn.
        // These receive directions are audited individually. They are safety rejection, never account proof.
        bool serverOnly = packetId is 3 or 7 or 49;
        return new("A04.MessageDirection", Version,
            !verifiedContract ? ControlAction.Unknown : serverOnly && clientOrigin ? ControlAction.Block : ControlAction.Pass,
            !verifiedContract ? Verdict.Unknown : serverOnly && clientOrigin ? Verdict.UnsafeInput : Verdict.Pass,
            !verifiedContract ? "direction-contract-unverified" : serverOnly && clientOrigin ? "server-only-message-from-client" : "direction-allowed",
            false, false, ImmutableDictionary<string, string>.Empty.Add("packetId", packetId.ToString()));
    }
}
