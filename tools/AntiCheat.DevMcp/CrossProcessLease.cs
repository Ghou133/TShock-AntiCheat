using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AntiCheat.DevMcp;

public sealed record LeaseOwner(int Pid, DateTimeOffset StartedUtc, string ExecutablePath,
    string InstanceId, string JobId, string? JobName = null)
{
    public static LeaseOwner Current(string instanceId, string jobId, string? jobName = null)
    {
        using var process = Process.GetCurrentProcess();
        return new(Environment.ProcessId, process.StartTime.ToUniversalTime(),
            Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable."),
            instanceId, jobId, jobName);
    }
}

public sealed record LeaseAcquireResult(bool Acquired, CrossProcessLease? Lease, string? BlockedReason);

/// <summary>The exclusive open handle is authority. Metadata and reusable PIDs never grant a lease.</summary>
public sealed class CrossProcessLease : IDisposable
{
    private readonly WindowsPathPins pins;
    private readonly FileStream stream;
    private int disposed;
    public string Path => pins.FullPath;
    public LeaseOwner Owner { get; }

    private CrossProcessLease(WindowsPathPins pins, FileStream stream, LeaseOwner owner)
        => (this.pins, this.stream, Owner) = (pins, stream, owner);

    public static LeaseAcquireResult TryAcquire(string absolutePath, LeaseOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actual = LeaseOwner.Current(owner.InstanceId, owner.JobId, owner.JobName);
        if (owner.Pid != actual.Pid || owner.StartedUtc != actual.StartedUtc ||
            !string.Equals(WindowsPathPins.Normalize(owner.ExecutablePath),
                WindowsPathPins.Normalize(actual.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Lease metadata must identify the actual handle-owning process.", nameof(owner));
        if (string.IsNullOrWhiteSpace(owner.InstanceId) || owner.InstanceId.Length > 128 ||
            string.IsNullOrWhiteSpace(owner.JobId) || owner.JobId.Length > 128 || owner.JobName?.Length > 256)
            throw new ArgumentException("Lease identity fields are missing or too long.", nameof(owner));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, ownership = "exclusive-file-handle", owner,
            acquiredUtc = DateTimeOffset.UtcNow, metadataIsNotLockAuthority = true
        });
        if (bytes.Length > 16384) throw new ArgumentException("Lease metadata is too large.", nameof(owner));
        var pins = WindowsPathPins.ForFile(absolutePath);
        FileStream? stream = null;
        try
        {
            // OPEN_ALWAYS preserves the lock's file identity across release/reacquisition. A stale
            // metadata record is harmless. Never delete/replace a lock another process may hold.
            var handle = pins.OpenFile(WindowsPathPins.Native.GenericRead | WindowsPathPins.Native.GenericWrite,
                share: 0, WindowsPathPins.Native.OpenAlways);
            try { stream = new FileStream(handle, FileAccess.ReadWrite, 4096, false); }
            catch { handle.Dispose(); throw; }
            stream.SetLength(0);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new(true, new CrossProcessLease(pins, stream, actual), null);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 32 or 33)
        {
            stream?.Dispose();
            pins.Dispose();
            return new(false, null, "lease-held-by-another-open-handle");
        }
        catch { stream?.Dispose(); pins.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { stream.Dispose(); }
        finally { pins.Dispose(); }
    }
}
