using System.Security.Cryptography;
using AntiCheat.DevMcp;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture, NonParallelizable]
public sealed class AtomicPublicationAndJobCollisionNativeTests
{
    [Test]
    public async Task RepeatedAtomicPublicationAndConcurrentBoundedReadsReturnOnlyCompleteOldOrNewBytes()
    {
        using var lab = new NativeTestDirectory();
        string path = lab.File("published.json");
        const int limit = 65536, publicationCount = 48, minimumReads = 256, maximumReads = 2048;
        byte[] oldBytes = Enumerable.Range(0, 8191).Select(i => (byte)(i % 251)).ToArray();
        byte[] newBytes = Enumerable.Range(0, 32789).Select(i => (byte)(255 - i % 251)).ToArray();
        using (var initial = WindowsPathPins.ForFile(path)) initial.AtomicWrite(oldBytes, limit);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var readerReady = new ManualResetEventSlim();
        using var newValueSeen = new ManualResetEventSlim();
        int publications = 0, reads = 0, oldSeen = 0, newSeen = 0, readsDuringPublication = 0, writerDone = 0;
        // Exactly two bounded test workers use real Windows handles, not a mocked file stream.
        var writer = Task.Factory.StartNew(() =>
        {
            try
            {
                using var pins = WindowsPathPins.ForFile(path);
                readerReady.Wait(deadline.Token);
                for (int index = 0; index < publicationCount; index++)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    pins.AtomicWrite(index % 2 == 0 ? newBytes : oldBytes, limit);
                    Interlocked.Increment(ref publications);
                    if (index == 0) newValueSeen.Wait(deadline.Token);
                    Thread.Yield();
                }
            }
            finally { Volatile.Write(ref writerDone, 1); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var reader = Task.Factory.StartNew(() =>
        {
            try
            {
                using var pins = WindowsPathPins.ForFile(path);
                while (Volatile.Read(ref writerDone) == 0 || reads < minimumReads)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (reads >= maximumReads) { Thread.Sleep(1); continue; }
                    byte[] observed = pins.ReadBounded(limit, allowAtomicReplace: true);
                    if (observed.AsSpan().SequenceEqual(oldBytes)) Interlocked.Increment(ref oldSeen);
                    else if (observed.AsSpan().SequenceEqual(newBytes))
                    { Interlocked.Increment(ref newSeen); newValueSeen.Set(); }
                    else throw new InvalidDataException("A concurrent read was neither complete published version; bytes=" + observed.Length);
                    Interlocked.Increment(ref reads);
                    if (Volatile.Read(ref writerDone) == 0 && Volatile.Read(ref publications) > 0)
                        Interlocked.Increment(ref readsDuringPublication);
                    readerReady.Set();
                    Thread.Yield();
                }
            }
            catch { deadline.Cancel(); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await Task.WhenAll(reader, writer); }
        finally
        {
            lab.Record("concurrent-atomic-publication", new
            {
                path, limit, publicationCount, publications, reads, oldSeen, newSeen, readsDuringPublication,
                oldSha256 = Convert.ToHexString(SHA256.HashData(oldBytes)), newSha256 = Convert.ToHexString(SHA256.HashData(newBytes)),
                readerFailure = reader.Exception?.ToString(), writerFailure = writer.Exception?.ToString()
            });
        }
        using var final = WindowsPathPins.ForFile(path);
        Assert.Multiple(() =>
        {
            Assert.That(publications, Is.EqualTo(publicationCount));
            Assert.That(reads, Is.InRange(minimumReads, maximumReads));
            Assert.That(oldSeen, Is.Positive);
            Assert.That(newSeen, Is.Positive);
            Assert.That(readsDuringPublication, Is.Positive, "Reads must overlap the ongoing publication sequence.");
            Assert.That(final.ReadBounded(limit, allowAtomicReplace: true), Is.EqualTo(oldBytes));
        });
    }

    [Test]
    public void ActualByteReadBoundAcceptsItsExactLimitAndRejectsOneAdditionalByte()
    {
        using var lab = new NativeTestDirectory();
        string path = lab.File("bounded-input.bin");
        const int readLimit = 4096;
        byte[] exact = Enumerable.Range(0, readLimit).Select(i => (byte)(i % 251)).ToArray();
        byte[] oversized = exact.Append((byte)253).ToArray();
        using var pins = WindowsPathPins.ForFile(path);
        pins.AtomicWrite(exact, readLimit + 1);
        Assert.That(pins.ReadBounded(readLimit), Is.EqualTo(exact), "The exact boundary remains a valid read.");
        pins.AtomicWrite(oversized, readLimit + 1);
        Assert.Throws<InvalidDataException>(() => pins.ReadBounded(readLimit, allowAtomicReplace: true));
        Assert.That(pins.ReadBounded(readLimit + 1), Is.EqualTo(oversized), "The rejected read must leave the real input intact.");
        lab.Record("actual-read-limit", new { path, readLimit, acceptedBytes = exact.Length, rejectedBytes = oversized.Length });
    }

    [Test]
    public async Task ExplicitJobNameCollisionRejectsWithoutTerminatingTheExistingOwnedProcess()
    {
        using var lab = new NativeTestDirectory();
        string jobName = "Local\\terraria-dev-" + Guid.NewGuid().ToString("N");
        var environment = new Dictionary<string, string> { [NativeFixtureProgram.TokenVariable] = lab.Token };
        using var first = OwnedProcess.Start(NativeFixtureProgram.Executable,
            [NativeFixtureProgram.EntryArgument, "hold", lab.Root, "sentinel"], lab.Root, environment,
            lab.File("named-first-stdout.log"), lab.File("named-first-stderr.log"), requestedJobName: jobName);
        var firstIdentity = await lab.Identity("sentinel");
        Assert.That(OwnedProcess.IsSameProcessAlive(firstIdentity), Is.True);
        var initialMembers = first.GetMembers();
        lab.Record("job-name-collision-before", new { jobName, firstIdentity, initialMembers, first.ActiveProcessCount });
        Assert.Multiple(() =>
        {
            Assert.That(initialMembers, Does.Contain(firstIdentity));
            Assert.That(first.MemberSnapshotIncomplete, Is.False);
            Assert.That(first.ActiveProcessCount, Is.EqualTo(initialMembers.Count));
            Assert.That(initialMembers.Where(x => x != firstIdentity).All(x => string.Equals(x.ExecutablePath,
                Path.Combine(Environment.SystemDirectory, "conhost.exe"), StringComparison.OrdinalIgnoreCase)), Is.True);
        });

        var failure = Assert.Throws<IOException>(() =>
        {
            using var rejected = OwnedProcess.Start(NativeFixtureProgram.Executable,
                [NativeFixtureProgram.EntryArgument, "echo", "collision must not run"], lab.Root, environment,
                lab.File("named-collision-stdout.log"), lab.File("named-collision-stderr.log"), requestedJobName: jobName);
        });
        var inspection = OwnedProcess.InspectJob(jobName);
        var survivors = first.GetMembers();
        lab.Record("job-name-collision", new { jobName, firstIdentity, rejection = failure!.Message, inspection, survivors });
        Assert.Multiple(() =>
        {
            Assert.That(OwnedProcess.IsSameProcessAlive(firstIdentity), Is.True, "Collision cleanup must not terminate the pre-existing job.");
            Assert.That(inspection.Exists, Is.True);
            Assert.That(inspection.ErrorCode, Is.Null);
            Assert.That(inspection.ActiveProcessCount, Is.EqualTo(initialMembers.Count));
            Assert.That(first.MemberSnapshotIncomplete, Is.False);
            Assert.That(survivors, Is.EquivalentTo(initialMembers), "Collision rejection must preserve the entire existing identity set, including its system console host.");
            Assert.That(initialMembers.Select(OwnedProcess.IsSameProcessAlive), Has.All.EqualTo(true));
            Assert.That(first.Cleanup.TerminationRequested, Is.False);
        });
        first.TerminateOwnedTree("native-test-completed-job-name-collision");
        Assert.That(await lab.Wait(first), Is.Not.Zero);
        Assert.That(first.Cleanup.ActiveJobProcesses, Is.Zero);
        Assert.That(first.Cleanup.LogsDrained && !first.Cleanup.TimedOut, Is.True);
        Assert.That(initialMembers.Select(OwnedProcess.IsSameProcessAlive), Has.All.EqualTo(false));
    }
}
