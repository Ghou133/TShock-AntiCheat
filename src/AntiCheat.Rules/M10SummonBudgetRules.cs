using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>A lower bound on still-active, same-session native commits and a conservative resource ceiling.
/// Neither accepted equipment nor this budget establishes lawful item acquisition or a cheat verdict.</summary>
public sealed record M10SummonBudgetInput(SessionKey Session, SessionKey SnapshotSession,
    bool VersionMatched, bool NativeCapacityReturned, bool InputsUnchanged, bool FreshOwnSlime,
    bool RequestCancelled, double KnownActiveSlots, double IncomingSlots, double CapacityUpperBound,
    int KnownEntities, int RetirementUncertainEntities);

public static class M10SummonBudgetRules
{
    public const string RuleId = "C3.NativeSlimeSummonBudget";
    public const string Version = "1.0.0";

    public static BusinessRuleResult Evaluate(M10SummonBudgetInput input)
    {
        var facts = ImmutableDictionary<string, string>.Empty
            .Add("knownActiveSlots", input.KnownActiveSlots.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("incomingSlots", input.IncomingSlots.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("capacityUpperBound", input.CapacityUpperBound.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("knownEntities", input.KnownEntities.ToString())
            .Add("retirementUncertainEntities", input.RetirementUncertainEntities.ToString())
            .Add("budgetKind", "resource-only-session-high-water-capacity-and-active-commit-lower-bound")
            .Add("replacementOrder", "native-SlimeStaff-FreeUpPetsAndMinions-kill29-before-create27")
            .Add("accountCheatingProven", "false");
        BusinessRuleResult Result(ControlAction action, Verdict verdict, string reason) =>
            new(RuleId, Version, action, verdict, reason, false, false, facts);
        if (input.RequestCancelled || !input.VersionMatched || input.Session != input.SnapshotSession ||
            !input.NativeCapacityReturned || !input.InputsUnchanged || !input.FreshOwnSlime)
            return Result(ControlAction.Unknown, Verdict.Unknown, "native-summon-resource-premises-unavailable");
        if (!double.IsFinite(input.KnownActiveSlots) || !double.IsFinite(input.IncomingSlots) ||
            !double.IsFinite(input.CapacityUpperBound) || input.KnownActiveSlots < 0 || input.IncomingSlots != 1 ||
            input.CapacityUpperBound < 1 || input.KnownEntities < 0 || input.RetirementUncertainEntities < 0)
            return Result(ControlAction.Unknown, Verdict.Unknown, "native-summon-resource-values-invalid");
        return input.KnownActiveSlots + input.IncomingSlots > input.CapacityUpperBound + 0.000001
            ? Result(ControlAction.Block, Verdict.ResourceAbuse, "native-slime-summon-resource-budget-exceeded")
            : Result(ControlAction.Pass, Verdict.Pass, "known-native-summon-resource-lower-bound-fits");
    }
}
