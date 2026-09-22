using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Uses the existing transport and passive QA extension; no alternate game simulator.
internal static class M5EffectsScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        var root = Directory.GetParent(Path.GetFullPath(host.RunDirectory))!.Parent!.FullName;
        var directory = Path.Combine(host.ReportDirectory, "m5-effects"); Directory.CreateDirectory(directory);
        using var sources = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/m5-observed-source-frames.json")));
        foreach (var source in sources.RootElement.EnumerateArray())
        {
            using var stream = File.OpenRead(Path.Combine(root, source.GetProperty("path").GetString()!));
            Check(Convert.ToHexString(SHA256.HashData(stream)) == source.GetProperty("sha256").GetString(), "original-source-hash-" + source.GetProperty("label").GetString());
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "sources.json"), sources.RootElement.GetRawText());
        var observer = await Actor("M5EffectsControl");
        await host.ConsoleCommand("qa_capture 27 28 100 153 155");
        var normal = await Actor("M5ActiveButcher");
        await host.ConsoleCommand("qa_m5_target " + normal.Name);
        var initial = await State("active-before");
        var target = initial.GetProperty("target");
        byte index = target.GetProperty("index").GetByte(), generation = target.GetProperty("generation").GetByte();
        Check(target.GetProperty("active").GetBoolean() && target.GetProperty("life").GetInt32() == 3000, "owned-active-target-life3000-defense0");
        await normal.Send(28, w => { w.Write(index); w.Write(generation); w.Write((short)20); w.Write(0f); w.Write((byte)1); w.Write((byte)0); });
        await Task.Delay(250);
        var legal = await State("active-legal-hit");
        Check(legal.GetProperty("target").GetProperty("life").GetInt32() == 2980, "normal-generation-correct-damage20-reaches-live-strike");
        var raw28 = Source("butcher").GetProperty("frames").EnumerateArray().First(x => x.GetProperty("packet").GetInt32() == 28);
        var body28 = Convert.FromHexString(raw28.GetProperty("body").GetString()!);
        body28[0] = index; body28[1] = generation;
        await File.WriteAllTextAsync(Path.Combine(directory, "active-28-mapping.json"), JsonSerializer.Serialize(new { source = raw28, npcIndex = index, generation, body = Convert.ToHexString(body28), changes = "Only index and generation mapped to the live owned target; source damage1000, knockback1, direction2 and crit1 preserved." }));
        await normal.Send(28, w => w.Write(body28)); await Task.Delay(250);
        var hit = await State("active-tool-hit");
        Check(hit.GetProperty("target").GetProperty("life").GetInt32() == 980, "observed-butcher-damage1000-crit1-causes-actual2000-life-loss");
        await normal.Send(28, w => w.Write(body28)); await Task.Delay(250);
        var death = await State("active-tool-death");
        Check(!death.GetProperty("target").GetProperty("active").GetBoolean(), "second-mapped-tool-hit-kills-owned-live-npc");
        Check(CountBan(normal.Name) == 0 && !normal.IsBanRejection, "ordinary-high-hit-unsupported-no-hard-sanction");
        Check(death.GetProperty("effects").EnumerateArray().Count(x => x.GetProperty("effect").GetProperty("kind").GetString() == "strike-method-entry") >= 3, "actual-strike-method-entries-recorded");
        Check(death.GetProperty("worldItems").GetRawText() != initial.GetProperty("worldItems").GetRawText(), "death-world-item-state-recorded-and-changed");
        // Drops are captured as actual world state, not counted as uniquely attributed by proximity.
        await normal.DisposeAsync(); await Task.Delay(200);

        var portal = await Actor("M5ObservedNpc100");
        await host.ConsoleCommand("qa_m5_target " + portal.Name);
        var pInitial = await State("portal-before-prefix");
        Check(pInitial.GetProperty("target").GetProperty("life").GetInt32() == 3000 && pInitial.GetProperty("target").GetProperty("active").GetBoolean(), "new-owned-live-target-established-for100");
        int pIndex = pInitial.GetProperty("target").GetProperty("index").GetInt32();
        var frames100 = Source("npc100").GetProperty("frames").EnumerateArray().ToArray();
        var mapped = new List<object>();
        foreach (var frame in frames100.Where(x => x.GetProperty("packet").GetInt32() == 27))
        {
            byte[] body = Convert.FromHexString(frame.GetProperty("body").GetString()!);
            uint oldKey = BinaryPrimitives.ReadUInt32LittleEndian(body);
            uint key = LabClient.ProjectileKey(portal.Slot, 930 + mapped.Count, (int)(oldKey >> 18));
            BinaryPrimitives.WriteUInt32LittleEndian(body, key);
            mapped.Add(new { source = frame, oldKey, key, body = Convert.ToHexString(body) });
            await portal.Send(27, w => w.Write(body));
        }
        await observer.Drain(TimeSpan.FromMilliseconds(300));
        var prefix = await State("portal-after-prefix");
        Check(CountBan(portal.Name) == 0 && observer.Projectiles.Any(x => x.Type == 601), "prefix601-actually-exported-without-account-sanction");
        await File.WriteAllTextAsync(Path.Combine(directory, "portal-prefix-outcomes.json"), JsonSerializer.Serialize(new {
            declaration601 = true, declaration602 = true,
            exported601 = observer.Projectiles.Count(x => x.Type == 601), exported602 = observer.Projectiles.Count(x => x.Type == 602),
            nativeCoreProtectionRetained = true, prefixState = prefix.GetProperty("target"), noPrefixRollbackClaim = true }));
        var raw100 = frames100.Single(x => x.GetProperty("packet").GetInt32() == 100);
        byte[] body100 = Convert.FromHexString(raw100.GetProperty("body").GetString()!);
        BinaryPrimitives.WriteUInt16LittleEndian(body100, checked((ushort)pIndex));
        mapped.Add(new { source = raw100, target = pIndex, body = Convert.ToHexString(body100) });
        await File.WriteAllTextAsync(Path.Combine(directory, "portal-mappings.json"), JsonSerializer.Serialize(new { frames = mapped, originalSpacingPreserved = false, prefixRollbackClaimed = false }));
        int notice = observer.NpcAuthorityMessages.Count;
        await Proof(portal, "NPC02.ServerPortalTeleport", [LabClient.Packet(100, w => w.Write(body100))]);
        await observer.Drain(TimeSpan.FromMilliseconds(300));
        var pAfter = await State("portal-after-first-proof");
        Check(prefix.GetProperty("target").GetRawText() == pAfter.GetProperty("target").GetRawText(), "actual-live-npc-state-unchanged-by-illegal100");
        Check(!pAfter.GetProperty("effects").EnumerateArray().Skip(prefix.GetProperty("effects").GetArrayLength()).Any(x => x.GetProperty("effect").GetProperty("kind").GetString() == "teleport-method-entry"), "illegal100-never-enters-target-teleport-method");
        Check(observer.NpcAuthorityMessages.Skip(notice).All(x => x.Message != 100 || x.Npc != pIndex), "no-illegal100-authority-outbound");

        var resize = await Actor("M5ObservedResize155");
        var cBefore = await State("resize-targets-before");
        var chestFrames = Source("resize").GetProperty("frames").EnumerateArray().ToArray();
        Check(chestFrames.Length == 100 && chestFrames.Select(x => BinaryPrimitives.ReadInt16LittleEndian(Convert.FromHexString(x.GetProperty("body").GetString()!))).SequenceEqual(Enumerable.Range(0, 100).Select(x => (short)x)), "exact-original-targets0-through99-no-index-remapping");
        int sizesBefore = observer.ChestSizes.Count;
        await Proof(resize, "CONTAINER01.ChestResizeAuthority", chestFrames.Select(x => LabClient.Packet(155, w => w.Write(Convert.FromHexString(x.GetProperty("body").GetString()!)))).ToArray());
        await observer.Drain(TimeSpan.FromMilliseconds(400));
        var cAfter = await State("resize-targets-after");
        Check(cBefore.GetProperty("toolTargetChests").GetRawText() == cAfter.GetProperty("toolTargetChests").GetRawText(), "all-actual-tool-targets0-through99-capacity-items-preserved");
        Check(observer.ChestSizes.Skip(sizesBefore).All(x => x.Chest is < 0 or > 99), "no-tool-target155-outbound-to-observer");
        Check(!cAfter.GetProperty("effects").EnumerateArray().Skip(cBefore.GetProperty("effects").GetArrayLength()).Any(x => x.GetProperty("effect").TryGetProperty("packet", out var p) && p.GetInt32() == 155), "write-side-send155-method-not-entered-for-tool-batch");
        Check(cAfter.GetProperty("effectsDropped").GetInt32() == 0, "bounded-passive-effect-recording-no-overflow");
        await host.ConsoleCommand("qa_capture off");
        await observer.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status = "passed", syntheticReplayOfObservedTool = true, actualClientUiThisRun = false, source28Damage = 1000, source28Crit = true, actualFirstToolLifeLoss = 2000, originalToolSpacingPreserved = false, noPrefixRollbackClaim = true }));

        JsonElement Source(string label) => sources.RootElement.EnumerateArray().Single(x => x.GetProperty("label").GetString() == label);
        void Check(bool value, string label) => host.Assert(value, "m5-effects:" + label);
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock/tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$name", name); return Convert.ToInt64(cmd.ExecuteScalar());
        }
        long CountBan(string name) => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + name);
        async Task<LabClient> Actor(string name)
        {
            var actor = await host.Connect(name); await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350 && Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", name) == 1, name + "-authenticated-default-no-bypass");
            return actor;
        }
        async Task<JsonElement> State(string label)
        {
            string path = Path.Combine(host.ReportDirectory, "m5-state-latest.json");
            string prior = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
            await host.ConsoleCommand("qa_m5_state");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(50);
                if (File.Exists(path))
                {
                    try { string text = await File.ReadAllTextAsync(path); if (text != prior) { using var doc = JsonDocument.Parse(text); await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return doc.RootElement.Clone(); } }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
                }
                if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("M5 state " + label);
            }
        }
        async Task Proof(LabClient actor, string rule, byte[][] packets)
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            int start = host.ConsoleLines().Length;
            await actor.SendBatch(packets);
            await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed, TimeSpan.FromSeconds(8));
            Check(actor.ProofBatches == 1 && actor.DisconnectReason == "AntiCheat proven violation.", rule + "-first-proof-disconnect");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (CountBan(actor.Name) != 1 && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
            Check(CountBan(actor.Name) == 1 && host.ConsoleLines().Skip(start).Count(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("accountId=" + account + " ") && x.Contains("rule=" + rule + " ") && x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) && x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1, rule + "-single-canceled-revoked-permanent-record");
            host.RecordProvenAccount(actor.Name, account, rule);
            await actor.DisposeAsync(); await Task.Delay(200);
            // Root reconnects every replay account after the side-effect snapshots. Rejoining
            // legitimately sends chest-size section synchronization and is a separate action.
        }
    }
}
