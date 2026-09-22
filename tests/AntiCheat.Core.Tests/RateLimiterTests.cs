using System.Collections.Concurrent;
using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class RateLimiterTests
{
    [Test]
    public void NormalSynchronizationBurstAndRefillPassWithoutAccountPenalty()
    {
        var clock = new ControlledClock();
        var limiter = new BoundedRateLimiter(clock, new(8, 100, 10, TimeSpan.FromMinutes(1)));
        Assert.That(limiter.TryConsume("session-1", 100).Behavior, Is.EqualTo(ControlAction.Pass));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(limiter.TryConsume("session-1", 10).Behavior, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void ExcessCostBlocksOnlyActionAndNeverProducesCheatVerdict()
    {
        var limiter = new BoundedRateLimiter(new ControlledClock(), new(8, 10, 1, TimeSpan.FromMinutes(1)));
        var excess = limiter.TryConsume("session-1", 11);
        Assert.That(excess.Behavior, Is.EqualTo(ControlAction.Block));
        Assert.That(excess.Verdict, Is.EqualTo(Verdict.ResourceAbuse));
        Assert.That(limiter.TryConsume("session-1", 1).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(-1)]
    [TestCase(0)]
    public void InvalidCostIsUnsafeInputWithoutAllocatingState(double cost)
    {
        var limiter = new BoundedRateLimiter(new ControlledClock(), new(8, 10, 1, TimeSpan.FromMinutes(1)));
        Assert.That(limiter.TryConsume("session-1", cost).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(limiter.Count, Is.Zero);
    }

    [Test]
    public void CapacityRejectsNewKeyUntilExpiryWithoutEvictingActiveBudget()
    {
        var clock = new ControlledClock();
        var limiter = new BoundedRateLimiter(clock, new(2, 10, 1, TimeSpan.FromSeconds(5)));
        limiter.TryConsume("one", 10);
        limiter.TryConsume("two", 10);
        Assert.That(limiter.TryConsume("three").Reason, Is.EqualTo("budget-key-capacity"));
        Assert.That(limiter.TryConsume("one").Behavior, Is.EqualTo(ControlAction.Block));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.That(limiter.TryConsume("three").Behavior, Is.EqualTo(ControlAction.Pass));
        Assert.That(limiter.Count, Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentRequestsCannotOverspendOrGrowBeyondConfiguredCapacity()
    {
        var limiter = new BoundedRateLimiter(new ControlledClock(), new(32, 100, 1, TimeSpan.FromMinutes(1)));
        var results = new ConcurrentBag<RateLimitDecision>();
        Parallel.For(0, 1000, _ => results.Add(limiter.TryConsume("shared")));
        Assert.That(results.Count(x => x.Behavior == ControlAction.Pass), Is.EqualTo(100));
        Parallel.For(0, 1000, index => limiter.TryConsume(index.ToString()));
        Assert.That(limiter.Count, Is.EqualTo(32));
    }

    [Test]
    public void WallClockJumpDoesNotCreateTokensOrExpireBudgets()
    {
        var clock = new ControlledClock();
        var limiter = new BoundedRateLimiter(clock, new(2, 1, 1, TimeSpan.FromSeconds(5)));
        limiter.TryConsume("one");
        clock.JumpWallClock(TimeSpan.FromDays(365));
        Assert.That(limiter.TryConsume("one").Behavior, Is.EqualTo(ControlAction.Block));
        clock.JumpWallClock(TimeSpan.FromDays(-730));
        Assert.That(limiter.TryConsume("one").Behavior, Is.EqualTo(ControlAction.Block));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(limiter.TryConsume("one").Behavior, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void RetiredSessionKeyCanBeForgottenWithoutClearingNewGenerationBudget()
    {
        var limiter = new BoundedRateLimiter(new ControlledClock(), new(1, 1, 1, TimeSpan.FromMinutes(1)));
        limiter.TryConsume("slot-4-generation-1");
        Assert.That(limiter.Forget("slot-4-generation-1"), Is.True);
        Assert.That(limiter.TryConsume("slot-4-generation-2").Behavior, Is.EqualTo(ControlAction.Pass));
        Assert.That(limiter.Forget("slot-4-generation-1"), Is.False);
        Assert.That(limiter.TryConsume("slot-4-generation-2").Behavior, Is.EqualTo(ControlAction.Block));
        Assert.That(limiter.Count, Is.EqualTo(1));
    }

    [Test]
    public void OversizedKeyDoesNotAllocateEntry()
    {
        var limiter = new BoundedRateLimiter(new ControlledClock(), new(2, 1, 1, TimeSpan.FromSeconds(5)));
        Assert.That(limiter.TryConsume(new string('x', 129)).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(limiter.Count, Is.Zero);
    }
}
