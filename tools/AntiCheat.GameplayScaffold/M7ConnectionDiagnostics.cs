using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Terraria;
using TerrariaApi.Server;

namespace CompatibilityAudit;

/// <summary>Passive isolated-lab metadata. Observed socket generations are diagnostic identities,
/// not authenticated sessions. No packet payloads, names, UUIDs or exception messages are retained.</summary>
public sealed partial class M7ConnectionDiagnostics : IDisposable
{
    private const int Capacity = 512;
    private readonly Func<string?> output;
    private readonly TerrariaPlugin? registrator;
    private readonly object gate = new();
    private readonly SlotState?[] slots = new SlotState[256];
    private readonly Queue<Event> events = new(Capacity);
    private readonly List<object> firstExceptions = new(8);
    private long sequence, generation, dropped;
    private bool installed;
    private int observerFailed;
    [ThreadStatic] private static bool observingException;
    private sealed class SlotState
    {
        public object? Socket;
        public long Generation;
        public int State;
        public bool Pending, Approved;
        public Frame? LastCompleteBufferedFrame;
        public int? LastSendType;
        public long LastSendOrder;
    }
    private sealed record Frame(int Packet, int Length, long Order);
    private sealed record Event(long Order, DateTimeOffset Utc, int Thread, string Kind, int Slot,
        long Generation, int State, bool Pending, bool Approved, bool SocketMissing, int? Value,
        string Attribution, long? SourceSocketIdentity = null, long? CurrentSocketIdentity = null,
        bool? SourceMatchesCurrent = null, string? Stack = null, int? SourceRemotePort = null,
        int? CurrentRemotePort = null);

    public M7ConnectionDiagnostics(Func<string?> output, TerrariaPlugin? registrator = null,
        bool observeSocketCallbacks = false)
    { this.output = output; this.registrator = registrator; this.observeSocketCallbacks = observeSocketCallbacks; }
    public void Install()
    {
        if (installed) return;
        HookEvents.Terraria.RemoteClient.Reset += OnReset;
        HookEvents.Terraria.RemoteClient.TryRead += OnTryRead;
        HookEvents.Terraria.RemoteClient.ServerReadCallBack += OnReadCallback;
        HookEvents.Terraria.NetMessage.CheckBytes += OnCheckBytes;
        HookEvents.Terraria.NetMessage.SendData += OnSend;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        if (registrator is not null)
        {
            ServerApi.Hooks.ServerConnect.Register(registrator, OnConnectBefore, 3000);
            ServerApi.Hooks.ServerConnect.Register(registrator, OnConnectAfter, -3000);
        }
        installed = true;
        if (observeSocketCallbacks) Guard(InstallSocketObservers);
    }
    private SlotState Observe(RemoteClient client, string boundary)
    {
        var state = slots[client.Id] ??= new SlotState();
        bool socketChanged = !ReferenceEquals(state.Socket, client.Socket);
        if (socketChanged)
        {
            state.Socket = client.Socket;
            // A new non-null actual Socket reference, not a reused RemoteClient or a client field.
            if (client.Socket is not null) state.Generation = ++generation;
            state.LastCompleteBufferedFrame = null; state.LastSendType = null; state.LastSendOrder = 0;
        }
        if (socketChanged || state.State != client.State || state.Pending != client.PendingTermination ||
            state.Approved != client.PendingTerminationApproved)
        {
            state.State = client.State; state.Pending = client.PendingTermination;
            state.Approved = client.PendingTerminationApproved;
            Add(client, state, "state-observed-at-" + boundary, null, "sampled-transition-not-first-setter");
        }
        return state;
    }
    private void Add(RemoteClient client, SlotState state, string kind, int? value, string attribution,
        Terraria.Net.Sockets.ISocket? source = null)
    {
        var now = DateTimeOffset.UtcNow;
        PruneEventsAndMakeSpace(now);
        var sourceIdentity = source is null ? null : SocketIdentity(source);
        var currentIdentity = client.Socket is null ? null : SocketIdentity(client.Socket);
        events.Enqueue(new(++sequence, now, Environment.CurrentManagedThreadId, kind, client.Id,
            state.Generation, client.State, client.PendingTermination, client.PendingTerminationApproved,
            client.Socket is null, value, attribution,
            sourceIdentity?.Identity, currentIdentity?.Identity,
            source is null ? null : ReferenceEquals(source, client.Socket), null,
            sourceIdentity?.RemotePort, currentIdentity?.RemotePort));
    }
    private bool Enabled(RemoteClient? client) => OutputDirectory() is not null && client is not null &&
        (uint)client.Id < slots.Length;
    private void OnReset(RemoteClient client, HookEvents.Terraria.RemoteClient.ResetEventArgs args) => Guard(() =>
    {
        if (!Enabled(client)) return;
        lock (gate) Add(client, Observe(client, "reset"), "native-reset-entry", null,
            "server-reset-attempt-core-cancellation-unchanged");
    });
    private void OnTryRead(RemoteClient client, HookEvents.Terraria.RemoteClient.TryReadEventArgs args) => Guard(() =>
    {
        if (!Enabled(client)) return;
        lock (gate) Observe(client, "try-read");
    });
    private void OnReadCallback(RemoteClient client, HookEvents.Terraria.RemoteClient.ServerReadCallBackEventArgs args) => Guard(() =>
    {
        if (!Enabled(client)) return;
        lock (gate)
        {
            var state = Observe(client, "read-callback");
            // Only the enclosing actual Linux callback supplies the originating Socket. An
            // unrelated/manual RemoteClient callback still has no source attribution.
            var scope = CurrentSocketScope;
            bool exact = scope?.Owner == this && ReferenceEquals(scope.Client, client) && scope.Phase == "linux-read";
            if (args.length == 0 || client.State <= 1)
                Add(client, state, "read-callback-entry", args.length,
                    exact ? "actual-linux-read-source-socket-and-callback-target" :
                    "callback-source-socket-unavailable-current-slot-snapshot-only", exact ? scope!.Socket : null);
        }
    });
    private void OnConnectBefore(ConnectEventArgs args) => ObserveConnect(args, "server-connect-before");
    private void OnConnectAfter(ConnectEventArgs args) => ObserveConnect(args, "server-connect-after");
    private void ObserveConnect(ConnectEventArgs args, string boundary) => Guard(() =>
    {
        if ((uint)args.Who >= Netplay.Clients.Length || !Enabled(Netplay.Clients[args.Who])) return;
        lock (gate)
        {
            var client = Netplay.Clients[args.Who];
            Add(client, Observe(client, boundary), boundary, args.Handled ? 1 : 0,
                "handled-at-observer-priority-not-specific-handler-reason");
        }
    });
    private void OnCheckBytes(object? sender, HookEvents.Terraria.NetMessage.CheckBytesEventArgs args) => Guard(() =>
    {
        int slot = args.bufferIndex;
        if ((uint)slot >= slots.Length || (uint)slot >= Netplay.Clients.Length ||
            (uint)slot >= NetMessage.buffer.Length || !Enabled(Netplay.Clients[slot])) return;
        var client = Netplay.Clients[slot]; var buffer = NetMessage.buffer[slot];
        // Same buffer monitor used by native CheckBytes/ReceiveBytes; never wait in an observer.
        if (buffer is null || !Monitor.TryEnter(buffer)) return;
        try
        {
            lock (gate)
            {
                var state = Observe(client, "check-bytes");
                if (buffer.totalData < 3 || buffer.totalData > buffer.readBuffer.Length) return;
                int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.readBuffer);
                if (length < 3 || length > buffer.totalData) return;
                int packet = buffer.readBuffer[2];
                state.LastCompleteBufferedFrame = new(packet, length, ++sequence);
                if (packet == 1 && client.State <= 1)
                    Add(client, state, "complete-buffered-hello-header", length,
                        "framing-only-payload-not-read-parser-acceptance-not-proven");
            }
        }
        finally { Monitor.Exit(buffer); }
    });
    private void OnSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => Guard(() =>
    {
        int slot = args.remoteClient;
        if ((uint)slot >= slots.Length || (uint)slot >= Netplay.Clients.Length || !Enabled(Netplay.Clients[slot])) return;
        lock (gate)
        {
            var client = Netplay.Clients[slot]; var state = Observe(client, "send");
            state.LastSendType = args.msgType; state.LastSendOrder = ++sequence;
            if (args.msgType is 2 or 3 or 37)
                Add(client, state, "send-attempt", args.msgType,
                    args.msgType == 2 ? "server-protocol-disconnect-attempt-not-socket-close-completion" :
                    "native-send-entry-not-delivery-acknowledgment");
        }
    });
    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (observingException || Volatile.Read(ref observerFailed) != 0) return;
        lock (gate) if (firstExceptions.Count >= 8) return;
        observingException = true;
        try
        {
            if (OutputDirectory() is null) return;
            var stack = new StackTrace(args.Exception, false).ToString();
            var scope = CurrentSocketScope;
            bool exact = scope?.Owner == this;
            // RuntimeDetour may rename the original body to a dynamic trampoline; the verified
            // enclosing actual socket boundary remains exact even without the original type text.
            if (!exact && !stack.Contains("Terraria.Net.Sockets.TcpSocket.", StringComparison.Ordinal) &&
                !stack.Contains("TShockAPI.Sockets.LinuxTcpSocket.", StringComparison.Ordinal) &&
                !stack.Contains("Terraria.Netplay.OnConnectionAccepted", StringComparison.Ordinal) &&
                !stack.Contains("Terraria.RemoteClient.", StringComparison.Ordinal) &&
                !stack.Contains("Terraria.NetMessage.mfwh_CheckBytes", StringComparison.Ordinal) &&
                !stack.Contains("Terraria.NetMessage.mfwh_orig_SendData", StringComparison.Ordinal)) return;
            lock (gate)
            {
                if (firstExceptions.Count >= 8) return;
                firstExceptions.Add(new { order = ++sequence, utc = DateTimeOffset.UtcNow,
                    managedThread = Environment.CurrentManagedThreadId, exceptionType = args.Exception.GetType().FullName,
                    args.Exception.HResult, socketError = (args.Exception as System.Net.Sockets.SocketException)?.SocketErrorCode,
                    stack = stack.Length > 4096 ? stack[..4096] : stack,
                    sourceSocketIdentity = exact ? SocketIdentity(scope!.Socket).Identity : (long?)null,
                    sourceRemotePort = exact ? SocketIdentity(scope!.Socket).RemotePort : null,
                    sourceBoundary = exact ? scope!.Phase : null,
                    selection = exact ? "actual-installed-socket-scope" : "native-type-stack-filter",
                    exactSourceSocketAttribution = exact, exactConnectionAttribution = false });
            }
            WriteSnapshot();
        }
        catch (Exception error) { Fail(error); }
        finally { observingException = false; }
    }
    public object Snapshot()
    {
        lock (gate)
        {
            while (events.Count > 0 && DateTimeOffset.UtcNow - events.Peek().Utc > TimeSpan.FromMinutes(2))
            { events.Dequeue(); dropped++; }
            PruneSocketIdentities();
            return new { schema = 2, sequence, dropped, observerFailed = Volatile.Read(ref observerFailed) != 0,
            capacity = Capacity, historyTtlSeconds = 120,
            generationScope = "observed-server-socket-reference-not-account-session",
            stateTransitionsAreSampled = true, sourceSocketInReadCallbackAvailable = socketObserversInstalled,
            linuxSocketDetoursRequested = observeSocketCallbacks, linuxSocketDetoursInstalled = socketObserversInstalled,
            linuxSocketDetoursWereInstalled = socketObserversWereInstalled,
            exactSourceAvailableOnlyInsideActualLinuxCallback = true,
            socketIdentityCapacity = Capacity, socketIdentityTtlSeconds = 120,
            socketIdentityEvictions, retainedSocketIdentities = socketIdentities.Count,
            firstExceptionCapacity = 8, firstExceptionCaptureFull = firstExceptions.Count >= 8,
            firstExceptionScope = "first-eight-process-wide-matches-later-target-exception-may-be-unobserved",
            overflowPolicy = "expire-120s-then-evict-oldest-noncritical-before-accept-first-close-or-root-reject",
            socketReferences = socketIdentities.Select(x => new { x.Identity, x.RemotePort, x.FirstCloseOrder }).ToArray(),
            events = events.ToArray(), firstExceptions = firstExceptions.ToArray(),
            slots = slots.Select((state, slot) => (state, slot)).Where(x => x.state is not null)
                .Select(x => new { x.slot, x.state!.Generation, x.state.State, x.state.Pending, x.state.Approved,
                    socketMissing = x.state.Socket is null, x.state.LastCompleteBufferedFrame,
                    lastFrameMeaning = "complete-framing-at-CheckBytes-entry-not-successful-game-processing",
                    x.state.LastSendType, x.state.LastSendOrder }).ToArray() };
        }
    }
    public void WriteSnapshot()
    {
        string? directory = OutputDirectory(); if (directory is null) return;
        try
        {
            // One fixed bounded snapshot, serialized under the same gate to prevent interleaving writers.
            lock (gate)
            {
                string pending = Path.Combine(directory, "m7-connection-diagnostics.pending");
                using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.Read))
                { JsonSerializer.Serialize(stream, Snapshot()); stream.Flush(true); }
                File.Move(pending, Path.Combine(directory, "m7-connection-diagnostics.json"), true);
            }
        }
        catch (Exception error) { Fail(error); }
    }
    public void Flush() => WriteSnapshot();
    private string? OutputDirectory()
    {
        if (Volatile.Read(ref observerFailed) != 0) return null;
        bool prior = observingException; observingException = true;
        try { return output(); }
        catch (Exception error) { Fail(error); return null; }
        finally { observingException = prior; }
    }
    private void Guard(Action action)
    {
        if (Volatile.Read(ref observerFailed) != 0) return;
        bool prior = observingException; observingException = true;
        try { action(); }
        catch (Exception error) { Fail(error); }
        finally { observingException = prior; }
    }
    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref observerFailed, 1) != 0) return;
        try { Console.Error.WriteLine("M7_CONNECTION_DIAGNOSTIC_WRITE_FAILED " + error.GetType().Name); }
        catch (Exception) { /* A broken observation sink cannot change the observed runtime failure. */ }
    }
    public void Dispose()
    {
        if (!installed) return;
        DisposeSocketObservers();
        HookEvents.Terraria.RemoteClient.Reset -= OnReset;
        HookEvents.Terraria.RemoteClient.TryRead -= OnTryRead;
        HookEvents.Terraria.RemoteClient.ServerReadCallBack -= OnReadCallback;
        HookEvents.Terraria.NetMessage.CheckBytes -= OnCheckBytes;
        HookEvents.Terraria.NetMessage.SendData -= OnSend;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        if (registrator is not null)
        {
            ServerApi.Hooks.ServerConnect.Deregister(registrator, OnConnectBefore);
            ServerApi.Hooks.ServerConnect.Deregister(registrator, OnConnectAfter);
        }
        WriteSnapshot(); installed = false;
        lock (gate) Array.Clear(slots);
    }
}
