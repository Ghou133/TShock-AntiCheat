using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Three fixed requests against the existing owned loopback acceptance harness.
/// No configurable target, replay loop, exploit-consequence probe, or stock-client UI claim.</summary>
internal static class M10QuickStackScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m10-quick-stack"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var frames = new List<object>(3); var results = new List<object>(3);
        var json = new JsonSerializerOptions { WriteIndented = true };
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-isolated-run");
            await host.FixtureSnapshot();
            var actor = await Actor("M10QuickActor"); var peer = await Actor("M10QuickPeer");
            await host.ConsoleCommand("qa_m10_quickstack " + actor.Name);
            var initial = await Fresh(DateTimeOffset.MinValue, "prepared");
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("witnessInstalled").GetBoolean(), "guard-and-native-witness-installed");
            Check(initial.GetProperty("scope").GetString() == "TestLab", "effective-testlab-execution-scope");
            var owner = initial.GetProperty("actor");
            Check(owner.GetProperty("IsLoggedIn").GetBoolean() && owner.GetProperty("HasSentInventory").GetBoolean() &&
                owner.GetProperty("samePlayer").GetBoolean() && !owner.GetProperty("bypass").GetBoolean() &&
                !owner.GetProperty("sscBypass").GetBoolean(), "ordinary-current-account-and-ssc");
            var chest = initial.GetProperty("chest"); short chestId = chest.GetProperty("id").GetInt16();
            int x = chest.GetProperty("x").GetInt32(), y = chest.GetProperty("y").GetInt32();
            await actor.MoveTo((x - 3) * 16, (y - 1) * 16); await actor.PingAsync();
            await peer.MoveTo((x - 5) * 16, (y - 1) * 16); await peer.PingAsync();
            await Exchange("normal-first-transfer", [10], permitted: true, sourceSlot: 10, expectedSource: 0, expectedChest: 10006);
            await Exchange("repeated-source-rejected", [11, 11], permitted: false, sourceSlot: 11, expectedSource: 11, expectedChest: 10006);
            await Exchange("same-account-normal-after-rejection", [11], permitted: true, sourceSlot: 11, expectedSource: 0, expectedChest: 10017);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-account-cheat-verdict");
            status = "passed";

            async Task Exchange(string label, short[] slots, bool permitted, int sourceSlot, int expectedSource, int expectedChest)
            {
                await peer.Drain(TimeSpan.FromMilliseconds(100));
                var before = await State(label + "-before"); int peerWrites = peer.ChestItems.Count;
                var frame = LabClient.Packet(85, writer =>
                { writer.Write(slots.Length); foreach (short slot in slots) writer.Write(slot); writer.Write(false); });
                frames.Add(new { label, source = "fixed-synthetic-loopback-TCP85-acceptance", hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(160));
                var after = await State(label + "-after");
                long Delta(string name) => after.GetProperty(name).GetInt64() - before.GetProperty(name).GetInt64();
                Check(Delta("checks") == 1 && Delta("rejected") == (permitted ? 0 : 1) &&
                    Delta("duplicateRejected") == (permitted ? 0 : 1), label + "-actual-product-branch");
                Check(Delta("rawRequests") == 1 && after.GetProperty("witnessFaults").GetInt64() == 0, label + "-bounded-complete-raw-witness");
                var raw = after.GetProperty("lastRaw");
                Check(raw.GetProperty("Sequence").GetInt64() == after.GetProperty("rawRequests").GetInt64() &&
                    raw.GetProperty("SamePlayer").GetBoolean() && raw.GetProperty("SameThread").GetBoolean() &&
                    !raw.GetProperty("HandledBefore").GetBoolean() && raw.GetProperty("HandledAfter").GetBoolean() == !permitted,
                    label + "-same-dispatch-cancellation-and-identity");
                Check(Delta("nativeReads") == (permitted ? 1 : 0) && Delta("nativeTransfers") == (permitted ? 1 : 0),
                    label + "-native-reader-and-transfer-entry");
                var afterAssets = after.GetProperty("assets");
                int source = afterAssets.GetProperty("Inventory").EnumerateArray().Single(item => item.GetProperty("Slot").GetInt32() == sourceSlot).GetProperty("Stack").GetInt32();
                int wood = afterAssets.GetProperty("Chest").EnumerateArray().Where(item => item.GetProperty("Type").GetInt32() == 9).Sum(item => item.GetProperty("Stack").GetInt32());
                Check(source == expectedSource && wood == expectedChest, label + "-actual-source-and-chest-quantity");
                int actualPeerWrites = peer.ChestItems.Skip(peerWrites).Count(item => item.Chest == chestId);
                if (permitted)
                {
                    Check(Delta("chestSends") >= 1 && actualPeerWrites >= 1 && Delta("inventorySends") >= 1,
                        label + "-actual-native-sync-and-peer-receipt");
                }
                else
                {
                    Check(raw.GetProperty("Before").GetRawText() == raw.GetProperty("After").GetRawText(),
                        label + "-exact-dispatch-all-input-chest-and-world-items-unchanged");
                    var beforeAssets = before.GetProperty("assets");
                    Check(beforeAssets.GetProperty("Inventory").GetRawText() == afterAssets.GetProperty("Inventory").GetRawText() &&
                        beforeAssets.GetProperty("VoidBag").GetRawText() == afterAssets.GetProperty("VoidBag").GetRawText() &&
                        beforeAssets.GetProperty("Chest").GetRawText() == afterAssets.GetProperty("Chest").GetRawText(),
                        label + "-stored-assets-unchanged");
                    Check(Delta("chestSends") == 0 && Delta("inventorySends") == 0 && actualPeerWrites == 0,
                        label + "-zero-transfer-refund-or-slot-sync");
                }
                Healthy(actor); results.Add(new { label, permitted, before, after, actualPeerWrites });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure,
                frames, results, syntheticLoopbackTcp = true, nativeServerExecution = true, stockClientGui = false,
                accountBanQualification = false, scope = "three fixed isolated acceptance requests; slot-list safety BLOCK only" }, json));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool condition, string label) => host.Assert(condition, "m10-quick-stack:" + label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-authenticated-ssc"); return client;
        }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0,
                client.Name + "-unbanned-and-active");
        }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m10_quickstack_state"); return await Fresh(requested, label);
        }
        async Task<JsonElement> Fresh(DateTimeOffset requested, string label)
        {
            string path = Path.Combine(host.ReportDirectory, "m10-quick-stack-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                if (File.Exists(path)) try
                {
                    string content = await File.ReadAllTextAsync(path); using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), content); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Fresh M10 Quick Stack state missing: " + label);
        }
    }
}
