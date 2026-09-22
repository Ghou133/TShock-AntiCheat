using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real TCP paint transactions plus a bounded, naturally advanced native liquid facility.</summary>
internal static class M9WorldScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m9-world"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(2); var frames = new List<object>(); var paints = new List<object>();
        var liquidSamples = new List<JsonElement>(48); var pingSamples = new List<object>(96);
        string status = "failed", failure = "", liquidStatus = "not-started";
        JsonElement? liquidInitial = null, liquidFinal = null;
        bool budgetPauseObserved = false; int logStart = host.ConsoleLines().Length;
        var entireRun = Stopwatch.StartNew();
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime-and-real-server-ready");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-isolated-run");
            await host.FixtureSnapshot();
            var actor = await Actor("M9WorldActor"); var peer = await Actor("M9WorldPeer");
            await host.ConsoleCommand("qa_m9_paint " + actor.Name);
            var prepared = await ReadFresh("m9-paint-state-latest.json", "paint-prepared", DateTimeOffset.MinValue);
            short x = prepared.GetProperty("x").GetInt16(), y = prepared.GetProperty("y").GetInt16();
            long account = prepared.GetProperty("actor").GetProperty("account").GetInt64();
            Check(prepared.GetProperty("guardPresent").GetBoolean() && prepared.GetProperty("healthy").GetBoolean(),
                "paint-live-guard-supported");
            Check(!prepared.GetProperty("actor").GetProperty("bypass").GetBoolean() &&
                prepared.GetProperty("actor").GetProperty("allowed").GetBoolean(), "paint-ordinary-permission-effective");
            await actor.MoveTo((x - 2) * 16, (y - 1) * 16); await actor.PingAsync();
            await peer.MoveTo((x - 4) * 16, (y - 1) * 16); await peer.PingAsync();

            await Paint("allowed", 7, 7, "legal-initial");
            await Paint("revoke-during", 8, 7, "authorization-lost-restored");
            await Paint("allowed", 9, 9, "legal-after-restore");
            await Paint("deny-before", 10, 9, "write-before-denial");
            await Paint("aba-during", 11, 11, "aba-not-restored");
            await Paint("allowed", 12, 12, "legal-after-aba");
            await Paint("replace-during", 13, 13, "replaced-object-not-restored");
            await Paint("allowed", 14, 14, "legal-after-replacement");
            Healthy(actor, "paint-actor-no-ban"); Healthy(peer, "paint-peer-never-attributed");

            await host.ConsoleCommand("qa_m9_liquid " + actor.Name);
            // The fixture command writes this baseline before the next native world update.
            // Do not overwrite it with a later state command before retaining it.
            liquidInitial = await ReadFresh("m9-liquid-state-latest.json", "liquid-seeded", DateTimeOffset.MinValue);
            liquidSamples.Add(liquidInitial.Value);
            Check(liquidInitial.Value.GetProperty("healthy").GetBoolean(), "liquid-live-contract-supported");
            Check(liquidInitial.Value.GetProperty("expectedTotal").GetInt64() == 21600L * 255,
                "liquid-explicit-21600-cell-owned-seed");
            Check(Conserved(liquidInitial.Value), "liquid-seed-conserved");
            liquidStatus = "running";
            var liquidWait = Stopwatch.StartNew(); int sampleNumber = 0, lastPaintRound = -1;
            do
            {
                await Task.Delay(900);
                double actorPing = await MeasurePing(actor), peerPing = await MeasurePing(peer);
                pingSamples.Add(new { sample = sampleNumber, actorPingMilliseconds = actorPing,
                    peerPingMilliseconds = peerPing, sinceSeedMilliseconds = liquidWait.Elapsed.TotalMilliseconds });
                // Concurrent ordinary business uses the same real input and effective rule path.
                int paintRound = (int)(liquidWait.Elapsed.TotalSeconds / 8);
                if (paintRound != lastPaintRound)
                {
                    lastPaintRound = paintRound;
                    byte color = (byte)(paintRound % 2 == 0 ? 16 : 17);
                    await Paint("allowed", color, color, "liquid-concurrent-ordinary-paint-" + paintRound);
                }
                var sample = await LiquidState("liquid-sample-" + (sampleNumber++).ToString("D2"));
                liquidSamples.Add(sample); liquidFinal = sample;
                Check(sample.GetProperty("healthy").GetBoolean(), "liquid-sample-" + sampleNumber + "-contract-healthy");
                Check(Conserved(sample), "liquid-sample-" + sampleNumber + "-exact-water-conservation");
                Check(sample.GetProperty("m9LiquidCallsDropped").GetInt32() == 0, "liquid-sample-" + sampleNumber + "-complete-bounded-timing");
                budgetPauseObserved |= sample.GetProperty("budgetPaused").GetInt64() > liquidInitial.Value.GetProperty("budgetPaused").GetInt64();
                if (sample.GetProperty("checking").GetInt32() == 0 && sample.GetProperty("unpaid").GetInt64() == 0)
                { liquidStatus = "converged"; break; }
            } while (liquidWait.Elapsed < TimeSpan.FromSeconds(45) && liquidSamples.Count < 48);

            var final = liquidFinal ?? liquidInitial.Value;
            Check(final.GetProperty("allowed").GetInt64() > liquidInitial.Value.GetProperty("allowed").GetInt64() &&
                final.GetProperty("observedWork").GetInt64() > liquidInitial.Value.GetProperty("observedWork").GetInt64(),
                "liquid-real-game-update-native-work-entered-product-budget");
            Check(final.GetProperty("schedulerCalls").GetInt32() > 0 &&
                final.GetProperty("processCpuMilliseconds").GetDouble() >= 0 && final.GetProperty("WorkingSet64").GetInt64() > 0,
                "liquid-measured-call-wall-process-cpu-and-memory-separate-from-work-units");
            Healthy(actor, "liquid-concurrent-actor-active-unbanned"); Healthy(peer, "liquid-peer-active-unbanned");
            if (liquidStatus != "converged")
            {
                liquidStatus = "partial-not-converged-within-bounded-observation"; status = "partial";
                throw new TimeoutException("M9 liquid facility remained active after the bounded observation: checking=" +
                    final.GetProperty("checking").GetInt32() + ", nativeQueue=" + final.GetProperty("numLiquid").GetInt32() +
                    ", buffer=" + final.GetProperty("numLiquidBuffer").GetInt32() + ", unpaid=" + final.GetProperty("unpaid").GetInt64() +
                    ". Water was preserved and normal ping/paint was measured; full convergence is not claimed.");
            }
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)),
                "world-safety-and-host-faults-create-no-cheat-incidents");
            status = "passed";

            async Task Paint(string mode, byte submitted, byte expected, string label)
            {
                await host.ConsoleCommand("qa_m9_paint_mode " + mode);
                var before = await PaintState(label + "-before");
                Check(before.GetProperty("healthy").GetBoolean() && before.GetProperty("unknown").GetInt64() == 0,
                    label + "-effective-guard-no-unknown-exit");
                Check(before.GetProperty("actor").GetProperty("allowed").GetBoolean() &&
                    before.GetProperty("actor").GetProperty("account").GetInt64() == account,
                    label + "-original-authorized-account-at-request-start");
                await peer.Drain(TimeSpan.FromMilliseconds(100)); int relayBefore = peer.PacketCount(63), tileBefore = peer.PacketCount(20);
                var frame = LabClient.Packet(63, writer => { writer.Write(x); writer.Write(y); writer.Write(submitted); writer.Write((byte)0); });
                frames.Add(new { label, mode, source = "synthetic-TCP63", hex = Convert.ToHexString(frame) });
                var paintTimer = Stopwatch.StartNew();
                await actor.SendBatch(frame); await actor.PingAsync();
                double requestAndSameStreamPingBarrierMilliseconds = paintTimer.Elapsed.TotalMilliseconds;
                double? peerPaintRelayMilliseconds = null;
                if (mode == "allowed")
                {
                    await peer.WaitUntil(() => peer.PacketCount(63) > relayBefore, TimeSpan.FromSeconds(5));
                    peerPaintRelayMilliseconds = paintTimer.Elapsed.TotalMilliseconds;
                }
                await peer.Drain(TimeSpan.FromMilliseconds(180));
                var after = await PaintState(label + "-after");
                double authoritativeStateObservationMilliseconds = paintTimer.Elapsed.TotalMilliseconds;
                bool ordinary = mode == "allowed", restored = mode == "revoke-during", deniedBefore = mode == "deny-before";
                bool refused = mode is "aba-during" or "replace-during";
                Check(after.GetProperty("healthy").GetBoolean() && after.GetProperty("unknown").GetInt64() == 0,
                    label + "-effective-guard-remained-healthy");
                Check(after.GetProperty("color").GetInt32() == expected, label + "-actual-world-color");
                Check(Delta(after, before, "allowed") == (deniedBefore ? 0 : 1) &&
                    Delta(after, before, "blocked") == (deniedBefore ? 1 : 0) &&
                    Delta(after, before, "restored") == (restored ? 1 : 0) &&
                    Delta(after, before, "refused") == (refused ? 1 : 0), label + "-exact-executable-action-counters");
                Check(Delta(after, before, "relaySuppressed") == (ordinary ? 0 : 1) &&
                    peer.PacketCount(63) - relayBefore == (ordinary ? 1 : 0), label + "-actual-peer63-relay-or-suppression");
                JsonElement? completion = null;
                if (!deniedBefore)
                {
                    completion = after.GetProperty("journal").EnumerateArray().Last().Clone();
                    var record = completion.Value;
                    string outcome = ordinary ? "authorized-commit" : restored ? "authorization-lost-restored" : "authorization-lost-conflict-no-restore";
                    Check(record.GetProperty("Outcome").GetString() == outcome && record.GetProperty("AccountId").GetInt64() == account &&
                        record.GetProperty("Session").GetProperty("Slot").GetInt32() == actor.Slot &&
                        record.GetProperty("Session").GetProperty("WorldEpoch").GetInt64() > 0 &&
                        record.GetProperty("X").GetInt32() == x && record.GetProperty("Y").GetInt32() == y &&
                        record.GetProperty("Submitted").GetByte() == submitted && record.GetProperty("Current").GetByte() == expected &&
                        record.GetProperty("Before").GetByte() == before.GetProperty("color").GetByte(), label + "-real-request-origin-and-before-committed-result");
                    Check(record.GetProperty("ObjectObservation").GetGuid() != Guid.Empty &&
                        (refused ? record.GetProperty("Revision").GetInt64() > 1 : record.GetProperty("Revision").GetInt64() == 1),
                        label + "-mutation-revision-excludes-ABA-and-object-replacement");
                }
                Healthy(actor, label + "-actor-unbanned");
                paints.Add(new { label, mode, submitted, expected, before, after, completion,
                    actualPeerPaintRelays = peer.PacketCount(63) - relayBefore,
                    actualPeerTileResyncs = peer.PacketCount(20) - tileBefore,
                    requestAndSameStreamPingBarrierMilliseconds, peerPaintRelayMilliseconds,
                    authoritativeStateObservationMilliseconds,
                    timingBoundary = "same TCP-stream ping proves request processing; peer relay timing only for allowed paint; state observation includes deliberate peer drain and console snapshot delay",
                    origin = "real-TCP63-session; unauthorized timing/ABA/replacement introduced only by controlled trusted-host callback" });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(frames, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "paints.json"), JsonSerializer.Serialize(paints, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "liquid-samples.json"), JsonSerializer.Serialize(liquidSamples, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, liquidStatus, budgetPauseObserved, durationMilliseconds = entireRun.Elapsed.TotalMilliseconds,
                paintScenarios = paints.Count, liquidInitial, liquidFinal, pingSamples,
                sourceQueueInsertion = true, initialWaterCells = 21600, artificialFacility = true,
                directNativeUpdate = false, perTickRefill = false, queueClipping = false,
                syntheticTcp = true, realNativeServer = true, originalClientGui = false,
                calibrationScope = "one local machine; real complete scheduler call wall percentiles and whole-server CPU are separately measured; scheduler work units do not equal CPU milliseconds",
                recoveryScope = "ordinary block paint; trusted-host authorization races/ABA/replacement controlled for test; no item/drop/refund or automatic account verdict",
                traces = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("qa_m9", StringComparison.Ordinal) ||
                    line.Contains("ANTICHEAT_", StringComparison.Ordinal)).ToArray()
            }, json));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool condition, string label) => host.Assert(condition, "m9-world:" + label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-real-authentication-and-SSC"); return client;
        }
        void Healthy(LabClient client, string label)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, label);
        }
        async Task<double> MeasurePing(LabClient client)
        { var timer = Stopwatch.StartNew(); await client.PingAsync(); return timer.Elapsed.TotalMilliseconds; }
        async Task<JsonElement> PaintState(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m9_paint_state");
            return await ReadFresh("m9-paint-state-latest.json", label, requested);
        }
        async Task<JsonElement> LiquidState(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m9_liquid_state");
            return await ReadFresh("m9-liquid-state-latest.json", label, requested);
        }
        async Task<JsonElement> ReadFresh(string file, string label, DateTimeOffset requested)
        {
            string path = Path.Combine(host.ReportDirectory, file); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path)) try
                {
                    string content = await File.ReadAllTextAsync(path); using var document = JsonDocument.Parse(content);
                    if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), content); return document.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Fresh M9 world snapshot missing: " + label);
        }
        static long Delta(JsonElement after, JsonElement before, string property) =>
            after.GetProperty(property).GetInt64() - before.GetProperty(property).GetInt64();
        static bool Conserved(JsonElement sample) => sample.GetProperty("total").GetInt64() == sample.GetProperty("expectedTotal").GetInt64();
    }
}
