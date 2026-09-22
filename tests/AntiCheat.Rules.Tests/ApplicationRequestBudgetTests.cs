using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class ApplicationRequestBudgetTests
{
    private static readonly Guid Run = Guid.Parse("E859F170-E465-438E-88DA-CF2AADE582C7");
    private static SessionKey Key(int slot = 1, long generation = 1, long world = 1) => new(Run, world, slot, generation);

    [Test]
    public void LegitimateBurstAndSustainedRequestsEnterEffectiveAdmission()
    {
        var clock = new NetworkTestClock(); var budget = new ApplicationRequestBudget(clock);
        for (int i = 0; i < 12; i++) Assert.That(budget.Consume(Key(), 11, 64).Allowed, Is.True);
        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            Assert.That(budget.Consume(Key(), 11, 256).Allowed, Is.True);
        }
        Assert.That(budget.Admitted, Is.EqualTo(92)); Assert.That(budget.Rejected, Is.Zero);
    }

    [Test]
    public void ExcessIsDroppedThenSameSessionRecoversWithoutReplayOrSanction()
    {
        var clock = new NetworkTestClock(); var budget = new ApplicationRequestBudget(clock);
        for (int i = 0; i < 16; i++) Assert.That(budget.Consume(Key(), 11, 256).Allowed, Is.True);
        Assert.That(budget.Consume(Key(), 11, 256).Allowed, Is.False);
        Assert.That(budget.Consume(Key(), 11, 256).Allowed, Is.False);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.That(budget.Consume(Key(), 11, 256).Allowed, Is.True);
        Assert.That(budget.Admitted, Is.EqualTo(17)); Assert.That(budget.Rejected, Is.EqualTo(2));
    }

    [Test]
    public void ReconnectWorldChangesAndConcurrentSessionsDoNotRefreshAccountBudget()
    {
        var budget = new ApplicationRequestBudget(new NetworkTestClock());
        for (int i = 0; i < 32; i++) Assert.That(budget.Consume(Key(), 11, 0).Allowed, Is.True);
        budget.Forget(Key());
        Assert.That(budget.Consume(Key(generation: 2), 11, 0).Reason, Is.EqualTo("application-account-budget"));
        Assert.That(budget.Consume(Key(world: 2), 11, 0).Allowed, Is.False);
        Assert.That(budget.Consume(Key(slot: 2), 11, 0).Allowed, Is.False);
        Assert.That(budget.Consume(Key(slot: 3), 22, 0).Allowed, Is.True);
    }

    [Test]
    public void SlotReuseNewAccountDoesNotInheritAnotherSessionsLimit()
    {
        var budget = new ApplicationRequestBudget(new NetworkTestClock());
        for (int i = 0; i < 32; i++) budget.Consume(Key(), 11, 0);
        budget.Forget(Key());
        Assert.That(budget.Consume(Key(generation: 2), 22, 100).Allowed, Is.True);
    }

    [Test]
    public void PreAuthenticationStillHasConnectionAdmissionAndLongTextHasWeight()
    {
        var budget = new ApplicationRequestBudget(new NetworkTestClock());
        Assert.That(budget.Consume(Key(), null, 2000).Cost, Is.EqualTo(8.8125));
        Assert.That(budget.Consume(Key(), null, 2000).Allowed, Is.True);
        Assert.That(budget.Consume(Key(), null, 65535).Allowed, Is.False);
        Assert.That(budget.AccountBucketCount, Is.Zero);
    }

    [Test]
    public void AccountTableCapacityIsBoundedAndDoesNotEvictLiveBudgets()
    {
        var clock = new NetworkTestClock(); var budget = new ApplicationRequestBudget(clock);
        for (int i = 1; i <= 4096; i++)
        {
            Assert.That(budget.Consume(Key(generation: i), i, 0).Allowed, Is.True);
            budget.Forget(Key(generation: i));
        }
        Assert.That(budget.AccountBucketCount, Is.EqualTo(4096));
        Assert.That(budget.Consume(Key(generation: 4097), 4097, 0).Allowed, Is.False);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(budget.Consume(Key(generation: 4098), 4097, 0).Allowed, Is.True);
    }

    [Test]
    public void ApplicationSourceEventsUseActualPeerAndExistingCooldownWithoutDisconnect()
    {
        var clock = new NetworkTestClock(); var network = new NetworkControls(clock);
        network.Open(Key(), "::ffff:203.0.113.80");
        network.Open(Key(slot: 2), "203.0.113.80");
        var first = network.ReportApplicationRejection(Key());
        Assert.That(first.Disposition, Is.EqualTo(NetworkDisposition.Block));
        Assert.That(first.SourceEvent!.SocketPeer, Is.EqualTo("203.0.113.80"));
        Assert.That(first.SourceEvent.Recommendation, Is.EqualTo(FirewallRecommendation.RecordOnly));
        Assert.That(first.SourceEvent.ProposedTarget, Is.Null);
        Assert.That(network.ReportApplicationRejection(Key(slot: 2)).SourceEvent, Is.Null);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That(network.ReportApplicationRejection(Key(slot: 2)).SourceEvent, Is.Not.Null);
        Assert.That(network.Consume(Key(slot: 2), 64).Disposition, Is.EqualTo(NetworkDisposition.Allow));
    }
}
