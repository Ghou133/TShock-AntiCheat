using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Bounded, real-loopback acceptance against the existing NetworkLab transport and scaffold.
// World writes are authorized chest slots and native NPC strikes with measured life and peer output.
internal static class M5MaintenanceLoadScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host, int samplesPerPhase = 80, int maximumLoadSeconds = 30)
    {
        if (samplesPerPhase is < 68 or > 256 || maximumLoadSeconds is < 10 or > 30)
            throw new ArgumentOutOfRangeException(nameof(samplesPerPhase), "Use 68..256 samples per phase and 10..30 seconds.");
        string directory = Path.Combine(host.ReportDirectory, "m5-maintenance-load");
        Directory.CreateDirectory(directory);
        var actors = new List<LabClient>(8);
        var samples = new List<PingSample>(samplesPerPhase * 3);
        var metrics = new List<object>(8);
        var phaseResults = new List<object>(3);
        var connections = new List<object>(4);
        var gameplayInputs = new List<object>(128);
        byte gameplayTarget = 255, gameplayGeneration = 0;
        int gameplayExpectedLife = 3000;
        var total = Stopwatch.StartNew();
        bool journalLocked = false, hardFault = false, complete = false;
        string? failure = null;
        try
        {
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "both-isolation-markers-present");
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.RunDirectory, "tshock/anticheat.json")));
            Check(config.RootElement.GetProperty("ExecutionScope").GetString() == "TestLab" && host.RuntimeVerified(), "fresh-verified-TestLab-target-required");
            Check(host.ConnectFrom is not null, "actual-source-IP-binding-available");

            var first = await Actor("M5MaintenanceA");
            var initialSecond = await Actor("M5LoadInitialB");
            Check(Address(first) == "127.0.0.1" && Address(initialSecond) == "127.0.0.1", "initial-actual-loopback-sources-match");
            await host.ConsoleCommand("qa_m5_target " + first.Name);
            var targetSetup = await State("m6-gameplay-target-setup");
            gameplayTarget = targetSetup.GetProperty("target").GetProperty("index").GetByte();
            gameplayGeneration = targetSetup.GetProperty("target").GetProperty("generation").GetByte();
            Check(TargetLife(targetSetup) == 3000, "real-gameplay-load-target-initialized-once");
            var active = new[] { first, initialSecond };
            var load = Stopwatch.StartNew();
            await LoadPhase("normal", active, 60, false, load);
            await LoadPhase("bounded-burst", active, 0, true, load);
            int initialPort = Port(initialSecond);
            string initialAddress = Address(initialSecond);
            await initialSecond.DisposeAsync();
            await Task.Delay(150);
            Check(load.Elapsed.TotalSeconds < maximumLoadSeconds - 3, "source-IP-reconnect-has-time-budget");
            var second = await Actor("M5MaintenanceB", IPAddress.Parse("127.0.0.2"));
            Check(Address(second) == "127.0.0.2" && Address(second) != initialAddress && Address(first) == "127.0.0.1",
                "replacement-actual-source-IP-changed-control-source-preserved");
            active = [first, second];
            await LoadPhase("source-IP-change-loopback", active, 25, false, load);
            load.Stop();
            Check(load.Elapsed.TotalSeconds <= maximumLoadSeconds && samples.Count >= 200,
                "bounded-load-under30seconds-at-least200-real154-roundtrips");
            Check(actors.All(x => BanCount(x.Name) == 0), "load-does-not-create-account-bans");
            await Write("load-results.json", new
            {
                loadDurationMs = load.Elapsed.TotalMilliseconds, maximumLoadSeconds, configuredSamplesPerPhase = samplesPerPhase,
                maximumConcurrentClients = 2, actualLoadConnections = 3,
                actualLoopbackSourceIPs = new[] { Address(first), Address(second) }.Distinct().ToArray(),
                sourceIPRotationTested = true, sourcePortChanged = Port(second) != initialPort,
                publicIPRotationTested = false, proxyAcceptanceTested = false, connections, phases = phaseResults,
                overall = Distribution(samples.Select(x => x.RoundTripMs)), samples, metrics, gameplayInputs,
                actualGameplayPath = "packet28 native NPC strike; damage1 periodically interleaved with measured pings",
                note = "Packet154 sender-only replies measured over real TCP with one outstanding ping per client. Source IPs are actual socket local endpoints on 127.0.0.1 and 127.0.0.2 only; no public IP or proxy acceptance claim. Finite loopback sample, not internet latency or production player capacity. p99 omitted below 1000 samples."
            });

            // Establish successful counterexamples for both actors on the exact same paths first.
            await host.ConsoleCommand("qa_prepare " + first.Name);
            await Until(() => File.Exists(Path.Combine(host.RunDirectory, "qa-scaffold-room.json")), TimeSpan.FromSeconds(6));
            await first.Drain(TimeSpan.FromMilliseconds(300));
            await host.ConsoleCommand("qa_chests " + first.Name);
            await Until(() => File.Exists(Path.Combine(host.RunDirectory, "qa-chests.json")), TimeSpan.FromSeconds(6));
            using var chestFile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.RunDirectory, "qa-chests.json")));
            var chests = new[] { chestFile.RootElement.GetProperty("primary").Clone(), chestFile.RootElement.GetProperty("secondary").Clone() };
            for (int i = 0; i < active.Length; i++)
            {
                var actor = active[i]; var chest = chests[i];
                await actor.Send(31, w => { w.Write(chest.GetProperty("x").GetInt16()); w.Write(chest.GetProperty("y").GetInt16()); });
                await actor.WaitUntil(() => actor.ActiveChest == chest.GetProperty("id").GetInt16(), TimeSpan.FromSeconds(5));
                await Mutate(actor, chest, 1, "M5-normal-before-" + i);
            }
            await DrainAll(active);
            var healthyBefore = await Snapshot("healthy-before-maintenance");
            CheckValues(healthyBefore, active, 1, "healthy-first-normal");
            Check(active.All(x => active.All(y => x.Messages.Any(m => m.Contains("M5-normal-before-" + Array.IndexOf(active, y), StringComparison.Ordinal)))),
                "both-normal-chat-markers-delivered-before-maintenance");
            await GameplayWrites("healthy-before-maintenance", active, true);

            var proof = await Actor("M5MaintenanceProof");
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", proof.Name);
            int incidentStart = host.ConsoleLines().Length;
            await proof.SendBatch(LabClient.Packet(153, w => { w.Write((byte)0); w.Write((short)1); }),
                LabClient.Packet(5, w => LabClient.WriteInventory(w, proof.Slot, 77, 9)));
            await proof.WaitUntil(() => proof.Closed || proof.DisconnectReason is not null, TimeSpan.FromSeconds(8));
            Check(proof.DisconnectReason == "AntiCheat proven violation." && proof.ProofBatches == 1, "first-proof-actually-revoked-and-disconnected");
            await Until(() => BanCount(proof.Name) == 1, TimeSpan.FromSeconds(8));
            Check(host.ConsoleLines().Skip(incidentStart).Count(x => x.Contains("ANTICHEAT_INCIDENT ") &&
                x.Contains("accountId=" + account + " ") && x.Contains("rule=NPC01.ServerDebuffDamage ") &&
                x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) && x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1,
                "exactly-one-first-proof-cancel-revoke-incident");
            host.RecordProvenAccount(proof.Name, account, "NPC01.ServerDebuffDamage");
            await proof.DisposeAsync();
            await Task.Delay(250);
            string intentDirectory = Path.Combine(host.RunDirectory, "tshock/anticheat/enforcement");
            var journals = Directory.GetFiles(intentDirectory, "*.json").Order(StringComparer.Ordinal).ToArray();
            Check(journals.Length > 0, "real-first-proof-durable-intent-exists");
            var beforeHashes = journals.ToDictionary(path => Path.GetFileName(path)!, Hash);
            var stable = await State("journal-ready");
            Check(!Maintenance(stable), "healthy-before-real-journal-read-failure");

            await host.ConsoleCommand("qa_m5_recoverable");
            journalLocked = true;
            var maintenance = await StateUntil("recoverable-maintenance", x => x.GetProperty("engineMaintenance").GetBoolean());
            Check(!maintenance.GetProperty("infrastructureFailed").GetBoolean(), "real-journal-failure-enters-engine-maintenance");
            var retainedBefore = await Snapshot("maintenance-before-attempts");
            for (int i = 0; i < active.Length; i++) await Mutate(active[i], chests[i], 2, "M5-blocked-maintenance-" + i);
            var maintenancePings = await Task.WhenAll(active.Select(x => x.PingAsync()));
            await DrainAll(active);
            var retainedAfter = await Snapshot("maintenance-after-attempts");
            CheckValues(retainedAfter, active, 1, "maintenance-normal-writes-blocked");
            Check(ChestHash(retainedBefore, "primary") == ChestHash(retainedAfter, "primary") &&
                ChestHash(retainedBefore, "secondary") == ChestHash(retainedAfter, "secondary"), "maintenance-actual-world-chest-states-unchanged");
            Check(active.All(x => !x.Messages.Any(m => m.Contains("M5-blocked-maintenance-", StringComparison.Ordinal))), "maintenance-chat-not-relayed");
            Check(active.All(x => !x.Closed && BanCount(x.Name) == 0), "maintenance-does-not-ban-or-disconnect-either-normal-account");
            Check(BanCount(proof.Name) == 1, "revoked-account-permanent-ban-retained-during-maintenance");
            await GameplayWrites("recoverable-maintenance", active, false);
            await Write("maintenance-input-outcomes.json", new { maintenancePingsMs = maintenancePings, actors = active.Select(x => x.Evidence()),
                worldObject = "two independently authorized open chests and one owned live NPC", tileEditingClaimed = false,
                input = new { inventoryPacket = 5, chatPacket = 82, worldChestPacket = 32, nativeNpcStrikePacket = 28, allowedPingPacket = 154 },
                gameplayInputs,
                preexistingProofAccount = account, revokedSocketClosed = proof.Closed });

            await host.ConsoleCommand("qa_m5_recover");
            journalLocked = false;
            var recovered = await StateUntil("automatically-recovered", x => !Maintenance(x));
            Check(journals.All(path => Hash(path) == beforeHashes[Path.GetFileName(path)]), "real-intent-bytes-unchanged-across-read-lock-recovery");
            Check(BanCount(proof.Name) == 1 && active.All(x => BanCount(x.Name) == 0), "recovery-preserves-only-proven-ban");
            // Neither queued actions nor an SSC replay may apply the blocked value2 after recovery.
            var beforeResend = await Snapshot("recovered-before-new-input");
            CheckValues(beforeResend, active, 1, "blocked-actions-never-replayed-on-recovery");
            await GameplayWrites("recovered-native-gameplay", active, true);
            for (int i = 0; i < active.Length; i++) await Mutate(active[i], chests[i], 3, "M5-normal-recovered-" + i);
            await Task.WhenAll(active.Select(x => x.PingAsync()));
            await DrainAll(active);
            var afterResend = await Snapshot("recovered-new-normal-input");
            CheckValues(afterResend, active, 3, "normal-inventory-world-writes-resume-after-health-recovery");
            Check(active.All(x => Enumerable.Range(0, 2).All(i => x.Messages.Any(m => m.Contains("M5-normal-recovered-" + i, StringComparison.Ordinal)))),
                "normal-chat-resumes-for-both-after-health-recovery");

            await host.ConsoleCommand("qa_m5_maintenance");
            hardFault = true;
            var hard = await StateUntil("hard-dispatcher-fault", x => x.GetProperty("infrastructureFailed").GetBoolean());
            Check(hard.GetProperty("callbackFailed").GetInt32() == 1, "actual-wrong-thread-dispatcher-failure-latched-once");
            for (int i = 0; i < active.Length; i++) await Mutate(active[i], chests[i], 4, "M5-blocked-hardfault-" + i);
            await Task.WhenAll(active.Select(x => x.PingAsync()));
            await DrainAll(active);
            var hardAfter = await Snapshot("hard-fault-existing-accounts");
            CheckValues(hardAfter, active, 3, "hard-maintenance-blocks-existing-normal-inventory-world-writes");
            Check(active.All(x => !x.Messages.Any(m => m.Contains("M5-blocked-hardfault-", StringComparison.Ordinal)) && BanCount(x.Name) == 0),
                "hard-maintenance-blocks-chat-without-account-sanction");
            await GameplayWrites("hard-maintenance", active, false);
            foreach (var actor in active) await actor.DisposeAsync();
            await Task.Delay(400);
            var empty = await Snapshot("hard-fault-zero-online");
            var idle = await State("hard-fault-idle-callback");
            Check(empty.GetProperty("players").GetArrayLength() == 0 && Maintenance(idle) && idle.GetProperty("callbackFailed").GetInt32() == 1,
                "latched-maintenance-survives-zero-online-idle-callback");

            var admission = await host.Connect("M5MaintenanceAdmission"); actors.Add(admission);
            await admission.Send(1, w => w.Write("Terraria326"));
            await admission.WaitUntil(() => admission.Closed || admission.DisconnectReason is not null, TimeSpan.FromSeconds(8));
            Check(!admission.Authenticated && admission.Slot == 255 && (admission.Closed || admission.DisconnectReason is not null), "new-real-TCP-admission-refused-during-hard-maintenance");
            await Write("hard-fault-admission.json", admission.Evidence());
            await admission.DisposeAsync();
            Check(BanCount(proof.Name) == 1 && actors.Where(x => x != proof).All(x => BanCount(x.Name) == 0), "all-faults-and-load-create-no-additional-permanent-bans");
            complete = true;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            throw;
        }
        finally
        {
            // Release only this scenario's file handle through the guarded scaffold; never reset security flags.
            if (journalLocked)
            {
                try { await host.ConsoleCommand("qa_m5_recover"); }
                catch (Exception ex) { failure ??= "cleanup: " + ex.GetType().Name + ": " + ex.Message; }
            }
            foreach (var actor in actors)
            {
                try { await actor.DisposeAsync(); }
                catch (Exception ex) { failure ??= "dispose: " + ex.GetType().Name + ": " + ex.Message; }
            }
            await Write("summary.json", new { status = complete && failure is null ? "passed" : "failed", failure,
                elapsedMs = total.Elapsed.TotalMilliseconds, hardFaultIntentionallyLatched = hardFault,
                loadSamples = samples.Count, phases = phaseResults, metrics, samples, gameplayInputs,
                actualClientUiThisRun = false, realServerAndTcp = true, osFirewallMutated = false,
                proofIdentityRoot = "authenticated account and server session", maintenanceWorldPath = "packet32 real open chest and packet28 native NPC strike",
                note = "Hard dispatcher integrity faults intentionally remain latched until server restart. Resource samples do not establish production capacity or final Terraria1.4.5.8 compatibility." });
        }

        void Check(bool value, string label) => host.Assert(value, "m5-maintenance-load:" + label);
        async Task Write(string name, object value) => await File.WriteAllTextAsync(Path.Combine(directory, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        async Task<LabClient> Actor(string name, IPAddress? localBind = null)
        {
            var actor = localBind is null ? await host.Connect(name) :
                await (host.ConnectFrom ?? throw new InvalidOperationException("This load scenario requires actual source IP binding."))(name, localBind);
            actors.Add(actor);
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350 && Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", name) == 1,
                name + "-authenticated-ordinary-account-complete-ssc");
            connections.Add(new { name, sourceIP = Address(actor), sourcePort = Port(actor) });
            return actor;
        }
        async Task LoadPhase(string phase, LabClient[] clients, int pauseMs, bool burst, Stopwatch load)
        {
            var phaseTimer = Stopwatch.StartNew();
            await DrainAll(clients);
            var before = await State(phase + "-before");
            Check(TargetLife(before) == gameplayExpectedLife, phase + "-prior-native-life-preserved");
            int offset = samples.Count;
            int controlPackets = 0, npcStrikes = 0;
            var outputStarts = clients.ToDictionary(x => x, x => x.NpcStrikes.Count);
            while (samples.Count - offset < samplesPerPhase)
            {
                if (load.Elapsed.TotalSeconds >= maximumLoadSeconds - 1)
                    throw new TimeoutException(phase + " exceeded the bounded load deadline.");
                if (burst)
                    foreach (var actor in clients)
                        for (int i = 0; i < 4; i++) { await actor.UseItem(false); controlPackets++; }
                if ((samples.Count - offset) % 16 == 0)
                    foreach (var actor in clients) { await SendGameplayStrike(actor, phase, 1); npcStrikes++; }
                int remaining = samplesPerPhase - (samples.Count - offset);
                var batch = clients.Take(Math.Min(clients.Length, remaining)).ToArray();
                var timings = await Task.WhenAll(batch.Select(x => x.PingAsync(TimeSpan.FromSeconds(1))));
                for (int i = 0; i < batch.Length; i++) samples.Add(new(phase, batch[i].Name, load.Elapsed.TotalMilliseconds, timings[i]));
                if (pauseMs > 0) await Task.Delay(pauseMs);
            }
            await DrainAll(clients);
            var after = await State(phase + "-after");
            Check(load.Elapsed.TotalSeconds <= maximumLoadSeconds, phase + "-load-deadline-respected");
            Check(clients.All(x => !x.Closed && x.Authenticated && BanCount(x.Name) == 0), phase + "-both-normal-accounts-still-healthy");
            Check(!Maintenance(after), phase + "-no-infrastructure-maintenance");
            Check(TargetLife(after) == TargetLife(before) - npcStrikes &&
                TargetGeneration(after) == gameplayGeneration, phase + "-real-gameplay-strikes-have-exact-native-life-cost");
            gameplayExpectedLife = TargetLife(after);
            Check(StrikeEntries(after) - StrikeEntries(before) == npcStrikes,
                phase + "-all-load-strikes-enter-native-method-once");
            int receivedStrikes = clients.Sum(x => x.NpcStrikes.Skip(outputStarts[x]).Count(s =>
                s.Target == gameplayTarget && s.Generation == gameplayGeneration && s.Damage == 1));
            Check(receivedStrikes == npcStrikes, phase + "-real-opposite-peer-received-each-native-hit");
            var measured = samples.Skip(offset).Select(x => x.RoundTripMs).ToArray();
            metrics.Add(new { phase, before = Resource(before), after = Resource(after),
                workingSetDelta = after.GetProperty("process").GetProperty("workingSet").GetInt64() - before.GetProperty("process").GetProperty("workingSet").GetInt64(),
                managedBytesDelta = after.GetProperty("process").GetProperty("managedBytes").GetInt64() - before.GetProperty("process").GetProperty("managedBytes").GetInt64() });
            phaseResults.Add(new { phase, durationMs = phaseTimer.Elapsed.TotalMilliseconds, measured = Distribution(measured),
                concurrentClients = clients.Length, additionalControlPackets = controlPackets,
                actualNpcStrikes = npcStrikes, actualPeerStrikeResponses = receivedStrikes,
                nativeNpcLifeBefore = TargetLife(before), nativeNpcLifeAfter = TargetLife(after),
                clients = clients.Select(x => x.Evidence()).ToArray() });
        }
        async Task SendGameplayStrike(LabClient actor, string phase, short damage)
        {
            var frame = LabClient.Packet(28, w => { w.Write(gameplayTarget); w.Write(gameplayGeneration);
                w.Write(damage); w.Write(0f); w.Write((byte)1); w.Write((byte)0); });
            Check(gameplayInputs.Count < 128, "bounded-gameplay-input-evidence-capacity");
            gameplayInputs.Add(new { phase, actor = actor.Name, packet = 28, target = gameplayTarget,
                generation = gameplayGeneration, damage, frame = Convert.ToHexString(frame) });
            await actor.Send(28, w => w.Write(frame.AsSpan(3)));
        }
        async Task GameplayWrites(string phase, LabClient[] clients, bool permitted)
        {
            await DrainAll(clients);
            var before = await State(phase + "-npc-before");
            Check(TargetLife(before) == gameplayExpectedLife, phase + "-no-blocked-gameplay-replayed-before-new-input");
            var outputStarts = clients.ToDictionary(x => x, x => x.NpcStrikes.Count);
            foreach (var actor in clients) await SendGameplayStrike(actor, phase, 7);
            await Task.WhenAll(clients.Select(x => x.PingAsync()));
            await DrainAll(clients);
            var after = await State(phase + "-npc-after");
            int expected = permitted ? clients.Length : 0;
            Check(TargetLife(after) == TargetLife(before) - expected * 7 && TargetGeneration(after) == gameplayGeneration,
                phase + "-native-npc-life-shows-write-policy");
            gameplayExpectedLife = TargetLife(after);
            Check(StrikeEntries(after) - StrikeEntries(before) == expected,
                phase + "-native-strike-entry-shows-prewrite-maintenance-block");
            int observed = clients.Sum(x => x.NpcStrikes.Skip(outputStarts[x]).Count(s =>
                s.Target == gameplayTarget && s.Generation == gameplayGeneration && s.Damage == 7));
            Check(observed == expected, phase + "-actual-opposite-peer-strike-output-shows-write-policy");
            Check(after.GetProperty("effectsDropped").GetInt32() == 0 && clients.All(x => !x.Closed && BanCount(x.Name) == 0),
                phase + "-complete-effects-and-no-account-sanctions");
            await Write(phase + "-gameplay-outcome.json", new { phase, permitted, target = gameplayTarget,
                generation = gameplayGeneration, nativeLifeBefore = TargetLife(before), nativeLifeAfter = TargetLife(after),
                nativeStrikeEntries = StrikeEntries(after) - StrikeEntries(before), actualPeerStrikeOutputs = observed,
                processBefore = Resource(before), processAfter = Resource(after) });
        }
        int TargetLife(JsonElement state) => state.GetProperty("target").GetProperty("life").GetInt32();
        byte TargetGeneration(JsonElement state) => state.GetProperty("target").GetProperty("generation").GetByte();
        int StrikeEntries(JsonElement state) => state.GetProperty("effects").EnumerateArray().Count(x =>
            x.GetProperty("effect").GetProperty("kind").GetString() == "strike-method-entry");
        async Task Mutate(LabClient actor, JsonElement chest, short value, string marker)
        {
            await actor.Send(5, w => LabClient.WriteInventory(w, actor.Slot, value, 9));
            await actor.Send(32, w => { w.Write(chest.GetProperty("id").GetInt16()); w.Write((byte)0); w.Write(value); w.Write((byte)0); w.Write((short)9); });
            await actor.Chat(marker);
        }
        void CheckValues(JsonElement snapshot, LabClient[] clients, int value, string label)
        {
            foreach (var actor in clients)
            {
                var player = snapshot.GetProperty("players").EnumerateArray().Single(x => x.GetProperty("Name").GetString() == actor.Name);
                Check(player.GetProperty("inventory").EnumerateArray().Any(x => x.GetProperty("slot").GetInt32() == 10 &&
                    x.GetProperty("type").GetInt32() == 9 && x.GetProperty("stack").GetInt32() == value), label + "-inventory-" + actor.Name);
            }
            foreach (string which in new[] { "primary", "secondary" })
                Check(snapshot.GetProperty("chestPair").GetProperty(which).GetProperty("items").EnumerateArray().Any(x =>
                    x.GetProperty("slot").GetInt32() == 0 && x.GetProperty("type").GetInt32() == 9 && x.GetProperty("stack").GetInt32() == value),
                    label + "-actual-chest-" + which);
        }
        async Task<JsonElement> Snapshot(string label)
        {
            var state = await host.FixtureSnapshot(); await Write(label + ".json", state); return state;
        }
        async Task<JsonElement> State(string label)
        {
            string path = Path.Combine(host.ReportDirectory, "m5-state-latest.json");
            string prior = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
            await host.ConsoleCommand("qa_m5_state");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(6))
            {
                await Task.Delay(50);
                if (!File.Exists(path)) continue;
                try
                {
                    string text = await File.ReadAllTextAsync(path);
                    if (text == prior) continue;
                    using var document = JsonDocument.Parse(text);
                    await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text);
                    return document.RootElement.Clone();
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            throw new TimeoutException("M5 state " + label);
        }
        async Task<JsonElement> StateUntil(string label, Func<JsonElement, bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                var state = await State(label);
                if (predicate(state)) return state;
                await Task.Delay(100);
            }
            throw new TimeoutException(label);
        }
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock/tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            command.Parameters.AddWithValue("$name", name); return Convert.ToInt64(command.ExecuteScalar());
        }
        long BanCount(string name) => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + name);
    }

    private sealed record PingSample(string Phase, string Account, double LoadElapsedMs, double RoundTripMs);
    private static async Task DrainAll(IEnumerable<LabClient> actors) => await Task.WhenAll(actors.Select(x => x.Drain(TimeSpan.FromMilliseconds(250))));
    private static bool Maintenance(JsonElement state) => state.GetProperty("infrastructureFailed").GetBoolean() || state.GetProperty("engineMaintenance").GetBoolean();
    private static string? ChestHash(JsonElement state, string which) => state.GetProperty("chestPair").GetProperty(which).GetProperty("currentStateSha256").GetString();
    private static int Port(LabClient actor) => JsonSerializer.SerializeToElement(actor.Evidence()).GetProperty("localPort").GetInt32();
    private static string Address(LabClient actor) => JsonSerializer.SerializeToElement(actor.Evidence()).GetProperty("localAddress").GetString()
        ?? throw new InvalidOperationException("Connected client lacks its actual local IP address.");
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static object Resource(JsonElement state) => new
    {
        utc = state.GetProperty("utc"), process = state.GetProperty("process"), dispatcherPending = state.GetProperty("dispatcherPending"),
        dispatcherMaxDrainMs = state.TryGetProperty("dispatcherMaxDrainMs", out var duration) ? duration : (JsonElement?)null,
        dispatcherCapacity = state.TryGetProperty("dispatcherCapacity", out var capacity) ? capacity : (JsonElement?)null,
        timingMethod = "Actual dispatcher snapshot if exposed; no inferred per-tick timings."
    };
    private static object Distribution(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        double Percentile(double p) => ordered[Math.Clamp((int)Math.Ceiling(p * ordered.Length) - 1, 0, ordered.Length - 1)];
        return new { count = ordered.Length, minMs = ordered[0], medianMs = Percentile(.5), p95Ms = Percentile(.95),
            p99Ms = ordered.Length >= 1000 ? (double?)Percentile(.99) : null, maxMs = ordered[^1], meanMs = ordered.Average(),
            percentileMethod = "nearest-rank", p99Status = ordered.Length >= 1000 ? "reported" : "not-reported-fewer-than1000-samples" };
    }
    private static async Task Until(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed >= timeout) throw new TimeoutException("M5 maintenance scenario condition.");
            await Task.Delay(50);
        }
    }
}
