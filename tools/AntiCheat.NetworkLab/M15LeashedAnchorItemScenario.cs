using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M15LeashedAnchorItemScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host, LabClient actor, LabClient peer)
    {
        string directory = Path.Combine(host.ReportDirectory, "m15-leashed"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        List<object> outcomes = [], frames = []; string status = "failed", failure = "";
        int logStart = host.ConsoleLines().Length;
        try
        {
            await Command("setup"); var setup = await Snapshot("setup");
            peer.LeashedModuleId = setup.GetProperty("moduleId").GetUInt16();
            Check(setup.GetProperty("observersInstalled").GetBoolean() && setup.GetProperty("targets").GetArrayLength() == 2 &&
                setup.GetProperty("targets").EnumerateArray().All(t => t.GetProperty("item").GetInt32() == 0 &&
                    t.GetProperty("leashedId").ValueKind == JsonValueKind.Null && t.GetProperty("actorAllowed").GetBoolean() &&
                    !t.GetProperty("peerAllowed").GetBoolean() && t.GetProperty("tileValid").GetBoolean()), "two-native-empty-anchors");
            foreach (int family in new[] { 9, 10 })
            {
                await Command("select " + family);
                await Step(family + "-legal-first-insert", family, actor, true);
                await Step(family + "-legal-same-item-respawn", family, actor, true);
                await Command("deny");
                await Step(family + "-revoked-same-item", family, actor, false);
                await Step(family + "-revoked-clear-item", family, actor, false, 0);
                await Step(family + "-independent-peer-denied", family, peer, false);
                await Command("allow");
                await Step(family + "-same-account-recovers", family, actor, true);
            }
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "permission-block-is-not-account-proof");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await Command("finish"); var final = await Snapshot("finished");
            Check(!final.GetProperty("observersInstalled").GetBoolean() && final.GetProperty("targets").EnumerateArray()
                .All(t => !t.GetProperty("regionPresent").GetBoolean()), "own-observers-and-regions-removed");
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientGui = false,
                scope = "156 exact kite/critter anchor current one-cell build permission before item write and leashed-entity respawn",
                expectedAccountBan = false, clientOptimisticConsumption = "not-executed", clientInventoryAfterDenial = "not-executed",
                outcomes });
        }

        async Task Step(string label, int family, LabClient subject, bool write, short? requestedItem = null)
        {
            var before = await Snapshot(label + "-before"); var target = Target(before, family);
            Check(target.GetProperty(subject == actor ? "actorAllowed" : "peerAllowed").GetBoolean() == write,
                label + "-current-permission-precondition");
            short x = target.GetProperty("x").GetInt16(), y = target.GetProperty("y").GetInt16();
            short item = requestedItem ?? target.GetProperty("requestedItem").GetInt16();
            await peer.PingAsync();
            int fullSyncsBefore = peer.LeashedFullSyncs.Count(s => s.X == x && s.Y == y);
            byte[] frame = LabClient.Packet(156, writer => { writer.Write(x); writer.Write(y); writer.Write(item); });
            frames.Add(new { label, subject = subject.Name, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
            await subject.SendBatch(frame); await subject.PingAsync(); await peer.PingAsync();
            var after = await Snapshot(label + "-after"); var raw = after.GetProperty("lastRaw"); var current = Target(after, family);
            Check(after.GetProperty("raw").GetInt64() == before.GetProperty("raw").GetInt64() + 1 &&
                raw.GetProperty("receivingSlot").GetInt32() == subject.Slot && raw.GetProperty("x").GetInt32() == x &&
                raw.GetProperty("y").GetInt32() == y && raw.GetProperty("item").GetInt32() == item &&
                raw.GetProperty("handled").GetBoolean() == !write, label + "-fresh-real-receive-and-cancel");
            Check(after.GetProperty("safetyBlocked").GetInt64() - before.GetProperty("safetyBlocked").GetInt64() == (write ? 0 : 1),
                label + "-product-safety-executed");
            Check(current.GetProperty("item").GetInt32() == (write ? item : target.GetProperty("item").GetInt32()) && current.GetProperty("leashedActive").GetBoolean() &&
                current.GetProperty("registeredLeashed").GetBoolean(), label + "-actual-item-and-native-leashed-state");
            Check((target.GetProperty("leashedId").GetRawText() != current.GetProperty("leashedId").GetRawText()) == write,
                label + "-native-leashed-id-replaced-only-on-allow");
            var outgoing = peer.LeashedFullSyncs.Where(s => s.X == x && s.Y == y).Skip(fullSyncsBefore).ToArray();
            Check(peer.LeashedFullSyncsDropped == 0 && outgoing.Length == (write ? 1 : 0) && (!write ||
                outgoing[0].Slot == current.GetProperty("leashedId").GetInt32() && outgoing[0].Type == current.GetProperty("leashedType").GetInt32()),
                label + "-independent-peer-exact-leashed-full-sync");
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            query.Parameters.AddWithValue("$name", "acc:" + subject.Name);
            Check(subject.Authenticated && !subject.Closed && subject.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0,
                label + "-connected-without-permanent-ban");
            outcomes.Add(new { label, family, write, before, after, outgoing = outgoing.Select(s => new { s.Slot, s.Type, s.X, s.Y }),
                dropped = peer.LeashedFullSyncsDropped });
        }
        Task Command(string text) => host.ConsoleCommand("qa_m14r_entity leashed " + text);
        void Check(bool condition, string label) => host.Assert(condition, "m15-leashed:" + label);
        static JsonElement Target(JsonElement snapshot, int family) => snapshot.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("family").GetInt32() == family);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow; await Command("state");
            string path = Path.Combine(host.ReportDirectory, "m15-leashed-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path)) try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
                    if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { var result = document.RootElement.Clone(); await Write(label + ".json", result); return result; }
                }
                catch (JsonException) { }
                catch (IOException) { }
                await Task.Delay(40);
            }
            throw new TimeoutException("Fresh bounded leashed snapshot unavailable: " + label);
        }
    }
}
