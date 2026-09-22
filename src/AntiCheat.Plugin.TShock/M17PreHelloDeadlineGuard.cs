using System.Reflection;
using MonoMod.RuntimeDetour;
using Terraria;
using TShockAPI;
using TShockAPI.Sockets;

namespace AntiCheat.Plugin.TShock;

/// <summary>Monotonic availability deadline for native accepted TCP before the first complete Hello.
/// Uses the existing maintenance callback and captured-retirement queue; no account, timer or receive hook.</summary>
public sealed class M17PreHelloDeadlineGuard(TimeProvider clock, TimeSpan? timeout = null) : IDisposable
{
    private sealed record Entry(RemoteClient Native, M16TimeoutTransportRetirement Retirement, long FirstSeenAt);
    private readonly object gate = new();
    private readonly Entry?[] entries = new Entry?[256];
    private readonly TimeSpan lifetime = timeout is null || timeout > TimeSpan.Zero ? timeout ?? TimeSpan.FromSeconds(120) :
        throw new ArgumentOutOfRangeException(nameof(timeout));
    private int cursor;
    private Hook? resetHook;
    private bool disposed, failed;
    public bool Healthy { get { lock (gate) return !failed && !disposed && resetHook is not null; } }
    public Action<Exception>? IntegrityFault { get; set; }
    public int Count { get { lock (gate) return entries.Count(e => e is not null); } }
    public M16TimeoutTransportRetirement? CaptureRetirement(int slot)
    { lock (gate) return (uint)slot < entries.Length ? entries[slot]?.Retirement : null; }
    public long? CaptureFirstSeenAt(int slot)
    { lock (gate) return (uint)slot < entries.Length ? entries[slot]?.FirstSeenAt : null; }
    public void Install()
    {
        lock (gate)
        {
            if (disposed || failed || resetHook is not null) return;
            if (typeof(RemoteClient).Assembly.GetName().Version != new Version(1, 4, 5, 8))
                throw new NotSupportedException("Pre-Hello deadline requires audited Terraria1.4.5.8 Reset semantics.");
            resetHook = new Hook(typeof(RemoteClient).GetMethod(nameof(RemoteClient.Reset), BindingFlags.Instance | BindingFlags.Public)!,
                (Action<Action<RemoteClient>, RemoteClient>)AfterReset);
        }
    }
    private void AfterReset(Action<RemoteClient> original, RemoteClient client)
    {
        try { original(client); }
        finally
        {
            try
            {
                // A canceled/deferred reset retains its socket and its deadline. Only the
                // native reset's actual cleared socket ends this observed native generation.
                if ((uint)client.Id < entries.Length)
                    lock (gate) if (client.Socket is null && ReferenceEquals(entries[client.Id]?.Native, client)) entries[client.Id] = null;
            }
            catch (Exception error) { Fault(error); }
        }
    }
    public bool IsTerminal(int slot)
    {
        lock (gate)
        {
            if ((uint)slot >= entries.Length || entries[slot] is not { } entry) return false;
            return entry.Retirement.Authorized && entry.Retirement.MatchesPhysical();
        }
    }
    public IReadOnlyList<M16TimeoutTransportRetirement> Inspect(int maximumToInspect = 16)
    {
        if (maximumToInspect is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumToInspect));
        var expired = new List<M16TimeoutTransportRetirement>(maximumToInspect);
        try
        {
            lock (gate)
            {
                if (!Healthy) return expired;
                long now = clock.GetTimestamp();
                for (int count = 0; count < maximumToInspect; count++)
                {
                    int slot = cursor; cursor = (cursor + 1) % entries.Length;
                    var entry = entries[slot];
                    if (entry is not null && !entry.Retirement.MatchesPhysical()) entries[slot] = entry = null;
                    // A terminal tombstone remains only while its exact physical socket is
                    // still assigned. Retried closes retain5s at most in the shared queue;
                    // failure leaves this bounded active-transport write barrier until native cleanup.
                    if (entry?.Retirement.Authorized == true) continue;
                    var client = slot < Netplay.Clients.Length ? Netplay.Clients[slot] : null;
                    if (client is null || client.State != 0 || slot >= TShockAPI.TShock.Players.Length || TShockAPI.TShock.Players[slot] is not null ||
                        client.Socket is not LinuxTcpSocket socket || socket.GetType() != typeof(LinuxTcpSocket) ||
                        !((Terraria.Net.Sockets.ISocket)socket).IsConnected())
                    { entries[slot] = null; continue; }
                    if (entry is null)
                    {
                        var retirement = M16TimeoutTransportRetirement.CaptureBeforeHello(client);
                        if (retirement is null) continue;
                        entries[slot] = entry = new(client, retirement, now);
                    }
                    var age = clock.GetElapsedTime(entry.FirstSeenAt, now);
                    if (age < TimeSpan.Zero) throw new InvalidOperationException("Pre-Hello monotonic clock reversed.");
                    if (age >= lifetime) expired.Add(entry.Retirement);
                }
            }
        }
        catch (Exception error) { Fault(error); }
        return expired;
    }
    private void Fault(Exception error)
    {
        bool report;
        lock (gate) { report = !failed; failed = true; }
        if (report) try { IntegrityFault?.Invoke(error); } catch { }
    }
    public void Dispose()
    {
        Hook? hook;
        lock (gate) { if (disposed) return; disposed = true; hook = resetHook; resetHook = null; Array.Clear(entries); }
        hook?.Dispose();
    }
}
