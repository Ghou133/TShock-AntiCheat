using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Synthetic TCP59 into real switch, wire collection and atomic native liquid transfer.</summary>
internal static class M7GameplayBusinessScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        string directory = Path.Combine(host.ReportDirectory, "m7-gameplay-business"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var frames = new List<object>(); var results = new List<object>();
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        var timer = Stopwatch.StartNew();
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime-verified");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-isolated-run");
            await host.FixtureSnapshot();
            var observer = await Actor("M7PumpObserver"); var actor = await Actor("M7PumpActor");
            await host.ConsoleCommand("qa_m7_pumps " + actor.Name);
            var prepared = await ReadFresh("prepared", DateTimeOffset.MinValue);
            Check(prepared.GetProperty("guardPresent").GetBoolean() && prepared.GetProperty("contractHealthy").GetBoolean(), "installed-live-pump-guard");
            Check(!prepared.GetProperty("actor").GetProperty("bypass").GetBoolean() &&
                !prepared.GetProperty("actor").GetProperty("sscBypass").GetBoolean(), "ordinary-actor-without-bypass");
            await Pump("allowed", true);
            await Pump("protected", false);
            // No refill command: a second legitimate operation with an empty source is the real
            // native no-op and must still finish normally after the neighboring region rejection.
            await Pump("allowed", true, emptySource: true);
            await Loadouts();
            await Propagation();
            await observer.PingAsync(); await actor.PingAsync();
            Healthy(actor, "actor-continues-without-sanction"); Healthy(observer, "observer-continues-without-sanction");
            Check(!host.ConsoleLines().Skip(logStart).Any(x => x.Contains("ANTICHEAT_INCIDENT ")), "world-control-does-not-create-cheat-incidents");
            status = "passed";

            async Task Propagation()
            {
                await host.ConsoleCommand("qa_m8_liquid");
                var preparedFlow = await ReadFresh("flow-prepared", DateTimeOffset.MinValue, "m8-flow-state-latest.json");
                observer.LiquidModuleId = preparedFlow.GetProperty("moduleId").GetUInt16();
                actor.LiquidModuleId = observer.LiquidModuleId;
                short x = preparedFlow.GetProperty("SwitchX").GetInt16(), y = preparedFlow.GetProperty("SwitchY").GetInt16();
                await actor.MoveTo((x - 1) * 16, (y - 1) * 16); await actor.PingAsync();
                await observer.Drain(TimeSpan.FromMilliseconds(150));
                var before = await FlowState("flow-before"); int relays = observer.LiquidUpdates.Count;
                Check(before.GetProperty("guardPresent").GetBoolean() && before.GetProperty("healthy").GetBoolean(), "flow-live-guard-effective");
                Check(before.GetProperty("inputLiquid").GetInt32() == 200 && before.GetProperty("channelLiquid").GetInt32() == 0,
                    "flow-one-seed-and-empty-downstream");
                var frame = LabClient.Packet(59, w => { w.Write(x); w.Write(y); });
                frames.Add(new { label = "m8-pump-then-natural-propagation", source = "synthetic-tcp", hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync();
                JsonElement after = default; var wait = Stopwatch.StartNew(); int sequence = 0;
                do
                {
                    await observer.Drain(TimeSpan.FromMilliseconds(200));
                    after = await FlowState("flow-after-" + sequence++);
                    if (after.GetProperty("channelLiquid").GetInt32() == 200 && after.GetProperty("outputLiquid").GetInt32() == 0) break;
                } while (wait.Elapsed < TimeSpan.FromSeconds(8));
                Check(after.GetProperty("healthy").GetBoolean() && after.GetProperty("allowed").GetInt64() > before.GetProperty("allowed").GetInt64() &&
                    after.GetProperty("observedWork").GetInt64() > before.GetProperty("observedWork").GetInt64(), "flow-actual-native-scheduler-inside-product-contract");
                Check(after.GetProperty("inputLiquid").GetInt32() == 0 && after.GetProperty("outputLiquid").GetInt32() == 0 &&
                    after.GetProperty("channelLiquid").GetInt32() == 200 && after.GetProperty("totalLiquid").GetInt32() == 200,
                    "flow-native-downstream-conserves-water-without-refill-or-manual-update");
                int outX = after.GetProperty("OutX").GetInt32(), top = after.GetProperty("Y").GetInt32();
                var delivered = observer.LiquidUpdates.Skip(relays).Where(c => c.X >= outX && c.X <= outX + 1 && c.Y >= top + 2 && c.Y <= top + 5)
                    .Select(c => new { c.X, c.Y, c.Liquid, c.Type }).ToArray();
                Check(delivered.Any(c => c.Liquid > 0) && observer.LiquidUpdatesDropped == 0, "flow-peer-received-native-NetLiquidModule-downstream");
                var movement = after.GetProperty("movementSnapshot");
                Check(movement.ValueKind == JsonValueKind.Object && movement.GetProperty("Received").GetInt64() > 0 &&
                    !movement.GetProperty("CanSupplyHardProof").GetBoolean() && movement.GetProperty("Samples").GetArrayLength() > 0,
                    "m8-G04-production-packet13-observation-is-soft");
                Healthy(actor, "flow-and-movement-normal-actor-no-sanction");
                results.Add(new { label = "m8-natural-liquid-propagation", before, after, delivered, captureLimit = 4096,
                    dropped = observer.LiquidUpdatesDropped, directNativeUpdateOrQueueInsertion = false });
            }

            async Task<JsonElement> FlowState(string label)
            {
                var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m8_liquid_state");
                return await ReadFresh(label, requested, "m8-flow-state-latest.json");
            }

            async Task Loadouts()
            {
                await host.ConsoleCommand("qa_m7_loadouts " + actor.Name);
                var preparedLoadout = await ReadFresh("loadout-prepared", DateTimeOffset.MinValue, "m7-loadout-state-latest.json");
                Check(preparedLoadout.GetProperty("observerPresent").GetBoolean() && preparedLoadout.GetProperty("observerHealthy").GetBoolean(), "loadout-actual-product-observer-installed");
                await observer.Drain(TimeSpan.FromMilliseconds(100));
                var before = await LoadoutState("loadout-short-before"); int relays = observer.LoadoutUpdates.Count;
                var shortFrame = LabClient.Packet(147, w => { w.Write(actor.Slot); w.Write((byte)1); });
                frames.Add(new { label = "loadout-short-body", source = "synthetic-tcp", hex = Convert.ToHexString(shortFrame) });
                await actor.SendBatch(shortFrame); await actor.PingAsync(); await observer.Drain(TimeSpan.FromMilliseconds(150));
                var after = await LoadoutState("loadout-short-after");
                Check(after.GetProperty("CurrentLoadoutIndex").GetInt32() == before.GetProperty("CurrentLoadoutIndex").GetInt32() &&
                    Same(before, after, "armor") && Same(before, after, "dye") && Same(before, after, "loadouts") &&
                    Same(before, after, "sscArmor") && Same(before, after, "visibility"), "loadout-short-frame-before-all-SSC-and-native-writes");
                Check(observer.LoadoutUpdates.Count == relays && after.GetProperty("observed").GetInt64() == before.GetProperty("observed").GetInt64() &&
                    after.GetProperty("malformedBlocked").GetInt64() == before.GetProperty("malformedBlocked").GetInt64() + 1,
                    "loadout-short-frame-no-transaction-or-relay");
                Healthy(actor, "loadout-malformed-is-not-cheat-ban");
                foreach (byte target in new byte[] { 1, 2, 0 })
                {
                    string label = "loadout-page-" + target;
                    before = await LoadoutState(label + "-before"); relays = observer.LoadoutUpdates.Count;
                    int expectedHelmet = before.GetProperty("loadouts")[target].GetProperty("armor")[0].GetProperty("type").GetInt32();
                    var frame = LabClient.Packet(147, w => { w.Write(actor.Slot); w.Write(target); w.Write((ushort)1); });
                    frames.Add(new { label, source = "synthetic-tcp", hex = Convert.ToHexString(frame) });
                    await actor.SendBatch(frame); await actor.PingAsync(); await observer.Drain(TimeSpan.FromMilliseconds(180));
                    after = await LoadoutState(label + "-after");
                    Check(after.GetProperty("CurrentLoadoutIndex").GetInt32() == target &&
                        after.GetProperty("armor")[0].GetProperty("type").GetInt32() == expectedHelmet &&
                        after.GetProperty("sscArmor")[0][0].GetProperty("NetId").GetInt32() == expectedHelmet,
                        label + "-actual-native-and-SSC-page-swap");
                    var actualRelays = observer.LoadoutUpdates.Skip(relays).Where(x => x.Player == actor.Slot).ToArray();
                    Check(actualRelays.Length == 1 && actualRelays[0].Loadout == target && actualRelays[0].Visibility == 1,
                        label + "-actual-TCP147-forward");
                    var completion = after.GetProperty("completion");
                    Check(after.GetProperty("completed").GetInt64() == before.GetProperty("completed").GetInt64() + 1 &&
                        completion.GetProperty("ServerPermutationObserved").GetBoolean() && completion.GetProperty("SscPermutationObserved").GetBoolean() &&
                        completion.GetProperty("VisibilityObserved").GetBoolean() && completion.GetProperty("AppliedLoadout").GetInt32() == target,
                        label + "-full-array-product-transaction-witness");
                    Check(!completion.GetProperty("AttributionComplete").GetBoolean() && !completion.GetProperty("ClientEquipmentTransitionComplete").GetBoolean(),
                        label + "-scaffold-and-client-transition-limits-explicit");
                    Check(after.GetProperty("statDefense").GetInt32() == after.GetProperty("helmetDefense").GetInt32() &&
                        Math.Abs(after.GetProperty("meleeDamage").GetDouble() - (target == 1 ? 1.12 : 1.0)) < 0.001,
                        label + "-real-server-effective-equipment-vanity-and-disabled-exceptions");
                    var calculation = after.GetProperty("calculation");
                    Check(after.GetProperty("equipmentExecutionHealthy").GetBoolean() && calculation.ValueKind == JsonValueKind.Object &&
                        calculation.GetProperty("NativeCalculationReturned").GetBoolean() &&
                        calculation.GetProperty("ProjectionMatchesServerCommit").GetBoolean() &&
                        calculation.GetProperty("LoadoutRevision").GetInt64() == completion.GetProperty("Revision").GetInt64(),
                        label + "-m8-product-observed-real-UpdateEquips-call-return");
                    Check(!calculation.GetProperty("ClientMultiSlotCompletionObserved").GetBoolean() &&
                        !calculation.GetProperty("ItemAcquisitionProven").GetBoolean(), label + "-m8-calculation-does-not-fabricate-completion-or-origin");
                    Healthy(actor, label + "-legal-business-continues");
                    results.Add(new { label, target, expectedHelmet, actualRelays, completion = completion.Clone(),
                        defense = after.GetProperty("statDefense").GetInt32(), meleeDamage = after.GetProperty("meleeDamage").GetDouble() });
                }
            }

            async Task Pump(string name, bool permitted, bool emptySource = false)
            {
                string label = "pump-" + name + (emptySource ? "-empty-source" : "");
                var position = Fixture(await State(label + "-position"), name);
                short x = position.GetProperty("SwitchX").GetInt16(), y = position.GetProperty("SwitchY").GetInt16();
                await actor.MoveTo((x - 1) * 16, (y - 1) * 16); await actor.PingAsync();
                await observer.Drain(TimeSpan.FromMilliseconds(100));
                var before = await State(label + "-before"); var source = Fixture(before, name);
                Check(source.GetProperty("switchAllowed").GetBoolean() &&
                    source.GetProperty("frameNeighborAllowed").GetBoolean() == permitted, label + "-exact-operation-permissions");
                Check(source.GetProperty("inputLiquid").GetInt32() == (emptySource ? 0 : 200) &&
                    source.GetProperty("outputLiquid").GetInt32() == (emptySource ? 200 : 0), label + "-real-initial-liquid");
                var frame = LabClient.Packet(59, w => { w.Write(x); w.Write(y); });
                frames.Add(new { label, source = "synthetic-tcp", hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync(); await observer.Drain(TimeSpan.FromMilliseconds(200));
                var after = await State(label + "-after"); var destination = Fixture(after, name);
                Check(destination.GetProperty("inputLiquid").GetInt32() == (permitted ? 0 : 200) &&
                    destination.GetProperty("outputLiquid").GetInt32() == (permitted ? 200 : 0), label + "-actual-liquid-transfer-or-zero-partial-write");
                var effects = after.GetProperty("effects").EnumerateArray().Skip(before.GetProperty("effects").GetArrayLength())
                    .Where(e => e.GetProperty("Label").GetString() == name).ToArray();
                var transfers = effects.Where(e => e.GetProperty("kind").GetString() == "native-pump-transfer-entry").ToArray();
                Check(transfers.Length == 1 && transfers.Single().GetProperty("ContinueExecution").GetBoolean() == permitted &&
                    transfers.Single().GetProperty("source").GetInt32() == actor.Slot &&
                    transfers.Single().GetProperty("inputs").GetInt32() == 4 && transfers.Single().GetProperty("outputs").GetInt32() == 4,
                    label + "-actual-native-collection-and-before-write-cancellation");
                int nativeFrames = effects.Count(e => e.GetProperty("kind").GetString() == "native-pump-frame-entry");
                Check(permitted && !emptySource ? nativeFrames > 0 : nativeFrames == 0, label + "-actual-framing-boundary");
                string counter = permitted ? "allowed" : "permissionBlocked";
                Check(after.GetProperty(counter).GetInt64() == before.GetProperty(counter).GetInt64() + 1,
                    label + "-exact-live-guard-entry");
                Check(!after.GetProperty("nativeRunning").GetBoolean() && after.GetProperty("nativeCurrentUser").GetInt32() == 255 &&
                    after.GetProperty("effectsDropped").GetInt32() == 0, label + "-native-cleanup-and-bounded-complete-evidence");
                Healthy(actor, label + "-no-ban"); results.Add(new { label, permitted, emptySource,
                    inputAfter = destination.GetProperty("inputLiquid").GetInt32(), outputAfter = destination.GetProperty("outputLiquid").GetInt32(), nativeFrames, effects });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await Write("frames.json", frames); await Write("results.json", results);
            await Write("summary.json", new { status, failure, source = "synthetic-tcp", actualLoopbackTcp = true,
                actualClientUiThisRun = false, scaffoldLoaded = true, productionQualificationClaimed = false,
                durationMs = timer.Elapsed.TotalMilliseconds, clients = clients.Select(x => x.Evidence()),
                note = "TCP59 triggers native HitSwitch, pump collection and XferWater. TCP147 tests short-frame zero-write safety then three real SSC/native page swaps, outgoing relay, product transaction witness and effective armor/accessory effects. Console only prepares owned fixtures. Scaffold makes attribution incomplete; client multi-slot completion and production qualification are not claimed.",
                console = host.ConsoleLines().Skip(logStart).Where(x => x.Contains("ANTICHEAT_") || x.Contains("QA_M7_PUMP") || x.Contains("QA_M7_LOADOUT") || x.Contains("GameplayScaffold qa_m7")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m7-gameplay-business:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
        void Healthy(LabClient client, string label)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, label);
        }
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-actual-SSC-and-authentication"); return client;
        }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m7_pump_state"); return await ReadFresh(label, requested);
        }
        async Task<JsonElement> LoadoutState(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m7_loadout_state");
            return await ReadFresh(label, requested, "m7-loadout-state-latest.json");
        }
        async Task<JsonElement> ReadFresh(string label, DateTimeOffset requested, string file = "m7-pump-state-latest.json")
        {
            string path = Path.Combine(host.ReportDirectory, file); var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                    try
                    {
                        string text = await File.ReadAllTextAsync(path); using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return document.RootElement.Clone(); }
                    }
                    catch (Exception error) when (error is IOException or JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Fresh M7 pump fixture snapshot missing: " + label);
        }
        static bool Same(JsonElement before, JsonElement after, string property) => before.GetProperty(property).GetRawText() == after.GetProperty(property).GetRawText();
        static JsonElement Fixture(JsonElement state, string name) => state.GetProperty("fixtures").EnumerateArray().Single(x => x.GetProperty("Label").GetString() == name);
    }
}
