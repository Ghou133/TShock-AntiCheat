using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Native-plugin-set TCP acceptance. The existing TShock hardmode/time/worldevent commands
/// prepare the disposable world; no scaffold, custom completion bit or item-origin certificate.</summary>
internal static class M11SolarTabletScenario
{
    private const string Rule = "PG-NAT-108.SolarTabletHardmode";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal sealed record WorldInput(int WorldId, bool HardMode, bool DayTime, bool Eclipse,
        bool Plantera, bool Golem, bool Ssc, string PacketHex);
    internal sealed record TimeInput(bool DayTime, int Time, short SunOffset, short MoonOffset, string PacketHex);

    internal static TimeInput ReadTime(byte[]? packet)
    {
        if (packet is not { Length: 10 } || packet[0] != 18 || packet[1] > 1)
            throw new InvalidDataException("Missing native time18 frame (day/time/sun/moon, no eclipse field).");
        return new(packet[1] == 1, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(2)),
            BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(6)), BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(8)),
            Convert.ToHexString(packet));
    }

    // Reads only the bounded prefix of the audited protocol326 native packet7. The thirteen
    // TreeTops bytes are fixed by target TreeTopsInfo.AreaId.Count; no search for flag-like bytes.
    internal static WorldInput ReadWorld(byte[]? packet)
    {
        if (packet is not { Length: >= 140 and <= 1024 } || packet[0] != 7)
            throw new InvalidDataException("Missing bounded native world-info packet.");
        using var reader = new BinaryReader(new MemoryStream(packet, 1, packet.Length - 1, false));
        reader.ReadInt32(); byte timeFlags = reader.ReadByte(); reader.ReadByte();
        reader.ReadBytes(12); int world = reader.ReadInt32(); reader.ReadString();
        reader.ReadByte(); reader.ReadBytes(24); reader.ReadBytes(17);
        reader.ReadSingle(); reader.ReadByte(); reader.ReadBytes(12 + 4 + 12 + 4 + 13);
        reader.ReadSingle(); byte progression = reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); byte bosses = reader.ReadByte();
        return new(world, (progression & 16) != 0, (timeFlags & 1) != 0, (timeFlags & 4) != 0,
            (progression & 128) != 0, (bosses & 64) != 0, (progression & 64) != 0, Convert.ToHexString(packet));
    }

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m11-solar-tablet"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(4); var phases = new List<object>(12); var frames = new List<object>(4);
        string status = "failed", failure = ""; int start = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                !File.Exists(Path.Combine(host.RunDirectory, "app", "ServerPlugins", "GameplayScaffold.dll")), "owned-native-plugin-set-no-scaffold");
            await M11EquipmentScenario.RunAsync(host);
            // Native HandleSpawnBoss checks this permission even for Solar Tablet -6. The default
            // TShock group lacks it. Grant exactly this interaction permission in the owned world;
            // retain default/guest inheritance and every anti-cheat/Bouncer protection.
            const string permission = "tshock.npc.startinvasion";
            string beforePermissions = Text("SELECT Commands FROM GroupList WHERE GroupName=$name", "default");
            string beforeParent = Text("SELECT Parent FROM GroupList WHERE GroupName=$name", "default");
            await host.ConsoleCommand("group addperm default " + permission);
            await Until(() => Text("SELECT Commands FROM GroupList WHERE GroupName=$name", "default")
                .Split(',').Contains(permission, StringComparer.Ordinal), TimeSpan.FromSeconds(5));
            string afterPermissions = Text("SELECT Commands FROM GroupList WHERE GroupName=$name", "default");
            Check(beforePermissions.Split(',').Append(permission).ToHashSet(StringComparer.Ordinal)
                .SetEquals(afterPermissions.Split(',')) &&
                Text("SELECT Parent FROM GroupList WHERE GroupName=$name", "default") == beforeParent,
                "console-grants-only-native-startinvasion-permission");
            phases.Add(new { label = "native-interaction-permission", group = "default", beforeParent,
                beforePermissions, afterPermissions, addedPermission = permission });
            var peer = await Actor("M11SolarPeer");
            var initial = await World(peer, "initial");
            Check(initial.Ssc && !initial.HardMode && !initial.Eclipse, "initial-prehardmode-ssc-no-eclipse");
            await SetDay(peer);
            await CommandAndLog("hardmode", "Hardmode is now on.");
            // Native StartHardmode sets the flag then transforms in the background. Its completion
            // resets sections and broadcasts a chat; it does not promise a world7. The new actor's
            // ordinary initial admission supplies the actual hardmode world7 without reauthenticating peers.
            var legal = await Actor("M11SolarLegal");
            var hard = await World(legal, "hardmode-original-gate-open");
            Check(hard.HardMode && hard.DayTime && !hard.Eclipse, "actual-hardmode-native-world-baseline");
            Check(!hard.Plantera && !hard.Golem, "source-Plantera-policy-not-adopted");
            int worldBefore = peer.PacketCount(7);
            await Request(legal, "native-hardmode-allowed", "Pass", "native-solar-tablet-hardmode-gate-open");
            await WorldChanged(peer, worldBefore, world => world.Eclipse);
            Check((await World(peer, "allowed-native-eclipse")).Eclipse, "legal-request-causes-native-world-effect");
            Healthy(legal); Healthy(peer);
            await ClearEclipse(peer);

            // A client which was sent true may still have an earlier legitimate request in flight
            // after the console resets the world. Server false alone must never ban this subject.
            worldBefore = peer.PacketCount(7);
            await CommandAndLog("hardmode", "Hardmode is now off.");
            await WorldChanged(peer, worldBefore, world => !world.HardMode);
            worldBefore = peer.PacketCount(7);
            await Request(legal, "exported-hardmode-then-reset", "Unknown", "solar-tablet-hardmode-export-session-or-plugin-history-incomplete");
            await WorldChanged(peer, worldBefore, world => world.Eclipse);
            Check((await World(peer, "unknown-delayed-request-retained")).Eclipse, "unknown-delayed-request-is-not-blanket-blocked");
            Healthy(legal); await ClearEclipse(peer);

            var fresh = await Actor("M11SolarUnknown");
            long account = Account(fresh.Name);
            var closed = await World(fresh, "fresh-prehardmode-subject");
            Check(!closed.HardMode && closed.DayTime && !closed.Eclipse, "fresh-connection-closed-native-use-gate");
            // Existing imported/passively received Tablet storage does not invoke the active-use rule.
            await fresh.Send(5, writer =>
            { writer.Write(fresh.Slot); writer.Write((short)11); writer.Write((short)1); writer.Write((byte)0); writer.Write((short)2767); writer.Write((byte)0); });
            await fresh.PingAsync(); Healthy(fresh);
            int mark = host.ConsoleLines().Length, peerItems = peer.InventoryUpdates.Count;
            worldBefore = peer.PacketCount(7);
            // A fresh connection does not clear the process-wide export-integrity latch. In the
            // actual console-prepared run it can therefore stop at the earlier context-Unknown
            // branch. Both explicit Unknown branches preserve the same ordinary native action;
            // do not claim that this TCP fixture established complete world history.
            await Request(fresh, "fresh-prehardmode-unproved-context", "Unknown",
                "solar-tablet-hardmode-export-session-or-plugin-history-incomplete",
                "solar-tablet-started-item-identity-history-unproved");
            await WorldChanged(peer, worldBefore, world => world.Eclipse);
            var after = await World(peer, "unknown-fresh-request-retained");
            Check(after.Eclipse && after.WorldId == closed.WorldId && !after.HardMode,
                "unproved-active-use-does-not-blanket-block-native-eclipse");
            await fresh.Send(5, writer => LabClient.WriteInventory(writer, fresh.Slot, 1, 9)); await fresh.PingAsync();
            await peer.WaitUntil(() => peer.InventoryUpdates.Skip(peerItems).Any(x => x.Player == fresh.Slot && x.Slot == 10 && x.Item == 9),
                TimeSpan.FromSeconds(5));
            Check(true, "unknown-subject-followup-normal-slot-forwarded");
            Check(!host.ConsoleLines().Skip(mark).Any(line => line.Contains("ANTICHEAT_INCIDENT ") && line.Contains("rule=" + Rule + " ")),
                "unproved-started-item-history-produces-zero-incident");
            Healthy(fresh); Healthy(peer); Healthy(legal);
            await fresh.DisposeAsync();
            var reconnect = await host.Connect(fresh.Name); clients.Add(reconnect);
            await reconnect.Join(); await reconnect.Login(); Healthy(reconnect);
            Check(reconnect.Authenticated && !reconnect.IsBanRejection, "same-account-reconnect-allowed");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, rule = Rule, frames, phases, syntheticLoopbackTcp = true, stockClientGui = false,
                worldPreparation = "existing authenticated TShock console hardmode/time/worldevent commands on this disposable owned world",
                itemAcquisitionProof = false, sourcePolicyAdopted = false, hardCandidateAdmitted = false,
                synchronization = "time noon -> native18; eclipse request/worldevent/hardmode off -> native7; hardmode on verified by a new actor's ordinary initial7; no post-admission6",
                rejectedProofReason = "native SSC5 can replace the item which started an existing use animation",
                freshConnectionWorldHistoryComplete = "not asserted by TCP; exact observed Unknown reason retained in traces",
                clientDiagnostics = clients.Select(client => new { client.Name,
                    packet7Count = client.PacketCount(7), packet18Count = client.PacketCount(18),
                    lastWorldPacketHex = client.LastWorldInfo is { } worldPacket ? Convert.ToHexString(worldPacket) : null,
                    lastTimePacketHex = client.LastTimeInfo is { } timePacket ? Convert.ToHexString(timePacket) : null,
                    client.DisconnectReason, messages = client.Messages.TakeLast(12).ToArray() }).ToArray(),
                traces = host.ConsoleLines().Skip(start).Where(line => line.Contains(Rule, StringComparison.Ordinal) ||
                    line.Contains("Hardmode is now", StringComparison.Ordinal) || line.Contains("eclipse", StringComparison.OrdinalIgnoreCase)).ToArray()
            }, Json));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m11-solar-tablet:" + label);
        long Scalar(string sql, string name) => Convert.ToInt64(Query(sql, name) ?? 0L);
        string Text(string sql, string name) => Convert.ToString(Query(sql, name)) ?? "";
        object? Query(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = sql; query.Parameters.AddWithValue("$name", name);
            return query.ExecuteScalar();
        }
        long Account(string name) => Scalar("SELECT ID FROM Users WHERE Username=$name", name);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            // Initial SSC/login can legitimately set TShock LastThreat. Retain its native five-second
            // throttle; do not diagnose a downstream core rejection as a missing world-info packet.
            await client.Drain(TimeSpan.FromMilliseconds(5200));
            Check(client.Authenticated && client.SscSlots.Count >= 350 && Account(name) > 0, name + "-ordinary-auth-ssc");
            Check(Text("SELECT UserGroup FROM Users WHERE Username=$name", name) == "default", name + "-default-no-bypass-group");
            return client;
        }
        void Healthy(LabClient client) => Check(!client.Closed && client.DisconnectReason is null &&
            Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + client.Name) == 0, client.Name + "-connected-unbanned");
        async Task<WorldInput> World(LabClient client, string label)
        {
            // Read the actual admitted/exported world frame. Ping only drains its receive stream;
            // no world command or post-admission6 is manufactured by taking a snapshot.
            await client.PingAsync();
            var result = ReadWorld(client.LastWorldInfo); phases.Add(new { label, actor = client.Name, world = result }); return result;
        }
        async Task WorldChanged(LabClient client, int count, Func<WorldInput, bool> condition)
        {
            await client.WaitUntil(() => client.PacketCount(7) > count && condition(ReadWorld(client.LastWorldInfo)), TimeSpan.FromSeconds(5));
        }
        async Task SetDay(LabClient client)
        {
            int count = client.PacketCount(18); await host.ConsoleCommand("time noon");
            await client.WaitUntil(() => client.PacketCount(18) > count, TimeSpan.FromSeconds(5));
            var clock = ReadTime(client.LastTimeInfo);
            phases.Add(new { label = "console-noon-time18-export", actor = client.Name, clock });
            Check(clock.DayTime && clock.Time is >= 27000 and < 27120, "noon-command-actual-native-time18");
        }
        async Task ClearEclipse(LabClient client)
        {
            int count = client.PacketCount(7);
            await host.ConsoleCommand("worldevent eclipse");
            await WorldChanged(client, count, world => !world.Eclipse);
        }
        async Task CommandAndLog(string command, string response)
        {
            int mark = host.ConsoleLines().Length; await host.ConsoleCommand(command);
            await Until(() => host.ConsoleLines().Skip(mark).Any(line => line.Contains(response, StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
        }
        async Task Request(LabClient client, string label, string verdict, params string[] reasons)
        {
            long account = Account(client.Name); int mark = host.ConsoleLines().Length;
            byte[] frame = LabClient.Packet(61, writer => { writer.Write((short)client.Slot); writer.Write((short)-6); });
            frames.Add(new { label, hex = Convert.ToHexString(frame), account, slot = client.Slot });
            await client.SendBatch(frame); await client.PingAsync();
            await Until(() => host.ConsoleLines().Skip(mark).Any(line => line.Contains("ANTICHEAT_RULE_INPUT ") &&
                line.Contains("rule=" + Rule + " ") && line.Contains("accountId=" + account + " ") &&
                line.Contains("verdict=" + verdict + " ") && reasons.Any(reason => line.Contains("reason=" + reason + " "))), TimeSpan.FromSeconds(5));
            Check(true, label + "-actual-rule-branch");
        }
        static async Task Until(Func<bool> predicate, TimeSpan limit)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate())
            {
                DevRunLifecycle.Check(); if (watch.Elapsed >= limit) throw new TimeoutException("Solar Tablet acceptance condition did not complete.");
                await DevRunLifecycle.WaitAsync(Task.Delay(30), TimeSpan.FromSeconds(1));
            }
        }
    }
}
