using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M13DisplaySafetyScenario
{
    // Audited ItemID constants and native UI predicates are exercised in M13DisplayEntitySafetyTests.
    private const ushort WoodHelmet = 727, BorealWoodHelmet = 2509, RedDye = 1007, SlimySaddle = 2430, WoodenSword = 24;
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m13-display"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(2); var outcomes = new List<object>(16); var frames = new List<object>(16);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        bool prepared = false;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-target");
            var actor = await Actor("M13DisplayActor"); var peer = await Actor("M13DisplayPeer");
            await host.ConsoleCommand("qa_m13_display setup " + actor.Name);
            var initial = await Snapshot("prepared"); prepared = true;
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("currentRegisteredObject").GetBoolean() &&
                initial.GetProperty("tileValid").GetBoolean(), "actual-native-fixture-and-product-guard");
            Check(!initial.GetProperty("actor").GetProperty("bypass").GetBoolean() &&
                !initial.GetProperty("actor").GetProperty("sscBypass").GetBoolean() &&
                initial.GetProperty("actor").GetProperty("regionAllowed").GetBoolean(), "ordinary-actor-with-specific-region-grant");
            int id = initial.GetProperty("id").GetInt32();
            await Step("legal-wood-helmet", 0, 0, WoodHelmet, true);
            await Step("legal-last-equipment-mount-slot", 0, 8, SlimySaddle, true);
            await Step("legal-last-mount-dye-slot", 1, 8, RedDye, true);
            await Step("legal-held-wooden-sword", 3, 0, WoodenSword, true);
            await Step("legal-pose-ignored-slot255", 2, 255, 1, true);
            await Step("equipment-first-invalid-slot", 0, 9, BorealWoodHelmet, false);
            await Step("dye-first-invalid-slot", 1, 9, RedDye, false);
            await Step("misc-first-invalid-slot", 3, 1, WoodenSword, false);
            await Step("undefined-command4", 4, 0, BorealWoodHelmet, false);
            await Step("undefined-command255", 255, 0, BorealWoodHelmet, false);
            await Step("valid-helmet-change-after-safety-blocks", 0, 0, BorealWoodHelmet, true);
            await host.ConsoleCommand("qa_m13_display deny");
            Check(!(await Snapshot("region-denied")).GetProperty("actor").GetProperty("regionAllowed").GetBoolean(), "actual-core-region-denial-armed");
            await Step("known-command-reaches-core-region-denial", 0, 0, WoodHelmet, false, coreDeny: true);
            await Step("undefined-alias-cannot-bypass-denied-region", 4, 0, WoodHelmet, false);
            await host.ConsoleCommand("qa_m13_display allow");
            Check((await Snapshot("region-allowed-again")).GetProperty("actor").GetProperty("regionAllowed").GetBoolean(), "normal-region-grant-restored");
            await Step("normal-helmet-write-after-region-restored", 0, 0, WoodHelmet, true);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-account-proof-from-safety-rejection");
            status = "passed";

            async Task Step(string label, byte command, byte slot, ushort type, bool allowed, bool coreDeny = false)
            {
                var before = await Snapshot(label + "-before"); await peer.PingAsync();
                int priorPeer = peer.PacketCount(121);
                byte[] frame = LabClient.Packet(121, writer =>
                {
                    writer.Write(actor.Slot); writer.Write(id); writer.Write(slot); writer.Write(command);
                    if (command == 2) writer.Write((byte)type);
                    else { writer.Write(type); writer.Write((ushort)1); writer.Write((byte)0); }
                });
                frames.Add(new { label, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync(); await peer.PingAsync();
                var after = await Snapshot(label + "-after"); var raw = after.GetProperty("lastRaw");
                Check(raw.GetProperty("sequence").GetInt64() == before.GetProperty("raw").GetInt64() + 1 &&
                    raw.GetProperty("command").GetInt32() == command && raw.GetProperty("slot").GetInt32() == slot &&
                    raw.GetProperty("handled").GetBoolean() == !allowed, label + "-actual-current-request-cancellation");
                Check(after.GetProperty("safetyBlocked").GetInt64() == before.GetProperty("safetyBlocked").GetInt64() + (!allowed && !coreDeny ? 1 : 0),
                    label + "-specific-early-safety-counter");
                Check(after.GetProperty("sends").GetInt64() == before.GetProperty("sends").GetInt64() + (allowed ? 1 : 0) &&
                    peer.PacketCount(121) == priorPeer + (allowed ? 1 : 0), label + "-native-relay-and-independent-peer");
                long expectedEvents = command == 2 ? 0 : allowed || coreDeny ? 1 : 0;
                Check(after.GetProperty("itemEvents").GetInt64() == before.GetProperty("itemEvents").GetInt64() + expectedEvents,
                    label + "-existing-tshock-item-hook-preserved");
                if (allowed)
                {
                    if (command == 2) Check(after.GetProperty("pose").GetInt32() == type, label + "-actual-pose-write");
                    else
                    {
                        string collection = command switch { 1 => "dyes", 3 => "misc", _ => "equip" };
                        Check(after.GetProperty(collection)[slot].GetProperty("type").GetInt32() == type &&
                            after.GetProperty(collection)[slot].GetProperty("stack").GetInt32() == 1, label + "-actual-object-slot-write");
                    }
                }
                else Check(ObjectContents(before) == ObjectContents(after), label + "-all-native-object-content-preserved");
                Healthy(actor); outcomes.Add(new { label, allowed, coreDeny, command, slot, type, raw,
                    peer121Delta = peer.PacketCount(121) - priorPeer, before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            bool successfulBeforeCleanup = status == "passed";
            if (prepared)
            {
                try { await host.ConsoleCommand("qa_m13_display finish"); await Snapshot("finished-region-restored"); }
                catch (Exception cleanup) { failure += "\nfixture restore: " + cleanup; status = "failed"; }
            }
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientGui = false,
                scope = "packet121 exact item-command/slot safety before TShock and native writes; existing region handlers retained",
                expectedAccountBan = false, outcomes, clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("ANTICHEAT_") || line.Contains("qa_m13_display")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
            if (successfulBeforeCleanup)
                Check(status == "passed", "scenario-and-owned-fixture-restoration-completed");
        }

        void Check(bool condition, string label) => host.Assert(condition, "m13-display:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-ordinary-SSC-account"); return client;
        }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            query.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0,
                client.Name + "-usable-without-permanent-ban");
        }
        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m13_display state");
            string path = Path.Combine(host.ReportDirectory, "m13-display-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { var result = document.RootElement.Clone(); await Write(label + ".json", result); return result; }
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                await Task.Delay(40);
            }
            throw new TimeoutException("Fresh bounded display snapshot unavailable: " + label);
        }
    }

    private static string ObjectContents(JsonElement snapshot) => string.Join("|", new[] { "equip", "dyes", "misc", "pose" }.Select(name => snapshot.GetProperty(name).GetRawText()));
}
