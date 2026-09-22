using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Core;

namespace AntiCheat.Persistence;

/// <summary>
/// One bounded startup marker, guarded by the owning FileEnforcementJournal's lock and process lease.
/// An armed unclean run cannot establish that its memory-only proof window was empty. Recovery stays
/// restricted until an explicit external verification; this class never invents the missing account.
/// </summary>
internal sealed class RunSafetyGuard(string directory, bool legacyAtOpen, TimeProvider clock)
{
    private sealed record State(int Schema, Guid ServerRunId, ExecutionScope Scope, bool CanProduceProofs,
        bool Clean, DateTimeOffset UpdatedUtc, string? VerifiedRecoveryReference);
    private sealed record Envelope(State State, string Digest);
    private readonly string path = Path.Combine(directory, "run-safety.state");
    private Guid? activeRun;
    private const int MaxBytes = 4096;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async ValueTask<RunSafetyStatus> BeginAsync(RunSafetyRequest request, CancellationToken cancellationToken)
    {
        if (request.ServerRunId == Guid.Empty || !Enum.IsDefined(request.Scope) ||
            request.Scope == ExecutionScope.ObserveOnly && request.CanProduceProofs)
            throw new InvalidDataException("Invalid run safety request.");
        var previous = await ReadAsync(cancellationToken);
        if (activeRun == request.ServerRunId)
        {
            if (previous?.ServerRunId != request.ServerRunId || previous.Scope != request.Scope ||
                previous.CanProduceProofs != request.CanProduceProofs)
                throw new InvalidDataException("Run safety marker changed during this run.");
            return RunSafetyStatus.Ready;
        }
        if (previous is null && legacyAtOpen) return RunSafetyStatus.LegacyStateNeedsReview;
        if (previous is { Clean: false, CanProduceProofs: true }) return RunSafetyStatus.PreviousEnforcementRunUnclean;
        var state = new State(1, request.ServerRunId, request.Scope, request.CanProduceProofs, false,
            clock.GetUtcNow(), previous?.VerifiedRecoveryReference);
        await WriteAsync(state, cancellationToken);
        activeRun = request.ServerRunId;
        return RunSafetyStatus.Ready;
    }

    public async ValueTask CompleteAsync(Guid serverRunId, CancellationToken cancellationToken)
    {
        var previous = await ReadAsync(cancellationToken);
        if (activeRun != serverRunId || previous?.ServerRunId != serverRunId)
            throw new InvalidOperationException("Cannot close another or unstarted safety run.");
        if (!previous.Clean) await WriteAsync(previous with { Clean = true, UpdatedUtc = clock.GetUtcNow() }, cancellationToken);
    }

    public async ValueTask ConfirmRecoveryAsync(string verificationReference, CancellationToken cancellationToken)
    {
        if (activeRun is not null) throw new InvalidOperationException("Recovery verification requires an inactive run.");
        if (string.IsNullOrWhiteSpace(verificationReference) || verificationReference.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(verificationReference));
        var previous = await ReadAsync(cancellationToken);
        var verified = previous is null
            ? new State(1, Guid.NewGuid(), ExecutionScope.ObserveOnly, false, true, clock.GetUtcNow(), verificationReference)
            : previous with { Clean = true, UpdatedUtc = clock.GetUtcNow(), VerifiedRecoveryReference = verificationReference };
        await WriteAsync(verified, cancellationToken);
    }

    private async ValueTask<State?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Oversized run safety marker.");
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Json, cancellationToken)
            ?? throw new InvalidDataException("Missing run safety marker.");
        var state = envelope.State;
        if (state is null || state.Schema != 1 || state.ServerRunId == Guid.Empty || !Enum.IsDefined(state.Scope) ||
            state.Scope == ExecutionScope.ObserveOnly && state.CanProduceProofs ||
            state.VerifiedRecoveryReference?.Length > 256 || envelope.Digest != Digest(state))
            throw new InvalidDataException("Invalid run safety marker.");
        return state;
    }

    private async ValueTask WriteAsync(State state, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(state, Digest(state)), Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Oversized run safety marker.");
        // This distinct suffix cannot be mistaken for an enforcement-intent temporary record.
        var temporary = Path.Combine(directory, "run-safety.next");
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        // A process may not enable proofs until the move succeeds. Power-loss durability of directory
        // metadata is an OS/filesystem deployment validation, not established by these process tests.
    }

    private static string Digest(State state) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, Json)));
}
