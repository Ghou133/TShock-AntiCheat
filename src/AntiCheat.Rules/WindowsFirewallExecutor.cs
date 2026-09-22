using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record RestrictedFirewallOptions(string ApplicationPath, int LocalTcpPort,
    string[] ExclusiveSourceAddresses, string[] ProtectedAddresses,
    int MaximumEvents = 1024, int MaximumTargets = 256);
public sealed record RestrictedFirewallRule(string Name, string Address, string ApplicationPath, int LocalTcpPort, DateTimeOffset ExpiresUtc);
public sealed record FirewallStoredEvent(Guid EventId, string ContentHash, DateTimeOffset RetainUntilUtc);
public sealed record FirewallStoredTarget(string Address, DateTimeOffset ExpiresUtc);
public sealed record FirewallPersistentState(int Version, string Scope, DateTimeOffset LastUtc,
    FirewallStoredEvent[] Events, FirewallStoredTarget[] Targets);

public interface IFirewallStateStore : IDisposable
{
    FirewallPersistentState? Load();
    void Save(FirewallPersistentState state);
}

/// <summary>Host boundary: only this executor's named, application-and-port-scoped rules are exposed.</summary>
public interface IRestrictedFirewallBackend
{
    IReadOnlyList<RestrictedFirewallRule> ReadOwnedRules();
    void EnsureRule(RestrictedFirewallRule rule);
    void RemoveRule(string ruleName);
}

/// <summary>Explicit, persistent OS-operation executor for an external helper. Simulation stays in the original dry-run executor.</summary>
public sealed class WindowsFirewallExecutor : IFirewallIpcExecutor, IDisposable
{
    public const string RuleGroup = "TShock AntiCheat Temporary Blocks v1";
    public const string RulePrefix = "TShockAntiCheat-v1-";
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly RestrictedFirewallOptions options;
    private readonly IFirewallStateStore store;
    private readonly IRestrictedFirewallBackend backend;
    private readonly InMemoryFirewallExecutor dryRun;
    private readonly BoundedRateLimiter executionBudget;
    private readonly HashSet<string> exclusive, protectedAddresses;
    private FirewallPersistentState state;
    private bool disposed;
    public string Scope { get; }
    public bool IsHealthy { get; private set; }
    public string? LastFault { get; private set; }

    public WindowsFirewallExecutor(TimeProvider clock, RestrictedFirewallOptions options,
        IFirewallStateStore store, IRestrictedFirewallBackend backend)
    {
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(backend);
        ValidateOptions(options);
        this.clock = clock; this.options = options with { ApplicationPath = Path.GetFullPath(options.ApplicationPath),
            ExclusiveSourceAddresses = options.ExclusiveSourceAddresses.ToArray(), ProtectedAddresses = options.ProtectedAddresses.ToArray() };
        this.store = store; this.backend = backend;
        exclusive = this.options.ExclusiveSourceAddresses.ToHashSet(StringComparer.Ordinal);
        protectedAddresses = this.options.ProtectedAddresses.ToHashSet(StringComparer.Ordinal);
        Scope = ScopeFor(this.options.ApplicationPath, options.LocalTcpPort);
        state = new(1, Scope, clock.GetUtcNow(), [], []);
        dryRun = new(clock, options.MaximumEvents, options.MaximumTargets);
        executionBudget = new(clock, new(1, 8, 2, TimeSpan.FromMinutes(1)));
        // Constructor does not touch the OS; the dedicated helper must explicitly call RecoverAndReconcile.
    }

    public static void ValidateOptions(RestrictedFirewallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApplicationPath) || !Path.IsPathFullyQualified(options.ApplicationPath) ||
            options.ApplicationPath.Length > 2048 || Path.GetExtension(options.ApplicationPath) != ".exe" ||
            options.LocalTcpPort is < 1024 or > 65535 or 3389 or 5985 or 5986 ||
            options.MaximumEvents is < 1 or > 4096 || options.MaximumTargets is < 1 or > 1024 ||
            options.ExclusiveSourceAddresses is null || options.ProtectedAddresses is null ||
            options.ExclusiveSourceAddresses.Length > 1024 || options.ProtectedAddresses.Length > 1024)
            throw new ArgumentException("Expected a bounded explicit game executable/port and source policy.");
        foreach (string value in options.ExclusiveSourceAddresses.Concat(options.ProtectedAddresses))
            if (!NetworkControls.TryNormalizeAddress(value, out var parsed) || parsed!.ToString() != value)
                throw new ArgumentException("Source policy must contain canonical individual IP addresses.");
        foreach (string value in options.ExclusiveSourceAddresses)
            if (!IsEligiblePublicAddress(value)) throw new ArgumentException("Exclusive source policy contains a protected address class.");
    }

    public static string ScopeFor(string applicationPath, int port) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(applicationPath).ToUpperInvariant() + "\n" + port.ToString(CultureInfo.InvariantCulture))))[..16];
    public static string RuleName(string scope, string address) => RulePrefix + scope + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address)));

    public static bool IsEligiblePublicAddress(string address)
    {
        if (!NetworkControls.TryNormalizeAddress(address, out var ip) || ip!.ToString() != address || IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            return false;
        byte[] bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.ScopeId == 0 && (bytes[0] & 0xe0) == 0x20; // Conservative global-unicast 2000::/3 only.
        return bytes[0] is > 0 and < 224 && bytes[0] != 10 && bytes[0] != 127 &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 169 && bytes[1] == 254);
    }

    public FirewallIpcResponse Apply(FirewallIpcCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Even with a live OS backend attached, these operations cannot reach any OS or persistent-target action.
        if (command.Operation != FirewallIpcOperation.ExecuteTemporaryBlock) return dryRun.Apply(command);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var validation = FirewallIpcProtocol.Validate(command, clock, allowExecution: true);
            if (validation is not null) return Rejected(command, validation);
            string address = command.SourceEvent.ProposedTarget!;
            if (!IsEligiblePublicAddress(address) || protectedAddresses.Contains(address) || !exclusive.Contains(address))
                return Rejected(command, "source-not-in-host-exclusive-policy-or-protected");
            if (executionBudget.TryConsume("host-execution").Behavior != ControlAction.Pass)
                return Rejected(command, "host-execution-budget");
            if (!IsHealthy) return Rejected(command, "firewall-maintenance");
            var now = clock.GetUtcNow();
            if (now < state.LastUtc)
            { Fault("clock-moved-backwards"); return Rejected(command, "firewall-maintenance"); }
            string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { command.Operation, command.SourceEvent }, FirewallIpcProtocol.JsonOptions)));
            var previous = state.Events.FirstOrDefault(x => x.EventId == command.SourceEvent.EventId);
            if (previous is not null)
                return previous.ContentHash == hash
                    ? new(command.RequestId, true, "duplicate-event", false, state.Events.Length, ExecutedTargets: state.Targets.Count(x => x.ExpiresUtc > now))
                    : Rejected(command, "event-id-content-conflict");
            var events = state.Events.Where(x => x.RetainUntilUtc > now).ToList();
            var targets = state.Targets.Where(x => x.ExpiresUtc > now && Allowed(x.Address)).ToDictionary(x => x.Address, StringComparer.Ordinal);
            if (events.Count >= options.MaximumEvents) return Rejected(command, "event-capacity");
            if (!targets.ContainsKey(address) && targets.Count >= options.MaximumTargets) return Rejected(command, "target-capacity");
            var expiry = command.SourceEvent.ObservedUtc.AddSeconds(command.SourceEvent.SuggestedTtlSeconds);
            expiry = expiry < now.AddSeconds(command.SourceEvent.SuggestedTtlSeconds) ? expiry : now.AddSeconds(command.SourceEvent.SuggestedTtlSeconds);
            if (expiry <= now) return Rejected(command, "source-block-ttl-already-expired");
            if (targets.TryGetValue(address, out var existing) && existing.ExpiresUtc > expiry) expiry = existing.ExpiresUtc;
            targets[address] = new(address, expiry);
            events.Add(new(command.SourceEvent.EventId, hash, now.AddMinutes(20)));
            var next = new FirewallPersistentState(1, Scope, now, events.ToArray(), targets.Values.OrderBy(x => x.Address).ToArray());
            try
            {
                // Durable intent first. If OS configuration fails, recovery retries exactly this desired state.
                store.Save(next); state = next;
                ReconcileBackend(now);
                return new(command.RequestId, true, "temporary-block-configured", false, state.Events.Length, ExecutedTargets: state.Targets.Length);
            }
            catch (Exception exception) when (ExpectedFault(exception))
            { Fault(exception.GetType().Name); return Rejected(command, "firewall-maintenance"); }
        }
    }

    /// <summary>Called on startup and periodically by the helper. Never called synchronously by a game packet hook.</summary>
    public bool RecoverAndReconcile()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                var loaded = store.Load() ?? new(1, Scope, clock.GetUtcNow(), [], []);
                ValidateState(loaded);
                var now = clock.GetUtcNow();
                if (now < loaded.LastUtc) throw new InvalidDataException("Persistent clock moved backwards.");
                var next = loaded with { LastUtc = now,
                    Events = loaded.Events.Where(x => x.RetainUntilUtc > now).ToArray(),
                    Targets = loaded.Targets.Where(x => x.ExpiresUtc > now && Allowed(x.Address)).ToArray() };
                store.Save(next); state = next;
                ReconcileBackend(now);
                IsHealthy = true; LastFault = null;
                return true;
            }
            catch (Exception exception) when (ExpectedFault(exception)) { Fault(exception.GetType().Name); return false; }
        }
    }

    public bool ReleaseAllConfiguredBlocks()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsHealthy) return false;
            try
            {
                var next = state with { LastUtc = clock.GetUtcNow(), Targets = [] };
                store.Save(next); state = next; ReconcileBackend(clock.GetUtcNow());
                return true;
            }
            catch (Exception exception) when (ExpectedFault(exception)) { Fault(exception.GetType().Name); return false; }
        }
    }

    /// <summary>Bounded expiry cleanup remains possible when durable desired-state recovery fails.
    /// Removes only fully validated owned, expired rules; it never creates rules or declares recovery healthy.</summary>
    public bool RemoveExpiredConfiguredBlocks()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                var rules = backend.ReadOwnedRules();
                if (rules.Count > 1024) throw new InvalidDataException("Owned OS rule inventory exceeds budget.");
                // Validate the whole bounded inventory before the first mutation, including injected backends.
                foreach (var rule in rules)
                    if (rule.Name != RuleName(Scope, rule.Address) || rule.ApplicationPath != options.ApplicationPath ||
                        rule.LocalTcpPort != options.LocalTcpPort || !IsEligiblePublicAddress(rule.Address))
                        throw new InvalidDataException("Owned OS rule scope mismatch.");
                var now = clock.GetUtcNow();
                foreach (var rule in rules)
                    if (rule.ExpiresUtc <= now) backend.RemoveRule(rule.Name);
                return true;
            }
            catch (Exception exception) when (ExpectedFault(exception)) { Fault(exception.GetType().Name); return false; }
        }
    }

    private void ReconcileBackend(DateTimeOffset now)
    {
        var current = backend.ReadOwnedRules();
        if (current.Count > 1024) throw new InvalidDataException("Owned OS rule inventory exceeds budget.");
        var desired = state.Targets.Where(x => x.ExpiresUtc > now).Select(x => new RestrictedFirewallRule(
            RuleName(Scope, x.Address), x.Address, options.ApplicationPath, options.LocalTcpPort, x.ExpiresUtc)).ToDictionary(x => x.Name);
        foreach (var rule in current)
        {
            if (rule.Name != RuleName(Scope, rule.Address) || rule.ApplicationPath != options.ApplicationPath || rule.LocalTcpPort != options.LocalTcpPort)
                throw new InvalidDataException("Owned OS rule scope mismatch.");
            if (!desired.ContainsKey(rule.Name)) backend.RemoveRule(rule.Name);
        }
        foreach (var rule in desired.Values)
            if (!current.Contains(rule)) backend.EnsureRule(rule);
    }

    private void ValidateState(FirewallPersistentState value)
    {
        if (value.Version != 1 || value.Scope != Scope || value.Events is null || value.Targets is null ||
            value.Events.Length > options.MaximumEvents || value.Targets.Length > options.MaximumTargets ||
            value.Events.Any(x => x is null) || value.Targets.Any(x => x is null) ||
            value.Events.Select(x => x.EventId).Distinct().Count() != value.Events.Length ||
            value.Targets.Select(x => x.Address).Distinct(StringComparer.Ordinal).Count() != value.Targets.Length ||
            value.Events.Any(x => x.EventId == Guid.Empty || x.ContentHash is null || x.ContentHash.Length != 64 || !x.ContentHash.All(char.IsAsciiHexDigit) ||
                x.RetainUntilUtc > value.LastUtc.AddMinutes(20)) ||
            value.Targets.Any(x => !IsEligiblePublicAddress(x.Address) || x.ExpiresUtc > value.LastUtc.AddSeconds(900)))
            throw new InvalidDataException("Firewall persistent state is not valid for this bounded scope.");
    }

    private bool Allowed(string address) => exclusive.Contains(address) && !protectedAddresses.Contains(address) && IsEligiblePublicAddress(address);
    private static bool ExpectedFault(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.InvalidComObjectException;
    private void Fault(string reason) { IsHealthy = false; LastFault = reason; }
    private FirewallIpcResponse Rejected(FirewallIpcCommand command, string reason) => new(command.RequestId, false, reason, false);
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; IsHealthy = false; store.Dispose(); }
    }
}
