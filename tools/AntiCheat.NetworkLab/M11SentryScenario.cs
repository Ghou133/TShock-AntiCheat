using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M11SentryScenario
{
    private const string Rule = "C3.NativeFrostHydraSentryBudget";
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m11-sentry"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(2); var frames = new List<object>(); var outcomes = new List<object>();
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-target");
            await host.FixtureSnapshot();
            var actor = await Actor("M11SentryActor"); var peer = await Actor("M11SentryPeer");
            await host.ConsoleCommand("qa_m10_summon " + actor.Name + " " + peer.Name);
            var initial = await WaitCapacity(actor, 1, "initial-capacity");
            Check(Subject(initial, actor).GetProperty("upperBound").GetInt32() == 2, "pending348-upper-bound-is-two-before50");
            uint Key(LabClient subject, int identity) => LabClient.ProjectileKey(subject.Slot, identity, 10);
            await Exchange(peer, actor, Key(peer, 610), true, "other-owner-first-hydra");
            uint current = Key(actor, 610);
            await Exchange(actor, peer, current, true, "ordinary-initial-hydra");
            for (int index = 611; index <= 613; index++)
            {
                uint next = Key(actor, index);
                await Exchange(actor, peer, next, true, "max1-create-before-retire-" + index);
                var transient = await Snapshot("max1-transient-" + index);
                Check(Keys(transient, actor).SetEquals(new[] { current, next }), "max1-real-two-body-transient-" + index);
                await Retire(actor, current, "native-order-retire-" + index);
                var retired = await Snapshot("after-retire-" + index);
                Check(Keys(retired, actor).SetEquals(new[] { next }), "actual-native-retirement-reclaims-only-old-" + index);
                Check(Keys(retired, peer).Contains(Key(peer, 610)), "other-player-unchanged-" + index);
                current = next;
            }
            await Exchange(actor, peer, Key(actor, 614), true, "possible-wartable-second-before50");
            await Exchange(actor, peer, Key(actor, 615), true, "possible-max2-replacement-transient-third");
            await Exchange(actor, peer, Key(actor, 616), false, "first-extra-beyond-ceiling-and-transient");
            uint conversionKey = Key(actor, 620);
            byte[] ordinary = LabClient.Packet(27, writer => actor.WriteProjectileDamage(writer, conversionKey, 1, 8));
            frames.Add(new { label = "same-key-type-change-original-nonsentry", source = "synthetic-loopback-tcp", key = conversionKey, hex = Convert.ToHexString(ordinary) });
            await actor.SendBatch(ordinary); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(160));
            var original = await Snapshot("before-same-key-type-change");
            Check(ProjectileType(original, actor, conversionKey) == 1, "same-key-type-change-original-native-arrow-exists");
            await Exchange(actor, peer, conversionKey, false, "same-key-nonsentry-to-hydra-cannot-bypass-bound");
            Check(ProjectileType(await Snapshot("after-blocked-type-change"), actor, conversionKey) == 1,
                "blocked-type-change-preserves-original-entity-type");
            await actor.Send(50, writer => { writer.Write(actor.Slot); writer.Write((ushort)348); writer.Write((ushort)0); });
            var increased = await WaitCapacity(actor, 2, "accepted-wartable-native-capacity");
            Check(Subject(increased, actor).GetProperty("upperBound").GetInt32() == 2, "accepted-buff-does-not-double-count-pending-allowance");
            await Exchange(actor, peer, Key(actor, 617), false, "still-bounded-after-capability-confirmation");
            await Retire(actor, current, "late-actual-retirement");
            await Exchange(actor, peer, conversionKey, true, "actual-retirement-allows-native-same-key-resource-addition");
            Check(ProjectileType(await Snapshot("after-allowed-type-change"), actor, conversionKey) == 308,
                "allowed-type-change-has-real-hydra-poststate");
            await actor.Send(50, writer => { writer.Write(actor.Slot); writer.Write((ushort)0); });
            var decreased = await WaitCapacity(actor, 1, "capacity-decrease");
            Check(Subject(decreased, actor).GetProperty("upperBound").GetInt32() == 2, "decrease-keeps-session-high-water");
            await Exchange(actor, peer, Key(actor, 619), false, "decline-keeps-resource-bound");
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ")), "resource-only-no-cheat-incident");
            status = "passed";

            async Task Exchange(LabClient sender, LabClient receiver, uint key, bool allowed, string label)
            {
                var before = await Snapshot(label + "-before"); var prior = Subject(before, sender).GetProperty("latestDecision");
                long sequence = prior.ValueKind == JsonValueKind.Null ? 0 : prior.GetProperty("sequence").GetInt64();
                long passes = prior.ValueKind == JsonValueKind.Null ? 0 : prior.GetProperty("passes").GetInt64();
                long blocks = prior.ValueKind == JsonValueKind.Null ? 0 : prior.GetProperty("blocks").GetInt64();
                long unknowns = prior.ValueKind == JsonValueKind.Null ? 0 : prior.GetProperty("unknowns").GetInt64();
                long commits = before.GetProperty("nativeCommits").GetInt64(); int peerStart = receiver.ProjectileClaims.Count;
                var preserved = Keys(before, sender);
                byte[] frame = LabClient.Packet(27, writer => sender.WriteProjectileDamage(writer, key, 308, 8));
                frames.Add(new { label, source = "synthetic-loopback-tcp", key, hex = Convert.ToHexString(frame) });
                await sender.SendBatch(frame); await sender.PingAsync(); await receiver.Drain(TimeSpan.FromMilliseconds(160));
                var after = await Snapshot(label + "-after"); var subject = Subject(after, sender); var raw = subject.GetProperty("LastRaw");
                var decision = subject.GetProperty("latestDecision");
                bool peerSaw = receiver.ProjectileClaims.Skip(peerStart).Any(claim => claim.Key == key && claim.Type == 308);
                Check(raw.GetProperty("packet").GetInt32() == 27 && raw.GetProperty("key").GetUInt32() == key &&
                    raw.GetProperty("handled").GetBoolean() == !allowed, label + "-exact-raw-cancellation");
                Check(peerSaw == allowed && Keys(after, sender).Contains(key) == allowed &&
                    after.GetProperty("nativeCommits").GetInt64() == commits + (allowed ? 1 : 0), label + "-actual-resource-commit-and-peer-output");
                Check(preserved.IsSubsetOf(Keys(after, sender)), label + "-existing-resources-preserved");
                Check(decision.GetProperty("sequence").GetInt64() == sequence + 1 && decision.GetProperty("key").GetUInt32() == key &&
                    decision.GetProperty("action").GetString() == (allowed ? "Pass" : "Block") &&
                    decision.GetProperty("verdict").GetString() == (allowed ? "Pass" : "ResourceAbuse") &&
                    decision.GetProperty("passes").GetInt64() == passes + (allowed ? 1 : 0) &&
                    decision.GetProperty("blocks").GetInt64() == blocks + (allowed ? 0 : 1) &&
                    decision.GetProperty("unknowns").GetInt64() == unknowns &&
                    SameSession(decision.GetProperty("session"), subject.GetProperty("nativeCapacitySession")), label + "-precise-current-effective-decision");
                outcomes.Add(new { label, allowed, key, peerSaw, decision }); Healthy(sender);
            }
            async Task Retire(LabClient sender, uint key, string label)
            {
                var position = Subject(await Snapshot(label + "-before"), sender);
                byte[] frame = LabClient.Packet(29, writer => { writer.Write(key); writer.Write(position.GetProperty("x").GetSingle()); writer.Write(position.GetProperty("y").GetSingle()); });
                frames.Add(new { label, source = "synthetic-loopback-tcp-native27-then29-order", key, hex = Convert.ToHexString(frame) });
                await sender.SendBatch(frame); await sender.PingAsync();
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, rule = Rule, version = "1.0.0", actualLoopbackTcp = true,
                actualClientGui = false, accountBanExpected = false, scaffoldCapacityAssignments = false,
                scope = "same-session native committed single-body FrostHydra308; conservative capacity + one spawn-before-retire transient",
                outcomes, clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("ANTICHEAT_") || line.Contains("qa_m11_sentry")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m11-sentry:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-ordinary-SSC-account"); return client;
        }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0,
                client.Name + "-playable-no-account-ban");
        }
        async Task<JsonElement> WaitCapacity(LabClient actor, int expected, string label)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var state = await Snapshot(label); var subject = Subject(state, actor);
                if (state.GetProperty("healthy").ValueKind == JsonValueKind.True && state.GetProperty("targetValidated").ValueKind == JsonValueKind.True &&
                    subject.GetProperty("nativeCapacityReady").GetBoolean() && subject.GetProperty("nativeCapacity").GetInt32() == expected)
                {
                    Check(!subject.GetProperty("bypass").GetBoolean() && !subject.GetProperty("sscBypass").GetBoolean(), label + "-no-bypass");
                    return state;
                }
                await Task.Delay(80);
            }
            throw new TimeoutException("Actual native sentry capacity missing: " + label);
        }
        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m11_sentry_state");
            string path = Path.Combine(host.ReportDirectory, "m11-sentry-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                    try
                    {
                        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        if (data.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { var result = data.RootElement.Clone(); await Write(label + ".json", result); return result; }
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                await Task.Delay(40);
            }
            throw new TimeoutException("Fresh sentry snapshot missing: " + label);
        }
    }
    private static JsonElement Subject(JsonElement state, LabClient client) => state.GetProperty("actors").EnumerateArray()
        .Single(actor => actor.GetProperty("slot").GetInt32() == client.Slot);
    private static HashSet<uint> Keys(JsonElement state, LabClient client) => Subject(state, client).GetProperty("activeSentries")
        .EnumerateArray().Where(entity => entity.GetProperty("type").GetInt32() == 308).Select(entity => entity.GetProperty("key").GetUInt32()).ToHashSet();
    private static int? ProjectileType(JsonElement state, LabClient client, uint key) => Subject(state, client).GetProperty("activeProjectiles")
        .EnumerateArray().Where(entity => entity.GetProperty("key").GetUInt32() == key).Select(entity => (int?)entity.GetProperty("type").GetInt32()).SingleOrDefault();
    private static bool SameSession(JsonElement left, JsonElement right) => left.GetProperty("ServerRunId").GetGuid() == right.GetProperty("ServerRunId").GetGuid() &&
        left.GetProperty("WorldEpoch").GetInt64() == right.GetProperty("WorldEpoch").GetInt64() &&
        left.GetProperty("Slot").GetInt32() == right.GetProperty("Slot").GetInt32() && left.GetProperty("Generation").GetInt64() == right.GetProperty("Generation").GetInt64();
}
