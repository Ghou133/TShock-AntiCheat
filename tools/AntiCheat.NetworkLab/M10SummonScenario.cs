using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Owned synthetic TCP into the real native capacity/commit guard; native client producer and GUI remain separate evidence.</summary>
internal static class M10SummonScenario
{
    private const string Rule = "C3.NativeSlimeSummonBudget";
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        string directory = Path.Combine(host.ReportDirectory, "m10-summon"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var frames = new List<object>(12); var outcomes = new List<object>(12);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-target");
            await host.FixtureSnapshot();
            var actor = await Actor("M10SummonActor"); var peer = await Actor("M10SummonPeer");
            await host.ConsoleCommand("qa_m10_summon " + actor.Name + " " + peer.Name);
            var ready = await WaitCapacity(actor, 1, "initial-native-capacity");
            Check(!ActorState(ready, actor).GetProperty("bypass").GetBoolean() &&
                !ActorState(ready, actor).GetProperty("sscBypass").GetBoolean(), "ordinary-SSC-account");
            var secondReady = await WaitCapacity(peer, 1, "second-account-native-capacity");
            uint peerKey = LabClient.ProjectileKey(peer.Slot, 410, 9);
            byte[] peerFrame = LabClient.Packet(27, writer => peer.WriteProjectileDamage(writer, peerKey, 266, 8));
            int actorPeerStart = actor.ProjectileClaims.Count;
            frames.Add(new { label = "second-account-first-slime", source = "synthetic-loopback-tcp", key = peerKey, hex = Convert.ToHexString(peerFrame) });
            await peer.SendBatch(peerFrame); await peer.PingAsync(); await actor.Drain(TimeSpan.FromMilliseconds(160));
            var secondAfter = await Snapshot("second-account-first-slime-after");
            var peerDecision = ActorState(secondAfter, peer).GetProperty("latestDecision");
            Check(peerDecision.GetProperty("sequence").GetInt64() == 1 && peerDecision.GetProperty("key").GetUInt32() == peerKey &&
                peerDecision.GetProperty("action").GetString() == "Pass" && peerDecision.GetProperty("verdict").GetString() == "Pass" &&
                peerDecision.GetProperty("passes").GetInt64() == 1 && peerDecision.GetProperty("unknowns").GetInt64() == 0 &&
                SameSession(peerDecision.GetProperty("session"), ActorState(secondAfter, peer).GetProperty("nativeCapacitySession")),
                "second-account-initial4-does-not-poison-authenticated-first-pass");
            Check(ActiveKeys(secondAfter, peer).Contains(peerKey) && actor.ProjectileClaims.Skip(actorPeerStart).Any(claim => claim.Key == peerKey) &&
                secondAfter.GetProperty("nativeCommits").GetInt64() == secondReady.GetProperty("nativeCommits").GetInt64() + 1,
                "second-account-first-pass-native-commit-and-real-peer-output");
            uint first = LabClient.ProjectileKey(actor.Slot, 410, 9);
            uint second = LabClient.ProjectileKey(actor.Slot, 411, 9);
            uint third = LabClient.ProjectileKey(actor.Slot, 412, 9);
            uint extra = LabClient.ProjectileKey(actor.Slot, 413, 9);
            uint replacement = LabClient.ProjectileKey(actor.Slot, 414, 9);
            uint afterDecline = LabClient.ProjectileKey(actor.Slot, 415, 9);
            await Exchange("ordinary-first-slime", first, permitted: true);
            await actor.Send(50, writer => { writer.Write(actor.Slot); writer.Write((ushort)64); writer.Write((ushort)0); });
            await WaitCapacity(actor, 1, "native-slime-buff-recalculation");
            await Exchange("pending-quickbuff-domain-second-before50", second, permitted: true);
            // This is the later50, after the second27: the native client QuickBuff producer is tested separately.
            await actor.Send(50, writer => { writer.Write(actor.Slot); writer.Write((ushort)64); writer.Write((ushort)110); writer.Write((ushort)0); });
            await WaitCapacity(actor, 2, "native-summoning-buff-capacity-increase");
            await Exchange("remaining-bewitched-domain-third", third, permitted: true);
            await Exchange("first-extra-above-native-pending-effect-domain", extra, permitted: false);
            var firstState = await Snapshot("before-normal-replacement");
            var position = ActorState(firstState, actor);
            byte[] retire = Retire(first, position.GetProperty("x").GetSingle(), position.GetProperty("y").GetSingle());
            byte[] create = LabClient.Packet(27, writer => actor.WriteProjectileDamage(writer, replacement, 266, 8));
            int replacementStart = peer.ProjectileClaims.Count;
            frames.Add(new { label = "native-order29-before27-replacement", source = "synthetic-loopback-tcp-audited-native-order",
                retire = Convert.ToHexString(retire), create = Convert.ToHexString(create) });
            await actor.SendBatch(retire, create); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(160));
            var replaced = await Snapshot("after-normal-replacement");
            Check(peer.ProjectileClaims.Skip(replacementStart).Any(claim => claim.Key == replacement && claim.Type == 266), "normal-replacement-has-real-peer27");
            Check(ActiveKeys(replaced, actor).Contains(replacement), "normal-replacement-has-native-entity");
            Check(ActorState(replaced, actor).GetProperty("LastRaw").GetProperty("handled").GetBoolean() == false,
                "replacement27-not-cancelled-even-if-old29-core-cancelled");
            var replacementDecision = ActorState(replaced, actor).GetProperty("latestDecision");
            Check(replacementDecision.GetProperty("key").GetUInt32() == replacement && replacementDecision.GetProperty("action").GetString() == "Pass" &&
                replacementDecision.GetProperty("verdict").GetString() == "Pass" &&
                replacementDecision.GetProperty("sequence").GetInt64() == ActorState(firstState, actor).GetProperty("latestDecision").GetProperty("sequence").GetInt64() + 1,
                "replacement-exact-current-decision-is-pass");
            outcomes.Add(new { label = "native-order29-before27-replacement", oldRemainsActive = ActiveKeys(replaced, actor).Contains(first),
                replacementActive = true, uncertaintyContract = "Raw29 is not claimed to prove Kill. A retained old entity is excluded from the resource lower bound." });

            await actor.Send(50, writer => { writer.Write(actor.Slot); writer.Write((ushort)64); writer.Write((ushort)0); });
            var decreased = await WaitCapacity(actor, 1, "native-capacity-decrease");
            Check(ActorState(decreased, actor).GetProperty("upperBound").GetInt32() == 3,
                "decline-retains-conservative-session-ceiling-without-deleting-old-minions");
            await Exchange("first-extra-above-conservative-ceiling", afterDecline, permitted: false);
            await actor.PingAsync(); await peer.PingAsync();
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ")),
                "resource-guard-never-creates-cheat-incident");
            foreach (string verdict in new[] { "Pass", "ResourceAbuse" })
                Check(host.ConsoleLines().Skip(logStart).Any(line => line.Contains("rule=" + Rule + " ") &&
                    line.Contains("slot=" + actor.Slot + " ") && line.Contains("verdict=" + verdict)),
                    "first-deduplicated-product-" + verdict + "-log-independent-witness");
            status = "passed";

            async Task Exchange(string label, uint key, bool permitted)
            {
                var before = await Snapshot(label + "-before"); int peerStart = peer.ProjectileClaims.Count;
                long commits = before.GetProperty("nativeCommits").GetInt64();
                var previous = ActorState(before, actor).GetProperty("latestDecision");
                long previousSequence = previous.ValueKind == JsonValueKind.Null ? 0 : previous.GetProperty("sequence").GetInt64();
                long previousPasses = previous.ValueKind == JsonValueKind.Null ? 0 : previous.GetProperty("passes").GetInt64();
                long previousBlocks = previous.ValueKind == JsonValueKind.Null ? 0 : previous.GetProperty("blocks").GetInt64();
                long previousUnknowns = previous.ValueKind == JsonValueKind.Null ? 0 : previous.GetProperty("unknowns").GetInt64();
                byte[] frame = LabClient.Packet(27, writer => actor.WriteProjectileDamage(writer, key, 266, 8));
                frames.Add(new { label, source = "synthetic-loopback-tcp", key, hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(160));
                var after = await Snapshot(label + "-after");
                var raw = ActorState(after, actor).GetProperty("LastRaw");
                bool peerSaw = peer.ProjectileClaims.Skip(peerStart).Any(claim => claim.Key == key && claim.Type == 266);
                bool active = ActiveKeys(after, actor).Contains(key);
                long delta = after.GetProperty("nativeCommits").GetInt64() - commits;
                Check(raw.GetProperty("key").GetUInt32() == key && raw.GetProperty("packet").GetInt32() == 27 &&
                    raw.GetProperty("handled").GetBoolean() == !permitted, label + "-exact-raw-cancellation");
                Check(peerSaw == permitted && active == permitted && delta == (permitted ? 1 : 0), label + "-native-commit-and-real-peer-output");
                var decision = ActorState(after, actor).GetProperty("latestDecision");
                Check(decision.GetProperty("sequence").GetInt64() == previousSequence + 1 && decision.GetProperty("key").GetUInt32() == key &&
                    decision.GetProperty("action").GetString() == (permitted ? "Pass" : "Block") &&
                    decision.GetProperty("verdict").GetString() == (permitted ? "Pass" : "ResourceAbuse") &&
                    SameSession(decision.GetProperty("session"), ActorState(after, actor).GetProperty("nativeCapacitySession")) &&
                    decision.GetProperty("passes").GetInt64() == previousPasses + (permitted ? 1 : 0) &&
                    decision.GetProperty("blocks").GetInt64() == previousBlocks + (permitted ? 0 : 1) &&
                    decision.GetProperty("unknowns").GetInt64() == previousUnknowns, label + "-exact-current-product-decision");
                outcomes.Add(new { label, permitted, key, peerSaw, active, nativeCommitDelta = delta, decision });
                Healthy(actor);
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, rule = Rule, version = "1.0.0",
                actualLoopbackTcp = true, actualClientGui = false, scaffoldCapacityAssignments = false,
                accountBanExpected = false, scope = "ordinary Slime266 resource ceiling; sentry/fractional/multipart incoming outside this rule",
                outcomes, clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("ANTICHEAT_") || line.Contains("qa_m10_summon")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m10-summon:" + label);
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
                client.Name + "-still-playable-no-account-ban");
        }
        async Task<JsonElement> WaitCapacity(LabClient actor, int expected, string label)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var state = await Snapshot(label);
                var subject = ActorState(state, actor);
                if (state.GetProperty("guardPresent").GetBoolean() && state.GetProperty("healthy").ValueKind == JsonValueKind.True &&
                    state.GetProperty("nativeCalculations").GetInt64() > 0 && subject.GetProperty("nativeCapacityReady").GetBoolean() &&
                    subject.GetProperty("nativeCapacity").GetInt32() == expected) return state;
                await Task.Delay(80);
            }
            throw new TimeoutException("Real native summon capacity unavailable: " + label);
        }
        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m10_summon_state");
            string path = Path.Combine(host.ReportDirectory, "m10-summon-state-latest.json"); var timer = Stopwatch.StartNew();
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
            throw new TimeoutException("Fresh summon witness state missing: " + label);
        }
    }
    private static JsonElement ActorState(JsonElement state, LabClient client) => state.GetProperty("actors").EnumerateArray()
        .Single(actor => actor.GetProperty("slot").GetInt32() == client.Slot);
    private static HashSet<uint> ActiveKeys(JsonElement state, LabClient client) => ActorState(state, client).GetProperty("activeMinions")
        .EnumerateArray().Select(entity => entity.GetProperty("key").GetUInt32()).ToHashSet();
    private static bool SameSession(JsonElement left, JsonElement right) =>
        left.GetProperty("ServerRunId").GetGuid() == right.GetProperty("ServerRunId").GetGuid() &&
        left.GetProperty("WorldEpoch").GetInt64() == right.GetProperty("WorldEpoch").GetInt64() &&
        left.GetProperty("Slot").GetInt32() == right.GetProperty("Slot").GetInt32() &&
        left.GetProperty("Generation").GetInt64() == right.GetProperty("Generation").GetInt64();
    private static byte[] Retire(uint key, float x, float y) => LabClient.Packet(29, writer => { writer.Write(key); writer.Write(x); writer.Write(y); });
}
