using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum M17CommandWorkKind { Login, Register, PasswordChange, AccountAdministration }
public sealed record M17CommandWorkDecision(bool Allowed, string Reason, double Cost);
public sealed record M17CommandWorkOptions
{
    public double ActorBurst { get; init; } = 8;
    public double ActorTokensPerSecond { get; init; } = .2;
    public double GlobalBurst { get; init; } = 128;
    public double GlobalTokensPerSecond { get; init; } = 8;
}

/// <summary>Admission units for audited expensive command entry, not measured CPU milliseconds.
/// No text, claimed account name, password, IP ban or sanction is represented by this model.</summary>
public sealed class M17CommandWorkBudget
{
    public const string Contract = "I04.NativeCredentialCommandWork/1.4.5.8-v1";
    private readonly object gate = new();
    private readonly BoundedRateLimiter sessions, accounts, global;
    private long admitted, blocked;
    public M17CommandWorkBudget(TimeProvider clock, M17CommandWorkOptions? options = null)
    {
        options ??= new();
        sessions = new(clock, new(256, options.ActorBurst, options.ActorTokensPerSecond, TimeSpan.FromMinutes(10)));
        accounts = new(clock, new(4096, options.ActorBurst, options.ActorTokensPerSecond, TimeSpan.FromMinutes(10)));
        global = new(clock, new(1, options.GlobalBurst, options.GlobalTokensPerSecond, TimeSpan.FromMinutes(10)));
    }
    public long Admitted { get { lock (gate) return admitted; } }
    public long Blocked { get { lock (gate) return blocked; } }
    public int AccountBucketCount => accounts.Count;
    public int SessionBucketCount => sessions.Count;
    public M17CommandWorkDecision Consume(SessionKey session, long? authenticatedAccountId, M17CommandWorkKind kind)
    {
        if (session.ServerRunId == Guid.Empty || session.WorldEpoch <= 0 || session.Generation <= 0 || session.Slot is < 0 or >= 256 ||
            authenticatedAccountId is <= 0 || !Enum.IsDefined(kind)) return new(false, "invalid-command-work-context", 0);
        double cost = kind switch
        {
            M17CommandWorkKind.Register => 1,
            M17CommandWorkKind.Login => 2, // verify plus potential transparent work-factor upgrade
            M17CommandWorkKind.PasswordChange => 3, // verify, upgrade and requested new password
            _ => 1 // native /user add or password: one new hash; other subcommands still pay a command-entry unit
        };
        lock (gate)
        {
            var own = sessions.TryConsume(Key(session), cost);
            if (own.Behavior != ControlAction.Pass) return Reject("command-session-work-budget", cost);
            // Only the authenticated sender is charged. A requested login/target account
            // cannot deplete another person's account bucket before authentication.
            if (authenticatedAccountId is { } account && accounts.TryConsume(account.ToString(CultureInfo.InvariantCulture), cost).Behavior != ControlAction.Pass)
                return Reject("command-account-work-budget", cost);
            // Rejected local work never executes and does not charge the global work pool.
            // Ordinary ingress bytes still retain their independent source/global costs.
            if (global.TryConsume("credential-command-work", cost).Behavior != ControlAction.Pass)
                return Reject("command-global-work-budget", cost);
            if (admitted != long.MaxValue) admitted++;
            return new(true, "command-work-within-budget", cost);
        }
    }
    private M17CommandWorkDecision Reject(string reason, double cost)
    { if (blocked != long.MaxValue) blocked++; return new(false, reason, cost); }
    public void Forget(SessionKey session) => sessions.Forget(Key(session));
    private static string Key(SessionKey session) => string.Create(CultureInfo.InvariantCulture,
        $"{session.ServerRunId:N}/{session.WorldEpoch}/{session.Slot}/{session.Generation}");
}
