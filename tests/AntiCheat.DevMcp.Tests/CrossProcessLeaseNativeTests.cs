using AntiCheat.DevMcp;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture, NonParallelizable]
public sealed class CrossProcessLeaseNativeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task IndependentProcessesExcludeEachOtherThenReacquireAfterReleaseOrOwnerDeath(bool terminateOwner)
    {
        using var lab = new NativeTestDirectory();
        var first = lab.Start("lease", lab.Root, "first");
        var firstResult = await lab.ReadWhenReady<LeaseFixtureResult>("first.json");
        Assert.That(firstResult.Acquired, Is.True);

        var second = lab.Start("lease", lab.Root, "second");
        var secondResult = await lab.ReadWhenReady<LeaseFixtureResult>("second.json");
        Assert.Multiple(() =>
        {
            Assert.That(first.Pid, Is.Not.EqualTo(second.Pid));
            Assert.That(secondResult.Acquired, Is.False);
            Assert.That(secondResult.BlockedReason, Is.EqualTo("lease-held-by-another-open-handle"));
            Assert.That(OwnedProcess.IsSameProcessAlive(firstResult.Owner), Is.True);
        });
        Assert.That(await lab.Wait(second), Is.EqualTo(23));

        if (terminateOwner) first.TerminateOwnedTree("native-test-lease-owner-death");
        else File.WriteAllText(lab.File("first.release"), "release owned test lease");
        int firstCode = await lab.Wait(first);
        Assert.That(firstCode == 0, Is.EqualTo(!terminateOwner));

        var third = lab.Start("lease", lab.Root, "third");
        var thirdResult = await lab.ReadWhenReady<LeaseFixtureResult>("third.json");
        Assert.Multiple(() =>
        {
            Assert.That(thirdResult.Acquired, Is.True, "The exclusive handle must release after graceful exit or actual owner death.");
            Assert.That(thirdResult.BlockedReason, Is.Null);
            Assert.That(thirdResult.Owner.Pid, Is.EqualTo(third.Pid));
        });
        File.WriteAllText(lab.File("third.release"), "release owned test lease");
        Assert.That(await lab.Wait(third), Is.Zero);
        Assert.That(File.Exists(lab.File("shared.lock")), Is.True, "Releasing a lease must preserve the lock file identity.");
        lab.Record("cross-process-lease-results", new { terminateOwner, firstResult, secondResult, thirdResult });
    }

    [Test]
    public void ReusablePidMetadataCannotPretendToOwnTheCurrentLeaseHandle()
    {
        using var lab = new NativeTestDirectory();
        var identity = LeaseOwner.Current("native-current", "native-job");
        var stale = identity with { StartedUtc = identity.StartedUtc.AddSeconds(-1) };
        Assert.Throws<ArgumentException>(() => CrossProcessLease.TryAcquire(lab.File("forged.lock"), stale));
        Assert.That(File.Exists(lab.File("forged.lock")), Is.False);
        using var valid = CrossProcessLease.TryAcquire(lab.File("valid.lock"), identity).Lease;
        Assert.That(valid, Is.Not.Null);
    }

    private sealed record LeaseFixtureResult(bool Acquired, string? BlockedReason, OwnedProcessMember Owner);
}
