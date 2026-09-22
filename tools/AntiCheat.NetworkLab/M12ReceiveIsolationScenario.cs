using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Fixed, bounded owned-loopback lifecycle scenarios. Holds one real completed
/// receive before the product source guard; never fabricates account/SSC completion.</summary>
internal static class M12ReceiveIsolationScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m12-receive-isolation"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(14); var phases = new List<object>(6);
        string status = "failed", failure = "";
        string? armId = null;
        long armGeneration = 0;
        try
        {
            Check(host.RuntimeVerified() && host.ConnectFrom is not null, "locked-runtime-and-owned-distinct-loopback-sources");
            await host.FixtureSnapshot();
            var peer = await Actor("M12RObserver", "127.0.0.4");
            var cases = new[] { (Mode: "data", Stage: "admitted"), (Mode: "data", Stage: "preauth"),
                (Mode: "data", Stage: "authenticated-ssc"), (Mode: "eof", Stage: "authenticated-ssc"),
                (Mode: "fault", Stage: "authenticated-ssc") };
            int caseId = 0;
            foreach (var test in cases)
            {
                string label = test.Mode + "-" + test.Stage;
                var old = await Actor("M12ROld" + ++caseId, "127.0.0.2"); old.PauseHeartbeat = true;
                await old.PingAsync();
                long oldAccount = Account(old.Name); int slot = old.Slot;
                armId = Guid.NewGuid().ToString("N"); armGeneration = 0;
                await host.ConsoleCommand($"qa_m12_receive arm {old.Name} {test.Mode} {armId}");
                var armed = await State(s => s.GetProperty("stage").GetString() == "armed" &&
                    s.GetProperty("oldActor").GetString() == old.Name && s.GetProperty("oldAccount").GetInt64() == oldAccount &&
                    s.GetProperty("oldSlot").GetInt32() == slot && s.GetProperty("oldGeneration").GetInt64() > 0 &&
                    s.GetProperty("current").GetProperty("socketIsOld").GetBoolean() &&
                    s.GetProperty("readiness").GetProperty("BindingMatches").GetBoolean() &&
                    s.GetProperty("readiness").GetProperty("Generation").GetInt64() == s.GetProperty("oldGeneration").GetInt64() &&
                    (test.Mode == "data" || s.GetProperty("readiness").GetProperty("Pending").GetBoolean()));
                armGeneration = armed.GetProperty("oldGeneration").GetInt64();
                await File.WriteAllTextAsync(Path.Combine(directory, "arm-" + caseId + ".json"), armed.GetRawText());
                byte[]? oldBytes = null;
                if (test.Mode == "data")
                {
                    // The old valid inventory write would change the reused slot's SSC state
                    // if dispatched; the following A01 candidate would punish the new account.
                    byte[] write = LabClient.Packet(5, w => LabClient.WriteInventory(w, old.Slot, 1, 8));
                    byte[] proof = LabClient.Packet(120, w => { w.Write(peer.Slot); w.Write((byte)1); });
                    oldBytes = write.Concat(proof).ToArray(); await old.SendBatch(write, proof);
                }
                else if (test.Mode == "eof") await old.DisposeAsync();
                else await host.ConsoleCommand("qa_m12_receive fault");
                var held = await State(s => s.GetProperty("stage").GetString() == "held");
                await File.WriteAllTextAsync(Path.Combine(directory, "held-" + caseId + ".json"), held.GetRawText());
                if (oldBytes is not null)
                    Check(held.GetProperty("receivedHex").GetString() == Convert.ToHexString(oldBytes), label + "-actual-completed-private-buffer-has-exact-two-old-frames");
                else Check(held.GetProperty("receivedLength").GetInt32() == (test.Mode == "eof" ? 0 : -1), label + "-actual-endread-outcome");
                if (test.Mode != "fault") await host.ConsoleCommand("qa_m12_receive retire");
                await State(s => s.GetProperty("current").GetProperty("generation").GetInt64() == 0);
                await old.DisposeAsync();
                var next = await Connect("M12RNew" + caseId, "127.0.0.3");
                await State(s => s.GetProperty("current").GetProperty("generation").GetInt64() >
                    held.GetProperty("oldGeneration").GetInt64());
                if (test.Stage != "admitted") { await next.Join(); next.PauseHeartbeat = true; }
                if (test.Stage == "authenticated-ssc") await next.RegisterAndLogin();
                if (test.Stage != "admitted") await next.PingAsync();
                string? sscBefore = test.Stage == "authenticated-ssc" ? CharacterInventory(Account(next.Name)) : null;
                int mark = host.ConsoleLines().Length;
                await host.ConsoleCommand("qa_m12_receive release");
                var released = await State(s => s.GetProperty("stage").GetString() == "released");
                var before = released.GetProperty("before"); var after = released.GetProperty("after");
                Check(before.GetProperty("slot").GetInt32() == slot && after.GetProperty("slot").GetInt32() == slot &&
                    !after.GetProperty("socketIsOld").GetBoolean(), label + "-actual-native-slot-reused-by-distinct-source");
                foreach (string property in new[] { "generation", "account", "loggedIn", "sentInventory", "canWrite", "inventory", "sanctions", "network" })
                    Check(before.GetProperty(property).GetRawText() == after.GetProperty(property).GetRawText(), label + "-old-completion-cannot-change-" + property);
                Check(!after.GetProperty("PendingTermination").GetBoolean(), label + "-new-socket-not-terminated");
                if (sscBefore is not null)
                    Check(sscBefore == CharacterInventory(Account(next.Name)), label + "-old-completion-cannot-change-persisted-ssc");
                Check(!host.ConsoleLines().Skip(mark).Any(line => line.Contains("ANTICHEAT_INCIDENT") || line.Contains("ANTICHEAT_NETWORK_DRYRUN")),
                    label + "-old-completion-does-not-produce-account-or-source-incident");
                if (test.Stage == "admitted") { await next.Join(); next.PauseHeartbeat = true; }
                if (test.Stage != "authenticated-ssc") await next.RegisterAndLogin();
                Check(next.Slot == slot && next.Authenticated && next.SscSlots.Count >= 350 && Account(next.Name) != oldAccount,
                    label + "-independent-account-authenticates-and-ssc-restores");
                Check(Bans(old.Name) == 0 && Bans(next.Name) == 0, label + "-zero-cross-account-bans");
                int bubbles = peer.Bubbles.Count, inventory = peer.InventoryUpdates.Count;
                await next.Send(120, w => { w.Write(next.Slot); w.Write((byte)0); });
                await peer.WaitUntil(() => peer.Bubbles.Skip(bubbles).Any(x => x.Player == next.Slot && x.Emote == 0), TimeSpan.FromSeconds(5));
                await next.Send(5, w => LabClient.WriteInventory(w, next.Slot, 1, 9));
                await peer.WaitUntil(() => peer.InventoryUpdates.Skip(inventory).Any(x => x.Player == next.Slot && x.Slot == 10 && x.Item == 9), TimeSpan.FromSeconds(5));
                Check(true, label + "-new-account-own-legal-emoji-and-inventory-still-forwarded");
                phases.Add(new { label, oldAccount, newAccount = Account(next.Name), slot,
                    oldSource = "127.0.0.2", newSource = "127.0.0.3", armed, held, released,
                    oldEvidence = old.Evidence(), newEvidence = next.Evidence() });
                if (caseId == cases.Length)
                {
                    int following = peer.InventoryUpdates.Count; int proofStart = host.ConsoleLines().Length;
                    await next.SendBatch(LabClient.Packet(120, w => { w.Write(peer.Slot); w.Write((byte)1); }),
                        LabClient.Packet(5, w => LabClient.WriteInventory(w, next.Slot, 1, 14)));
                    await next.WaitUntil(() => next.DisconnectReason is not null || next.Closed, TimeSpan.FromSeconds(8));
                    await Until(() => Bans(next.Name) == 1, TimeSpan.FromSeconds(10));
                    Check(next.DisconnectReason == "AntiCheat proven violation.", "new-account-own-first-proof-disconnects");
                    Check(host.ConsoleLines().Skip(proofStart).Any(line => line.Contains("ANTICHEAT_INCIDENT") &&
                        line.Contains("accountId=" + Account(next.Name)) && line.Contains("canceled=true", StringComparison.OrdinalIgnoreCase) &&
                        line.Contains("revoked=true", StringComparison.OrdinalIgnoreCase)), "new-account-own-first-proof-synchronously-cancels-and-revokes");
                    Check(peer.InventoryUpdates.Skip(following).All(x => x.Player != next.Slot || x.Slot != 10 || x.Item != 14),
                        "new-account-own-following-write-blocked-after-revocation");
                    Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + next.Name) == 1,
                        "new-account-own-proof-persists-one-permanent-account-ban");
                    string persistedInventory = await InventoryAfterNativeLeave(Account(next.Name));
                    await peer.PingAsync();
                    Check(peer.InventoryUpdates.Skip(following).All(x => x.Player != next.Slot || x.Slot != 10 || x.Item != 14),
                        "new-account-own-following-write-still-absent-after-native-leave");
                    Check(persistedInventory.Split('~')[10] == "9,1,0,0",
                        "only-new-account-own-legal-inventory-persists-after-first-proof");
                    host.RecordProvenAccount(next.Name, Account(next.Name), "A01.EmojiSenderMismatch");
                    await next.DisposeAsync();
                    var reconnect = await Connect(next.Name, "127.0.0.3");
                    try { await reconnect.Join(); }
                    catch (IOException) when (reconnect.DisconnectReason is not null || reconnect.Closed) { }
                    if (reconnect.DisconnectReason is null && !reconnect.Closed) await reconnect.LoginAndExpectRejected();
                    Check(reconnect.IsBanRejection, "new-account-own-proof-rejects-reconnect");
                }
                else
                {
                    // Dispose acknowledges only the client close. Request native retirement
                    // and observe its actual generation before selecting the next reused slot.
                    await host.ConsoleCommand("kick " + next.Name + " M12 completed owned lifecycle case");
                    await next.DisposeAsync();
                    await State(s => s.GetProperty("current").GetProperty("generation").GetInt64() == 0);
                }
            }
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try { await host.ConsoleCommand("qa_m12_receive release"); } catch (Exception) { }
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            { status, failure, phases, scope = "controlled real TCP completed-read interleaving; actual native admission, TShock auth/SSC and Core sanctions",
                stockClientGui = false, historicalCrossAccountMisbanProven = false,
                fixedPacingRequired = false, noAccountDetectionDisabled = true }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m12-receive-isolation:" + label);
        async Task<LabClient> Connect(string name, string address)
        { var client = await host.ConnectFrom!(name, IPAddress.Parse(address)); clients.Add(client); return client; }
        async Task<LabClient> Actor(string name, string address)
        { var client = await Connect(name, address); await client.Join(); await client.RegisterAndLogin(); Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-ordinary-auth-ssc"); return client; }
        long Account(string name) => Scalar("SELECT ID FROM Users WHERE Username=$name", name);
        long Bans(string name) => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + name);
        string CharacterInventory(long account)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT Inventory FROM tsCharacter WHERE Account=$id";
            query.Parameters.AddWithValue("$id", account); return Convert.ToString(query.ExecuteScalar()) ?? "";
        }
        async Task<string> InventoryAfterNativeLeave(long account)
        {
            // A committed ban and the client's disconnect message precede TShock's SSC save.
            // OnLeave clears Players[slot] first, then synchronously saves; the native Reset
            // body sets State=0 only after Leave returns. Await that lifecycle barrier, never
            // retry until the desired inventory value appears.
            string initial = CharacterInventory(account), actual = initial;
            JsonElement? observed = null;
            bool resetComplete = false;
            var timer = Stopwatch.StartNew();
            try
            {
                await State(state =>
                {
                    observed = state;
                    var current = state.GetProperty("current");
                    return current.GetProperty("actor").ValueKind == JsonValueKind.Null &&
                        current.GetProperty("generation").GetInt64() == 0 && current.GetProperty("State").GetInt32() == 0;
                });
                resetComplete = true;
                actual = CharacterInventory(account);
                return actual;
            }
            finally
            {
                if (!resetComplete) actual = CharacterInventory(account);
                string Slot10(string inventory) => inventory.Split('~') is { Length: > 10 } slots ? slots[10] : "<missing>";
                await File.WriteAllTextAsync(Path.Combine(directory, "ssc-leave-persistence.json"), JsonSerializer.Serialize(new
                {
                    account, nativeResetComplete = resetComplete, elapsedMs = timer.Elapsed.TotalMilliseconds,
                    deadlineMs = 8000, initialSlot10 = Slot10(initial), actualSlot10 = Slot10(actual),
                    expectedSlot10 = "9,1,0,0", forbiddenFollowingItem = 14, lastNativeState = observed,
                    barrier = "actual native actor-null/generation-zero/State-zero after synchronous TShock OnLeave SSC save",
                    desiredValueWasNotWaitPredicate = true
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = sql; query.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(query.ExecuteScalar() ?? 0L);
        }
        async Task<JsonElement> State(Func<JsonElement, bool> predicate)
        {
            var timer = Stopwatch.StartNew();
            string file = Path.Combine(host.ReportDirectory, "m12-receive-state-latest.json");
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                string requestId = Guid.NewGuid().ToString("N");
                var sentAt = DateTimeOffset.UtcNow;
                await host.ConsoleCommand("qa_m12_receive state " + requestId);
                // A single request stays outstanding until its full new snapshot is observed.
                // Predicate polling never fills the console queue with unacknowledged commands.
                var state = await ReadM12StateAsync(file, requestId, sentAt, timer);
                if (state.GetProperty("armId").GetString() == armId &&
                    (armGeneration == 0 || state.GetProperty("oldGeneration").GetInt64() == armGeneration) && predicate(state)) return state;
                await Task.Delay(25);
            }
            throw new TimeoutException("M12 receive-isolation bounded predicate wait expired.");
        }
    }
    private static async Task<JsonElement> ReadM12StateAsync(string path, string requestId, DateTimeOffset sentAt, Stopwatch timer)
    {
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                var state = json.RootElement;
                if (state.TryGetProperty("requestId", out var id) && id.GetString() == requestId &&
                    state.GetProperty("utc").GetDateTimeOffset() > sentAt) return state.Clone();
            }
            catch (IOException) { } catch (JsonException) { }
            await Task.Delay(25);
        }
        throw new TimeoutException("M12 receive-isolation fresh command acknowledgement expired.");
    }
    private static async Task Until(Func<bool> predicate, TimeSpan timeout)
    { await Until(() => Task.FromResult(predicate()), timeout); }
    private static async Task Until(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!await predicate())
        { if (timer.Elapsed > timeout) throw new TimeoutException("M12 receive-isolation bounded predicate wait expired."); await Task.Delay(25); }
    }
}
