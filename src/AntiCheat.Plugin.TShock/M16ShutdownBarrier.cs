namespace AntiCheat.Plugin.TShock;

public enum M16ShutdownWaitOutcome { Clean, Unclean, TimedOut, Canceled, Faulted }

public sealed record M16ShutdownWaitResult(M16ShutdownWaitOutcome Outcome, string? FailureType = null)
{
    public bool CleanConfirmed => Outcome == M16ShutdownWaitOutcome.Clean;
}

/// <summary>
/// The synchronous TSAPI Dispose boundary must keep the host alive while the existing shutdown
/// task finishes its durable write and rename. This terminal-only barrier neither starts work,
/// pumps game callbacks, disposes the journal nor changes the engine's clean-run decision.
/// </summary>
public static class M16ShutdownBarrier
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(30);

    public static M16ShutdownWaitResult WaitForExit(Task<bool> completion, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(completion);
        TimeSpan wait = budget ?? DefaultBudget;
        if (wait < TimeSpan.Zero || wait > MaximumBudget) throw new ArgumentOutOfRangeException(nameof(budget));
        try
        {
            if (!completion.Wait(wait)) return new(M16ShutdownWaitOutcome.TimedOut);
            return new(completion.GetAwaiter().GetResult() ? M16ShutdownWaitOutcome.Clean : M16ShutdownWaitOutcome.Unclean);
        }
        catch (Exception error)
        {
            // Task.Wait observes terminal cancellation/failure through AggregateException.
            // Neither one authorizes a clean marker or a second cleanup operation.
            return completion.IsCanceled ? new(M16ShutdownWaitOutcome.Canceled)
                : new(M16ShutdownWaitOutcome.Faulted, error.GetBaseException().GetType().Name);
        }
    }
}
