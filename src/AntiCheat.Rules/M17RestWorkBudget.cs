using System.Net;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M17RestWorkOptions
{
    public int SourceCapacity { get; init; } = 4096;
    public double SourceBurst { get; init; } = 8;
    public double SourceTokensPerSecond { get; init; } = .2;
    public double GlobalBurst { get; init; } = 128;
    public double GlobalTokensPerSecond { get; init; } = 8;
    public TimeSpan IdleTtl { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Selected REST account-work admission units, not measured CPU time.
/// Transport-source/global resource ownership only: no account, token or gameplay session.</summary>
public sealed class M17RestWorkBudget
{
    public const string Contract = "I04.NativeRestAccountWork/1.4.5.8-v1";
    private readonly object gate = new();
    private readonly BoundedRateLimiter sources, global;
    private long admitted, blocked;
    public M17RestWorkBudget(TimeProvider clock, M17RestWorkOptions? options = null)
    {
        options ??= new();
        sources = new(clock, new(options.SourceCapacity, options.SourceBurst, options.SourceTokensPerSecond, options.IdleTtl));
        global = new(clock, new(1, options.GlobalBurst, options.GlobalTokensPerSecond, options.IdleTtl));
    }
    public long Admitted { get { lock (gate) return admitted; } }
    public long Blocked { get { lock (gate) return blocked; } }
    public int SourceCount => sources.Count;
    public bool Consume(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        string source = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        lock (gate)
        {
            bool allowed = sources.TryConsume(source).Behavior == ControlAction.Pass &&
                global.TryConsume("rest-account-work").Behavior == ControlAction.Pass;
            if (allowed) { if (admitted != long.MaxValue) admitted++; }
            else if (blocked != long.MaxValue) blocked++;
            return allowed;
        }
    }
}
