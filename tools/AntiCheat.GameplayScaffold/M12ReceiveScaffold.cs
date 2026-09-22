using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Sockets;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private readonly object m12ReceiveGate = new();
    private Hook? m12ReceiveHook, m12AvailabilityHook;
    private ISocket? m12ReceiveSocket;
    private TSPlayer? m12OldActor;
    private Action? m12HeldCompletion;
    private string m12ReceiveMode = "none", m12ReceiveStage = "idle";
    private long m12ReceiveDeadline, m12ReceiveGeneration;
    private int m12OldAccount;
    private string? m12OldSource;
    private string? m12OldName, m12ReceiveArmId, m12ReceiveRequestId;
    private long m12ReceiveCommandRevision;
    private int m12ReceiveLength, m12OldSlot = -1;
    private string m12ReceiveHex = "";
    private bool m12ForceRead;
    private object? m12ReleaseBefore, m12ReleaseAfter;

    private void M12ReceiveCommand(string[] arguments)
    {
        Require(arguments.Length >= 1, "Use qa_m12_receive arm <actor> <data|eof|fault>, retire, release, or state.");
        switch (arguments[0])
        {
            case "arm":
                Require(arguments.Length is 3 or 4 && arguments[2] is "data" or "eof" or "fault", "Invalid receive-isolation arm.");
                var actor = ResolvePlayer(arguments[1]);
                Require(!actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc), "Ordinary authenticated SSC account required.");
                lock (m12ReceiveGate) Require(m12HeldCompletion is null && m12ReceiveStage is "idle" or "released" or "timeout-released", "Only one owned operation may be held.");
                InstallM12Receive();
                lock (m12ReceiveGate)
                {
                    m12OldActor = actor; m12OldSlot = actor.Index; m12ReceiveSocket = Netplay.Clients[actor.Index].Socket;
                    m12OldName = actor.Name; m12ReceiveArmId = arguments.Length == 4 ? arguments[3] : null;
                    m12OldAccount = actor.Account.ID; m12OldSource = m12ReceiveSocket.GetRemoteAddress()?.ToString();
                    m12ReceiveMode = arguments[2]; m12ReceiveStage = "armed"; m12ReceiveLength = 0;
                    m12ReceiveHex = ""; m12ReleaseBefore = null; m12ReleaseAfter = null;
                    m12ForceRead = m12ReceiveMode != "data";
                    m12ReceiveDeadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
                    m12ReceiveGeneration = ReceiveGeneration(Netplay.Clients[actor.Index], m12ReceiveSocket);
                }
                if (m12ForceRead) Netplay.Clients[actor.Index].TryRead();
                break;
            case "retire":
                lock (m12ReceiveGate) Require(m12ReceiveStage == "held" && m12OldActor is not null,
                    "An actual completed old receive must be held before retirement.");
                Require(ReferenceEquals(TShock.Players[m12OldSlot], m12OldActor) &&
                    ReferenceEquals(Netplay.Clients[m12OldSlot].Socket, m12ReceiveSocket), "Old account still owns the retiring binding.");
                // Existing TShock account disconnect triggers its ordinary Leave and native Reset.
                m12OldActor!.Disconnect("M12 owned receive-isolation retirement");
                break;
            case "fault":
                Require(m12ReceiveMode == "fault" && m12ReceiveStage == "armed", "Arm one real fault read first.");
                Require(CaptureM12Readiness().Pending, "The captured binding must own a real pending read before closing its socket.");
                m12ReceiveSocket!.Close();
                break;
            case "release": ReleaseM12Receive("released"); break;
            case "state": Require(arguments.Length is 1 or 2, "Invalid receive-isolation state request."); break;
            default: throw new ArgumentException("Unknown M12 receive command.");
        }
        lock (m12ReceiveGate)
        {
            m12ReceiveRequestId = arguments[0] == "state" && arguments.Length == 2 ? arguments[1] : null;
            m12ReceiveCommandRevision++;
        }
        WriteM12ReceiveState();
    }

    private static Type ReceiveType => typeof(ServerApi).Assembly.GetType("TerrariaApi.Server.Hooking.ReceiveIsolation", true)!;
    private static long ReceiveGeneration(RemoteClient client, ISocket socket) =>
        (long)ReceiveType.GetMethod("BindingGeneration", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [client, socket])!;

    private void InstallM12Receive()
    {
        if (m12ReceiveHook is not null) return;
        var completion = ReceiveType.GetMethod("SocketCompletion", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("M12 stable operation completion is absent.");
        m12ReceiveHook = new(completion, (Action<Action<object, int>, object, int>)HoldM12Completion);
        var map = typeof(LinuxTcpSocket).GetInterfaceMap(typeof(ISocket));
        int index = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(ISocket.IsDataAvailable));
        bool Available(Func<LinuxTcpSocket, bool> original, LinuxTcpSocket socket)
        {
            lock (m12ReceiveGate)
                if (ReferenceEquals(socket, m12ReceiveSocket) && m12ForceRead && m12ReceiveStage == "armed")
                    // ReceiveIsolation rechecks capacity and ownership after this query.
                    // Keep readiness for this one armed operation until actual completion;
                    // its own Reading reservation prevents duplicate submissions.
                    return true;
            return original(socket);
        }
        m12AvailabilityHook = new(map.TargetMethods[index], (Func<Func<LinuxTcpSocket, bool>, LinuxTcpSocket, bool>)Available);
    }

    private void HoldM12Completion(Action<object, int> original, object operation, int length)
    {
        // Read only the immutable operation fields introduced by the audited repair.
        // Neither current slot nor account is used to infer the old source.
        var binding = operation.GetType().GetField("Binding")!.GetValue(operation)!;
        var socket = (ISocket)binding.GetType().GetField("Socket")!.GetValue(binding)!;
        lock (m12ReceiveGate)
        {
            if (m12ReceiveStage == "armed" && ReferenceEquals(socket, m12ReceiveSocket))
            {
                m12HeldCompletion = () => original(operation, length);
                m12ReceiveLength = length;
                var bytes = (byte[])operation.GetType().GetField("Bytes")!.GetValue(operation)!;
                m12ReceiveHex = Convert.ToHexString(bytes.AsSpan(0, Math.Clamp(length, 0, 1024)));
                m12ReceiveStage = "held";
                return;
            }
        }
        original(operation, length);
    }

    private void ReleaseM12Receive(string stage)
    {
        Action? complete;
        lock (m12ReceiveGate)
        {
            complete = m12HeldCompletion; m12HeldCompletion = null; m12ForceRead = false;
            if (complete is null) { m12ReceiveStage = stage; return; }
            m12ReleaseBefore = CaptureM12Account();
        }
        complete();
        lock (m12ReceiveGate) { m12ReleaseAfter = CaptureM12Account(); m12ReceiveStage = stage; }
    }

    private object? CaptureM12Account()
    {
        if ((uint)m12OldSlot >= 255) return null;
        var actor = TShock.Players[m12OldSlot]; var client = Netplay.Clients[m12OldSlot];
        var plugin = M5Plugin();
        var bindings = plugin.GetType().GetField("_bindings", PrivateM5)?.GetValue(plugin) as Array;
        var binding = bindings?.GetValue(m12OldSlot);
        var session = binding?.GetType().GetProperty("Key")?.GetValue(binding);
        var engine = plugin.GetType().GetField("_engine", PrivateM5)?.GetValue(plugin);
        object? networkState = null;
        var network = plugin.GetType().GetField("_network", PrivateM5)?.GetValue(plugin);
        if (network is not null && session is not null)
        {
            var gate = network.GetType().GetField("gate", PrivateM5)!.GetValue(network)!;
            lock (gate)
            {
                var connection = (network.GetType().GetField("sessions", PrivateM5)!.GetValue(network) as Array)?.GetValue(m12OldSlot);
                object? Read(string name) => connection?.GetType().GetProperty(name)?.GetValue(connection);
                if (Equals(Read("Session"), session)) networkState = new { session = Read("Session"),
                    peer = Read("Peer")?.ToString(), bytes = Read("Bytes"), cost = Read("Cost") };
            }
        }
        return new { slot = m12OldSlot, actor = actor?.Name, account = actor?.Account?.ID,
            loggedIn = actor?.IsLoggedIn, sentInventory = actor?.HasSentInventory,
            socketIsOld = ReferenceEquals(client.Socket, m12ReceiveSocket),
            generation = client.Socket is null ? 0 : ReceiveGeneration(client, client.Socket), session,
            canWrite = session is null ? null : engine?.GetType().GetMethod("CanWrite")?.Invoke(engine, [session]),
            network = networkState,
            client.State, client.PendingTermination, client.PendingTerminationApproved, client._isReading,
            totalData = NetMessage.buffer[m12OldSlot].totalData,
            inventory = actor?.TPlayer.inventory.Select(item => new { item.type, item.stack, item.prefix }).ToArray(),
            sanctions = engine?.GetType().GetProperty("SanctionCount")?.GetValue(engine),
            source = "actual TShock account, SSC completion, Core binding and native state; no fixture authentication" };
    }

    private sealed record M12Readiness(bool BindingMatches, long Generation, bool Pending, int? Finished);
    private M12Readiness CaptureM12Readiness()
    {
        var type = ReceiveType;
        var gate = type.GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        lock (gate)
        {
            var slots = (Array)type.GetField("Slots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var binding = (uint)m12OldSlot < slots.Length ? slots.GetValue(m12OldSlot) : null;
            object? Read(string name) => binding?.GetType().GetField(name)!.GetValue(binding);
            bool matches = binding is not null && ReferenceEquals(Read("Client"), Netplay.Clients[m12OldSlot]) &&
                ReferenceEquals(Read("Socket"), m12ReceiveSocket) && ReferenceEquals(Netplay.Clients[m12OldSlot].Socket, m12ReceiveSocket) &&
                ReferenceEquals(Read("Buffer"), NetMessage.buffer[m12OldSlot]) && Read("Retired") is false && Read("Resetting") is false;
            var reading = Read("Reading");
            int? finished = reading is null ? null : (int)reading.GetType().GetField("Finished")!.GetValue(reading)!;
            bool pending = matches && reading is not null && finished == 0 &&
                ReferenceEquals(reading.GetType().GetField("Binding")!.GetValue(reading), binding);
            return new(matches, binding is null ? 0 : (long)Read("Generation")!, pending, finished);
        }
    }

    private void WriteM12ReceiveState()
    {
        object payload;
        lock (m12ReceiveGate) payload = new { utc = DateTimeOffset.UtcNow, mode = m12ReceiveMode,
            armId = m12ReceiveArmId, requestId = m12ReceiveRequestId, commandRevision = m12ReceiveCommandRevision,
            stage = m12ReceiveStage, oldSlot = m12OldSlot, oldAccount = m12OldAccount, oldSource = m12OldSource,
            oldActor = m12OldName, oldGeneration = m12ReceiveGeneration, receivedLength = m12ReceiveLength,
            readiness = CaptureM12Readiness(),
            receivedHex = m12ReceiveHex, before = m12ReleaseBefore, after = m12ReleaseAfter, current = CaptureM12Account(),
            snapshot = ReceiveType.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null),
            scope = "one completed real socket operation held at stable completion before product source guard; 30-second capacity-one TTL; console-only fixture" };
        WriteM16TimeoutSnapshot(Path.Combine(output!, "m12-receive-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void TickM12Receive()
    {
        bool timedOut;
        lock (m12ReceiveGate) timedOut = m12ReceiveStage is "armed" or "held" && Stopwatch.GetTimestamp() > m12ReceiveDeadline;
        if (timedOut) { ReleaseM12Receive("timeout-released"); WriteM12ReceiveState(); }
    }

    private void DisposeM12Receive()
    {
        ReleaseM12Receive("released");
        m12AvailabilityHook?.Dispose(); m12ReceiveHook?.Dispose();
        m12AvailabilityHook = null; m12ReceiveHook = null;
    }
}
