using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M17CommandWorkScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m17-command-work"); Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(4); string status = "failed", failure = "";
        long initialBans = BanCount(); int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-native-runtime"); await host.FixtureSnapshot();
            await host.ConsoleCommand("qa_m17_work start"); var initial = await State("initial");
            Check(initial.GetProperty("healthy").GetBoolean(), "exact-command-work-hook-installed");
            var normal = await Connect("M17WorkNormal"); await normal.Join(); await normal.RegisterAndLogin();
            Check(normal.Authenticated && normal.SscSlots.Count >= 350, "normal-native-registration-login-and-SSC");
            var normalState = await State("normal-account");
            Check(Delta(normalState, initial, "hashCompleted") == 1 && Delta(normalState, initial, "verifySucceeded") == 1 &&
                Delta(normalState, initial, "blocked") == 0, "normal-native-hash-and-password-check-complete");

            var retries = await Connect("M17WorkRetries"); await retries.Join();
            await retries.Chat("/register LocalLab784!"); await retries.Drain(TimeSpan.FromMilliseconds(300));
            var beforeRetries = await State("before-legal-failed-retries");
            for (int i = 0; i < 2; i++) { await retries.Chat("/login WrongFixturePassword!"); await retries.Drain(TimeSpan.FromMilliseconds(200)); }
            var failedRetries = await State("two-legal-failed-retries");
            Check(Delta(failedRetries, beforeRetries, "verifyCompleted") == 2 && Delta(failedRetries, beforeRetries, "verifySucceeded") == 0 &&
                Delta(failedRetries, beforeRetries, "blocked") == 0 && Actor(failedRetries, retries).GetProperty("loginAttempts").GetInt32() == 2,
                "native-failed-login-behavior-preserved-without-fake-budget-failures");
            await retries.Login(); Check(retries.Authenticated && retries.SscSlots.Count >= 350, "correct-login-after-two-failures-completes-SSC");

            var limited = await Connect("M17WorkLimited"); await limited.Join();
            await limited.Chat("/register LocalLab784!"); await limited.Drain(TimeSpan.FromMilliseconds(300));
            Check(limited.Messages.Any(m => m.Contains("registered", StringComparison.OrdinalIgnoreCase)), "ordinary-unauthed-registration-actually-created-own-account");
            var before = await State("before-native-hash-burst"); string storedPassword = ReadStoredPassword(limited.Name);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 8; i++) await limited.Chat("/register LocalLab784!");
            await limited.Drain(TimeSpan.FromMilliseconds(300)); var blocked = await State("native-hash-burst-limited");
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "bounded-burst-before-one-token-refill");
            Check(Delta(blocked, before, "hashStarted") == 7 && Delta(blocked, before, "hashCompleted") == 7 &&
                Delta(blocked, before, "admitted") == 7 && Delta(blocked, before, "blocked") == 1,
                "eighth-existing-account-retry-stopped-before-real-BCrypt");
            Check(ReadStoredPassword(limited.Name) == storedPassword && Actor(blocked, limited).GetProperty("account").ValueKind == JsonValueKind.Null,
                "taken-account-hash-not-mutated-and-no-authenticated-subject-invented");
            await limited.Chat("/login LocalLab784!"); await limited.Drain(TimeSpan.FromMilliseconds(150));
            var noVerify = await State("denied-login-did-not-run-verifier");
            Check(Delta(noVerify, blocked, "verifyStarted") == 0 && Actor(noVerify, limited).GetProperty("loginAttempts").GetInt32() == 0,
                "resource-denial-is-not-an-invalid-password-or-native-login-attempt");
            await normal.Chat("M17-independent-normal-chat"); await normal.Chat("/who"); await normal.PingAsync();
            await retries.Drain(TimeSpan.FromMilliseconds(150));
            Check(retries.Messages.Any(m => m.Contains("M17-independent-normal-chat", StringComparison.Ordinal)), "same-NAT-normal-chat-and-command-still-work");
            await Task.Delay(TimeSpan.FromSeconds(10.2)); // Natural two-unit refill; no policy/timer reset.
            await limited.Login(); Check(limited.Authenticated && limited.SscSlots.Count >= 350, "natural-refill-restores-native-login-and-SSC");
            var final = await State("natural-refill-recovery");
            Check(final.GetProperty("healthy").GetBoolean() && Actor(final, limited).GetProperty("authenticated").GetBoolean(), "ordinary-recovered-account-bound-by-root");
            Check(clients.All(c => !c.Closed && c.DisconnectReason is null) && BanCount() == initialBans &&
                !host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "resource-budget-created-no-sanction-or-disconnect");

            // Independent native policy counterexample: exhausting admission units cannot
            // hide TShock's already-reached invalid-login limit. This close is core-owned.
            var core = await Connect("M17WorkCoreLimit"); await core.Join();
            await core.Chat("/register LocalLab784!"); await core.Drain(TimeSpan.FromMilliseconds(300));
            await Task.Delay(TimeSpan.FromSeconds(5.2)); // Registration's one admission unit refills naturally.
            var beforeCore = await State("before-core-login-failure-limit");
            Check(beforeCore.GetProperty("maximumLoginAttempts").GetInt32() == 3, "unchanged-native-default-login-attempt-limit");
            for (int i = 0; i < 4; i++) { await core.Chat("/login WrongFixturePassword!"); await core.Drain(TimeSpan.FromMilliseconds(200)); }
            var fourFailures = await State("four-real-core-login-failures");
            Check(Delta(fourFailures, beforeCore, "verifyCompleted") == 4 && Delta(fourFailures, beforeCore, "verifySucceeded") == 0 &&
                Delta(fourFailures, beforeCore, "admitted") == 4 && Delta(fourFailures, beforeCore, "blocked") == 0 &&
                Actor(fourFailures, core).GetProperty("loginAttempts").GetInt32() == 4, "native-four-failures-consume-eight-units-without-synthetic-failure");
            await core.Chat("/login WrongFixturePassword!"); var closeWait = Stopwatch.StartNew();
            while (!core.Closed && closeWait.Elapsed < TimeSpan.FromSeconds(8)) await core.Drain(TimeSpan.FromMilliseconds(100));
            var coreClosed = await State("core-default-limit-disconnected-own-client");
            Check(core.Closed && core.DisconnectReason?.Contains("invalid login attempts", StringComparison.OrdinalIgnoreCase) == true &&
                Delta(coreClosed, fourFailures, "verifyStarted") == 0 && Delta(coreClosed, fourFailures, "admitted") == 0 &&
                Delta(coreClosed, fourFailures, "blocked") == 0, "core-prehash-kick-remains-effective-after-work-allowance-exhausted");
            Check(new[] { normal, retries, limited }.All(c => !c.Closed && c.DisconnectReason is null) && BanCount() == initialBans &&
                !host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "core-policy-exception-does-not-affect-peers-or-create-anticheat-sanctions");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            { status, failure, nativeCredentialHandlers = true, syntheticTcp = true, stockClient = false, accountBan = false, coreLoginLimitCounterexample = true,
                expensiveWorkPrevented = "only audited selected credential command entry; actual native hash/verify counters independent", credentialsRecorded = false }, options));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool result, string label) => host.Assert(result, "m17-command-work:" + label);
        async Task<LabClient> Connect(string name)
        { var client = await host.Connect(name); client.OmitChatMessageTextFromEvidence = true; clients.Add(client); return client; }
        async Task<JsonElement> State(string label)
        {
            var utc = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m17_work state");
            string path = Path.Combine(host.ReportDirectory, "m17-command-work-state-latest.json"); var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path)) try
                {
                    string text = await ReadSnapshotTextAsync(path); using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= utc)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Fresh native credential-work observation missing.");
        }
        SqliteConnection Database() { var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open(); return db; }
        long BanCount() { using var db = Database(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans"; return Convert.ToInt64(query.ExecuteScalar()); }
        string ReadStoredPassword(string name)
        { using var db = Database(); using var query = db.CreateCommand(); query.CommandText = "SELECT Password FROM Users WHERE Username=$name"; query.Parameters.AddWithValue("$name", name); return (string)query.ExecuteScalar()!; }
        static JsonElement Actor(JsonElement state, LabClient client) => state.GetProperty("actors").EnumerateArray().Single(a => a.GetProperty("slot").GetInt32() == client.Slot);
        static long Delta(JsonElement after, JsonElement before, string field) => after.GetProperty(field).GetInt64() - before.GetProperty(field).GetInt64();
    }
    internal static async Task<string> ReadSnapshotTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
