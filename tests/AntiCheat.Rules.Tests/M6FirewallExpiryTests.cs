using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M6FirewallExpiryTests
{
    [Test]
    public void UnreadableStateDoesNotPreventExpiredOwnedRuleCleanupOrEraseFutureRule()
    {
        var clock = new NetworkTestClock(); var store = new BrokenStore(); var backend = new Backend();
        var options = new RestrictedFirewallOptions(Path.Combine(Path.GetTempPath(), "m6-expiry.exe"), 7777, ["203.0.113.8"], []);
        using var executor = new WindowsFirewallExecutor(clock, options, store, backend);
        var expired = new RestrictedFirewallRule(WindowsFirewallExecutor.RuleName(executor.Scope, "203.0.113.8"), "203.0.113.8",
            options.ApplicationPath, options.LocalTcpPort, clock.GetUtcNow().AddSeconds(-1));
        var future = expired with { Name = WindowsFirewallExecutor.RuleName(executor.Scope, "203.0.113.9"), Address = "203.0.113.9", ExpiresUtc = clock.GetUtcNow().AddSeconds(20) };
        backend.Rules.Add(expired); backend.Rules.Add(future);
        Assert.That(executor.RecoverAndReconcile(), Is.False);
        Assert.That(executor.RemoveExpiredConfiguredBlocks(), Is.True);
        Assert.That(backend.Rules, Is.EqualTo(new[] { future }));
        Assert.That(executor.IsHealthy, Is.False, "Cleanup cannot turn broken recovery into healthy admission.");
        Assert.That(store.Saves, Is.Zero);
        Assert.That(executor.RemoveExpiredConfiguredBlocks(), Is.True); Assert.That(backend.Removed, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.That(executor.RemoveExpiredConfiguredBlocks(), Is.True); Assert.That(backend.Rules, Is.Empty);
    }

    [Test]
    public void ScopeConflictIsDetectedBeforeAnyExpiredRuleIsDeleted()
    {
        var clock = new NetworkTestClock(); var backend = new Backend();
        var options = new RestrictedFirewallOptions(Path.Combine(Path.GetTempPath(), "m6-expiry.exe"), 7777, [], []);
        using var executor = new WindowsFirewallExecutor(clock, options, new BrokenStore(), backend);
        var owned = new RestrictedFirewallRule(WindowsFirewallExecutor.RuleName(executor.Scope, "203.0.113.8"), "203.0.113.8",
            options.ApplicationPath, options.LocalTcpPort, clock.GetUtcNow().AddSeconds(-1));
        backend.Rules.Add(owned); backend.Rules.Add(owned with { Name = "foreign-rule" });
        Assert.That(executor.RemoveExpiredConfiguredBlocks(), Is.False);
        Assert.That(backend.Removed, Is.Zero); Assert.That(backend.Rules.Count, Is.EqualTo(2));
    }
    private sealed class BrokenStore : IFirewallStateStore
    {
        public int Saves;
        public FirewallPersistentState? Load() => throw new IOException("fixture unreadable state");
        public void Save(FirewallPersistentState state) => Saves++;
        public void Dispose() { }
    }
    private sealed class Backend : IRestrictedFirewallBackend
    {
        public readonly List<RestrictedFirewallRule> Rules = [];
        public int Removed;
        public IReadOnlyList<RestrictedFirewallRule> ReadOwnedRules() => Rules.ToArray();
        public void EnsureRule(RestrictedFirewallRule rule) => throw new AssertionException("Expiry sweep must never create a rule.");
        public void RemoveRule(string name) { Rules.RemoveAll(rule => rule.Name == name); Removed++; }
    }
}
