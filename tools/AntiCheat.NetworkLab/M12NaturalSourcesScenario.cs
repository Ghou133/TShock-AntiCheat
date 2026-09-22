using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Transport input and bounded source-history acceptance only. PASS does not count as a
/// new natural detection and a source candidate observed on the server is not a client starter.</summary>
internal static class M12NaturalSourcesScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m12-natural-sources"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(5); var evidence = new List<object>(8);
        string status = "failed", failure = ""; int begin = host.ConsoleLines().Length;
        try
        {
            var actor = await Actor("M12UseSources");
            var world = M11SolarTabletScenario.ReadWorld(actor.LastWorldInfo);
            Check(world.Ssc && !world.HardMode, "admitted-native-prehardmode-ssc-world");
            await Inventory(actor, 434); // Actual canonical Clockwork Assault Rifle.
            await actor.UseItem(true); await actor.PingAsync();
            await Inventory(actor, 2767); await actor.UseItem(false); await actor.PingAsync();
            var solar = await Request(actor, -6, "PG-NAT-108.SolarTabletHardmode", "Unknown");
            Check(solar.Contains("observedPossibleUseItemIds=434,2767 ", StringComparison.Ordinal), "actual-input-history-keeps-rifle-and-arrival-tablet");
            Check(solar.Contains("possibleUseSourceSetComplete=False ", StringComparison.Ordinal) &&
                solar.Contains("unobservedAnimationStartStillPossible=True ", StringComparison.Ordinal) &&
                solar.Contains("sscExportProvesClientReceipt=False", StringComparison.Ordinal), "source-set-stays-conservative-and-no-receipt-proof");
            Healthy(actor);
            foreach (short summon in new short[] { 125, 126, 127, 134 })
            {
                var client = await Actor("M12Variant" + summon);
                int item = summon is 125 or 126 ? 544 : summon == 127 ? 557 : 556;
                await Inventory(client, (short)item); await client.UseItem(true); await client.PingAsync(); await client.UseItem(false);
                var line = await Request(client, summon, "PG-NAT-081.MechanicalSummonVariant", "Pass");
                Check(line.Contains("nativeMechdusaWorld=False", StringComparison.Ordinal) &&
                    line.Contains("clientVariantHistoryComplete=False", StringComparison.Ordinal), "mechanical-group-" + summon + "-actual-open-world-contract-and-explicit-gap");
                Healthy(client);
            }
            Check(!host.ConsoleLines().Skip(begin).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-source-history-or-native-pass-account-sanctions");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, evidence, syntheticLoopbackTcp = true, stockClientGui = false,
                sourceHistoryScope = "C2S13 accepted server item possibility plus C2S61 current selected item; SSC export hook separately native-method tested",
                newNaturalHardRules = 0, naturalAcquisitionCoverageAdded = false,
                mechanicalNegativeVariantHistory = "native-method and adapter tests only; no from-start Mechdusa-world TCP candidate qualification",
                nativeSummonExecutionClaimed = false, clientAnimationStarterClaimed = false, sscReceiptClaimed = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool condition, string label) => host.Assert(condition, "m12-natural-sources:" + label);
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = sql; query.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(query.ExecuteScalar() ?? 0L);
        }
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin(); await client.PingAsync();
            Check(client.Authenticated && client.SscSlots.Count >= 350 && Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", name) == 1,
                name + "-ordinary-authenticated-ssc-default-account"); return client;
        }
        void Healthy(LabClient client) => Check(!client.Closed && client.DisconnectReason is null &&
            Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + client.Name) == 0, client.Name + "-connected-unbanned");
        async Task Inventory(LabClient client, short item)
        {
            byte[] frame = LabClient.Packet(5, writer => { writer.Write(client.Slot); writer.Write((short)0); writer.Write((short)1); writer.Write((byte)0); writer.Write(item); writer.Write((byte)0); });
            await client.SendBatch(frame); await client.PingAsync();
            evidence.Add(new { label = "client-inventory-input-not-origin-proof", client.Name, item, frameHex = Convert.ToHexString(frame) });
        }
        async Task<string> Request(LabClient client, short summon, string rule, string verdict)
        {
            int start = host.ConsoleLines().Length; long account = Scalar("SELECT ID FROM Users WHERE Username=$name", client.Name);
            byte[] frame = LabClient.Packet(61, writer => { writer.Write((short)client.Slot); writer.Write(summon); });
            await client.SendBatch(frame); await client.PingAsync(); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                DevRunLifecycle.Check();
                string? line = host.ConsoleLines().Skip(start).FirstOrDefault(value => value.Contains("ANTICHEAT_RULE_INPUT ", StringComparison.Ordinal) &&
                    value.Contains("rule=" + rule + " ", StringComparison.Ordinal) && value.Contains("accountId=" + account + " ", StringComparison.Ordinal) &&
                    value.Contains("verdict=" + verdict + " ", StringComparison.Ordinal));
                if (line is not null)
                {
                    evidence.Add(new { label = "actual-raw61-contract", client.Name, account, summon, rule, verdict, frameHex = Convert.ToHexString(frame), trace = line });
                    Check(true, rule + "-" + summon + "-actual-requested-contract"); return line;
                }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Missing native-source rule outcome: " + rule + "/" + summon);
        }
    }
}
