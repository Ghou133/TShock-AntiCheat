using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M14LObjectPlacementScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m14l-placement"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        List<LabClient> clients = []; List<object> outcomes = []; List<object> frames = [];
        string status = "failed", failure = ""; bool prepared = false;
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-runtime");
            var actor = await Actor("M14LDisplayActor"); var peer = await Actor("M14LDisplayPeer");
            await host.ConsoleCommand("qa_m14l_placement setup " + actor.Name + " " + peer.Name);
            prepared = true; var initial = await Snapshot("setup");
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("objects").EnumerateArray().All(entry =>
                entry.GetProperty("registered").GetBoolean() && entry.GetProperty("tileValid").GetBoolean()), "actual-native-targets-and-product");
            Check(initial.GetProperty("actor").GetProperty("rackAllowed").GetBoolean() &&
                !initial.GetProperty("peer").GetProperty("rackAllowed").GetBoolean(), "ordinary-scoped-region-grant");
            foreach (int packet in new[] { 89, 123, 133 })
            {
                await host.ConsoleCommand("qa_m14l_placement select " + packet);
                await Step(packet + "-stack-zero", packet, actor, peer, 0, false);
                await Step(packet + "-stack-two", packet, actor, peer, 2, false);
                await Step(packet + "-negative-stack", packet, actor, peer, -1, false);
                await Step(packet + "-single-item-recovers", packet, actor, peer, 1, true);
            }
            await host.ConsoleCommand("qa_m14l_placement select 123");
            await host.ConsoleCommand("qa_m14l_placement deny");
            Check(!(await Snapshot("revoked")).GetProperty("actor").GetProperty("rackAllowed").GetBoolean(), "actual-region-revoked");
            await Step("weapon-rack-revoked-single-write", 123, actor, peer, 1, false);
            await Step("weapon-rack-unrelated-peer-denied", 123, peer, actor, 1, false);
            await host.ConsoleCommand("qa_m14l_placement allow");
            await Step("weapon-rack-same-account-restores", 123, actor, peer, 1, true);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "safety-is-not-cheating-proof");
            status = "passed";

            async Task Step(string label, int packet, LabClient subject, LabClient witness, short stack, bool write)
            {
                var before = await Snapshot(label + "-before");
                var target = before.GetProperty("objects").EnumerateArray().Single(entry => entry.GetProperty("packet").GetInt32() == packet);
                short x = target.GetProperty("x").GetInt16(), y = target.GetProperty("y").GetInt16();
                short type = packet == 133 ? (short)4009 : (short)24; // Locked native Apple / WoodenSword.
                await witness.PingAsync(); int received = witness.PacketCount(86);
                byte[] frame = LabClient.Packet((byte)packet, writer =>
                { writer.Write(x); writer.Write(y); writer.Write(type); writer.Write((byte)0); writer.Write(stack); });
                frames.Add(new { label, subject = subject.Name, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
                await subject.SendBatch(frame); await subject.PingAsync(); await witness.PingAsync();
                var after = await Snapshot(label + "-after"); var raw = after.GetProperty("lastRaw");
                Check(after.GetProperty("raw").GetInt64() == before.GetProperty("raw").GetInt64() + 1 &&
                    raw.GetProperty("packet").GetInt32() == packet && raw.GetProperty("receivingSlot").GetInt32() == subject.Slot &&
                    raw.GetProperty("handled").GetBoolean() == !write, label + "-actual-current-receive-and-cancel");
                Check(after.GetProperty("safetyBlocked").GetInt64() - before.GetProperty("safetyBlocked").GetInt64() == (write ? 0 : 1),
                    label + "-product-safety-executed");
                Check(after.GetProperty("sends").GetInt64() - before.GetProperty("sends").GetInt64() == (write ? 1 : 0), label + "-native-object-publication");
                Check(witness.PacketCount(86) - received == (write ? 1 : 0), label + "-independent-peer-publication");
                if (write)
                {
                    var current = after.GetProperty("objects").EnumerateArray().Single(entry => entry.GetProperty("packet").GetInt32() == packet);
                    Check(current.GetProperty("type").GetInt32() == type && current.GetProperty("stack").GetInt32() == 1 &&
                        current.GetProperty("tileValid").GetBoolean(), label + "-effective-single-item-native-write");
                }
                else
                {
                    Check(before.GetProperty("objects").GetRawText() == after.GetProperty("objects").GetRawText(), label + "-no-object-write");
                    Check(before.GetProperty("worldItems").GetRawText() == after.GetProperty("worldItems").GetRawText(), label + "-no-return-or-drop-side-effect");
                }
                Healthy(subject); outcomes.Add(new { label, packet, stack, write, raw, before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            bool successful = status == "passed";
            if (prepared)
                try
                {
                    await host.ConsoleCommand("qa_m14l_placement finish"); var final = await Snapshot("finished");
                    Check(!final.GetProperty("observersInstalled").GetBoolean() &&
                        final.GetProperty("currentAllowed").GetRawText() == final.GetProperty("originalAllowed").GetRawText(), "fixture-authorizations-restored");
                }
                catch (Exception error) { failure += "\ncleanup: " + error; status = "failed"; }
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientGui = false,
                scope = "89/123/133 single-transfer semantics and123 current rack permission; zero object/drop effects on BLOCK",
                expectedAccountBan = false, outcomes, clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("ANTICHEAT_") || line.Contains("qa_m14l_placement")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
            if (successful) Check(status == "passed", "scenario-and-cleanup-completed");
        }
        void Check(bool condition, string label) => host.Assert(condition, "m14l-placement:" + label);
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
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m14l_placement state");
            string path = Path.Combine(host.ReportDirectory, "m14l-placement-state-latest.json"); var timer = Stopwatch.StartNew();
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
            throw new TimeoutException("Fresh bounded placement snapshot unavailable: " + label);
        }
    }
}
