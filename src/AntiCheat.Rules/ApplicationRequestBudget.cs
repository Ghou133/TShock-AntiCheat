using System.Globalization;
using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record ApplicationBudgetDecision(bool Allowed, string Reason, double Cost);

/// <summary>
/// Admission before TShock chat formatting, command parsing, alias scans, permission logs and handlers.
/// The packet's strings have already been decoded by TSAPI. Units are request/character weights, not CPU time.
/// No text, command, password or client identity is retained. Rejections are never account sanctions.
/// </summary>
public sealed class ApplicationRequestBudget
{
    private readonly object gate = new();
    private readonly BoundedRateLimiter sessions;
    private readonly BoundedRateLimiter accounts;
    private long admitted, rejected;

    public ApplicationRequestBudget(TimeProvider clock)
    {
        sessions = new(clock, new(256, 32, 8, TimeSpan.FromMinutes(2)));
        accounts = new(clock, new(4096, 32, 8, TimeSpan.FromMinutes(2)));
    }

    public long Admitted { get { lock (gate) return admitted; } }
    public long Rejected { get { lock (gate) return rejected; } }
    public int AccountBucketCount => accounts.Count;

    public ApplicationBudgetDecision Consume(SessionKey session, long? authenticatedAccountId, int textCharacters)
    {
        if (session.ServerRunId == Guid.Empty || session.WorldEpoch <= 0 || session.Generation <= 0 ||
            session.Slot is < 0 or >= 256 || authenticatedAccountId is <= 0 || textCharacters is < 0 or > 65535)
            return new(false, "invalid-application-admission-context", 0);
        double cost = 1 + textCharacters / 256d;
        lock (gate)
        {
            var connection = sessions.TryConsume(Key(session), cost);
            // Account budgets survive logout, reconnect, slot reuse and world changes until normal refill/TTL.
            // A different account on the same NAT has its own bucket. Source/global limits remain NetworkControls'.
            var account = authenticatedAccountId is { } id
                ? accounts.TryConsume(id.ToString(CultureInfo.InvariantCulture), cost) : null;
            bool allowed = connection.Behavior == ControlAction.Pass && account?.Behavior != ControlAction.Block;
            if (allowed) { if (admitted != long.MaxValue) admitted++; }
            else if (rejected != long.MaxValue) rejected++;
            return new(allowed, allowed ? "application-within-budget" :
                connection.Behavior == ControlAction.Block ? "application-session-budget" : "application-account-budget", cost);
        }
    }

    public void Forget(SessionKey session) => sessions.Forget(Key(session));
    private static string Key(SessionKey session) => string.Create(CultureInfo.InvariantCulture,
        $"{session.ServerRunId:N}/{session.WorldEpoch}/{session.Slot}/{session.Generation}");
}
