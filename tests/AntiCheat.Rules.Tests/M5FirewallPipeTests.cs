using System.Net;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture, SupportedOSPlatform("windows")]
public sealed class M5FirewallPipeTests
{
    [Test]
    public async Task ExplicitWindowsUserAclRetainsAuthenticatedProtocolAndDeniesNetworkLogons()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows SID ACL contract.");
        using var identity = WindowsIdentity.GetCurrent();
        var factory = new WindowsFirewallPipeFactory(identity.User!.Value);
        var security = factory.BuildSecurity();
        var entries = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.That(security.AreAccessRulesProtected, Is.True);
        Assert.That(entries.Any(x => x.IdentityReference.Value == "S-1-5-2" && x.AccessControlType == AccessControlType.Deny), Is.True);
        Assert.That(entries.Where(x => x.AccessControlType == AccessControlType.Allow).All(x => x.IdentityReference.Value == identity.User.Value), Is.True);
        Assert.Throws<ArgumentException>(() => new WindowsFirewallPipeFactory("S-1-1-0"));
        Assert.Throws<ArgumentException>(() => new WindowsFirewallPipeFactory("S-1-5-32-544"));
        var clock = TimeProvider.System; byte[] key = RandomNumberGenerator.GetBytes(32);
        string name = "m5-firewall-acl-" + Guid.NewGuid().ToString("N");
        try
        {
            using var server = new FirewallIpcServer(name, key, clock, (IFirewallIpcExecutor)new InMemoryFirewallExecutor(clock),
                TimeSpan.FromSeconds(3), factory.Create);
            var serving = server.ServeOnceAsync();
            var source = new FirewallDryRun(clock).Create(IPAddress.Loopback,
                new(clock.GetUtcNow(), clock.GetUtcNow(), 1, 100, 1), NetworkAbuseReason.WeightedPacketCost, TimeSpan.FromSeconds(5));
            var result = await FirewallIpcClient.SendAsync(name, key, new(Guid.NewGuid(), FirewallIpcOperation.RecordEvent, source), clock);
            Assert.That(await serving, Is.EqualTo(result)); Assert.That(result.Accepted, Is.True); Assert.That(result.DryRun, Is.True);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
