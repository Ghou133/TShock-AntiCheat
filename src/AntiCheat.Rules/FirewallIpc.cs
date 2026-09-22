using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum FirewallIpcOperation { RecordEvent, SimulateTemporaryBlock, ExecuteTemporaryBlock }
public sealed record FirewallIpcCommand(Guid RequestId, FirewallIpcOperation Operation, FirewallDryRunEvent SourceEvent);
public sealed record FirewallIpcResponse(Guid RequestId, bool Accepted, string Reason, bool DryRun = true,
    int RecordedEvents = 0, int SimulatedTargets = 0, int ExecutedTargets = 0);

public interface IFirewallIpcExecutor
{
    FirewallIpcResponse Apply(FirewallIpcCommand command);
}

/// <summary>Only records source events and simulates a temporary address set in memory. There is no OS firewall API.</summary>
public sealed class InMemoryFirewallExecutor : IFirewallIpcExecutor
{
    private sealed record Stored(string ContentHash, long ExpiresAt, FirewallIpcResponse Response);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly int maximumEvents;
    private readonly int maximumTargets;
    private readonly TimeSpan eventTtl;
    private readonly Dictionary<Guid, Stored> events = [];
    private readonly Dictionary<string, long> targets = new(StringComparer.Ordinal);

    public InMemoryFirewallExecutor(TimeProvider clock, int maximumEvents = 1024, int maximumTargets = 256,
        TimeSpan? eventTtl = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maximumEvents is < 1 or > 4096 || maximumTargets is < 1 or > 1024 || (eventTtl ?? TimeSpan.FromMinutes(20)) <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        this.clock = clock; this.maximumEvents = maximumEvents; this.maximumTargets = maximumTargets;
        this.eventTtl = eventTtl ?? TimeSpan.FromMinutes(20);
    }

    public FirewallIpcResponse Apply(FirewallIpcCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = FirewallIpcProtocol.Validate(command, clock);
        if (validation is not null) return new(command.RequestId, false, validation);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { command.Operation, command.SourceEvent }, FirewallIpcProtocol.JsonOptions)));
        lock (gate)
        {
            var now = clock.GetTimestamp();
            Purge(now);
            if (events.TryGetValue(command.SourceEvent.EventId, out var prior))
                return prior.ContentHash == hash ? prior.Response with { RequestId = command.RequestId, Reason = "duplicate-event", RecordedEvents = events.Count, SimulatedTargets = targets.Count }
                    : new(command.RequestId, false, "event-id-content-conflict", true, events.Count, targets.Count);
            if (events.Count >= maximumEvents)
                return new(command.RequestId, false, "event-capacity", true, events.Count, targets.Count);
            if (command.Operation == FirewallIpcOperation.SimulateTemporaryBlock)
            {
                var target = command.SourceEvent.ProposedTarget!;
                if (!targets.ContainsKey(target) && targets.Count >= maximumTargets)
                    return new(command.RequestId, false, "target-capacity", true, events.Count, targets.Count);
                var expiresAt = AddDuration(now, TimeSpan.FromSeconds(command.SourceEvent.SuggestedTtlSeconds));
                // Repeated events cannot shorten an existing simulated exclusion.
                targets[target] = targets.TryGetValue(target, out var previous) ? Math.Max(previous, expiresAt) : expiresAt;
            }
            var response = new FirewallIpcResponse(command.RequestId, true,
                command.Operation == FirewallIpcOperation.RecordEvent ? "recorded-dry-run" : "simulated-temporary-block", true, events.Count + 1, targets.Count);
            events.Add(command.SourceEvent.EventId, new(hash, AddDuration(now, eventTtl), response));
            return response;
        }
    }

    public bool IsSimulatedBlocked(string address)
    {
        if (!NetworkControls.TryNormalizeAddress(address, out var parsed)) return false;
        lock (gate) { Purge(clock.GetTimestamp()); return targets.ContainsKey(parsed!.ToString()); }
    }

    private long AddDuration(long timestamp, TimeSpan duration)
    {
        double amount = duration.TotalSeconds * clock.TimestampFrequency;
        return amount >= long.MaxValue - timestamp ? long.MaxValue : timestamp + (long)Math.Ceiling(amount);
    }
    private void Purge(long now)
    {
        // IPC is outside packet callbacks. This scan is bounded by 4096 events and 1024 simulated targets.
        foreach (var key in events.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray()) events.Remove(key);
        foreach (var key in targets.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) targets.Remove(key);
    }
}

/// <summary>One authenticated, framed local request at a time; use in an isolated helper, never synchronously in a packet hook.</summary>
public sealed class FirewallIpcServer : IDisposable
{
    private readonly string pipeName;
    private readonly byte[] key;
    private readonly TimeProvider clock;
    private readonly IFirewallIpcExecutor executor;
    private readonly TimeSpan timeout;
    private readonly Func<string, NamedPipeServerStream>? pipeFactory;
    private bool disposed;

    public FirewallIpcServer(string pipeName, byte[] sharedKey, TimeProvider clock, InMemoryFirewallExecutor executor,
        TimeSpan? timeout = null) : this(pipeName, sharedKey, clock, (IFirewallIpcExecutor)executor, timeout) { }

    public FirewallIpcServer(string pipeName, byte[] sharedKey, TimeProvider clock, IFirewallIpcExecutor executor,
        TimeSpan? timeout = null, Func<string, NamedPipeServerStream>? pipeFactory = null)
    {
        FirewallIpcProtocol.ValidateEndpoint(pipeName, sharedKey);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(executor);
        this.pipeName = pipeName; key = sharedKey.ToArray(); this.clock = clock; this.executor = executor;
        this.timeout = timeout ?? TimeSpan.FromSeconds(3);
        this.pipeFactory = pipeFactory;
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<FirewallIpcResponse> ServeOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var pipe = pipeFactory?.Invoke(pipeName) ?? new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        FirewallIpcResponse response;
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            var bytes = await FirewallIpcProtocol.ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false);
            var command = FirewallIpcProtocol.Authenticate(bytes, key, clock, out var reason);
            response = command is null ? new(Guid.Empty, false, reason) : executor.Apply(command);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(Guid.Empty, false, "ipc-timeout"); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or FormatException or ArgumentException)
        { response = new(Guid.Empty, false, "malformed-ipc-request"); }
        if (pipe.IsConnected)
        {
            try { await FirewallIpcProtocol.WriteFrameAsync(pipe, FirewallIpcProtocol.EncodeResponse(response, key, clock), deadline.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { /* The caller may have closed after rejection. */ }
        }
        return response;
    }

    public void Dispose() { if (disposed) return; disposed = true; CryptographicOperations.ZeroMemory(key); }
}

public static class FirewallIpcClient
{
    /// <summary>No shell, elevation, remote host or arbitrary action is accepted. sharedKey is never included in the wire payload.</summary>
    public static async Task<FirewallIpcResponse> SendAsync(string pipeName, byte[] sharedKey, FirewallIpcCommand command,
        TimeProvider clock, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        FirewallIpcProtocol.ValidateEndpoint(pipeName, sharedKey);
        ArgumentNullException.ThrowIfNull(command); ArgumentNullException.ThrowIfNull(clock);
        var duration = timeout ?? TimeSpan.FromSeconds(3);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var frame = FirewallIpcProtocol.Encode(command, sharedKey, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await FirewallIpcProtocol.WriteFrameAsync(pipe, frame, deadline.Token).ConfigureAwait(false);
        var responseBytes = await FirewallIpcProtocol.ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false);
        var response = FirewallIpcProtocol.AuthenticateResponse(responseBytes, sharedKey, clock);
        if ((!response.DryRun && (command.Operation != FirewallIpcOperation.ExecuteTemporaryBlock ||
                command.SourceEvent.DryRun || !command.SourceEvent.Execute)) ||
            (response.RequestId != command.RequestId && response.RequestId != Guid.Empty))
            throw new InvalidDataException("IPC response does not match the request.");
        return response;
    }
}

internal static class FirewallIpcProtocol
{
    internal const int MaximumFrameBytes = 16384;
    private const int MaximumPayloadBytes = 8192;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record Payload(DateTimeOffset SentUtc, FirewallIpcCommand Command);
    private sealed record ResponsePayload(DateTimeOffset SentUtc, FirewallIpcResponse Response);
    private sealed record Envelope(int Version, string Payload, string AuthenticationTag);

    internal static void ValidateEndpoint(string pipeName, byte[] sharedKey)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 96 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            || sharedKey is null || sharedKey.Length != 32)
            throw new ArgumentException("Expected a local bounded pipe name and a 256-bit shared key.");
    }

    internal static byte[] Encode(FirewallIpcCommand command, byte[] key, TimeProvider clock)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(clock.GetUtcNow(), command), JsonOptions);
        if (payload.Length > MaximumPayloadBytes) throw new InvalidDataException("IPC payload exceeds budget.");
        var envelope = new Envelope(1, Convert.ToBase64String(payload), Convert.ToHexString(Sign(key, payload, 0)));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    internal static byte[] EncodeResponse(FirewallIpcResponse response, byte[] key, TimeProvider clock)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ResponsePayload(clock.GetUtcNow(), response), JsonOptions);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(payload), Convert.ToHexString(Sign(key, payload, 1))), JsonOptions);
    }

    internal static FirewallIpcResponse AuthenticateResponse(byte[] bytes, byte[] key, TimeProvider clock)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
        if (envelope is null || envelope.Version != 1 || envelope.Payload is null || envelope.Payload.Length > 12000
            || envelope.AuthenticationTag is null || envelope.AuthenticationTag.Length != 64)
            throw new InvalidDataException("Invalid IPC response envelope.");
        var payload = Convert.FromBase64String(envelope.Payload);
        if (payload.Length > MaximumPayloadBytes || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.AuthenticationTag), Sign(key, payload, 1)))
            throw new InvalidDataException("IPC response authentication failed.");
        var response = JsonSerializer.Deserialize<ResponsePayload>(payload, JsonOptions);
        if (response?.Response is null || (clock.GetUtcNow() - response.SentUtc).Duration() > TimeSpan.FromSeconds(30))
            throw new InvalidDataException("IPC response is empty or expired.");
        return response.Response;
    }

    private static byte[] Sign(byte[] key, byte[] payload, byte direction)
    {
        byte[] domainSeparated = new byte[payload.Length + 1];
        domainSeparated[0] = direction;
        payload.CopyTo(domainSeparated, 1);
        return HMACSHA256.HashData(key, domainSeparated);
    }

    internal static FirewallIpcCommand? Authenticate(byte[] bytes, byte[] key, TimeProvider clock, out string reason)
    {
        reason = "ipc-authentication-failed";
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
        if (envelope is null || envelope.Version != 1 || envelope.Payload is null || envelope.Payload.Length > 12000
            || envelope.AuthenticationTag is null || envelope.AuthenticationTag.Length != 64) return null;
        var payloadBytes = Convert.FromBase64String(envelope.Payload);
        if (payloadBytes.Length > MaximumPayloadBytes) return null;
        var tag = Convert.FromHexString(envelope.AuthenticationTag);
        if (!CryptographicOperations.FixedTimeEquals(tag, Sign(key, payloadBytes, 0))) return null;
        var payload = JsonSerializer.Deserialize<Payload>(payloadBytes, JsonOptions);
        if (payload is null || payload.Command is null) return null;
        if ((clock.GetUtcNow() - payload.SentUtc).Duration() > TimeSpan.FromSeconds(30))
        { reason = "ipc-request-expired"; return null; }
        reason = "authenticated";
        return payload.Command;
    }

    internal static string? Validate(FirewallIpcCommand command, TimeProvider clock, bool allowExecution = false)
    {
        if (command is null || command.RequestId == Guid.Empty || !Enum.IsDefined(command.Operation) || command.SourceEvent is null)
            return "invalid-ipc-command";
        bool execute = command.Operation == FirewallIpcOperation.ExecuteTemporaryBlock;
        if (execute && !allowExecution) return "os-execution-not-enabled";
        var value = command.SourceEvent;
        if (value.EventId == Guid.Empty || value.DryRun == execute || value.Execute != execute || !Enum.IsDefined(value.Recommendation)
            || !Enum.IsDefined(value.Reason) || value.SuggestedTtlSeconds is < 1 or > 900
            || value.AddressProvenance != "server-socket-peer" || value.RuleVersion is null || value.RuleVersion.Length > 128
            || value.EvidenceReference?.Length > 256 || value.Window is null || value.Window.EndUtc < value.Window.StartUtc
            || value.Window.Connections < 0 || value.Window.Bytes < 0 || value.Window.Cost < 0
            || !NetworkControls.TryNormalizeAddress(value.SocketPeer, out var peer)
            || peer!.ToString() != value.SocketPeer || value.AccountId is <= 0)
            return "invalid-source-event";
        if ((clock.GetUtcNow() - value.ObservedUtc).Duration() > TimeSpan.FromSeconds(30)
            || value.Window.EndUtc > value.ObservedUtc + TimeSpan.FromSeconds(30)) return "source-event-expired-or-future";
        if (value.VerifiedClientAddress is not null && (!value.ProxyPeer
            || !NetworkControls.TryNormalizeAddress(value.VerifiedClientAddress, out _))) return "unverified-client-address";
        if (value.ProxyPeer && value.ProposedTarget is not null) return "proxy-target-forbidden";
        if (value.ProposedTarget is not null && value.ProposedTarget != peer.ToString()) return "target-must-be-actual-socket-peer";
        if (command.Operation is FirewallIpcOperation.SimulateTemporaryBlock or FirewallIpcOperation.ExecuteTemporaryBlock)
        {
            // Recompute eligibility with the existing Core policy instead of trusting a mutable recommendation field.
            var eligibility = new FirewallDryRun(clock).Create(peer, value.Window, value.Reason,
                TimeSpan.FromSeconds(value.SuggestedTtlSeconds), proxyPeer: value.ProxyPeer, exclusiveSourceVerified: value.ExclusiveSourceVerified);
            if (value.Recommendation != FirewallRecommendation.ProposeTemporaryBlock || value.ProposedTarget is null
                || eligibility.Recommendation != FirewallRecommendation.ProposeTemporaryBlock)
                return execute ? "source-not-eligible-for-execution" : "source-not-eligible-for-simulation";
        }
        return null;
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("IPC frame outside capacity.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("IPC frame outside capacity.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
