using AntiCheat.Core;
using AntiCheat.Persistence;
using NUnit.Framework;

namespace AntiCheat.Persistence.Tests;

[TestFixture]
public sealed class FileJournalTests
{
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "AntiCheat-IsolatedTests", Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static BanIntent Intent(long account = 7)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        var evidence = new Evidence(id, "A02.SelfSlot", "1", new(Guid.NewGuid(), 1, 0, 1), account, now,
            "test-lab", "1", "test-fixture", RuleQualification.TestLab,
            new(true, true, true, true, true, true, true, true, true), 13, 0, 2);
        return new(id, account, evidence, now);
    }

    [Test] public async Task PendingIntentSurvivesRestartAndAppliedIsNotReplayed()
    {
        var intent = Intent();
        using (var journal = new FileEnforcementJournal(directory)) await journal.AppendAsync(intent);
        using (var journal = new FileEnforcementJournal(directory))
        {
            Assert.That(await journal.ReadAsync(), Is.EqualTo(new[] { intent }));
            await journal.AppendAsync(intent);
            await journal.MarkAppliedAsync(intent.IncidentId);
            await journal.MarkAppliedAsync(intent.IncidentId);
        }
        using var reopened = new FileEnforcementJournal(directory);
        Assert.That(await reopened.ReadAsync(), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.json"), Has.Length.EqualTo(1), "Completed evidence is retained.");
    }

    [Test] public async Task CapacityNeverEvictsPendingIntent()
    {
        using var journal = new FileEnforcementJournal(directory, new(MaxRecords: 1, AppliedRetention: TimeSpan.Zero));
        var first = Intent();
        await journal.AppendAsync(first);
        Assert.ThrowsAsync<IOException>(async () => await journal.AppendAsync(Intent(8)));
        Assert.That(await journal.ReadAsync(), Is.EqualTo(new[] { first }));
        await journal.MarkAppliedAsync(first.IncidentId);
        var second = Intent(8);
        await journal.AppendAsync(second);
        Assert.That(await journal.ReadAsync(), Is.EqualTo(new[] { second }));
    }

    [Test] public async Task ConcurrentDuplicateAppendsProduceOneIntent()
    {
        using var journal = new FileEnforcementJournal(directory);
        var intent = Intent();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => journal.AppendAsync(intent).AsTask()));
        Assert.That(await journal.ReadAsync(), Has.Count.EqualTo(1));
    }

    [Test] public async Task ConflictingIncidentIsRejected()
    {
        using var journal = new FileEnforcementJournal(directory);
        var intent = Intent();
        await journal.AppendAsync(intent);
        var conflicting = intent with { CreatedUtc = intent.CreatedUtc.AddDays(1) };
        Assert.ThrowsAsync<InvalidDataException>(async () => await journal.AppendAsync(conflicting));
        Assert.That(await journal.ReadAsync(), Is.EqualTo(new[] { intent }));
    }

    [Test] public async Task TruncatedRecordFailsRecovery()
    {
        var intent = Intent();
        using (var journal = new FileEnforcementJournal(directory)) await journal.AppendAsync(intent);
        await File.WriteAllTextAsync(Path.Combine(directory, intent.IncidentId.ToString("N") + ".json"), "{");
        using var reopened = new FileEnforcementJournal(directory);
        Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await reopened.ReadAsync());
    }

    [Test] public async Task IncompleteProofIsRejectedBeforeWriting()
    {
        using var journal = new FileEnforcementJournal(directory);
        var intent = Intent();
        intent = intent with { Evidence = intent.Evidence with { Preconditions = intent.Evidence.Preconditions with { Authenticated = false } } };
        Assert.ThrowsAsync<InvalidDataException>(async () => await journal.AppendAsync(intent));
        Assert.That(await journal.ReadAsync(), Is.Empty);
    }

    [Test] public async Task OversizedRecordDoesNotLeavePartialIntent()
    {
        using var journal = new FileEnforcementJournal(directory, new(MaxRecordBytes: 256));
        Assert.ThrowsAsync<InvalidDataException>(async () => await journal.AppendAsync(Intent()));
        Assert.That(await journal.ReadAsync(), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
    }

    [Test] public void TwoWritersCannotOpenSameDirectory()
    {
        using var journal = new FileEnforcementJournal(directory);
        Assert.Throws<IOException>(() => { using var duplicate = new FileEnforcementJournal(directory); });
    }

    [Test] public async Task InterruptedReplacementKeepsCommittedPendingIntent()
    {
        var intent = Intent();
        using (var journal = new FileEnforcementJournal(directory)) await journal.AppendAsync(intent);
        await File.WriteAllTextAsync(Path.Combine(directory, intent.IncidentId.ToString("N") + ".tmp"), "incomplete replacement");
        using var recovered = new FileEnforcementJournal(directory);
        Assert.That(await recovered.ReadAsync(), Is.EqualTo(new[] { intent }));
        Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
    }

    [Test] public async Task CancelledAppendDoesNotBecomeDurable()
    {
        using var journal = new FileEnforcementJournal(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.That(async () => await journal.AppendAsync(Intent(), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await journal.ReadAsync(), Is.Empty);
    }

    [Test] public async Task CompleteFirstTemporaryIntentIsPromotedInsteadOfDeleted()
    {
        var intent = Intent();
        using (var journal = new FileEnforcementJournal(directory)) await journal.AppendAsync(intent);
        var committed = Path.Combine(directory, intent.IncidentId.ToString("N") + ".json");
        var temporary = Path.ChangeExtension(committed, ".tmp");
        // Simulate a process boundary after the complete file flush but before its first rename.
        File.Move(committed, temporary);
        using var reopened = new FileEnforcementJournal(directory);
        Assert.That(await reopened.ReadAsync(), Is.EqualTo(new[] { intent }));
        Assert.That(File.Exists(committed), Is.True);
        Assert.That(File.Exists(temporary), Is.False);
    }

    [Test] public async Task IncompleteOnlyTemporaryRecordIsRetainedAndFailsRecovery()
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(temporary, "{");
        using var journal = new FileEnforcementJournal(directory);
        Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await journal.ReadAsync());
        Assert.That(File.Exists(temporary), Is.True, "Evidence of an interrupted first intent cannot be hidden.");
    }

    [Test] public async Task CompleteTemporaryAcknowledgementIsPromotedBeforeConservativeReplayReset()
    {
        var intent = Intent();
        using (var journal = new FileEnforcementJournal(directory))
        {
            await journal.AppendAsync(intent);
            await journal.MarkAppliedAsync(intent.IncidentId);
        }
        var committed = Path.Combine(directory, intent.IncidentId.ToString("N") + ".json");
        File.Move(committed, Path.ChangeExtension(committed, ".tmp"));
        using var reopened = new FileEnforcementJournal(directory);
        Assert.That(await reopened.ReadAsync(), Is.EqualTo(new[] { intent }));
        Assert.That(File.Exists(committed), Is.True);
        Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
    }

    [Test] public async Task TemporaryRecordOverflowFailsBeforeDeletingAnyEvidence()
    {
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 3; index++)
            await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"), "{");
        using var journal = new FileEnforcementJournal(directory, new(MaxRecords: 2));
        Assert.ThrowsAsync<IOException>(async () => await journal.ReadAsync());
        Assert.That(Directory.GetFiles(directory, "*.tmp"), Has.Length.EqualTo(3));
    }

    [Test] public void IntegerOverflowOrUnboundedConfigurationIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileEnforcementJournal(directory, new(MaxRecords: int.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileEnforcementJournal(directory, new(MaxRecordBytes: int.MaxValue)));
    }
}
