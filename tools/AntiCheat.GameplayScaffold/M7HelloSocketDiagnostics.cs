using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using TShockAPI.Sockets;

namespace CompatibilityAudit;

/// <summary>Fixed code boundaries, never player-supplied text or an authentication identity.</summary>
public enum RootConnectBoundary
{
    EarlierHandled, InvalidSlotOrMaintenance, MissingPlayer, ExistingPlayerRevoked,
    ExistingPlayerAccepted, SessionCapacityRejected, NetworkBudgetRejected, BindingAccepted
}

public sealed partial class M7ConnectionDiagnostics
{
    private readonly bool observeSocketCallbacks;
    private readonly List<Hook> socketHooks = new(4);
    private readonly List<SocketEntry> socketIdentities = new(Capacity);
    private long socketSequence, socketIdentityEvictions;
    private bool socketObserversInstalled, socketObserversWereInstalled;
    [ThreadStatic] private static SocketScope? CurrentSocketScope;
    private sealed record SocketScope(M7ConnectionDiagnostics Owner, ISocket Socket,
        RemoteClient? Client, string Phase);
    private sealed class SocketEntry(ISocket socket, long identity, DateTimeOffset observed)
    {
        public readonly ISocket Socket = socket;
        public readonly long Identity = identity;
        public DateTimeOffset Observed = observed;
        public long? FirstCloseOrder;
        public int? RemotePort;
    }

    private void InstallSocketObservers()
    {
        if (OutputDirectory() is null || socketObserversInstalled) return;
        if (typeof(Netplay).Assembly.GetName().Version != new Version(1, 4, 5, 8))
            throw new NotSupportedException("Socket observation requires audited Terraria 1.4.5.8.");
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var read = typeof(LinuxTcpSocket).GetMethod("ReadCallback", instance, [typeof(IAsyncResult)])
            ?? throw new MissingMethodException("Audited Linux read callback is absent.");
        var send = typeof(LinuxTcpSocket).GetMethod("SendCallback", instance, [typeof(IAsyncResult)])
            ?? throw new MissingMethodException("Audited Linux send callback is absent.");
        var socketMap = typeof(LinuxTcpSocket).GetInterfaceMap(typeof(ISocket));
        int closeIndex = Array.FindIndex(socketMap.InterfaceMethods, x => x.Name == nameof(ISocket.Close));
        if (closeIndex < 0) throw new MissingMethodException("Audited Linux close is absent.");
        var close = socketMap.TargetMethods[closeIndex];
        var accepted = typeof(Netplay).GetMethod(nameof(Netplay.OnConnectionAccepted),
            BindingFlags.Static | BindingFlags.Public, [typeof(ISocket)])
            ?? throw new MissingMethodException("Audited acceptance wrapper is absent.");
        try
        {
            // Hook wraps the public acceptance entry, including the pre-existing TSAPI callback
            // that replaces native acceptance. It does not invoke OriginalMethod or change its flag.
            socketHooks.Add(new(accepted, (Action<Action<ISocket>, ISocket>)AroundAccepted));
            socketHooks.Add(new(read, (Action<Action<LinuxTcpSocket, IAsyncResult>, LinuxTcpSocket, IAsyncResult>)AroundRead));
            socketHooks.Add(new(send, (Action<Action<LinuxTcpSocket, IAsyncResult>, LinuxTcpSocket, IAsyncResult>)AroundSend));
            socketHooks.Add(new(close, (Action<Action<LinuxTcpSocket>, LinuxTcpSocket>)AroundClose));
            socketObserversInstalled = true; socketObserversWereInstalled = true;
        }
        catch { DisposeSocketObservers(); throw; }
    }

    private void DisposeSocketObservers()
    {
        // A diagnostic detach failure is reported independently, not substituted for a native error.
        foreach (var hook in socketHooks.AsEnumerable().Reverse())
        {
            try { hook.Dispose(); }
            catch (Exception error) { Fail(error); }
        }
        socketHooks.Clear(); socketObserversInstalled = false;
    }

    public void ObserveRootConnect(int slot, RootConnectBoundary boundary) => Guard(() =>
    {
        if (!Enum.IsDefined(boundary) || (uint)slot >= Netplay.Clients.Length ||
            !Enabled(Netplay.Clients[slot])) return;
        lock (gate)
        {
            var client = Netplay.Clients[slot];
            Add(client, Observe(client, "root-connect"), "root-connect-" + boundary, null,
                "fixed-root-code-boundary-no-account-or-network-source-identity", client.Socket);
        }
    });

    private void AroundAccepted(Action<ISocket> original, ISocket socket)
    {
        var previous = CurrentSocketScope;
        CurrentSocketScope = new(this, socket, null, "connection-accepted");
        bool returned = false;
        try
        {
            RecordSocket(socket, null, "connection-accepted-entry", null, "before-entire-hook-chain");
            original(socket); returned = true;
        }
        finally
        {
            RecordSocket(socket, null, returned ? "connection-accepted-return" : "connection-accepted-unwind",
                null, "actual-socket-reference-sampled-after-entire-hook-chain");
            CurrentSocketScope = previous;
        }
    }

    private void AroundRead(Action<LinuxTcpSocket, IAsyncResult> original, LinuxTcpSocket socket, IAsyncResult result)
    {
        RemoteClient? target = null;
        Guard(() => target = M10ConnectionLifecycleDiagnostics.SubmittedReadTarget(result.AsyncState, socket));
        AroundCallback(original, socket, result, target, "linux-read");
    }

    private void AroundSend(Action<LinuxTcpSocket, IAsyncResult> original, LinuxTcpSocket socket, IAsyncResult result) =>
        AroundCallback(original, socket, result, null, "linux-send");

    private void AroundCallback(Action<LinuxTcpSocket, IAsyncResult> original, LinuxTcpSocket socket,
        IAsyncResult result, RemoteClient? client, string phase)
    {
        var previous = CurrentSocketScope;
        CurrentSocketScope = new(this, socket, client, phase);
        bool returned = false;
        try
        {
            RecordSocket(socket, client, phase + "-entry", null, "actual-callback-receiver-socket");
            original(socket, result); returned = true;
        }
        finally
        {
            RecordSocket(socket, client, phase + (returned ? "-return" : "-unwind"), null,
                "return-may-follow-native-catch-not-proof-of-success");
            CurrentSocketScope = previous;
        }
    }

    private void AroundClose(Action<LinuxTcpSocket> original, LinuxTcpSocket socket)
    {
        var previous = CurrentSocketScope;
        CurrentSocketScope = new(this, socket, null, "linux-close");
        bool returned = false;
        try
        {
            RecordSocket(socket, null, "linux-close-entry", null, "actual-close-receiver-first-attempt-marked", close: true);
            original(socket); returned = true;
        }
        finally
        {
            RecordSocket(socket, null, returned ? "linux-close-return" : "linux-close-unwind", null,
                "actual-close-return-does-not-prove-peer-received-fin");
            CurrentSocketScope = previous;
        }
    }

    private void RecordSocket(ISocket socket, RemoteClient? callbackTarget, string kind, int? value,
        string attribution, bool close = false) => Guard(() =>
    {
        if (OutputDirectory() is null) return;
        lock (gate)
        {
            var identity = SocketIdentity(socket);
            // Read callbacks carry the exact RemoteClient delegate target, even if its slot now
            // holds another Socket. Other rare boundaries use a fixed, at-most-256 reference scan.
            var client = callbackTarget;
            if (client is null)
                for (int i = 0; i < Math.Min(256, Netplay.Clients.Length); i++)
                    if (Netplay.Clients[i] is { } candidate && ReferenceEquals(candidate.Socket, socket))
                    { client = candidate; break; }
            if (client is not null && (uint)client.Id >= slots.Length) client = null;
            // Scope still surrounds EVERY real callback for first-chance attribution. Routine
            // post-handshake callbacks do not displace the bounded early-handshake history.
            if ((kind.StartsWith("linux-read-", StringComparison.Ordinal) || kind.StartsWith("linux-send-", StringComparison.Ordinal)) &&
                client is { State: > 1, PendingTermination: false, PendingTerminationApproved: false } &&
                ReferenceEquals(client.Socket, socket)) return;
            var state = client is null ? null : Observe(client, kind);
            var now = DateTimeOffset.UtcNow;
            PruneEventsAndMakeSpace(now);
            bool firstClose = close && identity.FirstCloseOrder is null;
            long order = ++sequence;
            if (firstClose) identity.FirstCloseOrder = order;
            string? stack = close ? MethodStack() : null;
            var currentIdentity = client?.Socket is null ? null : SocketIdentity(client.Socket);
            events.Enqueue(new(order, now, Environment.CurrentManagedThreadId, kind, client?.Id ?? -1,
                state?.Generation ?? 0, client?.State ?? -1, client?.PendingTermination ?? false,
                client?.PendingTerminationApproved ?? false, client?.Socket is null,
                close ? (firstClose ? 1 : 0) : value, attribution, identity.Identity,
                currentIdentity?.Identity,
                client is null ? null : ReferenceEquals(socket, client.Socket), stack,
                identity.RemotePort, currentIdentity?.RemotePort));
        }
    });

    private static string MethodStack()
    {
        // Method/type names only: no arguments, local variables, source filenames or messages.
        string stack = string.Join("\n", new StackTrace(false).GetFrames().Take(24)
            .Select(frame => frame.GetMethod()).Where(method => method is not null)
            .Select(method => method!.DeclaringType?.FullName + "." + method.Name));
        return stack.Length > 2048 ? stack[..2048] : stack;
    }

    private SocketEntry SocketIdentity(ISocket socket)
    {
        PruneSocketIdentities();
        var known = socketIdentities.FirstOrDefault(entry => ReferenceEquals(entry.Socket, socket));
        if (known is not null) { known.Observed = DateTimeOffset.UtcNow; return known; }
        if (socketIdentities.Count == Capacity)
        {
            var oldest = socketIdentities.MinBy(entry => entry.Observed)!;
            socketIdentities.Remove(oldest); socketIdentityEvictions++;
        }
        var entry = new SocketEntry(socket, ++socketSequence, DateTimeOffset.UtcNow);
        // TShock's audited Linux implementation returns its server-constructed TcpAddress.
        // Keep the peer port for transport correlation; never serialize an address or payload.
        if (socket is LinuxTcpSocket && socket.GetRemoteAddress() is TcpAddress peer &&
            peer.Port is > 0 and <= 65535) entry.RemotePort = peer.Port;
        socketIdentities.Add(entry); return entry;
    }

    private void PruneSocketIdentities()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        socketIdentityEvictions += socketIdentities.RemoveAll(entry => entry.Observed < cutoff);
    }

    private void PruneEventsAndMakeSpace(DateTimeOffset now)
    {
        while (events.Count > 0 && now - events.Peek().Utc > TimeSpan.FromMinutes(2))
        { events.Dequeue(); dropped++; }
        if (events.Count < Capacity) return;
        var retained = events.ToArray();
        int evict = Array.FindIndex(retained, item => !Critical(item));
        if (evict < 0) evict = 0;
        events.Clear();
        for (int i = 0; i < retained.Length; i++) if (i != evict) events.Enqueue(retained[i]);
        dropped++;
    }

    private static bool Critical(Event item) =>
        item.Kind.StartsWith("connection-accepted-", StringComparison.Ordinal) ||
        item.Kind == "linux-close-entry" && item.Value == 1 ||
        item.Kind.StartsWith("root-connect-", StringComparison.Ordinal) &&
            item.Kind is not "root-connect-BindingAccepted" and not "root-connect-ExistingPlayerAccepted";
}
