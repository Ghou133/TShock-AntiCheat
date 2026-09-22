using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M5FirewallExecutorTests
{
    private string directory = null!;
    private NetworkTestClock clock = null!;
    private FakeBackend backend = null!;
    private RestrictedFirewallOptions options = null!;
    private WindowsFirewallExecutor executor = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Tests", "m5-firewall-" + Guid.NewGuid().ToString("N"));
        clock = new(); backend = new();
        options = new(Path.Combine(directory, "TShock.Server.exe"), 7777,
            ["203.0.113.8", "198.51.100.9", "203.0.113.10", "2001:db8::8"], ["203.0.113.10"], 16, 4);
        executor = NewExecutor();
    }

    [TearDown]
    public void TearDown()
    {
        executor.Dispose();
        // Exact owned test directory, not a computed ancestor or user data directory.
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private WindowsFirewallExecutor NewExecutor(RestrictedFirewallOptions? policy = null) =>
        new(clock, policy ?? options, new FileFirewallStateStore(directory), backend);

    private FirewallIpcCommand Command(string address = "203.0.113.8", int ttl = 5)
    {
        var source = new FirewallDryRun(clock).Create(IPAddress.Parse(address),
            new(clock.GetUtcNow(), clock.GetUtcNow(), 1, 100, 4), NetworkAbuseReason.WeightedPacketCost,
            TimeSpan.FromSeconds(ttl), exclusiveSourceVerified: true);
        return new(Guid.NewGuid(), FirewallIpcOperation.ExecuteTemporaryBlock, source with { DryRun = false, Execute = true });
    }

    [Test]
    public void ConstructionAndAllDryRunOperationsNeverTouchOsEvenWithLiveExecutorObject()
    {
        Assert.That(backend.Reads, Is.Zero); Assert.That(executor.IsHealthy, Is.False);
        var explicitCommand = Command();
        var dry = explicitCommand.SourceEvent with { DryRun = true, Execute = false };
        foreach (var operation in new[] { FirewallIpcOperation.RecordEvent, FirewallIpcOperation.SimulateTemporaryBlock })
        {
            var result = executor.Apply(new(Guid.NewGuid(), operation, dry with { EventId = Guid.NewGuid() }));
            Assert.That(result.Accepted, Is.True); Assert.That(result.DryRun, Is.True);
        }
        Assert.That(executor.Apply(explicitCommand).Accepted, Is.False, "Explicit OS command requires completed helper recovery.");
        Assert.That(backend.Reads, Is.Zero); Assert.That(backend.Ensures, Is.Zero); Assert.That(backend.Removes, Is.Zero);
        Assert.That(new InMemoryFirewallExecutor(clock).Apply(explicitCommand).Reason, Is.EqualTo("os-execution-not-enabled"));
        Assert.That(executor.Apply(new(Guid.NewGuid(), FirewallIpcOperation.SimulateTemporaryBlock, explicitCommand.SourceEvent)).Accepted, Is.False);
    }

    [TestCase("127.0.0.1")][TestCase("::1")][TestCase("10.0.0.1")][TestCase("100.64.0.1")]
    [TestCase("fc00::1")][TestCase("fe80::1")][TestCase("224.0.0.1")][TestCase("203.0.113.10")]
    [TestCase("198.51.100.99")]
    public void ProtectedSharedManagementAndUnprovisionedSourcesNeverGetRules(string address)
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        Assert.That(executor.Apply(Command(address)).Accepted, Is.False);
        Assert.That(backend.Rules, Is.Empty); Assert.That(backend.Ensures, Is.Zero);
    }

    [Test]
    public void AuthenticatedSourceFieldsCannotAuthorizeProxyNatOtherTargetOrImplicitExecution()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        var command = Command();
        foreach (var source in new[]
        {
            command.SourceEvent with { ProxyPeer = true, VerifiedClientAddress = "198.51.100.9" },
            command.SourceEvent with { ExclusiveSourceVerified = false },
            command.SourceEvent with { ProposedTarget = "198.51.100.9" },
            command.SourceEvent with { ProposedTarget = "203.0.113.8/24" },
            command.SourceEvent with { ProposedTarget = "203.0.113.8; command" },
            command.SourceEvent with { DryRun = true },
            command.SourceEvent with { Execute = false },
            command.SourceEvent with { AddressProvenance = "chat-message" },
            command.SourceEvent with { SuggestedTtlSeconds = 901 }
        }) Assert.That(executor.Apply(command with { SourceEvent = source }).Accepted, Is.False);
        Assert.That(backend.Ensures, Is.Zero);
    }

    [Test]
    public void DurableRestartKeepsExactEventIdAndExpiryAndExpiresEvenWithNoNewRequests()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        var command = Command();
        Assert.That(executor.Apply(command).Accepted, Is.True);
        var initial = backend.Rules.Values.Single();
        Assert.That(initial.ApplicationPath, Is.EqualTo(options.ApplicationPath));
        Assert.That(initial.LocalTcpPort, Is.EqualTo(7777));
        Assert.That(initial.Address, Is.EqualTo("203.0.113.8"));
        Assert.That(initial.Name, Is.EqualTo(WindowsFirewallExecutor.RuleName(executor.Scope, initial.Address)));
        executor.Dispose(); clock.Advance(TimeSpan.FromSeconds(2)); executor = NewExecutor();
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        Assert.That(executor.Apply(command with { RequestId = Guid.NewGuid() }).Reason, Is.EqualTo("duplicate-event"));
        Assert.That(backend.Ensures, Is.EqualTo(1)); Assert.That(backend.Rules.Values.Single().ExpiresUtc, Is.EqualTo(initial.ExpiresUtc));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(backend.Rules, Is.Empty);
        Assert.That(executor.Apply(command with { RequestId = Guid.NewGuid() }).Reason, Is.EqualTo("duplicate-event"));
        Assert.That(backend.Rules, Is.Empty); Assert.That(backend.Removes, Is.EqualTo(1));
        executor.Dispose(); executor = NewExecutor();
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(backend.Rules, Is.Empty);
    }

    [Test]
    public void ContentConflictAndCapacityPreservePriorRulesWhileNewEventCanExtendWithinTtl()
    {
        executor.Dispose(); executor = NewExecutor(options with { MaximumEvents = 2, MaximumTargets = 1 });
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        var first = Command(); Assert.That(executor.Apply(first).Accepted, Is.True);
        Assert.That(executor.Apply(first with { SourceEvent = first.SourceEvent with { SuggestedTtlSeconds = 6 } }).Reason,
            Is.EqualTo("event-id-content-conflict"));
        Assert.That(executor.Apply(Command("198.51.100.9")).Reason, Is.EqualTo("target-capacity"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(executor.Apply(Command(ttl: 10)).Accepted, Is.True);
        Assert.That(backend.Rules.Values.Single().ExpiresUtc, Is.EqualTo(clock.GetUtcNow().AddSeconds(10)));
        Assert.That(executor.Apply(Command(ttl: 20)).Reason, Is.EqualTo("event-capacity"));
    }

    [Test]
    public void UncertainOsWriteRecoversPersistedIntentWithoutDuplicateRules()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        backend.ThrowAfterEnsure = true;
        var command = Command();
        Assert.That(executor.Apply(command).Reason, Is.EqualTo("firewall-maintenance"));
        Assert.That(executor.IsHealthy, Is.False); Assert.That(backend.Rules.Count, Is.EqualTo(1));
        Assert.That(executor.Apply(Command("198.51.100.9")).Accepted, Is.False);
        executor.Dispose(); executor = NewExecutor();
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        Assert.That(executor.Apply(command).Reason, Is.EqualTo("duplicate-event"));
        Assert.That(backend.Ensures, Is.EqualTo(1)); Assert.That(backend.Rules.Count, Is.EqualTo(1));
    }

    [Test]
    public void DiskFailurePrecedesAnyOsMutationAndRecoversOnlyAfterStateIsReadable()
    {
        executor.Dispose();
        using var actual = new FileFirewallStateStore(directory);
        var faults = new FaultStore(actual);
        executor = new(clock, options, faults, backend);
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        faults.FailWrites = true;
        Assert.That(executor.Apply(Command()).Accepted, Is.False); Assert.That(backend.Ensures, Is.Zero);
        Assert.That(executor.IsHealthy, Is.False);
        faults.FailWrites = false;
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        Assert.That(backend.Rules, Is.Empty);
        Assert.That(executor.Apply(Command()).Accepted, Is.True);
    }

    [Test]
    public void CorruptOrForeignScopeStateCannotIssueOrDeleteOsRules()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(executor.Apply(Command()).Accepted, Is.True);
        executor.Dispose();
        File.WriteAllText(Path.Combine(directory, "firewall-state-v1.json"), "{broken");
        int reads = backend.Reads;
        executor = NewExecutor();
        Assert.That(executor.RecoverAndReconcile(), Is.False); Assert.That(backend.Reads, Is.EqualTo(reads));
        Assert.That(backend.Rules.Count, Is.EqualTo(1));
        Assert.That(executor.Apply(Command()).Accepted, Is.False);
    }

    [Test]
    public void StaleOwnedRulesAreRemovedAndHostPolicyChangeCannotKeepProtectedTargets()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(executor.Apply(Command()).Accepted, Is.True);
        backend.Rules.Clear(); // Out-of-band deletion is repaired by explicit maintenance.
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(backend.Ensures, Is.EqualTo(2));
        executor.Dispose(); executor = NewExecutor(options with { ProtectedAddresses = ["203.0.113.8"] });
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(backend.Rules, Is.Empty);
        string address = "198.51.100.9";
        var orphan = new RestrictedFirewallRule(WindowsFirewallExecutor.RuleName(executor.Scope, address), address,
            options.ApplicationPath, options.LocalTcpPort, clock.GetUtcNow().AddSeconds(10));
        backend.Rules.Add(orphan.Name, orphan);
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(backend.Rules, Is.Empty);
    }

    [Test]
    public void ClockRollbackFailsWithoutExtendingAndGracefulReleaseKeepsReplayDedupe()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        var command = Command(); Assert.That(executor.Apply(command).Accepted, Is.True);
        clock.JumpUtc(TimeSpan.FromSeconds(-1));
        Assert.That(executor.RecoverAndReconcile(), Is.False);
        Assert.That(executor.Apply(Command()).Accepted, Is.False);
        clock.JumpUtc(TimeSpan.FromSeconds(1));
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        Assert.That(executor.ReleaseAllConfiguredBlocks(), Is.True); Assert.That(backend.Rules, Is.Empty);
        Assert.That(executor.Apply(command).Reason, Is.EqualTo("duplicate-event")); Assert.That(backend.Rules, Is.Empty);
    }

    [Test]
    public void FileSnapshotHasExclusiveLeaseChecksumAndBoundedGrowth()
    {
        Assert.Throws<IOException>(() => new FileFirewallStateStore(directory));
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(executor.Apply(Command()).Accepted, Is.True);
        string file = Path.Combine(directory, "firewall-state-v1.json");
        using var content = JsonDocument.Parse(File.ReadAllBytes(file));
        var payload = Convert.FromBase64String(content.RootElement.GetProperty("Payload").GetString()!);
        Assert.That(content.RootElement.GetProperty("Sha256").GetString(), Is.EqualTo(Convert.ToHexString(SHA256.HashData(payload))));
        Assert.That(new FileInfo(file).Length, Is.LessThan(2 * 1024 * 1024));
        Assert.That(Directory.GetFiles(directory, "*.pending"), Is.Empty);
    }

    [Test]
    public void DifferentExecutablePortScopeCannotReplayAnotherHelpersPersistentState()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True); Assert.That(executor.Apply(Command()).Accepted, Is.True);
        executor.Dispose();
        int reads = backend.Reads;
        executor = NewExecutor(options with { LocalTcpPort = 7788 });
        Assert.That(executor.RecoverAndReconcile(), Is.False);
        Assert.That(backend.Reads, Is.EqualTo(reads)); Assert.That(backend.Rules.Count, Is.EqualTo(1));
    }

    [Test]
    public void ExecutionBurstBudgetIsBoundedAndRecoversWithoutTreatingRateAsCheating()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        var command = Command();
        for (int index = 0; index < 8; index++) Assert.That(executor.Apply(command).Accepted, Is.True);
        Assert.That(executor.Apply(command).Reason, Is.EqualTo("host-execution-budget"));
        Assert.That(backend.Ensures, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.That(executor.Apply(command).Reason, Is.EqualTo("duplicate-event"));
        Assert.That(backend.Ensures, Is.EqualTo(1));
    }

    [Test]
    public void CorruptedChecksumAndExpiredOriginalObservationNeverProduceOsActions()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        var old = Command(); clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(executor.Apply(old).Reason, Is.EqualTo("source-event-expired-or-future")); Assert.That(backend.Ensures, Is.Zero);
        executor.Dispose();
        string file = Path.Combine(directory, "firewall-state-v1.json");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(file)))
            File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Sha256 = new string('0', 64),
                Payload = json.RootElement.GetProperty("Payload").GetString() }));
        int reads = backend.Reads; executor = NewExecutor();
        Assert.That(executor.RecoverAndReconcile(), Is.False); Assert.That(backend.Reads, Is.EqualTo(reads));
    }

    [Test]
    public async Task ExistingRealNamedPipeHmacDispatchesExplicitOsCommandOnlyToInjectedBoundary()
    {
        Assert.That(executor.RecoverAndReconcile(), Is.True);
        string pipe = "m5-firewall-" + Guid.NewGuid().ToString("N"); byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var server = new FirewallIpcServer(pipe, key, clock, executor);
            var command = Command(); var serving = server.ServeOnceAsync();
            var result = await FirewallIpcClient.SendAsync(pipe, key, command, clock);
            Assert.That(await serving, Is.EqualTo(result));
            Assert.That(result.Accepted, Is.True); Assert.That(result.DryRun, Is.False); Assert.That(result.ExecutedTargets, Is.EqualTo(1));
            Assert.That(backend.Ensures, Is.EqualTo(1));
            serving = server.ServeOnceAsync();
            byte[] wrong = RandomNumberGenerator.GetBytes(32);
            Assert.ThrowsAsync<InvalidDataException>(async () => await FirewallIpcClient.SendAsync(pipe, wrong, Command("198.51.100.9"), clock));
            Assert.That((await serving).Accepted, Is.False); Assert.That(backend.Ensures, Is.EqualTo(1));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private sealed class FakeBackend : IRestrictedFirewallBackend
    {
        public Dictionary<string, RestrictedFirewallRule> Rules { get; } = [];
        public int Reads, Ensures, Removes; public bool ThrowAfterEnsure;
        public IReadOnlyList<RestrictedFirewallRule> ReadOwnedRules() { Reads++; return Rules.Values.ToArray(); }
        public void EnsureRule(RestrictedFirewallRule rule)
        {
            Ensures++; Rules[rule.Name] = rule;
            if (ThrowAfterEnsure) { ThrowAfterEnsure = false; throw new IOException("Injected uncertain OS boundary outcome."); }
        }
        public void RemoveRule(string name) { Removes++; Rules.Remove(name); }
    }
    private sealed class FaultStore(IFirewallStateStore inner) : IFirewallStateStore
    {
        public bool FailWrites;
        public FirewallPersistentState? Load() => inner.Load();
        public void Save(FirewallPersistentState state)
        { if (FailWrites) throw new IOException("Injected durable-write failure."); inner.Save(state); }
        public void Dispose() => inner.Dispose();
    }
}
