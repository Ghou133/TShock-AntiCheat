using System.Collections.Concurrent;
using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class EnforcementTests
{
    private sealed class UnguardedJournal(MemoryJournal inner) : IEnforcementJournal
    {
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) => inner.AppendAsync(intent, cancellationToken);
        public ValueTask MarkAppliedAsync(Guid id, CancellationToken cancellationToken = default) => inner.MarkAppliedAsync(id, cancellationToken);
    }

    [Test]
    public async Task StartupCannotAdmitAccountsWhileDurableMarkerIsStillBeingArmed()
    {
        var lab = new Lab();
        var ready = new TaskCompletionSource<RunSafetyStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Journal.OnBeginRun = () => new(ready.Task);
        var recovery = lab.Engine.RecoverAsync().AsTask();
        Assert.That(lab.Engine.IsMaintenanceMode, Is.True);
        Assert.That(lab.Engine.OpenSession(4), Is.Null);
        ready.SetResult(RunSafetyStatus.Ready);
        Assert.That(await recovery, Is.True);
        Assert.That(lab.Engine.OpenSession(4), Is.Not.Null);
    }

    [TestCase(ExecutionScope.TestLab, RuleQualification.TestLab)]
    [TestCase(ExecutionScope.Production, RuleQualification.ProductionQualified)]
    public async Task QualifiedRuleCannotStartWithoutRunSafetyGuard(ExecutionScope scope, RuleQualification qualification)
    {
        var lab = new Lab();
        var engine = new AntiCheatEngine(lab.Clock, new() { Scope = scope }, Lab.Policy with { Qualification = qualification },
            new UnguardedJournal(lab.Journal), lab.Bans);
        Assert.That(await engine.RecoverAsync(), Is.False);
        Assert.That(engine.MaintenanceReason, Is.EqualTo("durable-run-safety-guard-required"));
        Assert.That(engine.OpenSession(4), Is.Null);
    }

    [Test]
    public async Task RepeatedEventCreatesOneIncidentAndOneBanWithoutWarnings()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var first = lab.Engine.Observe(Lab.Violation(key));
        for (var index = 0; index < 100; index++)
        {
            var duplicate = lab.Engine.Observe(Lab.Violation(key));
            Assert.That(duplicate.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(duplicate.Incident!.IncidentId, Is.EqualTo(first.Incident!.IncidentId));
        }
        await lab.Engine.PumpAsync();
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.SanctionCount, Is.EqualTo(1));
        Assert.That(lab.Journal.Appends, Is.EqualTo(1));
        Assert.That(lab.Bans.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentCallbacksRevokeAtomicallyAndProduceOneImmutableIntent()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var results = new ConcurrentBag<ActionDecision>();
        Parallel.For(0, 256, _ => results.Add(lab.Engine.Observe(Lab.Violation(key))));
        await lab.Engine.PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results.Count(x => x.Verdict == Verdict.ProvenCheat), Is.EqualTo(1));
            Assert.That(results.All(x => x.Behavior == ControlAction.Block), Is.True);
            Assert.That(results.Select(x => x.Incident!.IncidentId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(lab.Bans.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SlowBanDoesNotAllowWritesOrConcurrentUnboundedPumps()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Bans.BeforeBan = async _ => { entered.SetResult(); await finish.Task; };
        lab.Engine.Observe(Lab.Violation(key));
        var pumping = lab.Engine.PumpAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.That(lab.Engine.CanWrite(key), Is.False);
            Assert.That((await lab.Engine.PumpAsync()).Attempted, Is.Zero);
            Assert.That(lab.Bans.Calls, Is.EqualTo(1));
        }
        finally { finish.SetResult(); }
        Assert.That((await pumping).Applied, Is.EqualTo(1));
    }

    [Test]
    public async Task StalledPumpDeadlineStillEntersMaintenanceWithoutWaitingForIoCompletion()
    {
        var lab = new Lab(new() { Scope = ExecutionScope.TestLab, PersistenceDeadline = TimeSpan.FromSeconds(5) });
        var key = await lab.Login();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Bans.BeforeBan = async _ => { entered.SetResult(); await finish.Task; };
        lab.Engine.Observe(Lab.Violation(key));
        var pumping = lab.Engine.PumpAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            lab.Clock.Advance(TimeSpan.FromSeconds(5));
            Assert.That(lab.Engine.IsMaintenanceMode, Is.True);
            Assert.That(lab.Engine.OpenSession(5), Is.Null);
            Assert.That(lab.Engine.Observe(Lab.Violation(key)).Verdict, Is.EqualTo(Verdict.Fault));
        }
        finally { finish.SetResult(); }
        Assert.That((await pumping).Applied, Is.EqualTo(1));
        Assert.That(lab.Engine.IsMaintenanceMode, Is.False);
        Assert.That(lab.Engine.CanWrite(key), Is.False);
    }

    [Test]
    public async Task DatabaseFailureRetainsJournalAndBlocksOnlyKnownAccountBeforeDeadline()
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Bans.Fail = true;
        var original = lab.Engine.Observe(Lab.Violation(key)).Incident;
        var failed = await lab.Engine.PumpAsync();
        lab.Engine.Disconnect(key);
        var reconnect = lab.Engine.OpenSession(4)!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(failed.Failed, Is.EqualTo(1));
            Assert.That(lab.Engine.IsMaintenanceMode, Is.False);
            Assert.That(lab.Engine.Authenticate(reconnect, 100), Is.EqualTo(AuthenticationResult.AccountBlocked));
            Assert.That(lab.Engine.CanWrite(reconnect), Is.False);
            Assert.That(lab.Journal.Pending.Values.Single(), Is.EqualTo(original));
        });
        var unrelated = await lab.Login(5, 200);
        Assert.That(lab.Engine.CanWrite(unrelated), Is.True);
        lab.Bans.Fail = false;
        Assert.That((await lab.Engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(lab.Bans.Accounts[100], Is.EqualTo(original));
    }

    [Test]
    public async Task JournalFailureUsesDurableBanButRetainsEvidenceForRetry()
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Journal.FailAppend = true;
        lab.Engine.Observe(Lab.Violation(key));
        await lab.Engine.PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(lab.Bans.Accounts.ContainsKey(100), Is.True);
            Assert.That(lab.Engine.CanWrite(key), Is.False);
            Assert.That(lab.Engine.IsMaintenanceMode, Is.False);
            Assert.That(lab.Engine.PendingCount, Is.EqualTo(1));
        });
        lab.Journal.FailAppend = false;
        Assert.That((await lab.Engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(lab.Bans.Calls, Is.EqualTo(1), "A successful DB ban need not be called again in this process.");
        Assert.That(lab.Journal.All.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AllStorageFailureEntersExplicitMaintenanceThenRecoversWithoutReenablingOffender()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var bystander = await lab.Login(5, 200);
        lab.Journal.FailAppend = true;
        lab.Bans.Fail = true;
        lab.Engine.Observe(Lab.Violation(key));
        await lab.Engine.PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(lab.Engine.IsMaintenanceMode, Is.True);
            Assert.That(lab.Engine.MaintenanceReason, Is.EqualTo("sanction-persistence-unavailable"));
            Assert.That(lab.Engine.Authenticate(bystander, 200), Is.EqualTo(AuthenticationResult.Maintenance));
            Assert.That(lab.Engine.OpenSession(6), Is.Null);
            Assert.That(lab.Engine.SanctionCount, Is.EqualTo(1), "Storage failure is not extra cheating evidence.");
        });
        lab.Journal.FailAppend = false;
        lab.Bans.Fail = false;
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.IsMaintenanceMode, Is.False);
        Assert.That(lab.Engine.CanWrite(key), Is.False);
        Assert.That(lab.Engine.CanWrite(bystander), Is.True);
    }

    [Test]
    public async Task PendingPersistenceDeadlineUsesMonotonicClockAndDoesNotExpireSanction()
    {
        var lab = new Lab(new() { Scope = ExecutionScope.TestLab, PersistenceDeadline = TimeSpan.FromSeconds(5) });
        var key = await lab.Login();
        lab.Bans.Fail = true;
        lab.Engine.Observe(Lab.Violation(key));
        await lab.Engine.PumpAsync();
        lab.Clock.JumpWallClock(TimeSpan.FromDays(30));
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.IsMaintenanceMode, Is.False);
        lab.Clock.Advance(TimeSpan.FromSeconds(5));
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.IsMaintenanceMode, Is.True);
        Assert.That(lab.Engine.PendingCount, Is.EqualTo(1));
        lab.Bans.Fail = false;
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.IsMaintenanceMode, Is.False);
        Assert.That(lab.Engine.CanWrite(key), Is.False);
    }

    [Test]
    public async Task AckFailureRetriesJournalWithoutDuplicateAccountWrite()
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Journal.FailMark = true;
        lab.Engine.Observe(Lab.Violation(key));
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.PendingCount, Is.EqualTo(1));
        lab.Journal.FailMark = false;
        await lab.Engine.PumpAsync();
        Assert.That(lab.Engine.PendingCount, Is.Zero);
        Assert.That(lab.Bans.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task BoundedOutboxProcessesOnlyRequestedBatchAndNeverDropsProvenIntent()
    {
        var lab = new Lab(new() { Scope = ExecutionScope.TestLab, MaxSanctions = 3 });
        for (var index = 0; index < 3; index++)
        {
            var key = await lab.Login(index, 100 + index);
            lab.Engine.Observe(Lab.Violation(key));
        }
        Assert.That(lab.Engine.SanctionCount, Is.EqualTo(3));
        Assert.That(lab.Engine.IsMaintenanceMode, Is.True, "Full account fence storage must not silently evict.");
        Assert.That(lab.Engine.OpenSession(4), Is.Null);
        var pump = await lab.Engine.PumpAsync(1);
        Assert.That(pump.Attempted, Is.EqualTo(1));
        Assert.That(lab.Engine.PendingCount, Is.EqualTo(2));
        await lab.Engine.PumpAsync(2);
        Assert.That(lab.Bans.Accounts.Count, Is.EqualTo(3));
        Assert.That(lab.Engine.SanctionCount, Is.EqualTo(3));
    }

    [Test]
    public async Task RecoveryMustFinishBeforeNewAuthentication()
    {
        var lab = new Lab();
        Assert.That(lab.Engine.IsMaintenanceMode, Is.True);
        Assert.That(lab.Engine.OpenSession(4), Is.Null);
        lab.Journal.FailRead = true;
        Assert.That(await lab.Engine.RecoverAsync(), Is.False);
        Assert.That(lab.Engine.IsMaintenanceMode, Is.True);
        lab.Journal.FailRead = false;
        Assert.That(await lab.Engine.RecoverAsync(), Is.True);
        Assert.That(lab.Engine.OpenSession(4), Is.Not.Null);
    }

    [Test]
    public async Task CrashAfterJournalBeforeDatabaseReplaysSameIncidentAndRejectsAccount()
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Bans.Fail = true;
        var original = lab.Engine.Observe(Lab.Violation(key)).Incident;
        await lab.Engine.PumpAsync();
        var restarted = new AntiCheatEngine(lab.Clock, new() { Scope = ExecutionScope.TestLab }, Lab.Policy, lab.Journal, lab.Bans);
        Assert.That(await restarted.RecoverAsync(), Is.True);
        var reconnect = restarted.OpenSession(4)!.Value;
        Assert.That(restarted.Authenticate(reconnect, 100), Is.EqualTo(AuthenticationResult.AccountBlocked));
        lab.Bans.Fail = false;
        await restarted.PumpAsync();
        Assert.That(lab.Bans.Accounts[100], Is.EqualTo(original));
        Assert.That(lab.Journal.Pending, Is.Empty);
    }

    [Test]
    public async Task CrashAfterDatabaseBeforeAckReplaysIdempotentlyWithoutASecondAccountBan()
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Journal.FailMark = true;
        lab.Engine.Observe(Lab.Violation(key));
        await lab.Engine.PumpAsync();
        var restarted = new AntiCheatEngine(lab.Clock, new() { Scope = ExecutionScope.TestLab }, Lab.Policy, lab.Journal, lab.Bans);
        Assert.That(await restarted.RecoverAsync(), Is.True);
        lab.Journal.FailMark = false;
        await restarted.PumpAsync();
        Assert.That(lab.Bans.Calls, Is.EqualTo(2), "An uncertain previous acknowledgement must be retried.");
        Assert.That(lab.Bans.Accounts.Count, Is.EqualTo(1), "The store applies a permanent account ban idempotently.");
        Assert.That(lab.Journal.Pending, Is.Empty);
    }

    [Test]
    public async Task CanceledPumpRetainsIntentAndRevocationForRetry()
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Engine.Observe(Lab.Violation(key));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await lab.Engine.PumpAsync(cancellationToken: cancellation.Token));
        Assert.That(lab.Engine.CanWrite(key), Is.False);
        Assert.That(lab.Engine.PendingCount, Is.EqualTo(1));
        Assert.That((await lab.Engine.PumpAsync()).Applied, Is.EqualTo(1));
    }

    [TestCase(ExecutionScope.Production)]
    [TestCase(ExecutionScope.ObserveOnly)]
    public async Task HistoricalProductionQualifiedIntentCanRecoverWhileCurrentRuleIsDisabled(ExecutionScope scope)
    {
        // Fabricated production qualification is exclusively a recovery-contract fixture; this does
        // not qualify an actual runtime rule, and MemoryBans cannot touch TShock accounts.
        var lab = new Lab();
        var key = await lab.Login();
        var original = lab.Engine.Observe(Lab.Violation(key)).Incident!;
        var historical = original with { Evidence = original.Evidence with { Qualification = RuleQualification.ProductionQualified } };
        lab.Journal.Pending[historical.IncidentId] = historical;
        var restarted = new AntiCheatEngine(lab.Clock, new() { Scope = scope }, RulePolicy.Disabled, lab.Journal, lab.Bans);
        Assert.That(await restarted.RecoverAsync(), Is.True);
        Assert.That((await restarted.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(lab.Bans.Accounts.Count, Is.EqualTo(1));
    }

    [TestCase(ExecutionScope.Production)]
    [TestCase(ExecutionScope.ObserveOnly)]
    public async Task LabJournalCannotBeReplayedIntoLiveAccountBans(ExecutionScope scope)
    {
        var lab = new Lab();
        var key = await lab.Login();
        lab.Bans.Fail = true;
        lab.Engine.Observe(Lab.Violation(key));
        await lab.Engine.PumpAsync();
        var production = new AntiCheatEngine(lab.Clock, new() { Scope = scope }, RulePolicy.Disabled, lab.Journal, lab.Bans);
        Assert.That(await production.RecoverAsync(), Is.False);
        Assert.That(production.IsMaintenanceMode, Is.True);
        Assert.That((await production.PumpAsync()).Attempted, Is.Zero);
        Assert.That(lab.Bans.Accounts, Is.Empty);
    }

    [Test]
    public async Task CorruptRecoveredProofCannotBeApplied()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var intent = lab.Engine.Observe(Lab.Violation(key)).Incident!;
        var corrupt = intent with { Evidence = intent.Evidence with { AccountId = 200 } };
        lab.Journal.Pending[intent.IncidentId] = corrupt;
        var restarted = new AntiCheatEngine(lab.Clock, new() { Scope = ExecutionScope.TestLab }, Lab.Policy, lab.Journal, lab.Bans);
        Assert.That(await restarted.RecoverAsync(), Is.False);
        Assert.That((await restarted.PumpAsync()).Attempted, Is.Zero);
        Assert.That(lab.Bans.Calls, Is.Zero);
    }
}
