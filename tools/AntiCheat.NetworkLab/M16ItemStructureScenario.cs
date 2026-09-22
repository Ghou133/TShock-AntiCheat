using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M16ItemStructureScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-item-structure"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var results = new List<object>(32);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) && File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-loopback-run");
            await host.FixtureSnapshot();
            var actor = await Actor("M16ItemActor"); var peer = await Actor("M16ItemPeer");
            await host.ConsoleCommand("qa_m16_item_structure " + actor.Name);
            var initial = await Fresh(DateTimeOffset.MinValue, "initial");
            Check(initial.GetProperty("witnessInstalled").GetBoolean() && initial.GetProperty("itemTableReady").GetBoolean(), "actual-native-witness-and-production-catalog-ready");
            var owner = initial.GetProperty("actor");
            Check(owner.GetProperty("samePlayer").GetBoolean() && owner.GetProperty("HasSentInventory").GetBoolean() && !owner.GetProperty("bypass").GetBoolean() &&
                !owner.GetProperty("sscBypass").GetBoolean(), "ordinary-current-ssc-account");
            var chest = initial.GetProperty("chest"); short chestId = chest.GetProperty("id").GetInt16();
            short x = chest.GetProperty("x").GetInt16(), y = chest.GetProperty("y").GetInt16();
            await actor.MoveTo((x - 2) * 16, (y - 1) * 16); await actor.PingAsync();
            int opened = actor.ChestItems.Count, ack = actor.PacketCount(33);
            await actor.Send(31, writer => { writer.Write(x); writer.Write(y); });
            await actor.WaitUntil(() => actor.ActiveChest == chestId && actor.PacketCount(33) > ack && actor.ChestItems.Skip(opened)
                .Where(item => item.Chest == chestId).Select(item => item.Slot).Distinct().Count() == chest.GetProperty("maxItems").GetInt32(), TimeSpan.FromSeconds(8));
            Check(actor.ActiveChest == chestId, "real-normal-chest-open-response");
            int typeMax = initial.GetProperty("itemCount").GetInt32(), prefixMax = initial.GetProperty("prefixCount").GetInt32();
            var aliases = initial.GetProperty("aliases").EnumerateArray().ToDictionary(value => value.GetProperty("wire").GetInt32(), value => value.GetProperty("canonical").GetInt32());
            // Legal controls precede all protected-invalid inputs. Negative aliases come from
            // the actual native netDefaults map; they are protocol compatibility controls,
            // not a claim that the current stock writer emits a negative Item.type.
            foreach (int packet in new[] { 5, 32 })
            {
                foreach (short type in new[] { (short)1, (short)9, (short)(typeMax - 1), (short)-48, (short)-1 })
                    await Exchange($"p{packet}-legal-type-{type}", packet, 10, type, 1, 0, 0, true, type < 0 ? aliases[type] : type, 1, true);
                await Exchange($"p{packet}-air-unused-fields", packet, 10, 0, -1, 255, 0, true, 0, packet == 5 ? 0 : -1, true);
            }
            foreach (int slot in new[] { 58, initial.GetProperty("trash").GetInt32(), initial.GetProperty("lastSlot").GetInt32() })
            {
                bool relayed = slot != initial.GetProperty("trash").GetInt32();
                await Exchange($"special-{slot}-favorite-and-ignored-flags", 5, slot, 9, 1, 0, 255, true, 9, 1, relayed);
                await Exchange($"special-{slot}-air", 5, slot, 0, 0, 255, 255, true, 0, 0, relayed);
            }
            foreach (int packet in new[] { 5, 32 })
            {
                await Exchange($"p{packet}-baseline-before-rejection", packet, 10, 9, 2, 0, 0, true, 9, 2, true);
                foreach (var invalid in new[] { ((short)-49, (byte)0), ((short)typeMax, (byte)0), (short.MaxValue, (byte)0), ((short)24, (byte)prefixMax), ((short)24, byte.MaxValue) })
                    await Exchange($"p{packet}-invalid-{invalid.Item1}-{invalid.Item2}", packet, 10, invalid.Item1, 1, invalid.Item2, 0, false, 9, 2, false);
                await Exchange($"p{packet}-same-account-recovery", packet, 10, 9, 1, 0, 0, true, 9, 1, true);
            }
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-account-cheat-verdict");
            status = "passed";

            async Task Exchange(string label, int packet, int slot, short type, short stack, byte prefix, byte flags, bool allowed, int canonical, int storedStack, bool relayed)
            {
                var before = await State(label + "-before"); int inventoryMark = peer.InventoryUpdates.Count, chestMark = peer.ChestItems.Count;
                var frame = LabClient.Packet((byte)packet, writer =>
                {
                    if (packet == 5) { writer.Write(actor.Slot); writer.Write((short)slot); }
                    else { writer.Write(chestId); writer.Write((byte)slot); }
                    writer.Write(stack); writer.Write(prefix); writer.Write(type); if (packet == 5) writer.Write(flags);
                });
                await actor.SendBatch(frame); await actor.PingAsync();
                bool Received() => packet == 5 ? peer.InventoryUpdates.Skip(inventoryMark).Any(item => item.Player == actor.Slot && item.Slot == slot && item.Item == canonical) :
                    peer.ChestItems.Skip(chestMark).Any(item => item.Chest == chestId && item.Slot == slot && item.Item == canonical);
                if (allowed && relayed) await peer.WaitUntil(Received, TimeSpan.FromSeconds(5)); else await peer.Drain(TimeSpan.FromMilliseconds(80));
                var after = await State(label + "-after"); long Delta(string key) => after.GetProperty(key).GetInt64() - before.GetProperty(key).GetInt64();
                var raw = after.GetProperty("lastRaw");
                Check(Delta("rawRequests") == 1 && after.GetProperty("witnessFaults").GetInt64() == 0 && raw.GetProperty("Packet").GetInt32() == packet &&
                    raw.GetProperty("SamePlayer").GetBoolean() && raw.GetProperty("SameThread").GetBoolean(), label + "-complete-current-raw-witness");
                Check(!raw.GetProperty("HandledBefore").GetBoolean() && raw.GetProperty("HandledAfter").GetBoolean() == !allowed &&
                    Delta("coreEntries") == (allowed ? 1 : 0) && (allowed ? Delta("nativeWrites") >= 1 : Delta("nativeWrites") == 0), label + "-actual-cancel-core-native-boundary");
                var item = after.GetProperty("assets").GetProperty(packet == 5 ? "Inventory" : "Chest").EnumerateArray().Single(value => value.GetProperty("Slot").GetInt32() == slot);
                Check(item.GetProperty("Type").GetInt32() == canonical && item.GetProperty("Stack").GetInt32() == storedStack, label + "-actual-native-poststate");
                if (allowed)
                {
                    if (packet == 5 && canonical != 0) Check(item.GetProperty("Favorite").GetBoolean() == ((flags & 1) != 0), label + "-native-favorite-bit");
                    if (relayed) Check(Received() && Delta("sends") >= 1, label + "-actual-canonical-peer-receipt");
                }
                else
                {
                    Check(before.GetProperty("assets").GetRawText() == after.GetProperty("assets").GetRawText() && raw.GetProperty("Before").GetRawText() == raw.GetProperty("After").GetRawText(), label + "-native-ssc-and-chest-unchanged");
                    Check(Delta("sends") == 0 && peer.InventoryUpdates.Count == inventoryMark && peer.ChestItems.Count == chestMark, label + "-zero-correction-or-forward");
                }
                Healthy(actor); results.Add(new { label, packet, allowed, frameHex = Convert.ToHexString(frame), before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, results, originalId = "v2:C01",
                syntheticLoopbackTcp = true, nativeServerExecution = true, stockClientGui = false, acquisitionHistoryClaim = false,
                accountBanQualification = false, wholeOriginalTaskComplete = false, unprotectedInvalidNativeCall = false,
                scope = "actual packet5/32 type and prefix domains, native aliases, air, special-slot and canonical peer poststate" }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool condition, string label) => host.Assert(condition, "m16-item-structure:" + label);
        async Task<LabClient> Actor(string name)
        { var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin(); Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-authenticated-initial-ssc"); return client; }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name"; query.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0, client.Name + "-healthy-unbanned");
        }
        async Task<JsonElement> State(string label)
        { var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m16_item_structure_state"); return await Fresh(requested, label); }
        async Task<JsonElement> Fresh(DateTimeOffset requested, string label)
        {
            string path = Path.Combine(host.ReportDirectory, "m16-item-structure-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                if (File.Exists(path)) try
                {
                    string content = await ReadSnapshotTextAsync(path); using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), content); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Fresh M16 item structure state missing: " + label);
        }
    }

    internal static async Task<string> ReadSnapshotTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
