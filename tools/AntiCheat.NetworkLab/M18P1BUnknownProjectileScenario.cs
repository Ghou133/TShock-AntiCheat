using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Narrow P1-B negative slice. A normal non-summon projectile type is sent
// through the same native packet27 transport after the summon/sentry guard is
// prepared. The candidate must leave this unknown side branch playable and
// must not count it as a minion resource or issue a permanent sanction.
internal static class M18P1BUnknownProjectileScenario
{
    private const short NonSummonProjectileType = 1; // native WoodenArrowFriendly
    private const short Damage = 8;
    private const string Rule = "C3.NativeSlimeSummonBudget";

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m18-p1-b-unknown-projectile");
        Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(2);
        var frames = new List<object>(2);
        string status = "failed", failure = "";
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")),
                "owned-locked-target");
            var actor = await Connect("M18P1BUnknownActor");
            var peer = await Connect("M18P1BUnknownPeer");
            await host.ConsoleCommand("qa_m10_summon " + actor.Name + " " + peer.Name);
            var before = await Snapshot("before-unknown-projectile");
            var subjectBefore = Subject(before, actor);
            Check(subjectBefore.GetProperty("IsLoggedIn").GetBoolean() &&
                subjectBefore.GetProperty("HasSentInventory").GetBoolean() &&
                !subjectBefore.GetProperty("bypass").GetBoolean() &&
                !subjectBefore.GetProperty("sscBypass").GetBoolean(),
                "ordinary-authenticated-SSC-actor-without-bypass");
            Check(before.GetProperty("healthy").ValueKind == JsonValueKind.True &&
                before.GetProperty("guardPresent").GetBoolean() &&
                subjectBefore.GetProperty("nativeCapacityReady").GetBoolean(),
                "summon-guard-ready-before-unknown-projectile");

            uint key = LabClient.ProjectileKey(actor.Slot, 730, 11);
            long commitsBefore = before.GetProperty("nativeCommits").GetInt64();
            int peerClaimsBefore = peer.ProjectileClaims.Count;
            byte[] frame = LabClient.Packet(27, writer => actor.WriteProjectileDamage(writer, key,
                NonSummonProjectileType, Damage));
            frames.Add(new
            {
                label = "unknown-non-summon-projectile",
                source = "synthetic-loopback-tcp",
                packet = 27,
                key,
                projectileType = NonSummonProjectileType,
                damage = Damage,
                frameHex = Convert.ToHexString(frame)
            });
            await actor.SendBatch(frame);
            await actor.PingAsync();
            await peer.PingAsync();
            var after = await Snapshot("after-unknown-projectile");
            var subjectAfter = Subject(after, actor);
            var raw = subjectAfter.GetProperty("LastRaw");
            Check(raw.GetProperty("packet").GetInt32() == 27 &&
                raw.GetProperty("key").GetUInt32() == key &&
                raw.GetProperty("type").GetInt32() == NonSummonProjectileType &&
                !raw.GetProperty("handled").GetBoolean(),
                "unknown-projectile-reaches-native-path-without-cancellation");
            Check(peer.ProjectileClaims.Skip(peerClaimsBefore).Any(claim =>
                    claim.Key == key && claim.Type == NonSummonProjectileType && claim.Damage == Damage),
                "unknown-projectile-has-real-native-peer-output");
            Check(after.GetProperty("nativeCommits").GetInt64() == commitsBefore,
                "unknown-projectile-does-not-increment-summon-commit-count");
            Check(subjectAfter.GetProperty("latestDecision").ValueKind == JsonValueKind.Null,
                "unknown-projectile-does-not-create-summon-decision");
            Check(!subjectAfter.GetProperty("activeMinions").EnumerateArray().Any(entity =>
                    entity.GetProperty("key").GetUInt32() == key),
                "unknown-projectile-is-not-counted-as-active-minion");
            Check(Healthy(actor) && Healthy(peer), "unknown-projectile-keeps-both-accounts-playable");
            Check(!host.ConsoleLines().Skip(logStart).Any(line =>
                    line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)),
                "unknown-projectile-never-creates-cheat-incident");
            await Write("outcome.json", new
            {
                label = "unknown-non-summon-projectile",
                packet = 27,
                key,
                projectileType = NonSummonProjectileType,
                raw,
                peerOutput = peer.ProjectileClaims.Skip(peerClaimsBefore)
                    .Select(claim => new { key = claim.Key, type = claim.Type, damage = claim.Damage }).ToArray(),
                nativeCommitDelta = after.GetProperty("nativeCommits").GetInt64() - commitsBefore,
                latestDecision = subjectAfter.GetProperty("latestDecision"),
                activeMinionKeys = subjectAfter.GetProperty("activeMinions").EnumerateArray()
                    .Select(entity => entity.GetProperty("key").GetUInt32()).ToArray(),
                boundary = "non-summon/child-projectile classification remains outside the ordinary Slime266 and FrostHydra308 resource contracts",
                sanction = "none"
            });
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.ToString();
            throw;
        }
        finally
        {
            await Write("summary.json", new
            {
                status,
                failure,
                rule = Rule,
                version = "1.0.0",
                actualLoopbackTcp = true,
                actualClientGui = false,
                accountBanExpected = false,
                scope = "unknown non-summon packet27 side branch; child projectiles and all other summon types remain unclaimed",
                clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart)
                    .Where(line => line.Contains("ANTICHEAT_", StringComparison.Ordinal) ||
                        line.Contains("qa_m10_summon", StringComparison.Ordinal)).ToArray()
            });
            foreach (var client in clients) await client.DisposeAsync();
        }

        async Task<LabClient> Connect(string name)
        {
            var client = await host.Connect(name);
            clients.Add(client);
            await client.Join();
            await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350,
                name + "-real-authentication-and-SSC");
            return client;
        }

        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m10_summon_state");
            string path = Path.Combine(host.ReportDirectory, "m10-summon-state-latest.json");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        if (data.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            var result = data.RootElement.Clone();
                            await Write(label + ".json", result);
                            return result;
                        }
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                }
                await Task.Delay(40);
            }
            throw new TimeoutException("Fresh unknown-projectile summon witness state missing: " + label);
        }

        Task Write(string name, object value) => File.WriteAllTextAsync(
            Path.Combine(directory, name), JsonSerializer.Serialize(value, json));

        bool Healthy(LabClient client)
        {
            using var database = new SqliteConnection("Data Source=" +
                Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            return client.Authenticated && !client.Closed && client.DisconnectReason is null &&
                Convert.ToInt64(command.ExecuteScalar()) == 0;
        }

        void Check(bool value, string label) => host.Assert(value, "m18-p1-b-unknown:" + label);
    }

    private static JsonElement Subject(JsonElement state, LabClient client) =>
        state.GetProperty("actors").EnumerateArray()
            .Single(actor => actor.GetProperty("slot").GetInt32() == client.Slot);
}
