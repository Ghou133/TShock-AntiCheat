using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real loopback input, native server consumption, and actual peer responses in an owned fixture.</summary>
internal static class M6GameplaySafetyScenario
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m6-gameplay-safety");
        Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2);
        var frames = new List<object>(16);
        var results = new List<object>(16);
        string status = "failed", failure = "";
        var timer = Stopwatch.StartNew();
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime-verified");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-isolated-run");
            await host.FixtureSnapshot();
            var observer = await Actor("M6SafetyObserver");
            var actor = await Actor("M6SafetyActor");
            await host.ConsoleCommand("qa_m6_safety " + actor.Name);
            var prepared = await ReadFresh("m6-safety-state-latest.json", "prepared", DateTimeOffset.MinValue);
            var chest = prepared.GetProperty("chest");
            short chestId = chest.GetProperty("id").GetInt16();
            short chestX = chest.GetProperty("x").GetInt16(), chestY = chest.GetProperty("y").GetInt16();
            ushort module = prepared.GetProperty("module").GetUInt16();
            Check(module != 0 && module != 1, "runtime-registered-crafting-module-id");
            Check(Wood(prepared) == 30 && chest.GetProperty("maxItems").GetInt32() == 40,
                "fresh-owned-forty-slot-chest-with-thirty-wood");
            await actor.MoveTo((chestX - 3) * 16, (chestY - 1) * 16);
            await actor.PingAsync();
            await observer.MoveTo((chestX - 5) * 16, (chestY - 1) * 16);
            await observer.PingAsync();
            var before = await State("closed-nearby-before");
            Check(before.GetProperty("actor").GetProperty("nativeChest").GetInt32() == -1 &&
                before.GetProperty("actor").GetProperty("ActiveChest").GetInt32() == -1 &&
                before.GetProperty("chest").GetProperty("usingPlayer").GetInt32() == -1 &&
                !before.GetProperty("chest").GetProperty("locked").GetBoolean() &&
                before.GetProperty("actor").GetProperty("inRange").GetBoolean() &&
                before.GetProperty("actor").GetProperty("regionAllowed").GetBoolean() &&
                before.GetProperty("inventoryContextPresent").GetBoolean(),
                "real-closed-unlocked-nearby-chest-with-ordinary-region-permission");

            // Legal first: normal remote consumption from a closed nearby chest needs no open/lease step.
            await Craft("legal-closed-nearby", [(9, 10)], [chestId], true, 20, 1);

            // This frame declares a million ingredients but contains no entries. The raw pre-parser
            // must reject before native List(capacity). It is never sent without AntiCheat loaded.
            await observer.Drain(TimeSpan.FromMilliseconds(100));
            var hugeBefore = await State("tiny-huge-count-before");
            int hugeWrites = observer.ChestItems.Count, hugeResponses = actor.CraftResponses.Count;
            var hugeFrame = LabClient.Packet(82, w => { w.Write(module); w.Write7BitEncodedInt(1_000_000); });
            frames.Add(new { label = "tiny-huge-count", hex = Convert.ToHexString(hugeFrame),
                declaredCount = 1_000_000, actualFrameBytes = hugeFrame.Length });
            await actor.SendBatch(hugeFrame);
            await actor.PingAsync();
            await observer.Drain(TimeSpan.FromMilliseconds(200));
            var hugeAfter = await State("tiny-huge-count-after");
            Check(hugeAfter.GetProperty("malformedBlocked").GetInt64() == hugeBefore.GetProperty("malformedBlocked").GetInt64() + 1,
                "tiny-huge-count-rejected-by-raw-parser");
            NoCraftMutation(hugeBefore, hugeAfter, hugeWrites, "tiny-huge-count");
            Check(actor.CraftResponses.Count == hugeResponses, "malformed-request-does-not-reach-native-response");
            Healthy(actor, "tiny-huge-count-does-not-ban-account-or-break-next-ping");

            // Duplicate identities must not make the real twenty wood look like forty.
            await Craft("duplicate-chest-insufficient", [(9, 30)], [chestId, chestId], false, 20, 0);
            // Requirements share the same remaining balance, preserving their original order.
            await Craft("cumulative-requirements-insufficient", [(9, 15), (9, 15)], [chestId], false, 20, 0);
            await Craft("legal-cumulative-requirements", [(9, 4), (9, 6)], [chestId], true, 10, 2);
            await Craft("legal-duplicate-reference-normalized", [(9, 4)], [chestId, chestId], true, 6, 1);

            await host.ConsoleCommand("qa_m5_target " + actor.Name);
            var targetBefore = await M5State("npc-target-before");
            byte target = targetBefore.GetProperty("target").GetProperty("index").GetByte();
            byte generation = targetBefore.GetProperty("target").GetProperty("generation").GetByte();
            Check(targetBefore.GetProperty("target").GetProperty("life").GetInt32() == 3000,
                "owned-live-npc-baseline");
            await Strike("legal-native-hit", 0f, 1, 2980, true);
            await Strike("unsafe-NaN-knockback", float.NaN, 1, 2980, false);
            await Strike("unsafe-direction255", 0f, 255, 2980, false);
            await Strike("legal-after-unsafe-hit", 0f, 2, 2960, true);
            await Statue("allowed", true);
            await Statue("protected", false);
            Healthy(actor, "all-filtered-inputs-retain-ordinary-actor-account");
            Healthy(observer, "observer-never-attributed");
            Check(!host.ConsoleLines().Skip(logStart).Any(x => x.Contains("ANTICHEAT_INCIDENT ")),
                "safety-and-unknown-damage-do-not-create-proven-cheat-incidents");
            status = "passed";

            async Task Craft(string label, (int Item, int Stack)[] required, int[] chests,
                bool approved, int expectedWood, int expectedConsumes)
            {
                await observer.Drain(TimeSpan.FromMilliseconds(100));
                var prior = await State(label + "-before");
                int responses = actor.CraftResponses.Count, writes = observer.ChestItems.Count;
                var frame = LabClient.Packet(82, w =>
                {
                    w.Write(module); w.Write7BitEncodedInt(required.Length);
                    foreach (var item in required) { w.Write(item.Item); w.Write7BitEncodedInt(item.Stack); }
                    w.Write7BitEncodedInt(chests.Length);
                    foreach (int id in chests) w.Write7BitEncodedInt(id);
                });
                frames.Add(new { label, hex = Convert.ToHexString(frame),
                    requirements = required.Select(x => new { item = x.Item, stack = x.Stack }), chests });
                await actor.SendBatch(frame);
                await actor.WaitUntil(() => actor.CraftResponses.Skip(responses).Any(x => x.Module == module), TimeSpan.FromSeconds(5));
                await actor.PingAsync();
                await observer.Drain(TimeSpan.FromMilliseconds(200));
                var after = await State(label + "-after");
                var response = actor.CraftResponses.Skip(responses).Where(x => x.Module == module).ToArray();
                Check(response.Length == 1 && response.Single().Approved == approved, label + "-actual-single-approval-response");
                Check(Wood(after) == expectedWood, label + "-exact-native-remaining-wood");
                Check(Consumes(after) - Consumes(prior) == expectedConsumes, label + "-native-consume-entry-count");
                if (!approved) NoCraftMutation(prior, after, writes, label);
                else
                {
                    var updates = observer.ChestItems.Skip(writes).Where(x => x.Chest == chestId).ToArray();
                    Check(updates.Length == expectedConsumes && updates.Last().Slot == 0 &&
                        updates.Last().Item == 9 && updates.Last().Stack == expectedWood,
                        label + "-actual-peer-slot-broadcasts-match-consumption");
                }
                if (chests.Length > 1)
                    Check(after.GetProperty("duplicateTargetsRemoved").GetInt64() == prior.GetProperty("duplicateTargetsRemoved").GetInt64() + 1,
                        label + "-actual-duplicate-reference-normalization");
                Check(after.GetProperty("lastSimulationSteps").GetInt32() <= 131072 &&
                    after.GetProperty("effectsDropped").GetInt32() == 0, label + "-bounded-work-and-complete-effects");
                Healthy(actor, label + "-no-sanction");
                results.Add(new { label, approved, woodBefore = Wood(prior), woodAfter = Wood(after),
                    actualConsumeEntries = Consumes(after) - Consumes(prior),
                    actualPeerUpdates = observer.ChestItems.Skip(writes).Where(x => x.Chest == chestId)
                        .Select(x => new { chest = x.Chest, slot = x.Slot, item = x.Item, stack = x.Stack }).ToArray() });
            }

            void NoCraftMutation(JsonElement prior, JsonElement after, int priorUpdates, string label)
            {
                Check(prior.GetProperty("chest").GetProperty("items").GetRawText() ==
                    after.GetProperty("chest").GetProperty("items").GetRawText(), label + "-all-forty-slots-unchanged");
                Check(Consumes(prior) == Consumes(after), label + "-zero-partial-consume-entries");
                Check(!observer.ChestItems.Skip(priorUpdates).Any(x => x.Chest == chestId), label + "-zero-peer-slot-writes");
                Check(WoodDrops(prior).SequenceEqual(WoodDrops(after)), label + "-no-new-wood-drop-or-refund");
            }

            async Task Strike(string label, float knockback, byte direction, int expectedLife, bool forwarded)
            {
                await observer.Drain(TimeSpan.FromMilliseconds(100));
                var prior = await M5State(label + "-before");
                int outgoing = observer.NpcStrikes.Count;
                var frame = LabClient.Packet(28, w => { w.Write(target); w.Write(generation); w.Write((short)20);
                    w.Write(knockback); w.Write(direction); w.Write((byte)0); });
                frames.Add(new { label, hex = Convert.ToHexString(frame), damage = 20,
                    knockback = float.IsNaN(knockback) ? "NaN" : knockback.ToString(System.Globalization.CultureInfo.InvariantCulture), direction });
                await actor.SendBatch(frame);
                await actor.PingAsync();
                await observer.Drain(TimeSpan.FromMilliseconds(250));
                var after = await M5State(label + "-after");
                Check(after.GetProperty("target").GetProperty("life").GetInt32() == expectedLife &&
                    after.GetProperty("target").GetProperty("generation").GetByte() == generation,
                    label + "-actual-same-generation-npc-life");
                var sent = observer.NpcStrikes.Skip(outgoing).Where(x => x.Target == target && x.Generation == generation).ToArray();
                Check(forwarded ? sent.Length == 1 && sent.Single().Damage == 20 && float.IsFinite(sent.Single().Knockback)
                    : sent.Length == 0, label + "-actual-peer-strike-output");
                if (!forwarded)
                {
                    Check(after.GetProperty("target").GetRawText() == prior.GetProperty("target").GetRawText(),
                        label + "-no-life-position-or-velocity-change");
                    Check(!after.GetProperty("effects").EnumerateArray().Skip(prior.GetProperty("effects").GetArrayLength())
                        .Any(x => x.GetProperty("effect").GetProperty("kind").GetString() == "strike-method-entry"),
                        label + "-native-strike-not-entered");
                }
                var observedFrames = after.GetProperty("packetObservations").EnumerateArray()
                    .Skip(prior.GetProperty("packetObservations").GetArrayLength()).ToArray();
                Check(observedFrames.Length == 1 && observedFrames.Single().GetProperty("Handled").GetBoolean() == !forwarded &&
                    observedFrames.Single().GetProperty("encodedDirection").GetByte() == direction &&
                    after.GetProperty("packetObservationsDropped").GetInt32() == 0,
                    label + "-actual-post-hook-cancellation-state");
                Check(after.GetProperty("effectsDropped").GetInt32() == 0, label + "-complete-bounded-effects");
                Healthy(actor, label + "-no-account-ban");
                results.Add(new { label, expectedLife, actualLife = after.GetProperty("target").GetProperty("life").GetInt32(),
                    actualPeerStrikes = sent.Select(x => new { target = x.Target, generation = x.Generation,
                        damage = x.Damage, knockback = x.Knockback, direction = x.Direction }).ToArray() });
            }

            async Task Statue(string name, bool permitted)
            {
                string label = "real-switch-statue-" + name;
                var initial = await State(label + "-position");
                var circuit = initial.GetProperty("statues").GetProperty("fixtures").EnumerateArray()
                    .Single(x => x.GetProperty("Label").GetString() == name);
                short x = circuit.GetProperty("SwitchX").GetInt16(), y = circuit.GetProperty("SwitchY").GetInt16();
                await actor.MoveTo((x - 2) * 16, (y - 2) * 16);
                await actor.PingAsync();
                await observer.Drain(TimeSpan.FromMilliseconds(100));
                var prior = await State(label + "-before");
                var before = prior.GetProperty("statues");
                circuit = before.GetProperty("fixtures").EnumerateArray().Single(f => f.GetProperty("Label").GetString() == name);
                var footprint = circuit.GetProperty("footprint").EnumerateArray().ToArray();
                Check(before.GetProperty("guardPresent").GetBoolean() && before.GetProperty("contractHealthy").GetBoolean() &&
                    circuit.GetProperty("switchAllowed").GetBoolean() && !circuit.GetProperty("cooldownPresent").GetBoolean(),
                    label + "-root-guard-live-source-switch-allowed-and-no-prior-cooldown");
                Check(footprint.Length == 6 && footprint.Count(p => p.GetProperty("allowed").GetBoolean()) == (permitted ? 6 : 5) &&
                    (permitted || !footprint.Last().GetProperty("allowed").GetBoolean()),
                    label + "-exact-last-footprint-cell-permission");
                var npcBefore = before.GetProperty("spawnedNpcs").EnumerateArray()
                    .Select(n => n.GetProperty("index").GetInt32() + ":" + n.GetProperty("generation").GetByte()).ToHashSet();
                int peerBefore = observer.NpcTypes.Count;
                int statuePeerBefore = observer.NpcStatueTypes.Count;
                var frame = LabClient.Packet(59, w => { w.Write(x); w.Write(y); });
                frames.Add(new { label, hex = Convert.ToHexString(frame), switchX = x, switchY = y,
                    statueX = circuit.GetProperty("X").GetInt32(), statueY = circuit.GetProperty("Y").GetInt32(),
                    note = "One actual TCP59; native MessageBuffer sets CurrentUser and calls HitSwitch over the fixture wire graph." });
                await actor.SendBatch(frame);
                await actor.PingAsync();
                await observer.Drain(TimeSpan.FromMilliseconds(200));
                var afterState = await State(label + "-after");
                var after = afterState.GetProperty("statues");
                var effects = after.GetProperty("effects").EnumerateArray().Skip(before.GetProperty("effects").GetArrayLength()).ToArray();
                var wireEntries = effects.Where(e => e.GetProperty("kind").GetString() == "statue-execution-hook" &&
                    e.GetProperty("Label").GetString() == name).ToArray();
                Check(wireEntries.Length == 1 && wireEntries[0].GetProperty("currentUser").GetInt32() == actor.Slot &&
                    wireEntries[0].GetProperty("ContinueExecution").GetBoolean() == permitted,
                    label + "-actual-graph-reached-statue-with-current-actor-and-cancellation");
                Check(after.GetProperty("allowed").GetInt64() - before.GetProperty("allowed").GetInt64() == (permitted ? 1 : 0) &&
                    after.GetProperty("permissionBlocked").GetInt64() - before.GetProperty("permissionBlocked").GetInt64() == (permitted ? 0 : 1) &&
                    after.GetProperty("budgetBlocked").GetInt64() == before.GetProperty("budgetBlocked").GetInt64(),
                    label + "-actual-root-guard-counter-delta");
                var created = after.GetProperty("spawnedNpcs").EnumerateArray().Where(n => !npcBefore.Contains(
                    n.GetProperty("index").GetInt32() + ":" + n.GetProperty("generation").GetByte())).ToArray();
                var cooldownEntries = effects.Where(e => e.GetProperty("kind").GetString() == "native-cooldown-entry" &&
                    e.GetProperty("Label").GetString() == name).ToArray();
                var spawnEntries = effects.Where(e => e.GetProperty("kind").GetString() == "native-statue-npc-create-entry" &&
                    e.GetProperty("Label").GetString() == name).ToArray();
                Check(cooldownEntries.Length == (permitted ? 1 : 0) && spawnEntries.Length == (permitted ? 1 : 0) &&
                    (!permitted || spawnEntries[0].GetProperty("cooldownPresent").GetBoolean()),
                    label + "-actual-cooldown-write-and-npc-create-or-zero-native-entry");
                Check(created.Length == (permitted ? 1 : 0) && created.All(n => n.GetProperty("type").GetInt32() == 1 &&
                    n.GetProperty("SpawnedFromStatue").GetBoolean()), label + "-exact-new-native-statue-npc-generation");
                var exports = effects.Where(e => e.GetProperty("kind").GetString() == "native-statue-npc-export").ToArray();
                if (permitted)
                {
                    int npc = created.Single().GetProperty("index").GetInt32();
                    byte npcGeneration = created.Single().GetProperty("generation").GetByte();
                    Check(exports.Any(e => e.GetProperty("index").GetInt32() == npc && e.GetProperty("generation").GetByte() == npcGeneration) &&
                        observer.NpcStatueTypes.Skip(statuePeerBefore).Any(n => n.Slot == npc && n.Generation == npcGeneration && n.Type == 1),
                        label + "-actual-native-and-peer23-export-of-created-generation");
                }
                else
                {
                    Check(exports.Length == 0 && observer.NpcStatueTypes.Skip(statuePeerBefore)
                        .All(n => npcBefore.Contains(n.Slot + ":" + n.Generation)),
                        label + "-zero-new-statue-export-or-peer-generation");
                    Check(!after.GetProperty("fixtures").EnumerateArray().Single(f => f.GetProperty("Label").GetString() == name)
                        .GetProperty("cooldownPresent").GetBoolean(), label + "-rejected-statue-has-no-cooldown-entry");
                }
                Check(after.GetProperty("effectsDropped").GetInt32() == 0 && !after.GetProperty("nativeRunning").GetBoolean() &&
                    after.GetProperty("nativeCurrentUser").GetInt32() == 255, label + "-bounded-evidence-and-native-source-reset");
                var preserved = await M5State(label + "-combat-target-preserved");
                Check(preserved.GetProperty("target").GetProperty("index").GetInt32() == target &&
                    preserved.GetProperty("target").GetProperty("generation").GetByte() == generation &&
                    preserved.GetProperty("target").GetProperty("life").GetInt32() == 2960,
                    label + "-prior-combat-target-not-replaced-or-mutated");
                Healthy(actor, label + "-actor-unbanned");
                results.Add(new { label, permitted, nativeEffects = effects, created,
                    actualPeer23 = observer.NpcTypes.Skip(peerBefore).Select(n => new { n.Slot, n.Generation, n.Type }).ToArray(),
                    actualStatuePeer23 = observer.NpcStatueTypes.Skip(statuePeerBefore).Select(n => new { n.Slot, n.Generation, n.Type }).ToArray() });
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            await Write("frames.json", frames);
            await Write("results.json", results);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientUiThisRun = false,
                scaffoldLoaded = true, productionQualificationClaimed = false, durationMs = timer.Elapsed.TotalMilliseconds,
                note = "Real native server crafting, NPC life/output, and TCP59 HitSwitch-to-statue permission/side-effect checks in separate owned circuits. Recipe result grant occurs on clients and is not simulated or claimed here.",
                clients = clients.Select(x => x.Evidence()), console = host.ConsoleLines().Skip(logStart).Where(x =>
                    x.Contains("ANTICHEAT_") || x.Contains("QA_M6") || x.Contains("GameplayScaffold qa_m6")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m6-gameplay-safety:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, Json));
        long BanCount(string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + name); return Convert.ToInt64(command.ExecuteScalar());
        }
        void Healthy(LabClient client, string label) => Check(client.Authenticated && !client.Closed &&
            client.DisconnectReason is null && BanCount(client.Name) == 0, label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-authenticated-with-actual-SSC");
            return client;
        }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m6_state");
            return await ReadFresh("m6-safety-state-latest.json", label, requested);
        }
        async Task<JsonElement> M5State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m5_state");
            return await ReadFresh("m5-state-latest.json", label, requested);
        }
        async Task<JsonElement> ReadFresh(string file, string label, DateTimeOffset requested)
        {
            string path = Path.Combine(host.ReportDirectory, file); var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path); using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return doc.RootElement.Clone(); }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Fresh M6 fixture snapshot missing: " + label);
        }
        static int Wood(JsonElement state) => state.GetProperty("chest").GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("type").GetInt32() == 9).Sum(x => x.GetProperty("stack").GetInt32());
        static int Consumes(JsonElement state) => state.GetProperty("consumeEntries").GetArrayLength();
        static string[] WoodDrops(JsonElement state) => state.GetProperty("worldItems").EnumerateArray()
            .Where(x => x.GetProperty("type").GetInt32() == 9).Select(x => x.GetProperty("index").GetInt32() + ":" +
                x.GetProperty("type").GetInt32() + ":" + x.GetProperty("stack").GetInt32()).Order(StringComparer.Ordinal).ToArray();
    }
}
