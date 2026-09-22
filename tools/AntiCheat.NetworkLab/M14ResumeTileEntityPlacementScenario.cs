using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M14ResumeTileEntityPlacementScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m14r-entity"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        List<LabClient> clients = []; List<object> outcomes = []; List<object> frames = [];
        string status = "failed", failure = ""; bool prepared = false;
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-runtime");
            var actor = await Actor("M14REntityActor"); var peer = await Actor("M14REntityPeer");
            await host.ConsoleCommand("qa_m14r_entity setup " + actor.Name + " " + peer.Name); prepared = true;
            var initial = await Snapshot("setup");
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("observersInstalled").GetBoolean() &&
                initial.GetProperty("objects").GetArrayLength() == 4 && initial.GetProperty("objects").EnumerateArray().All(entry =>
                !entry.GetProperty("registered").GetBoolean() && entry.GetProperty("tileValid").GetBoolean() &&
                entry.GetProperty("anchorAllowed").GetBoolean() && entry.GetProperty("actorAllowed").GetBoolean() &&
                !entry.GetProperty("peerAllowed").GetBoolean()), "valid-empty-native-targets-and-ordinary-corner-grants");
            foreach (int family in new[] { 1, 3, 4, 5 })
            {
                await host.ConsoleCommand("qa_m14r_entity select " + family);
                await Step(family + "-legal-create", family, actor, peer, true);
                var corners = family == 1 ? new[] { (0, 1), (1, 0), (1, 1) } : new[] { (-1, -1) };
                foreach (var (dx, dy) in corners)
                {
                    string label = family + (family == 1 ? $"-cell-{dx}-{dy}" : "-far-cell");
                    await host.ConsoleCommand("qa_m14r_entity select " + family);
                    if (family == 1) await host.ConsoleCommand($"qa_m14r_entity corner {dx} {dy}");
                    await host.ConsoleCommand("qa_m14r_entity deny");
                    var denied = Target(await Snapshot(label + "-denied"), family);
                    Check(denied.GetProperty("anchorAllowed").GetBoolean() && !denied.GetProperty("actorAllowed").GetBoolean(),
                        label + "-actual-revocation-keeps-anchor-allowed");
                    await Step(label + "-revoked", family, actor, peer, false);
                    await Step(label + "-independent-peer-denied", family, peer, actor, false);
                    await host.ConsoleCommand("qa_m14r_entity allow");
                    await Step(label + "-same-account-recovers", family, actor, peer, true);
                }
            }
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "safety-is-not-account-proof");
            await M15LeashedAnchorItemScenario.RunAsync(host, actor, peer);
            status = "passed";

            async Task Step(string label, int family, LabClient subject, LabClient witness, bool write)
            {
                var before = await Snapshot(label + "-before"); var target = Target(before, family);
                short x = target.GetProperty("x").GetInt16(), y = target.GetProperty("y").GetInt16();
                Check(!target.GetProperty("registered").GetBoolean(), label + "-fresh-absent-entity");
                await witness.PingAsync(); int received = witness.PacketCount(86);
                byte[] frame = LabClient.Packet(87, writer => { writer.Write(x); writer.Write(y); writer.Write((byte)family); });
                frames.Add(new { label, subject = subject.Name, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
                await subject.SendBatch(frame); await subject.PingAsync(); await witness.PingAsync();
                var after = await Snapshot(label + "-after"); var raw = after.GetProperty("lastRaw");
                Check(after.GetProperty("raw").GetInt64() == before.GetProperty("raw").GetInt64() + 1 &&
                    raw.GetProperty("receivingSlot").GetInt32() == subject.Slot && raw.GetProperty("type").GetInt32() == family &&
                    raw.GetProperty("x").GetInt32() == x && raw.GetProperty("y").GetInt32() == y &&
                    raw.GetProperty("handled").GetBoolean() == !write, label + "-fresh-real-receive-and-cancel");
                Check(after.GetProperty("safetyBlocked").GetInt64() - before.GetProperty("safetyBlocked").GetInt64() == (write ? 0 : 1),
                    label + "-product-safety-executed");
                Check(after.GetProperty("sends").GetInt64() - before.GetProperty("sends").GetInt64() == (write ? 1 : 0), label + "-native-publication");
                Check(witness.PacketCount(86) - received == (write ? 1 : 0), label + "-independent-peer-publication");
                var current = Target(after, family);
                Check(current.GetProperty("registered").GetBoolean() == write && current.GetProperty("tileValid").GetBoolean(), label + "-actual-server-entity");
                Check(after.GetProperty("nextEntityId").GetInt32() - before.GetProperty("nextEntityId").GetInt32() == (write ? 1 : 0),
                    label + "-registration-allocation-count");
                Check(before.GetProperty("worldItems").GetRawText() == after.GetProperty("worldItems").GetRawText(), label + "-unchanged-server-world-items");
                Healthy(subject); outcomes.Add(new { label, family, write, before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            bool successful = status == "passed";
            if (prepared)
                try
                {
                    await host.ConsoleCommand("qa_m14r_entity finish"); var final = await Snapshot("finished");
                    Check(!final.GetProperty("observersInstalled").GetBoolean() && final.GetProperty("objects").EnumerateArray()
                        .All(entry => !entry.GetProperty("regionPresent").GetBoolean()), "fixture-observers-and-own-regions-removed");
                }
                catch (Exception error) { failure += "\ncleanup: " + error; status = "failed"; }
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientGui = false,
                scope = "87 empty item-frame/mannequin/weapon-rack/hat-rack entity creation checks entire current footprint before registration and86",
                clientOptimisticConsumption = "not-executed", clientInventoryAfterDenial = "not-executed",
                expectedAccountBan = false, outcomes, clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("ANTICHEAT_") || line.Contains("qa_m14r_entity")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
            if (successful) Check(status == "passed", "scenario-and-cleanup-completed");
        }
        void Check(bool condition, string label) => host.Assert(condition, "m14r-entity:" + label);
        static JsonElement Target(JsonElement snapshot, int family) => snapshot.GetProperty("objects").EnumerateArray()
            .Single(entry => entry.GetProperty("type").GetInt32() == family);
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
                client.Name + "-connected-without-permanent-ban");
        }
        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m14r_entity state");
            string path = Path.Combine(host.ReportDirectory, "m14r-entity-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                    try
                    {
                        // The main-thread observer may be publishing a newer snapshot concurrently.
                        // Sharing writes prevents a read poll from rejecting the one requested publication.
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
            throw new TimeoutException("Fresh bounded entity snapshot unavailable: " + label);
        }
    }
}
