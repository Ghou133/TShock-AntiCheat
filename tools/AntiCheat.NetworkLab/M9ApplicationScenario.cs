using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M9ApplicationScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m9-application"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var frames = new List<object>();
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-real-runtime");
            await host.FixtureSnapshot();
            var actor = await Actor("M9AppActor"); var peer = await Actor("M9AppPeer");
            await host.ConsoleCommand("qa_m9_application " + actor.Name + " " + peer.Name);
            var initial = await State("initial");
            Check(initial.GetProperty("witnessInstalled").GetBoolean(), "actual-command-body-witness-installed");
            Check(initial.GetProperty("actors").EnumerateArray().All(x => !x.GetProperty("bypass").GetBoolean() &&
                !x.GetProperty("consolePermission").GetBoolean() && x.GetProperty("samePlayer").GetBoolean()), "normal-authenticated-no-bypass");

            byte[][] normal = Enumerable.Range(0, 6).Select(i => Chat(i % 2 == 0 ? "/who" : ".who")).ToArray();
            await actor.SendBatch(normal); await actor.PingAsync();
            await actor.Chat("[c/00ff00:M9-normal-chat]"); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(200));
            var legal = await State("legal-burst");
            Check(Entries(legal, actor) - Entries(initial, actor) == 6 && Parsed(legal, actor) - Parsed(initial, actor) == 6,
                "legal-burst-enters-native-parser-and-both-prefixes");
            Check(legal.GetProperty("rejected").GetInt64() == initial.GetProperty("rejected").GetInt64(), "legal-burst-not-limited");
            Check(peer.Messages.Any(m => m.Contains("M9-normal-chat", StringComparison.Ordinal)), "normal-tagged-chat-delivered");

            int deniedBefore = actor.Messages.Count;
            await actor.Chat("/qa_status"); await actor.PingAsync(); await actor.Drain(TimeSpan.FromMilliseconds(100));
            var permission = await State("permission-denied");
            Check(Entries(permission, actor) == Entries(legal, actor) + 1 && Parsed(permission, actor) == Parsed(legal, actor) + 1,
                "ordinary-no-permission-attempt-enters-core-permission-check");
            Check(actor.Messages.Skip(deniedBefore).Any(m => m.Contains("access", StringComparison.OrdinalIgnoreCase)),
                "core-permission-denial-response-preserved");
            Healthy(actor, "no-permission-is-not-cheating");

            await Task.Delay(4200); // Real refill; no fixture reset or forced limiter state.
            var beforeBurst = await State("before-over-budget");
            string suppressed = "M9-budget-suppressed-" + new string('x', 200);
            var attack = Enumerable.Range(0, 160).Select(_ => Chat("/who")).Append(Chat(suppressed)).ToArray();
            frames.Add(new { phase = "legal-burst", source = "synthetic-tcp82", hex = normal.Select(Convert.ToHexString).ToArray() });
            frames.Add(new { phase = "bounded-overload", source = "synthetic-tcp82", requests = 160,
                commandHex = Convert.ToHexString(attack[0]), trailingChatHex = Convert.ToHexString(attack[^1]) });
            await actor.SendBatch(attack); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(150));
            var rejected = await State("over-budget");
            long allowed = rejected.GetProperty("admitted").GetInt64() - beforeBurst.GetProperty("admitted").GetInt64();
            long blocked = rejected.GetProperty("rejected").GetInt64() - beforeBurst.GetProperty("rejected").GetInt64();
            long commandEntries = Entries(rejected, actor) - Entries(beforeBurst, actor);
            Check(allowed + blocked == 161 && blocked > 100 && allowed > 0, "actual-overload-admission-and-rejection-accounted");
            Check(commandEntries == allowed && Parsed(rejected, actor) - Parsed(beforeBurst, actor) == allowed,
                "rejected-requests-stop-before-native-command-body-and-parser");
            Check(!peer.Messages.Any(m => m.Contains("M9-budget-suppressed-", StringComparison.Ordinal)), "over-budget-chat-not-broadcast");
            Healthy(actor, "limited-session-remains-connected-and-unbanned");

            var innocentBefore = Entries(rejected, peer);
            await peer.Chat("/who"); await peer.PingAsync();
            var independent = await State("independent-same-NAT");
            Check(Entries(independent, peer) == innocentBefore + 1, "independent-account-shared-loopback-still-dispatches");
            Healthy(peer, "independent-account-not-punished");

            await Task.Delay(4200);
            var idle = await State("refilled-no-replay");
            Check(Entries(idle, actor) == Entries(rejected, actor), "refill-does-not-replay-discarded-commands");
            await actor.Chat("/who"); await actor.PingAsync();
            await actor.Chat("M9-recovered-chat"); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(150));
            var recovered = await State("recovered");
            Check(Entries(recovered, actor) == Entries(idle, actor) + 1 &&
                recovered.GetProperty("rejected").GetInt64() == idle.GetProperty("rejected").GetInt64(), "same-session-command-resumes-after-refill");
            Check(peer.Messages.Any(m => m.Contains("M9-recovered-chat", StringComparison.Ordinal)), "same-session-chat-resumes");
            var sourceEvents = host.ConsoleLines().Skip(logStart).Where(x => x.Contains("application-chat/admission-1.0.0", StringComparison.Ordinal)).ToArray();
            Check(sourceEvents.Length == 1 && sourceEvents[0].Contains("127.0.0.1", StringComparison.Ordinal), "existing-source-event-cooldown-bounds-reject-log");
            Check(!host.ConsoleLines().Skip(logStart).Any(x => x.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "application-controls-create-no-cheat-incidents");
            Healthy(actor, "recovered-actor-unbanned"); Healthy(peer, "peer-unbanned");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, frames, syntheticTcp = true, gui = "not_executed", noReplay = true,
                support = "pre-TShock-OnChat request/character admission; TSAPI string decoding already occurred",
                limits = "32 weighted burst / 8 per second per connection and authenticated account; source/global use existing NetworkControls",
                traces = host.ConsoleLines().Skip(logStart).Where(x => x.Contains("ANTICHEAT_NETWORK_DRYRUN", StringComparison.Ordinal)).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m9-application:" + label);
        async Task<LabClient> Actor(string name)
        {
            var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350, name + "-real-auth-SSC"); return actor;
        }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m9_application_state");
            string path = Path.Combine(host.ReportDirectory, "m9-application-state-latest.json"); var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path); using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return doc.RootElement.Clone(); }
                    }
                    catch (Exception error) when (error is IOException or JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Application state unavailable: " + label);
        }
        void Healthy(LabClient actor, string label)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + actor.Name);
            Check(actor.Authenticated && !actor.Closed && actor.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, label);
        }
    }

    private static byte[] Chat(string text) => LabClient.Packet(82, w => { w.Write((ushort)1); w.Write("Say"); w.Write(text); });
    private static long Entries(JsonElement state, LabClient player) => state.GetProperty("actors").EnumerateArray()
        .Single(x => x.GetProperty("Name").GetString() == player.Name).GetProperty("Entries").GetInt64();
    private static long Parsed(JsonElement state, LabClient player) => state.GetProperty("actors").EnumerateArray()
        .Single(x => x.GetProperty("Name").GetString() == player.Name).GetProperty("Parsed").GetInt64();
}
