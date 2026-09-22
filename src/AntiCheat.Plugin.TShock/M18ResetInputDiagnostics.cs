using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AntiCheat.Core;

namespace AntiCheat.Plugin.TShock;

/// <summary>Bounded, opt-in TestLab observation. This helper has no hooks and supplies
/// no rule input; root calls it on the already established GameUpdate thread.</summary>
public sealed class M18ResetInputDiagnostics : IDisposable
{
    private sealed class Slot(SessionKey session)
    {
        public readonly SessionKey Session = session;
        public string? Last;
        public int Emitted;
    }
    private readonly Slot?[] slots = new Slot?[255];
    private readonly StreamWriter writer;
    private readonly string diskCandidateHash;
    private readonly string runtimeHash;
    private readonly bool locationEmpty;
    private int emitted, updateThread;
    private bool disposed;
    public const int MaximumPerGeneration = 12, MaximumRunRecords = 1024;
    public bool Healthy { get; private set; } = true;
    public string LastFault { get; private set; } = "none";
    public int DroppedRecords { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private M18ResetInputDiagnostics(string directory, string verifiedDiskCandidatePath)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "reset-inputs.jsonl");
        locationEmpty = string.IsNullOrEmpty(typeof(M18ResetInputDiagnostics).Assembly.Location);
        // TSAPI can load plugin bytes and leave Assembly.Location empty. This is
        // only the already verified run's disk candidate, never an in-memory hash.
        diskCandidateHash = Hash(File.ReadAllBytes(verifiedDiskCandidatePath));
        runtimeHash = Hash(File.ReadAllBytes(typeof(Terraria.Main).Assembly.Location));
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
    }
    public static M18ResetInputDiagnostics? TryCreate(string antiCheatDirectory, bool isolatedTestLab, string? verifiedDiskCandidatePath = null)
    {
        if (!isolatedTestLab || Environment.GetEnvironmentVariable("ANTICHEAT_M18_RESET_DIAGNOSTICS") != "1") return null;
        return new(Path.Combine(antiCheatDirectory, "m18-reset-inputs"), verifiedDiskCandidatePath ??
            Path.Combine(AppContext.BaseDirectory, "ServerPlugins", "AntiCheat.Plugin.TShock.dll"));
    }
    public void Observe(SessionKey session, long? boundAccount, M18ArrowSourceResetIntervals? collector, bool nativeHost)
    {
        if (disposed || !Healthy || (uint)session.Slot >= slots.Length) return;
        try
        {
            int thread = Environment.CurrentManagedThreadId;
            if (updateThread != 0 && thread != updateThread) throw new InvalidOperationException("Diagnostic thread changed.");
            updateThread = thread;
            var snapshot = collector?.Capture(session);
            var current = slots[session.Slot];
            if (current is null || current.Session != session) slots[session.Slot] = current = new(session);
            // Completion order is asynchronous. Trigger on finite semantic stages,
            // never each slot count/digest/sequence update while restoring350 slots.
            string signature = JsonSerializer.Serialize(new
            {
                boundAccount, nativeHost, healthy = collector?.Healthy == true, fault = collector?.LastFault,
                present = snapshot is not null, snapshot?.Account, snapshot?.NativeTransportIdentityVerified,
                worldInfoSeen = snapshot?.WorldInfoSequence > 0, readySeen = snapshot?.ReadySequence > 0,
                slotsCovered = snapshot?.Slots.Length == 350, snapshot?.RequiredWritesCompleted,
                snapshot?.SectionAccepted, snapshot?.SpawnAccepted, snapshot?.HistoricalResetSequenceObserved,
                snapshot?.SscRestoreInProgress, snapshot?.InitialResetInputsAvailable, snapshot?.Gap,
                laterItemsSeen = snapshot?.LaterItemExports > 0, laterBuffsSeen = snapshot?.LaterBuffExports > 0,
                serverProjectilesSeen = snapshot?.ServerProjectileExports > 0
            }, Json);
            if (signature == current.Last) return;
            current.Last = signature;
            if (emitted >= MaximumRunRecords || current.Emitted >= MaximumPerGeneration)
            { DroppedRecords = Math.Min(int.MaxValue - 1, DroppedRecords) + 1; return; }
            string SlotDigest() => Hash(Encoding.UTF8.GetBytes(string.Join(";", snapshot!.Slots.Select(item =>
                string.Create(CultureInfo.InvariantCulture, $"{item.Slot},{item.Type},{item.Stack},{item.Prefix}")))));
            var summary = new
            {
                session, boundAccount, nativeHost, collectorHealthy = collector?.Healthy == true,
                collectorFault = collector?.LastFault ?? "collector-unavailable", snapshotPresent = snapshot is not null,
                snapshotAccount = snapshot?.Account, snapshot?.NativeTransportIdentityVerified,
                snapshot?.WorldInfoSequence, snapshot?.ReadySequence,
                ownSlots = snapshot?.Slots.Length ?? 0,
                completedSlots = snapshot?.Slots.Count(item => item.WriteCompleted) ?? 0,
                firstSlotSequence = snapshot is { Slots.Length: > 0 } ? snapshot.Slots.Min(item => item.SendSequence) : 0,
                lastSlotSequence = snapshot is { Slots.Length: > 0 } ? snapshot.Slots.Max(item => item.SendSequence) : 0,
                slotsDigest = snapshot is null ? null : SlotDigest(),
                buffCount = snapshot?.Buffs.Length ?? 0,
                buffsDigest = snapshot is null ? null : Hash(Encoding.UTF8.GetBytes(string.Join(",", snapshot.Buffs))),
                snapshot?.Loadout, snapshot?.SectionAccepted, snapshot?.SpawnAccepted, snapshot?.RequiredWritesCompleted,
                snapshot?.HistoricalResetSequenceObserved, snapshot?.SscRestoreInProgress, snapshot?.InitialResetInputsAvailable,
                snapshot?.LaterItemExports, snapshot?.LaterBuffExports, snapshot?.ServerProjectileExports, snapshot?.Gap
            };
            bool limit = emitted == MaximumRunRecords - 1 || current.Emitted == MaximumPerGeneration - 1;
            var record = new { kind = limit ? "limit" : "snapshot", sequence = ++emitted, utc = DateTimeOffset.UtcNow,
                diskCandidateHash, locationEmpty, candidateHashMeaning = "verified-run-disk-image-not-in-memory-hash", runtimeHash,
                maximumPerGeneration = MaximumPerGeneration, maximumRunRecords = MaximumRunRecords,
                droppedRecords = DroppedRecords, summary };
            current.Emitted++;
            writer.WriteLine(JsonSerializer.Serialize(record, Json));
        }
        catch (Exception error) { Healthy = false; LastFault = error.GetType().Name; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose() { if (disposed) return; disposed = true; try { writer.Dispose(); } finally { Array.Clear(slots); } }
}
