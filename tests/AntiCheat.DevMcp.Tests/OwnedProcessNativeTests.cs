using System.Diagnostics;
using System.Text.Json;
using AntiCheat.DevMcp;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture, NonParallelizable]
public sealed class OwnedProcessNativeTests
{
    [Test]
    public async Task ActualArgvPreservesUnicodeEmptyQuotesAndTrailingBackslashes()
    {
        using var lab = new NativeTestDirectory();
        string[] expected = ["", "中文 🌋", "plain", "two words", "a\"b", "\"quoted\"", "\\", @"C:\path with spaces\",
            "two\\\\before\"quote", "embedded\twhitespace", "line1\nline2", ""];
        var child = lab.Start(new[] { "echo" }.Concat(expected).ToArray());
        Assert.That(await lab.Wait(child), Is.Zero);
        string[] actual = JsonSerializer.Deserialize<string[]>(File.ReadAllText(child.Stdout.Path))!;
        lab.Record("actual-argv", actual);
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(child.Stdout.ReachedEof, Is.True);
            Assert.That(child.Stderr.ObservedBytes, Is.Zero);
            Assert.That(child.Cleanup.ActiveJobProcesses, Is.Zero);
            Assert.That(child.Cleanup.LogsDrained, Is.True);
            Assert.That(child.Cleanup.TerminationRequested, Is.False, "Normal argv completion must not require terminating its console host.");
        });
    }

    [Test]
    public async Task BothStreamsContinueDrainingAfterEachFourMiBLimit()
    {
        using var lab = new NativeTestDirectory();
        var child = lab.Start("flood");
        Assert.That(await lab.Wait(child), Is.Zero, "A child must not block after its log storage fills.");
        foreach (var (stream, value) in new[] { (child.Stdout, (byte)'O'), (child.Stderr, (byte)'E') })
        {
            Assert.Multiple(() =>
            {
                Assert.That(stream.ObservedBytes, Is.EqualTo(NativeFixtureProgram.FloodBytes));
                Assert.That(stream.WrittenBytes, Is.EqualTo(OwnedProcess.MaximumLogBytes));
                Assert.That(stream.DroppedBytes, Is.EqualTo(NativeFixtureProgram.FloodBytes - OwnedProcess.MaximumLogBytes));
                Assert.That(stream.Completed && stream.ReachedEof, Is.True);
                Assert.That(stream.Error, Is.Null);
                Assert.That(new FileInfo(stream.Path).Length, Is.EqualTo(OwnedProcess.MaximumLogBytes));
                Assert.That(File.ReadAllBytes(stream.Path).All(b => b == value), Is.True, "stdout and stderr must remain separate.");
            });
        }
        Assert.That(child.Cleanup.LogsDrained && !child.Cleanup.TimedOut, Is.True);
        Assert.That(child.Cleanup.TerminationRequested, Is.False, "Normal output completion must drain without forced descendant termination.");
    }

    [Test]
    public async Task TerminationKillsOwnedRootChildAndGrandchildButPreservesSameExeSentinel()
    {
        using var lab = new NativeTestDirectory();
        var sentinel = lab.StartSentinel();
        var sentinelIdentity = await lab.Identity("sentinel");
        var tree = lab.Start("tree-root", lab.Root);
        var root = await lab.Identity("root");
        var child = await lab.Identity("child");
        var grandchild = await lab.Identity("grandchild");
        var expectedFixtures = new[] { root, child, grandchild };
        await NativeTestDirectory.Until(() =>
        {
            var observed = tree.GetMembers();
            return expectedFixtures.All(expected => observed.Contains(expected));
        }, "the three exact owned fixture identities");
        var members = tree.GetMembers();
        lab.Record("owned-members-before-termination", members);
        Assert.Multiple(() =>
        {
            Assert.That(members.Where(x => string.Equals(x.ExecutablePath, sentinelIdentity.ExecutablePath, StringComparison.OrdinalIgnoreCase)),
                Is.EquivalentTo(expectedFixtures));
            Assert.That(tree.MemberSnapshotIncomplete, Is.False);
            Assert.That(tree.ActiveProcessCount, Is.EqualTo(members.Count));
            // Actual Windows diagnostics observed one owned System32 conhost per fixture.
            // Keep every host in the evidence and exit assertions; do not silently ignore it.
            Assert.That(members.Where(x => !expectedFixtures.Contains(x)).All(IsSystemConsoleHost), Is.True);
            Assert.That(members.Select(OwnedProcess.IsSameProcessAlive), Has.All.EqualTo(true));
            Assert.That(members.Any(x => x.Pid == sentinelIdentity.Pid), Is.False);
            Assert.That(OwnedProcess.IsSameProcessAlive(sentinelIdentity), Is.True);
        });
        tree.TerminateOwnedTree("native-test-owned-tree");
        Assert.That(await lab.Wait(tree), Is.Not.Zero);
        AssertClean(tree);
        Assert.Multiple(() =>
        {
            Assert.That(members.Select(OwnedProcess.IsSameProcessAlive), Has.All.EqualTo(false), "Every observed owned member, including console hosts, must exit.");
            Assert.That(OwnedProcess.IsSameProcessAlive(sentinelIdentity), Is.True, "An independent process of the same image must remain alive.");
            Assert.That(sentinel.HasExited, Is.False);
        });
        lab.Record("sentinel-survived-owned-termination", sentinelIdentity);
    }

    [Test]
    public async Task NaturalRootExitReapsStillRunningChildAndGrandchild()
    {
        using var lab = new NativeTestDirectory();
        var tree = lab.Start("tree-root-exit", lab.Root);
        var root = await lab.Identity("root");
        var child = await lab.Identity("child");
        var grandchild = await lab.Identity("grandchild");
        Assert.Multiple(() =>
        {
            Assert.That(OwnedProcess.IsSameProcessAlive(child), Is.True);
            Assert.That(OwnedProcess.IsSameProcessAlive(grandchild), Is.True);
        });
        Assert.That(await lab.Wait(tree), Is.Zero, "The actual root exit code must be retained.");
        AssertClean(tree);
        Assert.Multiple(() =>
        {
            Assert.That(tree.Cleanup.TerminationRequested, Is.True);
            Assert.That(tree.Cleanup.Reason, Is.EqualTo("root-exited-descendant-cleanup"));
            Assert.That(new[] { root, child, grandchild }.Select(OwnedProcess.IsSameProcessAlive), Has.All.EqualTo(false));
        });
    }

    [Test]
    public async Task CanceledWaitReapsTheActualOwnedTreeBeforeThrowing()
    {
        using var lab = new NativeTestDirectory();
        var tree = lab.Start("tree-root", lab.Root);
        var root = await lab.Identity("root");
        var child = await lab.Identity("child");
        var grandchild = await lab.Identity("grandchild");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await tree.WaitForExitAsync(canceled.Token));
        AssertClean(tree);
        Assert.That(new[] { root, child, grandchild }.Select(OwnedProcess.IsSameProcessAlive), Has.All.EqualTo(false));
        lab.Record("canceled-wait-cleanup", tree.Cleanup);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExistingStdoutOrStderrEvidenceIsNeverOverwritten(bool stdoutExists)
    {
        using var lab = new NativeTestDirectory();
        string stdout = lab.File("existing-stdout.log"), stderr = lab.File("existing-stderr.log");
        string existing = stdoutExists ? stdout : stderr;
        byte[] sentinel = [0, 255, 19, 42, 10]; File.WriteAllBytes(existing, sentinel);
        Assert.That(() =>
        {
            using var process = OwnedProcess.Start(NativeFixtureProgram.Executable,
                [NativeFixtureProgram.EntryArgument, "echo", "must-not-run"], lab.Root,
                new Dictionary<string, string> { [NativeFixtureProgram.TokenVariable] = lab.Token }, stdout, stderr);
        }, Throws.Exception);
        Assert.That(File.ReadAllBytes(existing), Is.EqualTo(sentinel));
        lab.Record("existing-log-preserved", new { existing, bytes = Convert.ToHexString(File.ReadAllBytes(existing)) });
    }

    private static void AssertClean(OwnedProcess process) => Assert.Multiple(() =>
    {
        Assert.That(process.Cleanup.RootExited, Is.True);
        Assert.That(process.Cleanup.ActiveJobProcesses, Is.Zero);
        Assert.That(process.Cleanup.LogsDrained, Is.True);
        Assert.That(process.Cleanup.TimedOut, Is.False);
        Assert.That(process.Cleanup.Error, Is.Null);
        Assert.That(process.AllExited, Is.True);
    });

    private static bool IsSystemConsoleHost(OwnedProcessMember member) => string.Equals(member.ExecutablePath,
        Path.Combine(Environment.SystemDirectory, "conhost.exe"), StringComparison.OrdinalIgnoreCase);
}
