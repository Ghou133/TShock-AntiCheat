using System.Collections.Immutable;
using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class ProjectileRuleTests
{
    private static ProjectileObservation Observation => new(new(Actor.Slot, 9, 2), ProjectileOperation.Create, 15);
    private static ProjectileAuthorityContext Authority => new(Input, Actor, Actor.WorldEpoch, 5, 5, null,
        true, true, false, false, false, 1000, 2000);
    private static ProjectileState Existing => new(Observation.Identity, Actor.Slot, Actor, Observation.Type, true);
    private static ProjectileCatalog Catalog => new(Fingerprint, "verified-test-projectile-mechanisms",
        new Dictionary<int, ProjectileCategory>
        {
            [15] = new(ProjectileSourceConstraint.Allowed, [], []),
            [16] = new(ProjectileSourceConstraint.ServerOnly, [], []),
            [17] = new(ProjectileSourceConstraint.VerifiedParent, ImmutableHashSet.Create(15), []),
            [18] = new(ProjectileSourceConstraint.VerifiedWeapon, [], ImmutableHashSet.Create(25))
        }.ToImmutableDictionary(), true);
    private static ProjectileSourceContext Source => new(Input, true, false, true, 15, 25, false);

    [Test] public void FreshOwnKeyIsLegal() => Pass(ProjectileRules.EvaluateAuthority(Observation, Authority));
    [Test] public void ExistingOwnProjectileCanUpdateAndDestroy()
    {
        Pass(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Update }, Authority with { Existing = Existing, IsFreshCreation = false }));
        Pass(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy, Type = 0 }, Authority with { Existing = Existing, IsFreshCreation = false }));
    }
    [Test] public void TransferredProjectileUsesActualOwnerInsteadOfOriginalSpawner()
    {
        var transferredKey = Observation.Identity with { Spawner = 3 };
        Pass(ProjectileRules.EvaluateAuthority(Observation with { Identity = transferredKey, Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { Identity = transferredKey }, IsFreshCreation = false }));
    }
    [Test] public void ServerCreationAndScopedTransferAreLegal()
    {
        Pass(ProjectileRules.EvaluateAuthority(Observation with { Identity = Observation.Identity with { Spawner = 255 } }, Authority with { ServerOrigin = true }));
        Pass(ProjectileRules.EvaluateAuthority(Observation with { Identity = Observation.Identity with { Spawner = 3 } }, Authority with { AuthorizedOwnershipTransfer = true }));
    }
    [Test] public void ForeignFreshCreationIsSingleCompleteAuthorityViolation() =>
        Candidate(ProjectileRules.EvaluateAuthority(Observation with { Identity = Observation.Identity with { Spawner = 3 } }, Authority));
    [Test] public void ForeignActualOwnerDestroyRetainsNativeRejectionWithoutCheatProof() =>
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { OwnerSlot = 3, OwnerSession = Actor with { Slot = 3 } }, IsFreshCreation = false }));
    [Test] public void ForeignActualOwnerUpdateRetainsIndependentProof()
    {
        var result = ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Update },
            Authority with { Existing = Existing with { OwnerSlot = 3, OwnerSession = Actor with { Slot = 3 } }, IsFreshCreation = false });
        Candidate(result);
        Assert.That(result.Version, Is.EqualTo("1.2.0"));
        Assert.That(result.Facts["existingType"], Is.EqualTo("15"));
    }
    [Test] public void DifferentFullIdentityRemainsASafetyRejection()
    {
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { Identity = Observation.Identity with { Generation = 1 } } }));
    }
    [Test] public void InactiveOwnCleanupIsUnknownBecausePeerCleanupMayStillBeRequired()
    {
        var result = ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { Active = false }, IsFreshCreation = false });
        Unknown(result);
        Assert.That(result.Reason, Is.EqualTo("native-cleanup-server-inactive"));
        Assert.That(result.PrerequisitesComplete, Is.False);
    }
    [Test] public void MissingOwnKeyWithoutRetainedProvenanceIsUnknownRatherThanAnAssumedPeerNoop()
    {
        var result = ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { IsFreshCreation = false });
        Unknown(result);
        Assert.That(result.Reason, Is.EqualTo("native-cleanup-key-missing"));
        Assert.That(result.PrerequisitesComplete, Is.False);
    }
    [Test] public void ExpiredInactiveOwnershipDoesNotInventACurrentSessionOrPreventNativeCleanup() =>
        Unknown(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { Active = false, OwnerSession = default }, IsFreshCreation = false }));
    [Test] public void InactiveTransferredKeyStillUsesItsObservedActualOwner() =>
        Unknown(ProjectileRules.EvaluateAuthority(Observation with { Identity = Observation.Identity with { Spawner = 3 }, Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { Identity = Observation.Identity with { Spawner = 3 }, Active = false }, IsFreshCreation = false }));
    [Test] public void KnownForeignInactiveOwnerCannotBeOverriddenByRetainedSelfOwnership() =>
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { Existing = Existing with { Active = false, OwnerSlot = 3 }, RetainedOwnerSession = Actor, IsFreshCreation = false }));
    [Test] public void MissingForeignSpawnerRemainsBlockedEvenWithAnOldSelfOwnershipRecord() =>
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Identity = Observation.Identity with { Spawner = 3 }, Operation = ProjectileOperation.Destroy },
            Authority with { RetainedOwnerSession = Actor, IsFreshCreation = false }));
    [TestCase(false)] [TestCase(true)]
    public void KnownPreviousSessionBlocksCleanupForInactiveAndMissingKeys(bool inactive)
    {
        var old = Actor with { Generation = Actor.Generation + 1 };
        var result = ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { Existing = inactive ? Existing with { Active = false, OwnerSession = old } : null,
                RetainedOwnerSession = old, IsFreshCreation = false });
        BlockOnly(result);
        Assert.That(result.Reason, Is.EqualTo("projectile-belongs-to-previous-session"));
    }
    [Test] public void OldWorldOwnershipCannotBecomeCurrentCleanupAuthority() =>
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Destroy },
            Authority with { RetainedOwnerSession = Actor with { WorldEpoch = Actor.WorldEpoch + 1 }, IsFreshCreation = false }));
    [Test] public void FreshOwnCreationDoesNotInheritRetainedPreviousSessionCleanupConstraints() =>
        Pass(ProjectileRules.EvaluateAuthority(Observation,
            Authority with { RetainedOwnerSession = Actor with { Generation = Actor.Generation + 1 } }));
    [Test] public void ReconnectedSlotDoesNotInheritOldOwnedProjectile() =>
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Update },
            Authority with { Existing = Existing with { OwnerSession = Actor with { Generation = 11 } } }));
    [Test] public void ExistingOwnSlotWithoutOwnerSessionProvenanceIsUnknown() =>
        Unknown(ProjectileRules.EvaluateAuthority(Observation with { Operation = ProjectileOperation.Update },
            Authority with { Existing = Existing with { OwnerSession = default } }));
    [Test] public void SnapshotRevisionOrWorldChangePreventsProof()
    {
        Unknown(ProjectileRules.EvaluateAuthority(Observation, Authority with { CurrentRevision = 6 }));
        Unknown(ProjectileRules.EvaluateAuthority(Observation, Authority with { SnapshotWorldEpoch = 3 }));
    }
    [Test] public void OwnershipTransitionAndLookupGapAreUnknown()
    {
        Unknown(ProjectileRules.EvaluateAuthority(Observation, Authority with { OwnershipTransitionInProgress = true }));
        Unknown(ProjectileRules.EvaluateAuthority(Observation, Authority with { LookupComplete = false }));
    }
    [Test] public void InvalidKeyRemainsSafetyRejectionEvenWithScopedTransfer() =>
        BlockOnly(ProjectileRules.EvaluateAuthority(Observation with { Identity = Observation.Identity with { Index = 1000 } },
            Authority with { AuthorizedOwnershipTransfer = true }));
    [Test] public void AllowedClientAndCorrelatedChildAndWeaponCreationsPass()
    {
        Pass(ProjectileRules.EvaluateSource(Observation, Source, Catalog));
        Pass(ProjectileRules.EvaluateSource(Observation with { Type = 17 }, Source, Catalog));
        Pass(ProjectileRules.EvaluateSource(Observation with { Type = 18 }, Source, Catalog));
    }
    [Test] public void ExistingServerOnlyProjectileSyncIsNotIndependentCreation() =>
        Pass(ProjectileRules.EvaluateSource(Observation with { Type = 16, Operation = ProjectileOperation.Update }, Source with { FreshCreation = false }, Catalog));
    [Test] public void ScopedPluginCanCreateServerOnlyProjectile() =>
        Pass(ProjectileRules.EvaluateSource(Observation with { Type = 16 }, Source with { ScopedServerOrPluginCreation = true }, Catalog));
    [Test] public void VerifiedServerOnlyCreationIsCompletePredicate() =>
        Candidate(ProjectileRules.EvaluateSource(Observation with { Type = 16 }, Source, Catalog));
    [Test] public void VerifiedParentMismatchIsCompletePredicate() =>
        Candidate(ProjectileRules.EvaluateSource(Observation with { Type = 17 }, Source with { CorrelatedParentType = 99 }, Catalog));
    [Test] public void HeldWeaponAloneCannotProveProjectileSource() =>
        Unknown(ProjectileRules.EvaluateSource(Observation with { Type = 18 }, Source with { CorrelatedWeaponType = 99, SourceCorrelationComplete = false }, Catalog));
    [Test] public void MissingCausalRecordIsNeverProofOfImpossibleSource()
    {
        Unknown(ProjectileRules.EvaluateSource(Observation with { Type = 17 }, Source with { CorrelatedParentType = null }, Catalog));
        Unknown(ProjectileRules.EvaluateSource(Observation with { Type = 18 }, Source with { CorrelatedWeaponType = null }, Catalog));
    }
    [Test] public void UnmodeledProjectileOrVersionGapIsUnknown()
    {
        Unknown(ProjectileRules.EvaluateSource(Observation with { Type = 19 }, Source, Catalog));
        Unknown(ProjectileRules.EvaluateSource(Observation with { Type = 16 }, Source, Catalog with { RuntimeFingerprint = "other-version" }));
    }

    private static SummonBudgetContext Budget => new(Input, Actor, 1.5, 0.5, 0, 2, true, true, false);
    private static FishingContext Fishing => new(Input, Actor, 3, 2, 0, 5, true, true, false);
    [Test] public void FractionalMinionSlotsAreCountedWithoutRounding() => Pass(ProjectileRules.EvaluateSummonBudget(Budget));
    [Test] public void LegitimateSummonReplacementSubtractsCorrelatedSlots() =>
        Pass(ProjectileRules.EvaluateSummonBudget(Budget with { ExistingSlots = 2, IncomingSlots = 1, ReplacedSlots = 1 }));
    [Test] public void MinionBudgetExcessIsResourceProtectionNeverPermanentProof()
    {
        var result = ProjectileRules.EvaluateSummonBudget(Budget with { IncomingSlots = 1 });
        BlockOnly(result);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
    }
    [Test] public void DespawnAndEquipmentChangeWindowDoNotInferExtraMinion() =>
        Unknown(ProjectileRules.EvaluateSummonBudget(Budget with { ExistingSlots = 5, DespawnOrEquipmentTransition = true }));
    [Test] public void MissingReplacementCausalityAndNonfiniteServerDataAreUnknown()
    {
        Unknown(ProjectileRules.EvaluateSummonBudget(Budget with { ReplacementCausalityComplete = false }));
        Unknown(ProjectileRules.EvaluateSummonBudget(Budget with { IncomingSlots = double.NaN }));
    }
    [Test] public void LegitimateMoreThanTwoBobbersPass() => Pass(ProjectileRules.EvaluateFishing(Fishing));
    [Test] public void RecastReplacementAndPendingDespawnAreHandled()
    {
        Pass(ProjectileRules.EvaluateFishing(Fishing with { ExistingBobbersForCast = 5, IncomingBobbers = 5, ReplacedBobbers = 5 }));
        Unknown(ProjectileRules.EvaluateFishing(Fishing with { ExistingBobbersForCast = 5, DespawnOrCastTransition = true }));
    }
    [Test] public void FishingOverrunIsOnlyBoundedResourceControl() =>
        BlockOnly(ProjectileRules.EvaluateFishing(Fishing with { IncomingBobbers = 3 }));
    [Test] public void UnverifiedFishingMechanismOrPreviousSessionCannotBan()
    {
        Unknown(ProjectileRules.EvaluateFishing(Fishing with { MechanismDefinitionComplete = false }));
        Unknown(ProjectileRules.EvaluateFishing(Fishing with { SnapshotSession = Actor with { Generation = 11 } }));
    }
    [Test] public void IncompleteParserCannotDriveSummonOrFishingResourceDecision()
    {
        Unknown(ProjectileRules.EvaluateSummonBudget(Budget with { IncomingSlots = 10, Input = Input with { ParserComplete = false } }));
        Unknown(ProjectileRules.EvaluateFishing(Fishing with { IncomingBobbers = 10, Input = Input with { ParserComplete = false } }));
    }
}
