using System.Collections.Immutable;

namespace AntiCheat.Core;

/// <summary>A pure rule result. It cannot grant execution qualification or choose the sanctioned account.</summary>
public sealed record BusinessRuleResult(string RuleId, string Version, ControlAction Action, Verdict Verdict,
    string Reason, bool PredicateSatisfied, bool PrerequisitesComplete, ImmutableDictionary<string, string> Facts);

/// <summary>Server-owned admission for one versioned predicate, separate from its result and configuration.</summary>
public sealed record BusinessRulePolicy(string RuleId, string Version, string RuntimeFingerprint,
    string ContextVersion, RuleQualification Qualification, string AuditReference);

public sealed record BusinessObservation(SessionKey Session, byte PacketId,
    BusinessRuleResult Result, ProofContext Context);
