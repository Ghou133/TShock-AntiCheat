using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public readonly record struct M14NpcBuffStateEntry(int Type, int Time);
public sealed record M14NpcBuffStateObservation(int NpcIndex, ImmutableArray<M14NpcBuffStateEntry> Entries);

/// <summary>The exact protocol326 full-NPC-buff-state publisher contract, separate from AddBuff53 and removal137.</summary>
public static class M14NpcBuffStateRules
{
    public const string RuleId = "NPC04.BuffStateSyncAuthority";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-npc-buff-state-server-publisher-v1";
    public const int MessageId = 54, NpcCapacity = 200, BuffCapacity = 20, BuffTypeCount = 401;

    public static BusinessRuleResult Evaluate(M14NpcBuffStateObservation state, RuleInputContext input,
        bool nativePublisherContractComplete, string contractVersion = ContractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", MessageId), ("npcIndex", state.NpcIndex),
            ("entryCount", state.Entries.IsDefault ? -1 : state.Entries.Length), ("contractVersion", contractVersion),
            ("nativePublisherContractComplete", nativePublisherContractComplete),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized),
            ("proofKind", "client-attempted-authoritative-npc-buff-state-publication"),
            ("targetNpcStateRequired", false), ("serverMutationSucceededRequired", false));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion || state.Entries.IsDefault)
            return RuleResults.Unknown(RuleId, "npc-buff-state-parser-or-contract-unverified", facts);
        if (state.NpcIndex < 0 || state.NpcIndex >= NpcCapacity || state.Entries.Length > BuffCapacity ||
            state.Entries.Any(entry => entry.Type <= 0 || entry.Type >= BuffTypeCount || entry.Time is < 0 or > ushort.MaxValue))
            return RuleResults.Block(RuleId, "npc-buff-state-outside-audited-wire-domain", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "native-server-npc-buff-state-publication", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-npc-buff-state-extension", facts);
        if (scopedClientExtensionAuthorized is null || !nativePublisherContractComplete)
            return RuleResults.Unknown(RuleId, "npc-buff-state-client-extension-contract-unavailable", facts);
        // All six original54 call sites are in server branches. Original client AddBuff uses53,
        // while receive54 only updates the local NPC and does not echo. No target-state inference.
        return RuleResults.Candidate(RuleId, "original-client-cannot-publish-full-npc-buff-state", input, facts);
    }
}
