using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M9CraftScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m9-craft"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var frames = new List<object>(); var results = new List<object>();
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime"); await host.FixtureSnapshot();
            var actor = await Actor("M9CraftActor"); var peer = await Actor("M9CraftPeer");
            await host.ConsoleCommand("qa_m9_craft " + actor.Name);
            var initial = await State("initial");
            var chest = initial.GetProperty("chest"); short chestId = chest.GetProperty("id").GetInt16();
            int x = chest.GetProperty("x").GetInt32(), y = chest.GetProperty("y").GetInt32();
            ushort module = initial.GetProperty("module").GetUInt16();
            Check(initial.GetProperty("finalGuardHealthy").GetBoolean(), "real-native-final-guard-installed");
            Check(!initial.GetProperty("actor").GetProperty("bypass").GetBoolean() &&
                !initial.GetProperty("actor").GetProperty("sscBypass").GetBoolean(), "ordinary-actor-no-bypass");
            await actor.MoveTo((x - 3) * 16, (y - 1) * 16); await actor.PingAsync();
            await peer.MoveTo((x - 5) * 16, (y - 1) * 16); await peer.PingAsync();
            await Craft(actor, "legal-effective-branch", [(9, 3), (9, 2)], true, 25, 2, false);
            await host.ConsoleCommand("qa_m9_craft_arm");
            var armed = await State("armed"); Check(armed.GetProperty("m9CraftMutationArmed").GetBoolean(), "controlled-host-mutation-armed");
            await Craft(actor, "late-host-overlap-blocked", [(9, 1)], false, 25, 0, true);
            await Craft(actor, "same-actor-recovery", [(9, 2)], true, 23, 1, false);
            await Craft(peer, "independent-account-normal", [(9, 2)], true, 21, 1, false);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-fabricated-account-verdict");
            status = "passed";

            async Task Craft(LabClient client, string label, (int Type, int Stack)[] requirements,
                bool approved, int expectedWood, int consumes, bool finalRejected)
            {
                await peer.Drain(TimeSpan.FromMilliseconds(100)); var before = await State(label + "-before");
                int responseStart = client.CraftResponses.Count, writeStart = peer.ChestItems.Count;
                var frame = LabClient.Packet(82, writer =>
                {
                    writer.Write(module); writer.Write7BitEncodedInt(requirements.Length);
                    foreach (var req in requirements) { writer.Write(req.Type); writer.Write7BitEncodedInt(req.Stack); }
                    writer.Write7BitEncodedInt(1); writer.Write7BitEncodedInt(chestId);
                });
                frames.Add(new { label, source = "synthetic-TCP82", frame = Convert.ToHexString(frame) });
                await client.SendBatch(frame);
                await client.WaitUntil(() => client.CraftResponses.Skip(responseStart).Any(r => r.Module == module), TimeSpan.FromSeconds(5));
                await client.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(180));
                var after = await State(label + "-after");
                var responses = client.CraftResponses.Skip(responseStart).Where(r => r.Module == module).ToArray();
                Check(responses.Length == 1 && responses[0].Approved == approved, label + "-single-real-response");
                Check(Wood(after) == expectedWood && Consumes(after) - Consumes(before) == consumes, label + "-native-quantity-and-consumption");
                Check(after.GetProperty("finalChecks").GetInt64() == before.GetProperty("finalChecks").GetInt64() + 1 &&
                    after.GetProperty("finalRejections").GetInt64() == before.GetProperty("finalRejections").GetInt64() + (finalRejected ? 1 : 0),
                    label + "-final-product-branch-genuinely-entered");
                Check(after.GetProperty("earlyRejections").GetInt64() == before.GetProperty("earlyRejections").GetInt64(), label + "-early-preflight-passed");
                Check(after.GetProperty("effectsDropped").GetInt32() == 0 && after.GetProperty("finalSteps").GetInt32() <= 131072, label + "-bounded-complete-evidence");
                if (!approved)
                {
                    Check(chest.GetProperty("id").GetInt16() == chestId &&
                        before.GetProperty("chest").GetRawText() == after.GetProperty("chest").GetRawText(), label + "-all-slots-identical");
                    Check(!peer.ChestItems.Skip(writeStart).Any(c => c.Chest == chestId), label + "-zero-peer-slot-writes");
                    Check(after.GetProperty("m9CraftInjected").GetInt32() == 1 && !after.GetProperty("m9CraftMutationArmed").GetBoolean(), label + "-one-controlled-callback");
                }
                Healthy(client); results.Add(new { label, approved, expectedWood, consumes, before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, frames, results,
                syntheticTcp = true, nativeServerExecution = true, originalClientGui = false,
                scope = "native post-final-filter quantity protection including controlled host mutation; no native-only client duplication claim; no new ban qualification" },
                new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool condition, string label) => host.Assert(condition, "m9-craft:" + label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-actual-account-and-SSC"); return client;
        }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, client.Name + "-unbanned-and-active");
        }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m9_craft_state");
            string path = Path.Combine(host.ReportDirectory, "m9-craft-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path)) try
                {
                    string content = await File.ReadAllTextAsync(path); using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), content); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Fresh M9 craft state missing: " + label);
        }
        static int Wood(JsonElement state) => state.GetProperty("chest").GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("type").GetInt32() == 9).Sum(i => i.GetProperty("stack").GetInt32());
        static int Consumes(JsonElement state) => state.GetProperty("consumeEntries").GetArrayLength();
    }
}
