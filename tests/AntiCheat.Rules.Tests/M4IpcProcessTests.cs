using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

// The test assembly is executable only to host the existing IPC classes in a real child.
// This adds no wire codec, OS firewall implementation, production service or account store.
public static class M4IpcProcessEntry
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--m4-ipc-host") return 2;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[]? key = null;
        try
        {
            // The secret is sent through this owned child's redirected stdin, never arguments,
            // environment, evidence files or public pipe messages. The host logs responses only.
            string? encoded = await Console.In.ReadLineAsync(lifetime.Token);
            if (encoded is null || encoded.Length != 44) return 3;
            key = Convert.FromBase64String(encoded);
            using var server = new FirewallIpcServer(args[1], key, TimeProvider.System,
                new InMemoryFirewallExecutor(TimeProvider.System, maximumEvents: 16, maximumTargets: 4));
            // Console's synchronized Windows reader can block before returning its async task.
            // Exactly one shutdown reader is scheduled for this bounded, owned test process.
            Task<string?> stop = Task.Run(() => Console.In.ReadLine(), lifetime.Token);
            for (int sequence = 0; sequence < 32; sequence++)
            {
                Task<FirewallIpcResponse> serving = server.ServeOnceAsync(lifetime.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "ready", sequence, pid = Environment.ProcessId }));
                if (await Task.WhenAny(serving, stop) == stop)
                {
                    lifetime.Cancel();
                    try { await serving; } catch (OperationCanceledException) { }
                    return await stop == "stop" ? 0 : 4;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "result", sequence, response = await serving }));
            }
            return 5;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OperationCanceledException)
        {
            Console.Error.WriteLine(error.GetType().Name);
            return 6;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}

[TestFixture, NonParallelizable]
public sealed class M4IpcProcessTests
{
    [Test]
    public async Task WindowsChildKillBreaksPartialRequestAndSameEndpointRestartsWithoutClaimingPersistence()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("This scenario records the actual Windows named-pipe process boundary.");
        string root = FindRepository();
        string report = Path.Combine(root, "artifacts", "test-runs", "m4-ipc-process", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(report);
        await File.WriteAllTextAsync(Path.Combine(report, ".anticheat-ipc-lab"), "Isolated local named-pipe test only. No OS firewall actions.\n");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string pipeName = "anticheat-m4-" + Guid.NewGuid().ToString("N");
        var hosts = new List<Host>(2);
        var checks = new List<object>(24);
        var timer = Stopwatch.StartNew();
        string status = "failed", failure = "";
        try
        {
            var first = await Host.Start(pipeName, key, report, "first");
            hosts.Add(first);
            Check(first.Pid != Environment.ProcessId && !first.Process.HasExited, "executor-is-a-distinct-live-windows-process");

            var normal = await Send(first, Command("127.0.0.1"));
            Check(normal.Accepted && normal.DryRun && normal.RecordedEvents == 1 && normal.SimulatedTargets == 0,
                "normal-loopback-source-recorded-without-any-simulated-target");
            var sharedSource = Command("203.0.113.8");
            var shared = await Send(first, sharedSource with { Operation = FirewallIpcOperation.SimulateTemporaryBlock });
            Check(!shared.Accepted && shared.Reason == "source-not-eligible-for-simulation",
                "signed-shared-source-cannot-request-simulated-exclusion");

            var temporary = Command("203.0.113.8", simulate: true);
            var created = await Send(first, temporary);
            Check(created.Accepted && created.SimulatedTargets == 1 && created.RecordedEvents == 2,
                "eligible-test-address-is-only-an-in-memory-simulated-target");
            await Task.Delay(1200);
            var duplicate = await Send(first, temporary with { RequestId = Guid.NewGuid() });
            Check(duplicate.Accepted && duplicate.Reason == "duplicate-event" && duplicate.RecordedEvents == 2 && duplicate.SimulatedTargets == 0,
                "same-process-duplicate-after-real-ttl-does-not-extend-simulation");

            byte[] wrong = RandomNumberGenerator.GetBytes(32);
            try
            {
                int count = first.Results.Count;
                Assert.ThrowsAsync<InvalidDataException>(async () => await FirewallIpcClient.SendAsync(pipeName, wrong,
                    Command("127.0.0.1"), TimeProvider.System));
                await first.WaitForResults(count + 1);
                Check(first.Results.Last().Reason == "ipc-authentication-failed", "wrong-key-request-and-response-rejected-across-processes");
            }
            finally { CryptographicOperations.ZeroMemory(wrong); }
            var stillAlive = await Send(first, Command("127.0.0.1"));
            Check(stillAlive.Accepted && stillAlive.RecordedEvents == 3 && stillAlive.SimulatedTargets == 0,
                "same-host-accepts-normal-request-after-authentication-failure");

            await using (var partial = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await partial.ConnectAsync(deadline.Token);
                byte[] incomplete = new byte[5];
                BinaryPrimitives.WriteInt32LittleEndian(incomplete, 100);
                incomplete[4] = (byte)'{';
                await partial.WriteAsync(incomplete, deadline.Token);
                await partial.FlushAsync(deadline.Token);
                int completedBefore = first.Results.Count;
                Task<int> pendingRead = partial.ReadAsync(new byte[4], deadline.Token).AsTask();
                Check(!pendingRead.IsCompleted && !first.Process.HasExited, "incomplete-frame-is-pending-before-process-kill");
                await first.Kill();
                Check(first.ExitCode is not 0 && first.ForcedKill, "actual-owned-ipc-host-force-kill-completed");
                bool disconnected = false;
                try { disconnected = await pendingRead == 0; }
                catch (IOException) { disconnected = true; }
                Check(disconnected && !deadline.IsCancellationRequested, "pending-pipe-read-observes-real-disconnect-within-deadline");
                Check(first.Results.Count == completedBefore, "incomplete-request-has-no-executor-acceptance-receipt");
            }
            var missingTimer = Stopwatch.StartNew();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await FirewallIpcClient.SendAsync(pipeName, key,
                Command("127.0.0.1"), TimeProvider.System, timeout: TimeSpan.FromMilliseconds(200)));
            Check(missingTimer.Elapsed < TimeSpan.FromSeconds(3), "typed-client-missing-host-fails-with-bounded-timeout");

            var restarted = await Host.Start(pipeName, key, report, "restart");
            hosts.Add(restarted);
            Check(restarted.Pid != first.Pid && !restarted.Process.HasExited, "same-local-pipe-name-restarts-in-a-new-process");
            var afterRestart = await Send(restarted, Command("127.0.0.1"));
            Check(afterRestart.Accepted && afterRestart.RecordedEvents == 1 && afterRestart.SimulatedTargets == 0,
                "normal-record-after-restart-exposes-empty-memory-state");
            // Source evidence remains fresh (<30s). Reusing an event ID across a dead memory-only
            // executor is NOT durable idempotency; this test explicitly records that boundary.
            var replayed = await Send(restarted, temporary with { RequestId = Guid.NewGuid() });
            Check(replayed.Accepted && replayed.Reason == "simulated-temporary-block" && replayed.SimulatedTargets == 1 && replayed.RecordedEvents == 2,
                "memory-only-executor-does-not-claim-idempotency-across-process-restart");
            var repeated = await Send(restarted, temporary with { RequestId = Guid.NewGuid() });
            Check(repeated.Accepted && repeated.Reason == "duplicate-event" && repeated.RecordedEvents == 2,
                "restarted-host-deduplicates-within-its-own-lifetime");
            await restarted.Stop();
            Check(restarted.ExitCode == 0 && !restarted.ForcedKill, "restarted-helper-shuts-down-cleanly-with-no-surviving-child");
            status = "passed";

            async Task<FirewallIpcResponse> Send(Host host, FirewallIpcCommand command)
            {
                int count = host.Results.Count;
                var response = await FirewallIpcClient.SendAsync(pipeName, key, command, TimeProvider.System);
                await host.WaitForResults(count + 1);
                Assert.That(host.Results.Last(), Is.EqualTo(response), "Authenticated response must match separate executor receipt.");
                return response;
            }
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; throw; }
        finally
        {
            foreach (var host in hosts) await host.DisposeAsync();
            CryptographicOperations.ZeroMemory(key);
            await File.WriteAllTextAsync(Path.Combine(report, "scenario.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, checks, elapsedMs = timer.ElapsedMilliseconds,
                operatingSystem = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                actualNamedPipeTransport = true, actualOwnedChildKill = hosts.Any(x => x.ForcedKill),
                osFirewallExercised = false, simulatedExecutorPersistsAcrossRestart = false, accountPunishmentsExercised = false,
                gameTcpLoadCalibrated = false, sharedKeyWrittenToEvidence = false,
                rulesSha256 = Hash(typeof(FirewallIpcServer).Assembly.Location),
                coreSha256 = Hash(typeof(FirewallDryRun).Assembly.Location),
                testAssemblySha256 = Hash(typeof(M4IpcProcessEntry).Assembly.Location),
                hosts = hosts.Select(x => new { x.Pid, x.ExitCode, x.ForcedKill, x.ReadyCount, responses = x.Results.ToArray(), x.StandardError })
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Out.WriteLine("M4 IPC process evidence: " + report);
        }

        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed, elapsedMs = timer.ElapsedMilliseconds });
            Assert.That(passed, Is.True, name);
        }
    }

    private static FirewallIpcCommand Command(string address, bool simulate = false)
    {
        var clock = TimeProvider.System;
        var source = new FirewallDryRun(clock).Create(IPAddress.Parse(address),
            new(clock.GetUtcNow(), clock.GetUtcNow(), 1, 100, 4), NetworkAbuseReason.WeightedPacketCost,
            TimeSpan.FromSeconds(1), exclusiveSourceVerified: simulate);
        return new(Guid.NewGuid(), simulate ? FirewallIpcOperation.SimulateTemporaryBlock : FirewallIpcOperation.RecordEvent, source);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "START_HERE.md")) && File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;
        throw new InvalidOperationException("Run this fixture from its repository test build.");
    }

    private sealed class Host : IAsyncDisposable
    {
        public Process Process { get; }
        public int Pid { get; }
        public int? ExitCode { get; private set; }
        public bool ForcedKill { get; private set; }
        public int ReadyCount => Volatile.Read(ref readyCount);
        public ConcurrentQueue<FirewallIpcResponse> Results { get; } = new();
        public string StandardError { get; private set; } = "";
        private readonly Task capture;
        private readonly Task captureErrors;
        private int readyCount;
        private bool disposed;

        private Host(Process process, string report, string phase)
        {
            Process = process; Pid = process.Id;
            capture = Capture(); captureErrors = CaptureErrors();
            async Task Capture()
            {
                using var log = new StreamWriter(Path.Combine(report, phase + "-stdout.jsonl"));
                int count = 0;
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (++count > 70 || line.Length > 4096) throw new InvalidDataException("IPC helper output exceeded fixture budget.");
                    await log.WriteLineAsync(line);
                    using var record = JsonDocument.Parse(line);
                    if (record.RootElement.GetProperty("kind").GetString() == "ready") Interlocked.Increment(ref readyCount);
                    else Results.Enqueue(record.RootElement.GetProperty("response").Deserialize<FirewallIpcResponse>()!);
                }
            }
            async Task CaptureErrors()
            {
                char[] buffer = new char[4096];
                int count = await process.StandardError.ReadBlockAsync(buffer);
                StandardError = new string(buffer, 0, count);
                await File.WriteAllTextAsync(Path.Combine(report, phase + "-stderr.log"), StandardError);
            }
        }

        public static async Task<Host> Start(string pipeName, byte[] key, string report, string phase)
        {
            string assembly = typeof(M4IpcProcessEntry).Assembly.Location;
            string exe = Path.ChangeExtension(assembly, ".exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("The existing NUnit assembly must be built with its child-host entry point.", exe);
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(assembly)!
            };
            start.ArgumentList.Add("--m4-ipc-host"); start.ArgumentList.Add(pipeName);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned IPC host.");
            var host = new Host(process, report, phase);
            try
            {
                await process.StandardInput.WriteLineAsync(Convert.ToBase64String(key));
                await process.StandardInput.FlushAsync();
                await host.Until(() => host.ReadyCount > 0);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public Task WaitForResults(int count) => Until(() => Results.Count >= count);
        private async Task Until(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (capture.IsFaulted) await capture;
                if (Process.HasExited) throw new IOException("Owned IPC helper exited before checkpoint: " + Process.ExitCode);
                if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned IPC helper checkpoint.");
                await Task.Delay(10);
            }
        }
        public async Task Kill()
        {
            if (Process.HasExited) throw new InvalidOperationException("IPC helper exited before the forced-kill checkpoint.");
            Process.Kill(entireProcessTree: true); ForcedKill = true;
            await Complete();
        }
        public async Task Stop()
        {
            if (!Process.HasExited)
            {
                await Process.StandardInput.WriteLineAsync("stop");
                await Process.StandardInput.FlushAsync();
            }
            await Complete();
        }
        private async Task Complete()
        {
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            ExitCode = Process.ExitCode;
            await Task.WhenAll(capture, captureErrors).WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            try
            {
                try { await Stop(); }
                finally { if (!Process.HasExited) await Kill(); }
            }
            finally { Process.Dispose(); disposed = true; }
        }
    }
}
