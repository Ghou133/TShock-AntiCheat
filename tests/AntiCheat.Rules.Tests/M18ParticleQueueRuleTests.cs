using System.Collections.Immutable;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M18ParticleQueueRuleTests
{
    private static readonly SessionKey Session = new(Guid.NewGuid(), 1, 7, 1);

    private static M18ParticleQueueOptions Options => new()
    {
        Enabled = true,
        WindowTicks = 3,
        PerSessionEvents = 2,
        EventCapacity = 4,
    };

    private static M18ParticleObservation Observation(
        SessionKey? session = null,
        long accountId = 6207,
        int type = M18ParticleQueue.StormLightningType,
        byte invokingPlayer = 7,
        bool positionFinite = true,
        bool movementFinite = true,
        bool attributionComplete = true)
        => new(session ?? Session, accountId, type, invokingPlayer, positionFinite,
            movementFinite, ParseComplete: true, ClientOrigin: true,
            BeforeSideEffects: true, attributionComplete)
        {
            PayloadIdentityMatchesSession = invokingPlayer == (session?.Slot ?? Session.Slot),
        };

    [Test]
    public void CompleteStormLightningRequestsAreCountedAndOtherTypesAreOutsideCandidate()
    {
        var queue = new M18ParticleQueue(Options);
        var first = queue.Observe(0, Observation());
        var other = queue.Observe(0, Observation(type: 60));
        var second = queue.Observe(0, Observation());
        var third = queue.Observe(0, Observation());

        Assert.That(first.Counted, Is.True);
        Assert.That(other.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(other.Counted, Is.False);
        Assert.That(second.Counted, Is.True);
        Assert.That(third.IsResourceBlock, Is.True);
        Assert.That(third.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
    }

    [Test]
    public void PayloadMismatchUsesTheAuthenticatedSessionWhileIncompleteFactsDoNotMutateQueue()
    {
        var queue = new M18ParticleQueue(Options);
        var mismatch = queue.Observe(0, Observation(invokingPlayer: 8));
        Assert.Multiple(() =>
        {
            Assert.That(mismatch.Counted, Is.True);
            Assert.That(mismatch.PayloadIdentityMatchesSession, Is.False);
            Assert.That(mismatch.Reason, Is.EqualTo("particle-window-observed-payload-identity-mismatch"));
            Assert.That(mismatch.SessionEvents, Is.EqualTo(1));
        });

        foreach (var observation in new[]
        {
            Observation(attributionComplete: false),
            Observation(positionFinite: false),
            Observation(movementFinite: false),
            Observation(accountId: 0),
        })
        {
            var result = queue.Observe(0, observation);
            Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(result.Counted, Is.False);
        }

        Assert.That(queue.Observe(0, Observation()).Counted, Is.True);
        Assert.That(queue.Observe(0, Observation()).IsResourceBlock, Is.True);
    }

    [Test]
    public void WindowExpiryReopensBudgetWithoutGrowingTheRing()
    {
        var queue = new M18ParticleQueue(Options);
        Assert.That(queue.Observe(0, Observation()).Counted, Is.True);
        Assert.That(queue.Observe(0, Observation()).Counted, Is.True);
        Assert.That(queue.Observe(1, Observation()).IsResourceBlock, Is.True);
        var afterExpiry = queue.Observe(3, Observation());

        Assert.That(afterExpiry.Counted, Is.True);
        Assert.That(afterExpiry.SessionEvents, Is.EqualTo(1));
        Assert.That(afterExpiry.EventsRetained, Is.EqualTo(1));
    }

    [Test]
    public void ExactSessionAndWorldCleanupDiscardOnlyTheMatchingQueue()
    {
        var queue = new M18ParticleQueue(Options);
        queue.Observe(0, Observation());
        var nextGeneration = Session with { Generation = 2 };
        Assert.That(queue.Observe(0, Observation(session: nextGeneration)).Counted, Is.True);
        Assert.That(queue.Observe(0, Observation(session: nextGeneration)).Counted, Is.True);
        Assert.That(queue.Observe(0, Observation(session: nextGeneration)).IsResourceBlock, Is.True);

        queue.Forget(nextGeneration);
        Assert.That(queue.Observe(0, Observation(session: nextGeneration)).Counted, Is.True);
        queue.AdvanceWorld(8);
        Assert.That(queue.Observe(0, Observation(session: nextGeneration)).Counted, Is.True);
    }

    [Test]
    public void ResourceBlockIsExplicitlyNonPredicateAndNonSanctionEvidence()
    {
        var queue = new M18ParticleQueue(Options);
        queue.Observe(0, Observation());
        queue.Observe(0, Observation());
        var decision = queue.Observe(0, Observation());
        var result = M18ParticleQueueRules.ResourceBlock(
            new(Session, "fp", "fp", true, true, true, false),
            ImmutableDictionary<string, string>.Empty,
            decision);

        Assert.That(result.RuleId, Is.EqualTo("F08.LightningParticleDensityBudget"));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(result.PrerequisitesComplete, Is.False);
    }
}
