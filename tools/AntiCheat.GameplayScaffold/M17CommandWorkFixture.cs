using System.Reflection;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using TShockAPI;
using TShockAPI.DB;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private Hook? m17CredentialHashObserver, m17CredentialVerifyObserver;
    private long m17HashStarted, m17HashCompleted, m17VerifyStarted, m17VerifyCompleted, m17VerifySucceeded;
    private void M17CommandWorkCommand(string[] args)
    {
        Require(args.Length == 1 && args[0] is "start" or "state", "Use qa_m17_work start or state.");
        if (args[0] == "start")
        {
            Require(m17CredentialHashObserver is null && m17CredentialVerifyObserver is null, "Credential observation starts once per owned run.");
            m17CredentialHashObserver = new Hook(typeof(UserAccount).GetMethod(nameof(UserAccount.CreateBCryptHash), [typeof(string)])!,
                (Action<Action<UserAccount, string>, UserAccount, string>)((original, account, password) =>
                {
                    Interlocked.Increment(ref m17HashStarted);
                    original(account, password); // Synchronous forwarding only; no credential retention.
                    Interlocked.Increment(ref m17HashCompleted);
                }));
            try
            {
                m17CredentialVerifyObserver = new Hook(typeof(UserAccount).GetMethod(nameof(UserAccount.VerifyPassword), [typeof(string)])!,
                    (Func<Func<UserAccount, string, bool>, UserAccount, string, bool>)((original, account, password) =>
                    {
                        Interlocked.Increment(ref m17VerifyStarted);
                        bool success = original(account, password);
                        Interlocked.Increment(ref m17VerifyCompleted);
                        if (success) Interlocked.Increment(ref m17VerifySucceeded);
                        return success;
                    }));
            }
            catch { DisposeM17CommandWork(); throw; }
        }
        WriteM17CommandWorkState();
    }
    private void WriteM17CommandWorkState()
    {
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_commandWork", PrivateM5)?.GetValue(plugin);
        var bindings = (Array)plugin.GetType().GetField("_bindings", PrivateM5)!.GetValue(plugin)!;
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        WriteM17CommandWorkSnapshot(Path.Combine(output!, "m17-command-work-state-latest.json"), JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, healthy = Value("Healthy"), admitted = Value("Admitted"), blocked = Value("Blocked"),
            maximumLoginAttempts = TShock.Config.Settings.MaximumLoginAttempts,
            accountBuckets = Value("AccountBucketCount"), hashStarted = Interlocked.Read(ref m17HashStarted),
            hashCompleted = Interlocked.Read(ref m17HashCompleted), verifyStarted = Interlocked.Read(ref m17VerifyStarted),
            verifyCompleted = Interlocked.Read(ref m17VerifyCompleted), verifySucceeded = Interlocked.Read(ref m17VerifySucceeded),
            actors = TShock.Players.Select((actor, slot) => new { actor, slot }).Where(x => x.actor is not null).Take(256).Select(x => new
            {
                x.slot, authenticated = x.actor.IsLoggedIn, account = x.actor.IsLoggedIn ? x.actor.Account?.ID : null,
                loginAttempts = x.actor.LoginAttempts,
                session = bindings.GetValue(x.slot)?.GetType().GetProperty("Key")!.GetValue(bindings.GetValue(x.slot)),
                bypass = x.actor.HasPermission("anticheat.bypass"), console = x.actor.HasPermission("compatibility.qa.console")
            }).ToArray(),
            scope = "passive native credential-call counters; no password/hash/requested account retained; does not provide rule proof inputs"
        }, jsonOptions));
    }
    private static void WriteM17CommandWorkSnapshot(string path, string json)
    {
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp", previous = pending + ".bak";
        bool published = false;
        try
        {
            File.WriteAllText(pending, json);
            if (File.Exists(path)) File.Replace(pending, path, previous);
            else File.Move(pending, path);
            published = true;
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
            if (published && File.Exists(previous)) File.Delete(previous);
        }
    }
    private void DisposeM17CommandWork()
    {
        try { m17CredentialVerifyObserver?.Dispose(); }
        finally { m17CredentialVerifyObserver = null; m17CredentialHashObserver?.Dispose(); m17CredentialHashObserver = null; }
    }
}
