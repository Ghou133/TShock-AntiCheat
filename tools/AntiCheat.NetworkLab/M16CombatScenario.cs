using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Ordinary NativePluginSet server. Permission-scoped native command is preparation, combat frames
/// are synthetic TCP, and the optional read-only observer is separate from product decisions.</summary>
internal static class M16CombatScenario
{
    private const string Rule = "COMBAT16.MoonLordCoreImmunity";

    public static async Task RunAsync(M4ObservedReplayHarness host, string? nativeProducerDirectory = null)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-combat"); Directory.CreateDirectory(directory);
        string statePath = Path.Combine(host.RunDirectory, "tshock", "anticheat", "m16-combat-diagnostics", "m16-combat-state.json");
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(); var frames = new List<object>(); var results = new List<object>();
        int preparationRaces = 0;
        IReadOnlyList<M17NativeCombatWitness> nativeWitnesses = [];
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-runtime");
            if (nativeProducerDirectory is not null)
                nativeWitnesses = M17NativeCombatWitnessCorpus.Load(host, nativeProducerDirectory);
            var actor = await Actor("M16CombatActor"); var peer = await Actor("M16CombatPeer");
            var initial = await State("initial");
            Check(initial.GetProperty("plugins").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x)
                .SequenceEqual(new[] { "AntiCheat.Plugin.TShock.AntiCheatPlugin", "TShockAPI.TShock" }.OrderBy(x => x)),
                "actual-native-plugin-set-no-scaffold");
            Check(!initial.GetProperty("windowExpired").GetBoolean(), "bounded-observer-window-open");
            Check(!Npcs(initial).Any(n => Int(n, "type") == 398), "no-preexisting-core-in-owned-world");
            // The native command is AllowServer=false. Use its ordinary in-game path with one
            // explicitly scoped permission, then restore the peer's original default group.
            const string spawnGroup = "M16SpawnOnly";
            Check(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", peer.Name) == 1,
                "preparation-peer-starts-in-normal-default-group");
            Check(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                "combat-actor-starts-in-normal-default-group");
            var groupsBefore = new[] { GroupEvidence(actor.Name), GroupEvidence(peer.Name) };
            await host.ConsoleCommand("group add " + spawnGroup + " tshock.npc.spawnmob");
            await WaitDatabase(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", spawnGroup) == 1,
                "spawn-only-group-created");
            await host.ConsoleCommand("group parent " + spawnGroup + " default");
            await WaitDatabase(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default' AND Commands='tshock.npc.spawnmob'", spawnGroup) == 1,
                "exact-spawn-permission-over-default-without-bypass");
            await host.ConsoleCommand("user group " + peer.Name + " " + spawnGroup);
            await WaitDatabase(() => Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='M16SpawnOnly'", peer.Name) == 1,
                "preparation-peer-has-only-normal-spawn-permission");
            var preparationGroups = new[] { GroupEvidence(actor.Name), GroupEvidence(peer.Name) };
            await peer.Chat("/spawnmob 398 1"); await peer.PingAsync();
            JsonElement state;
            try { state = await WaitState(s => Npcs(s).Any(n => Int(n, "type") == 398), "real-native-command-spawned-core"); }
            finally
            {
                await host.ConsoleCommand("user group " + peer.Name + " default");
                await WaitDatabase(() => Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", peer.Name) == 1,
                    "preparation-permission-removed-before-combat");
                await peer.PingAsync();
            }
            Check(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                "combat-actor-remained-in-normal-default-group");
            JsonElement core = Npcs(state).Single(n => Int(n, "type") == 398);
            await File.WriteAllTextAsync(Path.Combine(directory, "native-preparation.json"), JsonSerializer.Serialize(new {
                kind = "ordinary-TShock-command-via-real-TCP82", command = "/spawnmob 398 1", permission = "tshock.npc.spawnmob",
                permissionGroup = spawnGroup, parent = "default", restoredGroupBeforeCombat = "default", core,
                groupsBefore, preparationGroups, groupsBeforeCombat = new[] { GroupEvidence(actor.Name), GroupEvidence(peer.Name) },
                excluded = "No scaffold, no bypass, no NPC field or health-context write; native NewNPC plus actual AI" }, json));
            await MoveNear(actor, core); await MoveNear(peer, core);
            state = await WaitState(s => Npcs(s).Any(n => Int(n, "type") == 398 && Float(n, "phase") == 0 && Bool(n, "dontTakeDamage")),
                "native-core-phase-zero-with-natural-parts", 10);
            core = Npcs(state).Single(n => Int(n, "type") == 398);
            int coreSlot = Int(core, "slot"), coreGeneration = Int(core, "generation");
            Check(Npcs(state).Count(n => Int(n, "type") is 396 or 397) == 3, "native-ai-created-three-parts");
            // The newly spawned phase0 parts briefly expose an opening animation before
            // closing. Require an observed closed->open native attack window instead.
            var legalWindow = new M16InitialLegalWindow(); var legalWait = Stopwatch.StartNew();
            bool initialLegalCompleted = false; int windowSnapshots = 0;
            while (!initialLegalCompleted && legalWait.Elapsed < TimeSpan.FromSeconds(40))
            {
                state = await State("initial-legal-window-" + ++windowSnapshots);
                var legalPart = legalWindow.Select(Npcs(state));
                if (legalPart.ValueKind != JsonValueKind.Object) { await Task.Delay(100); continue; }
                await MoveNear(actor, legalPart);
                initialLegalCompleted = await Hit(actor, peer, legalPart, 9, false, true,
                    "legal-native-part-first-before-shield-policy-" + legalWindow.Attempts, legalComparatorAttempt: true);
            }
            Check(initialLegalCompleted, "first-legal-control-completed-at-open-receipt-before-shield-policy");
            await MoveNear(actor, core);
            foreach (var witness in nativeWitnesses)
                await Hit(actor, peer, core, witness.Damage, witness.Critical, false,
                    witness.Label + "-current-core-closed", nativeWitness: witness);
            await Hit(actor, peer, core, 9, false, false, "current-core-shield-blocks-ordinary28");
            await Hit(actor, peer, core, 0, false, false, "current-core-shield-blocks-native-minimum-damage");
            await Hit(actor, peer, core, -1, false, false, "current-core-shield-blocks-negative-wire-minimum-damage");
            await Hit(peer, actor, core, 9, false, false, "second-authenticated-account-same-current-policy");
            await Hit(actor, peer, core, 9, false, false, "old-generation-keeps-native-noop", (coreGeneration + 255) & 255, stale: true);
            Healthy(actor); Healthy(peer);

            // Select a currently vulnerable native part without changing fields/health. A part can
            // close between this snapshot and actual receipt. Such preparation races are recorded
            // separately and never counted as a strict legal control or as a core-policy success.
            var progression = Stopwatch.StartNew(); int partHits = 0;
            while (progression.Elapsed < TimeSpan.FromSeconds(110))
            {
                state = await State("phase-progress-" + partHits);
                core = Npcs(state).Single(n => Int(n, "slot") == coreSlot && Int(n, "generation") == coreGeneration);
                if (Float(core, "phase") == 1 && !Bool(core, "dontTakeDamage")) break;
                var part = Npcs(state).FirstOrDefault(n => Int(n, "type") is 396 or 397 &&
                    !Bool(n, "dontTakeDamage") && Int(n, "life") > 1 && Float(n, "phase") != -2);
                if (part.ValueKind == JsonValueKind.Object)
                {
                    if (++partHits > 12) throw new InvalidOperationException("Native part progression exceeded bounded hit count.");
                    await MoveNear(actor, part);
                    await Hit(actor, peer, part, 19000, true, true, "native-vulnerable-part-progress-" + partHits, preparationAttempt: true);
                }
                else await Task.Delay(150);
            }
            state = await State("native-core-opened");
            core = Npcs(state).Single(n => Int(n, "slot") == coreSlot && Int(n, "generation") == coreGeneration);
            Check(Float(core, "phase") == 1 && !Bool(core, "dontTakeDamage"), "real-part-defeat-opens-same-core-without-fixture-state-write");
            Check(partHits > 0, "phase-opened-after-actual-native-part-hit-consumption");
            await MoveNear(actor, core); await MoveNear(peer, core);
            await Hit(actor, peer, core, 9, false, true, "ordinary-vulnerable-core-hit");
            foreach (var witness in nativeWitnesses)
                await Hit(actor, peer, core, witness.Damage, witness.Critical, true,
                    witness.Label + "-native-core-open-recovery", nativeWitness: witness);
            await Hit(actor, peer, core, 1005, false, true, "high-damage-not-a-proof");
            await Hit(actor, peer, core, 1005, true, true, "normal-critical-hit");
            await Hit(peer, actor, core, 19000, false, true, "second-account-high-damage-preserved-with-bouncer");
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ") && line.Contains("rule=" + Rule + " ")),
                "phase-policy-never-generates-cheat-incident");
            Check((await State("final")).GetProperty("dropped").GetInt64() == 0, "observer-has-no-dropped-request-evidence");
            status = "passed";

            async Task<bool> Hit(LabClient sender, LabClient observer, JsonElement target, short damage, bool critical,
                bool allowed, string label, int generationOverride = -1, bool stale = false, bool preparationAttempt = false, M17NativeCombatWitness? nativeWitness = null, bool legalComparatorAttempt = false)
            {
                int slot = Int(target, "slot"), generation = generationOverride < 0 ? Int(target, "generation") : generationOverride;
                var before = await State(label + "-before"); long sequence = LastSequence(before);
                await observer.Drain(TimeSpan.FromMilliseconds(40)); int peerStart = observer.NpcStrikes.Count;
                byte[] frame = LabClient.Packet(28, writer =>
                { writer.Write((byte)slot); writer.Write((byte)generation); writer.Write(damage); writer.Write(0f); writer.Write((byte)1); writer.Write(critical ? (byte)1 : (byte)0); });
                if (nativeWitness is not null)
                {
                    frame = (byte[])nativeWitness.Frame.Clone();
                    frame[3] = checked((byte)slot); frame[4] = checked((byte)generation);
                    Check(frame.AsSpan(5).SequenceEqual(nativeWitness.Frame.AsSpan(5)),
                        label + "-original-native-damage-knockback-direction-critical-bytes-preserved");
                }
                string body = Convert.ToHexString(frame.AsSpan(3));
                frames.Add(new { label, source = nativeWitness is null ? "synthetic-loopback-TCP28" : "captured-native-result-mapped-to-current-target-and-replayed-over-TCP",
                    sender = sender.Name, sender.Slot, hex = Convert.ToHexString(frame),
                    nativeWitness, evidenceLimit = nativeWitness is null ? null : "Native producer ran in isolated method fixture; this is a real TCP replay, not a live stock-client weapon action; only target slot/generation are mapped." });
                await sender.SendBatch(frame); await sender.PingAsync();
                var after = await WaitState(s => s.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("sequence").GetInt64() > sequence &&
                    Int(e, "sender") == sender.Slot && e.GetProperty("body").GetString() == body), label + "-real-receiver-completion");
                var entry = after.GetProperty("events").EnumerateArray().Last(e => e.GetProperty("sequence").GetInt64() > sequence &&
                    Int(e, "sender") == sender.Slot && e.GetProperty("body").GetString() == body);
                await observer.Drain(TimeSpan.FromMilliseconds(120));
                bool peerSaw = observer.NpcStrikes.Skip(peerStart).Any(s => s.Target == slot && s.Generation == generation && s.Damage == Math.Max(0, (int)damage));
                var pre = entry.GetProperty("before"); var post = entry.GetProperty("after");
                bool lifeDecreased = Int(post, "life") < Int(pre, "life");
                // The actual native checkDead for 396/397 sets phase=-2 and calls
                // PrepareForDeathAnimation, which resets life to lifeMax. That exact
                // successful part-defeat transition is not a monotonically falling-life hit.
                bool nativePartDefeat = Int(pre, "type") is 396 or 397 && Int(post, "type") == Int(pre, "type") &&
                    Float(pre, "phase") != -2 && Float(post, "phase") == -2 && Bool(post, "active") &&
                    Bool(post, "dontTakeDamage") && Bool(post, "justHit") &&
                    Int(post, "life") == Int(post, "lifeMax") && Int(entry, "nativeLoot") == 0;
                Check(Bool(entry, "returned") && Bool(entry, "sameEntity") && entry.GetProperty("exceptionType").ValueKind == JsonValueKind.Null,
                    label + "-same-object-real-receiver-return-no-exception");
                bool preparationRace = (preparationAttempt || legalComparatorAttempt) && Bool(pre, "dontTakeDamage");
                if (preparationAttempt || legalComparatorAttempt)
                {
                    Check(Int(target, "type") is 396 or 397 && !Bool(target, "dontTakeDamage") && Float(target, "phase") != -2 &&
                        Int(pre, "type") == Int(target, "type") && Int(pre, "slot") == slot && Int(pre, "generation") == generation && Int(pre, "objectIdentity") == Int(target, "objectIdentity"),
                        label + "-bounded-preparation-target-selected-open-and-same-native-part-at-receipt");
                    if (preparationRace) preparationRaces++;
                    await File.WriteAllTextAsync(Path.Combine(directory, label + "-preparation-attempt.json"), JsonSerializer.Serialize(new
                    {
                        label, preparationRace, legalComparatorAttempt,
                        countedAsStrictLegalControl = legalComparatorAttempt && !preparationRace, selectedSnapshot = target,
                        outcome = preparationRace ? "native-part-closed-between-selection-and-receipt" : "native-part-still-open-at-receipt",
                        actualNativeConsumption = lifeDecreased || nativePartDefeat, nativePartDefeat, peerSaw, entry,
                        scope = preparationRace || preparationAttempt ? "Preparation only; parts396/397 are outside core398 policy. Arrival in an immune phase is not labeled a legal hit." : "Strict legal comparator requires actual open receipt and all native consumption assertions; parts396/397 are outside core398 policy."
                    }, json));
                }
                if (allowed)
                {
                    if (!preparationAttempt && !preparationRace) Check(!Bool(pre, "dontTakeDamage"), label + "-actual-vulnerable-target-at-receipt");
                    Check((lifeDecreased || nativePartDefeat) && Int(entry, "nativeStrikes") == 1 && Int(entry, "nativeRelays") == 1 && peerSaw,
                        label + "-real-consumption-native-strike-and-peer-relay");
                    if (Int(pre, "type") == 398)
                        await Outcome(sender, "Pass", "native-core-vulnerable-phase-damage-amount-not-adjudicated");
                }
                else
                {
                    Check(Int(post, "life") == Int(pre, "life") && Bool(post, "justHit") == Bool(pre, "justHit") &&
                        Bool(post, "senderInteraction") == Bool(pre, "senderInteraction") &&
                        Int(entry, "nativeStrikes") == 0 && Int(entry, "nativeLoot") == 0 && Int(entry, "nativeRelays") == 0 && !peerSaw,
                        label + "-no-life-hit-credit-loot-or-relay-side-effect");
                    if (!stale)
                    {
                        Check(Bool(pre, "dontTakeDamage") && Int(pre, "type") == 398 && Int(pre, "generation") == generation && Int(pre, "objectIdentity") == Int(target, "objectIdentity"),
                            label + "-actual-supported-current-shield-premise");
                        await Outcome(sender, "UnsafeInput", "current-native-core-immune-phase-denies-client-strike");
                    }
                }
                bool strictLegal = allowed && !preparationAttempt && !preparationRace;
                Healthy(sender); Healthy(observer); results.Add(new { label, allowed, stale, preparationAttempt, legalComparatorAttempt, preparationRace,
                    countedAsStrictLegalControl = strictLegal, peerSaw, lifeDecreased, nativePartDefeat, entry });
                return strictLegal;
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(frames, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure,
                rule = Rule, source = "real-native-server-and-synthetic-loopback-TCP", ordinaryStockClient = "not-executed",
                nativePreparation = "TShock in-game spawnmob with temporary spawn-only permission restored before combat; native AI spawns parts; TCP28 targets parts selected as open; selection-to-receipt races recorded as preparation only",
                observer = "isolated-read-only; no health/context/phase writes; independent product decision logs required",
                firstBan = false, scope = "MoonLordCore398 current shield phase BLOCK; not full ordinary damage/lock-health model",
                cases = results.Count, preparationRaces, nativeProducerWitnesses = nativeWitnesses.Count, clients = clients.Select(c => c.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(l => l.Contains("rule=" + Rule + " ") || l.Contains("spawn", StringComparison.OrdinalIgnoreCase)).ToArray() }, json));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool condition, string label) => host.Assert(condition, "m16-combat:" + label);
        long Account(string name)
            => Scalar("SELECT ID FROM Users WHERE Username=$name", name);
        long Scalar(string query, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = query; cmd.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        async Task WaitDatabase(Func<bool> predicate, string label)
        {
            var timeout = Stopwatch.StartNew();
            while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(predicate(), label);
        }
        object GroupEvidence(string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT u.ID, u.UserGroup, g.Parent, g.Commands FROM Users u JOIN GroupList g ON g.GroupName=u.UserGroup WHERE u.Username=$name";
            cmd.Parameters.AddWithValue("$name", name); using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("No actual account/group evidence for " + name);
            return new { name, accountId = reader.GetInt64(0), group = reader.GetString(1),
                parent = reader.IsDBNull(2) ? null : reader.GetString(2), directPermissions = reader.GetString(3),
                source = "actual-TShock-SQLite-account-and-group" };
        }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name"; cmd.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(cmd.ExecuteScalar()) == 0,
                client.Name + "-authenticated-unbanned-playable");
        }
        async Task Outcome(LabClient client, string verdict, string reason)
        {
            long account = Account(client.Name); var timeout = Stopwatch.StartNew();
            bool Found() => host.ConsoleLines().Any(line => line.Contains("rule=" + Rule + " ") && line.Contains("accountId=" + account + " ") &&
                line.Contains("verdict=" + verdict + " ") && line.Contains("reason=" + reason + " "));
            while (!Found() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(30);
            Check(Found(), client.Name + "-actual-product-" + verdict + "-" + reason);
        }
        async Task<LabClient> Actor(string name)
        {
            var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350 && Account(name) > 0, name + "-real-authenticated-ssc"); return actor;
        }
        async Task MoveNear(LabClient actor, JsonElement npc)
        { await actor.MoveTo(Float(npc, "x"), Float(npc, "y") + 120); await actor.PingAsync(); }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m16_combat_state");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    if (File.Exists(statePath))
                    {
                        using var file = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var doc = JsonDocument.Parse(file);
                        if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            var snapshot = doc.RootElement.Clone();
                            await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), snapshot.GetRawText());
                            return snapshot;
                        }
                    }
                }
                catch (IOException) { } catch (JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("No new bounded native combat snapshot: " + label);
        }
        async Task<JsonElement> WaitState(Func<JsonElement, bool> predicate, string label, int seconds = 5)
        {
            var wait = Stopwatch.StartNew(); int attempt = 0;
            while (wait.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                var state = await State(label + "-" + ++attempt); if (predicate(state)) return state;
                await Task.Delay(100);
            }
            throw new TimeoutException("Native combat state did not reach: " + label);
        }
    }
    private static IEnumerable<JsonElement> Npcs(JsonElement state) => state.GetProperty("npc").EnumerateArray();
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static float Float(JsonElement value, string name) => value.GetProperty(name).GetSingle();
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static long LastSequence(JsonElement state) => state.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()).DefaultIfEmpty().Max();
}

/// <summary>Tool-only bounded native-window selector. A snapshot is preparation;
/// only Hit's actual receiver evidence can complete the strict legal comparator.</summary>
internal sealed class M16InitialLegalWindow
{
    private readonly Dictionary<int, (int Generation, int Object)> closed = new(3);
    public int Attempts { get; private set; }
    public const int MaximumAttempts = 3;
    public JsonElement Select(IEnumerable<JsonElement> snapshots)
    {
        foreach (var value in snapshots)
        {
            int type = value.GetProperty("type").GetInt32();
            if (type is not (396 or 397)) continue;
            int slot = value.GetProperty("slot").GetInt32();
            var identity = (value.GetProperty("generation").GetInt32(), value.GetProperty("objectIdentity").GetInt32());
            if (!value.GetProperty("active").GetBoolean() || value.GetProperty("life").GetInt32() <= 1 || value.GetProperty("phase").GetSingle() < 0)
            { closed.Remove(slot); continue; }
            if (value.GetProperty("dontTakeDamage").GetBoolean())
            {
                if (!closed.ContainsKey(slot) && closed.Count == 3) throw new InvalidOperationException("Initial legal window observed more than three owned parts.");
                closed[slot] = identity; continue;
            }
            if (value.GetProperty("phase").GetSingle() == 0 || !closed.TryGetValue(slot, out var previous) || previous != identity) continue;
            closed.Remove(slot);
            if (++Attempts > MaximumAttempts) throw new InvalidOperationException("Initial legal comparator exhausted three distinct native opening windows.");
            return value.Clone();
        }
        return default;
    }
}
