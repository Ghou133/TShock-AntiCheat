using System.Diagnostics;
using System.Text.Json;
using AntiCheat.DevMcp;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

internal sealed class NativeTestDirectory : IDisposable
{
    private readonly List<OwnedProcess> owned = [];
    private readonly List<Process> independent = [];
    private readonly List<OwnedProcessMember> identities = [];
    private readonly Dictionary<string, object?> evidence = [];
    private int launch;
    public string Root { get; }
    public string Token { get; } = Guid.NewGuid().ToString("N");

    public NativeTestDirectory()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) Assert.Ignore("Native Windows 10+ process/path tests were not executed on this platform.");
        Root = Path.Combine(NativeFixtureProgram.ProjectRoot(), ".lab", "devmcp-native-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        System.IO.File.WriteAllText(File(".native-fixture.json"), JsonSerializer.Serialize(new { token = Token }));
        evidence["test"] = TestContext.CurrentContext.Test.FullName;
        evidence["utc"] = DateTimeOffset.UtcNow;
        evidence["directory"] = Root;
    }

    public string File(string name) => Path.Combine(Root, name);

    public OwnedProcess Start(params string[] arguments)
    {
        int number = ++launch;
        var process = OwnedProcess.Start(NativeFixtureProgram.Executable,
            new[] { NativeFixtureProgram.EntryArgument }.Concat(arguments).ToArray(), Root,
            new Dictionary<string, string> { [NativeFixtureProgram.TokenVariable] = Token },
            File($"stdout-{number}.log"), File($"stderr-{number}.log"));
        owned.Add(process);
        identities.Add(new(process.Pid, process.StartedUtc, process.ExecutablePath));
        Record("launch-" + number, new { process.Pid, process.StartedUtc, process.ExecutablePath, process.JobName, arguments });
        return process;
    }

    public Process StartSentinel()
    {
        var info = new ProcessStartInfo(NativeFixtureProgram.Executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root };
        foreach (string arg in new[] { NativeFixtureProgram.EntryArgument, "hold", Root, "sentinel" }) info.ArgumentList.Add(arg);
        info.Environment[NativeFixtureProgram.TokenVariable] = Token;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Sentinel did not start.");
        independent.Add(process);
        Record("sentinel-launch", new { process.Id, startedUtc = process.StartTime.ToUniversalTime(), executable = NativeFixtureProgram.Executable });
        return process;
    }

    public async Task<T> ReadWhenReady<T>(string name)
    {
        await Until(() => System.IO.File.Exists(File(name)), "fixture file " + name);
        using var stream = new FileStream(File(name), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.That(stream.Length, Is.LessThan(16384), "Fixture result must remain bounded.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Missing fixture result: " + name);
    }

    public async Task<OwnedProcessMember> Identity(string name)
    {
        var identity = await ReadWhenReady<OwnedProcessMember>(name + ".json");
        identities.Add(identity);
        Record(name + "-identity", identity);
        return identity;
    }

    public static async Task Until(Func<bool> predicate, string description)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(6)) Assert.Fail("Timed out waiting for " + description);
            await Task.Delay(25);
        }
    }

    public async Task<int> Wait(OwnedProcess process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        int code = await process.WaitForExitAsync(deadline.Token);
        Record("cleanup-" + process.Pid, new { code, process.Cleanup, stdout = process.Stdout, stderr = process.Stderr });
        return code;
    }

    public void Record(string name, object? value)
    {
        evidence[name] = value;
        System.IO.File.WriteAllText(File("evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        // Only exact handles/recorded identities from this fixture are cleaned. Never enumerate
        // processes by image name, and never recursively delete retained test evidence.
        var cleanupErrors = new List<string>();
        foreach (var process in owned)
        {
            try { process.Dispose(); }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); }
        }
        foreach (var process in independent)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            finally { process.Dispose(); }
        }
        foreach (var identity in identities.DistinctBy(x => (x.Pid, x.StartedUtc)))
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == identity.StartedUtc.UtcDateTime &&
                    string.Equals(process.MainModule?.FileName, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                { process.Kill(); process.WaitForExit(3000); }
            }
            catch (ArgumentException) { }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); }
        }
        Record("test-cleanup-errors", cleanupErrors);
        TestContext.AddTestAttachment(File("evidence.json"), "Actual owned Windows process/path evidence");
        Assert.That(cleanupErrors, Is.Empty, "Fixture cleanup itself failed; retained evidence identifies the exact owned processes.");
    }
}
