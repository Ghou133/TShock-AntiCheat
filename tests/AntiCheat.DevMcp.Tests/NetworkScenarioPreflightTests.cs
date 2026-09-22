using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture]
public sealed class NetworkScenarioPreflightTests
{
    [TestCase(new[] { "-GameplaySlice", "SolarTablet" }, "GameplaySlice")]
    [TestCase(new[] { "-ProtectionCases", "-ProtectionSlice", "NotARealSlice" }, "NotARealSlice")]
    [TestCase(new[] { "-ProtectionSlice", "SolarTablet" }, "ProtectionSlice requires ProtectionCases")]
    [TestCase(new[] { "-ProductionIdentity", "-ProtectionCases" }, "Conflicting scenarios")]
    [TestCase(new[] { "-InventoryCases", "-HandshakeCases" }, "Conflicting scenarios")]
    public async Task InvalidSelectionFailsBeforeRuntimeAndDirectoryCreation(string[] arguments, string error)
    {
        var result = await RunScript(arguments, null);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Not.Zero);
            Assert.That(result.Output, Does.Contain(error));
            Assert.That(result.Output, Does.Not.Contain("Unverified candidate"));
        });
    }

    [Test]
    public async Task RegisteredRequestCannotFallBackToCommonRegression()
    {
        var result = await RunScript(["-NoBuild"], "protection-cases:solartablet");
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("does not match selected scenario 'regression'"));
    }

    [Test]
    public void EveryRegisteredTcpScenarioHasAnExactSelection()
    {
        Type catalog = typeof(ScenarioId).Assembly.GetType("AntiCheat.DevMcp.ScenarioCatalog", true)!;
        var scenarios = ((Array)catalog.GetField("All", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!).Cast<object>();
        MethodInfo selection = catalog.GetMethod("NetworkSelection", BindingFlags.NonPublic | BindingFlags.Static)!;
        var selections = scenarios.Where(s => (bool)s.GetType().GetProperty("IsTcp")!.GetValue(s)!)
            .Select(s => (string)selection.Invoke(null, [s])!).ToArray();
        Assert.That(selections, Is.Unique);
        Assert.That(selections, Does.Contain("protection-cases:solartablet"));
        Assert.That(selections, Does.Contain("production-identity"));
    }

    private static async Task<(int ExitCode, string Output)> RunScript(string[] arguments, string? expected)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AntiCheat.sln"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "Test output must remain under this project.");
        string root = directory!.FullName;
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        foreach (string arg in new[] { "-NoProfile", "-File", Path.Combine(root, "scripts/Test-NetworkLab.ps1") }.Concat(arguments))
            start.ArgumentList.Add(arg);
        // If preflight regresses, an invalid runtime prevents any actual server launch.
        start.ArgumentList.Add("-TargetRoot");
        start.ArgumentList.Add(Path.Combine(root, "artifacts/m12-tools/never-a-runtime"));
        start.Environment.Remove("ANTICHEAT_DEV_JOB_DIR");
        start.Environment.Remove("ANTICHEAT_DEV_EXPECTED_SCENARIO");
        if (expected is not null) start.Environment["ANTICHEAT_DEV_EXPECTED_SCENARIO"] = expected;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
