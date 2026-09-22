using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M14LEventStateObservation(int MessageId, ImmutableArray<int> Fields);

/// <summary>Two audited NPC event state publications. Neither message is a client event request.</summary>
public static class M14LProtocolRules
{
    public const string RuleId = "NPC05.EventStateAuthority";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-event-state-publisher-v1";

    public static BusinessRuleResult Evaluate(M14LEventStateObservation state, RuleInputContext input,
        bool nativePublisherContractComplete, string contractVersion = ContractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", state.MessageId),
            ("publication", state.MessageId == 101 ? "lunar-tower-shields" : "moon-lord-countdown"),
            ("fields", state.Fields.IsDefault ? "unavailable" : string.Join(",", state.Fields)),
            ("contractVersion", contractVersion), ("nativePublisherContractComplete", nativePublisherContractComplete),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized),
            ("proofKind", "client-attempted-authoritative-npc-event-state-publication"),
            ("gameplayHistoryRequired", false), ("serverMutationSucceededRequired", false));
        if (state.MessageId is not (101 or 103))
            return RuleResults.Pass(RuleId, "outside-audited-event-state-publications", facts);
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion || state.Fields.IsDefault)
            return RuleResults.Unknown(RuleId, "event-state-parser-or-contract-unverified", facts);
        if (state.Fields.Length != (state.MessageId == 101 ? 4 : 2) ||
            state.MessageId == 101 && state.Fields.Any(value => value is < 0 or > ushort.MaxValue))
            return RuleResults.Block(RuleId, "event-state-outside-audited-wire-domain", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "native-server-npc-event-state-publication", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-event-state-extension", facts);
        if (!nativePublisherContractComplete || scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "event-state-client-extension-contract-unavailable", facts);
        // The proof is the original publisher role, never shield/countdown magnitude or event history.
        // Original client receive101/103 only consumes state and has no echo; request61 is separate.
        return RuleResults.Candidate(RuleId, "original-client-cannot-publish-npc-event-state", input, facts);
    }
}
