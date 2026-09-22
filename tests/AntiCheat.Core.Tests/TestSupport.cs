using System.Collections.Concurrent;
using AntiCheat.Core;

namespace AntiCheat.Core.Tests;

internal sealed class ControlledClock : TimeProvider
{
    private long ticks;
    private DateTimeOffset utc = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => utc;
    public void Advance(TimeSpan amount) { Interlocked.Add(ref ticks, amount.Ticks); utc += amount; }
    public void JumpWallClock(TimeSpan amount) => utc += amount;
}

internal sealed class MemoryJournal : IEnforcementJournal, IRunSafetyGuard
{
    public ConcurrentDictionary<Guid, BanIntent> Pending { get; } = new();
    public ConcurrentDictionary<Guid, BanIntent> All { get; } = new();
    public bool FailRead { get; set; }
    public bool FailAppend { get; set; }
    public bool FailMark { get; set; }
    public int Appends;
    public Func<ValueTask<RunSafetyStatus>>? OnBeginRun { get; set; }
    // Deliberately a nondurable Core test double. File guard/restart checks are a separate test layer.
    public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) =>
        OnBeginRun?.Invoke() ?? ValueTask.FromResult(RunSafetyStatus.Ready);
    public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (FailRead) throw new IOException("Injected journal read failure.");
        return ValueTask.FromResult<IReadOnlyList<BanIntent>>(Pending.Values.ToArray());
    }
    public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Appends);
        if (FailAppend) throw new IOException("Injected journal append failure.");
        All.TryAdd(intent.IncidentId, intent);
        Pending.TryAdd(intent.IncidentId, intent);
        return ValueTask.CompletedTask;
    }
    public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default)
    {
        if (FailMark) throw new IOException("Injected journal acknowledgement failure.");
        Pending.TryRemove(incidentId, out _);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MemoryBans : IAccountBanStore
{
    public ConcurrentDictionary<long, BanIntent> Accounts { get; } = new();
    public bool Fail { get; set; }
    public int Calls;
    public Func<BanIntent, ValueTask>? BeforeBan { get; set; }
    public async ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        if (BeforeBan is not null) await BeforeBan(intent);
        if (Fail) throw new IOException("Injected account database failure.");
        Accounts.TryAdd(intent.AccountId, intent);
    }
}

internal sealed class Lab
{
    public static readonly RulePolicy Policy = new("test-lab-only/fixture-v1", RuleQualification.TestLab,
        "tests/A02-fixture-not-live-qualified", [5], "fixture-1");
    public static readonly ProofContext Complete = new("test-lab-only/fixture-v1", "fixture-1", true, true, true, true);
    public ControlledClock Clock { get; } = new();
    public MemoryJournal Journal { get; } = new();
    public MemoryBans Bans { get; } = new();
    public AntiCheatEngine Engine { get; }
    public Lab(EngineOptions? options = null, RulePolicy? policy = null)
    {
        Engine = new(Clock, options ?? new() { Scope = ExecutionScope.TestLab }, policy ?? Policy, Journal, Bans);
    }
    public async Task<SessionKey> Login(int slot = 4, long account = 100)
    {
        if (Engine.IsMaintenanceMode) await Engine.RecoverAsync();
        var key = Engine.OpenSession(slot)!.Value;
        if (Engine.Authenticate(key, account) != AuthenticationResult.Authenticated)
            throw new InvalidOperationException("Test login was rejected.");
        return key;
    }
    public static SelfSlotObservation Violation(SessionKey key) => new(key, 5, key.Slot + 1, Complete);
}
