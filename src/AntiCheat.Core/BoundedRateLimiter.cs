namespace AntiCheat.Core;

public sealed record RateLimitOptions(int MaxKeys, double Burst, double TokensPerSecond, TimeSpan IdleTtl);
public sealed record RateLimitDecision(ControlAction Behavior, Verdict Verdict, string Reason, double RemainingTokens);

/// <summary>Per-key cost budgets. Key admission and expired-entry work are bounded; rate decisions never ban accounts.</summary>
public sealed class BoundedRateLimiter
{
    private sealed class Bucket(string key, double tokens, long now)
    {
        public string Key { get; } = key;
        public double Tokens { get; set; } = tokens;
        public long UpdatedAt { get; set; } = now;
        public long LastSeen { get; set; } = now;
    }
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly RateLimitOptions options;
    private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
    private readonly Bucket?[] buckets;
    private readonly Queue<int> free;
    private int cursor;

    public BoundedRateLimiter(TimeProvider clock, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxKeys is < 1 or > 1_000_000 || !double.IsFinite(options.Burst) || options.Burst <= 0 ||
            !double.IsFinite(options.TokensPerSecond) || options.TokensPerSecond <= 0 || options.IdleTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.clock = clock;
        this.options = options;
        buckets = new Bucket[options.MaxKeys];
        free = new Queue<int>(Enumerable.Range(0, options.MaxKeys));
    }
    public int Count { get { lock (gate) return indices.Count; } }

    /// <summary>Release a completed session's exact generation key. Do not use this to reset source/global budgets.</summary>
    public bool Forget(string key)
    {
        lock (gate)
        {
            if (!indices.Remove(key, out var index)) return false;
            buckets[index] = null;
            free.Enqueue(index);
            return true;
        }
    }

    /// <param name="key">A bounded server-owned connection/account/socket-source key, never a player command.</param>
    public RateLimitDecision TryConsume(string key, double cost = 1)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 || !double.IsFinite(cost) || cost <= 0)
            return new(ControlAction.Block, Verdict.UnsafeInput, "invalid-budget-input", 0);
        lock (gate)
        {
            var now = clock.GetTimestamp();
            Purge(now);
            Bucket bucket;
            if (indices.TryGetValue(key, out var index))
            {
                bucket = buckets[index]!;
                var elapsed = clock.GetElapsedTime(bucket.UpdatedAt, now);
                if (elapsed < TimeSpan.Zero)
                    return new(ControlAction.Block, Verdict.ResourceAbuse, "monotonic-clock-invalid", bucket.Tokens);
                bucket.Tokens = Math.Min(options.Burst, bucket.Tokens + elapsed.TotalSeconds * options.TokensPerSecond);
                bucket.UpdatedAt = now;
                bucket.LastSeen = now;
            }
            else
            {
                if (free.Count == 0)
                    return new(ControlAction.Block, Verdict.ResourceAbuse, "budget-key-capacity", 0);
                index = free.Dequeue();
                bucket = new Bucket(key, options.Burst, now);
                buckets[index] = bucket;
                indices.Add(key, index);
            }
            if (cost > bucket.Tokens)
                return new(ControlAction.Block, Verdict.ResourceAbuse, "cost-budget-exhausted", bucket.Tokens);
            bucket.Tokens -= cost;
            return new(ControlAction.Pass, Verdict.Pass, "within-cost-budget", bucket.Tokens);
        }
    }

    private void Purge(long now)
    {
        // At most eight buckets per attempt, including capacity rejection. A temporarily uncollected
        // expired slot can reject admission; a new address cannot force an all-key scan on every packet.
        for (var checkedCount = 0; checkedCount < Math.Min(8, buckets.Length); checkedCount++)
        {
            var index = cursor;
            cursor = (cursor + 1) % buckets.Length;
            var bucket = buckets[index];
            if (bucket is null) continue;
            var elapsed = clock.GetElapsedTime(bucket.LastSeen, now);
            if (elapsed < options.IdleTtl) continue;
            indices.Remove(bucket.Key);
            buckets[index] = null;
            free.Enqueue(index);
        }
    }
}
