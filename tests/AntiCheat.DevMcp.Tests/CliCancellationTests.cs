using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using AntiCheat.DevMcp;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

// Executes the real foreground control flow through narrow in-memory operations. These tests
// establish cancellation/owner-exit ordering; native process and durable job tests are separate.
[TestFixture]
public sealed class CliCancellationTests
{
    private const string JobId = "1234567890abcdef1234567890abcdef";

    [Test]
    public async Task CtrlCOwnerCannotExitBeforeTheMonitorPublishesItsCanceledTerminalResult()
    {
        using var cancellation = new CancellationTokenSource();
        var statusRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<DevReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<DevReply>();
        int cancels = 0, reads = 0;
        bool ownerDisposed = false;
        async Task<int> OwnedRun()
        {
            try
            {
                return await Run(() => Task.FromResult(Started()), id =>
                {
                    Assert.That(id, Is.EqualTo(JobId));
                    Interlocked.Increment(ref reads); statusRequested.TrySetResult(); return terminal.Task;
                }, (id, reason) =>
                {
                    Assert.That(id, Is.EqualTo(JobId)); Assert.That(reason, Does.Contain("Ctrl-C"));
                    Interlocked.Increment(ref cancels); return Task.FromResult(Status("running"));
                }, reply =>
                {
                    writes.Add(reply);
                    if (writes.Count == 1) cancellation.Cancel();
                    return Task.CompletedTask;
                }, cancellation.Token);
            }
            finally { ownerDisposed = true; }
        }

        var running = OwnedRun();
        try
        {
            await statusRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(cancels, Is.EqualTo(1));
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(running.IsCompleted, Is.False, "Ctrl-C is a request, not a completed monitor result.");
            Assert.That(ownerDisposed, Is.False, "Program must not enter disposal while cancellation is still being classified.");
            Assert.That(writes, Has.Count.EqualTo(1), "Only the original start has been published before terminal observation.");
            terminal.SetResult(Status("canceled"));
            Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(130));
            Assert.That(ownerDisposed, Is.True);
            Assert.That(writes.Last().Data["status"]!.GetValue<string>(), Is.EqualTo("canceled"));
            Assert.That(cancels, Is.EqualTo(1));
        }
        finally { terminal.TrySetResult(Status("canceled")); await running.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [TestCase("failed", 1)]
    [TestCase("passed", 0)]
    [TestCase("blocked", 1)]
    [TestCase("interrupted", 1)]
    public async Task ARealTerminalOutcomeCoincidentWithCtrlCIsNeverRelabeledAsCanceled(string state, int exitCode)
    {
        using var cancellation = new CancellationTokenSource();
        var writes = new List<DevReply>();
        var original = Status(state);
        original.Data["first_failed_assertion"] = state == "failed" ? "original fixture assertion" : null;
        int cancels = 0;
        int result = await Run(() => Task.FromResult(Started()), _ => Task.FromResult(original),
            (_, _) => { cancels++; return Task.FromResult(Status("running")); }, reply =>
            {
                writes.Add(reply); if (writes.Count == 1) cancellation.Cancel(); return Task.CompletedTask;
            }, cancellation.Token);
        Assert.That(result, Is.EqualTo(exitCode));
        Assert.That(writes.Last(), Is.SameAs(original));
        Assert.That(cancels, Is.EqualTo(1));
        if (state == "failed") Assert.That(writes.Last().Data["first_failed_assertion"]!.GetValue<string>(), Is.EqualTo("original fixture assertion"));
    }

    [Test]
    public async Task AnUncompletedStatusReadHasABoundedWaitAndDoesNotClaimCancellationCompleted()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<DevReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<DevReply>();
        int cancels = 0, reads = 0;
        var elapsed = Stopwatch.StartNew();
        try
        {
            int result = await Run(() => Task.FromResult(Started()), _ => { reads++; return pending.Task; },
                (_, _) => { cancels++; return Task.FromResult(Status("running")); }, reply =>
                {
                    writes.Add(reply); if (writes.Count == 1) cancellation.Cancel(); return Task.CompletedTask;
                }, cancellation.Token, TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(result, Is.EqualTo(2));
            Assert.That(cancels, Is.EqualTo(1)); Assert.That(reads, Is.EqualTo(1));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(writes.Last().Data["code"]!.GetValue<string>(), Is.EqualTo("cancellation_wait_timeout"));
            Assert.That(writes.Last().Error, Does.Contain("no successful cancellation is claimed"));
        }
        finally { pending.TrySetResult(Status("running")); }
    }

    [Test]
    public void OrdinaryOutputFailureDoesNotManufactureAnExplicitPublicCancelRequest()
    {
        int cancels = 0, reads = 0;
        var original = new IOException("fixture broken output pipe");
        var thrown = Assert.ThrowsAsync<IOException>(async () => await Run(
            () => Task.FromResult(Started()), _ => { reads++; return Task.FromResult(Status("running")); },
            (_, _) => { cancels++; return Task.FromResult(Status("running")); },
            _ => Task.FromException(original), CancellationToken.None));
        Assert.That(thrown, Is.SameAs(original));
        Assert.That(cancels, Is.Zero, "Program disposal records owner interruption for an output failure.");
        Assert.That(reads, Is.Zero);
    }

    [Test]
    public void BrokenTerminalOutputAfterCtrlCStillOccursOnlyAfterTerminalObservation()
    {
        using var cancellation = new CancellationTokenSource();
        int writes = 0, cancels = 0, reads = 0;
        bool observed = false;
        var original = new IOException("fixture terminal output pipe closed");
        var thrown = Assert.ThrowsAsync<IOException>(async () => await Run(() => Task.FromResult(Started()), _ =>
        {
            reads++; observed = true; return Task.FromResult(Status("canceled"));
        }, (_, _) => { cancels++; return Task.FromResult(Status("running")); }, _ =>
        {
            if (++writes == 1) { cancellation.Cancel(); return Task.CompletedTask; }
            Assert.That(observed, Is.True);
            return Task.FromException(original);
        }, cancellation.Token));
        Assert.That(thrown, Is.SameAs(original));
        Assert.That(reads, Is.EqualTo(1)); Assert.That(cancels, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationOrStatusErrorsAreReturnedWithoutRetryOrInventingATerminalOutcome(bool cancelFails)
    {
        using var cancellation = new CancellationTokenSource();
        var original = new DevReply("error", new JsonObject { ["code"] = "fixture_unavailable" }, "fixture operation unavailable");
        var writes = new List<DevReply>();
        int cancels = 0, reads = 0;
        int result = await Run(() => Task.FromResult(Started()), _ => { reads++; return Task.FromResult(original); },
            (_, _) => { cancels++; return Task.FromResult(cancelFails ? original : Status("running")); }, reply =>
            {
                writes.Add(reply); if (writes.Count == 1) cancellation.Cancel(); return Task.CompletedTask;
            }, cancellation.Token);
        Assert.That(result, Is.EqualTo(2)); Assert.That(writes.Last(), Is.SameAs(original));
        Assert.That(cancels, Is.EqualTo(1)); Assert.That(reads, Is.EqualTo(cancelFails ? 0 : 1));
    }

    private static DevReply Started() => new("started", new JsonObject { ["job_id"] = JobId, ["status"] = "running" });
    private static DevReply Status(string state) => new("ok", new JsonObject { ["job_id"] = JobId, ["status"] = state });

    private static Task<int> Run(Func<Task<DevReply>> start, Func<string, Task<DevReply>> status,
        Func<string, string, Task<DevReply>> cancel, Func<DevReply, Task> write, CancellationToken cancellation,
        TimeSpan? limit = null)
    {
        var cli = typeof(DevOperations).Assembly.GetType("AntiCheat.DevMcp.Cli", throwOnError: true)!;
        var method = cli.GetMethod("RunForegroundAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task<int>)method.Invoke(null, [start, status, cancel, write, cancellation, limit ?? TimeSpan.FromSeconds(2)])!;
    }
}
