using System.Collections.Immutable;
using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>Server-owned provenance for the specific rule snapshot; SSC/Main.player alone do not attest completeness.</summary>
public sealed record RuleInputContext(SessionKey Session, string RuntimeFingerprint, string SnapshotFingerprint,
    bool ParserComplete, bool SnapshotComplete, bool AttributionComplete, bool ExceptionsExcluded,
    bool ClientOrigin = true)
{
    public bool VersionMatched => !string.IsNullOrWhiteSpace(RuntimeFingerprint) && RuntimeFingerprint != "unknown" &&
        RuntimeFingerprint.Length <= 1024 && RuntimeFingerprint == SnapshotFingerprint;
    public bool Complete => VersionMatched && ParserComplete && SnapshotComplete && AttributionComplete &&
        ExceptionsExcluded && ClientOrigin && Session.ServerRunId != Guid.Empty && Session.WorldEpoch > 0 &&
        Session.Generation > 0 && Session.Slot is >= 0 and < 256;
}

internal static class RuleResults
{
    internal const string Version = "1.0.0";
    internal static ImmutableDictionary<string, string> Facts(params (string Key, object? Value)[] values) =>
        values.ToImmutableDictionary(x => x.Key, x => Convert.ToString(x.Value, CultureInfo.InvariantCulture) ?? "null");
    internal static BusinessRuleResult Pass(string id, string reason, ImmutableDictionary<string, string> facts) =>
        new(id, Version, ControlAction.Pass, Verdict.Pass, reason, false, true, facts);
    internal static BusinessRuleResult Unknown(string id, string reason, ImmutableDictionary<string, string> facts) =>
        new(id, Version, ControlAction.Unknown, Verdict.Unknown, reason, false, false, facts);
    internal static BusinessRuleResult Block(string id, string reason, ImmutableDictionary<string, string> facts, bool resource = false) =>
        new(id, Version, ControlAction.Block, resource ? Verdict.ResourceAbuse : Verdict.UnsafeInput, reason, false, false, facts);
    internal static BusinessRuleResult Candidate(string id, string reason, RuleInputContext context, ImmutableDictionary<string, string> facts) =>
        context.Complete
            ? new(id, Version, ControlAction.Block, Verdict.ProvenCheat, reason, true, true, facts)
            : Unknown(id, "proof-context-incomplete:" + reason, facts);
}
