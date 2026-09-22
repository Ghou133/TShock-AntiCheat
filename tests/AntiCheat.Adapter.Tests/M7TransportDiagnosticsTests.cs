using System.Net.Sockets;
using System.Text.Json;
using NUnit.Framework;

namespace AntiCheat.Adapter.Tests;

[TestFixture]
public sealed class M7TransportDiagnosticsTests
{
    [Test]
    public void SuccessfulFrameMetadataAndLocalCloseAreSeparatedFromPeerAcceptance()
    {
        var trace = new M7TransportDiagnostics(); trace.Phase("hello-sent");
        trace.CompleteWrite(1, 15); trace.CompleteRead(3, 4); trace.Close("local-dispose");
        trace.Failure(new OperationCanceledException("private credentials"), "read", true);
        var json = JsonSerializer.Serialize(trace.Snapshot()); using var parsed = JsonDocument.Parse(json);
        Assert.That(parsed.RootElement.GetProperty("firstClose").GetProperty("initiator").GetString(), Is.EqualTo("local-dispose"));
        Assert.That(parsed.RootElement.GetProperty("lastRead").GetProperty("packet").GetInt32(), Is.EqualTo(3));
        Assert.That(json, Does.Not.Contain("private credentials"));
    }
    [Test]
    public void ResetMetadataPreservesFirstFailureAndEachTransportHasItsOwnGeneration()
    {
        var first = new M7TransportDiagnostics(); var second = new M7TransportDiagnostics();
        first.Failure(new IOException("secret", new SocketException((int)SocketError.ConnectionReset)), "read", false);
        first.Failure(new ObjectDisposedException("secret"), "dispose", true);
        for (int i = 0; i < 100; i++) first.Phase("bounded-phase");
        using var one = JsonDocument.Parse(JsonSerializer.Serialize(first.Snapshot()));
        using var two = JsonDocument.Parse(JsonSerializer.Serialize(second.Snapshot()));
        Assert.That(one.RootElement.GetProperty("firstException").GetProperty("socketError").GetInt32(), Is.EqualTo((int)SocketError.ConnectionReset));
        Assert.That(one.RootElement.GetProperty("transitions").GetArrayLength(), Is.EqualTo(32));
        Assert.That(one.RootElement.GetProperty("dropped").GetInt32(), Is.EqualTo(68));
        Assert.That(one.RootElement.GetProperty("connectionGeneration").GetInt64(), Is.Not.EqualTo(two.RootElement.GetProperty("connectionGeneration").GetInt64()));
    }
}
