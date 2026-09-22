using System.Collections.Immutable;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class ContainerRuleTests
{
    private static ContainerObservation Observation => new(10, 1, ContainerOperation.SlotWrite);
    private static ContainerContext Context => new(Input, Actor, Actor.WorldEpoch, 8, 8, true, 40, 10,
        true, false, true, true, ImmutableHashSet.Create(10, 11), true);
    [Test] public void OpenChestSlotWriteAndSortPass()
    {
        Pass(ContainerRules.Evaluate(Observation, Context));
        Pass(ContainerRules.Evaluate(Observation with { Operation = ContainerOperation.Sort }, Context));
    }
    [TestCase(ContainerOperation.QuickStack)] [TestCase(ContainerOperation.NearbyCraft)]
    public void BulkOperationsCanUseUnopenedChest(ContainerOperation operation) =>
        Pass(ContainerRules.Evaluate(Observation with { ContainerId = 11, Operation = operation }, Context with { OpenContainerId = null }));
    [Test] public void NormalOpenDoesNotRequirePriorOpenLease() =>
        Pass(ContainerRules.Evaluate(Observation with { ContainerId = 11, Operation = ContainerOperation.Open }, Context with { OpenContainerId = null }));
    [Test] public void CloseStillWorksAfterTargetRemoved() =>
        Pass(ContainerRules.Evaluate(Observation with { ContainerId = -1, Operation = ContainerOperation.Close }, Context with { TargetExists = false }));
    [Test] public void RapidChestSwitchIsNotStableMismatchProof() =>
        Unknown(ContainerRules.Evaluate(Observation with { ContainerId = 11 }, Context with { SwitchInProgress = true }));
    [Test] public void ConfirmedForeignChestWriteIsCompletePredicate() =>
        Candidate(ContainerRules.Evaluate(Observation with { ContainerId = 11 }, Context));
    [Test] public void LeaseGapAndRevisionRaceDoNotProveDuplication()
    {
        Unknown(ContainerRules.Evaluate(Observation with { ContainerId = 11 }, Context with { LeaseComplete = false }));
        Unknown(ContainerRules.Evaluate(Observation with { ContainerId = 11 }, Context with { CurrentRevision = 9 }));
    }
    [Test] public void ReconnectAndWorldResetRejectOldProofContext()
    {
        Unknown(ContainerRules.Evaluate(Observation with { ContainerId = 11 }, Context with { SnapshotSession = Actor with { Generation = 11 } }));
        Unknown(ContainerRules.Evaluate(Observation with { ContainerId = 11 }, Context with { WorldEpoch = 3 }));
    }
    [Test] public void PermissionDenialAndInvalidSlotAreBlockOnly()
    {
        BlockOnly(ContainerRules.Evaluate(Observation, Context with { RegionAllowed = false }));
        BlockOnly(ContainerRules.Evaluate(Observation with { Slot = 40 }, Context));
    }
    [Test] public void UnmodeledNearbyCraftDoesNotRequireInventoryLedger() =>
        Unknown(ContainerRules.Evaluate(Observation with { Operation = ContainerOperation.NearbyCraft }, Context with { OperationAuthorizationComplete = false }));
    [Test] public void BulkTargetOutsideLocalAuthorizationIsOnlySafetyRejection() =>
        BlockOnly(ContainerRules.Evaluate(Observation with { ContainerId = 12, Operation = ContainerOperation.QuickStack }, Context));
}
