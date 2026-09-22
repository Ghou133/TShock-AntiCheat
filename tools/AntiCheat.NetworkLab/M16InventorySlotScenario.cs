using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M16InventorySlotScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-inventory-slots"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var results = new List<object>(24);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) && File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-loopback-run");
            await host.FixtureSnapshot();
            var actor = await Actor("M16SlotActor"); var peer = await Actor("M16SlotPeer");
            await host.ConsoleCommand("qa_m16_inventory_slots " + actor.Name);
            var initial = await Fresh(DateTimeOffset.MinValue, "initial");
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("witnessInstalled").GetBoolean(), "product-and-passive-witness-installed");
            Check(initial.GetProperty("actor").GetProperty("samePlayer").GetBoolean() && initial.GetProperty("actor").GetProperty("HasSentInventory").GetBoolean(), "current-native-player-and-ssc-ready");
            Check(!initial.GetProperty("actor").GetProperty("bypass").GetBoolean() && !initial.GetProperty("actor").GetProperty("sscBypass").GetBoolean(), "no-test-bypass");
            // The legal control proves the actual product Read branch is reached before any
            // protected boundary request; assembly presence alone is insufficient.
            await Exchange("ordinary-inventory-control", 10, 9, true, true);
            var starts = initial.GetProperty("bankStarts").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var lengths = initial.GetProperty("bankLengths").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Check(starts.Length == 4 && lengths.Length == 4 && lengths.All(x => x is > 0 and < 200), "actual-bank-array-boundaries");
            for (int index = 0; index < 4; index++)
            {
                await Exchange($"bank-{index}-last-allocated", starts[index] + lengths[index] - 1, 9, true, index == 3);
                await Exchange($"bank-{index}-normal-air", starts[index] + lengths[index] - 1, 0, true, index == 3);
                await Exchange($"bank-{index}-reserved-nonair-rejected", starts[index] + lengths[index], 9, false, false);
                await Exchange($"bank-{index}-reserved-air-rejected", starts[index] + lengths[index], 0, false, false);
            }
            await Exchange("last-loadout-slot-air", initial.GetProperty("lastSlot").GetInt32(), 0, true, true);
            await Exchange("native-trash-scalar", initial.GetProperty("trash").GetInt32(), 9, true, false);
            await Exchange("native-trash-clear", initial.GetProperty("trash").GetInt32(), 0, true, false);
            await Exchange("ordinary-same-account-recovery", 10, 0, true, true);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-account-cheat-verdict");
            status = "passed";

            async Task Exchange(string label, int slot, short type, bool allowed, bool relayed)
            {
                var before = await State(label + "-before"); int received = peer.InventoryUpdates.Count;
                byte[] frame = LabClient.Packet(5, writer =>
                { writer.Write(actor.Slot); writer.Write((short)slot); writer.Write((short)(type == 0 ? 0 : 1)); writer.Write((byte)0); writer.Write(type); writer.Write((byte)0); });
                await actor.SendBatch(frame); await actor.PingAsync();
                if (allowed && relayed)
                    await peer.WaitUntil(() => peer.InventoryUpdates.Skip(received).Any(x => x.Player == actor.Slot && x.Slot == slot && x.Item == type), TimeSpan.FromSeconds(5));
                else await peer.Drain(TimeSpan.FromMilliseconds(80));
                var after = await State(label + "-after");
                long Delta(string name) => after.GetProperty(name).GetInt64() - before.GetProperty(name).GetInt64();
                Check(Delta("checks") == 1 && Delta("rejected") == (allowed ? 0 : 1), label + "-actual-product-guard-branch");
                Check(Delta("rawRequests") == 1 && after.GetProperty("witnessFaults").GetInt64() == 0, label + "-complete-raw-witness");
                var raw = after.GetProperty("lastRaw");
                Check(raw.GetProperty("Slot").GetInt32() == slot && raw.GetProperty("SamePlayer").GetBoolean() && raw.GetProperty("SameThread").GetBoolean() &&
                    !raw.GetProperty("HandledBefore").GetBoolean() && raw.GetProperty("HandledAfter").GetBoolean() == !allowed, label + "-exact-cancellation-boundary");
                Check(Delta("coreEntries") == (allowed ? 1 : 0) && Delta("nativeWrites") == (allowed ? 1 : 0), label + "-actual-core-and-native-setter-reachability");
                if (allowed)
                {
                    var actual = after.GetProperty("assets").GetProperty("Native").EnumerateArray().Single(x => x.GetProperty("Slot").GetInt32() == slot);
                    Check(actual.GetProperty("Type").GetInt32() == type && actual.GetProperty("Stack").GetInt32() == (type == 0 ? 0 : 1), label + "-native-stored-item");
                    if (relayed) Check(peer.InventoryUpdates.Skip(received).Any(x => x.Player == actor.Slot && x.Slot == slot && x.Item == type), label + "-actual-peer-forward");
                }
                else
                {
                    Check(raw.GetProperty("Before").GetRawText() == raw.GetProperty("After").GetRawText() &&
                        before.GetProperty("assets").GetRawText() == after.GetProperty("assets").GetRawText(), label + "-native-and-ssc-storage-unchanged");
                    Check(Delta("inventorySends") == 0 && !peer.InventoryUpdates.Skip(received).Any(x => x.Player == actor.Slot && x.Slot == slot), label + "-no-write-refund-or-peer-forward");
                }
                Healthy(actor); results.Add(new { label, allowed, slot, frameHex = Convert.ToHexString(frame), before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, results,
                originalIds = new[] { "v2:C01", "v2:A01" }, syntheticLoopbackTcp = true, nativeServerExecution = true,
                stockClientGui = false, wholeOriginalTaskComplete = false, accountBanQualification = false,
                unsafeUnprotectedNativeSetterInvoked = false, scope = "packet5 current mapped-array boundary only; no item source or quantity proof" }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m16-inventory-slot:" + label);
        async Task<LabClient> Actor(string name)
        {
            var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350, name + "-authenticated-ssc"); return actor;
        }
        void Healthy(LabClient actor)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            query.Parameters.AddWithValue("$name", "acc:" + actor.Name);
            Check(actor.Authenticated && !actor.Closed && actor.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0, actor.Name + "-healthy-unbanned");
        }
        async Task<JsonElement> State(string label)
        { var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m16_inventory_slots_state"); return await Fresh(requested, label); }
        async Task<JsonElement> Fresh(DateTimeOffset requested, string label)
        {
            string path = Path.Combine(host.ReportDirectory, "m16-inventory-slot-state-latest.json"); var timer = Stopwatch.StartNew();
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
            throw new TimeoutException("Fresh M16 mapped-slot state missing: " + label);
        }
    }
}
