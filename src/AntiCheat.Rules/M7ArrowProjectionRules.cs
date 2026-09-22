using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>The internal domain is the entire Int32 domain, including all Int16 wraparound preimages.
/// No item maximum or accepted initial damage is asserted to be legitimate.</summary>
public sealed record ArrowProjectionContext(RuleInputContext Input, bool LifecycleVerified,
    bool SameLiveEntity, bool InitialDeclarationConfirmed, int InitialWireDamage, bool SourceException);

public static class M7ArrowProjectionRules
{
    public const string RuleId = "C7.WoodenArrowDamageProjection";
    public const string Version = "1.0.0";
    public const string MechanismVersion = "terraria-1.4.5.8-type1-int32-single-reflection-projection-v1";

    /// <summary>Exact projection of all Int32 preimages through identity or truncation by four.
    /// The sign-dependent remainder is retained; no per-packet enumeration is needed.
    /// Zero is an additional conservative cleanup allowance.</summary>
    public static bool IsReachable(int initialWire, int candidateWire)
    {
        if (initialWire is < short.MinValue or > short.MaxValue || candidateWire is < short.MinValue or > short.MaxValue)
            return false;
        if (candidateWire == initialWire || candidateWire == 0) return true;
        int residue = unchecked((ushort)(short)initialWire);
        int quarterResidue = unchecked((ushort)(short)candidateWire) & 0x3fff;
        int floor = residue >> 2;
        int ceiling = (floor + ((residue & 3) == 0 ? 0 : 1)) & 0x3fff;
        return quarterResidue == floor || quarterResidue == ceiling;
    }

    public static BusinessRuleResult Evaluate(ProjectileObservation observation, int damage, ArrowProjectionContext context)
    {
        var facts = RuleResults.Facts(("mechanismVersion", MechanismVersion), ("projectileType", observation.Type),
            ("keyIndex", observation.Identity.Index), ("keyGeneration", observation.Identity.Generation),
            ("initialWireDamage", context.InitialWireDamage), ("damage", damage),
            ("internalDomain", "all-signed-int32-preimages"), ("initialDamageLegitimacy", "not-established"),
            ("transformSet", "identity|single-truncate-divide4|zero-cleanup"), ("sourceException", context.SourceException));
        BusinessRuleResult Result(BusinessRuleResult result) => result with { Version = Version };
        if (observation.Type != 1 || observation.Operation == ProjectileOperation.Destroy)
            return Result(RuleResults.Pass(RuleId, "outside-wooden-arrow-projection", facts));
        if (!context.LifecycleVerified || !context.Input.VersionMatched || !context.Input.ParserComplete ||
            !context.Input.ClientOrigin || observation.Identity.Spawner != context.Input.Session.Slot ||
            !context.SameLiveEntity || !context.InitialDeclarationConfirmed || observation.Operation != ProjectileOperation.Update)
            return Result(RuleResults.Unknown(RuleId, "arrow-projection-lifecycle-unavailable", facts));
        if (context.SourceException)
            return Result(RuleResults.Unknown(RuleId, "arrow-projection-source-or-export-exception", facts));
        if (context.InitialWireDamage is < short.MinValue or > short.MaxValue || damage is < short.MinValue or > short.MaxValue)
            return Result(RuleResults.Unknown(RuleId, "arrow-projection-wire-domain-unavailable", facts));
        if (IsReachable(context.InitialWireDamage, damage))
            return Result(RuleResults.Pass(RuleId, "arrow-damage-has-native-int32-preimage", facts));
        return Result(RuleResults.Candidate(RuleId, "arrow-damage-outside-native-internal-projection", context.Input, facts));
    }
}
