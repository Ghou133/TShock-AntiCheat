using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class FirewallIpcTests
{
    [Test]
    public async Task RealNamedPipeAuthenticatesAndRecordsOnlyInMemory()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        var result = await Send(Command(clock, "127.0.0.1"), clock, executor);
        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(result.DryRun, Is.True);
            Assert.That(result.RecordedEvents, Is.EqualTo(1));
            Assert.That(result.SimulatedTargets, Is.Zero);
        });
    }

    [Test]
    public async Task SimulationIsTemporaryAndDuplicateEventsDoNotExtendIt()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        var command = Command(clock, "203.0.113.8", simulate: true) with
        {
            SourceEvent = Source(clock, "203.0.113.8", true) with { SuggestedTtlSeconds = 1 }
        };
        Assert.That((await Send(command, clock, executor)).Accepted, Is.True);
        Assert.That(executor.IsSimulatedBlocked("::ffff:203.0.113.8"), Is.True);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(executor.IsSimulatedBlocked("203.0.113.8"), Is.False);
        var duplicate = await Send(command with { RequestId = Guid.NewGuid() }, clock, executor);
        Assert.That(duplicate.Accepted, Is.True);
        Assert.That(duplicate.Reason, Is.EqualTo("duplicate-event"));
        Assert.That(duplicate.RecordedEvents, Is.EqualTo(1));
        Assert.That(executor.IsSimulatedBlocked("203.0.113.8"), Is.False);
    }

    [Test]
    public async Task WrongKeyIsRejectedByServerAndClientRejectsUnauthenticatedResponse()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        var key = RandomNumberGenerator.GetBytes(32); var wrong = RandomNumberGenerator.GetBytes(32);
        var name = PipeName(); using var server = new FirewallIpcServer(name, key, clock, executor);
        var serving = server.ServeOnceAsync();
        Assert.ThrowsAsync<InvalidDataException>(async () => await FirewallIpcClient.SendAsync(name, wrong, Command(clock), clock));
        var result = await serving;
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo("ipc-authentication-failed"));
    }

    [Test]
    public async Task NatProxyLoopbackAndPrivateTargetsCannotBeSimulatedBlocked()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        var ordinary = Command(clock, "203.0.113.8") with { Operation = FirewallIpcOperation.SimulateTemporaryBlock };
        Assert.That((await Send(ordinary, clock, executor)).Accepted, Is.False);
        foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "::1", "fe80::1" })
            Assert.That((await Send(Command(clock, address, true), clock, executor)).Accepted, Is.False, address);
        var proxy = Command(clock, "203.0.113.8", true);
        proxy = proxy with { SourceEvent = proxy.SourceEvent with { ProxyPeer = true, VerifiedClientAddress = "198.51.100.9" } };
        var result = await Send(proxy, clock, executor);
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo("proxy-target-forbidden"));
    }

    [Test]
    public async Task SignedMessagesStillCannotRequestOsExecutionShellTargetsOrArbitraryOperations()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        var command = Command(clock, "203.0.113.8", true);
        var bad = new[]
        {
            command with { SourceEvent = command.SourceEvent with { Execute = true } },
            command with { SourceEvent = command.SourceEvent with { DryRun = false } },
            command with { SourceEvent = command.SourceEvent with { ProposedTarget = "203.0.113.8; remove-all" } },
            command with { SourceEvent = command.SourceEvent with { ProposedTarget = "198.51.100.1" } },
            command with { SourceEvent = command.SourceEvent with { SuggestedTtlSeconds = 901 } },
            command with { Operation = (FirewallIpcOperation)99 }
        };
        foreach (var value in bad) Assert.That((await Send(value, clock, executor)).Accepted, Is.False);
    }

    [Test]
    public async Task EventIdConflictAndBoundedCapacitiesFailClosedWithoutChangingTargets()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock, 1, 1, TimeSpan.FromSeconds(2));
        var command = Command(clock);
        Assert.That((await Send(command, clock, executor)).Accepted, Is.True);
        var conflict = command with { SourceEvent = command.SourceEvent with { SuggestedTtlSeconds = 2 } };
        Assert.That((await Send(conflict, clock, executor)).Reason, Is.EqualTo("event-id-content-conflict"));
        Assert.That((await Send(Command(clock), clock, executor)).Reason, Is.EqualTo("event-capacity"));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.That((await Send(Command(clock), clock, executor)).Accepted, Is.True);
        var targets = new InMemoryFirewallExecutor(clock, 8, 1);
        Assert.That((await Send(Command(clock, "203.0.113.8", true), clock, targets)).Accepted, Is.True);
        Assert.That((await Send(Command(clock, "198.51.100.9", true), clock, targets)).Reason, Is.EqualTo("target-capacity"));
        Assert.That(targets.IsSimulatedBlocked("198.51.100.9"), Is.False);
    }

    [Test]
    public async Task SourceEventsAndWireRequestsMustBeFresh()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        var old = Command(clock);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That((await Send(old, clock, executor)).Reason, Is.EqualTo("source-event-expired-or-future"));
        var clientClock = new NetworkTestClock();
        var key = RandomNumberGenerator.GetBytes(32); var name = PipeName();
        using var server = new FirewallIpcServer(name, key, clock, executor);
        var serving = server.ServeOnceAsync();
        Assert.ThrowsAsync<InvalidDataException>(async () => await FirewallIpcClient.SendAsync(name, key, Command(clientClock), clientClock));
        Assert.That((await serving).Reason, Is.EqualTo("ipc-request-expired"));
    }

    [Test]
    public async Task OversizedLengthIsRejectedBeforeAllocatingTheClaimedBody()
    {
        var clock = new NetworkTestClock(); var key = RandomNumberGenerator.GetBytes(32); var name = PipeName();
        using var server = new FirewallIpcServer(name, key, clock, new(clock));
        var serving = server.ServeOnceAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(deadline.Token);
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, int.MaxValue);
        await client.WriteAsync(length, deadline.Token);
        var result = await serving;
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo("malformed-ipc-request"));
    }

    [Test]
    public async Task SlowPartialFrameAndMissingServerHaveBoundedTimeouts()
    {
        var clock = new NetworkTestClock(); var key = RandomNumberGenerator.GetBytes(32); var name = PipeName();
        using var server = new FirewallIpcServer(name, key, clock, new(clock), TimeSpan.FromMilliseconds(150));
        var serving = server.ServeOnceAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync();
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, 100);
        await client.WriteAsync(length);
        Assert.That((await serving).Reason, Is.EqualTo("ipc-timeout"));
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await FirewallIpcClient.SendAsync(PipeName(), key, Command(clock), clock, timeout: TimeSpan.FromMilliseconds(100)));
    }

    [Test]
    public void EndpointAndKeyAreRestrictedWithoutLoggingSecrets()
    {
        var clock = new NetworkTestClock(); var executor = new InMemoryFirewallExecutor(clock);
        Assert.Throws<ArgumentException>(() => new FirewallIpcServer("bad/remote", RandomNumberGenerator.GetBytes(32), clock, executor));
        Assert.Throws<ArgumentException>(() => new FirewallIpcServer("safe", new byte[16], clock, executor));
    }

    private static async Task<FirewallIpcResponse> Send(FirewallIpcCommand command, TimeProvider clock, InMemoryFirewallExecutor executor)
    {
        var key = RandomNumberGenerator.GetBytes(32); var name = PipeName();
        using var server = new FirewallIpcServer(name, key, clock, executor);
        var serving = server.ServeOnceAsync();
        var result = await FirewallIpcClient.SendAsync(name, key, command, clock);
        Assert.That(await serving, Is.EqualTo(result));
        CryptographicOperations.ZeroMemory(key);
        return result;
    }
    private static string PipeName() => "anticheat-test-" + Guid.NewGuid().ToString("N");
    private static FirewallIpcCommand Command(TimeProvider clock, string address = "127.0.0.1", bool simulate = false)
        => new(Guid.NewGuid(), simulate ? FirewallIpcOperation.SimulateTemporaryBlock : FirewallIpcOperation.RecordEvent, Source(clock, address, simulate));
    private static FirewallDryRunEvent Source(TimeProvider clock, string address, bool exclusive)
        => new FirewallDryRun(clock).Create(IPAddress.Parse(address), new(clock.GetUtcNow(), clock.GetUtcNow(), 1, 100, 4),
            NetworkAbuseReason.WeightedPacketCost, TimeSpan.FromSeconds(5), exclusiveSourceVerified: exclusive);
}
