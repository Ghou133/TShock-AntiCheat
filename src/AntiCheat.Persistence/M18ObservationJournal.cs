using System.Text.Json;

namespace AntiCheat.Persistence;

/// <summary>Bounded local observation records for the M18 candidate routes.
/// This is deliberately separate from FileEnforcementJournal: it is not a
/// sanction store, and a write failure never changes packet admission.</summary>
public sealed record M18ObservationJournalOptions(
    int MaxStrikeBuckets = 128,
    int MaxImportantItemBuckets = 128,
    int MaxSamplesPerBucket = 8,
    int MaxFileBytes = 1_048_576)
{
    public void Validate()
    {
        if (MaxStrikeBuckets is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaxStrikeBuckets));
        if (MaxImportantItemBuckets is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaxImportantItemBuckets));
        if (MaxSamplesPerBucket is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaxSamplesPerBucket));
        if (MaxFileBytes is < 16_384 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
    }
}

public sealed record M18StrikeObservationRecord(
    string SessionId,
    long AccountId,
    long WorldEpoch,
    int WorldId,
    string Stage,
    int TargetSlot,
    int TargetGeneration,
    int TargetType,
    int WireDamage,
    int ReceiverDamage,
    int TargetLife,
    int TargetLifeMax,
    bool StageSnapshotComplete,
    bool TargetActive,
    bool TargetGenerationMatchesCurrent,
    bool AttributionComplete,
    bool LegalExceptionsExcluded,
    bool Counted,
    bool LowDamage,
    bool ExtremeDamage,
    string SourceContext,
    string Action,
    string Verdict,
    string Reason,
    DateTimeOffset ObservedUtc)
{
    public bool ButcherPatternDetected { get; init; }
    public bool ButcherPositionContextComplete { get; init; }
    public bool ButcherPreControlAtTarget { get; init; }
    public bool ButcherControlJumpDetected { get; init; }
    public bool ButcherReturnPositionStable { get; init; }
    public bool ButcherRepeatedAttackSignature { get; init; }
    public bool ButcherCrossTargetContinuation { get; init; }
    public int ButcherCompletedTargetSequences { get; init; }
    public int ButcherCurrentTargetStrikes { get; init; }
    public bool SummonContextComplete { get; init; }
    public bool SummonMaintenanceBuffObserved { get; init; }
    public bool MatchingSummonEntityObserved { get; init; }
    public int MatchingSummonEntityCount { get; init; }
    public bool PostNativeOneShotDeathEvidence { get; init; }
    public bool PostNativeSanctionCandidate { get; init; }
    public string InterceptionBoundary { get; init; } = "packet28-before-native-receiver";
}

public sealed record M18StrikeObservationBucket(
    string Key,
    string SessionId,
    long AccountId,
    long WorldEpoch,
    int WorldId,
    string Stage,
    int EventCount,
    int MaxWireDamage,
    int MaxReceiverDamage,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    int SamplesDropped,
    List<M18StrikeObservationRecord> Samples)
{
    // The bounded sample ring is intentionally not the peak witness. These
    // fields retain the complete context of the max event even after the
    // ordinary sample allowance has been consumed.
    public M18StrikeObservationRecord? PeakWireSample { get; init; }
    public M18StrikeObservationRecord? PeakReceiverSample { get; init; }
}

public sealed record M18ImportantItemObservationRecord(
    string SessionId,
    long AccountId,
    long WorldEpoch,
    int NpcSlot,
    int NpcGeneration,
    int NpcType,
    int ItemIndex,
    int ItemId,
    string ItemName,
    int Stack,
    int PreviousStack,
    int Delta,
    int MaxStack,
    string Source,
    bool SourceContextKnown,
    bool SourceAttributionComplete,
    bool Recorded,
    bool GrowthObserved,
    bool PossibleSorting,
    string Action,
    string Verdict,
    string Reason,
    string Boundary,
    DateTimeOffset ObservedUtc)
{
    // Totals are distinct from the physical slot value. Older records used
    // PreviousStack for both, so these optional fields preserve the repaired
    // mapping without invalidating old journal files.
    public int PreviousTotal { get; init; }
    public int CurrentTotal { get; init; }
    public int PreviousSlotStack { get; init; }
    public int CurrentSlotStack { get; init; }
}

public sealed record M18ImportantItemObservationBucket(
    string Key,
    string SessionId,
    long AccountId,
    long WorldEpoch,
    int ItemId,
    string ItemName,
    int EventCount,
    int MaxStack,
    int TotalDelta,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    int SamplesDropped,
    List<M18ImportantItemObservationRecord> Samples);

public sealed record M18ObservationJournalSnapshot(
    int SchemaVersion,
    DateTimeOffset SavedUtc,
    List<M18StrikeObservationBucket> StrikeBuckets,
    List<M18ImportantItemObservationBucket> ImportantItemBuckets)
{
    public int StrikeBucketsEvicted { get; init; }
    public int ImportantItemBucketsEvicted { get; init; }
}

public sealed class M18ObservationJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object sync = new();
    private readonly string filePath;
    private readonly M18ObservationJournalOptions options;
    private readonly Dictionary<string, M18StrikeObservationBucket> strikes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, M18ImportantItemObservationBucket> importantItems = new(StringComparer.Ordinal);
    private bool dirty;
    private bool disposed;
    private DateTimeOffset lastFlush = DateTimeOffset.MinValue;
    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    private int recordsSinceFlush;
    private int flushAttempts;
    private int flushFailures;
    private int strikeBucketsEvicted;
    private int importantItemBucketsEvicted;

    public M18ObservationJournal(string filePath, M18ObservationJournalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
        this.options = options ?? new();
        this.options.Validate();
        Load();
    }

    public string FilePath => filePath;
    public bool Healthy { get; private set; } = true;
    public string? LastErrorType { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public int StrikeBucketCount { get { lock (sync) return strikes.Count; } }
    public int ImportantItemBucketCount { get { lock (sync) return importantItems.Count; } }
    public int StrikeBucketsEvicted { get { lock (sync) return strikeBucketsEvicted; } }
    public int ImportantItemBucketsEvicted { get { lock (sync) return importantItemBucketsEvicted; } }
    public int FlushAttemptCount { get { lock (sync) return flushAttempts; } }
    public int FlushFailureCount { get { lock (sync) return flushFailures; } }

    public void RecordStrike(M18StrikeObservationRecord record)
    {
        if (record is null) return;
        lock (sync)
        {
            if (disposed || !Valid(record.SessionId, 128) || !Valid(record.Stage, 64) ||
                !Valid(record.Action, 64) || !Valid(record.Verdict, 64) || !Valid(record.Reason, 256))
            {
                Fault(new InvalidDataException("M18 strike observation exceeded the journal bounds."));
                return;
            }

            string key = record.SessionId + "|" + record.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + record.Stage;
            if (!strikes.TryGetValue(key, out var bucket))
            {
                EvictOldestStrikeIfNeeded();
                bucket = new(key, record.SessionId, record.AccountId, record.WorldEpoch, record.WorldId,
                    record.Stage, 0, 0, 0, record.ObservedUtc, record.ObservedUtc, 0, []);
            }

            int dropped = bucket.SamplesDropped;
            var samples = bucket.Samples ?? [];
            if (samples.Count < options.MaxSamplesPerBucket) samples.Add(record);
            else dropped = SaturatingIncrement(dropped);
            var peakWire = bucket.PeakWireSample;
            if (record.WireDamage > bucket.MaxWireDamage ||
                peakWire is null && record.WireDamage == bucket.MaxWireDamage)
                peakWire = record;
            var peakReceiver = bucket.PeakReceiverSample;
            if (record.ReceiverDamage > bucket.MaxReceiverDamage ||
                peakReceiver is null && record.ReceiverDamage == bucket.MaxReceiverDamage)
                peakReceiver = record;
            strikes[key] = bucket with
            {
                EventCount = SaturatingIncrement(bucket.EventCount),
                MaxWireDamage = Math.Max(bucket.MaxWireDamage, record.WireDamage),
                MaxReceiverDamage = Math.Max(bucket.MaxReceiverDamage, record.ReceiverDamage),
                LastObservedUtc = record.ObservedUtc,
                SamplesDropped = dropped,
                Samples = samples,
                PeakWireSample = peakWire,
                PeakReceiverSample = peakReceiver,
            };
            MarkDirty();
        }
    }

    public void RecordImportantItem(M18ImportantItemObservationRecord record)
    {
        if (record is null) return;
        lock (sync)
        {
            if (disposed || !Valid(record.SessionId, 128) || !Valid(record.ItemName, 128) ||
                !Valid(record.Source, 32) || !Valid(record.Boundary, 256))
            {
                Fault(new InvalidDataException("M18 important-item observation exceeded the journal bounds."));
                return;
            }

            string key = record.SessionId + "|" + record.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "|" + record.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!importantItems.TryGetValue(key, out var bucket))
            {
                EvictOldestImportantItemIfNeeded();
                bucket = new(key, record.SessionId, record.AccountId, record.WorldEpoch, record.ItemId,
                    record.ItemName, 0, 0, 0, record.ObservedUtc, record.ObservedUtc, 0, []);
            }

            int dropped = bucket.SamplesDropped;
            var samples = bucket.Samples ?? [];
            if (samples.Count < options.MaxSamplesPerBucket) samples.Add(record);
            else dropped = SaturatingIncrement(dropped);
            importantItems[key] = bucket with
            {
                EventCount = SaturatingIncrement(bucket.EventCount),
                MaxStack = Math.Max(bucket.MaxStack, record.Stack),
                TotalDelta = SaturatingAdd(bucket.TotalDelta, record.Delta),
                LastObservedUtc = record.ObservedUtc,
                SamplesDropped = dropped,
                Samples = samples,
            };
            MarkDirty();
        }
    }

    public M18ObservationJournalSnapshot Snapshot()
    {
        lock (sync)
        {
            return new(1, DateTimeOffset.UtcNow,
                strikes.Values.Select(Clone).ToList(), importantItems.Values.Select(Clone).ToList())
            {
                StrikeBucketsEvicted = strikeBucketsEvicted,
                ImportantItemBucketsEvicted = importantItemBucketsEvicted,
            };
        }
    }

    public bool Flush()
    {
        lock (sync)
        {
            if (disposed) return Healthy;
            if (!dirty) return Healthy;
            flushAttempts = SaturatingIncrement(flushAttempts);
            try
            {
                string? parent = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(parent)) throw new IOException("M18 journal parent path is unavailable.");
                Directory.CreateDirectory(parent);
                byte[] bytes = SerializeWithinFileBound();
                string temp = filePath + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, filePath, overwrite: true);
                dirty = false;
                recordsSinceFlush = 0;
                lastFlush = DateTimeOffset.UtcNow;
                retryAfter = DateTimeOffset.MinValue;
                flushFailures = 0;
                Healthy = true;
                LastErrorType = null;
                LastErrorMessage = null;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Fault(error);
                flushFailures = SaturatingIncrement(flushFailures);
                retryAfter = DateTimeOffset.UtcNow + RetryDelay(flushFailures);
                return false;
            }
        }
    }

    public void FlushIfDue(TimeSpan? interval = null)
    {
        var minimumInterval = interval ?? TimeSpan.FromSeconds(1);
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Flush interval cannot be negative.");

        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            // The record-count threshold is a trigger for eligibility, not a
            // way to bypass the minimum interval. This keeps a busy producer
            // from turning every update into a synchronous file write.
            if (!dirty || now < retryAfter ||
                lastFlush != DateTimeOffset.MinValue && now - lastFlush < minimumInterval) return;
        }
        Flush();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            Flush();
            disposed = true;
        }
    }

    private void Load()
    {
        lock (sync)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length > options.MaxFileBytes) throw new InvalidDataException("M18 observation journal is oversized.");
                using var stream = File.OpenRead(filePath);
                var snapshot = JsonSerializer.Deserialize<M18ObservationJournalSnapshot>(stream, Json)
                    ?? throw new InvalidDataException("M18 observation journal is empty.");
                if (snapshot.SchemaVersion != 1 || snapshot.StrikeBuckets is null || snapshot.ImportantItemBuckets is null ||
                    snapshot.StrikeBuckets.Count > options.MaxStrikeBuckets ||
                    snapshot.ImportantItemBuckets.Count > options.MaxImportantItemBuckets ||
                    snapshot.StrikeBucketsEvicted < 0 || snapshot.ImportantItemBucketsEvicted < 0)
                    throw new InvalidDataException("M18 observation journal schema or capacity is invalid.");
                foreach (var bucket in snapshot.StrikeBuckets)
                {
                    if (!Valid(bucket.Key, 512) || bucket.Samples is null || bucket.Samples.Count > options.MaxSamplesPerBucket)
                        throw new InvalidDataException("M18 strike observation bucket is invalid.");
                    strikes[bucket.Key] = Clone(bucket);
                }
                foreach (var bucket in snapshot.ImportantItemBuckets)
                {
                    if (!Valid(bucket.Key, 512) || bucket.Samples is null || bucket.Samples.Count > options.MaxSamplesPerBucket)
                        throw new InvalidDataException("M18 important-item observation bucket is invalid.");
                    importantItems[bucket.Key] = Clone(bucket);
                }
                strikeBucketsEvicted = snapshot.StrikeBucketsEvicted;
                importantItemBucketsEvicted = snapshot.ImportantItemBucketsEvicted;
                lastFlush = DateTimeOffset.UtcNow;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            {
                Fault(error);
                strikes.Clear();
                importantItems.Clear();
            }
        }
    }

    private void MarkDirty()
    {
        dirty = true;
        recordsSinceFlush = SaturatingIncrement(recordsSinceFlush);
    }

    private void Fault(Exception error)
    {
        Healthy = false;
        LastErrorType = error.GetType().Name;
        LastErrorMessage = error.Message.Length <= 256 ? error.Message : error.Message[..256];
    }

    private static bool Valid(string? value, int max)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    private static int SaturatingIncrement(int value) => value == int.MaxValue ? value : value + 1;
    private static int SaturatingAdd(int left, int right)
        => right > 0 && left > int.MaxValue - right ? int.MaxValue :
            right < 0 && left < int.MinValue - right ? int.MinValue : left + right;

    private static M18StrikeObservationBucket Clone(M18StrikeObservationBucket bucket)
        => bucket with { Samples = bucket.Samples is null ? [] : [.. bucket.Samples] };

    private static M18ImportantItemObservationBucket Clone(M18ImportantItemObservationBucket bucket)
        => bucket with { Samples = bucket.Samples is null ? [] : [.. bucket.Samples] };

    private void EvictOldestStrikeIfNeeded()
    {
        if (strikes.Count < options.MaxStrikeBuckets) return;
        string? key = strikes.Values
            .OrderBy(x => x.LastObservedUtc)
            .ThenBy(x => x.FirstObservedUtc)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (key is not null && strikes.Remove(key))
            strikeBucketsEvicted = SaturatingIncrement(strikeBucketsEvicted);
    }

    private void EvictOldestImportantItemIfNeeded()
    {
        if (importantItems.Count < options.MaxImportantItemBuckets) return;
        string? key = importantItems.Values
            .OrderBy(x => x.LastObservedUtc)
            .ThenBy(x => x.FirstObservedUtc)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (key is not null && importantItems.Remove(key))
            importantItemBucketsEvicted = SaturatingIncrement(importantItemBucketsEvicted);
    }

    private bool EvictOldestForFileBound()
    {
        var strike = strikes.Values
            .Select(x => (Kind: 0, x.LastObservedUtc, x.FirstObservedUtc, x.Key))
            .Concat(importantItems.Values.Select(x => (Kind: 1, x.LastObservedUtc, x.FirstObservedUtc, x.Key)))
            .OrderBy(x => x.LastObservedUtc)
            .ThenBy(x => x.FirstObservedUtc)
            .ThenBy(x => x.Kind)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (strike.Key is null) return false;
        if (strike.Kind == 0 && strikes.Remove(strike.Key))
        {
            strikeBucketsEvicted = SaturatingIncrement(strikeBucketsEvicted);
            return true;
        }
        if (strike.Kind == 1 && importantItems.Remove(strike.Key))
        {
            importantItemBucketsEvicted = SaturatingIncrement(importantItemBucketsEvicted);
            return true;
        }
        return false;
    }

    private byte[] SerializeWithinFileBound()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), Json);
        while (bytes.Length > options.MaxFileBytes)
        {
            if (!EvictOldestForFileBound())
                throw new IOException("M18 observation journal exceeded its file bound after bounded retention.");
            bytes = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), Json);
        }
        return bytes;
    }

    private static TimeSpan RetryDelay(int failureCount)
    {
        int exponent = Math.Min(Math.Max(failureCount - 1, 0), 5);
        return TimeSpan.FromMilliseconds(250 * (1 << exponent));
    }
}
