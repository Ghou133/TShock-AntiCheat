using AntiCheat.Persistence;
using NUnit.Framework;

namespace AntiCheat.Persistence.Tests;

[TestFixture]
public sealed class M18ObservationJournalTests
{
    [Test]
    public void StrikeSamplesAndHigherMaxSurviveRestartWithoutReasonDeduplication()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "m18-observations.json");
        try
        {
            using (var journal = new M18ObservationJournal(file))
            {
                journal.RecordStrike(Strike(1, 1, "npc-strike-queue-record-only"));
                journal.RecordStrike(Strike(9, 9, "npc-strike-queue-record-only"));
                Assert.That(journal.Flush(), Is.True);
                Assert.That(journal.Healthy, Is.True);
            }

            using var reopened = new M18ObservationJournal(file);
            var bucket = reopened.Snapshot().StrikeBuckets.Single();
            Assert.Multiple(() =>
            {
                Assert.That(bucket.EventCount, Is.EqualTo(2));
                Assert.That(bucket.MaxWireDamage, Is.EqualTo(9));
                Assert.That(bucket.MaxReceiverDamage, Is.EqualTo(9));
                Assert.That(bucket.Samples, Has.Count.EqualTo(2));
                Assert.That(bucket.Samples[0].WireDamage, Is.EqualTo(1));
                Assert.That(bucket.Samples[1].WireDamage, Is.EqualTo(9));
            });
        }
        finally { DeleteExact(root); }
    }

    [Test]
    public void PeakSamplesRetainFullContextAfterTheBoundedSampleWindowAndRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "m18-observations.json");
        try
        {
            using (var journal = new M18ObservationJournal(file))
            {
                for (int i = 0; i < 10; i++)
                    journal.RecordStrike(Strike(i + 1, i + 1, "peak-context", targetGeneration: i + 10));
                Assert.That(journal.Flush(), Is.True);
            }

            using var reopened = new M18ObservationJournal(file);
            var bucket = reopened.Snapshot().StrikeBuckets.Single();
            Assert.Multiple(() =>
            {
                Assert.That(bucket.Samples, Has.Count.EqualTo(8));
                Assert.That(bucket.SamplesDropped, Is.EqualTo(2));
                Assert.That(bucket.PeakWireSample, Is.Not.Null);
                Assert.That(bucket.PeakWireSample!.TargetGeneration, Is.EqualTo(19));
                Assert.That(bucket.PeakWireSample.Reason, Is.EqualTo("peak-context"));
                Assert.That(bucket.PeakReceiverSample!.TargetGeneration, Is.EqualTo(19));
            });
        }
        finally { DeleteExact(root); }
    }

    [Test]
    public void BucketCapacityRotatesAndFileBoundTrimsWithoutFaultingObservation()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "m18-observations.json");
        try
        {
            using (var journal = new M18ObservationJournal(file, new(
                MaxStrikeBuckets: 2,
                MaxImportantItemBuckets: 2,
                MaxSamplesPerBucket: 8,
                MaxFileBytes: 16_384)))
            {
                journal.RecordStrike(StrikeForBucket(1));
                journal.RecordStrike(StrikeForBucket(2));
                journal.RecordStrike(StrikeForBucket(3));
                Assert.Multiple(() =>
                {
                    Assert.That(journal.StrikeBucketCount, Is.EqualTo(2));
                    Assert.That(journal.StrikeBucketsEvicted, Is.EqualTo(1));
                    Assert.That(journal.Healthy, Is.True);
                });
                Assert.That(journal.Flush(), Is.True);
            }

            string boundedFile = Path.Combine(root, "bounded", "m18-observations.json");
            using (var bounded = new M18ObservationJournal(boundedFile, new(
                MaxStrikeBuckets: 128,
                MaxImportantItemBuckets: 2,
                MaxSamplesPerBucket: 8,
                MaxFileBytes: 16_384)))
            {
                for (int bucket = 1; bucket <= 32; bucket++)
                    for (int sample = 0; sample < 8; sample++)
                        bounded.RecordStrike(StrikeForBucket(bucket));
                Assert.That(bounded.Flush(), Is.True);
                Assert.That(bounded.StrikeBucketsEvicted, Is.GreaterThan(0));
            }
            Assert.That(new FileInfo(boundedFile).Length, Is.LessThanOrEqualTo(16_384));
        }
        finally { DeleteExact(root); }
    }

    [Test]
    public void FlushRefreshesAtTheBatchBoundaryAndBacksOffAfterAStorageFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        string validFile = Path.Combine(root, "valid", "m18-observations.json");
        string blocker = Path.Combine(root, "blocker");
        try
        {
            using (var journal = new M18ObservationJournal(validFile))
            {
                for (int i = 0; i < 16; i++) journal.RecordStrike(Strike(i + 1, i + 1, "batch-refresh"));
                journal.FlushIfDue(TimeSpan.FromHours(1));
                Assert.That(journal.FlushAttemptCount, Is.EqualTo(1));

                // A second full batch arriving before the interval must not
                // defeat the time gate. A zero interval below acts as the
                // deterministic due-now check for the same dirty journal.
                for (int i = 0; i < 16; i++) journal.RecordStrike(Strike(i + 17, i + 17, "batch-refresh-2"));
                journal.FlushIfDue(TimeSpan.FromHours(1));
                Assert.That(journal.FlushAttemptCount, Is.EqualTo(1));
                journal.FlushIfDue(TimeSpan.Zero);
                Assert.That(journal.FlushAttemptCount, Is.EqualTo(2));
            }

            Directory.CreateDirectory(root);
            File.WriteAllText(blocker, "not a directory");
            using var failed = new M18ObservationJournal(Path.Combine(blocker, "m18-observations.json"));
            failed.RecordStrike(Strike(4, 4, "retry-boundary"));
            Assert.That(failed.Flush(), Is.False);
            failed.FlushIfDue(TimeSpan.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(failed.FlushAttemptCount, Is.EqualTo(1));
                Assert.That(failed.FlushFailureCount, Is.EqualTo(1));
                Assert.That(failed.Healthy, Is.False);
            });
        }
        finally { DeleteExact(root); }
    }

    [Test]
    public void ImportantItemQuantityMergeRetainsPassiveAndUnknownSourceRecordsWithoutSanction()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "m18-observations.json");
        try
        {
            using var journal = new M18ObservationJournal(file);
            journal.RecordImportantItem(Item(0, 0, "Inventory", "Unknown", "normal-quantity-observation"));
            journal.RecordImportantItem(Item(5, 5, "Container", "Unknown", "normal-quantity-observation"));
            Assert.That(journal.Flush(), Is.True);

            var bucket = journal.Snapshot().ImportantItemBuckets.Single();
            Assert.Multiple(() =>
            {
                Assert.That(bucket.EventCount, Is.EqualTo(2));
                Assert.That(bucket.TotalDelta, Is.EqualTo(5));
                Assert.That(bucket.Samples.Select(x => x.Source), Is.EqualTo(new[] { "Inventory", "Container" }));
                Assert.That(bucket.Samples.All(x => x.Action == "Unknown" && x.Verdict == "Unknown"), Is.True);
                Assert.That(journal.Healthy, Is.True);
            });
        }
        finally { DeleteExact(root); }
    }

    [Test]
    public void StorageFailureOnlyMarksObservationHealthAndDoesNotCreateAControlDecision()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string blocker = Path.Combine(root, "parent-blocker");
        File.WriteAllText(blocker, "not a directory");
        string file = Path.Combine(blocker, "m18-observations.json");
        try
        {
            using var journal = new M18ObservationJournal(file);
            journal.RecordStrike(Strike(4, 4, "npc-strike-queue-record-only"));
            Assert.That(journal.Flush(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(journal.Healthy, Is.False);
                Assert.That(journal.Snapshot().StrikeBuckets, Has.Count.EqualTo(1));
                Assert.That(journal.LastErrorType, Is.Not.Null);
            });
        }
        finally { DeleteExact(root); }
    }

    [Test]
    public void MalformedPersistedSnapshotOnlyMarksObservationHealth()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ObservationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "m18-observations.json");
        try
        {
            File.WriteAllText(file, "{\"SchemaVersion\":1,\"SavedUtc\":\"2026-09-16T00:00:00Z\",\"StrikeBuckets\":null,\"ImportantItemBuckets\":[]}");
            using var journal = new M18ObservationJournal(file);
            Assert.Multiple(() =>
            {
                Assert.That(journal.Healthy, Is.False);
                Assert.That(journal.LastErrorType, Is.EqualTo(nameof(InvalidDataException)));
                Assert.That(journal.Snapshot().StrikeBuckets, Is.Empty);
                Assert.That(journal.Snapshot().ImportantItemBuckets, Is.Empty);
            });
        }
        finally { DeleteExact(root); }
    }

    private static M18StrikeObservationRecord Strike(int wire, int receiver, string reason, int targetGeneration = 3)
        => new("run/1/7/1", 6207, 1, 41, "PreHardmode", 11, targetGeneration, 50, wire, receiver,
            1000, 20000, true, true, true, true, true, true, false, false,
            "client-packet28", "Unknown", "Unknown", reason, DateTimeOffset.UtcNow);

    private static M18StrikeObservationRecord StrikeForBucket(int bucket)
        => new($"run/{bucket}/7/1", 6207, bucket, 41, "PreHardmode", 11, 3, 50, bucket, bucket,
            1000, 20000, true, true, true, true, true, true, false, false,
            "client-packet28", "Unknown", "Unknown", "bucket-rotation", DateTimeOffset.UtcNow);

    private static M18ImportantItemObservationRecord Item(int delta, int stack,
        string source, string verdict, string reason)
        => new("run/1/7/1", 6207, 1, -1, 0, 0, 4, 3318, "MoonLordBossBag", stack,
            stack - delta, delta, 9999, source, false, false, true, delta > 0, false,
            "Unknown", verdict, reason, "record-only", DateTimeOffset.UtcNow);

    private static void DeleteExact(string root)
    {
        if (Directory.Exists(root) && Path.GetFileName(root).Length == 32)
            Directory.Delete(root, recursive: true);
    }
}
