using AntiCheat.Core;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Plugin.TShock;

/// <summary>Real TShock account bans, dispatched onto the game callback context.</summary>
public sealed class TShockAccountBanStore(ServerThreadDispatcher dispatcher) : IAccountBanStore
{
    public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() => Apply(intent), cancellationToken);

    public static string ResolveAccountIdentifier(long authenticatedAccountId, UserAccount? account)
    {
        if (authenticatedAccountId <= 0 || authenticatedAccountId > int.MaxValue || account is null ||
            account.ID != authenticatedAccountId || string.IsNullOrWhiteSpace(account.Name))
            throw new InvalidOperationException("authenticated-account-id-mapping-unavailable");
        return $"{Identifier.Account}{account.Name}";
    }

    private static void Apply(BanIntent intent)
    {
        if (intent.AccountId <= 0 || intent.AccountId > int.MaxValue) throw new InvalidOperationException("invalid-account-id");
        // The database resolves the immutable numeric identity at commit time. A player name/IP/UUID never chooses the target.
        var account = ServerTShock.UserAccounts.GetUserAccountByID((int)intent.AccountId);
        string identifier = ResolveAccountIdentifier(intent.AccountId, account);
        var existing = ServerTShock.Bans.Bans.Values.Where(b => b.Identifier == identifier && b.ExpirationDateTime > DateTime.UtcNow).ToArray();
        if (existing.Any(b => b.ExpirationDateTime == DateTime.MaxValue && b.BanDateTime < DateTime.UtcNow)) return;
        if (existing.Length > 0)
        {
            // BanManager has no single-record update API and rejects duplicate identifiers. This adapter-owned
            // SQL changes only the matched ticket's sanction dates, preserving its administrator/reason.
            // A scheduled future ban must become active now when this distinct proof calls for immediate enforcement.
            var ban = existing[0];
            var starts = ban.BanDateTime < DateTime.UtcNow ? ban.BanDateTime : DateTime.UtcNow.AddSeconds(-1);
            using var connection = ServerTShock.DB.CloneEx();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "UPDATE PlayerBans SET Expiration=@expiry, Date=@starts WHERE TicketNumber=@ticket AND Identifier=@identifier AND Expiration=@previous AND Date=@previousStart";
            command.AddParameter("@expiry", DateTime.MaxValue.Ticks);
            command.AddParameter("@ticket", ban.TicketNumber);
            command.AddParameter("@identifier", identifier);
            command.AddParameter("@previous", ban.ExpirationDateTime.Ticks);
            command.AddParameter("@starts", starts.Ticks);
            command.AddParameter("@previousStart", ban.BanDateTime.Ticks);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("account-ban-upgrade-version-conflict");
            ban.ExpirationDateTime = DateTime.MaxValue;
            ban.BanDateTime = starts;
            return;
        }
        var result = ServerTShock.Bans.InsertBan(identifier, $"AntiCheat {intent.Evidence.RuleId}; incident {intent.IncidentId:N}",
            "AntiCheat", DateTime.UtcNow.AddSeconds(-1), DateTime.MaxValue);
        if (result.Ban is null || result.Ban.Identifier != identifier || result.Ban.ExpirationDateTime != DateTime.MaxValue
            || result.Ban.BanDateTime >= DateTime.UtcNow)
            throw new InvalidOperationException("tshock-permanent-account-ban-not-persisted");
    }
}
