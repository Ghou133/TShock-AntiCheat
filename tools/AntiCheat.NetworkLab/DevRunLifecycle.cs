using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntiCheat.DevMcp;

// Optional, local development-job observation. This never changes the scenario inputs,
// pacing, assertions, or production plugin. A missing environment variable is a no-op.
internal sealed class DevRunLifecycle : IDisposable
{
    private static DevRunLifecycle? current;
    private readonly string projectRoot, jobDirectory, jobId;
    private readonly WindowsPathPins directoryPins;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new(), monitorStop = new();
    private readonly Task monitor;
    private readonly JsonObject state;
    private int cleanup;
    private int suppression;
    private Exception? monitorFailure;
    private string? instanceId;

    private DevRunLifecycle(string root, string directory, string run, string report, int port, bool synthetic)
    {
        projectRoot = WindowsPathPins.Normalize(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        jobDirectory = WindowsPathPins.Normalize(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        jobId = Path.GetFileName(jobDirectory);
        directoryPins = WindowsPathPins.ForDirectory(jobDirectory);
        try
        {
        ValidateDirectory();
        string path = Path.Combine(jobDirectory, "lifecycle.json");
        state = File.Exists(path) ? JsonNode.Parse(ReadBounded(path, 256 * 1024))?.AsObject()
            ?? throw new InvalidDataException("Development lifecycle is not an object.") : new JsonObject();
        state["schemaVersion"] = 1;
        state["jobId"] = jobId;
        state["kind"] = "network";
        state["state"] = "running";
        state["runDirectory"] = run;
        state["isolatedDirectory"] = run;
        state["reportDirectory"] = report;
        state["summaryPath"] = Path.Combine(report, "summary.json");
        state["worldPath"] = Path.Combine(run, "worlds", "AntiCheatNetworkLab.wld");
        state["databasePath"] = Path.Combine(run, "tshock", "tshock.sqlite");
        state["loopbackPort"] = port;
        using var process = Process.GetCurrentProcess();
        state["runnerProcess"] = ProcessIdentity(process, Environment.ProcessPath);
        state["runnerAssemblyPath"] = typeof(DevRunLifecycle).Assembly.Location;
        state["serverProcesses"] = new JsonArray();
        state["clientProcesses"] = synthetic ? new JsonArray(ProcessIdentity(process, Environment.ProcessPath)) : null;
        state["clientProcessMeaning"] = synthetic ? "synthetic clients execute in the runner process" : "external GUI identity is not observed here";
        state["coordination"] = "MCP owns its lease; this runner cannot coordinate unrelated direct CLI/build invocations";
        ChangePhase("runner_starting");
        monitor = MonitorAsync();
        }
        catch { directoryPins.Dispose(); cancellation.Dispose(); monitorStop.Dispose(); throw; }
    }

    public static DevRunLifecycle? Open(string root, string run, string report, int port, bool synthetic)
    {
        string? directory = Environment.GetEnvironmentVariable("ANTICHEAT_DEV_JOB_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return null;
        return current = new DevRunLifecycle(root, directory, run, report, port, synthetic);
    }

    public static void Check()
    {
        var active = Volatile.Read(ref current);
        if (active is null || Volatile.Read(ref active.cleanup) != 0 || Volatile.Read(ref active.suppression) > 0) return;
        if (Volatile.Read(ref active.monitorFailure) is { } error) throw new IOException("Development cancellation monitor failed.", error);
        if (active.cancellation.IsCancellationRequested) throw new DevRunCanceledException();
    }

    public static CancellationToken Token
    {
        get
        {
            var active = Volatile.Read(ref current);
            return active is null || Volatile.Read(ref active.cleanup) != 0 || Volatile.Read(ref active.suppression) > 0
                ? CancellationToken.None : active.cancellation.Token;
        }
    }

    public static async Task WaitAsync(Task task, TimeSpan timeout)
    {
        if (task.IsFaulted) { await task.ConfigureAwait(false); return; }
        Check();
        var token = Token;
        try { await task.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == token)
        {
            if (task.IsFaulted) await task.ConfigureAwait(false);
            Check(); throw;
        }
        Check();
    }

    // The operation receives the token itself, so semaphore/socket work is canceled too;
    // merely canceling a WaitAsync wrapper would leave a live write behind. Existing local
    // cancellation and actual I/O faults retain their original exception/failed semantics.
    public static async Task RunCancelable(Func<CancellationToken, Task> operation, CancellationToken local = default)
    {
        local.ThrowIfCancellationRequested();
        Check();
        var devToken = Token;
        if (!devToken.CanBeCanceled) { await operation(local).ConfigureAwait(false); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(local, devToken);
        try { await operation(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!local.IsCancellationRequested && devToken.IsCancellationRequested &&
                                                      error.CancellationToken == linked.Token)
        { Check(); throw; }
        Check();
    }

    public static IDisposable SuppressCancellation()
    {
        var active = current;
        if (active is not null) Interlocked.Increment(ref active.suppression);
        return new Suppression(active);
    }

    public void EnterCleanup()
    {
        Interlocked.Exchange(ref cleanup, 1);
        ChangePhase("cleanup");
    }

    public void Phase(string phase) => ChangePhase(phase);

    public void ServerStarted(Process process)
    {
        lock (gate)
        {
            var identity = ProcessIdentity(process, process.StartInfo.FileName);
            identity["parentPid"] = Environment.ProcessId;
            state["serverProcess"] = identity.DeepClone();
            var servers = state["serverProcesses"]!.AsArray();
            if (servers.Count >= 8) throw new InvalidOperationException("Development server identity limit exceeded.");
            servers.Add(identity);
            Write();
        }
    }

    public void Complete(string result, int exitCode, string failure, bool forced, bool runSafetyClean, int? serverExitCode)
    {
        var monitorFault = FinishMonitoringAsync().GetAwaiter().GetResult();
        if (monitorFault is not null)
        {
            result = "failed"; exitCode = 1;
            if (!failure.Contains(monitorFault, StringComparison.Ordinal)) failure += " lifecycle-monitor:" + monitorFault;
        }
        lock (gate)
        {
            state["state"] = result;
            state["exitCode"] = exitCode;
            state["failure"] = failure.Length == 0 ? null : failure;
            state["monitorFailure"] = monitorFault;
            state["cleanUp"] = new JsonObject
            {
                ["forced"] = forced, ["runSafetyClean"] = runSafetyClean,
                ["serverExitCode"] = serverExitCode,
                ["status"] = forced ? "forced_cleanup" : state["serverProcesses"]!.AsArray().Count == 0 ? "not_started"
                    : runSafetyClean && serverExitCode == 0 ? "completed" : "not_confirmed"
            };
            ChangePhase("completed");
        }
    }

    public async Task<string?> FinishMonitoringAsync()
    {
        monitorStop.Cancel();
        await monitor.ConfigureAwait(false);
        var failure = Volatile.Read(ref monitorFailure);
        if (failure is null) return null;
        var text = failure.GetType().Name + ": " + failure.Message;
        return text.Length <= 2048 ? text : text[..2048];
    }

    private void ChangePhase(string phase)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var timings = state["phaseTimings"] as JsonArray ?? (state["timings"] as JsonArray)?.DeepClone().AsArray() ?? new JsonArray();
            if (timings.Parent is null) state["phaseTimings"] = timings;
            state.Remove("timings");
            long dropped = state["droppedPhaseCount"]?.GetValue<long>() ?? 0;
            if (dropped < 0) throw new InvalidDataException("Negative phase timing drop count.");
            while (timings.Count > 32) { timings.RemoveAt(0); if (dropped < long.MaxValue) ++dropped; }
            var changed = !string.Equals(state["phase"]?.GetValue<string>(), phase, StringComparison.Ordinal);
            if (changed && state["phase"]?.GetValue<string>() is { } previous &&
                DateTimeOffset.TryParse(state["phaseStartedUtc"]?.GetValue<string>(), out var started))
            {
                if (timings.Count == 32) { timings.RemoveAt(0); if (dropped < long.MaxValue) ++dropped; }
                timings.Add(new JsonObject { ["phase"] = previous, ["startedUtc"] = started.ToString("O"),
                    ["endedUtc"] = now.ToString("O"), ["elapsedMs"] = Math.Max(0, (now - started).TotalMilliseconds) });
            }
            state["droppedPhaseCount"] = dropped;
            state["phase"] = phase;
            if (changed || state["phaseStartedUtc"] is null) state["phaseStartedUtc"] = now.ToString("O");
            Write();
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                ValidateDirectory();
                string path = Path.Combine(jobDirectory, "cancel.request");
                if (File.Exists(path))
                {
                    using var request = JsonDocument.Parse(ReadBounded(path, 16 * 1024));
                    if (request.RootElement.GetProperty("jobId").GetString() != jobId)
                        throw new InvalidDataException("Cancellation request belongs to another job.");
                    lock (gate)
                    {
                        state["cancelRequestedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                        Write();
                    }
                    cancellation.Cancel();
                    return;
                }
            } while (await timer.WaitForNextTickAsync(monitorStop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Volatile.Write(ref monitorFailure, error);
            cancellation.Cancel();
        }
    }

    private void ValidateDirectory()
    {
        string expectedParent = Path.Combine(projectRoot, "artifacts", "devmcp", "jobs");
        if (jobId.Length != 32 || jobId.Any(c => !Uri.IsHexDigit(c)) ||
            !string.Equals(Path.GetDirectoryName(jobDirectory), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Development job must be a direct 32-hex child of this project's artifacts/devmcp/jobs.");
        // directoryPins holds the already-verified full ancestor chain for this instance.
        using var marker = JsonDocument.Parse(ReadBounded(Path.Combine(jobDirectory, ".terraria-dev-owned"), 16 * 1024));
        var value = marker.RootElement;
        string markedRoot = Path.GetFullPath(value.GetProperty("projectRoot").GetString()!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var owner = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent() : null;
        var markedInstance = value.GetProperty("instanceId").GetString();
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("jobId").GetString() != jobId ||
            !string.Equals(markedRoot, projectRoot, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(markedInstance) || instanceId is not null && instanceId != markedInstance ||
            !OperatingSystem.IsWindows() || value.GetProperty("ownerSid").GetString() != owner?.User?.Value)
            throw new InvalidDataException("Development job marker does not match this project, job, or Windows owner.");
        instanceId ??= markedInstance;
    }

    private static string ReadBounded(string path, long maximumBytes)
    {
        using var pins = WindowsPathPins.ForFile(path);
        var text = new UTF8Encoding(false, true).GetString(pins.ReadBounded(checked((int)maximumBytes), allowAtomicReplace: true));
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private void Write()
    {
        ValidateDirectory();
        var now = DateTimeOffset.UtcNow;
        state["utc"] = now.ToString("O");
        state["updatedUtc"] = now.ToString("O");
        if (state["startedUtc"] is null) state["startedUtc"] = now.ToString("O");
        if (DateTimeOffset.TryParse(state["startedUtc"]?.GetValue<string>(), out var started))
            state["elapsedMs"] = Math.Max(0, (now - started).TotalMilliseconds);
        if (DateTimeOffset.TryParse(state["phaseStartedUtc"]?.GetValue<string>(), out var phaseStarted))
            state["phaseElapsedMs"] = Math.Max(0, (now - phaseStarted).TotalMilliseconds);
        string path = Path.Combine(jobDirectory, "lifecycle.json");
        using var pins = WindowsPathPins.ForFile(path);
        pins.AtomicWrite(new UTF8Encoding(false, true).GetBytes(state.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), 256 * 1024);
    }

    private static JsonObject ProcessIdentity(Process process, string? executable) => new()
    { ["pid"] = process.Id, ["startedUtc"] = process.StartTime.ToUniversalTime().ToString("O"), ["executable"] = executable };

    public void Dispose()
    {
        FinishMonitoringAsync().GetAwaiter().GetResult();
        monitorStop.Dispose(); cancellation.Dispose();
        directoryPins.Dispose();
        if (ReferenceEquals(current, this)) current = null;
    }

    private sealed class Suppression(DevRunLifecycle? owner) : IDisposable
    {
        private DevRunLifecycle? active = owner;
        public void Dispose() { var prior = Interlocked.Exchange(ref active, null); if (prior is not null) Interlocked.Decrement(ref prior.suppression); }
    }
}

internal sealed class DevRunCanceledException() : OperationCanceledException("Development job cancellation requested.");
