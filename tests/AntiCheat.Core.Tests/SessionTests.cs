using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class SessionTests
{
    [Test]
    public async Task SlotReuseCannotInheritOldEvidenceOrBeDisconnectedByOldCallback()
    {
        var lab = new Lab();
        var old = await lab.Login(4, 100);
        lab.Engine.Disconnect(old);
        var current = await lab.Login(4, 200);
        lab.Engine.Disconnect(old);
        var decision = lab.Engine.Observe(Lab.Violation(old));
        Assert.Multiple(() =>
        {
            Assert.That(current.Generation, Is.GreaterThan(old.Generation));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(lab.Engine.CanWrite(current), Is.True);
            Assert.That(lab.Engine.GetSession(current)!.AccountId, Is.EqualTo(200));
            Assert.That(lab.Engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public async Task WorldChangeInvalidatesOldEvidenceWithoutBanningNewSession()
    {
        var lab = new Lab();
        var old = await lab.Login();
        lab.Engine.AdvanceWorld();
        var current = await lab.Login();
        Assert.That(current.WorldEpoch, Is.GreaterThan(old.WorldEpoch));
        Assert.That(lab.Engine.Observe(Lab.Violation(old)).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(lab.Engine.CanWrite(current), Is.True);
        Assert.That(lab.Engine.PendingCount, Is.Zero);
    }

    [Test]
    public async Task ProcessRestartKeysAreNeverInterchangeable()
    {
        var one = new Lab();
        var two = new Lab();
        var old = await one.Login();
        var current = await two.Login();
        Assert.That(old.ServerRunId, Is.Not.EqualTo(current.ServerRunId));
        Assert.That(two.Engine.Observe(Lab.Violation(old)).Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(two.Engine.CanWrite(current), Is.True);
    }

    [Test]
    public async Task AuthenticatedIdentityCannotSilentlyChangeInOneGeneration()
    {
        var lab = new Lab();
        var key = await lab.Login();
        Assert.That(lab.Engine.Authenticate(key, 100), Is.EqualTo(AuthenticationResult.Authenticated));
        Assert.That(lab.Engine.Authenticate(key, 200), Is.EqualTo(AuthenticationResult.IdentityChangeRejected));
        Assert.That(lab.Engine.CanWrite(key), Is.False);
        Assert.That(lab.Engine.SanctionCount, Is.Zero);
    }

    [Test]
    public async Task AuthenticatedAccountOtherLiveSessionsAreRevokedInSameObservation()
    {
        var lab = new Lab();
        var first = await lab.Login(4, 100);
        var second = await lab.Login(5, 100);
        lab.Engine.Observe(Lab.Violation(first));
        Assert.That(lab.Engine.CanWrite(first), Is.False);
        Assert.That(lab.Engine.CanWrite(second), Is.False);
        Assert.That(lab.Engine.SanctionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ActivePacketTouchExtendsIdleTtlUsingMonotonicTimeOnly()
    {
        var lab = new Lab(new() { Scope = ExecutionScope.TestLab, SessionIdleTtl = TimeSpan.FromSeconds(10) });
        var key = await lab.Login();
        lab.Clock.JumpWallClock(TimeSpan.FromDays(365));
        Assert.That(lab.Engine.CanWrite(key), Is.True);
        lab.Clock.JumpWallClock(TimeSpan.FromDays(-730));
        lab.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.That(lab.Engine.Touch(key), Is.True);
        lab.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.That(lab.Engine.CanWrite(key), Is.True);
        lab.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(lab.Engine.CanWrite(key), Is.False);
        Assert.That(lab.Engine.Touch(key), Is.False, "Expired state cannot be revived by a stale packet.");
        Assert.That(lab.Engine.SanctionCount, Is.Zero);
    }

    [Test]
    public async Task SessionStorageHasFixedCapacityAndOutOfRangeSlotsAreRejected()
    {
        var lab = new Lab(new() { Scope = ExecutionScope.TestLab, MaxSessions = 2 });
        await lab.Engine.RecoverAsync();
        Assert.That(lab.Engine.OpenSession(-1), Is.Null);
        Assert.That(lab.Engine.OpenSession(2), Is.Null);
        Assert.That(lab.Engine.OpenSession(0), Is.Not.Null);
        Assert.That(lab.Engine.OpenSession(1), Is.Not.Null);
    }
}
