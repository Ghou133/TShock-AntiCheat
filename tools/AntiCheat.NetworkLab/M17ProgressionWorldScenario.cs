using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real loopback receive of legal inventory synchronization and item-use intent.
/// Native field preparation/lifecycle Hook injection are explicitly separate from world loading or a stock client.</summary>
internal static class M17ProgressionWorldScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m17-progression-world"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(3); var observations = new List<object>(32);
        string status = "failed", failure = ""; bool bound = false; int startLog = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-locked-runtime");
            await host.FixtureSnapshot(); var actor = await Actor("M17WorldFacts", false);
            var initial = await Bind(actor, "initial"); bound = true;
            Check(initial.GetProperty("scope").GetString() == "Production", "actual-production-scope");
            var catalog = initial.GetProperty("catalog").EnumerateArray().ToArray();
            Check(catalog.Length == 24 && catalog.All(entry => entry.GetProperty("qualification").GetString() == "Unqualified"), "actual-24-catalog-policies-remain-unqualified");
            Check(initial.GetProperty("fields").GetArrayLength() == 20 && initial.GetProperty("witnessInstalled").GetBoolean(), "actual-twenty-fields-and-result-witness");
            var initialSession = initial.GetProperty("session").Clone();

            // Real5 for a normal inventory slot is passive synchronization. It intentionally
            // does not reach the progression index (only equipment5 or active13 does).
            await Exercise(actor, "normal-inventory-sync", -1, 5, expectProgression: false);
            await Exercise(actor, "normal-special-world-use-intent", -1, 13, expectProgression: true);
            await Exercise(actor, "passive-sync-during-native-world-transition", 1, 5, expectProgression: false);
            for (int field = 0; field < 20; field++)
            {
                string label = "same-tick-field-" + field.ToString("D2");
                await Exercise(actor, label, field, 13, expectProgression: true);
                // Restoration was inside the same raw callback; now wait for actual server
                // captures, without writing a clock, stable counter, snapshot or health bit.
                await Exercise(actor, label + "-restabilized", -1, 13, expectProgression: true);
            }
            await Exercise(actor, "same-tick-world-identity-change", 20, 13, expectProgression: true);
            await Exercise(actor, "world-identity-restabilized", -1, 13, expectProgression: true);

            Healthy(actor); await actor.DisposeAsync(); await WaitLeft("first-transport-left");
            var reboundActor = await Actor(actor.Name, true); var rebound = await Bind(reboundActor, "same-account-reconnect");
            var reboundSession = rebound.GetProperty("session");
            Check(rebound.GetProperty("account").GetInt64() == initial.GetProperty("account").GetInt64() &&
                reboundSession.GetProperty("ServerRunId").GetString() == initialSession.GetProperty("ServerRunId").GetString() &&
                reboundSession.GetProperty("WorldEpoch").GetInt64() == initialSession.GetProperty("WorldEpoch").GetInt64() &&
                reboundSession.GetRawText() != initialSession.GetRawText(), "real-reconnect-new-session-with-same-authenticated-account");
            if (reboundSession.GetProperty("Slot").GetInt32() == initialSession.GetProperty("Slot").GetInt32())
                Check(reboundSession.GetProperty("Generation").GetInt64() > initialSession.GetProperty("Generation").GetInt64(), "reused-slot-increments-generation");
            await Exercise(reboundActor, "reconnect-normal-use", -1, 13, expectProgression: true);

            Healthy(reboundActor); await reboundActor.DisposeAsync(); await WaitLeft("second-transport-left");
            var epochState = await Command("epoch", "native-lifecycle-hooks"); var lifecycle = epochState.GetProperty("lifecycle");
            Check(!lifecycle.GetProperty("fullWorldFileLoad").GetBoolean(), "lifecycle-hook-tier-not-full-world-load");
            long previousEpoch = lifecycle.GetProperty("before").GetInt64();
            Check(lifecycle.GetProperty("afterDisconnect").GetInt64() == previousEpoch + 1 && lifecycle.GetProperty("afterConnect").GetInt64() == previousEpoch + 2,
                "actual-native-hook-advances-production-world-generations");
            Check(lifecycle.GetProperty("disconnectHandlers").GetArrayLength() == 1 && lifecycle.GetProperty("connectHandlers").GetArrayLength() == 1,
                "current-runtime-world-hook-subscribers-recorded");
            foreach (string stage in new[] { "disconnected", "connected" })
                Check(lifecycle.GetProperty(stage).GetProperty("Facts").GetInt32() == 0 && lifecycle.GetProperty(stage).GetProperty("Stable").GetInt32() == 0,
                    stage + "-old-facts-cleared-by-product-lifecycle");
            var worldActor = await Actor(actor.Name, true); var worldBinding = await Bind(worldActor, "new-world-epoch-account");
            Check(worldBinding.GetProperty("session").GetProperty("WorldEpoch").GetInt64() == previousEpoch + 2 &&
                worldBinding.GetProperty("account").GetInt64() == initial.GetProperty("account").GetInt64(), "real-new-connection-binds-current-world-epoch");
            await Exercise(worldActor, "new-world-epoch-normal-use", -1, 13, expectProgression: true);
            Healthy(worldActor);
            Check(!host.ConsoleLines().Skip(startLog).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-cheat-incident-from-legal-or-unknown-world-inputs");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try
            {
                if (bound)
                {
                    var restored = await Command("restore", "final-world-restoration");
                    Check(restored.GetProperty("restored").GetBoolean() && restored.GetProperty("values").GetRawText() == restored.GetProperty("originalValues").GetRawText() &&
                        restored.GetProperty("actualWorld").GetInt32() == restored.GetProperty("originalWorld").GetInt32(), "all-twenty-native-fields-and-world-id-restored-readback");
                }
            }
            catch (Exception restoreError) { status = "failed"; failure += "\nRestoration: " + restoreError; throw; }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, originalId = "v2:E01", observations,
                    actualTcpParserAndProductHook = true, nativeFieldsArePreparation = true, lifecycleHookInjection = true,
                    fullWorldFileLoad = false, stockClientGui = false, nativeItemConsumption = false,
                    progressionSynchronizationPredicate = "independent tests; legal inventory5 here does not enter progressionIndex",
                    permanentBanQualificationAdded = false, wholeOriginalTaskComplete = false }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var client in clients) await client.DisposeAsync();
            }
        }

        void Check(bool condition, string label) => host.Assert(condition, "m17-progression-world:" + label);
        async Task<LabClient> Actor(string name, bool existing)
        { var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); if (existing) await actor.Login(); else await actor.RegisterAndLogin();
            actor.PauseHeartbeat = true; Check(actor.Authenticated && actor.SscSlots.Count >= 350, name + "-actual-auth-and-ssc"); return actor; }
        async Task<JsonElement> Bind(LabClient actor, string label)
        { var state = await Command("bind " + actor.Name, label); bound = true; Check(state.GetProperty("currentActor").GetBoolean(), label + "-current-native-actor"); return await Settled(label + "-settled"); }
        async Task WaitLeft(string label)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            { var state = await Command("state", label); if (!state.GetProperty("currentActor").GetBoolean()) return; await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1)); }
            throw new TimeoutException("Actual old transport did not leave: " + label);
        }
        async Task<JsonElement> Settled(string label)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var state = await Command("state", label); var cache = state.GetProperty("cache");
                if (cache.GetProperty("Stable").GetInt32() >= 2 && cache.GetProperty("Facts").GetInt32() == 22 &&
                    cache.GetProperty("WorldId").GetInt32() == state.GetProperty("actualWorld").GetInt32() &&
                    cache.GetProperty("Epoch").GetInt64() == state.GetProperty("epoch").GetInt64()) return state;
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Actual E01 producer did not settle: " + label);
        }
        async Task Exercise(LabClient actor, string label, int field, byte packet, bool expectProgression)
        {
            var before = await Settled(label + "-before"); long sequence = before.GetProperty("sequence").GetInt64();
            var armed = await Command("arm " + label + " " + field + " " + packet, label + "-armed");
            Check(armed.GetProperty("armed").GetProperty("Label").GetString() == label && armed.GetProperty("sequence").GetInt64() == sequence, label + "-fresh-specific-arm-ack");
            int itemSlot = before.GetProperty("itemSlot").GetInt32();
            if (packet == 5) await actor.Send(5, writer =>
            { writer.Write(actor.Slot); writer.Write((short)itemSlot); writer.Write((short)1); writer.Write((byte)0); writer.Write((short)678); writer.Write((byte)0); });
            else await actor.Send(13, writer =>
            { writer.Write(actor.Slot); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)itemSlot);
                writer.Write(before.GetProperty("position").GetProperty("x").GetSingle()); writer.Write(before.GetProperty("position").GetProperty("y").GetSingle()); });
            await actor.PingAsync(); var after = await Command("state", label + "-after"); var last = after.GetProperty("last");
            Check(after.GetProperty("faults").GetInt64() == 0 && after.GetProperty("sequence").GetInt64() == sequence + 1 &&
                last.GetProperty("Label").GetString() == label && last.GetProperty("Packet").GetByte() == packet, label + "-one-real-raw-receive");
            Check(last.GetProperty("sameThread").GetBoolean() && last.GetProperty("sameTick").GetBoolean() && last.GetProperty("restored").GetBoolean(),
                label + "-same-callback-field-change-and-finally-readback");
            Check(!last.GetProperty("handled").GetBoolean(), label + "-legal-or-unknown-input-not-blocked");
            var results = last.GetProperty("results").EnumerateArray().ToArray();
            Check(results.Length == (expectProgression ? 1 : 0), label + "-actual-index-result-count");
            if (expectProgression)
            {
                var result = results.Single(); bool transition = field >= 0;
                Check(result.GetProperty("RuleId").GetString() == "PG-NAT-002" && result.GetProperty("verdict").GetString() == (transition ? "Unknown" : "Pass") &&
                    result.GetProperty("Reason").GetString() == (transition ? "world-snapshot-incomplete-stale-or-transitioning" : "progress-condition-false"), label + "-actual-production-rule-reason");
                if (transition) Check(last.GetProperty("restoredCache").GetProperty("Stable").GetInt32() == 0 &&
                    result.GetProperty("cache").GetProperty("Stable").GetInt32() == 0, label + "-changed-back-values-do-not-revive-old-stability");
            }
            Healthy(actor); observations.Add(new { label, before, after });
        }
        void Healthy(LabClient actor)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name"; query.Parameters.AddWithValue("$name", "acc:" + actor.Name);
            Check(actor.Authenticated && !actor.Closed && actor.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0, actor.Name + "-healthy-unbanned");
        }
        async Task<JsonElement> Command(string command, string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m17_e01 " + command);
            string path = Path.Combine(host.ReportDirectory, "m17-progression-world-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                if (File.Exists(path)) try
                {
                    string text = await M17ContainerSortScenario.ReadSnapshotTextAsync(path); using var document = JsonDocument.Parse(text);
                    if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return document.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Fresh E01 command acknowledgement missing: " + label);
        }
    }
}
