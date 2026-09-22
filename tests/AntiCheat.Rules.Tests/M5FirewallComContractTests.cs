using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

/// <summary>Dynamic COM shape tests use plain managed objects. No COM activation, elevation or OS access occurs.</summary>
[TestFixture, SupportedOSPlatform("windows")]
public sealed class M5FirewallComContractTests
{
    [Test]
    public void ExactComPropertiesAndProtocolBeforePortsAreAppliedAndReadBack()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows COM contract is platform-scoped; this test does not activate COM.");
        var options = Options(); var factory = new FakeComFactory();
        Assert.Throws<InvalidOperationException>(() => new WindowsComFirewallBackend(options, false, factory));
        Assert.That(factory.PolicyCalls, Is.Zero);
        var backend = new WindowsComFirewallBackend(options, true, factory);
        string scope = WindowsFirewallExecutor.ScopeFor(options.ApplicationPath, 7777);
        var desired = new RestrictedFirewallRule(WindowsFirewallExecutor.RuleName(scope, "203.0.113.8"), "203.0.113.8",
            options.ApplicationPath, 7777, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.That(factory.PolicyCalls, Is.Zero, "Constructing even an explicitly enabled backend does not activate OS APIs.");
        backend.EnsureRule(desired);
        var stored = factory.Policy.Rules.Items.Single().Value;
        Assert.Multiple(() =>
        {
            Assert.That(stored.Grouping, Is.EqualTo(WindowsFirewallExecutor.RuleGroup));
            Assert.That(stored.ApplicationName, Is.EqualTo(options.ApplicationPath));
            Assert.That(stored.Protocol, Is.EqualTo(6)); Assert.That(stored.LocalPorts, Is.EqualTo("7777"));
            Assert.That(stored.RemoteAddresses, Is.EqualTo("203.0.113.8"));
            Assert.That(stored.Direction, Is.EqualTo(1)); Assert.That(stored.Action, Is.Zero);
            Assert.That(stored.Enabled, Is.True); Assert.That(stored.EdgeTraversal, Is.False);
        });
        Assert.That(backend.ReadOwnedRules().Single(), Is.EqualTo(desired));
        backend.EnsureRule(desired); Assert.That(factory.RuleCalls, Is.EqualTo(1), "Unchanged config is idempotent.");
        stored.RemoteAddresses = "203.0.113.8/32";
        Assert.That(backend.ReadOwnedRules().Single(), Is.EqualTo(desired), "Only full host prefixes normalize.");
        stored.RemoteAddresses = "203.0.113.8/255.255.255.255";
        Assert.That(backend.ReadOwnedRules().Single(), Is.EqualTo(desired));
        backend.RemoveRule(desired.Name); Assert.That(factory.Policy.Rules.Items, Is.Empty);
        backend.RemoveRule(desired.Name); Assert.That(factory.Policy.Rules.RemoveCalls, Is.EqualTo(1));
    }

    [Test]
    public void ForeignNameCollisionBroadAddressAndOtherScopeNeverOverwriteOrRemove()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows COM contract is platform-scoped; this test does not activate COM.");
        var options = Options(); var factory = new FakeComFactory();
        var backend = new WindowsComFirewallBackend(options, true, factory);
        string scope = WindowsFirewallExecutor.ScopeFor(options.ApplicationPath, 7777);
        var desired = new RestrictedFirewallRule(WindowsFirewallExecutor.RuleName(scope, "203.0.113.8"), "203.0.113.8",
            options.ApplicationPath, 7777, DateTimeOffset.UtcNow.AddMinutes(1));
        backend.EnsureRule(desired);
        var stored = factory.Policy.Rules.Items[desired.Name]; stored.Grouping = "Some administrator's rule";
        Assert.Throws<InvalidDataException>(() => backend.EnsureRule(desired));
        Assert.Throws<InvalidDataException>(() => backend.RemoveRule(desired.Name));
        Assert.That(factory.Policy.Rules.RemoveCalls, Is.Zero); Assert.That(factory.RuleCalls, Is.EqualTo(1));
        stored.Grouping = WindowsFirewallExecutor.RuleGroup; stored.RemoteAddresses = "203.0.113.0/24";
        Assert.Throws<InvalidDataException>(() => backend.ReadOwnedRules());
        Assert.Throws<ArgumentException>(() => backend.RemoveRule("Unrelated Windows rule"));
        Assert.Throws<ArgumentException>(() => backend.EnsureRule(desired with { Address = "*" }));
        Assert.Throws<ArgumentException>(() => backend.EnsureRule(desired with { LocalTcpPort = 3389 }));
    }

    private static RestrictedFirewallOptions Options() => new(Path.GetFullPath("TShock.Server.exe"), 7777, ["203.0.113.8"], []);

    public sealed class FakeComFactory : IWindowsFirewallComFactory
    {
        public FakePolicy Policy { get; } = new(); public int PolicyCalls, RuleCalls;
        public object CreatePolicy() { PolicyCalls++; return Policy; }
        public object CreateRule() { RuleCalls++; return new FakeRule(); }
    }
    public sealed class FakePolicy { public FakeRules Rules { get; } = new(); }
    public sealed class FakeRules : IEnumerable
    {
        public Dictionary<string, FakeRule> Items { get; } = []; public int RemoveCalls;
        public object Item(string name) => Items.TryGetValue(name, out var rule) ? rule :
            throw new COMException("Rule not found.", unchecked((int)0x80070002));
        public void Add(FakeRule rule) => Items[rule.Name] = rule;
        public void Remove(string name) { RemoveCalls++; Items.Remove(name); }
        public IEnumerator GetEnumerator() => Items.Values.GetEnumerator();
    }
    public sealed class FakeRule
    {
        public string Name { get; set; } = "";
        public string Grouping { get; set; } = "";
        public string Description { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string RemoteAddresses { get; set; } = "";
        public string LocalAddresses { get; set; } = "";
        public string RemotePorts { get; set; } = "";
        public string InterfaceTypes { get; set; } = "";
        public int Protocol { get; set; }
        private string ports = "";
        public string LocalPorts { get => ports; set { if (Protocol != 6) throw new InvalidOperationException("Protocol must precede LocalPorts."); ports = value; } }
        public int Direction { get; set; }
        public int Action { get; set; }
        public int Profiles { get; set; }
        public bool EdgeTraversal { get; set; }
        public bool Enabled { get; set; }
    }
}
