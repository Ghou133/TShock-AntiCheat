using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>The initial declaration is a temporal witness, not proof that its damage was legitimate.</summary>
public sealed record WoodenArrowEvolutionContext(RuleInputContext Input, bool ContractHealthy,
    bool SameLiveEntity, bool InitialDeclarationConfirmed, int InitialDeclaredDamage, bool SourceException,
    bool InternalDamageRangeProven = false);

public static class M5CombatRules
{
    public const string ArrowEvolutionRuleId = "C6.WoodenArrowDamageEscalation";
    public const string RuleId = ArrowEvolutionRuleId;
    public const string Version = "1.1.0";
    public const string MechanismVersion = "terraria-1.4.5.8-wooden-arrow-damage-evolution-v2-int16-premise";
    public const int ArrowType = 1;

    public static BusinessRuleResult EvaluateArrowEvolution(ProjectileObservation observation, int damage,
        WoodenArrowEvolutionContext context) => Evaluate(observation, damage, context) with { Version = Version };

    private static BusinessRuleResult Evaluate(ProjectileObservation observation, int damage, WoodenArrowEvolutionContext context)
    {
        var facts = RuleResults.Facts(("projectileType", observation.Type), ("spawner", observation.Identity.Spawner),
            ("keyIndex", observation.Identity.Index), ("keyGeneration", observation.Identity.Generation),
            ("damage", damage), ("initialDeclaredDamage", context.InitialDeclaredDamage),
            ("mechanismVersion", MechanismVersion), ("initialDamageLegitimacy", "not-established"),
            ("initialDeclarationConfirmed", context.InitialDeclarationConfirmed), ("sourceException", context.SourceException),
            ("internalDamageRangeProven", context.InternalDamageRangeProven));
        if (observation.Type != ArrowType || observation.Operation == ProjectileOperation.Destroy)
            return RuleResults.Pass(ArrowEvolutionRuleId, "outside-wooden-arrow-evolution", facts);
        if (!context.ContractHealthy || !context.Input.VersionMatched || !context.Input.ParserComplete ||
            !context.Input.ClientOrigin || observation.Identity.Spawner != context.Input.Session.Slot)
            return RuleResults.Unknown(ArrowEvolutionRuleId, "arrow-evolution-contract-or-actor-unavailable", facts);
        if (!context.SameLiveEntity || !context.InitialDeclarationConfirmed || observation.Operation != ProjectileOperation.Update ||
            context.InitialDeclaredDamage < 0)
            return RuleResults.Unknown(ArrowEvolutionRuleId, "arrow-initial-declaration-or-live-generation-unavailable", facts);
        if (context.SourceException)
            return RuleResults.Unknown(ArrowEvolutionRuleId, "arrow-server-source-or-export-exception", facts);
        if (damage <= context.InitialDeclaredDamage)
            return RuleResults.Pass(ArrowEvolutionRuleId, "arrow-damage-within-own-initial-declaration", facts);
        // Native internal65540 serializes to4, then reflection legitimately reduces it to16385.
        // Thus a nonnegative first Int16 cannot establish the monotonicity of the native Int32.
        // A future producer must close all source magnitudes and authorized exports before supplying this premise.
        if (!context.InternalDamageRangeProven)
            return RuleResults.Unknown(ArrowEvolutionRuleId, "arrow-native-internal-int16-range-unproved", facts);
        return RuleResults.Candidate(ArrowEvolutionRuleId, "existing-wooden-arrow-damage-raised-above-initial-declaration", context.Input, facts);
    }
}
