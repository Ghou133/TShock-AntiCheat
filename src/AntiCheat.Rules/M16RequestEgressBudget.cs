using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M16RequestEgressOptions
{
    public int SessionCapacity { get; init; } = 256;
    public int AccountCapacity { get; init; } = 4096;
    public double ActorBurstBytes { get; init; } = 1_048_576;
    public double ActorBytesPerSecond { get; init; } = 262_144;
    public double GlobalBurstBytes { get; init; } = 8_388_608;
    public double GlobalBytesPerSecond { get; init; } = 2_097_152;
    public TimeSpan Retention { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed record M16RequestEgressDecision(bool Allowed, string Reason);

/// <summary>Serialized chat bytes per selected recipient, never inbound character count, CPU cost,
/// or account punishment. No message text, credentials, IP or mutable packet is retained.</summary>
public sealed class M16RequestEgressBudget
{
    private readonly object gate = new();
    private readonly BoundedRateLimiter sessions, accounts, global;
    private long admittedBytes, blockedBytes, admittedSends, blockedSends;
    public const string ContractId = "I03.ChatSerializedEgressBudget/1.0.0";
    public M16RequestEgressBudget(TimeProvider clock, M16RequestEgressOptions? options = null)
    {
        options ??= new();
        sessions = new(clock, new(options.SessionCapacity, options.ActorBurstBytes, options.ActorBytesPerSecond, options.Retention));
        accounts = new(clock, new(options.AccountCapacity, options.ActorBurstBytes, options.ActorBytesPerSecond, options.Retention));
        global = new(clock, new(1, options.GlobalBurstBytes, options.GlobalBytesPerSecond, options.Retention));
    }
    public long AdmittedBytes { get { lock (gate) return admittedBytes; } }
    public long BlockedBytes { get { lock (gate) return blockedBytes; } }
    public long AdmittedSends { get { lock (gate) return admittedSends; } }
    public long BlockedSends { get { lock (gate) return blockedSends; } }
    public int AccountBucketCount => accounts.Count;
    public M16RequestEgressDecision Consume(SessionKey session, long account, int bytes)
    {
        if (session.ServerRunId == Guid.Empty || session.WorldEpoch <= 0 || session.Generation <= 0 ||
            session.Slot is < 0 or >= 256 || account <= 0 || bytes is < 5 or > 65535)
            return new(false, "invalid-chat-egress-context");
        lock (gate)
        {
            var connection = sessions.TryConsume(Key(session), bytes);
            // Rejected sender output cannot consume the shared global bucket and starve others.
            // Account budget survives reconnect; releasing a session never resets it.
            bool allowed = connection.Behavior == ControlAction.Pass;
            string reason = "chat-egress-session-budget";
            if (allowed) { allowed = accounts.TryConsume(account.ToString(CultureInfo.InvariantCulture), bytes).Behavior == ControlAction.Pass; reason = "chat-egress-account-budget"; }
            if (allowed) { allowed = global.TryConsume("chat", bytes).Behavior == ControlAction.Pass; reason = "chat-egress-global-budget"; }
            if (allowed) { admittedBytes = Add(admittedBytes, bytes); admittedSends = Add(admittedSends, 1); }
            else { blockedBytes = Add(blockedBytes, bytes); blockedSends = Add(blockedSends, 1); }
            return new(allowed, allowed ? "chat-egress-within-budget" : reason);
        }
    }
    public void Forget(SessionKey session) => sessions.Forget(Key(session));
    private static string Key(SessionKey session) => string.Create(CultureInfo.InvariantCulture,
        $"{session.ServerRunId:N}/{session.WorldEpoch}/{session.Slot}/{session.Generation}");
    private static long Add(long value, int increment) => value > long.MaxValue - increment ? long.MaxValue : value + increment;
}
