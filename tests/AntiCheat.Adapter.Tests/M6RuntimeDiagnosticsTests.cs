using System.Text.Json;
using CompatibilityAudit;
using NUnit.Framework;
using Terraria;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M6RuntimeDiagnosticsTests
{
    [Test]
    public void OriginalCaughtExceptionIsPersistedBeforeLoggerFailsAndCaptureIsBounded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "anticheat-m6-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool priorDedicated = Main.dedServ; var oldClient = Netplay.Clients[15];
        try
        {
            Main.dedServ = true; Netplay.Clients[15] = new RemoteClient { Socket = null! };
            using var diagnostics = new M6RuntimeDiagnostics(() => directory); diagnostics.Install();
            Exception original;
            try { throw new InvalidDataException("private fixture message must not enter diagnostic output"); }
            catch (Exception error) { original = error; }
            for (int i = 0; i < 2; i++)
                Assert.Throws<NullReferenceException>(() => NetMessage.LogMessageError(15, "fixture", original));
            Assert.That(diagnostics.Entries, Is.EqualTo(1), "Identical later logger failures must not displace distinct first errors.");
            for (int i = 0; i < 12; i++)
                Assert.Throws<NullReferenceException>(() => NetMessage.LogMessageError(15, "fixture" + i, original),
                    "Observation must leave the failing logger and its original exception behavior unchanged.");
            Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(8));
            using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "m6-first-runtime-error-01.json")));
            Assert.That(first.RootElement.GetProperty("exceptionType").GetString(), Is.EqualTo(typeof(InvalidDataException).FullName));
            Assert.That(first.RootElement.GetProperty("socketMissing").GetBoolean(), Is.True);
            Assert.That(first.RootElement.GetProperty("ContinueExecution").GetBoolean(), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(directory, "m6-first-runtime-error-01.json")), Does.Not.Contain("private fixture message"));
        }
        finally { Main.dedServ = priorDedicated; Netplay.Clients[15] = oldClient; Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void NoValidatedLabOutputMeansNoCaptureAndCoreCancellationIsUntouched()
    {
        using var diagnostics = new M6RuntimeDiagnostics(() => null); diagnostics.Install();
        var args = HookEvents.Terraria.NetMessage.InvokeLogMessageError(null, (_, _, _) => { }, 15, "x", new IOException());
        Assert.That(args.ContinueExecution, Is.True); Assert.That(diagnostics.Entries, Is.Zero);
    }

    [Test]
    public void ActualNativeCheckBytesPreservesFirstParseErrorBeforeMissingSocketLoggerFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), "anticheat-m7-native-checkbytes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool priorDedicated = Main.dedServ; var oldClient = Netplay.Clients[15]; var oldBuffer = NetMessage.buffer[15];
        try
        {
            Main.dedServ = true; Netplay.Clients[15] = new RemoteClient { Id = 15, Socket = null! };
            NetMessage.buffer[15] = new MessageBuffer { checkBytes = true, totalData = 2 };
            NetMessage.buffer[15].readBuffer[0] = 1; // Native framing rejects length 1 before any GetData dispatch.
            using var diagnostics = new M6RuntimeDiagnostics(() => directory); diagnostics.Install();
            Assert.Throws<NullReferenceException>(() => NetMessage.CheckBytes(15),
                "The observer preserves the actual native logger failure, rather than manufacturing a clean exit.");
            using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "m6-first-runtime-error-01.json")));
            Assert.That(first.RootElement.GetProperty("exceptionType").GetString(), Is.EqualTo(typeof(IndexOutOfRangeException).FullName));
            Assert.That(first.RootElement.GetProperty("messageId").GetString(), Is.EqualTo("?"));
            Assert.That(first.RootElement.GetProperty("stack").GetString(), Does.Contain("mfwh_CheckBytes"));
            Assert.That(first.RootElement.GetProperty("connectionMetadata").GetProperty("stateTransitionsAreSampled").GetBoolean(), Is.True);
            // Optional directed run output, separate from default disposable fixtures. Never copies packet bodies.
            string? retained = Environment.GetEnvironmentVariable("ANTICHEAT_M7_DIAGNOSTIC_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(retained))
            {
                Directory.CreateDirectory(retained);
                foreach (string file in Directory.GetFiles(directory, "*.json"))
                    File.Copy(file, Path.Combine(retained, Path.GetFileName(file)), true);
                string runtime = typeof(NetMessage).Assembly.Location;
                File.WriteAllText(Path.Combine(retained, "native-checkbytes-provenance.json"), JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, source = "actual-target-native-method-with-constructed-invalid-length-fixture",
                    test = nameof(ActualNativeCheckBytesPreservesFirstParseErrorBeforeMissingSocketLoggerFails),
                    runtimePath = runtime, runtimeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runtime))),
                    originalException = typeof(IndexOutOfRangeException).FullName,
                    externallyThrownException = typeof(NullReferenceException).FullName,
                    historical050314529ZRootCauseProven = false, observedExceptionsSuppressed = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            Main.dedServ = priorDedicated; Netplay.Clients[15] = oldClient; NetMessage.buffer[15] = oldBuffer;
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void ActualNativeDisconnectSendKeepsItsDistinctFailureAndPreFailureSideEffectVisible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "anticheat-m7-native-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool priorDedicated = Main.dedServ; int priorMode = Main.netMode;
        var oldClient = Netplay.Clients[15]; var oldBuffer = NetMessage.buffer[15];
        try
        {
            Main.dedServ = true; Main.netMode = 2;
            Netplay.Clients[15] = new RemoteClient { Id = 15, Socket = null! };
            NetMessage.buffer[15] = new MessageBuffer();
            using var diagnostics = new M6RuntimeDiagnostics(() => directory); diagnostics.Install();
            Assert.Throws<NullReferenceException>(() => NetMessage.SendData(2, 15),
                "Native disconnect formatting failure remains visible to the caller.");
            Assert.That(Netplay.Clients[15].Kicked, Is.True,
                "The real packet2 branch writes Kicked before its socket-dependent logging fails.");
            using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "m6-first-send-error-01.json")));
            Assert.That(first.RootElement.GetProperty("kind").GetString(), Is.EqualTo("first-chance-native-SendData-null"));
            Assert.That(first.RootElement.GetProperty("stack").GetString(), Does.Contain("mfwh_orig_SendData"));
            Assert.That(first.RootElement.GetProperty("contextIsExactTarget").GetBoolean(), Is.False);
            Assert.That(Directory.GetFiles(directory, "m6-first-runtime-error-*.json"), Is.Empty,
                "A SendData error is not relabelled as a CheckBytes/LogMessageError incident.");
            string? retained = Environment.GetEnvironmentVariable("ANTICHEAT_M7_DIAGNOSTIC_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(retained))
            {
                retained = Path.Combine(retained, "native-senddata"); Directory.CreateDirectory(retained);
                foreach (string file in Directory.GetFiles(directory, "*.json"))
                    File.Copy(file, Path.Combine(retained, Path.GetFileName(file)), true);
                string runtime = typeof(NetMessage).Assembly.Location;
                File.WriteAllText(Path.Combine(retained, "native-senddata-provenance.json"), JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, source = "actual-target-native-method-with-constructed-missing-socket-fixture",
                    test = nameof(ActualNativeDisconnectSendKeepsItsDistinctFailureAndPreFailureSideEffectVisible),
                    runtimePath = runtime, runtimeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runtime))),
                    packet = 2, socketMissing = true, kickedAfterException = Netplay.Clients[15].Kicked,
                    externallyThrownException = typeof(NullReferenceException).FullName,
                    historical045204834ZRootCauseProven = false, observedExceptionsSuppressed = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            Main.dedServ = priorDedicated; Main.netMode = priorMode;
            Netplay.Clients[15] = oldClient; NetMessage.buffer[15] = oldBuffer;
            Directory.Delete(directory, true);
        }
    }
}
