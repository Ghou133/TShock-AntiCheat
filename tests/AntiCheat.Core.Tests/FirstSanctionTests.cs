using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class FirstSanctionTests
{
    [Test]
    public async Task LegalSelfSlotDoesNotChangeInteractionOrBan()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var result = lab.Engine.Observe(new(key, 5, key.Slot, Lab.Complete));
        Assert.Multiple(() =>
        {
            Assert.That(result.Behavior, Is.EqualTo(ControlAction.Pass));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(lab.Engine.CanWrite(key), Is.True);
            Assert.That(lab.Engine.PendingCount, Is.Zero);
            Assert.That(lab.Bans.Calls, Is.Zero);
        });
    }

    [Test]
    public async Task FirstProvenEventBlocksSynchronouslyThenPersistsOneAccountBan()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var result = lab.Engine.Observe(Lab.Violation(key));
        Assert.Multiple(() =>
        {
            Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(result.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Incident!.Evidence.Preconditions.Complete, Is.True);
            Assert.That(result.Incident.Evidence.Qualification, Is.EqualTo(RuleQualification.TestLab));
            Assert.That(lab.Engine.CanWrite(key), Is.False, "Revocation precedes persistence.");
            Assert.That(lab.Bans.Calls, Is.Zero);
            Assert.That(lab.Journal.Appends, Is.Zero, "Observe never performs I/O.");
        });
        var pump = await lab.Engine.PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(pump.Applied, Is.EqualTo(1));
            Assert.That(lab.Bans.Accounts.Keys, Is.EquivalentTo(new long[] { 100 }));
            Assert.That(lab.Journal.All.Count, Is.EqualTo(1));
            Assert.That(lab.Engine.PendingCount, Is.Zero);
        });
    }
}
