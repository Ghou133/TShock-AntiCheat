using System.Net.Sockets;
using AntiCheat.Core;
using Terraria;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.Sockets;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Captures the actual accepted TCP connection, not a lookup of whichever connection
/// currently occupies a native slot. Caller must also validate its current root Binding.
/// Only the audited LinuxTcpSocket provider is supported by this retirement contract.
/// </summary>
public sealed class M16TimeoutTransportRetirement
{
    private readonly object gate = new();
    private readonly M10ConnectionPhaseAdapter? transport;
    private readonly RemoteClient nativeClient;
    private readonly int nativeSlot;
    private readonly LinuxTcpSocket provider;
    private readonly TcpClient connection;
    private readonly Socket acceptedSocket;
    private bool authorized, finished, started, wasClosed, finalObserved;
    private int attempts;
    private long requestedAt, lastAttemptAt;
    private string? failure;

    private M16TimeoutTransportRetirement(SessionKey key, TSPlayer player, RemoteClient client,
        LinuxTcpSocket provider, TcpClient connection, Socket acceptedSocket)
    {
        Session = key; this.provider = provider; this.connection = connection; this.acceptedSocket = acceptedSocket;
        nativeClient = client; nativeSlot = client.Id;
        transport = new(key, player, client, provider);
    }

    private M16TimeoutTransportRetirement(RemoteClient client, LinuxTcpSocket provider,
        TcpClient connection, Socket acceptedSocket)
    {
        nativeClient = client; nativeSlot = client.Id; this.provider = provider; this.connection = connection; this.acceptedSocket = acceptedSocket;
        // Before the first complete Hello no TSPlayer or engine Session exists.
        Session = null;
    }

    public SessionKey? Session { get; }
    public int NativeSlot => nativeSlot;
    public bool Authorized { get { lock (gate) return authorized; } }
    public bool Finished { get { lock (gate) return finished; } }
    public int Attempts { get { lock (gate) return attempts; } }
    public string? LastFailure { get { lock (gate) return failure; } }

    public static M16TimeoutTransportRetirement? Capture(SessionKey key, TSPlayer player,
        RemoteClient client, ISocket socket)
    {
        // The actual audited listener creates a new LinuxTcpSocket(TcpClient) for each
        // Accept. Capture both its TcpClient and raw Socket: neither enclosing mutable
        // field may redirect this retirement to somebody else's physical connection.
        if (socket.GetType() != typeof(LinuxTcpSocket) || socket is not LinuxTcpSocket provider ||
            provider._connection is not { } connection || connection.Client is not { } acceptedSocket) return null;
        var value = new M16TimeoutTransportRetirement(key, player, client, provider, connection, acceptedSocket);
        return value.Matches(key, player) ? value : null;
    }

    private bool Matches(SessionKey key, TSPlayer player) =>
        transport?.MatchesCurrent(key, player) == true && MatchesPhysical();

    public bool MatchesPhysical() => nativeClient.Id == nativeSlot && (uint)nativeSlot < Netplay.Clients.Length &&
        ReferenceEquals(Netplay.Clients[nativeSlot], nativeClient) &&
        ReferenceEquals(nativeClient.Socket, provider) && ReferenceEquals(provider._connection, connection) &&
        ReferenceEquals(connection.Client, acceptedSocket);

    public static M16TimeoutTransportRetirement? CaptureBeforeHello(RemoteClient client)
    {
        if (client.Socket is not LinuxTcpSocket provider || provider.GetType() != typeof(LinuxTcpSocket) ||
            provider._connection is not { } connection || connection.Client is not { } acceptedSocket) return null;
        var value = new M16TimeoutTransportRetirement(client, provider, connection, acceptedSocket);
        return value.MatchesPhysical() ? value : null;
    }

    /// <summary>Caller holds the root binding lock and verified no engine binding exists.
    /// An absent actor is checked again here; no account/session identity is manufactured.</summary>
    public bool TryAuthorizeBeforeHello()
    {
        lock (gate)
        {
            if (Session is not null || authorized || finished || !MatchesPhysical() || nativeClient.State != 0 ||
                (uint)nativeClient.Id >= TShockAPI.TShock.Players.Length || TShockAPI.TShock.Players[nativeClient.Id] is not null) return false;
            authorized = true;
            return true;
        }
    }

    /// <summary>
    /// Invoke while the root owns and validates the same Binding. Authorization is
    /// terminal for this physical connection; a following account/logout must not revive it.
    /// After this returns true, synchronously isolate that root session before Enqueue.
    /// </summary>
    public bool TryAuthorize(SessionKey key, TSPlayer player)
    {
        lock (gate)
        {
            if (authorized || finished || !Matches(key, player)) return false;
            authorized = true;
            return true;
        }
    }

    internal bool Attempt(long now, TimeProvider clock, bool first, out bool closed, bool finalAttempt = false)
    {
        lock (gate)
        {
            closed = false;
            if (!authorized || finished) return false;
            if (first)
            {
                if (started) return false;
                started = true; requestedAt = now;
            }
            else
            {
                if (!started)
                {
                    if (!finalAttempt) return false;
                    started = true; requestedAt = now;
                }
                TimeSpan age, since;
                try { age = clock.GetElapsedTime(requestedAt, now); since = clock.GetElapsedTime(lastAttemptAt, now); }
                catch (Exception error) { failure = error.GetType().Name; finished = true; return false; }
                if (age < TimeSpan.Zero || since < TimeSpan.Zero || age >= TimeSpan.FromSeconds(5))
                { failure ??= "timeout-retirement-clock-or-ttl"; finished = true; return false; }
                if (!finalAttempt && since < TimeSpan.FromMilliseconds(250)) return false;
            }
            attempts++; lastAttemptAt = now;
            try
            {
                // No native slot writes, TSPlayer.Disconnect, SendData hook, native Reset,
                // or callback replay. TcpClient.Client is itself mutable; only dispose
                // the captured Socket object, even if either enclosing provider field
                // was replaced meanwhile. Existing native cleanup owns both wrappers.
                acceptedSocket.Dispose();
                closed = true; wasClosed = true; finished = true;
            }
            catch (Exception error)
            {
                failure = error.GetType().Name;
                if (attempts >= 3) finished = true;
            }
            return true;
        }
    }

    internal void Abandon(string reason)
    { lock (gate) { failure ??= reason; finished = true; } }

    internal bool ObserveFinal(out bool closed)
    {
        lock (gate)
        {
            closed = wasClosed;
            if (!finished || finalObserved) return false;
            finalObserved = true; return true;
        }
    }
}

/// <summary>Bounded retirement retries driven only by the existing maintenance callback.</summary>
public sealed class M16TimeoutRetirementQueue(TimeProvider clock) : IDisposable
{
    private const int Capacity = 256;
    private readonly object gate = new();
    private readonly M16TimeoutTransportRetirement?[] pending = new M16TimeoutTransportRetirement?[Capacity];
    private int cursor, queued;
    private bool disposed;
    private long attempted, closed, failed;
    public Action<M16TimeoutTransportRetirement>? RetirementFailed { get; set; }
    public M16TimeoutRetirementSnapshot Snapshot
    { get { lock (gate) return new(queued, attempted, closed, failed); } }

    public void EnqueueAuthorized(M16TimeoutTransportRetirement retirement)
    {
        if (!retirement.Authorized || retirement.Finished) return;
        int index;
        lock (gate)
        {
            if (Array.Exists(pending, item => ReferenceEquals(item, retirement))) return;
            index = disposed ? -1 : Array.FindIndex(pending, item => item is null);
            if (index >= 0) { pending[index] = retirement; queued++; }
        }
        // First close is never delayed by an occupied queue or by a broken clock.
        long now = 0;
        bool clockHealthy = true;
        try { now = clock.GetTimestamp(); } catch { clockHealthy = false; }
        bool didAttempt = retirement.Attempt(now, clock, first: true, out bool didClose);
        if (!retirement.Finished && (index < 0 || !clockHealthy))
            retirement.Abandon(index < 0 ? "timeout-retirement-capacity" : "timeout-retirement-clock");
        Observe(retirement, didAttempt, didClose);
    }

    public void Maintain(int maximumToInspect = 16)
    {
        if (maximumToInspect is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumToInspect));
        for (int count = 0; count < maximumToInspect; count++)
        {
            M16TimeoutTransportRetirement? retirement;
            lock (gate)
            {
                if (disposed) return;
                retirement = pending[cursor]; cursor = (cursor + 1) % Capacity;
            }
            if (retirement is null) continue;
            bool attemptedNow = false, closedNow = false;
            try { attemptedNow = retirement.Attempt(clock.GetTimestamp(), clock, first: false, out closedNow); }
            catch (Exception error) { retirement.Abandon(error.GetType().Name); }
            Observe(retirement, attemptedNow, closedNow);
        }
    }

    private void Observe(M16TimeoutTransportRetirement retirement, bool didAttempt, bool didClose)
    {
        bool report = false;
        lock (gate)
        {
            if (didAttempt) attempted++;
            if (retirement.Finished)
            {
                int index = Array.FindIndex(pending, item => ReferenceEquals(item, retirement));
                if (index >= 0) { pending[index] = null; queued--; }
                if (retirement.ObserveFinal(out bool actuallyClosed))
                {
                    if (actuallyClosed) closed++;
                    else { failed++; report = true; }
                }
            }
        }
        if (report) try { RetirementFailed?.Invoke(retirement); } catch { }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        // At most one remaining attempt per queued owned connection during unload,
        // still within its three-attempt ceiling. Never leave queued object references.
        for (int index = 0; index < Capacity; index++)
        {
            M16TimeoutTransportRetirement? retirement;
            lock (gate) retirement = pending[index];
            if (retirement is null) continue;
            bool attemptedNow = false, closedNow = false;
            try { attemptedNow = retirement.Attempt(clock.GetTimestamp(), clock, first: false, out closedNow, finalAttempt: true); }
            catch (Exception error) { retirement.Abandon(error.GetType().Name); }
            if (!retirement.Finished) retirement.Abandon("timeout-retirement-plugin-disposed");
            Observe(retirement, attemptedNow, closedNow);
        }
    }
}

public sealed record M16TimeoutRetirementSnapshot(int Pending, long Attempts, long Closed, long Failed);
