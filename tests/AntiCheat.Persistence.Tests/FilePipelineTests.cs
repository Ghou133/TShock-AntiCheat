using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Persistence;
using NUnit.Framework;

namespace AntiCheat.Persistence.Tests;

[TestFixture]
public sealed class FilePipelineTests
{
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "AntiCheat-IsolatedTests", Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private static readonly RulePolicy LabPolicy = new("lab-v1", RuleQualification.TestLab, "fixture-only", ImmutableArray.Create((byte)5));
    private static AntiCheatEngine Engine(IEnforcementJournal journal, IAccountBanStore bans) =>
        new(TimeProvider.System, new EngineOptions { Scope = ExecutionScope.TestLab }, LabPolicy, journal, bans);
    private static SelfSlotObservation Observe(SessionKey key, bool complete = true) =>
        new(key, 5, key.Slot + 1, new("lab-v1", "1", true, true, complete, true));
    private sealed class FaultingAccountStore : IAccountBanStore
    {
        public bool Unavailable { get; set; }
        public HashSet<long> Accounts { get; } = [];
        public int Insertions { get; private set; }
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new IOException("test database unavailable");
            if (Accounts.Add(intent.AccountId)) Insertions++;
            return ValueTask.CompletedTask;
        }
    }

    [Test] public async Task FirstProvenEventWritesRealJournalAndRecoversDatabaseFailure()
    {
        var bans = new FaultingAccountStore { Unavailable = true };
        Guid incidentId;
        using (var journal = new FileEnforcementJournal(directory))
        {
            var engine = Engine(journal, bans);
            Assert.That(await engine.RecoverAsync(), Is.True);
            var key = engine.OpenSession(0)!.Value;
            Assert.That(engine.Authenticate(key, 7), Is.EqualTo(AuthenticationResult.Authenticated));
            var decision = engine.Observe(Observe(key));
            Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(engine.CanWrite(key), Is.False, "Revocation occurs before persistence starts.");
            incidentId = decision.Incident!.IncidentId;
            var failed = await engine.PumpAsync();
            Assert.That(failed.Failed, Is.EqualTo(1));
            Assert.That((await journal.ReadAsync()).Single().IncidentId, Is.EqualTo(incidentId));
            Assert.That(await engine.CompleteShutdownAsync(), Is.True, "The pending evidence is durable, so controlled shutdown can close its memory-only window.");
        }
        using var reopened = new FileEnforcementJournal(directory);
        var recovered = Engine(reopened, bans);
        Assert.That(await recovered.RecoverAsync(), Is.True);
        var rejoined = recovered.OpenSession(0)!.Value;
        Assert.That(recovered.Authenticate(rejoined, 7), Is.EqualTo(AuthenticationResult.AccountBlocked));
        bans.Unavailable = false;
        Assert.That((await recovered.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That((await recovered.PumpAsync()).Attempted, Is.Zero);
        Assert.That(bans.Insertions, Is.EqualTo(1));
        Assert.That(await reopened.ReadAsync(), Is.Empty);
    }

    [Test] public async Task UnknownNeverCreatesDurableIntent()
    {
        using var journal = new FileEnforcementJournal(directory);
        var bans = new FaultingAccountStore();
        var engine = Engine(journal, bans);
        await engine.RecoverAsync();
        var key = engine.OpenSession(0)!.Value;
        engine.Authenticate(key, 7);
        Assert.That(engine.Observe(Observe(key, complete: false)).Behavior, Is.EqualTo(ControlAction.Unknown));
        await engine.PumpAsync();
        Assert.That(engine.CanWrite(key), Is.True);
        Assert.That(await journal.ReadAsync(), Is.Empty);
        Assert.That(bans.Insertions, Is.Zero);
    }

    [Test] public async Task CorruptedJournalPutsEngineInMaintenance()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "broken");
        using var journal = new FileEnforcementJournal(directory);
        var engine = Engine(journal, new FaultingAccountStore());
        Assert.That(await engine.RecoverAsync(), Is.False);
        Assert.That(engine.IsMaintenanceMode, Is.True);
        Assert.That(engine.OpenSession(0), Is.Null);
    }
}
