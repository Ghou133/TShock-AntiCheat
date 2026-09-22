using System.Security.Cryptography;
using System.Text.Json;

namespace AntiCheat.Rules;

/// <summary>One bounded snapshot, atomic replacement, and an exclusive helper-process lease. No account/ban data.</summary>
public sealed class FileFirewallStateStore : IFirewallStateStore
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private sealed record Envelope(int Version, string Sha256, string Payload);
    private readonly string path;
    private readonly FileStream lease;
    private bool disposed;

    public FileFirewallStateStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An explicit absolute helper-state directory is required.");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // The deployment owner must supply a private directory; do not follow directory/file junctions.
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point in helper-state path.");
        path = Path.Combine(directory, "firewall-state-v1.json");
        RejectReparse(path);
        string lockPath = Path.Combine(directory, "firewall-state-v1.lock"); RejectReparse(lockPath);
        lease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
    }

    public FirewallPersistentState? Load()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RejectReparse(path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Firewall state exceeds capacity.");
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes);
        if (envelope is null || envelope.Version != 1 || envelope.Payload is null || envelope.Payload.Length > MaximumBytes ||
            envelope.Sha256 is null || envelope.Sha256.Length != 64) throw new InvalidDataException("Invalid firewall state envelope.");
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.Sha256), SHA256.HashData(payload)))
                throw new InvalidDataException("Firewall state checksum mismatch.");
        }
        catch (FormatException exception) { throw new InvalidDataException("Invalid firewall state encoding.", exception); }
        return JsonSerializer.Deserialize<FirewallPersistentState>(payload) ?? throw new InvalidDataException("Empty firewall state.");
    }

    public void Save(FirewallPersistentState state)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToHexString(SHA256.HashData(payload)), Convert.ToBase64String(payload)));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Firewall state exceeds capacity.");
        RejectReparse(path);
        string temporary = path + ".pending";
        RejectReparse(temporary);
        // The same lease covers this fixed temporary name; bounded crash leftovers are overwritten.
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }

    private static void RejectReparse(string value)
    {
        if (File.Exists(value) && (File.GetAttributes(value) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse point in helper-state file.");
    }
    public void Dispose() { if (disposed) return; disposed = true; lease.Dispose(); }
}
