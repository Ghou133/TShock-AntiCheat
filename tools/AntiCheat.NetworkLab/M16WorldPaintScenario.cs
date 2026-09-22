using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real TCP64 and ordinary Bouncer path; trusted fixture only schedules permission-race counterexamples.</summary>
internal static class M16WorldPaintScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-world-paint"); Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(); var evidence = new List<object>(); var frames = new List<object>();
        string status = "failed", failure = "";
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime");
            await host.FixtureSnapshot();
            var actor = await Actor("M16WallActor"); var peer = await Actor("M16WallPeer");
            await host.ConsoleCommand("qa_m16_wall " + actor.Name);
            var prepared = await ReadFresh("prepared", DateTimeOffset.MinValue);
            short x = prepared.GetProperty("x").GetInt16(), y = prepared.GetProperty("y").GetInt16();
            long account = prepared.GetProperty("actor").GetProperty("account").GetInt64();
            Check(prepared.GetProperty("healthy").GetBoolean(), "guard-supported");
            Check(!prepared.GetProperty("blockActive").GetBoolean() && prepared.GetProperty("wall").GetInt32() != 0,
                "normal-wall-only-cell");
            await actor.MoveTo((x - 2) * 16, (y - 1) * 16); await actor.PingAsync();
            await peer.MoveTo((x - 4) * 16, (y - 1) * 16); await peer.PingAsync();
            await Paint("allowed", 7, 7, "normal-wall");
            await Paint("core-denied", 8, 7, "normal-protected-wall-core-denial");
            await Paint("allowed", 9, 9, "normal-after-core-denial");
            await Paint("deny-before", 10, 9, "late-permission-prewrite-block");
            await Paint("revoke-during", 11, 9, "one-field-recovery");
            await Paint("allowed", 12, 12, "normal-after-recovery");
            await Paint("aba-during", 13, 13, "aba-conflict-kept");
            await Paint("replace-during", 14, 14, "replacement-conflict-kept");
            await Paint("later-paint-during", 15, 18, "later-paint-not-overwritten");
            await Paint("allowed", 16, 16, "normal-after-conflicts");
            Check(!host.ConsoleLines().Skip(logStart).Any(l => l.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-cheat-incident-from-permission-or-conflict");
            status = "passed";

            async Task Paint(string mode, byte submitted, byte expected, string label)
            {
                await host.ConsoleCommand("qa_m16_wall_mode " + mode);
                var before = await State(label + "-before");
                bool normal = mode == "allowed", coreDenied = mode == "core-denied", blocked = mode == "deny-before", restored = mode == "revoke-during";
                bool conflict = mode is "aba-during" or "replace-during" or "later-paint-during";
                Check(before.GetProperty("healthy").GetBoolean() && before.GetProperty("unknown").GetInt64() == 0, label + "-effective-supported-branch");
                Check(before.GetProperty("actor").GetProperty("account").GetInt64() == account &&
                    !before.GetProperty("actor").GetProperty("bypass").GetBoolean() &&
                    before.GetProperty("actor").GetProperty("allowed").GetBoolean() != coreDenied, label + "-ordinary-account-permission");
                await peer.Drain(TimeSpan.FromMilliseconds(100)); int relaysBefore = peer.PacketCount(64), resyncBefore = peer.PacketCount(20);
                var frame = LabClient.Packet(64, w => { w.Write(x); w.Write(y); w.Write(submitted); w.Write((byte)0); });
                frames.Add(new { label, kind = "synthetic-TCP64", hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync();
                if (normal) await peer.WaitUntil(() => peer.PacketCount(64) > relaysBefore, TimeSpan.FromSeconds(5));
                await peer.Drain(TimeSpan.FromMilliseconds(180));
                var after = await State(label + "-after");
                Check(after.GetProperty("healthy").GetBoolean() && after.GetProperty("unknown").GetInt64() == 0, label + "-supported-through-postcheck");
                Check(after.GetProperty("color").GetByte() == expected && after.GetProperty("blockColor").GetInt32() == 3 &&
                    after.GetProperty("wall").GetInt32() == prepared.GetProperty("wall").GetInt32() &&
                    !after.GetProperty("blockActive").GetBoolean(), label + "-actual-world-exact-surface-state");
                Check(Delta(after, before, "allowed") == (blocked || coreDenied ? 0 : 1) &&
                    Delta(after, before, "blocked") == (blocked ? 1 : 0) && Delta(after, before, "restored") == (restored ? 1 : 0) &&
                    Delta(after, before, "refused") == (conflict ? 1 : 0), label + "-actual-product-result-counters");
                Check(Delta(after, before, "relaySuppressed") == (normal || coreDenied ? 0 : 1) &&
                    peer.PacketCount(64) - relaysBefore == (normal ? 1 : 0), label + "-peer64-relay-or-suppression");
                if (!blocked && !coreDenied)
                {
                    var completion = after.GetProperty("journal").EnumerateArray().Last();
                    Check(completion.GetProperty("PacketType").GetInt32() == 64 && completion.GetProperty("AccountId").GetInt64() == account &&
                        completion.GetProperty("Before").GetByte() == before.GetProperty("color").GetByte() &&
                        completion.GetProperty("Current").GetByte() == expected &&
                        completion.GetProperty("Outcome").GetString() == (normal ? "authorized-commit" : restored ? "authorization-lost-restored" : "authorization-lost-conflict-no-restore"),
                        label + "-bounded-journal-real-request-and-outcome");
                }
                Healthy(actor, label + "-actor-unbanned"); Healthy(peer, label + "-peer-unbanned");
                evidence.Add(new { label, mode, submitted, expected, before, after, actualPeer64 = peer.PacketCount(64) - relaysBefore,
                    actualPeer20 = peer.PacketCount(20) - resyncBefore,
                    source = "real TCP; race/conflict modes are controlled trusted-host callbacks, not player cheat proof" });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(frames, options));
            await File.WriteAllTextAsync(Path.Combine(directory, "transactions.json"), JsonSerializer.Serialize(evidence, options));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure,
                cases = evidence.Count, syntheticTcp = true, realNativeServer = true, stockClient = false, accountBan = false,
                scope = "H01/H06 ordinary wall-paint local authorization transition; no item/refund/drop or later-world rollback" }, options));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool result, string label) => host.Assert(result, "m16-world-paint:" + label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-real-login-and-SSC"); return client;
        }
        void Healthy(LabClient client, string label)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, label);
        }
        async Task<JsonElement> State(string label)
        { var utc = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m16_wall_state"); return await ReadFresh(label, utc); }
        async Task<JsonElement> ReadFresh(string label, DateTimeOffset utc)
        {
            string path = Path.Combine(host.ReportDirectory, "m16-wall-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path)) try
                {
                    string text = await File.ReadAllTextAsync(path); using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= utc)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("M16 wall snapshot missing: " + label);
        }
        static long Delta(JsonElement after, JsonElement before, string name) => after.GetProperty(name).GetInt64() - before.GetProperty(name).GetInt64();
    }
}
