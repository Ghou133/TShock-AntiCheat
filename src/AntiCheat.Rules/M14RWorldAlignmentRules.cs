using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M14RWorldAlignmentObservation(int Hallow, int Corruption, int Crimson);

/// <summary>The three-byte packet57 is a server publication, not a request to alter world tiles.</summary>
public static class M14RWorldAlignmentRules
{
    public const string RuleId = "WORLD01.WorldAlignmentStateAuthority";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-world-alignment-publisher-v1";

    public static BusinessRuleResult Evaluate(M14RWorldAlignmentObservation state, RuleInputContext input,
        bool nativePublisherContractComplete, string contractVersion = ContractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", 57), ("hallow", state.Hallow),
            ("corruption", state.Corruption), ("crimson", state.Crimson),
            ("contractVersion", contractVersion), ("nativePublisherContractComplete", nativePublisherContractComplete),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized),
            ("proofKind", "client-attempted-server-world-alignment-publication"),
            ("worldTileHistoryRequired", false), ("serverMutationSucceededRequired", false));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "world-alignment-parser-or-contract-unverified", facts);
        if (state.Hallow is < 0 or > byte.MaxValue || state.Corruption is < 0 or > byte.MaxValue ||
            state.Crimson is < 0 or > byte.MaxValue)
            return RuleResults.Block(RuleId, "world-alignment-outside-wire-domain", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "native-server-world-alignment-publication", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-world-alignment-extension", facts);
        if (!nativePublisherContractComplete || scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "world-alignment-client-extension-contract-unavailable", facts);
        // Every byte value, including totals above 100, has the same publisher contract.
        // Actual world conversion, biome spread, counting history and Dryad interaction are irrelevant.
        return RuleResults.Candidate(RuleId, "original-client-cannot-publish-world-alignment", input, facts);
    }
}
