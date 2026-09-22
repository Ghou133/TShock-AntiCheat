using System.Text.Json;
using CompatibilityAudit;
using NUnit.Framework;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.Localization;
using Terraria.Testing;
using TerrariaApi.Server.Hooking;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7NativeDiagnosticSocketPatchTests
{
    private const int Slot = 15;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldBuffer = null!;
    private bool oldDedicated, oldAssertions;
    private int oldMode;
    private TextWriter oldOut = null!;
    private StringWriter output = null!;
    private LanguageManager oldLanguage = null!;

    [SetUp]
    public void SetUp()
    {
        oldClient = Netplay.Clients[Slot]; oldBuffer = NetMessage.buffer[Slot];
        oldDedicated = Main.dedServ; oldMode = Main.netMode; oldAssertions = Invariant.assertionsEnabled;
        oldOut = Console.Out; output = new StringWriter(); Console.SetOut(output);
        Main.dedServ = true; Main.netMode = 2; Invariant.assertionsEnabled = false;
        oldLanguage = LanguageManager.Instance;
        LanguageManager.Instance = new LanguageManager();
        LanguageManager.Instance.LoadLanguage(GameCulture.FromName("en-US"));
        Assert.That(Language.GetTextValue("CLI.ClientWasBooted", "language-fixture-address", "reason"),
            Does.Contain("language-fixture-address"), "Load the exact target embedded language resources before asserting native logging.");
        Netplay.Clients[Slot] = new RemoteClient { Id = Slot, Socket = null! };
        NetMessage.buffer[Slot] = new MessageBuffer();
    }
    [TearDown]
    public void TearDown()
    {
        Console.SetOut(oldOut); output.Dispose(); Netplay.Clients[Slot] = oldClient; NetMessage.buffer[Slot] = oldBuffer;
        Main.dedServ = oldDedicated; Main.netMode = oldMode; Invariant.assertionsEnabled = oldAssertions;
        LanguageManager.Instance = oldLanguage;
    }

    [Test]
    public void NativeCheckBytesKeepsFirstErrorLoggingAndDiscardRecoveryWithoutSecondaryNullReference()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "anticheat-m7-patched-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            MalformedLength();
            Assert.Throws<NullReferenceException>(() => NetMessage.CheckBytes(Slot));
            MalformedLength(); output.GetStringBuilder().Clear();
            using (var patch = NativeDiagnosticSocketPatch.Install())
            using (var observer = new M6RuntimeDiagnostics(() => temporary))
            {
                observer.Install();
                Assert.DoesNotThrow(() => NetMessage.CheckBytes(Slot));
                Assert.That(NetMessage.buffer[Slot].totalData, Is.Zero, "Native catch still discards the malformed framing bytes.");
                Assert.That(output.ToString(), Does.Contain(nameof(IndexOutOfRangeException)).And.Contain("<socket-unavailable>"));
                Assert.That(output.ToString(), Does.Not.Contain(nameof(NullReferenceException)));
                using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary, "m6-first-runtime-error-01.json")));
                Assert.That(first.RootElement.GetProperty("exceptionType").GetString(), Is.EqualTo(typeof(IndexOutOfRangeException).FullName));
            }
            Retain("patched-checkbytes", new { firstErrorLogged = true, malformedBytesDiscarded = NetMessage.buffer[Slot].totalData == 0,
                nativeConsoleOutput = output.ToString(), secondaryNullReferenceRemoved = true, originalParseErrorFixed = false }, temporary);
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Test]
    public void NativeDisconnectWithoutSocketStillLogsAndMarksTerminationAndDisposalRestoresOriginalBody()
    {
        Assert.Throws<NullReferenceException>(() => NetMessage.SendData(2, Slot));
        Netplay.Clients[Slot].Kicked = false; output.GetStringBuilder().Clear();
        int sendAttempts = 0;
        void ObserveSend(object? _, HookEvents.Terraria.NetMessage.SendPacketEventArgs __) => sendAttempts++;
        HookEvents.Terraria.NetMessage.SendPacket += ObserveSend;
        try
        {
            using (var patch = NativeDiagnosticSocketPatch.Install())
            {
                Assert.DoesNotThrow(() => NetMessage.SendData(2, Slot));
                Assert.That(Netplay.Clients[Slot].Kicked, Is.True);
                Assert.That(Netplay.Clients[Slot].PendingTermination, Is.True);
                Assert.That(sendAttempts, Is.Zero, "Native IsConnected check still skips network output when the socket is absent.");
                Assert.That(output.ToString(), Does.Contain("<socket-unavailable>"));
                Retain("patched-senddata", new { Netplay.Clients[Slot].Kicked, Netplay.Clients[Slot].PendingTermination,
                    sendAttempts, nativeConsoleOutput = output.ToString(), secondaryNullReferenceRemoved = true,
                    historicalShutdownRootCauseProven = false });
            }
            Assert.Throws<NullReferenceException>(() => NetMessage.SendData(2, Slot), "Disposal restores the actual original native failure.");
        }
        finally { HookEvents.Terraria.NetMessage.SendPacket -= ObserveSend; }
    }

    [Test]
    public void ConnectedNativeDisconnectPreservesAddressAndActualSendBytes()
    {
        var socket = new FixtureSocket(); Netplay.Clients[Slot].Socket = socket;
        using var patch = NativeDiagnosticSocketPatch.Install();
        NetMessage.SendData(2, Slot);
        Assert.That(socket.AddressReads, Is.EqualTo(1));
        Assert.That(socket.Sent.Count, Is.EqualTo(1));
        Assert.That(socket.Sent[0][2], Is.EqualTo(2));
        Assert.That(output.ToString(), Does.Contain("fixture-peer-address").And.Not.Contain("<socket-unavailable>"));
        Assert.That(Netplay.Clients[Slot].PendingTermination, Is.True);
        Retain("patched-connected-control", new { socket.AddressReads, sentPackets = socket.Sent.Count,
            packetType = socket.Sent[0][2], Netplay.Clients[Slot].PendingTermination,
            scope = "actual-native-SendData-with-in-memory-ISocket-not-TCP", nativeConsoleOutput = output.ToString() });
    }

    [Test]
    public void EarlierCoreSendCancellationRemainsCanceled()
    {
        void Cancel(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        HookEvents.Terraria.NetMessage.SendData += Cancel;
        try
        {
            using var patch = NativeDiagnosticSocketPatch.Install();
            NetMessage.SendData(2, Slot);
            Assert.That(Netplay.Clients[Slot].Kicked, Is.False);
            Assert.That(Netplay.Clients[Slot].PendingTermination, Is.False);
            Assert.That(output.ToString(), Is.Empty);
        }
        finally { HookEvents.Terraria.NetMessage.SendData -= Cancel; }
    }

    [Test]
    public void AddressProviderFailureIsNotCaughtOrRelabelled()
    {
        var original = new IOException("fixture-address-provider-error");
        Netplay.Clients[Slot].Socket = new FixtureSocket { AddressError = original };
        using var patch = NativeDiagnosticSocketPatch.Install();
        var thrown = Assert.Throws<IOException>(() => NetMessage.LogMessageError(Slot, "1", new InvalidDataException("fixture-first-error")));
        Assert.That(thrown, Is.SameAs(original));
    }

    [Test]
    public void NativeClosedTcpSocketWithClearedAddressAlsoKeepsDisconnectState()
    {
        ISocket socket = new TcpSocket(); socket.Close(); Netplay.Clients[Slot].Socket = socket;
        Assert.That(socket.GetRemoteAddress(), Is.Null, "Use the actual target socket's cleared diagnostic address.");
        using var patch = NativeDiagnosticSocketPatch.Install();
        Assert.DoesNotThrow(() => NetMessage.SendData(2, Slot));
        Assert.That(output.ToString(), Does.Contain("<socket-unavailable>"));
        Assert.That(Netplay.Clients[Slot].PendingTermination, Is.True);
    }

    [Test]
    public void NativeHandshakePacketDoesNotQueryTheDiagnosticAddress()
    {
        var socket = new FixtureSocket { AddressError = new IOException("must-not-read-address-for-packet3") };
        Netplay.Clients[Slot].Socket = socket;
        using var patch = NativeDiagnosticSocketPatch.Install();
        Assert.DoesNotThrow(() => NetMessage.SendData(3, Slot));
        Assert.That(socket.AddressReads, Is.Zero);
        Assert.That(socket.Sent.Single()[2], Is.EqualTo(3));
        Assert.That(Netplay.Clients[Slot].PendingTermination, Is.False);
    }

    private static void MalformedLength()
    {
        NetMessage.buffer[Slot].checkBytes = true; NetMessage.buffer[Slot].totalData = 2;
        NetMessage.buffer[Slot].readBuffer[0] = 1; NetMessage.buffer[Slot].readBuffer[1] = 0;
    }
    private static void Retain(string caseName, object result, string? files = null)
    {
        string? root = Environment.GetEnvironmentVariable("ANTICHEAT_M7_DIAGNOSTIC_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        string directory = Path.Combine(root, caseName); Directory.CreateDirectory(directory);
        if (files is not null)
            foreach (string file in Directory.GetFiles(files, "*.json")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        string runtime = typeof(NetMessage).Assembly.Location;
        string patchAssembly = typeof(NativeDiagnosticSocketPatch).Assembly.Location;
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, caseName, source = "actual-locked-OTAPI-method-with-source-TSAPI-IL-adapter-installed",
            runtimePath = runtime, runtimeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runtime))),
            patchImplementationAssemblyPath = patchAssembly,
            patchImplementationAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(patchAssembly))),
            implementationCompiledFromLinkedTsapiSource = true, nativeMethodBodiesAdaptedInMemory = true,
            originalDllModified = false, result
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FixtureSocket : ISocket
    {
        public readonly List<byte[]> Sent = new();
        public int AddressReads;
        public Exception? AddressError;
        public void Close() { }
        public bool IsConnected() => true;
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) => Sent.Add(data.AsSpan(offset, size).ToArray());
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() { }
        public RemoteAddress GetRemoteAddress() { AddressReads++; if (AddressError is not null) throw AddressError; return new FixtureAddress(); }
    }
    private sealed class FixtureAddress : RemoteAddress
    {
        public override string ToString() => "fixture-peer-address";
        public override string GetIdentifier() => ToString();
        public override string GetFriendlyName() => ToString();
        public override bool IsLocalHost() => true;
    }
}
