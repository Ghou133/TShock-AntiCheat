using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M14RCavernMonsterObservation(ImmutableArray<int> Types);

/// <summary>Packet136 publishes the server's six cavern monster selections to a joining client.</summary>
public static class M14RCavernMonsterRules
{
    public const string RuleId = "NPC06.CavernMonsterStateAuthority";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-cavern-monster-publisher-v1";

    public static BusinessRuleResult Evaluate(M14RCavernMonsterObservation state, RuleInputContext input,
        bool nativePublisherContractComplete, string contractVersion = ContractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", 136),
            ("typesRowMajor", state.Types.IsDefault ? "unavailable" : string.Join(",", state.Types)),
            ("contractVersion", contractVersion), ("nativePublisherContractComplete", nativePublisherContractComplete),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized),
            ("proofKind", "client-attempted-server-cavern-monster-publication"),
            ("npcSpawnOrTargetHistoryRequired", false), ("serverMutationSucceededRequired", false));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion || state.Types.IsDefault)
            return RuleResults.Unknown(RuleId, "cavern-monster-parser-or-contract-unverified", facts);
        if (state.Types.Length != 6 || state.Types.Any(type => type is < 0 or > ushort.MaxValue))
            return RuleResults.Block(RuleId, "cavern-monster-outside-wire-domain", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "native-server-cavern-monster-publication", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-cavern-monster-extension", facts);
        if (!nativePublisherContractComplete || scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "cavern-monster-client-extension-contract-unavailable", facts);
        // The six UInt16 values are deliberately not classified as NPC IDs or progression states.
        // The server alone publishes this matrix; a client has no request or echo form of136.
        return RuleResults.Candidate(RuleId, "original-client-cannot-publish-cavern-monster-types", input, facts);
    }
}
