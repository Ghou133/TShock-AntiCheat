using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M11SentryBudgetInput(SessionKey Session, SessionKey SnapshotSession,
    bool VersionMatched, bool NativeCapacityReturned, bool InputsUnchanged, bool FreshOwnHydra,
    bool RequestCancelled, int KnownActiveEntities, int CapacityUpperBound, bool NativeHostAndActorBound = true);

/// <summary>Frost Hydra resource ceiling, including one native spawn-before-retire transient.
/// Resource exhaustion is not a proof of cheating or of unlawful item acquisition.</summary>
public static class M11SentryBudgetRules
{
    public const string RuleId = "C3.NativeFrostHydraSentryBudget";
    public const string Version = "1.0.0";
    // Reset1 + up to44 native buff slots contributing1 + ten armor-benefit calls contributing2
    // + one boolean dd2Accessory + the single selected armor-set bonus (at most1).
    // Deliberately does not assume lawful equipment positions or unique buffs.
    public const int AbsoluteNativeCapacity = 67;
    public const int MaximumSupportedCapacity = AbsoluteNativeCapacity;

    public static BusinessRuleResult Evaluate(M11SentryBudgetInput input)
    {
        var facts = ImmutableDictionary<string, string>.Empty
            .Add("knownActiveEntities", input.KnownActiveEntities.ToString())
            .Add("capacityUpperBound", input.CapacityUpperBound.ToString())
            .Add("nativeTransientAllowance", "1")
            .Add("replacementOrder", "FrostHydra-create27-UpdateMaxTurrets-retire29")
            .Add("retirementRelease", "actual-inactive-or-identity-replaced; never-request-timeout-or-cache-eviction")
            .Add("accountCheatingProven", "false");
        BusinessRuleResult Result(ControlAction action, Verdict verdict, string reason) =>
            new(RuleId, Version, action, verdict, reason, false, false, facts);
        if (input.RequestCancelled || !input.VersionMatched || !input.NativeHostAndActorBound || !input.FreshOwnHydra)
            return Result(ControlAction.Unknown, Verdict.Unknown, "native-sentry-resource-premises-unavailable");
        if (input.KnownActiveEntities >= AbsoluteNativeCapacity + 1)
        {
            facts = facts.Add("absoluteNativeCapacity", AbsoluteNativeCapacity.ToString())
                .Add("capacityFallback", "native-loop-write-upper-bound-independent-of-equipment-and-buff-input-completion");
            return Result(ControlAction.Block, Verdict.ResourceAbuse, "native-frost-hydra-absolute-resource-bound-exceeded");
        }
        if (input.Session != input.SnapshotSession || !input.NativeCapacityReturned || !input.InputsUnchanged)
            return Result(ControlAction.Unknown, Verdict.Unknown, "native-sentry-resource-premises-unavailable");
        if (input.KnownActiveEntities < 0 || input.CapacityUpperBound is < 1 or > MaximumSupportedCapacity)
            return Result(ControlAction.Unknown, Verdict.Unknown, "native-sentry-resource-values-unsupported");
        return input.KnownActiveEntities >= input.CapacityUpperBound + 1
            ? Result(ControlAction.Block, Verdict.ResourceAbuse, "native-frost-hydra-sentry-resource-budget-exceeded")
            : Result(ControlAction.Pass, Verdict.Pass, "native-sentry-budget-including-spawn-before-retire-transient-fits");
    }
}
