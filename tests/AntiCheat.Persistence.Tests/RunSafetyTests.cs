using AntiCheat.Core;
using AntiCheat.Persistence;
using NUnit.Framework;

namespace AntiCheat.Persistence.Tests;

[TestFixture]
public sealed class RunSafetyTests
{
    private string directory = null!;
    private static readonly RulePolicy LabPolicy = new("lab-v1", RuleQualification.TestLab, "fixture-only", [5]);
    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "AntiCheat-IsolatedTests", Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class AccountStore : IAccountBanStore
    {
        public bool Fail { get; set; }
        public HashSet<long> Accounts { get; } = [];
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Injected test database failure.");
            Accounts.Add(intent.AccountId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingAppendJournal(FileEnforcementJournal inner) : IEnforcementJournal, IRunSafetyGuard
    {
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected test journal write failure after the startup marker was durable."));
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => inner.MarkAppliedAsync(incidentId, cancellationToken);
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => inner.BeginRunAsync(request, cancellationToken);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => inner.CompleteRunAsync(serverRunId, cancellationToken);
    }

    private static AntiCheatEngine Engine(IEnforcementJournal journal, IAccountBanStore bans, ExecutionScope scope = ExecutionScope.TestLab) =>
        new(TimeProvider.System, new() { Scope = scope }, scope == ExecutionScope.TestLab ? LabPolicy : RulePolicy.Disabled, journal, bans);
    private static SessionKey Login(AntiCheatEngine engine)
    {
        var key = engine.OpenSession(4)!.Value;
        Assert.That(engine.Authenticate(key, 7), Is.EqualTo(AuthenticationResult.Authenticated));
        return key;
    }
    private static ActionDecision Prove(AntiCheatEngine engine, SessionKey key) =>
        engine.Observe(new(key, 5, 5, new("lab-v1", "1", true, true, true, true)));

    [TestCase(ExecutionScope.TestLab)]
    [TestCase(ExecutionScope.ObserveOnly)]
    public async Task CrashBeforeFirstAppendLeavesDurableGuardAndCannotBeBypassedWithEmptyJournal(ExecutionScope restartScope)
    {
        var bans = new AccountStore();
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, bans);
            Assert.That(await engine.RecoverAsync(), Is.True);
            var key = Login(engine);
            Assert.That(Prove(engine, key).Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(engine.CanWrite(key), Is.False);
            Assert.That(Directory.GetFiles(directory, "*.json"), Is.Empty, "The intent is still memory-only.");
            // Simulated abrupt termination: dispose the file lease without CompleteShutdownAsync.
        }
        using var reopened = new FileEnforcementJournal(directory);
        var restarted = Engine(reopened, bans, restartScope);
        Assert.That(await restarted.RecoverAsync(), Is.False);
        Assert.That(restarted.MaintenanceReason, Is.EqualTo("previous-enforcement-run-unclean"));
        Assert.That(restarted.OpenSession(4), Is.Null);
        Assert.That(bans.Accounts, Is.Empty, "A lost account identity cannot be reconstructed or fabricated.");
        Assert.That(await reopened.ReadAsync(), Is.Empty);
    }

    [Test]
    public async Task BothStoresFailRestartStillSeesMarkerWrittenBeforeProofsWereEnabled()
    {
        var bans = new AccountStore { Fail = true };
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(new FailingAppendJournal(journal), bans);
            Assert.That(await engine.RecoverAsync(), Is.True);
            Prove(engine, Login(engine));
            await engine.PumpAsync();
            Assert.That(engine.IsMaintenanceMode, Is.True);
            Assert.That(await engine.CompleteShutdownAsync(), Is.False, "Undurable evidence cannot be marked clean.");
        }
        using var reopened = new FileEnforcementJournal(directory);
        var restarted = Engine(reopened, bans);
        Assert.That(await restarted.RecoverAsync(), Is.False);
        Assert.That(restarted.MaintenanceReason, Is.EqualTo("previous-enforcement-run-unclean"));
        Assert.That(restarted.OpenSession(4), Is.Null);
    }

    [Test]
    public async Task CleanShutdownWithDurableEvidenceAllowsAutomaticRecovery()
    {
        var bans = new AccountStore();
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, bans);
            Assert.That(await engine.RecoverAsync(), Is.True);
            Prove(engine, Login(engine));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(await engine.CompleteShutdownAsync(), Is.True);
            Assert.That(engine.OpenSession(5), Is.Null, "Clean shutdown has stopped new proof production.");
        }
        using var reopened = new FileEnforcementJournal(directory);
        var restarted = Engine(reopened, bans);
        Assert.That(await restarted.RecoverAsync(), Is.True);
        Assert.That(restarted.OpenSession(5), Is.Not.Null);
        Assert.That(bans.Accounts.Contains(7), Is.True, "The durable account store remains authoritative for applied bans.");
    }

    [Test]
    public async Task ControlledShutdownCanPreservePendingDatabaseBanForAutomaticReplay()
    {
        var bans = new AccountStore { Fail = true };
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, bans);
            Assert.That(await engine.RecoverAsync(), Is.True);
            Prove(engine, Login(engine));
            await engine.PumpAsync();
            Assert.That(engine.PendingCount, Is.EqualTo(1));
            Assert.That(await engine.CompleteShutdownAsync(), Is.True, "Pending structured evidence is durable even though the DB failed.");
        }
        using var reopened = new FileEnforcementJournal(directory);
        var restarted = Engine(reopened, bans);
        Assert.That(await restarted.RecoverAsync(), Is.True);
        var key = restarted.OpenSession(4)!.Value;
        Assert.That(restarted.Authenticate(key, 7), Is.EqualTo(AuthenticationResult.AccountBlocked));
        bans.Fail = false;
        Assert.That((await restarted.PumpAsync()).Applied, Is.EqualTo(1));
    }

    [TestCase(ExecutionScope.ObserveOnly)]
    [TestCase(ExecutionScope.TestLab)]
    public async Task ObserveOnlyUncleanRunMayRecoverAutomaticallyOrBeginFirstQualifiedLabRun(ExecutionScope restartScope)
    {
        var bans = new AccountStore();
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, bans, ExecutionScope.ObserveOnly);
            Assert.That(await engine.RecoverAsync(), Is.True);
            Assert.That(Prove(engine, Login(engine)).Verdict, Is.EqualTo(Verdict.Unknown));
            // No clean call; this run could not produce new proof decisions.
        }
        using var reopened = new FileEnforcementJournal(directory);
        var restarted = Engine(reopened, bans, restartScope);
        Assert.That(await restarted.RecoverAsync(), Is.True);
        Assert.That(restarted.OpenSession(4), Is.Not.Null);
        Assert.That(bans.Accounts, Is.Empty);
    }

    [Test]
    public async Task LegacyEmptyJournalDoesNotProveCleanAndRequiresExplicitVerifiedRecovery()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "journal.lock"), "");
        using var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new AccountStore(), ExecutionScope.ObserveOnly);
        Assert.That(await engine.RecoverAsync(), Is.False);
        Assert.That(engine.MaintenanceReason, Is.EqualTo("legacy-journal-review-required"));
        await journal.ConfirmRecoveryAsync("isolated-test/legacy-directory-verified-empty");
        Assert.That(await engine.RecoverAsync(), Is.True);
    }

    [Test]
    public async Task UncleanEnforcementRunDoesNotAutomaticallyClearItsOwnMarkerOnFailedShutdown()
    {
        var bans = new AccountStore();
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, bans);
            await engine.RecoverAsync();
            Prove(engine, Login(engine));
            Assert.That(await engine.CompleteShutdownAsync(), Is.False);
        }
        using var reopened = new FileEnforcementJournal(directory);
        var rejected = Engine(reopened, bans, ExecutionScope.ObserveOnly);
        Assert.That(await rejected.RecoverAsync(), Is.False);
        Assert.That(await rejected.CompleteShutdownAsync(), Is.False);
        Assert.That(await rejected.RecoverAsync(), Is.False);
    }

    [Test]
    public async Task UnavailableStartupMarkerPreventsProofProductionBeforeAnyAccountIsAccepted()
    {
        using var journal = new FileEnforcementJournal(directory);
        Directory.CreateDirectory(Path.Combine(directory, "run-safety.next"));
        var bans = new AccountStore();
        var engine = Engine(journal, bans);
        Assert.That(await engine.RecoverAsync(), Is.False);
        Assert.That(engine.MaintenanceReason, Is.EqualTo("run-safety-marker-unavailable"));
        Assert.That(engine.OpenSession(4), Is.Null);
        Assert.That(bans.Accounts, Is.Empty);
    }

    [Test]
    public async Task CorruptedMarkerCannotSilentlyDowngradeEnforcementRunToObservation()
    {
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, new AccountStore());
            await engine.RecoverAsync();
            await engine.CompleteShutdownAsync();
        }
        var marker = Path.Combine(directory, "run-safety.state");
        var text = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker, text.Replace("\"CanProduceProofs\":true", "\"CanProduceProofs\":false"));
        using var reopened = new FileEnforcementJournal(directory);
        var restarted = Engine(reopened, new AccountStore(), ExecutionScope.ObserveOnly);
        Assert.That(await restarted.RecoverAsync(), Is.False);
        Assert.That(restarted.OpenSession(4), Is.Null);
    }
}
