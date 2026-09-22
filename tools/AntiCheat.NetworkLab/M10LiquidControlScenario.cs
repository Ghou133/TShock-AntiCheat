using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Two separately invoked, bounded diagnostic runs. The off control is never product acceptance.</summary>
internal static class M10LiquidControlScenario
{
    private const string HistoricalWorldSha256 = "26DCBB727107274D151B2231493321417DCE30A2F8C2D7FD771C978532A1739A";

    public static async Task RunAsync(M4ObservedReplayHarness host, bool guardEnabled)
    {
        string directory = Path.Combine(host.ReportDirectory, "m10-liquid-control"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(2); var samples = new List<JsonElement>(48);
        var preparationSamples = new List<JsonElement>(96);
        var pings = new List<object>(48); var entire = Stopwatch.StartNew();
        string status = "failed", failure = ""; bool seeded = false, finishRecorded = false;
        JsonElement? initial = null, terminal = null;
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-isolated-run");
            using (var inputs = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.ReportDirectory, "inputs.json"))))
                Check(inputs.RootElement.GetProperty("worldSourceSha256").GetString() == HistoricalWorldSha256,
                    "same-historical-source-world-file");
            await host.FixtureSnapshot();
            var actor = await Actor("M10LiquidActor"); var peer = await Actor("M10LiquidPeer");
            var settling = Stopwatch.StartNew(); JsonElement ready; int preparationNumber = 0;
            do
            {
                ready = await State("preparation-wait-" + (preparationNumber++).ToString("D2"), "qa_m10_liquid_control_state");
                preparationSamples.Add(ready);
                if (ready.GetProperty("quiescent").GetBoolean()) break;
                await DevRunLifecycle.WaitAsync(Task.Delay(400), TimeSpan.FromSeconds(1));
            } while (settling.Elapsed < TimeSpan.FromSeconds(60) && preparationSamples.Count < 160);
            Check(ready.GetProperty("quiescent").GetBoolean(), "naturally-empty-initial-native-queues");
            var requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m10_liquid_control " + (guardEnabled ? "on" : "off") + " " + actor.Name + " " + peer.Name);
            initial = await Fresh(requested, "seeded", TimeSpan.FromSeconds(60) - settling.Elapsed);
            seeded = initial.Value.GetProperty("seeded").GetBoolean();
            Check(seeded && initial.Value.GetProperty("guardEnabled").GetBoolean() == guardEnabled, "one-time-explicit-treatment");
            Check(initial.Value.GetProperty("total").GetInt64() == 5508000L &&
                initial.Value.GetProperty("checkpoints").GetArrayLength() == 3, "full-seed-and-initial-checkpoints-retained");
            samples.Add(initial.Value);
            var observation = Stopwatch.StartNew(); int number = 0;
            do
            {
                await DevRunLifecycle.WaitAsync(Task.Delay(850), TimeSpan.FromSeconds(2));
                var ping = Stopwatch.StartNew();
                await DevRunLifecycle.WaitAsync(actor.PingAsync(), TimeSpan.FromSeconds(3));
                double actorMs = ping.Elapsed.TotalMilliseconds; ping.Restart();
                await DevRunLifecycle.WaitAsync(peer.PingAsync(), TimeSpan.FromSeconds(3));
                pings.Add(new { sample = number, actorMs, peerMs = ping.Elapsed.TotalMilliseconds });
                var sample = await State("sample-" + (number++).ToString("D2"), "qa_m10_liquid_control_state");
                samples.Add(sample); terminal = sample;
                if (sample.TryGetProperty("firstNativeException", out var nativeError) && nativeError.ValueKind == JsonValueKind.String)
                    throw new InvalidOperationException("Native Liquid.UpdateLiquid threw; partial native steps cannot qualify this control: " + nativeError.GetString());
                // A quantity difference is data. Do not abort before observing the later queue and terminal state.
                if (sample.GetProperty("checking").GetInt32() == 0 && sample.GetProperty("numLiquid").GetInt32() == 0 &&
                    sample.GetProperty("numLiquidBuffer").GetInt32() == 0 && (!guardEnabled || sample.GetProperty("unpaid").GetInt64() == 0))
                    break;
            } while (observation.Elapsed < TimeSpan.FromSeconds(90) && entire.Elapsed < TimeSpan.FromSeconds(160) && samples.Count < 110);
            terminal = await State("terminal", "qa_m10_liquid_control_finish"); finishRecorded = true;
            Check(terminal.Value.GetProperty("finished").GetBoolean() && terminal.Value.GetProperty("checkpoints").GetArrayLength() == 4,
                "complete-terminal-checkpoint-retained");
            Check(terminal.Value.GetProperty("schedulerCalls").GetInt32() > 0 &&
                terminal.Value.GetProperty("m10LiquidTimesDropped").GetInt32() == 0, "bounded-real-native-scheduler-timing");
            if (guardEnabled) Check(terminal.Value.GetProperty("healthy").GetBoolean(), "guard-on-contract-remained-healthy");
            Healthy(actor); Healthy(peer);
            status = guardEnabled ? "guard-on-observed" : "guard-off-control-observed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            // Save evidence even after a transport/assertion failure. Never turn cleanup into a second initialization.
            if (seeded && !finishRecorded)
                try { terminal = await State("terminal-after-error", "qa_m10_liquid_control_finish"); finishRecorded = true; }
                catch (Exception error) { failure += "\nTerminal capture: " + error; }
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, guardEnabled, elapsedMilliseconds = entire.Elapsed.TotalMilliseconds,
                initial, terminal, preparationSamples, samples, pings, finishRecorded,
                preparationLimitSeconds = 60, observationLimitSeconds = 90, wholeScenarioStopGateSeconds = 160,
                exactConservationObservedAtEverySample = samples.Count > 0 && samples.All(s => s.GetProperty("quantityDifference").GetInt64() == 0) &&
                    terminal is { } final && final.GetProperty("quantityDifference").GetInt64() == 0,
                minimumObservedQuantity = samples.Count == 0 ? (long?)null : Math.Min(samples.Min(s => s.GetProperty("total").GetInt64()),
                    terminal?.GetProperty("total").GetInt64() ?? long.MaxValue),
                maximumObservedQuantity = samples.Count == 0 ? (long?)null : Math.Max(samples.Max(s => s.GetProperty("total").GetInt64()),
                    terminal?.GetProperty("total").GetInt64() ?? long.MinValue),
                historicalExactReplay = false, fixtureArtificial = true, stockClientGui = false,
                nativeServerExecution = true, directNativeUpdateCalls = 0, queueReset = false, refill = false,
                productQualification = false,
                measurementVersion = "M11.actual-native-entry/1",
                conclusionBoundary = "Diagnostic run only. Compare first actual native body entrance, including clock, natural skipCount and full actual RNG state. Later natural timing/RNG may diverge; equal first state or paused zero writes do not prove eventual conservation or identical natural scheduling."
            }, json));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool condition, string label) => host.Assert(condition, "m10-liquid-control:" + label);
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
                client.Name + "-active-unbanned");
        }
        async Task<JsonElement> State(string label, string command)
        { var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand(command); return await Fresh(requested, label); }
        async Task<JsonElement> Fresh(DateTimeOffset requested, string label, TimeSpan? limit = null)
        {
            string path = Path.Combine(host.ReportDirectory, "m10-liquid-control-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < (limit ?? TimeSpan.FromSeconds(3)))
            {
                DevRunLifecycle.Check();
                if (File.Exists(path)) try
                {
                    string content = await File.ReadAllTextAsync(path); using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested &&
                        (label != "seeded" || doc.RootElement.GetProperty("seeded").GetBoolean()))
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), content); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Fresh M10 liquid control state missing: " + label);
        }
    }
}
