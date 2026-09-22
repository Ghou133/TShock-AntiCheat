using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Core;

namespace AntiCheat.Persistence;

public sealed record FileJournalOptions(int MaxRecords = 4096, int MaxRecordBytes = 16384,
    TimeSpan? AppliedRetention = null)
{
    public TimeSpan Retention => AppliedRetention ?? TimeSpan.FromDays(30);
}

/// <summary>Private local outbox. Never expires an unapplied intent. Capacity/corruption is a fault.</summary>
public sealed class FileEnforcementJournal : IEnforcementJournal, IRunSafetyGuard, IDisposable
{
    private sealed record Record(int Schema, bool Applied, DateTimeOffset? AppliedUtc, string Digest, BanIntent Intent);
    private readonly string directory;
    private readonly FileJournalOptions options;
    private readonly TimeProvider clock;
    private readonly FileStream ownership;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly RunSafetyGuard safety;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public FileEnforcementJournal(string directory, FileJournalOptions? options = null, TimeProvider? clock = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.options = options ?? new();
        this.clock = clock ?? TimeProvider.System;
        if (this.options.MaxRecords is < 1 or > 1_000_000 || this.options.MaxRecordBytes is < 256 or > 1_048_576 || this.options.Retention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        var legacyAtOpen = Directory.Exists(this.directory) &&
            Directory.EnumerateFileSystemEntries(this.directory).Any() &&
            !File.Exists(Path.Combine(this.directory, "run-safety.state"));
        Directory.CreateDirectory(this.directory);
        ownership = new FileStream(Path.Combine(this.directory, "journal.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        safety = new(this.directory, legacyAtOpen, this.clock);
    }

    public async ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var temporaryPaths = Directory.EnumerateFiles(directory, "*.tmp").Take(options.MaxRecords + 1).ToArray();
            if (temporaryPaths.Length > options.MaxRecords) throw new IOException("Enforcement temporary record capacity exceeded.");
            foreach (var temp in temporaryPaths)
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(temp), "N", out _))
                    throw new InvalidDataException("Unexpected journal temporary file.");
                var committed = Path.ChangeExtension(temp, ".json");
                if (File.Exists(committed))
                {
                    var authoritative = await ReadRecordAsync(committed, cancellationToken);
                    Record? replacement = null;
                    try { replacement = await ReadRecordAsync(temp, cancellationToken, temporary: true); }
                    catch (Exception exception) when (exception is JsonException or InvalidDataException) { }
                    if (replacement is not null && replacement.Intent != authoritative.Intent)
                        throw new InvalidDataException("Conflicting enforcement replacement.");
                    // A validated old record remains authoritative. Replaying its pending ban is safe
                    // even when an acknowledgement replacement was interrupted.
                    File.Delete(temp);
                }
                else
                {
                    // The only record may already be complete and flushed before an interrupted rename.
                    // Recover it; if it is incomplete, retain it and fail closed instead of hiding evidence loss.
                    var recoverable = await ReadRecordAsync(temp, cancellationToken, temporary: true);
                    if (BoundedPaths().Length >= options.MaxRecords) throw new IOException("Enforcement journal capacity exhausted.");
                    // Never truncate the only complete copy to promote it. Rename first; even an
                    // interrupted conservative acknowledgement reset then retains a valid committed record.
                    File.Move(temp, committed);
                    if (recoverable.Applied)
                        await CommitAsync(committed, recoverable with { Applied = false, AppliedUtc = null }, cancellationToken);
                }
            }
            var pending = new List<BanIntent>();
            foreach (var path in BoundedPaths())
            {
                var record = await ReadRecordAsync(path, cancellationToken);
                if (!record.Applied) pending.Add(record.Intent);
            }
            return pending;
        }
        finally { gate.Release(); }
    }

    public async ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Validate(intent);
            var path = PathFor(intent.IncidentId);
            var digest = Digest(intent);
            if (File.Exists(path))
            {
                var existing = await ReadRecordAsync(path, cancellationToken);
                if (existing.Digest != digest) throw new InvalidDataException("Incident id collision.");
                return;
            }
            var paths = BoundedPaths();
            if (paths.Length >= options.MaxRecords)
            {
                // Only completed evidence beyond retention may leave this bounded store.
                foreach (var candidate in paths)
                {
                    var record = await ReadRecordAsync(candidate, cancellationToken);
                    if (record.Applied && record.AppliedUtc is { } applied && clock.GetUtcNow() - applied >= options.Retention)
                        File.Delete(candidate);
                }
                if (BoundedPaths().Length >= options.MaxRecords) throw new IOException("Enforcement journal capacity exhausted.");
            }
            await CommitAsync(path, new(1, false, null, digest, intent), cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(incidentId);
            var record = await ReadRecordAsync(path, cancellationToken);
            if (!record.Applied)
                await CommitAsync(path, record with { Applied = true, AppliedUtc = clock.GetUtcNow() }, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private string[] BoundedPaths()
    {
        var paths = Directory.EnumerateFiles(directory, "*.json").Take(options.MaxRecords + 1).ToArray();
        if (paths.Length > options.MaxRecords) throw new IOException("Enforcement journal exceeds configured capacity.");
        return paths;
    }

    private async ValueTask<Record> ReadRecordAsync(string path, CancellationToken cancellationToken, bool temporary = false)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > options.MaxRecordBytes) throw new InvalidDataException("Oversized enforcement journal record.");
        var record = await JsonSerializer.DeserializeAsync<Record>(stream, Json, cancellationToken)
            ?? throw new InvalidDataException("Missing enforcement journal record.");
        Validate(record.Intent);
        if (record.Schema != 1 || record.Digest != Digest(record.Intent) || Path.GetFileName(path) != record.Intent.IncidentId.ToString("N") + (temporary ? ".tmp" : ".json")
            || record.Applied != record.AppliedUtc.HasValue)
            throw new InvalidDataException("Invalid enforcement journal record.");
        return record;
    }

    private async ValueTask CommitAsync(string path, Record record, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > options.MaxRecordBytes) throw new InvalidDataException("Oversized enforcement intent.");
        var temp = Path.ChangeExtension(path, ".tmp");
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        // Keep a failed/partial temp for verified recovery. The startup guard independently preserves
        // the possibility of a pre-append memory-only incident, including a total storage failure.
    }

    private string PathFor(Guid incidentId) => Path.Combine(directory, incidentId.ToString("N") + ".json");
    private static string Digest(BanIntent intent) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent, Json)));
    private static void Validate(BanIntent intent)
    {
        if (intent is null || intent.IncidentId == Guid.Empty || intent.AccountId <= 0 || intent.Evidence is null
            || intent.AccountId != intent.Evidence.AccountId || intent.Evidence.EventId != intent.IncidentId
            || intent.Evidence.Preconditions is null || !intent.Evidence.Preconditions.Complete)
            throw new InvalidDataException("Invalid enforcement intent.");
    }

    public async ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await safety.BeginAsync(request, cancellationToken); }
        finally { gate.Release(); }
    }
    public async ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await safety.CompleteAsync(serverRunId, cancellationToken); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Explicit controlled recovery only, after independently verifying any lost in-memory sanction window.
    /// This method does not clear pending evidence, remove bans, or infer that an unknown account cheated.
    /// It is intentionally not called by automatic startup or exposed as a player command.
    /// </summary>
    public async ValueTask ConfirmRecoveryAsync(string verificationReference, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await safety.ConfirmRecoveryAsync(verificationReference, cancellationToken); }
        finally { gate.Release(); }
    }
    public void Dispose() { ownership.Dispose(); gate.Dispose(); }
}
