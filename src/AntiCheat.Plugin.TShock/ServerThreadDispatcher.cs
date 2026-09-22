using System.Collections.Concurrent;

namespace AntiCheat.Plugin.TShock;

/// <summary>One bounded queue, drained by audited active or idle callbacks on the same server thread.</summary>
public sealed class ServerThreadDispatcher : IDisposable
{
    private sealed record Work(Action Callback, TaskCompletionSource Completion, CancellationToken Cancellation);
    private readonly ConcurrentQueue<Work> _queue = new();
    private readonly int _capacity;
    private int _count;
    private int _ownerThread;
    private int _disposed;

    public ServerThreadDispatcher(int capacity = 32)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int PendingCount => Volatile.Read(ref _count);

    public ValueTask InvokeAsync(Action callback, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return ValueTask.FromException(new ObjectDisposedException(nameof(ServerThreadDispatcher)));
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            return ValueTask.FromException(new InvalidOperationException("server-dispatch-capacity"));
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new(callback, completion, cancellationToken));
        // Covers disposal racing the enqueue without running any server action off-thread.
        if (Volatile.Read(ref _disposed) != 0) CancelQueued();
        return new(completion.Task);
    }

    public int Drain(int maxItems = 1)
    {
        if (maxItems < 0) throw new ArgumentOutOfRangeException(nameof(maxItems));
        int current = Environment.CurrentManagedThreadId;
        Interlocked.CompareExchange(ref _ownerThread, current, 0);
        if (_ownerThread != current) throw new InvalidOperationException("server-dispatch-thread-changed");
        int drained = 0;
        while (drained < maxItems && _queue.TryDequeue(out var work))
        {
            Interlocked.Decrement(ref _count);
            drained++;
            if (work.Cancellation.IsCancellationRequested) { work.Completion.TrySetCanceled(work.Cancellation); continue; }
            try { work.Callback(); work.Completion.TrySetResult(); }
            catch (Exception exception) { work.Completion.TrySetException(exception); }
        }
        return drained;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        CancelQueued();
    }

    private void CancelQueued()
    {
        while (_queue.TryDequeue(out var work))
        {
            Interlocked.Decrement(ref _count);
            work.Completion.TrySetException(new ObjectDisposedException(nameof(ServerThreadDispatcher)));
        }
    }
}
