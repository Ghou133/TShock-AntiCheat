using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum ContainerOperation { Open, Close, SlotWrite, QuickStack, Sort, NearbyCraft }
public sealed record ContainerObservation(int ContainerId, int Slot, ContainerOperation Operation);
public sealed record ContainerContext(RuleInputContext Input, SessionKey SnapshotSession, long WorldEpoch,
    long SnapshotRevision, long CurrentRevision, bool TargetExists, int SlotCount, int? OpenContainerId,
    bool LeaseComplete, bool SwitchInProgress, bool? RegionAllowed, bool? InRange,
    ImmutableHashSet<int> OperationAuthorizedTargets, bool OperationAuthorizationComplete);

public static class ContainerRules
{
    public const string RuleId = "B4.ContainerAuthorization";
    public const string Version = "1.1.0";
    public static BusinessRuleResult Evaluate(ContainerObservation observation, ContainerContext context)
        => EvaluateCore(observation, context) with { Version = Version };

    // LeaseComplete in1.1.0 requires an ordered same-target client32 after the latest raw31,
    // in addition to core acceptance. Matching server fields alone cannot close a switch window.
    private static BusinessRuleResult EvaluateCore(ContainerObservation observation, ContainerContext context)
    {
        var facts = RuleResults.Facts(("containerId", observation.ContainerId), ("slot", observation.Slot),
            ("operation", observation.Operation), ("openContainerId", context.OpenContainerId), ("revision", context.SnapshotRevision));
        if (!context.Input.ParserComplete) return RuleResults.Unknown(RuleId, "container-packet-incomplete", facts);
        if (observation.Operation == ContainerOperation.Close) return RuleResults.Pass(RuleId, "normal-container-close", facts);
        if (!context.Input.VersionMatched || context.SnapshotSession != context.Input.Session ||
            context.WorldEpoch != context.Input.Session.WorldEpoch || context.SnapshotRevision != context.CurrentRevision)
            return RuleResults.Unknown(RuleId, "container-snapshot-stale", facts);
        if (!context.TargetExists) return RuleResults.Block(RuleId, "container-target-no-longer-exists", facts);
        if (observation.Operation is ContainerOperation.SlotWrite &&
            (context.SlotCount <= 0 || observation.Slot < 0 || observation.Slot >= context.SlotCount))
            return RuleResults.Block(RuleId, "container-slot-out-of-range", facts);
        // Permission/range controls are safety policy decisions, never standalone cheating proof.
        if (context.RegionAllowed == false) return RuleResults.Block(RuleId, "container-region-permission-denied", facts);
        if (context.InRange == false) return RuleResults.Block(RuleId, "container-interaction-out-of-range", facts);
        if (context.RegionAllowed is null || context.InRange is null)
            return RuleResults.Unknown(RuleId, "container-authorization-context-missing", facts);
        if (observation.Operation == ContainerOperation.Open) return RuleResults.Pass(RuleId, "authorized-container-open", facts);
        if (context.SwitchInProgress) return RuleResults.Unknown(RuleId, "normal-container-switch-window", facts);
        // Quick-stack and nearby crafting do not require an open chest. Their own audited operation
        // authority is checked; no inventory ledger or command-driven transaction is introduced.
        if (observation.Operation is ContainerOperation.QuickStack or ContainerOperation.NearbyCraft)
        {
            if (!context.OperationAuthorizationComplete) return RuleResults.Unknown(RuleId, "bulk-or-nearby-operation-context-incomplete", facts);
            return context.OperationAuthorizedTargets.Contains(observation.ContainerId)
                ? RuleResults.Pass(RuleId, "authorized-bulk-or-nearby-operation", facts)
                : RuleResults.Block(RuleId, "target-outside-authorized-operation", facts);
        }
        if (!context.LeaseComplete) return RuleResults.Unknown(RuleId, "container-lease-unconfirmed", facts);
        if (context.OpenContainerId != observation.ContainerId)
            return RuleResults.Candidate(RuleId, "stable-container-write-target-mismatch", context.Input, facts);
        return RuleResults.Pass(RuleId, "authorized-open-container-mutation", facts);
    }
}
