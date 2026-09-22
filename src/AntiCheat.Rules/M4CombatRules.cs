using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>The one audited NPC mechanism, not a generic hostile-projectile classification.</summary>
public sealed record CultistRitualContext(RuleInputContext Input, bool ContractHealthy,
    bool LookupComplete, bool ExistingEntity, bool ScopedSourceException);
public sealed record PortalPlacementContext(RuleInputContext Input, bool ContractHealthy,
    bool LookupComplete, bool ExistingEntity, bool ScopedSourceException, bool NumbersFinite);

public static class M4CombatRules
{
    public const string RitualRuleId = "C2.CultistRitualRole";
    public const int RitualType = 490;
    public const string MechanismVersion = "terraria-1.4.5.8-cultist-ai084-v1";
    public const string PortalDamageRuleId = "C6.PortalPlacementDamage";
    public const int PortalType = 602;
    public const string PortalMechanismVersion = "terraria-1.4.5.8-portal-placement-zero-damage-v1";

    public static BusinessRuleResult EvaluateRitual(ProjectileObservation observation, CultistRitualContext context)
    {
        var facts = RuleResults.Facts(("projectileType", observation.Type), ("spawner", observation.Identity.Spawner),
            ("keyIndex", observation.Identity.Index), ("keyGeneration", observation.Identity.Generation),
            ("senderSlot", context.Input.Session.Slot), ("mechanismVersion", MechanismVersion),
            ("existingEntity", context.ExistingEntity), ("scopedSourceException", context.ScopedSourceException));
        if (observation.Type != RitualType || observation.Operation == ProjectileOperation.Destroy)
            return RuleResults.Pass(RitualRuleId, "outside-cultist-ritual-mechanism", facts);
        if (!context.Input.ClientOrigin)
            return RuleResults.Pass(RitualRuleId, "server-cultist-ritual-creation", facts);
        if (!context.ContractHealthy || !context.Input.VersionMatched || !context.Input.ParserComplete)
            return RuleResults.Unknown(RitualRuleId, "cultist-ritual-contract-unavailable", facts);
        // A received server key is not a new client-owned cause. C1 evaluates its independent authority question.
        if (observation.Identity.Spawner != context.Input.Session.Slot)
            return RuleResults.Unknown(RitualRuleId, "cultist-ritual-key-is-not-client-fresh-creation", facts);
        if (!context.LookupComplete || context.ExistingEntity || observation.Operation != ProjectileOperation.Create)
            return RuleResults.Unknown(RitualRuleId, "cultist-ritual-existing-or-unresolved-key", facts);
        if (context.ScopedSourceException)
            return RuleResults.Unknown(RitualRuleId, "cultist-ritual-server-plugin-source-exception", facts);
        // This assertion comes from AI_084's server-only creation branch and the target key allocator,
        // not from missing ItemUse, held equipment, projHostile alone, damage, or packet timing.
        return RuleResults.Candidate(RitualRuleId, "client-created-npc-only-cultist-ritual", context.Input, facts);
    }

    public static BusinessRuleResult EvaluatePortalDamage(ProjectileObservation observation, int damage, PortalPlacementContext context)
    {
        var facts = RuleResults.Facts(("projectileType", observation.Type), ("spawner", observation.Identity.Spawner),
            ("keyIndex", observation.Identity.Index), ("keyGeneration", observation.Identity.Generation),
            ("senderSlot", context.Input.Session.Slot), ("damage", damage), ("constructorDamage", 0),
            ("mechanismVersion", PortalMechanismVersion), ("existingEntity", context.ExistingEntity),
            ("scopedSourceException", context.ScopedSourceException));
        if (observation.Type != PortalType || observation.Operation == ProjectileOperation.Destroy)
            return RuleResults.Pass(PortalDamageRuleId, "outside-portal-placement-mechanism", facts);
        if (!context.Input.ClientOrigin)
            return RuleResults.Pass(PortalDamageRuleId, "server-portal-placement", facts);
        if (!context.ContractHealthy || !context.Input.VersionMatched || !context.Input.ParserComplete || !context.NumbersFinite)
            return RuleResults.Unknown(PortalDamageRuleId, "portal-placement-contract-or-input-unavailable", facts);
        if (damage == 0)
            return RuleResults.Pass(PortalDamageRuleId, "portal-placement-canonical-zero-damage", facts);
        if (observation.Identity.Spawner != context.Input.Session.Slot || !context.LookupComplete ||
            context.ExistingEntity || observation.Operation != ProjectileOperation.Create)
            return RuleResults.Unknown(PortalDamageRuleId, "portal-placement-not-verified-fresh-player-key", facts);
        if (context.ScopedSourceException)
            return RuleResults.Unknown(PortalDamageRuleId, "portal-placement-server-plugin-source-exception", facts);
        // PortalHelper.AddPortal sets damage to zero even when its parent has nonzero damage.
        // This exact constructor invariant is independent of a current weapon, source absence, DPS or timing.
        return RuleResults.Candidate(PortalDamageRuleId, "portal-placement-damage-differs-from-fixed-constructor", context.Input, facts);
    }
}
