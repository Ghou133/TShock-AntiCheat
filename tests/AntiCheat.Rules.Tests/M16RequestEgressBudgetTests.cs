using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

public sealed class M16RequestEgressBudgetTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(double seconds) => Ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }
    private static SessionKey Session(int slot = 1) => new(Guid.NewGuid(), 1, slot, 1);
    private static M16RequestEgressOptions Options(double global = 1000) => new()
    { ActorBurstBytes = 100, ActorBytesPerSecond = 25, GlobalBurstBytes = global, GlobalBytesPerSecond = 50 };
    [Test]
    public void SerializedFanoutConsumesBytesOncePerRecipientAndNaturallyRefills()
    {
        var clock = new Clock(); var budget = new M16RequestEgressBudget(clock, Options()); var session = Session();
        Assert.That(budget.Consume(session, 1, 50).Allowed, Is.True);
        Assert.That(budget.Consume(session, 1, 50).Allowed, Is.True);
        Assert.That(budget.Consume(session, 1, 5).Allowed, Is.False);
        Assert.That(budget.AdmittedBytes, Is.EqualTo(100));
        Assert.That(budget.BlockedBytes, Is.EqualTo(5));
        clock.Advance(2);
        Assert.That(budget.Consume(session, 1, 50).Allowed, Is.True);
        Assert.That(budget.AdmittedSends, Is.EqualTo(3));
    }
    [Test]
    public void ReconnectCannotResetAccountButAnotherAccountHasIndependentAllowance()
    {
        var budget = new M16RequestEgressBudget(new Clock(), Options()); var old = Session();
        Assert.That(budget.Consume(old, 1, 100).Allowed, Is.True); budget.Forget(old);
        Assert.That(budget.Consume(old with { Generation = 2 }, 1, 5).Reason, Is.EqualTo("chat-egress-account-budget"));
        Assert.That(budget.Consume(old with { Generation = 3 }, 2, 100).Allowed, Is.True);
        Assert.That(budget.AccountBucketCount, Is.EqualTo(2));
    }
    [Test]
    public void RejectedSenderCannotSpendOtherAccountsGlobalAllowance()
    {
        var budget = new M16RequestEgressBudget(new Clock(), Options(200)); var old = Session();
        Assert.That(budget.Consume(old, 1, 100).Allowed, Is.True);
        for (int i = 0; i < 100; i++) Assert.That(budget.Consume(old, 1, 100).Allowed, Is.False);
        Assert.That(budget.Consume(Session(2), 2, 100).Allowed, Is.True);
        Assert.That(budget.Consume(Session(3), 3, 5).Reason, Is.EqualTo("chat-egress-global-budget"));
    }
    [Test]
    public void AccountCapacityAndExpiryHaveExplicitBoundedBehavior()
    {
        var clock = new Clock(); var budget = new M16RequestEgressBudget(clock, Options() with { AccountCapacity = 1 });
        Assert.That(budget.Consume(Session(), 1, 5).Allowed, Is.True);
        Assert.That(budget.Consume(Session(2), 2, 5).Allowed, Is.False);
        Assert.That(budget.AccountBucketCount, Is.EqualTo(1)); clock.Advance(121);
        Assert.That(budget.Consume(Session(3), 2, 5).Allowed, Is.True);
        Assert.That(budget.AccountBucketCount, Is.EqualTo(1));
    }
    [TestCase(0)] [TestCase(4)] [TestCase(65536)]
    public void InvalidEnvelopeLengthNeverAllocatesOrCounts(int bytes)
    {
        var budget = new M16RequestEgressBudget(new Clock(), Options());
        Assert.That(budget.Consume(Session(), 1, bytes).Allowed, Is.False);
        Assert.That(budget.AccountBucketCount, Is.Zero);
        Assert.That(budget.BlockedBytes, Is.Zero);
    }
}
