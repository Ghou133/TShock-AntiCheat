using System.Net.Sockets;

/// <summary>Metadata for one actual LabClient transport lifetime; no protocol payload or exception messages.</summary>
internal sealed class M7TransportDiagnostics
{
    private static long nextGeneration;
    private readonly object gate = new();
    private readonly long generation = Interlocked.Increment(ref nextGeneration);
    private readonly List<object> transitions = new(32);
    private long order, dropped;
    private object? lastRead, lastWrite, firstException, firstClose;
    private string phase = "created";
    public void Phase(string value)
    {
        lock (gate)
        {
            phase = value;
            if (transitions.Count < 32) transitions.Add(new { order = ++order, utc = DateTimeOffset.UtcNow, phase });
            else dropped++;
        }
    }
    public void CompleteRead(byte packet, int length)
    { lock (gate) lastRead = new { order = ++order, utc = DateTimeOffset.UtcNow, packet, length }; }
    public void CompleteWrite(byte packet, int length)
    { lock (gate) lastWrite = new { order = ++order, utc = DateTimeOffset.UtcNow, packet, length }; }
    public void Close(string initiator)
    { lock (gate) firstClose ??= new { order = ++order, utc = DateTimeOffset.UtcNow, initiator, phase }; }
    public void Failure(Exception exception, string operation, bool locallyCanceled)
    {
        lock (gate)
        {
            firstException ??= new { order = ++order, utc = DateTimeOffset.UtcNow, operation, phase,
                locallyCanceled, exceptionType = exception.GetType().FullName, exception.HResult,
                innerType = exception.InnerException?.GetType().FullName,
                socketError = (exception as SocketException ?? exception.InnerException as SocketException)?.SocketErrorCode };
            firstClose ??= new { order = ++order, utc = DateTimeOffset.UtcNow,
                initiator = locallyCanceled ? "local-cancellation" : "transport-failure-peer-or-network-unresolved", phase };
        }
    }
    public object Snapshot()
    {
        lock (gate) return new { connectionGeneration = generation, scope = "one-LabClient-instance",
            phase, transitions = transitions.ToArray(), dropped, firstException, firstClose, lastRead, lastWrite,
            completeReadMeaning = "entire-framed-packet-read-before-protocol-observer",
            completeWriteMeaning = "local-WriteAsync-completed-not-peer-delivery-or-game-acceptance" };
    }
}
