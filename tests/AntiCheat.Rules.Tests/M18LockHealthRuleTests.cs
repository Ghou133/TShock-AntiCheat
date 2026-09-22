using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18LockHealthRuleTests
{
    private static readonly M18LockHealthObservation Complete = new(
        PairCount: 3,
        RequiredPairs: 3,
        LastPairGapTicks: 2,
        WindowTicks: 120,
        ResponseWindowTicks: 12,
        PendingHurtCount: 0,
        DamageExactlyOne: true,
        ServerOutputAttributed: true,
        ServerLifeWasReduced: true,
        FullLifeSync: true,
        ServerSideCharacter: true,
        ActiveGameplay: true,
        SynchronizationExcluded: true,
        ScopedPermissionExcluded: true,
        SessionComplete: true,
        ClientOrigin: true,
        BeforeCoreHandler: true);

    [Test]
    public void BelowThresholdRemainsUnknown()
    {
        var result = M18LockHealthRules.Evaluate(Complete with { PairCount = 2 });

        Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(result.PrerequisitesComplete, Is.False);
    }

    [Test]
    public void SustainedPatternRequestsOnlyNonSanctioningServiceBlock()
    {
        var result = M18LockHealthRules.Evaluate(Complete);

        Assert.Multiple(() =>
        {
            Assert.That(result.RuleId, Is.EqualTo(M18LockHealthRules.RuleId));
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
            Assert.That(result.Facts["serviceKickNotPermanentSanction"], Is.EqualTo("True"));
        });
    }

    [TestCase(nameof(M18LockHealthObservation.DamageExactlyOne))]
    [TestCase(nameof(M18LockHealthObservation.ServerLifeWasReduced))]
    [TestCase(nameof(M18LockHealthObservation.FullLifeSync))]
    [TestCase(nameof(M18LockHealthObservation.ServerSideCharacter))]
    [TestCase(nameof(M18LockHealthObservation.ActiveGameplay))]
    [TestCase(nameof(M18LockHealthObservation.SynchronizationExcluded))]
    [TestCase(nameof(M18LockHealthObservation.ScopedPermissionExcluded))]
    [TestCase(nameof(M18LockHealthObservation.SessionComplete))]
    [TestCase(nameof(M18LockHealthObservation.ClientOrigin))]
    [TestCase(nameof(M18LockHealthObservation.BeforeCoreHandler))]
    public void MissingAnyClosedContextNeverRequestsServiceKick(string field)
    {
        var observation = field switch
        {
            nameof(M18LockHealthObservation.DamageExactlyOne) => Complete with { DamageExactlyOne = false },
            nameof(M18LockHealthObservation.ServerLifeWasReduced) => Complete with { ServerLifeWasReduced = false },
            nameof(M18LockHealthObservation.FullLifeSync) => Complete with { FullLifeSync = false },
            nameof(M18LockHealthObservation.ServerSideCharacter) => Complete with { ServerSideCharacter = false },
            nameof(M18LockHealthObservation.ActiveGameplay) => Complete with { ActiveGameplay = false },
            nameof(M18LockHealthObservation.SynchronizationExcluded) => Complete with { SynchronizationExcluded = false },
            nameof(M18LockHealthObservation.ScopedPermissionExcluded) => Complete with { ScopedPermissionExcluded = false },
            nameof(M18LockHealthObservation.SessionComplete) => Complete with { SessionComplete = false },
            nameof(M18LockHealthObservation.ClientOrigin) => Complete with { ClientOrigin = false },
            nameof(M18LockHealthObservation.BeforeCoreHandler) => Complete with { BeforeCoreHandler = false },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

        var result = M18LockHealthRules.Evaluate(observation);

        Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void InvalidBoundsStayUnknownAndNeverBecomeCheatProof()
    {
        foreach (var observation in new[]
        {
            Complete with { RequiredPairs = 0 },
            Complete with { WindowTicks = 0 },
            Complete with { ResponseWindowTicks = 0 },
            Complete with { LastPairGapTicks = -1 },
            Complete with { PendingHurtCount = -1 },
            Complete with { PairCount = -1 },
        })
        {
            var result = M18LockHealthRules.Evaluate(observation);
            Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(result.PredicateSatisfied, Is.False);
            Assert.That(result.PrerequisitesComplete, Is.False);
        }
    }
}
