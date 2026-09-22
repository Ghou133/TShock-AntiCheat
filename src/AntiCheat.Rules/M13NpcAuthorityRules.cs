using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M13NpcAuthorityObservation(int Sender, int SummonType);

/// <summary>A narrow protocol-326 request proof, independent of whether the requested spawn succeeds.</summary>
public static class M13NpcAuthorityRules
{
    public const string RuleId = "NPC03.BossPartSummonRequest";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-prime-parts-client-request-v1";

    public static bool IsPrimePart(int type) => type is 128 or 129 or 130 or 131;

    public static BusinessRuleResult Evaluate(M13NpcAuthorityObservation request, RuleInputContext input,
        bool nativeHostContractComplete, string auditedContractVersion = ContractVersion)
    {
        var facts = RuleResults.Facts(("messageId", 61), ("sender", request.Sender),
            ("summonType", request.SummonType), ("contractVersion", auditedContractVersion),
            ("nativeHostContractComplete", nativeHostContractComplete),
            ("actualOrigin", input.ClientOrigin ? "client-connection" : "server"),
            ("proofKind", "client-request-for-server-created-prime-part"),
            ("nativeParentRequest", 127), ("spawnSuccessRequired", false));
        if (!IsPrimePart(request.SummonType))
            return RuleResults.Pass(RuleId, "outside-exact-prime-part-request-contract", facts);
        if (!input.ParserComplete || !input.VersionMatched || auditedContractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "prime-part-target-or-parser-unverified", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "server-npc-creation-is-not-a-client-request", facts);
        // A normal Mechanical Skull requests the head (127), while the server creates its parts.
        // The separate Mechdusa route uses -16. No inventory provenance or damage bound is assumed.
        if (request.Sender != input.Session.Slot)
            return RuleResults.Unknown(RuleId, "prime-part-request-sender-not-current-subject", facts);
        if (!nativeHostContractComplete)
            return RuleResults.Unknown(RuleId, "prime-part-client-extension-contract-unavailable", facts);
        return RuleResults.Candidate(RuleId, "native-client-cannot-request-individual-prime-part", input, facts);
    }
}
