using System.Text.Json;
using Microsoft.Data.Sqlite;

// Narrow live P1-C slice. The client sends ordinary protocol-326 packet28
// strikes to a native King Slime created by the existing TShock /spawnmob
// command. The candidate observes the native NPCLoot call and the bounded
// Main.item delta; this scenario never creates, removes, or edits an item.
// A reward observation is source context only, not creator proof or a sanction.
internal static class M18ImportantRewardScenario
{
    private const int NativeTargetType = 50; // NPCID.KingSlime
    private const int KingSlimeBossBag = 3318; // ItemID.KingSlimeBossBag for 1.4.5.8
    private const int StrikeDamage = 1000;
    private const int MaximumStrikeAttempts = 32;
    private const string SourceContract = "Terraria NPC.StrikeNPC -> NPCLoot -> Main.item delta; same client transaction only";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p1-c");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string status = "failed", failure = "";
        var observations = new List<object>();
        JsonElement? prepared = null, initial = null, modePreparation = null, rewardState = null, worldAfter = null;
        LabClient? observer = null, actor = null;
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            observer = await host.Connect("M18P1CRewardObserver");
            await observer.Join(); await observer.RegisterAndLogin();
            observer.PauseHeartbeat = true;
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            actor = await host.Connect("M18P1CRewardActor");
            await actor.Join(); await actor.RegisterAndLogin();
            actor.PauseHeartbeat = true;
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            // The supplied isolated seed is normal mode. The existing QA
            // scaffold records a one-time in-memory Expert-mode preparation
            // in this disposable server process so native boss-bag logic is
            // reachable. It does not create an item or write the world.
            string modeMarker = Path.Combine(run, "m18-world-mode-preparation.json");
            Check(!File.Exists(modeMarker), "world-mode-preparation-marker-is-fresh");
            await host.ConsoleCommand("qa_m18_expert_mode");
            await Until(() => File.Exists(modeMarker) && host.ConsoleLines().Any(line =>
                line.Contains("M18 Expert-mode preparation recorded", StringComparison.Ordinal)),
                "isolated-world-mode-preparation-recorded");
            modePreparation = JsonDocument.Parse(await File.ReadAllTextAsync(modeMarker)).RootElement.Clone();
            Check(modePreparation.Value.GetProperty("fixtureArtificial").GetBoolean(),
                "world-mode-preparation-is-explicit-setup");
            Check(!modePreparation.Value.GetProperty("worldFileMutation").GetBoolean() &&
                !modePreparation.Value.GetProperty("itemMutation").GetBoolean(),
                "world-mode-preparation-does-not-write-world-or-items");
            Check(modePreparation.Value.GetProperty("afterExpert").GetBoolean() &&
                !modePreparation.Value.GetProperty("afterMaster").GetBoolean(),
                "copied-server-enters-expert-mode-before-native-kill");
            observations.Add(new
            {
                kind = "isolated-world-mode-preparation",
                command = "qa_m18_expert_mode",
                marker = modePreparation,
                rewardBranchRequired = "Main.expertMode || Main.masterMode"
            });

            // The existing scaffold owns the disposable room. It is setup only;
            // all NPC and reward state below is observed through native paths.
            await host.ConsoleCommand("qa_prepare " + actor.Name);
            prepared = (await WaitSnapshot("prepared-room", snapshot =>
                snapshot.GetProperty("room").GetProperty("State").GetString() == "prepared")).Clone();
            Check(prepared.Value.GetProperty("worldItems").EnumerateArray()
                .All(item => item.GetProperty("type").GetInt32() != KingSlimeBossBag),
                "prepared-world-has-no-preexisting-king-slime-bag");
            Check(prepared.Value.GetProperty("npcs").EnumerateArray()
                .All(npc => npc.GetProperty("type").GetInt32() != NativeTargetType ||
                    !npc.GetProperty("active").GetBoolean()),
                "prepared-world-has-no-preexisting-active-king-slime");

            await host.ConsoleCommand("qa_capture 13 21 28");
            await Until(() => host.ConsoleLines().Any(line =>
                line.Contains("QA raw packet capture: 13,21,28", StringComparison.Ordinal)),
                "reward-slice-raw-capture-enabled");

            const string spawnGroup = "M18P1CSpawnOnly";
            Check(Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", spawnGroup) == 0,
                "fresh-native-spawn-permission-group");
            bool spawnGroupAssignmentAttempted = false;
            try
            {
                await host.ConsoleCommand("group add " + spawnGroup + " tshock.npc.spawnmob");
                await Until(() => Scalar(database,
                    "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", spawnGroup) == 1,
                    "native-spawn-group-created");
                await host.ConsoleCommand("group parent " + spawnGroup + " default");
                await Until(() => Scalar(database,
                    "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default' AND Commands='tshock.npc.spawnmob'", spawnGroup) == 1,
                    "native-spawn-group-parented");
                spawnGroupAssignmentAttempted = true;
                await host.ConsoleCommand("user group " + actor.Name + " " + spawnGroup);
                await Until(() => Scalar(database,
                    "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup=$group", actor.Name, spawnGroup) == 1,
                    "actor-temporarily-has-only-native-spawn-permission");
                await actor.Chat("/spawnmob " + NativeTargetType + " 1");
                await actor.PingAsync();
            }
            finally
            {
                if (spawnGroupAssignmentAttempted)
                {
                    await host.ConsoleCommand("user group " + actor.Name + " default");
                    await Until(() => Scalar(database,
                        "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                        "actor-native-spawn-permission-restored");
                }
            }

            initial = (await WaitSnapshot("native-king-slime-spawned", snapshot =>
                snapshot.GetProperty("npcs").EnumerateArray().Any(npc =>
                    npc.GetProperty("active").GetBoolean() &&
                    npc.GetProperty("life").GetInt32() > 0 &&
                    npc.GetProperty("generation").GetInt32() > 0 &&
                    npc.GetProperty("type").GetInt32() == NativeTargetType))).Clone();
            var targetJson = initial.Value.GetProperty("npcs").EnumerateArray()
                .Where(npc => npc.GetProperty("active").GetBoolean() &&
                    npc.GetProperty("life").GetInt32() > 0 &&
                    npc.GetProperty("generation").GetInt32() > 0 &&
                    npc.GetProperty("type").GetInt32() == NativeTargetType)
                .First();
            var target = new NpcTarget(
                    targetJson.GetProperty("index").GetInt32(),
                    targetJson.GetProperty("generation").GetInt32(),
                    targetJson.GetProperty("type").GetInt32(),
                    targetJson.GetProperty("life").GetInt32());
            Check(targetJson.GetProperty("netId").GetInt32() == NativeTargetType,
                "native-king-slime-net-id-is-target-type");
            Check(targetJson.GetProperty("boss").GetBoolean(),
                "native-king-slime-retains-boss-flag");
            Check(!targetJson.GetProperty("interactionSlots").EnumerateArray()
                .Any(slot => slot.GetInt32() == actor.Slot),
                "native-king-slime-starts-without-prestrike-player-interaction");
            Check(targetJson.GetProperty("spawnedFromStatue").GetBoolean() == false,
                "native-king-slime-is-not-statue-spawned");
            Check(target.Index is >= 0 and < 200 && target.Generation is > 0 and <= byte.MaxValue,
                "native-king-slime-target-slot-and-generation-bounds");
            Check(target.Type == NativeTargetType, "target-is-native-king-slime-not-fixture-npc");

            await host.ConsoleCommand("qa_m18_reward_trace " + target.Index);
            await Until(() => host.ConsoleLines().Any(line =>
                line.Contains("M18 native reward trace armed", StringComparison.Ordinal)),
                "native-reward-trace-armed");

            observations.Add(new
            {
                kind = "native-king-slime-preparation",
                command = "/spawnmob " + NativeTargetType + " 1",
                permission = "tshock.npc.spawnmob",
                permissionGroup = spawnGroup,
                restoredBeforeStrike = Scalar(database,
                    "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                target
            });

            int rawStrikeBefore = CountRawFrames(host.ReportDirectory, 28, actor.Slot);
            int peerWorldItemBefore = observer.WorldItemUpdates.Count;
            int actorRewardPacketBefore = actor.WorldItemUpdates.Count;
            JsonElement latest = initial.Value.Clone();
            var strikeResults = new List<object>();
            bool dead = false;
            for (int attempt = 1; attempt <= MaximumStrikeAttempts; attempt++)
            {
                var before = await host.FixtureSnapshot();
                var beforeTarget = ReadNpc(before, target.Index);
                if (!beforeTarget.Active || beforeTarget.Life <= 0)
                {
                    latest = before.Clone();
                    dead = true;
                    break;
                }

                int relayBefore = observer.NpcStrikes.Count(x => x.Target == target.Index);
                byte[] frame = LabClient.Packet(28, writer => WriteStrike(writer, target, StrikeDamage));
                await actor.SendBatch(frame);
                await actor.PingAsync();
                latest = (await WaitSnapshot("king-slime-strike-" + attempt, snapshot =>
                {
                    var current = ReadNpc(snapshot, target.Index);
                    return !current.Active || current.Life <= 0 || current.Life < beforeTarget.Life;
                })).Clone();
                var afterTarget = ReadNpc(latest, target.Index);
                if (attempt == 1)
                {
                    var afterJson = latest.GetProperty("npcs").EnumerateArray()
                        .Single(npc => npc.GetProperty("index").GetInt32() == target.Index);
                    Check(afterJson.GetProperty("interactionSlots").EnumerateArray()
                        .Any(slot => slot.GetInt32() == actor.Slot),
                        "packet28-native-path-records-actor-player-interaction");
                }
                strikeResults.Add(new
                {
                    attempt,
                    packet = 28,
                    frameHex = Convert.ToHexString(frame),
                    target,
                    before = beforeTarget,
                    after = afterTarget,
                    relays = observer.NpcStrikes.Skip(relayBefore)
                        .Where(x => x.Target == target.Index)
                        .Select(x => new { target = x.Target, generation = x.Generation, damage = x.Damage,
                            knockback = x.Knockback, direction = x.Direction }).ToArray(),
                    rawPacket28Delta = CountRawFrames(host.ReportDirectory, 28, actor.Slot) - rawStrikeBefore
                });
                if (!afterTarget.Active || afterTarget.Life <= 0)
                {
                    dead = true;
                    break;
                }
            }
            observations.Add(new
            {
                kind = "native-king-slime-client-strikes",
                sourceContract = SourceContract,
                wireDamage = StrikeDamage,
                strikeAttempts = strikeResults.Count,
                target,
                results = strikeResults,
                rawPacket28Delta = CountRawFrames(host.ReportDirectory, 28, actor.Slot) - rawStrikeBefore,
                targetDied = dead,
                sanction = "none; packet28 weapon/effect provenance and reward ownership remain incomplete"
            });
            Check(dead, "native-king-slime-dies-through-client-packet28");
            Check(Healthy(actor, database) && Healthy(observer, database),
                "native-reward-kill-keeps-both-accounts-healthy");

            string rewardMarker = Path.Combine(run, "m18-reward-state-" + actor.Slot + ".json");
            Check(!File.Exists(rewardMarker), "reward-state-marker-is-fresh-before-read-only-capture");
            await host.ConsoleCommand("qa_m18_reward_state " + actor.Name);
            await Until(() => File.Exists(rewardMarker) && host.ConsoleLines().Any(line =>
                line.Contains("M18 reward witness state written", StringComparison.Ordinal)),
                "read-only-native-reward-state-marker");
            rewardState = JsonDocument.Parse(await File.ReadAllTextAsync(rewardMarker)).RootElement.Clone();
            Check(!rewardState.Value.GetProperty("fixtureArtificial").GetBoolean(),
                "reward-state-is-live-read-only-observation");
            Check(rewardState.Value.GetProperty("readOnly").GetBoolean(),
                "reward-state-bridge-is-read-only");
            Check(rewardState.Value.GetProperty("expertMode").GetBoolean() ||
                rewardState.Value.GetProperty("masterMode").GetBoolean(),
                "native-seed-is-expert-or-master-for-boss-bag-reward");

            var nativeTrace = GetProperty(rewardState.Value, "nativeTrace");
            Check(nativeTrace.ValueKind == JsonValueKind.Array,
                "native-reward-trace-state-is-bounded-array");
            Check(nativeTrace.GetArrayLength() > 0,
                "native-reward-trace-records-native-event");

            var reward = rewardState.Value.GetProperty("reward");
            Check(reward.ValueKind == JsonValueKind.Object, "same-strike-native-reward-observation-present");
            long actorAccount = Scalar(database, "SELECT ID FROM Users WHERE Username=$name", actor.Name);
            Check(GetProperty(reward, "AccountId").GetInt64() == actorAccount,
                "reward-observation-account-is-authenticated-actor");
            Check(GetProperty(reward, "NpcSlot").GetInt32() == target.Index &&
                GetProperty(reward, "NpcGeneration").GetInt32() == target.Generation &&
                GetProperty(reward, "NpcType").GetInt32() == NativeTargetType,
                "reward-observation-keeps-native-target-slot-generation-type");
            Check(GetProperty(reward, "NativeLootMethodObserved").GetBoolean(),
                "reward-observation-requires-native-loot-hook");
            Check(GetProperty(reward, "SourceContextKnown").GetBoolean(),
                "reward-observation-has-bounded-source-context");
            Check(!GetProperty(reward, "SourceAttributionComplete").GetBoolean(),
                "reward-observation-does-not-claim-creator-attribution");
            var rewardItems = GetProperty(reward, "Items").EnumerateArray().Select(item => new
            {
                index = GetProperty(item, "ItemIndex").GetInt32(),
                id = GetProperty(item, "ItemId").GetInt32(),
                name = GetProperty(item, "ItemName").GetString() ?? "",
                stack = GetProperty(item, "Stack").GetInt32(),
                maxStack = GetProperty(item, "MaxStack").GetInt32(),
                x = GetProperty(item, "X").GetSingle(),
                y = GetProperty(item, "Y").GetSingle()
            }).ToArray();
            Check(rewardItems.Length is > 0 and <= 16, "reward-item-list-is-nonempty-and-bounded");
            var bag = rewardItems.FirstOrDefault(item => item.id == KingSlimeBossBag &&
                item.name == "KingSlimeBossBag" && item.stack > 0);
            Check(bag is not null, "king-slime-boss-bag-is-captured-in-native-instanced-item");
            Check(bag!.maxStack >= bag.stack, "captured-boss-bag-stack-respects-native-max-stack");

            await actor.PingAsync();
            var actorPacket90 = actor.WorldItemUpdates.Skip(actorRewardPacketBefore)
                .Where(x => x.Packet == 90 && x.Type == KingSlimeBossBag && x.Stack > 0)
                .ToArray();
            Check(actorPacket90.Length > 0,
                "interacting-actor-receives-native-boss-bag-packet90");

            worldAfter = (await host.FixtureSnapshot()).Clone();
            bool serverWorldBag = TryFindWorldItem(worldAfter.Value, KingSlimeBossBag, out var worldBag);
            Check(!serverWorldBag,
                "native-boss-bag-is-transient-instanced-not-persistent-world-item");
            await observer.PingAsync();
            var peerPacket21 = observer.WorldItemUpdates.Skip(peerWorldItemBefore)
                .Where(x => x.Packet == 21 && x.Type == KingSlimeBossBag && x.Stack > 0)
                .ToArray();
            Check(peerPacket21.Length == 0,
                "independent-peer-does-not-see-per-client-boss-bag-packet21");
            observations.Add(new
            {
                kind = "native-loot-to-important-item-reward-witness",
                sourceContract = SourceContract,
                target,
                reward = reward.Clone(),
                rewardItems,
                actorPacket90 = actorPacket90.Select(x => new { packet = x.Packet, item = x.Item, stack = x.Stack, type = x.Type }).ToArray(),
                serverWorldItemPresent = serverWorldBag,
                worldItem = serverWorldBag ? worldBag : (JsonElement?)null,
                peerPacket21 = peerPacket21.Select(x => new { packet = x.Packet, item = x.Item, stack = x.Stack, type = x.Type }).ToArray(),
                boundary = GetProperty(reward, "Boundary").GetString(),
                sourceAttributionComplete = false,
                creatorProof = false,
                sanction = "none"
            });
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "native-reward-observation-writes-no-permanent-ban");

            await WriteEvidence(report, run, prepared, initial, modePreparation, rewardState, worldAfter,
                observations, "passed", failure: "");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteEvidence(report, run, prepared, initial, modePreparation, rewardState, worldAfter,
                observations, "failed", failure);
            throw;
        }
        finally
        {
            await CopyRelevantEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p1-c-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string label) => host.Assert(condition, "m18-p1-c:" + label);

        async Task<JsonElement> WaitSnapshot(string label, Func<JsonElement, bool> predicate)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            JsonElement last = default;
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var snapshot = await host.FixtureSnapshot();
                last = snapshot;
                if (predicate(snapshot)) return snapshot;
                await Task.Delay(100);
            }
            if (last.ValueKind != JsonValueKind.Undefined)
                await File.WriteAllTextAsync(Path.Combine(report, label + "-timeout.json"),
                    JsonSerializer.Serialize(last, Json));
            throw new TimeoutException("M18P1C fixture condition timed out: " + label);
        }
    }

    private static JsonElement GetProperty(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property)) return property;
        foreach (var candidate in value.EnumerateObject())
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) return candidate.Value;
        throw new KeyNotFoundException("JSON property missing: " + name);
    }

    private static bool TryFindWorldItem(JsonElement snapshot, int type, out JsonElement item)
    {
        foreach (var candidate in snapshot.GetProperty("worldItems").EnumerateArray())
        {
            if (candidate.GetProperty("active").GetBoolean() &&
                candidate.GetProperty("type").GetInt32() == type &&
                candidate.GetProperty("stack").GetInt32() > 0)
            {
                item = candidate.Clone();
                return true;
            }
        }
        item = default;
        return false;
    }

    private static NpcState ReadNpc(JsonElement snapshot, int index)
    {
        var npc = snapshot.GetProperty("npcs").EnumerateArray()
            .Single(x => x.GetProperty("index").GetInt32() == index);
        return new NpcState(index, npc.GetProperty("active").GetBoolean(),
            npc.GetProperty("life").GetInt32(), npc.GetProperty("generation").GetInt32(),
            npc.GetProperty("type").GetInt32());
    }

    private static bool Healthy(LabClient client, string database)
        => !client.Closed && client.DisconnectReason is null &&
            Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0;

    private static byte[] StrikeFrame(NpcTarget target, int damage)
        => LabClient.Packet(28, writer => WriteStrike(writer, target, damage));

    private static void WriteStrike(BinaryWriter writer, NpcTarget target, int damage)
    {
        writer.Write(checked((byte)target.Index));
        writer.Write(checked((byte)target.Generation));
        writer.Write(checked((short)damage));
        writer.Write(0f);
        writer.Write((byte)1); // encoded direction: native direction 0
        writer.Write((byte)1); // critical flag
    }

    private static int CountRawFrames(string report, byte packetId, byte playerIndex)
    {
        int count = 0;
        foreach (string line in Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines))
        {
            if (!line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            var payload = document.RootElement.GetProperty("payload");
            if (payload.GetProperty("packetId").GetByte() == packetId &&
                payload.GetProperty("playerIndex").GetByte() == playerIndex) count++;
        }
        return count;
    }

    private static long Scalar(string database, string sql, params string?[] values)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$name", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$name", values.ElementAtOrDefault(0));
        if (sql.Contains("$group", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$group", values.ElementAtOrDefault(1));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task Until(Func<bool> predicate, string label)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }

    private static async Task WriteEvidence(string report, string run, JsonElement? prepared,
        JsonElement? initial, JsonElement? modePreparation, JsonElement? rewardState, JsonElement? worldAfter,
        IReadOnlyList<object> observations, string status, string failure)
    {
        await File.WriteAllTextAsync(Path.Combine(report, "m18-p1-c-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status,
                failure,
                runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / client packet28",
                sourceContract = SourceContract,
                prepared,
                initial,
                modePreparation,
                rewardState,
                worldAfter,
                observations,
                candidateBoundary = "Native same-transaction NPCLoot plus bounded Main.item delta is source context only; creator, weapon/effect, pickup ownership, GUI and formal qualification remain unproven.",
                sanction = "none"
            }, Json));
    }

    private static async Task CopyRelevantEvents(string sourceRoot, string report)
    {
        var files = Directory.EnumerateFiles(sourceRoot, "gameplay-events-*.jsonl");
        var lines = files.SelectMany(File.ReadLines)
            .Where(line => line.Contains("m18-important-reward-state", StringComparison.Ordinal) ||
                line.Contains("qa_prepare", StringComparison.Ordinal) ||
                line.Contains("qa-status", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"packetId\":13", StringComparison.Ordinal) ||
                line.Contains("\"packetId\":28", StringComparison.Ordinal) ||
                line.Contains("\"packetId\":21", StringComparison.Ordinal))
            .Take(4096)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(report, "m18-p1-c-events.jsonl"), lines);
    }

    private sealed record NpcTarget(int Index, int Generation, int Type, int InitialLife);
    private sealed record NpcState(int Index, bool Active, int Life, int Generation, int Type);
}
