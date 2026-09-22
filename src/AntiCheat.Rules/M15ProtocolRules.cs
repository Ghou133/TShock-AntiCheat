using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M15CreditsRollObservation(byte Operation, int RemainingTime);

/// <summary>Only packet140 operation0 is a server publication; operations1/2 are legitimate client requests.</summary>
public static class M15ProtocolRules
{
    public const string RuleId = "WORLD02.CreditsRollStateAuthority";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-credits-roll-publisher-v1";

    public static BusinessRuleResult Evaluate(M15CreditsRollObservation state, RuleInputContext input,
        bool nativePublisherContractComplete, string contractVersion = ContractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", 140), ("operation", state.Operation),
            ("remainingTime", state.RemainingTime), ("contractVersion", contractVersion),
            ("nativePublisherContractComplete", nativePublisherContractComplete),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized),
            ("proofKind", "client-attempted-server-credits-roll-publication"),
            ("worldHistoryRequired", false), ("serverMutationSucceededRequired", false));
        if (state.Operation != 0)
            return RuleResults.Pass(RuleId, state.Operation switch
            {
                1 => "credits-roll-sibling-copper-slime-request",
                2 => "credits-roll-sibling-elder-slime-request",
                _ => "outside-credits-roll-publication-suboperation"
            }, facts);
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "credits-roll-parser-or-contract-unverified", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "native-server-credits-roll-publication", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-credits-roll-extension", facts);
        if (!nativePublisherContractComplete || scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "credits-roll-client-extension-contract-unavailable", facts);
        // The original client receives operation0 without echo. Its legitimate copper/elder
        // slime requests are operations1/2, never reclassified by timer sign or magnitude.
        return RuleResults.Candidate(RuleId, "original-client-cannot-publish-credits-roll-state", input, facts);
    }
}
