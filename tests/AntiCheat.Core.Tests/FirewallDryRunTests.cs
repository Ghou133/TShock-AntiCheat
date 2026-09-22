using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class FirewallDryRunTests
{
    private readonly ControlledClock clock = new();
    private NetworkWindow Window => new(clock.GetUtcNow() - TimeSpan.FromSeconds(10), clock.GetUtcNow(), 20, 1000, 100);

    [Test]
    public void SharedNatIsRecordOnlyByDefaultAndHasNoExecutionTarget()
    {
        var result = new FirewallDryRun(clock).Create(IPAddress.Parse("203.0.113.9"), Window,
            NetworkAbuseReason.ConnectionBudget, TimeSpan.FromMinutes(1));
        Assert.That(result.Recommendation, Is.EqualTo(FirewallRecommendation.RecordOnly));
        Assert.That(result.ProposedTarget, Is.Null);
        Assert.That(result.AccountId, Is.Null);
        Assert.That(result.DryRun, Is.True);
        Assert.That(result.Execute, Is.False);
    }

    [Test]
    public void ProxyAddressIsNeverAProposedTargetEvenWithVerifiedClientAddress()
    {
        var result = new FirewallDryRun(clock).Create(IPAddress.Parse("203.0.113.9"), Window,
            NetworkAbuseReason.WeightedPacketCost, TimeSpan.FromMinutes(1), proxyPeer: true,
            exclusiveSourceVerified: true, verifiedClientAddress: IPAddress.Parse("198.51.100.7"));
        Assert.That(result.Recommendation, Is.EqualTo(FirewallRecommendation.RecordOnly));
        Assert.That(result.ProposedTarget, Is.Null);
        Assert.That(result.VerifiedClientAddress, Is.EqualTo("198.51.100.7"));
    }

    [Test]
    public void ExclusiveSocketSourceCanOnlyProduceDryRunTemporaryProposalWithBoundedTtl()
    {
        var result = new FirewallDryRun(clock).Create(IPAddress.Parse("::ffff:203.0.113.9"), Window,
            NetworkAbuseReason.BroadcastAmplification, TimeSpan.FromDays(365), exclusiveSourceVerified: true);
        Assert.That(result.SocketPeer, Is.EqualTo("203.0.113.9"));
        Assert.That(result.ProposedTarget, Is.EqualTo("203.0.113.9"));
        Assert.That(result.SuggestedTtlSeconds, Is.EqualTo(900));
        Assert.That(result.Execute, Is.False);
    }

    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("0.0.0.0")]
    [TestCase("224.0.0.1")]
    [TestCase("10.0.0.1")]
    [TestCase("192.168.1.1")]
    public void LocalOrNonUnicastAddressesAreRecordOnly(string address)
    {
        var result = new FirewallDryRun(clock).Create(IPAddress.Parse(address), Window,
            NetworkAbuseReason.ConnectionBudget, TimeSpan.FromMinutes(1), exclusiveSourceVerified: true);
        Assert.That(result.ProposedTarget, Is.Null);
    }

    [Test]
    public void StructuredFieldsCannotCreateMultilineOutputOrSelectTargetFromText()
    {
        var input = "reference\n203.0.113.4; Remove-NetFirewallRule";
        var result = new FirewallDryRun(clock).Create(IPAddress.Parse("203.0.113.9"), Window,
            NetworkAbuseReason.HandshakeTimeout, TimeSpan.FromMinutes(1), evidenceReference: input);
        var json = FirewallDryRun.Serialize(result);
        Assert.That(json.Contains('\n'), Is.False);
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("EvidenceReference").GetString(), Is.EqualTo(input));
        Assert.That(document.RootElement.GetProperty("Execute").GetBoolean(), Is.False);
        Assert.That(document.RootElement.GetProperty("ProposedTarget").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void InvalidWindowOrTtlIsAValidationFaultAndDoesNotEmitAnEvent()
    {
        var output = new FirewallDryRun(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => output.Create(IPAddress.Parse("203.0.113.9"),
            Window with { Cost = -1 }, NetworkAbuseReason.ConnectionBudget, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => output.Create(IPAddress.Parse("203.0.113.9"),
            Window, NetworkAbuseReason.ConnectionBudget, TimeSpan.Zero));
    }

    [Test]
    public void ConcurrentOutputUsesUniqueServerEventIdsWithoutInternalQueue()
    {
        var output = new FirewallDryRun(clock);
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 1000, _ => ids.Add(output.Create(IPAddress.Parse("203.0.113.9"), Window,
            NetworkAbuseReason.ConnectionBudget, TimeSpan.FromMinutes(1)).EventId));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(1000));
    }
}
