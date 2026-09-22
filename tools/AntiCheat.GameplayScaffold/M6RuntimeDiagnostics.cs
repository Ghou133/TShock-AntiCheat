using System.Text.Json;
using System.Runtime.ExceptionServices;
using Terraria;
using TerrariaApi.Server;

namespace CompatibilityAudit;

/// <summary>Isolated QA observer: captures the exception supplied to LogMessageError before its
/// own socket-dependent formatter can fail. No packet bodies, exception interception or runtime replacement.</summary>
public sealed class M6RuntimeDiagnostics : IDisposable
{
    private readonly Func<string?> validatedOutput;
    private readonly M7ConnectionDiagnostics connections;
    private readonly long[] disconnectOrder = new long[256];
    private readonly int[] disconnectThread = new int[256];
    private readonly object errorGate = new();
    private readonly HashSet<string> errorSignatures = new(StringComparer.Ordinal);
    private int entries;
    private int sendErrors;
    private long order;
    private bool installed;
    private int observerFailed;
    public int Entries => entries;
    public M6RuntimeDiagnostics(Func<string?> validatedOutput, TerrariaPlugin? registrator = null)
    { this.validatedOutput = validatedOutput; connections = new(validatedOutput, registrator); }
    public void Install()
    {
        if (installed) return;
        connections.Install();
        HookEvents.Terraria.NetMessage.LogMessageError += OnError;
        HookEvents.Terraria.NetMessage.SendData += OnSend;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        installed = true;
    }
    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        // Exit-nosave has a separate direct SendData failure, not a CheckBytes catch. Filter before formatting.
        if (Volatile.Read(ref sendErrors) >= 4 || args.Exception is not NullReferenceException ||
            args.Exception.TargetSite?.DeclaringType?.FullName != "Terraria.NetMessage" ||
            args.Exception.TargetSite.Name != "mfwh_orig_SendData") return;
        string? directory = OutputDirectory(); if (directory is null) return;
        int index = Interlocked.Increment(ref sendErrors); if (index > 4) return;
        try
        {
            int thread = Environment.CurrentManagedThreadId;
            var candidates = Enumerable.Range(0, 256).Where(slot => disconnectThread[slot] == thread && disconnectOrder[slot] != 0)
                .Select(slot => new { slot, lastDisconnectOrder = disconnectOrder[slot], clientState = Netplay.Clients[slot]?.State,
                    socketMissing = Netplay.Clients[slot]?.Socket is null }).ToArray();
            var evidence = new { utc = DateTimeOffset.UtcNow, index, kind = "first-chance-native-SendData-null",
                order = Interlocked.Increment(ref order), managedThread = thread, exceptionType = args.Exception.GetType().FullName,
                stack = Limit(args.Exception.StackTrace, 8192), priorDisconnectCandidatesOnThread = candidates,
                contextIsExactTarget = false, connectionMetadata = connections.Snapshot(), Main.dedServ, Main.netMode };
            using var stream = new FileStream(Path.Combine(directory, $"m6-first-send-error-{index:D2}.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, evidence); stream.Flush(true);
        }
        catch (Exception error) { ReportObserverFailure(error); }
    }
    private void OnSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (Volatile.Read(ref observerFailed) != 0) return;
        if (args.msgType != 2 || (uint)args.remoteClient >= 256) return;
        disconnectOrder[args.remoteClient] = Interlocked.Increment(ref order);
        disconnectThread[args.remoteClient] = Environment.CurrentManagedThreadId;
    }
    private void OnError(object? sender, HookEvents.Terraria.NetMessage.LogMessageErrorEventArgs args)
    {
        string? directory = OutputDirectory();
        if (directory is null || Volatile.Read(ref entries) >= 8) return;
        try
        {
            int slot = args.bufferIndex;
            var client = (uint)slot < Netplay.Clients.Length ? Netplay.Clients[slot] : null;
            var buffer = (uint)slot < NetMessage.buffer.Length ? NetMessage.buffer[slot] : null;
            var exception = args.exception;
            int index;
            string signature = $"{slot}:{Limit(args.id, 32)}:{exception?.GetType().FullName}:{exception?.TargetSite?.Name}:{client?.Socket is null}:{client?.State}";
            lock (errorGate)
            {
                if (entries >= 8 || !errorSignatures.Add(signature)) return;
                index = ++entries;
            }
            // A fixed 8 records, bounded stack, fixed local filename; retain the first record through later failures.
            var evidence = new
            {
                utc = DateTimeOffset.UtcNow, index, order = Interlocked.Increment(ref order),
                managedThread = Environment.CurrentManagedThreadId, kind = "pre-LogMessageError",
                slot, messageId = Limit(args.id, 32), args.ContinueExecution,
                exceptionType = exception?.GetType().FullName, exceptionHResult = exception?.HResult,
                stack = Limit(exception?.StackTrace, 8192), innerType = exception?.InnerException?.GetType().FullName,
                innerStack = Limit(exception?.InnerException?.StackTrace, 4096),
                clientMissing = client is null, socketMissing = client?.Socket is null,
                clientState = client?.State, pendingTermination = client?.PendingTermination,
                pendingTerminationApproved = client?.PendingTerminationApproved,
                bufferMissing = buffer is null, totalData = buffer?.totalData, checkBytes = buffer?.checkBytes,
                disconnectOrder = (uint)slot < 256 ? disconnectOrder[slot] : 0,
                disconnectThread = (uint)slot < 256 ? disconnectThread[slot] : 0,
                connectionMetadata = connections.Snapshot(),
                Main.dedServ, Main.netMode
            };
            string path = Path.Combine(directory, $"m6-first-runtime-error-{index:D2}.json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, evidence); stream.Flush(true);
        }
        catch (Exception error)
        {
            // Logging failure is visible; do not cancel, replace or swallow the observed runtime exception.
            ReportObserverFailure(error);
        }
    }
    private string? OutputDirectory()
    {
        if (Volatile.Read(ref observerFailed) != 0) return null;
        try { return validatedOutput(); }
        catch (Exception error) { ReportObserverFailure(error); return null; }
    }
    private void ReportObserverFailure(Exception error)
    {
        if (Interlocked.Exchange(ref observerFailed, 1) != 0) return;
        try { Console.Error.WriteLine("M6_RUNTIME_DIAGNOSTIC_WRITE_FAILED " + error.GetType().Name); }
        catch (Exception) { /* An unavailable diagnostic sink cannot alter the observed runtime failure. */ }
    }
    private static string? Limit(string? value, int maximum) => value?.Length > maximum ? value[..maximum] : value;
    public void Dispose()
    {
        if (!installed) return;
        HookEvents.Terraria.NetMessage.LogMessageError -= OnError;
        HookEvents.Terraria.NetMessage.SendData -= OnSend;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        connections.Dispose();
        installed = false;
    }
}
