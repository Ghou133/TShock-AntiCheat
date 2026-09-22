using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.Sockets;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private sealed record M16TimeoutTarget(string Label, int Port, int Slot, object? Session,
        object? RootBinding, RemoteClient Client, ISocket Socket, TcpClient Connection, Socket AcceptedSocket)
    {
        public object? PreHelloRetirement { get; set; }
        public object? PreHelloFirstSeenAt { get; set; }
    }
    private readonly Dictionary<string, M16TimeoutTarget> m16TimeoutTargets = new(16);
    private string? m16TimeoutPreviousPassword;

    // Read-only observations in the existing isolated console gate. The separate fixed
    // password preparation only selects the genuine native password-wait branch.
    // No deadline, clock, provider, phase, traffic, ban, or connection flag is assigned.
    private void M16TimeoutCommand(string[] args)
    {
        Require(args.Length >= 1, "Use qa_m16_timeouts track <label> <owned-remote-port>, or state.");
        if (args[0] == "password-on")
        {
            Require(args.Length == 1 && m16TimeoutPreviousPassword is null, "One isolated password preparation at a time.");
            m16TimeoutPreviousPassword = Netplay.ServerPassword;
            Netplay.ServerPassword = "M16OwnedPasswordOnly";
        }
        else if (args[0] == "password-off")
        {
            Require(args.Length == 1 && m16TimeoutPreviousPassword is not null, "No owned password preparation to restore.");
            Netplay.ServerPassword = m16TimeoutPreviousPassword!; m16TimeoutPreviousPassword = null;
        }
        else if (args[0] == "track")
        {
            Require(args.Length == 3 && args[1].Length is > 0 and <= 32 && int.TryParse(args[2], out _), "Invalid owned transport label/port.");
            Require(m16TimeoutTargets.Count < 16 && !m16TimeoutTargets.ContainsKey(args[1]), "Sixteen fixed observations per run; labels are unique.");
            int port = int.Parse(args[2]); Require(port is > 0 and <= 65535, "Invalid TCP port.");
            var plugin = M5Plugin(); var bindings = (Array)plugin.GetType().GetField("_bindings", PrivateM5)!.GetValue(plugin)!;
            M16TimeoutTarget? target = null;
            for (int slot = 0; slot < Math.Min(256, bindings.Length); slot++)
            {
                var binding = bindings.GetValue(slot); var client = Netplay.Clients[slot];
                if (client?.Socket is not LinuxTcpSocket socket ||
                    socket.GetType() != typeof(LinuxTcpSocket) || ((ISocket)socket).GetRemoteAddress() is not TcpAddress address ||
                    address.Port != port || !System.Net.IPAddress.IsLoopback(address.Address)) continue;
                Require(target is null, "Owned remote port must identify exactly one accepted transport.");
                target = new(args[1], port, slot, binding?.GetType().GetProperty("Key")!.GetValue(binding),
                    binding, client, socket, socket._connection, socket._connection.Client);
            }
            Require(target is not null, "No live exact loopback transport has this remote port.");
            m16TimeoutTargets.Add(args[1], target!);
        }
        else Require(args.Length == 1 && args[0] == "state", "Unknown timeout observation command.");
        WriteM16TimeoutState();
    }

    private void WriteM16TimeoutState()
    {
        var plugin = M5Plugin(); var type = plugin.GetType();
        var bindings = (Array)type.GetField("_bindings", PrivateM5)!.GetValue(plugin)!;
        var network = type.GetField("_network", PrivateM5)!.GetValue(plugin)!;
        var queue = type.GetField("_timeoutRetirements", PrivateM5)?.GetValue(plugin);
        var preHello = type.GetField("_preHelloDeadlines", PrivateM5)?.GetValue(plugin);
        var isolation = typeof(TerrariaApi.Server.ServerApi).Assembly.GetType("TerrariaApi.Server.Hooking.ReceiveIsolation");
        object? Value(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);
        var rows = m16TimeoutTargets.Values.Select(target =>
        {
            if (target.RootBinding is null && target.PreHelloRetirement is null &&
                ReferenceEquals(target.Client.Socket, target.Socket) && ReferenceEquals(target.Connection.Client, target.AcceptedSocket))
            {
                target.PreHelloRetirement = preHello?.GetType().GetMethod("CaptureRetirement")!.Invoke(preHello, [target.Slot]);
                target.PreHelloFirstSeenAt = preHello?.GetType().GetMethod("CaptureFirstSeenAt")!.Invoke(preHello, [target.Slot]);
            }
            var retirement = Value(target.RootBinding, "TimeoutRetirement") ?? target.PreHelloRetirement;
            bool closed;
            try { closed = !target.AcceptedSocket.Connected; }
            catch (ObjectDisposedException) { closed = true; }
            return new
            {
                target.Label, target.Port, target.Slot, session = target.Session,
                phase = target.Session is null ? null : network.GetType().GetMethod("CapturePhase")!.Invoke(network, [target.Session]),
                nativeOnly = target.RootBinding is null, preHelloFirstSeenAt = target.PreHelloFirstSeenAt,
                rootBindingMatches = target.RootBinding is not null && ReferenceEquals(bindings.GetValue(target.Slot), target.RootBinding),
                rootBindingAtSlotNull = bindings.GetValue(target.Slot) is null,
                nativeClientMatches = ReferenceEquals(Netplay.Clients[target.Slot], target.Client),
                currentSocketMatches = ReferenceEquals(target.Client.Socket, target.Socket),
                nativeSocketNull = target.Client.Socket is null,
                nativeState = target.Client.State, target.Client.PendingTermination, target.Client.PendingTerminationApproved,
                target.Client.IsActive, actorAtSlotNull = TShock.Players[target.Slot] is null, capturedConnectionClosed = closed,
                capturedSocketStillInTcpClient = ReferenceEquals(target.Connection.Client, target.AcceptedSocket),
                timeoutRequested = Value(target.RootBinding, "TimeoutTerminationRequested") ?? Value(retirement, "Authorized") ?? false,
                authorized = Value(retirement, "Authorized") ?? false, finished = Value(retirement, "Finished") ?? false,
                closeAttempts = Value(retirement, "Attempts") ?? 0, failure = Value(retirement, "LastFailure")
            };
        }).ToArray();
        WriteM16TimeoutSnapshot(Path.Combine(output!, "m16-timeout-state-latest.json"), JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, timestamp = Stopwatch.GetTimestamp(), timestampFrequency = Stopwatch.Frequency,
            queue = Value(queue, "Snapshot"), observations = rows,
            preHello = new { installed = Value(preHello, "Healthy"), count = Value(preHello, "Count") },
            receive = isolation?.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null),
            passwordPreparationActive = m16TimeoutPreviousPassword is not null,
            scope = "read-only exact native/provider identity; pre-Hello account/session/phase are null; explicit isolated password preparation does not mutate timing, phases or termination"
        }, jsonOptions));
    }

    private static void WriteM16TimeoutSnapshot(string path, string json)
    {
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string previous = pending + ".bak";
        bool published = false;
        try
        {
            // The old file needs its own name while a Windows reader still holds it.
            // A reader may briefly miss the path during replacement and uses its existing bounded wait.
            File.WriteAllText(pending, json);
            if (File.Exists(path)) File.Replace(pending, path, previous);
            else File.Move(pending, path);
            published = true;
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
            // On a failed replacement retain any old snapshot backup for diagnosis.
            if (published && File.Exists(previous)) File.Delete(previous);
        }
    }

    private void DisposeM16TimeoutFixture()
    {
        if (m16TimeoutPreviousPassword is not null) Netplay.ServerPassword = m16TimeoutPreviousPassword;
        m16TimeoutPreviousPassword = null; m16TimeoutTargets.Clear();
    }
}
