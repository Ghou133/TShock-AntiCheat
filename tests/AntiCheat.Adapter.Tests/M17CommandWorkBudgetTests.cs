using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

public sealed class M17CommandWorkBudgetTests
{
    private sealed class Clock : TimeProvider
    { public long Ticks; public override long TimestampFrequency => TimeSpan.TicksPerSecond; public override long GetTimestamp() => Ticks; public void Advance(int seconds) => Ticks += seconds * TimeSpan.TicksPerSecond; }
    private static SessionKey Session(int slot = 1) => new(Guid.NewGuid(), 1, slot, 1);
    [Test]
    public void NormalRegisterTwoFailedLoginsAndSuccessRemainWithinInitialAllowance()
    {
        var budget = new M17CommandWorkBudget(new Clock()); var session = Session();
        Assert.That(budget.Consume(session, null, M17CommandWorkKind.Register).Allowed, Is.True);
        for (int i = 0; i < 3; i++) Assert.That(budget.Consume(session, null, M17CommandWorkKind.Login).Allowed, Is.True);
        Assert.That(budget.AccountBucketCount, Is.Zero, "Attempted account names never allocate authenticated buckets.");
    }
    [Test]
    public void NormalRegisterLoginAndPasswordChangeUseExistingTransparentFlow()
    {
        var budget = new M17CommandWorkBudget(new Clock()); var session = Session();
        Assert.That(budget.Consume(session, null, M17CommandWorkKind.Register).Allowed, Is.True);
        Assert.That(budget.Consume(session, null, M17CommandWorkKind.Login).Allowed, Is.True);
        Assert.That(budget.Consume(session, 71, M17CommandWorkKind.PasswordChange).Allowed, Is.True);
        Assert.That(budget.AccountBucketCount, Is.EqualTo(1));
    }
    [Test]
    public void ThirtyTwoIndependentSameNatClientsCanRegisterAndLoginWithoutACombinedSourcePenalty()
    {
        var budget = new M17CommandWorkBudget(new Clock());
        for (int slot = 0; slot < 32; slot++)
        { var session = Session(slot); Assert.That(budget.Consume(session, null, M17CommandWorkKind.Register).Allowed, Is.True); Assert.That(budget.Consume(session, null, M17CommandWorkKind.Login).Allowed, Is.True); }
        Assert.That(budget.Admitted, Is.EqualTo(64)); Assert.That(budget.AccountBucketCount, Is.Zero);
    }
    [Test]
    public void AuthenticatedAccountSurvivesReconnectWhileDifferentAccountAndAnonymousGenerationAreIndependent()
    {
        var budget = new M17CommandWorkBudget(new Clock()); var first = Session();
        for (int i = 0; i < 4; i++) Assert.That(budget.Consume(first, 7, M17CommandWorkKind.Login).Allowed, Is.True);
        budget.Forget(first); var next = first with { Generation = 2 };
        Assert.That(budget.Consume(next, 7, M17CommandWorkKind.Login).Reason, Is.EqualTo("command-account-work-budget"));
        Assert.That(budget.Consume(next, 8, M17CommandWorkKind.Login).Allowed, Is.True);
        Assert.That(budget.Consume(first with { Generation = 3 }, null, M17CommandWorkKind.Login).Allowed, Is.True);
    }
    [Test]
    public void LocalDeniedSpamCannotSpendGlobalPoolAndNaturalRefillAllowsRetry()
    {
        var clock = new Clock(); var budget = new M17CommandWorkBudget(clock, new() { GlobalBurst = 10 }); var session = Session();
        for (int i = 0; i < 4; i++) Assert.That(budget.Consume(session, null, M17CommandWorkKind.Login).Allowed, Is.True);
        for (int i = 0; i < 100; i++) Assert.That(budget.Consume(session, null, M17CommandWorkKind.Login).Allowed, Is.False);
        Assert.That(budget.Consume(Session(2), 2, M17CommandWorkKind.Login).Allowed, Is.True);
        Assert.That(budget.Consume(Session(3), 3, M17CommandWorkKind.Login).Reason, Is.EqualTo("command-global-work-budget"));
        clock.Advance(10); Assert.That(budget.Consume(session, null, M17CommandWorkKind.Login).Allowed, Is.True);
    }
    [Test]
    public void SessionCapacityIsBoundedAndForgetOnlyReleasesItsGeneration()
    {
        var budget = new M17CommandWorkBudget(new Clock(), new() { GlobalBurst = 1000 }); var run = Guid.NewGuid();
        for (int i = 0; i < 256; i++) Assert.That(budget.Consume(new(run, 1, i, 1), null, M17CommandWorkKind.Register).Allowed, Is.True);
        Assert.That(budget.SessionBucketCount, Is.EqualTo(256));
        Assert.That(budget.Consume(new(run, 1, 0, 2), null, M17CommandWorkKind.Register).Allowed, Is.False);
        budget.Forget(new(run, 1, 0, 1)); Assert.That(budget.Consume(new(run, 1, 0, 2), null, M17CommandWorkKind.Register).Allowed, Is.True);
        Assert.That(budget.SessionBucketCount, Is.EqualTo(256));
    }
}
