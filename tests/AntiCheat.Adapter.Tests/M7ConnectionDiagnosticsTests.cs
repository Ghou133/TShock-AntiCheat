using System.Text.Json;
using CompatibilityAudit;
using NUnit.Framework;
using Terraria;
using Terraria.Net.Sockets;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7ConnectionDiagnosticsTests
{
    [Test]
    public void RealSocketReplacementStartsFreshObservationAndNeverAttributesUnidentifiedOldCallback()
    {
        string directory = Path.Combine(Path.GetTempPath(), "anticheat-m7-connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var old = Netplay.Clients[15];
        var socketA = new TcpSocket(); var socketB = new TcpSocket();
        try
        {
            var client = new RemoteClient { Id = 15, Socket = socketA, State = 1 };
            Netplay.Clients[15] = client;
            using var observer = new M7ConnectionDiagnostics(() => directory); observer.Install();
            var first = HookEvents.Terraria.RemoteClient.InvokeTryRead(client, () => { });
            using var before = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
            long firstGeneration = before.RootElement.GetProperty("slots")[0].GetProperty("Generation").GetInt64();
            Assert.That(first.ContinueExecution, Is.True);
            HookEvents.Terraria.RemoteClient.InvokeReset(client, () => { });
            client.Socket = socketB; client.State = 0;
            HookEvents.Terraria.RemoteClient.InvokeTryRead(client, () => { });
            var callback = HookEvents.Terraria.RemoteClient.InvokeServerReadCallBack(client, (_, _) => { }, null!, 0);
            using var after = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
            Assert.That(after.RootElement.GetProperty("slots")[0].GetProperty("Generation").GetInt64(), Is.GreaterThan(firstGeneration));
            Assert.That(after.RootElement.GetProperty("sourceSocketInReadCallbackAvailable").GetBoolean(), Is.False);
            Assert.That(after.RootElement.GetProperty("events").EnumerateArray().Last().GetProperty("Attribution").GetString(), Does.Contain("source-socket-unavailable"));
            Assert.That(callback.ContinueExecution, Is.True);
            Assert.That(client.PendingTermination, Is.False, "The observer must not run the callback or set termination itself.");
        }
        finally { Netplay.Clients[15] = old; ((ISocket)socketA).Close(); ((ISocket)socketB).Close(); Directory.Delete(directory, true); }
    }
    [Test]
    public void CompleteFramingMetadataRejectsPartialHeaderAndKeepsPayloadPrivate()
    {
        var old = Netplay.Clients[15]; var oldBuffer = NetMessage.buffer[15];
        bool enabled = true;
        try
        {
            Netplay.Clients[15] = new RemoteClient { Id = 15, Socket = null!, State = 0 };
            NetMessage.buffer[15] = new MessageBuffer();
            var buffer = NetMessage.buffer[15]; buffer.readBuffer[0] = 15; buffer.readBuffer[1] = 0;
            buffer.readBuffer[2] = 1; buffer.totalData = 3;
            using var observer = new M7ConnectionDiagnostics(() => enabled ? "enabled-no-write-in-this-fixture" : null); observer.Install();
            HookEvents.Terraria.NetMessage.InvokeCheckBytes(null!, _ => { }, 15);
            using var partial = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
            Assert.That(partial.RootElement.GetProperty("slots")[0].GetProperty("LastCompleteBufferedFrame").ValueKind, Is.EqualTo(JsonValueKind.Null));
            buffer.totalData = 15;
            HookEvents.Terraria.NetMessage.InvokeCheckBytes(null!, _ => { }, 15);
            using var complete = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
            Assert.That(complete.RootElement.GetProperty("slots")[0].GetProperty("LastCompleteBufferedFrame").GetProperty("Packet").GetInt32(), Is.EqualTo(1));
            Assert.That(complete.RootElement.GetProperty("slots")[0].GetProperty("lastFrameMeaning").GetString(), Does.Contain("not-successful-game-processing"));
            enabled = false;
        }
        finally { Netplay.Clients[15] = old; NetMessage.buffer[15] = oldBuffer; }
    }
    [Test]
    public void TransitionOverflowIsBoundedAndOriginalCancellationIsUntouched()
    {
        var old = Netplay.Clients[15];
        bool enabled = true;
        try
        {
            var client = new RemoteClient { Id = 15, Socket = null! }; Netplay.Clients[15] = client;
            using var observer = new M7ConnectionDiagnostics(() => enabled ? "enabled-no-write-in-this-fixture" : null); observer.Install();
            for (int i = 0; i < 700; i++)
            {
                client.State = i % 2;
                var args = HookEvents.Terraria.RemoteClient.InvokeTryRead(client, () => { });
                Assert.That(args.ContinueExecution, Is.True);
            }
            using var result = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
            Assert.That(result.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(512));
            Assert.That(result.RootElement.GetProperty("dropped").GetInt64(), Is.GreaterThan(0));
            enabled = false;
        }
        finally { Netplay.Clients[15] = old; }
    }
    [Test]
    public void ThrowingDiagnosticProviderLatchesOnceAndDoesNotReplaceNativeCallbacks()
    {
        int calls = 0; var priorError = Console.Error; using var errorOutput = new StringWriter();
        try
        {
            Console.SetError(errorOutput);
            using var observer = new M7ConnectionDiagnostics(() =>
            { calls++; throw new IOException("credentials-must-not-be-logged"); });
            observer.Install();
            var client = new RemoteClient { Id = 15, Socket = null! };
            for (int i = 0; i < 20; i++)
            {
                var args = HookEvents.Terraria.RemoteClient.InvokeTryRead(client, () => { });
                Assert.That(args.ContinueExecution, Is.True);
            }
            using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
            Assert.That(snapshot.RootElement.GetProperty("observerFailed").GetBoolean(), Is.True);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(client.PendingTermination, Is.False);
            Assert.That(errorOutput.ToString().Trim(), Is.EqualTo("M7_CONNECTION_DIAGNOSTIC_WRITE_FAILED IOException"));
            Assert.That(errorOutput.ToString(), Does.Not.Contain("credentials-must-not-be-logged"));
            string? retained = Environment.GetEnvironmentVariable("ANTICHEAT_M7_DIAGNOSTIC_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(retained))
            {
                Directory.CreateDirectory(retained);
                File.WriteAllText(Path.Combine(retained, "expected-observer-provider-failure.json"), JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, source = "explicit-observer-output-provider-fault-fixture",
                    expectedDiagnosticFailure = true, callbackAttempts = 20, providerCalls = calls,
                    boundedErrorOutput = errorOutput.ToString().Trim(), snapshot = observer.Snapshot()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { Console.SetError(priorError); }
    }
}
