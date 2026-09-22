using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>One effective Golem-world PASS over TCP on a separately prepared native world.
/// World-file preparation is not client evidence; initial server packet7 verifies loaded facts.</summary>
internal static class M13GolemLegalScenario
{
    private const string Rule = "PG-NAT-108.GolemSummonWorld";
    private const string Permission = "tshock.npc.summonboss";

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m13-golem-legal"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var evidence = new List<object>(5);
        string status = "failed", failure = ""; int begin = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "runtime-m12-r2-1.4.5.8-326-verified");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                !File.Exists(Path.Combine(host.RunDirectory, "app", "ServerPlugins", "GameplayScaffold.dll")), "owned-world-native-plugin-set");
            string beforePermissions = Text("SELECT Commands FROM GroupList WHERE GroupName=$name", "default");
            string beforeParent = Text("SELECT Parent FROM GroupList WHERE GroupName=$name", "default");
            if (!beforePermissions.Split(',').Contains(Permission, StringComparer.Ordinal))
            {
                await host.ConsoleCommand("group addperm default " + Permission);
                await Until(() => Text("SELECT Commands FROM GroupList WHERE GroupName=$name", "default")
                    .Split(',').Contains(Permission, StringComparer.Ordinal), TimeSpan.FromSeconds(5));
            }
            string afterPermissions = Text("SELECT Commands FROM GroupList WHERE GroupName=$name", "default");
            Check(beforePermissions.Split(',').Append(Permission).ToHashSet(StringComparer.Ordinal)
                .SetEquals(afterPermissions.Split(',')) && beforeParent == Text("SELECT Parent FROM GroupList WHERE GroupName=$name", "default"),
                "only-normal-boss-interaction-permission-added");
            evidence.Add(new { kind = "existing-console-permission", beforePermissions, afterPermissions, beforeParent });

            var actor = await Actor("M13GolemLegal");
            var peer = await Actor("M13GolemPeer");
            var world = M11SolarTabletScenario.ReadWorld(actor.LastWorldInfo);
            Check(world.Ssc && world.HardMode && world.Plantera && !world.Golem, "native-initial-world7-has-both-open-gates-before-first-golem");
            evidence.Add(new { kind = "actual-initial-server-world7", actor.Name, world });

            // Match TShock's actual five-second post-SSC threat throttle. This bounded drain is
            // an explicit supported core gate, not retrying a race or altering rule evidence.
            // No failed summon is retried: exactly one legal request follows this gate.
            await actor.Drain(TimeSpan.FromMilliseconds(5200));
            await actor.PingAsync(); await peer.PingAsync();
            int mark = host.ConsoleLines().Length, npcMark = peer.NpcTypes.Count;
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            byte[] frame = LabClient.Packet(61, writer => { writer.Write((short)actor.Slot); writer.Write((short)245); });
            await actor.SendBatch(frame); await actor.PingAsync();
            string? trace = null;
            await Until(() => (trace = host.ConsoleLines().Skip(mark).FirstOrDefault(line =>
                line.Contains("ANTICHEAT_RULE_INPUT ", StringComparison.Ordinal) &&
                line.Contains("rule=" + Rule + " ", StringComparison.Ordinal) &&
                line.Contains("accountId=" + account + " ", StringComparison.Ordinal) &&
                line.Contains("verdict=Pass ", StringComparison.Ordinal) &&
                line.Contains("reason=native-golem-altar-world-gates-open ", StringComparison.Ordinal))) is not null,
                TimeSpan.FromSeconds(5));
            Check(trace!.Contains("prerequisites=True ", StringComparison.Ordinal) &&
                trace.Contains("action=Pass ", StringComparison.Ordinal) && trace.Contains("canceled=False ", StringComparison.Ordinal) &&
                trace.Contains("worldBaselineComplete=True", StringComparison.Ordinal) &&
                trace.Contains("currentAccountAndActorBound=True", StringComparison.Ordinal) &&
                trace.Contains("pluginContractComplete=True", StringComparison.Ordinal), "245-effective-pass-complete-current-contract-no-bypass-or-unknown");
            await Until(() => host.ConsoleLines().Skip(mark).Any(line =>
                line.Contains("M13GolemLegal summoned the Golem!", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
            Check(true, "tshock-native-boss-handler-reached-after-pass");
            await peer.PingAsync();
            evidence.Add(new { kind = "single-synthetic-loopback-61-request", actor.Name, account, frameHex = Convert.ToHexString(frame), trace,
                receivedGolemNpcSyncs = peer.NpcTypes.Skip(npcMark).Count(npc => npc.Type == 245),
                altarCreatedByFixture = false, nativeSpawnSuccessRequired = false });

            await actor.SendBatch(LabClient.Packet(120, writer => { writer.Write(actor.Slot); writer.Write((byte)7); }));
            await peer.WaitUntil(() => peer.Bubbles.Any(bubble => bubble.Player == actor.Slot && bubble.Emote == 7), TimeSpan.FromSeconds(5));
            Check(true, "following-normal-gameplay-still-reaches-independent-peer");
            foreach (var client in clients)
            {
                await client.PingAsync();
                Check(client.Authenticated && !client.Closed && client.DisconnectReason is null &&
                    Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + client.Name) == 0,
                    client.Name + "-connected-without-account-ban");
            }
            Check(!host.ConsoleLines().Skip(begin).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-incident-from-legal-world-request");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, evidence, syntheticLoopbackTcp = true, nativeInitialWorldPacketVerified = status == "passed",
                stockClientGui = false, toolOffGui = false, toolOnGui = false, realFrameReplay = false,
                actualNativeAltarProduction = "separate M13NaturalGolemTests; this TCP request is synthesized",
                worldPreparation = "owned copy; only audited hardMode and downedPlantBoss header booleans changed before server startup",
                nativeGolemSpawnClaimed = false, coreThrottlePreparationMilliseconds = 5200,
                pluginBypassUsed = false, scaffoldLoaded = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m13-golem-legal:" + label);
        object? Query(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = sql; query.Parameters.AddWithValue("$name", name);
            return query.ExecuteScalar();
        }
        long Scalar(string sql, string name) => Convert.ToInt64(Query(sql, name) ?? 0L);
        string Text(string sql, string name) => Convert.ToString(Query(sql, name)) ?? "";
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin(); await client.PingAsync();
            Check(client.Authenticated && client.SscSlots.Count >= 350 &&
                Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", name) == 1,
                name + "-ordinary-default-ssc-account");
            return client;
        }
        static async Task Until(Func<bool> predicate, TimeSpan limit)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate())
            {
                DevRunLifecycle.Check();
                if (timer.Elapsed >= limit) throw new TimeoutException("Golem legal acceptance condition did not complete.");
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
        }
    }
}
