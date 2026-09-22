using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M16RequestEgressScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-request-egress"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(); string status = "failed", failure = "";
        var options = new JsonSerializerOptions { WriteIndented = true };
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime"); await host.FixtureSnapshot();
            var actor = await Actor("M16EgressActor"); var peer = await Actor("M16EgressPeer");
            await host.ConsoleCommand("qa_m16_egress " + actor.Name + " " + peer.Name);
            var initial = await State("initial");
            Check(initial.GetProperty("healthy").GetBoolean() && initial.GetProperty("actors").EnumerateArray().All(a =>
                a.GetProperty("authenticated").GetBoolean() && a.GetProperty("sameObject").GetBoolean() &&
                !a.GetProperty("bypass").GetBoolean() && !a.GetProperty("console").GetBoolean()), "ordinary-supported-accounts");
            await actor.Chat("M16-normal-egress-before"); await actor.Chat("/who"); await actor.PingAsync();
            await peer.Drain(TimeSpan.FromMilliseconds(150)); await actor.Drain(TimeSpan.FromMilliseconds(150));
            var normal = await State("normal-chat-and-command");
            Check(peer.Messages.Any(m => m.Contains("M16-normal-egress-before", StringComparison.Ordinal)), "ordinary-chat-actually-arrives");
            Check(Delta(normal, initial, "blockedSends") == 0 && Delta(normal, initial, "admittedBytes") > 0,
                "ordinary-chat-and-command-counted-without-block");
            await actor.Chat("/m16_egress"); await actor.PingAsync(TimeSpan.FromSeconds(10));
            await actor.Drain(TimeSpan.FromMilliseconds(250)); await peer.Drain(TimeSpan.FromMilliseconds(250));
            var limited = await State("bounded-output-limited");
            int actorReceived = actor.Messages.Count(m => m.StartsWith("M16-EGRESS-FIXTURE-", StringComparison.Ordinal));
            int peerReceived = peer.Messages.Count(m => m.StartsWith("M16-EGRESS-FIXTURE-", StringComparison.Ordinal));
            Check(Delta(limited, normal, "commandEntries") == 1, "one-real-command-body-ran-no-cpu-prevention-claim");
            Check(Delta(limited, normal, "admittedSends") > 0 && Delta(limited, normal, "blockedSends") > 0 &&
                Delta(limited, normal, "admittedSends") + Delta(limited, normal, "blockedSends") == 256,
                "128-response-lines-times-two-actual-recipients-accounted");
            Check(actorReceived > 0 && peerReceived > 0 && actorReceived + peerReceived == Delta(limited, normal, "admittedSends"),
                "actual-recipient-messages-match-admitted-transport-counts");
            Check(Delta(limited, normal, "admittedBytes") > 800000 && Delta(limited, normal, "blockedBytes") > 0,
                "actual-serialized-byte-budget-effective");
            Healthy(actor, "limited-actor-not-banned"); Healthy(peer, "receiving-peer-not-banned");
            await peer.Chat("M16-independent-egress-peer"); await peer.PingAsync(); await actor.Drain(TimeSpan.FromMilliseconds(150));
            Check(actor.Messages.Any(m => m.Contains("M16-independent-egress-peer", StringComparison.Ordinal)), "independent-same-NAT-account-still-sends");
            var independent = await State("independent-account");
            Check(Delta(independent, limited, "blockedSends") == 0, "sender-exhaustion-not-charged-to-peer");
            await Task.Delay(4200); // Natural four-second actor refill, no budget reset.
            await actor.Chat("M16-recovered-egress"); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(150));
            var recovered = await State("natural-refill-recovery");
            Check(peer.Messages.Any(m => m.Contains("M16-recovered-egress", StringComparison.Ordinal)), "normal-chat-after-natural-refill");
            Check(Delta(recovered, independent, "blockedSends") == 0 && Delta(recovered, limited, "commandEntries") == 0,
                "no-replay-of-dropped-output-or-command");
            Check(recovered.GetProperty("healthy").GetBoolean(), "final-supported-guard");
            Check(!host.ConsoleLines().Skip(logStart).Any(l => l.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-permanent-sanction-from-resource-budget");
            Healthy(actor, "recovered-actor-not-banned"); status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure,
                syntheticTcp = true, realNativeServer = true, stockClient = false,
                scope = "I03/I04 synchronous authenticated chat/command NetTextModule egress bytes; fixed bounded artificial command response",
                commandCpuProtection = false, worldStatePacketsLimited = false, accountBan = false }, options));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool result, string label) => host.Assert(result, "m16-request-egress:" + label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-real-login-SSC"); return client;
        }
        void Healthy(LabClient client, string label)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, label);
        }
        async Task<JsonElement> State(string label)
        {
            var utc = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m16_egress_state");
            string path = Path.Combine(host.ReportDirectory, "m16-egress-state-latest.json"); var timer = Stopwatch.StartNew();
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
            throw new TimeoutException("M16 egress snapshot missing: " + label);
        }
        static long Delta(JsonElement after, JsonElement before, string name) => after.GetProperty(name).GetInt64() - before.GetProperty(name).GetInt64();
    }
}
