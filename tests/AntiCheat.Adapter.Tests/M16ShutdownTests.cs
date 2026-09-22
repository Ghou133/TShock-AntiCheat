using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Persistence;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M16ShutdownTests
{
    private const string Fingerprint = "m16-shutdown-owned-fixture";
    private static readonly RulePolicy Policy = new(Fingerprint, RuleQualification.TestLab,
        "tests/M16ShutdownTests-only", [5], "1");
    private string directory = null!;

    [SetUp]
    public void Setup() => directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests",
        "m16-shutdown-" + Guid.NewGuid().ToString("N"));

    [TestCase(true, M16ShutdownWaitOutcome.Clean)]
    [TestCase(false, M16ShutdownWaitOutcome.Unclean)]
    public void BarrierPreservesTheEngineResult(bool result, M16ShutdownWaitOutcome outcome)
    {
        var observed = M16ShutdownBarrier.WaitForExit(Task.FromResult(result), TimeSpan.Zero);
        Assert.That(observed.Outcome, Is.EqualTo(outcome));
        Assert.That(observed.CleanConfirmed, Is.EqualTo(result));
    }

    [Test]
    public void TerminalFaultCancellationAndInvalidBudgetsNeverBecomeClean()
    {
        var fault = M16ShutdownBarrier.WaitForExit(Task.FromException<bool>(new IOException("owned-write-fault")));
        Assert.That(fault.Outcome, Is.EqualTo(M16ShutdownWaitOutcome.Faulted));
        Assert.That(fault.FailureType, Is.EqualTo(nameof(IOException)));
        var canceled = M16ShutdownBarrier.WaitForExit(Task.FromCanceled<bool>(new CancellationToken(true)));
        Assert.That(canceled.Outcome, Is.EqualTo(M16ShutdownWaitOutcome.Canceled));
        Assert.Throws<ArgumentOutOfRangeException>(() => M16ShutdownBarrier.WaitForExit(Task.FromResult(true), Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => M16ShutdownBarrier.WaitForExit(Task.FromResult(true), TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void ActualTsapiDisposeWaitsForItsExistingOperationAndDurableRenameWithoutCallerAwait()
    {
        var plugin = new AntiCheatPlugin(null!);
        var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new NoWrites());
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Bind(plugin, engine, journal, release.Task);
        var originalCompletion = plugin.ShutdownCompletion;
        var container = new PluginContainer(plugin);
        using var returned = new ManualResetEventSlim();
        Exception? disposeError = null;
        var disposeThread = new Thread(() =>
        {
            // Matches the audited TSAPI synchronous host, which installs no SynchronizationContext.
            SynchronizationContext.SetSynchronizationContext(null);
            try { container.Dispose(); }
            catch (Exception error) { disposeError = error; }
            finally { returned.Set(); }
        }) { IsBackground = true };
        disposeThread.Start();
        try
        {
            Assert.That(SpinWait.SpinUntil(() => !ReferenceEquals(plugin.ShutdownCompletion, originalCompletion),
                TimeSpan.FromSeconds(3)), Is.True);
            Assert.That(returned.Wait(TimeSpan.FromMilliseconds(150)), Is.False,
                "The real synchronous plugin-container boundary must not abandon its asynchronous shutdown task.");
            Assert.That(Clean(), Is.False);
            release.SetResult();
            Assert.That(returned.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(disposeError, Is.Null);
            Assert.That(plugin.ShutdownCompletion.IsCompletedSuccessfully, Is.True);
            Assert.That(plugin.ShutdownCompletion.Result, Is.True);
            Assert.That(Clean(), Is.True, "Inspect the authoritative renamed file before any caller await.");
            Assert.That(File.Exists(Path.Combine(directory, "run-safety.next")), Is.False);
            using var reopened = new FileEnforcementJournal(directory);
            Assert.That(reopened.ReadAsync().AsTask().GetAwaiter().GetResult(), Is.Empty);
        }
        finally
        {
            release.TrySetResult(); disposeThread.Join(TimeSpan.FromSeconds(6));
            plugin.ShutdownCompletion.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public void DisposeCancelsQueuedServerWritesAndPreservesDurablePendingIntentForReplay()
    {
        var plugin = new AntiCheatPlugin(null!);
        var dispatcher = Get<ServerThreadDispatcher>(plugin, "_dispatcher");
        var journal = new FileEnforcementJournal(directory);
        var bans = new DispatchedBans(dispatcher);
        var engine = Engine(journal, bans);
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        Prove(engine);
        var operation = engine.PumpAsync(1, Get<CancellationTokenSource>(plugin, "_shutdown").Token).AsTask();
        Bind(plugin, engine, journal, operation);
        try
        {
            Assert.That(SpinWait.SpinUntil(() => dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)), Is.True);
            new PluginContainer(plugin).Dispose();
            Assert.That(plugin.ShutdownCompletion.IsCompletedSuccessfully, Is.True);
            Assert.That(plugin.ShutdownCompletion.Result, Is.True);
            Assert.That(Clean(), Is.True);
            Assert.That(dispatcher.PendingCount, Is.Zero);
            Assert.That(bans.ServerWrites, Is.Zero, "No GameUpdate or queued game action may be pumped during Dispose.");
            using var reopened = new FileEnforcementJournal(directory);
            var pending = reopened.ReadAsync().AsTask().GetAwaiter().GetResult();
            Assert.That(pending, Has.Count.EqualTo(1), "Durable pending bans remain replayable; clean does not mean Applied.");
            Assert.That(pending[0].AccountId, Is.EqualTo(971));
        }
        finally { plugin.Dispose(); plugin.ShutdownCompletion.Wait(TimeSpan.FromSeconds(5)); }
    }

    [Test]
    public void MemoryOnlyProvenIntentPreventsCleanEvenAfterDisposeReturns()
    {
        var plugin = new AntiCheatPlugin(null!);
        var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new NoWrites());
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        Prove(engine);
        Bind(plugin, engine, journal, Task.CompletedTask);
        new PluginContainer(plugin).Dispose();
        Assert.That(plugin.ShutdownCompletion.IsCompletedSuccessfully, Is.True);
        Assert.That(plugin.ShutdownCompletion.Result, Is.False);
        Assert.That(Clean(), Is.False);
        Assert.That(Directory.EnumerateFiles(directory, "*.json"), Is.Empty);
        using var reopened = new FileEnforcementJournal(directory);
        Assert.That(Engine(reopened, new NoWrites()).RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.False,
            "The finite wait cannot erase a lost in-memory-proof window.");
    }

    [Test]
    public void RecoveryFaultRemainsUnclean()
    {
        using (var first = new FileEnforcementJournal(directory))
            Assert.That(Engine(first, new NoWrites()).RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        var plugin = new AntiCheatPlugin(null!);
        var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new NoWrites());
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.False);
        Assert.That(engine.MaintenanceReason, Is.EqualTo("previous-enforcement-run-unclean"));
        Bind(plugin, engine, journal, Task.CompletedTask);
        new PluginContainer(plugin).Dispose();
        Assert.That(plugin.ShutdownCompletion.IsCompletedSuccessfully, Is.True);
        Assert.That(plugin.ShutdownCompletion.Result, Is.False);
        Assert.That(Clean(), Is.False);
    }

    [Test]
    public void MarkerIoFailureIsNotReplacedWithASuccessfulTaskResult()
    {
        var plugin = new AntiCheatPlugin(null!);
        var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new NoWrites());
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        Bind(plugin, engine, journal, Task.CompletedTask);
        using (var locked = new FileStream(Path.Combine(directory, "run-safety.state"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            new PluginContainer(plugin).Dispose();
            Assert.That(plugin.ShutdownCompletion.IsCompletedSuccessfully, Is.True);
            Assert.That(plugin.ShutdownCompletion.Result, Is.False);
        }
        Assert.That(Clean(), Is.False);
    }

    [Test]
    public void ActualDisposeTimeoutReturnsUnconfirmedWithoutClosingJournalOrInventingClean()
    {
        var plugin = new AntiCheatPlugin(null!);
        var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new NoWrites());
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Bind(plugin, engine, journal, release.Task);
        try
        {
            var watch = Stopwatch.StartNew();
            new PluginContainer(plugin).Dispose();
            watch.Stop();
            Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(M16ShutdownBarrier.DefaultBudget - TimeSpan.FromMilliseconds(100)));
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
            Assert.That(plugin.ShutdownCompletion.IsCompleted, Is.False);
            Assert.That(Clean(), Is.False);
            Assert.Throws<IOException>(() => { using var conflicting = new FileEnforcementJournal(directory); });
            Assert.That(M16ShutdownBarrier.WaitForExit(plugin.ShutdownCompletion, TimeSpan.Zero).Outcome,
                Is.EqualTo(M16ShutdownWaitOutcome.TimedOut));
        }
        finally
        {
            // If a host remains alive, later actual durable completion may truthfully become clean.
            release.TrySetResult();
            Assert.That(plugin.ShutdownCompletion.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }
        Assert.That(plugin.ShutdownCompletion.Result, Is.True);
        Assert.That(Clean(), Is.True);
    }

    private bool Clean()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "run-safety.state")));
        return document.RootElement.GetProperty("State").GetProperty("Clean").GetBoolean();
    }

    private static AntiCheatEngine Engine(FileEnforcementJournal journal, IAccountBanStore bans) =>
        new(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, Policy, journal, bans);

    private static void Prove(AntiCheatEngine engine)
    {
        var session = engine.OpenSession(4)!.Value;
        Assert.That(engine.Authenticate(session, 971), Is.EqualTo(AuthenticationResult.Authenticated));
        var result = engine.Observe(new(session, 5, 5, new(Fingerprint, "1", true, true, true, true)));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    private static void Bind(AntiCheatPlugin plugin, AntiCheatEngine engine, FileEnforcementJournal journal, Task operation)
    {
        Set(plugin, "_engine", engine); Set(plugin, "_journal", journal); Set(plugin, "_operation", operation);
    }
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class NoWrites : IAccountBanStore
    {
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("No account database writes belong to this test."));
    }

    private sealed class DispatchedBans(ServerThreadDispatcher dispatcher) : IAccountBanStore
    {
        public int ServerWrites;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) =>
            dispatcher.InvokeAsync(() => ServerWrites++, cancellationToken);
    }
}
