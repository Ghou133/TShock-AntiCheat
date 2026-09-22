using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Rules;

// External helper only. This entry never elevates, launches processes, installs a service or edits global firewall policy.
if (args.Length != 2 || args[0] is not ("--validate" or "--serve-dry-run" or "--execute-os"))
{
    Console.Error.WriteLine("Usage: AntiCheat.FirewallAgent --validate|--serve-dry-run|--execute-os <absolute-config.json>");
    return 2;
}

byte[]? key = null;
WindowsFirewallExecutor? persistent = null;
bool cleanupAttempted = false;
try
{
    if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("Use an explicit absolute config path.");
    using var configStream = File.OpenRead(args[1]);
    if (configStream.Length is <= 0 or > 128 * 1024) throw new InvalidDataException("Config exceeds capacity.");
    var config = JsonSerializer.Deserialize<AgentConfiguration>(configStream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Missing config.");
    WindowsFirewallExecutor.ValidateOptions(config.Firewall);
    if (!Path.IsPathFullyQualified(config.StateDirectory) || string.IsNullOrWhiteSpace(config.PipeName) ||
        config.PipeName.Length > 96 || config.PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        throw new InvalidDataException("State directory and local pipe endpoint must be explicit and bounded.");
    if (args[0] == "--validate")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, osActivated = false, config.EnableOperatingSystemRules,
            exclusiveSources = config.Firewall.ExclusiveSourceAddresses.Length }));
        return 0;
    }
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    // Secret is supplied over owned redirected stdin, never command-line/environment/logs or output evidence.
    char[] secretBuffer = new char[45]; int secretLength = 0;
    while (secretLength < secretBuffer.Length)
    {
        int character = Console.In.Read();
        if (character is -1 or '\n') break;
        if (character == '\r') continue;
        secretBuffer[secretLength++] = (char)character;
    }
    if (secretLength != 44) throw new InvalidDataException("Expected one base64 256-bit key on stdin.");
    key = Convert.FromBase64CharArray(secretBuffer, 0, secretLength); Array.Clear(secretBuffer);
    if (key.Length != 32) throw new InvalidDataException("Expected a 256-bit key.");
    IFirewallIpcExecutor executor;
    Func<string, System.IO.Pipes.NamedPipeServerStream>? pipeFactory = null;
    if (args[0] == "--execute-os")
    {
        if (!config.EnableOperatingSystemRules) throw new InvalidOperationException("OS operations are disabled in this host config.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OS mode requires Windows.");
        pipeFactory = new WindowsFirewallPipeFactory(config.ClientUserSid ?? "").Create;
        var store = new FileFirewallStateStore(config.StateDirectory);
        persistent = new WindowsFirewallExecutor(TimeProvider.System, config.Firewall, store,
            new WindowsComFirewallBackend(config.Firewall, enableOsOperations: true));
        if (!persistent.RecoverAndReconcile())
        {
            // Normal reconciliation already removes expired rules; only failed recovery needs this independent sweep.
            persistent.RemoveExpiredConfiguredBlocks();
            throw new InvalidOperationException("Initial firewall state recovery failed: " + persistent.LastFault);
        }
        executor = persistent;
    }
    else executor = new InMemoryFirewallExecutor(TimeProvider.System, config.Firewall.MaximumEvents, config.Firewall.MaximumTargets);
    using var server = new FirewallIpcServer(config.PipeName, key, TimeProvider.System, executor, TimeSpan.FromSeconds(1), pipeFactory);
    // One bounded stdin shutdown reader; no per-request Task.Run or child-process creation.
    _ = Task.Run(() => { Console.In.ReadLine(); cancellation.Cancel(); });
    Console.WriteLine(JsonSerializer.Serialize(new { ready = true, osMode = persistent is not null, pid = Environment.ProcessId }));
    long lastMaintenance = TimeProvider.System.GetTimestamp();
    bool? lastHealth = persistent?.IsHealthy;
    long accepted = 0, rejected = 0;
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            if (persistent is not null && TimeProvider.System.GetElapsedTime(lastMaintenance) >= TimeSpan.FromSeconds(1))
            {
                bool healthy = persistent.RecoverAndReconcile(); lastMaintenance = TimeProvider.System.GetTimestamp();
                // Expired own rules can be removed even while the desired-state file is unreadable.
                if (!healthy) persistent.RemoveExpiredConfiguredBlocks();
                if (lastHealth != healthy)
                    Console.WriteLine(JsonSerializer.Serialize(new { healthChanged = true, healthy, fault = persistent.LastFault }));
                lastHealth = healthy;
            }
            var response = await server.ServeOnceAsync(cancellation.Token);
            // No per-request source/event/secret logging; only bounded process counters.
            if (response.Accepted) { if (accepted < long.MaxValue) accepted++; }
            else if (response.Reason != "ipc-timeout" && rejected < long.MaxValue) rejected++;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
    }
    bool released = persistent?.ReleaseAllConfiguredBlocks() ?? true;
    cleanupAttempted = true;
    Console.WriteLine(JsonSerializer.Serialize(new { stopped = true, accepted, rejected, ownedBlocksReleased = released }));
    return released ? 0 : 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { failed = true, errorType = exception.GetType().Name }));
    return 3;
}
finally
{
    if (!cleanupAttempted && persistent is { IsHealthy: true })
    {
        try { persistent.ReleaseAllConfiguredBlocks(); }
        catch { /* Durable targets remain for explicit startup reconciliation; never erase unknown state. */ }
    }
    persistent?.Dispose();
    if (key is not null) CryptographicOperations.ZeroMemory(key);
}

internal sealed record AgentConfiguration(string PipeName, string StateDirectory,
    bool EnableOperatingSystemRules, RestrictedFirewallOptions Firewall, string? ClientUserSid = null);
