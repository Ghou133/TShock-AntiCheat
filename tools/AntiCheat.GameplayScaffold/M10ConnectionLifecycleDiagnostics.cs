using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AntiCheat.Core;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using TShockAPI.Sockets;

namespace CompatibilityAudit;

/// <summary>Passive, bounded lab observations of actual method receivers. Diagnostic socket and
/// binding identities are not authenticated sessions. No operation is canceled, delayed or retried.</summary>
public sealed class M10ConnectionLifecycleDiagnostics(Func<string?> output) : IDisposable
{
    private const int SocketCapacity = 256, EventCapacity = 512;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);
    private readonly object gate = new();
    private readonly object flushGate = new();
    private readonly List<Hook> hooks = new(6);
    private readonly List<SocketRecord> sockets = new(SocketCapacity);
    private readonly Queue<LifecycleEvent> events = new(EventCapacity);
    private readonly SlotRecord?[] slots = new SlotRecord[256];
    private long order, socketSequence, generation, operationSequence, droppedEvents, evictedSockets;
    private int failed;
    private bool installed, wasInstalled;
    [ThreadStatic] private static Scope? currentScope;
    [ThreadStatic] private static bool observing;

    public sealed record Binding(int Slot, long Generation);
    public sealed record ClientState(int Slot, long Generation, long? SocketIdentity, int State,
        bool PendingTermination, bool PendingTerminationApproved, bool IsActive, bool MatchesCurrentSlotClient);
    public sealed record LifecycleEvent(long Order, DateTimeOffset Utc, long Timestamp, int Thread,
        string Kind, long Operation, long? ParentOperation, string? ParentBoundary,
        long? SourceSocketIdentity, int? SourceRemotePort, Binding? SourceBinding,
        ClientState? Current, bool? SourceMatchesCurrent, long? ResetOperation,
        long? ResetEntrySocketIdentity, bool? SourceMatchesResetEntry, int? Length,
        string? ExceptionType, int? HResult, int? SocketError, string? Stack, SessionKey? RootSession);
    private sealed class SlotRecord(RemoteClient client, ISocket? socket, long bindingGeneration)
    {
        public readonly RemoteClient Client = client;
        public readonly ISocket? Socket = socket;
        public readonly long Generation = bindingGeneration;
    }
    private sealed class SocketRecord(ISocket socket, long identity, DateTimeOffset now)
    {
        public readonly ISocket Socket = socket;
        public readonly long Identity = identity;
        public DateTimeOffset ObservedUtc = now;
        public int? RemotePort;
        public Binding? LastBinding;
        public RemoteClient? LastClient;
        public SessionKey? RootSession;
        public LifecycleEvent? FirstClose, FirstException;
        public LifecycleEvent? FirstForeignReadEntry, FirstForeignReadReturn;
    }
    private sealed record Scope(M10ConnectionLifecycleDiagnostics Owner, long Operation,
        string Boundary, ISocket? Source, RemoteClient? Client, long? EntrySocketIdentity, Scope? Parent);

    public void Install()
    {
        if (installed || DirectoryPath() is null) return;
        if (typeof(Netplay).Assembly.GetName().Version != new Version(1, 4, 5, 8))
            throw new NotSupportedException("Connection lifecycle observation requires audited Terraria 1.4.5.8.");
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var read = typeof(LinuxTcpSocket).GetMethod("ReadCallback", instance, [typeof(IAsyncResult)])
            ?? throw new MissingMethodException("Audited Linux read callback is absent.");
        var send = typeof(LinuxTcpSocket).GetMethod("SendCallback", instance, [typeof(IAsyncResult)])
            ?? throw new MissingMethodException("Audited Linux send callback is absent.");
        var map = typeof(LinuxTcpSocket).GetInterfaceMap(typeof(ISocket));
        int closeIndex = Array.FindIndex(map.InterfaceMethods, x => x.Name == nameof(ISocket.Close));
        if (closeIndex < 0) throw new MissingMethodException("Audited Linux close is absent.");
        var accepted = typeof(Netplay).GetMethod(nameof(Netplay.OnConnectionAccepted), [typeof(ISocket)])!;
        var reset = typeof(RemoteClient).GetMethod(nameof(RemoteClient.Reset), Type.EmptyTypes)!;
        var receive = typeof(RemoteClient).GetMethod(nameof(RemoteClient.ServerReadCallBack), [typeof(object), typeof(int)])!;
        try
        {
            hooks.Add(new(accepted, (Action<Action<ISocket>, ISocket>)AroundAccepted));
            hooks.Add(new(reset, (Action<Action<RemoteClient>, RemoteClient>)AroundReset));
            hooks.Add(new(read, (Action<Action<LinuxTcpSocket, IAsyncResult>, LinuxTcpSocket, IAsyncResult>)AroundRead));
            hooks.Add(new(send, (Action<Action<LinuxTcpSocket, IAsyncResult>, LinuxTcpSocket, IAsyncResult>)AroundSend));
            hooks.Add(new(map.TargetMethods[closeIndex], (Action<Action<LinuxTcpSocket>, LinuxTcpSocket>)AroundClose));
            hooks.Add(new(receive, (Action<Action<RemoteClient, object, int>, RemoteClient, object, int>)AroundReceive));
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            installed = true; wasInstalled = true;
        }
        catch { Detach(); throw; }
    }

    private void AroundAccepted(Action<ISocket> original, ISocket socket) =>
        Around("accept", socket, null, () => original(socket));

    /// <summary>Call only after root accepts its binding. This validates the exact current native
    /// object references; no account, name, UUID or client-provided identifier is inferred.</summary>
    public void ObserveRootBinding(SessionKey key, RemoteClient client, ISocket socket) => Guard(() =>
    {
        if (DirectoryPath() is null || client.Id != key.Slot || Netplay.Clients is not { } table ||
            (uint)key.Slot >= table.Length || !ReferenceEquals(table[key.Slot], client) ||
            !ReferenceEquals(client.Socket, socket)) return;
        lock (gate)
        {
            var source = Identity(socket); source.RootSession = key;
            var scope = new Scope(this, Interlocked.Increment(ref operationSequence), "root-binding",
                socket, client, source.Identity, currentScope);
            Record(scope, "root-binding");
        }
    });
    private void AroundReset(Action<RemoteClient> original, RemoteClient client) =>
        Around("reset", client.Socket, client, () => original(client));
    private void AroundRead(Action<LinuxTcpSocket, IAsyncResult> original, LinuxTcpSocket socket, IAsyncResult result)
    {
        RemoteClient? target = null;
        Guard(() => target = SubmittedReadTarget(result.AsyncState, socket));
        Around("linux-read", socket, target, () => original(socket, result));
    }
    internal static RemoteClient? SubmittedReadTarget(object? state, ISocket socket)
    {
        if (state is Tuple<SocketReceiveCallback, object> legacy) return legacy.Item1.Target as RemoteClient;
        // M12 provider captures the submitted Stream, Callback and State. Resolve only
        // this audited type and the immutable receive operation; never inspect the slot.
        if (state?.GetType().FullName != "TShockAPI.Sockets.LinuxTcpSocket+ReadOperation") return null;
        var callback = state.GetType().GetProperty("Callback")?.GetValue(state) as SocketReceiveCallback;
        if (callback?.Target is RemoteClient direct) return direct; // preserved historical direct-native fixture
        object? operation = state.GetType().GetProperty("State")?.GetValue(state);
        if (operation?.GetType().FullName != "TerrariaApi.Server.Hooking.ReceiveIsolation+Operation") return null;
        object? binding = operation.GetType().GetField("Binding")?.GetValue(operation);
        return binding is not null && ReferenceEquals(binding.GetType().GetField("Socket")?.GetValue(binding), socket)
            ? binding.GetType().GetField("Client")?.GetValue(binding) as RemoteClient : null;
    }
    private void AroundSend(Action<LinuxTcpSocket, IAsyncResult> original, LinuxTcpSocket socket, IAsyncResult result) =>
        Around("linux-send", socket, null, () => original(socket, result));
    private void AroundClose(Action<LinuxTcpSocket> original, LinuxTcpSocket socket) =>
        Around("linux-close", socket, null, () => original(socket));
    private void AroundReceive(Action<RemoteClient, object, int> original, RemoteClient client, object state, int length)
    {
        var read = FindScope("linux-read");
        // A direct/manual RemoteClient callback has no attributable originating socket.
        ISocket? source = read is not null && ReferenceEquals(read.Client, client) ? read.Source : null;
        Around("remote-read", source, client, () => original(client, state, length), length);
    }
    private void Around(string boundary, ISocket? source, RemoteClient? client, Action original, int? length = null)
    {
        var previous = currentScope;
        long? sourceIdentity = null;
        Guard(() => { lock (gate) { if (source is not null) sourceIdentity = Identity(source).Identity; } });
        var scope = new Scope(this, Interlocked.Increment(ref operationSequence), boundary, source, client, sourceIdentity, previous);
        currentScope = scope;
        bool returned = false;
        try
        {
            Record(scope, boundary + "-entry", length);
            // No diagnostic lock is held across the original hook chain or method body.
            original(); returned = true;
        }
        finally
        {
            Record(scope, boundary + (returned ? "-return" : "-unwind"), length);
            currentScope = previous;
        }
    }

    private void Record(Scope scope, string kind, int? length = null, Exception? error = null) => Guard(() =>
    {
        if (DirectoryPath() is null) return;
        lock (gate)
        {
            var source = scope.Source is null ? null : Identity(scope.Source);
            var client = scope.Client ?? source?.LastClient ?? FindClient(scope.Source);
            var current = Observe(client);
            bool? matches = source is null || client is null ? null : ReferenceEquals(source.Socket, client.Socket);
            bool routine = scope.Boundary is "linux-read" or "linux-send" or "remote-read";
            if (error is null && routine && length != 0 && matches == true && current is
                { State: > 1, PendingTermination: false, PendingTerminationApproved: false, MatchesCurrentSlotClient: true }) return;
            if (error is not null && (source is null || source.FirstException is not null)) return;
            var reset = FindScope("reset");
            var now = DateTimeOffset.UtcNow;
            Prune(now);
            var item = new LifecycleEvent(++order, now, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
                kind, scope.Operation, scope.Parent?.Owner == this ? scope.Parent.Operation : null,
                scope.Parent?.Owner == this ? scope.Parent.Boundary : null, source?.Identity, source?.RemotePort,
                source?.LastBinding, current, matches, reset?.Operation, reset?.EntrySocketIdentity,
                reset?.Source is null || source is null ? null : ReferenceEquals(reset.Source, source.Socket), length,
                error?.GetType().FullName, error?.HResult,
                (error as System.Net.Sockets.SocketException) is { } socketError ? (int)socketError.SocketErrorCode : null,
                error is not null ? MethodStack(error) : kind == "linux-close-entry" ? MethodStack() : null,
                source?.RootSession);
            if (events.Count == EventCapacity) { events.Dequeue(); droppedEvents++; }
            events.Enqueue(item);
            if (source is not null && kind == "linux-close-entry") source.FirstClose ??= item;
            if (source is not null && error is not null) source.FirstException = item;
            if (source is not null && (matches == false || current?.MatchesCurrentSlotClient == false))
            {
                if (kind == "remote-read-entry") source.FirstForeignReadEntry ??= item;
                if (kind == "remote-read-return") source.FirstForeignReadReturn ??= item;
            }
        }
    });

    private ClientState? Observe(RemoteClient? client)
    {
        if (client is null || (uint)client.Id >= slots.Length) return null;
        // Sampling a late delegate target must not install it over a replacement RemoteClient.
        int slot = client.Id;
        var table = Netplay.Clients;
        if (table is null || (uint)slot >= table.Length || !ReferenceEquals(table[slot], client))
            return new(slot, 0, client.Socket is null ? null : Identity(client.Socket).Identity, client.State,
                client.PendingTermination, client.PendingTerminationApproved, client.IsActive, false);
        var current = slots[slot];
        if (current is null || !ReferenceEquals(current.Client, client) || !ReferenceEquals(current.Socket, client.Socket))
            slots[slot] = current = new(client, client.Socket, ++generation);
        var socket = client.Socket is null ? null : Identity(client.Socket);
        if (socket is not null) { socket.LastBinding = new(slot, current.Generation); socket.LastClient = client; }
        return new(slot, current.Generation, socket?.Identity, client.State,
            client.PendingTermination, client.PendingTerminationApproved, client.IsActive, true);
    }
    private static RemoteClient? FindClient(ISocket? socket)
    {
        if (socket is null || Netplay.Clients is not { } clients) return null;
        for (int i = 0; i < Math.Min(256, clients.Length); i++)
            if (clients[i] is { } client && ReferenceEquals(client.Socket, socket)) return client;
        return null;
    }
    private Scope? FindScope(string boundary)
    {
        for (var scope = currentScope; scope is not null; scope = scope.Parent)
            if (scope.Owner == this && scope.Boundary == boundary) return scope;
        return null;
    }
    private SocketRecord Identity(ISocket socket)
    {
        var now = DateTimeOffset.UtcNow;
        Prune(now);
        var entry = sockets.FirstOrDefault(x => ReferenceEquals(x.Socket, socket));
        if (entry is not null) { entry.ObservedUtc = now; return entry; }
        if (sockets.Count == SocketCapacity)
        {
            var oldest = sockets.MinBy(x => x.ObservedUtc)!;
            sockets.Remove(oldest); evictedSockets++;
            for (int i = 0; i < slots.Length; i++)
                if (ReferenceEquals(slots[i]?.Socket, oldest.Socket)) slots[i] = null;
        }
        entry = new(socket, ++socketSequence, now);
        // LinuxTcpSocket._remoteAddress is server-constructed. Do not call arbitrary socket code.
        if (socket is LinuxTcpSocket { _remoteAddress: TcpAddress peer } && peer.Port is > 0 and <= 65535)
            entry.RemotePort = peer.Port;
        sockets.Add(entry); return entry;
    }
    private void Prune(DateTimeOffset now)
    {
        int removed = sockets.RemoveAll(x => now - x.ObservedUtc > Ttl);
        evictedSockets += removed;
        while (events.Count > 0 && now - events.Peek().Utc > Ttl) { events.Dequeue(); droppedEvents++; }
        if (removed > 0)
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] is { Socket: { } socket } && !sockets.Any(x => ReferenceEquals(x.Socket, socket))) slots[i] = null;
    }
    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (observing || Volatile.Read(ref failed) != 0 || currentScope?.Owner != this) return;
        Record(currentScope, "first-exception", error: args.Exception);
    }
    private static string MethodStack(Exception? error = null)
    {
        var trace = error is null ? new StackTrace(false) : new StackTrace(error, false);
        string value = string.Join("\n", trace.GetFrames().Take(24).Select(x => x.GetMethod())
            .Where(x => x is not null).Select(x => x!.DeclaringType?.FullName + "." + x.Name));
        return value.Length > 2048 ? value[..2048] : value;
    }
    public object Snapshot()
    {
        // Take the adapter's immutable counters outside the diagnostic lock. No socket,
        // reset, state mutation or user payload is involved in this read-only checkpoint.
        object? nativeAdmission = null;
        Guard(() => nativeAdmission = typeof(TerrariaApi.Server.ServerApi).Assembly
            .GetType("TerrariaApi.Server.Hooking.NetHooks")?
            .GetProperty("AdmissionSnapshot", BindingFlags.Static | BindingFlags.Public)?.GetValue(null));
        lock (gate)
        {
            Prune(DateTimeOffset.UtcNow);
            return new { schema = 1, installed, wasInstalled, observerFailed = Volatile.Read(ref failed) != 0,
                socketCapacity = SocketCapacity, eventCapacity = EventCapacity, ttlSeconds = (int)Ttl.TotalSeconds,
                stopwatchFrequency = Stopwatch.Frequency, order, droppedEvents, evictedSockets,
                identityScope = "actual-socket-object-and-observed-slot-binding-not-authenticated-session",
                overflowPolicy = "expire-120s-then-oldest-socket-or-event; first-close-error-and-foreign-read-pair-retained-per-live-socket",
                originalExecutionModified = false, rootCauseProven = false, nativeAdmission,
                sockets = sockets.Select(x => new { x.Identity, x.RemotePort, x.LastBinding, x.ObservedUtc,
                    x.RootSession, x.FirstClose, x.FirstException, x.FirstForeignReadEntry, x.FirstForeignReadReturn }).ToArray(), events = events.ToArray() };
        }
    }
    public void Flush()
    {
        var directory = DirectoryPath(); if (directory is null) return;
        Guard(() =>
        {
            // Explicit lab checkpoint only: never synchronous IO from a socket callback.
            lock (flushGate)
            {
                var snapshot = Snapshot();
                string pending = Path.Combine(directory, "m10-connection-lifecycle.pending");
                using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.Read))
                { JsonSerializer.Serialize(stream, snapshot); stream.Flush(true); }
                File.Move(pending, Path.Combine(directory, "m10-connection-lifecycle.json"), true);
            }
        });
    }
    private string? DirectoryPath()
    {
        if (Volatile.Read(ref failed) != 0) return null;
        string? value = null; Guard(() => value = output()); return value;
    }
    private void Guard(Action action)
    {
        if (Volatile.Read(ref failed) != 0) return;
        bool previous = observing; observing = true;
        try { action(); }
        catch (Exception error)
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
                try { Console.Error.WriteLine("M10_CONNECTION_LIFECYCLE_OBSERVER_FAILED " + error.GetType().Name); }
                catch (Exception) { /* Diagnostics cannot substitute for an observed native failure. */ }
        }
        finally { observing = previous; }
    }
    private void Detach()
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        foreach (var hook in hooks.AsEnumerable().Reverse())
            try { hook.Dispose(); }
            catch (Exception error)
            {
                Interlocked.Exchange(ref failed, 1);
                try { Console.Error.WriteLine("M10_CONNECTION_LIFECYCLE_DETACH_FAILED " + error.GetType().Name); }
                catch (Exception) { }
            }
        hooks.Clear(); installed = false;
    }
    public void Dispose()
    {
        if (!installed && hooks.Count == 0) return;
        Detach(); Flush();
        lock (gate) Array.Clear(slots);
    }
}
