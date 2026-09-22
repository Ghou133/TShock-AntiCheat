using System.Collections.Immutable;
using AntiCheat.Core;

namespace AntiCheat.Rules;

/// <summary>
/// Candidate-only service-rule input for the narrow SSC life-sync pattern. This is
/// deliberately not a cheating predicate: it can request a session kick, but it
/// must never create a permanent-sanction proof.
/// </summary>
public readonly record struct M18LockHealthObservation(
    int PairCount,
    int RequiredPairs,
    int LastPairGapTicks,
    int WindowTicks,
    int ResponseWindowTicks,
    int PendingHurtCount,
    bool DamageExactlyOne,
    bool ServerOutputAttributed,
    bool ServerLifeWasReduced,
    bool FullLifeSync,
    bool ServerSideCharacter,
    bool ActiveGameplay,
    bool SynchronizationExcluded,
    bool ScopedPermissionExcluded,
    bool SessionComplete,
    bool ClientOrigin,
    bool BeforeCoreHandler);

public static class M18LockHealthRules
{
    public const string RuleId = "G06.SustainedOneDamageFullLifeSync";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-ssc-one-damage-service-kick-v1";

    public static BusinessRuleResult Evaluate(M18LockHealthObservation observation)
    {
        var facts = RuleResults.Facts(
            ("pairCount", observation.PairCount),
            ("requiredPairs", observation.RequiredPairs),
            ("lastPairGapTicks", observation.LastPairGapTicks),
            ("windowTicks", observation.WindowTicks),
            ("responseWindowTicks", observation.ResponseWindowTicks),
            ("pendingHurtCount", observation.PendingHurtCount),
            ("damageExactlyOne", observation.DamageExactlyOne),
            ("serverOutputAttributed", observation.ServerOutputAttributed),
            ("serverLifeWasReduced", observation.ServerLifeWasReduced),
            ("fullLifeSync", observation.FullLifeSync),
            ("serverSideCharacter", observation.ServerSideCharacter),
            ("activeGameplay", observation.ActiveGameplay),
            ("synchronizationExcluded", observation.SynchronizationExcluded),
            ("scopedPermissionExcluded", observation.ScopedPermissionExcluded),
            ("sessionComplete", observation.SessionComplete),
            ("contract", ContractVersion),
            ("serviceKickNotPermanentSanction", true));

        if (observation.RequiredPairs is < 1 or > 32 || observation.PairCount < 0 ||
            observation.WindowTicks is < 1 or > 3600 || observation.ResponseWindowTicks is < 1 or > 120 ||
            observation.LastPairGapTicks < 0 || observation.PendingHurtCount < 0 ||
            !observation.DamageExactlyOne || !observation.ServerOutputAttributed ||
            !observation.ServerLifeWasReduced || !observation.FullLifeSync ||
            !observation.ServerSideCharacter || !observation.ActiveGameplay ||
            !observation.SynchronizationExcluded || !observation.ScopedPermissionExcluded ||
            !observation.SessionComplete || !observation.ClientOrigin || !observation.BeforeCoreHandler)
            return RuleResults.Unknown(RuleId, "lock-health-service-context-incomplete", facts);

        if (observation.PairCount < observation.RequiredPairs || observation.LastPairGapTicks > observation.WindowTicks)
            return RuleResults.Unknown(RuleId, "sustained-one-damage-pattern-not-reached", facts);

        // This result is an immediate service-policy block only. PredicateSatisfied
        // and PrerequisitesComplete stay false so AntiCheatEngine cannot manufacture
        // a BanIntent or revoke an account from a service-rule kick.
        return new BusinessRuleResult(
            RuleId,
            Version,
            ControlAction.Block,
            Verdict.UnsafeInput,
            "sustained-one-damage-full-life-sync-service-kick",
            PredicateSatisfied: false,
            PrerequisitesComplete: false,
            facts);
    }
}
