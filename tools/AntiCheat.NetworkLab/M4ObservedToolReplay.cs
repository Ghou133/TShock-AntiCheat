using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// This scenario reuses the existing NetworkLab client, server lifecycle and QA snapshot.
// Observed payloads are replay input, not another real UI run or a replacement packet framework.
internal sealed record M4ObservedReplayHarness(
    string RunDirectory,
    string ReportDirectory,
    Func<string, Task<LabClient>> Connect,
    Func<string, Task> ConsoleCommand,
    Func<Task<JsonElement>> FixtureSnapshot,
    Func<string[]> ConsoleLines,
    Func<bool> RuntimeVerified,
    Action<bool, string> Assert,
    Action<string, long, string> RecordProvenAccount,
    Func<string, IPAddress, Task<LabClient>>? ConnectFrom = null,
    Func<string, string?, Task<LabClient>>? ConnectWithIdentity = null,
    Func<Task>? RestartServer = null);

internal static class M4ObservedToolReplay
{
    private const string Rule = "NPC01.ServerDebuffDamage";
    private const string SourceRelativePath = "artifacts/network-runs/network-adapter-20260910T004906143Z/gameplay-events-20260910T004908381Z.jsonl";
    private const string SourceSha256 = "4CA9446EBAFFE0E9AEC98D945968A42F7B0E285417ADB9E0C04A0246DF082D00";
    private const string ToolCommit = "2f4b254b6f694c8cbdf0b97f886abc347c6e2512";
    private const string ToolDllSha256 = "C43BCB303404AB46965254ABEABDE89A3B6971C876974A62F17BC2791DED42BA";
    private const string ProjectilePrefixRelativePath = "artifacts/m4-source-tools/d3be-itemeditor-source-prefix-1267.jsonl";
    private const string ProjectilePrefixSha256 = "BDCB1C0FDB74CE3111833C61AE2B03BA61F0FADC62612C1071ED7C28FDAA47D7";
    private const string RitualRule = "C2.CultistRitualRole";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record CapturedFrame(int SourceLine, string Utc, byte Packet, byte OriginalNpc,
        string PayloadHex, string SourceLineSha256);
    private sealed record ReplayFrame(CapturedFrame Source, byte TargetNpc, string PayloadHex, string FramedHex);
    private sealed record Receipt(string File, int Line, string Utc, byte Packet, int PlayerIndex, string PayloadHex);
    private sealed record CapturedProjectileFrame(int SourceLine, string Utc, string Stage, string PayloadHex, string SourceLineSha256);
    private sealed record ReplayProjectileFrame(CapturedProjectileFrame Source, uint OriginalKey, uint Key, string PayloadHex, string FramedHex);

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        if (!File.Exists(Path.Combine(run, ".anticheat-lab")) || !File.Exists(Path.Combine(run, ".compatibility-isolated-test")))
            throw new InvalidOperationException("Observed replay requires this NetworkLab run and its existing QA scaffold.");
        string root = Directory.GetParent(run)?.Parent?.FullName ?? throw new InvalidOperationException("Missing repository root.");
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "observed-tool-replay");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string journal = Path.Combine(run, "tshock", "anticheat", "enforcement");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var source = ReadSource(Path.Combine(root, SourceRelativePath));
        await File.WriteAllTextAsync(Path.Combine(report, "observed-source-frames.json"), JsonSerializer.Serialize(new
        {
            sourceFile = SourceRelativePath, sourceFileSha256 = SourceSha256, frames = source,
            toolRepository = "https://github.com/UnrealMultiple/TerraAngel", toolCommit = ToolCommit, toolDllSha256 = ToolDllSha256,
            captureMeaning = "Actual C2S bodies observed before AntiCheat/core processing; not proof of server acceptance.",
            bypassAfterMarkerUtc = "2026-09-10T01:00:52.7297888+00:00",
            markerTimingNote = "The four original 153 receipts follow the operator's after marker by about 3-6 ms; exact receipt timestamps are retained."
        }, Json));
        var replay = new List<ReplayFrame>(8);
        var receipts = new List<Receipt>(9);
        string status = "failed", failure = "";
        long normalAccount = 0, bypassAccount = 0;
        string? normalDisconnect = null;
        Dictionary<byte, byte>? mapping = null;
        string[] normalConsole = [], bypassConsole = [];
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(source.Count(x => x.Packet == 28) == 4 && source.Count(x => x.Packet == 153) == 4,
                "exact-eight-observed-source-frames-hash-and-timestamps-verified");
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "tshock", "anticheat.json"))))
                Check(config.RootElement.GetProperty("ExecutionScope").GetString() == "TestLab", "actual-testlab-execution-scope");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var control = await host.Connect("M4ReplayControl");
            await control.Join(); await control.RegisterAndLogin();
            Check(control.Authenticated && control.SscSlots.Count >= 350, "normal-control-authentication-and-ssc-before-replay");
            await Echo(control, 2);
            var initial = await host.FixtureSnapshot();
            CheckCaptureHealth(initial);
            var emptySlots = initial.GetProperty("npcs").EnumerateArray().Where(x => !x.GetProperty("active").GetBoolean())
                .Select(x => checked((byte)x.GetProperty("index").GetInt32())).TakeLast(4).ToArray();
            Check(emptySlots.Length == 4, "four-distinct-unused-npc-slots-available");
            mapping = new byte[] { 0, 1, 5, 6 }.Zip(emptySlots).ToDictionary(x => x.First, x => x.Second);
            Check(mapping.Values.Distinct().Count() == 4, "observed-npc-indices-map-one-to-one-to-isolated-slots");
            replay.AddRange(source.Select(frame => Map(frame, mapping[frame.OriginalNpc])));
            Check(replay.All(frame => Convert.FromHexString(frame.PayloadHex).AsSpan(1)
                .SequenceEqual(Convert.FromHexString(frame.Source.PayloadHex).AsSpan(1))),
                "only-npc-index-byte-changed-generation-and-all-other-payload-bytes-preserved");
            await File.WriteAllTextAsync(Path.Combine(report, "replay-inputs.json"), JsonSerializer.Serialize(new
            {
                syntheticReplayOfObservedTool = true, npcIndexMap = mapping, replay,
                framing = "Length and packet type reconstructed with existing LabClient.Packet; captured body unchanged except byte 0.",
                npc28Layout = "byte index, byte generation, Int16 damage, Single knockback, byte direction+1, byte crit",
                originalTcpBatchBoundariesKnown = false, originalTimeSpacingReproduced = false,
                includesUncapturedToolPlayerControlFrames = false,
                targetDomain = "Four inactive high slots; does not validate active-NPC ordinary damage or AI movement."
            }, Json));
            await host.ConsoleCommand("qa_capture 28 153");
            await Until(() => host.ConsoleLines().Any(x => x.Contains("QA raw packet capture: 28,153", StringComparison.Ordinal)),
                "existing-passive-raw-capture-enabled");

            // Ordinary high damage is not a standalone hard predicate. Keep all original
            // generation bytes even if the real core consequently rejects a stale generation.
            var normal = await host.Connect("M4ObservedNormal28");
            await normal.Join(); await normal.RegisterAndLogin();
            normalAccount = Scalar("SELECT ID FROM Users WHERE Username=$value", normal.Name);
            await Echo(normal, 3);
            int normalLogStart = host.ConsoleLines().Length;
            foreach (var frame in replay.Where(x => x.Source.Packet == 28))
                await normal.Send(28, writer => writer.Write(Convert.FromHexString(frame.PayloadHex)));
            await Until(() => ReadReceipts(host.ReportDirectory, 28, normal.Slot).Count >= 4, "all-four-normal-28-bodies-actually-received");
            await control.Drain(TimeSpan.FromMilliseconds(250));
            normalDisconnect = normal.DisconnectReason;
            normalConsole = host.ConsoleLines().Skip(normalLogStart).ToArray();
            var normalReceipts = ReadReceipts(host.ReportDirectory, 28, normal.Slot);
            Check(normalReceipts.Select(x => x.PayloadHex).SequenceEqual(replay.Where(x => x.Source.Packet == 28).Select(x => x.PayloadHex)),
                "actual-normal-28-body-order-matches-observed-replay");
            receipts.AddRange(normalReceipts);
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + normal.Name) == 0 &&
                !normalConsole.Any(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("accountId=" + normalAccount + " ")),
                "ordinary-damage1000-sequence-does-not-create-proof-or-permanent-ban");
            Check(!normal.IsBanRejection && normal.DisconnectReason != "AntiCheat proven violation.",
                "ordinary-sequence-does-not-use-hard-sanction-disconnect");
            if (!normal.Closed && normal.DisconnectReason is null) await Echo(normal, 4);
            await normal.DisposeAsync();
            await Task.Delay(200);

            var bypass = await host.Connect("M4ObservedBypass153");
            await bypass.Join(); await bypass.RegisterAndLogin();
            bypassAccount = Scalar("SELECT ID FROM Users WHERE Username=$value", bypass.Name);
            Check(bypass.Authenticated && bypass.SscSlots.Count >= 350 && bypassAccount > 0, "bypass-actor-normal-authentication-and-ssc");
            await Echo(bypass, 5);
            var before = await host.FixtureSnapshot();
            var beforeNpcs = SelectedNpcs(before, mapping.Values);
            Check(beforeNpcs.All(x => !x.GetProperty("active").GetBoolean()), "all-replay-target-slots-still-inactive-before-first-proof");
            await File.WriteAllTextAsync(Path.Combine(report, "npc-state-before.json"), JsonSerializer.Serialize(beforeNpcs, Json));
            await host.ConsoleCommand("qa_capture 5 28 153");
            await Until(() => host.ConsoleLines().Any(x => x.Contains("QA raw packet capture: 5,28,153", StringComparison.Ordinal)),
                "existing-passive-followup-capture-enabled-after-authentication");
            int logStart = host.ConsoleLines().Length;
            int noticeStart = control.NpcAuthorityMessages.Count, inventoryStart = control.InventoryUpdates.Count;
            byte[] followup = LabClient.Packet(5, writer => LabClient.WriteInventory(writer, bypass.Slot, 1, 9));
            byte[][] batch = replay.Where(x => x.Source.Packet == 153).Select(x => Convert.FromHexString(x.FramedHex))
                .Append(followup).ToArray();
            await bypass.SendBatch(batch);
            await bypass.WaitUntil(() => bypass.DisconnectReason is not null || bypass.Closed, TimeSpan.FromSeconds(8));
            await control.Drain(TimeSpan.FromMilliseconds(500));
            Check(bypass.ProofBatches == 1 && bypass.DisconnectReason == "AntiCheat proven violation.",
                "one-four-frame-batch-is-disconnected-on-its-first-proven-153");
            await Until(() => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value AND Expiration=3155378975999999999", "acc:" + bypass.Name) == 1,
                "first-proven-153-has-one-real-permanent-account-record");
            await Until(() => ReadReceipts(host.ReportDirectory, 153, bypass.Slot).Count >= 4, "all-four-coalesced-153-frames-reached-pre-core-capture");
            bypassConsole = host.ConsoleLines().Skip(logStart).ToArray();
            Check(bypassConsole.Count(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("rule=" + Rule + " ") &&
                x.Contains("accountId=" + bypassAccount + " ") && x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1,
                "exactly-one-attributed-canceled-revoked-incident-for-four-source-frames");
            // Binding.TryReportRevokedPacket intentionally reports only once per session.
            // Require that one trace, then verify every remaining receipt and actual state;
            // a per-frame log count would conflict with the production bounded-log contract.
            Check(bypassConsole.Count(x => x.Contains("ANTICHEAT_REVOKED_PACKET ") && x.Contains("slot=" + bypass.Slot + " ")) == 1 &&
                bypassConsole.Any(x => x.Contains("ANTICHEAT_REVOKED_PACKET ") && x.Contains("slot=" + bypass.Slot + " ") && x.EndsWith("packet=153", StringComparison.Ordinal)),
                "first-subsequent-153-hits-existing-session-gate-with-one-bounded-trace");
            var bypassReceipts = ReadReceipts(host.ReportDirectory, 153, bypass.Slot);
            Check(bypassReceipts.Select(x => x.PayloadHex).SequenceEqual(replay.Where(x => x.Source.Packet == 153).Select(x => x.PayloadHex)),
                "actual-four-153-body-order-matches-observed-replay");
            receipts.AddRange(bypassReceipts);
            await Until(() => ReadReceipts(host.ReportDirectory, 5, bypass.Slot).Count == 1, "coalesced-inventory-followup-reached-pre-core-capture");
            var followupReceipt = ReadReceipts(host.ReportDirectory, 5, bypass.Slot).Single();
            Check(followupReceipt.PayloadHex == Convert.ToHexString(followup.AsSpan(3)) && followupReceipt.Line > bypassReceipts[^1].Line,
                "exact-inventory-followup-body-arrives-after-all-four-153-frames");
            receipts.Add(followupReceipt);
            var after = await host.FixtureSnapshot();
            CheckCaptureHealth(after);
            var afterNpcs = SelectedNpcs(after, mapping.Values);
            await File.WriteAllTextAsync(Path.Combine(report, "npc-state-after.json"), JsonSerializer.Serialize(afterNpcs, Json));
            Check(beforeNpcs.Select(x => x.GetRawText()).SequenceEqual(afterNpcs.Select(x => x.GetRawText())),
                "all-four-npc-slot-active-type-life-position-velocity-states-unchanged");
            Check(control.NpcAuthorityMessages.Skip(noticeStart).All(x => x.Message != 153 || !mapping.Values.Contains(checked((byte)x.Npc))),
                "no-replayed-153-authority-message-reaches-observer");
            Check(control.InventoryUpdates.Skip(inventoryStart).All(x => x.Player != bypass.Slot || x.Slot != 10 || x.Item != 9) &&
                CharacterInventory(bypassAccount).Split('~')[10].StartsWith("0,", StringComparison.Ordinal),
                "no-followup-inventory-broadcast-or-real-ssc-write");
            await Until(() => AppliedIntentCount(journal, bypassAccount) == 1, "one-existing-journal-intent-is-applied");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 1, "no-duplicate-normal-control-or-source-ban");
            host.RecordProvenAccount(bypass.Name, bypassAccount, Rule);
            await bypass.DisposeAsync();
            await Task.Delay(200);
            await Echo(control, 6);
            Check(!control.Closed && Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + control.Name) == 0,
                "innocent-control-still-works-after-bypass-sanction");
            await ReplayProjectiles(control);
            await host.ConsoleCommand("qa_capture off");
            status = "passed";
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(report, "replay-summary.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, syntheticReplayOfObservedTool = true, actualClientUiExecutedThisRun = false,
                sourceFile = SourceRelativePath, sourceFileSha256 = SourceSha256, source, replay, receipts,
                normalAccount, bypassAccount, normalDisconnect, normalConsole, bypassConsole,
                normal28Meaning = "High damage alone is unmodeled or core-rejected; no hard proof or permanent ban is accepted.",
                targetDomain = "Four inactive high NPC slots, with every payload byte except index preserved.",
                confirmsActiveNpcDamage = false, originalTcpBatchBoundariesKnown = false,
                originalTimeSpacingReproduced = false, elapsedMs = timer.ElapsedMilliseconds
            }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "observed-replay:" + name);
        void CheckCaptureHealth(JsonElement snapshot) => Check(snapshot.GetProperty("recordingHealth").GetProperty("dropped").GetInt64() == 0 &&
            !snapshot.GetProperty("recordingHealth").GetProperty("journalLimitReported").GetBoolean(), "existing-scaffold-capture-health-complete");
        async Task Echo(LabClient client, byte emote)
        {
            await client.Send(120, writer => { writer.Write(client.Slot); writer.Write(emote); });
            await client.WaitUntil(() => client.Bubbles.Any(x => x.Player == client.Slot && x.Emote == emote), TimeSpan.FromSeconds(5));
        }
        async Task ReplayProjectiles(LabClient control)
        {
            var projectileSource = ReadProjectileSource(Path.Combine(root, ProjectilePrefixRelativePath));
            var mapped = new List<ReplayProjectileFrame>(5);
            var received = new List<Receipt>(6);
            var observedConsole = new List<string>();
            long bowAccount = 0, staffAccount = 0;
            string projectileStatus = "failed", projectileFailure = "";
            try
            {
                Check(projectileSource.Count == 5, "exact-five-gui-projectile-frames-from-fixed-complete-prefix");
                // Stop capturing packet 5 before the two new actors' ordinary SSC handshakes.
                await host.ConsoleCommand("qa_capture 27");
                await Until(() => host.ConsoleLines().Any(x => x.EndsWith("QA raw packet capture: 27", StringComparison.Ordinal)),
                    "projectile-only-passive-capture-enabled");
                var bow = await host.Connect("M4ObservedBow27");
                await bow.Join(); await bow.RegisterAndLogin(); await Echo(bow, 7);
                bowAccount = Scalar("SELECT ID FROM Users WHERE Username=$value", bow.Name);
                Check(bow.Authenticated && bow.SscSlots.Count >= 350 && bowAccount > 0, "new-bow-actor-authenticated-with-real-ssc");
                int bowReceiptStart = ReadReceipts(host.ReportDirectory, 27, bow.Slot).Count;
                int bowLogStart = host.ConsoleLines().Length;
                mapped.AddRange(projectileSource.Take(3).Select((sourceFrame, index) => MapProjectile(sourceFrame, bow.Slot, 920 + index)));
                foreach (var frame in mapped)
                {
                    await bow.Send(27, writer => writer.Write(Convert.FromHexString(frame.PayloadHex)));
                    await control.Drain(TimeSpan.FromMilliseconds(200));
                }
                await Until(() => ReadReceipts(host.ReportDirectory, 27, bow.Slot).Count == bowReceiptStart + 3,
                    "all-three-bow-projectile-bodies-received");
                var bowReceipts = ReadReceipts(host.ReportDirectory, 27, bow.Slot).Skip(bowReceiptStart).ToArray();
                Check(bowReceipts.Select(x => x.PayloadHex).SequenceEqual(mapped.Select(x => x.PayloadHex)),
                    "bow-default-damage1005-and-ui490-actual-type1-order-preserved");
                received.AddRange(bowReceipts);
                var bowConsole = host.ConsoleLines().Skip(bowLogStart).ToArray();
                observedConsole.AddRange(bowConsole);
                Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + bow.Name) == 0 &&
                    !bowConsole.Any(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("accountId=" + bowAccount + " ")) &&
                    !bow.IsBanRejection && bow.DisconnectReason != "AntiCheat proven violation.",
                    "bow-damage1005-and-overridden-ui490-never-upgrade-to-hard-proof");
                Check(control.Projectiles.Any(x => x.Key == mapped[0].Key && x.Type == 1),
                    "actual-default-bow-arrow-reaches-innocent-observer");
                if (!bow.Closed && bow.DisconnectReason is null) await Echo(bow, 8);
                await bow.DisposeAsync(); await Task.Delay(200);

                var staff = await host.Connect("M4ObservedStaff27");
                await staff.Join(); await staff.RegisterAndLogin(); await Echo(staff, 9);
                staffAccount = Scalar("SELECT ID FROM Users WHERE Username=$value", staff.Name);
                Check(staff.Authenticated && staff.SscSlots.Count >= 350 && staffAccount > 0 && staffAccount != bowAccount,
                    "new-staff-actor-has-independent-account-and-ssc");
                int staffReceiptStart = ReadReceipts(host.ReportDirectory, 27, staff.Slot).Count;
                var defaultStaff = MapProjectile(projectileSource[3], staff.Slot, 923);
                var alteredStaff = MapProjectile(projectileSource[4], staff.Slot, 924);
                mapped.Add(defaultStaff); mapped.Add(alteredStaff);
                Check(mapped.Select(x => x.Key).Distinct().Count() == 5 && mapped.All(x =>
                    Convert.FromHexString(x.PayloadHex).AsSpan(4).SequenceEqual(Convert.FromHexString(x.Source.PayloadHex).AsSpan(4)) &&
                    (x.Key >> 18) == (x.OriginalKey >> 18)),
                    "only-packed-key-spawner-and-unused-index-remapped-generation-and-all-other-bytes-preserved");
                int staffLogStart = host.ConsoleLines().Length;
                await staff.Send(27, writer => writer.Write(Convert.FromHexString(defaultStaff.PayloadHex)));
                await control.WaitUntil(() => control.Projectiles.Any(x => x.Key == defaultStaff.Key && x.Type == 121), TimeSpan.FromSeconds(5));
                await Echo(staff, 10);
                Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + staff.Name) == 0 &&
                    !host.ConsoleLines().Skip(staffLogStart).Any(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("accountId=" + staffAccount + " ")),
                    "actual-default-staff121-is-accepted-before-altered-staff490");
                await host.ConsoleCommand("qa_capture 5 27");
                await Until(() => host.ConsoleLines().Any(x => x.EndsWith("QA raw packet capture: 5,27", StringComparison.Ordinal)),
                    "staff-followup-passive-capture-enabled-after-authentication");
                int staffInventoryReceiptStart = ReadReceipts(host.ReportDirectory, 5, staff.Slot).Count;
                int observerProjectileStart = control.Projectiles.Count, observerInventoryStart = control.InventoryUpdates.Count;
                byte[] followup = LabClient.Packet(5, writer => LabClient.WriteInventory(writer, staff.Slot, 1, 9));
                await staff.SendBatch(Convert.FromHexString(alteredStaff.FramedHex), followup);
                await staff.WaitUntil(() => staff.DisconnectReason is not null || staff.Closed, TimeSpan.FromSeconds(8));
                await control.Drain(TimeSpan.FromMilliseconds(500));
                Check(staff.ProofBatches == 1 && staff.DisconnectReason == "AntiCheat proven violation.",
                    "first-actual-gui490-replay-immediately-sanctions-independent-staff-account");
                await Until(() => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value AND Expiration=3155378975999999999", "acc:" + staff.Name) == 1,
                    "actual-staff490-has-one-real-permanent-account-ban");
                await Until(() => ReadReceipts(host.ReportDirectory, 27, staff.Slot).Count == staffReceiptStart + 2,
                    "both-actual-staff-bodies-received");
                var staffReceipts = ReadReceipts(host.ReportDirectory, 27, staff.Slot).Skip(staffReceiptStart).ToArray();
                Check(staffReceipts.Select(x => x.PayloadHex).SequenceEqual(new[] { defaultStaff.PayloadHex, alteredStaff.PayloadHex }),
                    "actual-staff121-then490-body-order-matches-gui-source");
                received.AddRange(staffReceipts);
                await Until(() => ReadReceipts(host.ReportDirectory, 5, staff.Slot).Count == staffInventoryReceiptStart + 1,
                    "staff-followup-actually-received-after-first-proof");
                var inventoryReceipt = ReadReceipts(host.ReportDirectory, 5, staff.Slot).Last();
                Check(inventoryReceipt.PayloadHex == Convert.ToHexString(followup.AsSpan(3)) && inventoryReceipt.Line > staffReceipts[^1].Line,
                    "exact-staff-inventory-followup-reaches-pre-core-after490");
                received.Add(inventoryReceipt);
                var staffConsole = host.ConsoleLines().Skip(staffLogStart).ToArray();
                observedConsole.AddRange(staffConsole);
                Check(staffConsole.Count(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("rule=" + RitualRule + " ") &&
                    x.Contains("accountId=" + staffAccount + " ") && x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) &&
                    x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1,
                    "one-exact-c2-incident-canceled-and-synchronously-revoked");
                Check(staffConsole.Count(x => x.Contains("ANTICHEAT_REVOKED_PACKET ") && x.Contains("slot=" + staff.Slot + " ") && x.EndsWith("packet=5", StringComparison.Ordinal)) == 1,
                    "staff-followup-hits-bounded-session-revocation-gate");
                Check(control.Projectiles.Skip(observerProjectileStart).All(x => x.Key != alteredStaff.Key) &&
                    control.InventoryUpdates.Skip(observerInventoryStart).All(x => x.Player != staff.Slot || x.Slot != 10 || x.Item != 9) &&
                    CharacterInventory(staffAccount).Split('~')[10].StartsWith("0,", StringComparison.Ordinal),
                    "no-altered490-outbound-or-followup-inventory-broadcast-or-real-ssc-write");
                await Until(() => AppliedIntentCount(journal, staffAccount, RitualRule) == 1, "one-c2-applied-journal-intent");
                Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 2, "only-npc153-and-staff490-have-permanent-account-bans");
                host.RecordProvenAccount(staff.Name, staffAccount, RitualRule);
                await staff.DisposeAsync(); await Task.Delay(200); await Echo(control, 11);
                Check(!control.Closed && Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + control.Name) == 0,
                    "innocent-control-still-works-after-both-observed-tool-sanctions");
                CheckCaptureHealth(await host.FixtureSnapshot());
                projectileStatus = "passed";
            }
            catch (Exception error) { projectileFailure = error.GetType().Name + ": " + error.Message; throw; }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(report, "projectile-replay-summary.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, status = projectileStatus, failure = projectileFailure,
                    syntheticReplayOfObservedTool = true, actualClientUiExecutedThisRun = false,
                    originalSourceFile = "artifacts/network-runs/network-adapter-20260910T012355781Z/gameplay-events-20260910T012357916Z.jsonl",
                    sourcePrefix = ProjectilePrefixRelativePath, prefixLines = 1267, prefixBytes = 5630600, prefixSha256 = ProjectilePrefixSha256,
                    wholeOriginalFileFinalHashClaimed = false, source = projectileSource, mapped, received, bowAccount, staffAccount,
                    keyMapping = "Packed uint: spawner=current actor slot, index=920..924 unique within this fresh isolated run, original generation retained; bytes 4 onward unchanged.",
                    originalTcpBatchBoundariesKnown = false, originalTimeSpacingReproduced = false, includesUncapturedUseOrAmmoFrames = false,
                    highDamageMeaning = "No hard proof from damage1005 alone; upstream block is not reclassified as cheating.",
                    projectileWorldStateSnapshotAvailable = false, outboundObservation = "Existing independent control client's packet27 messages and actual SSC state, not a new world-projectile snapshot.",
                    observedConsole
                }, Json));
            }
        }
        SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open(); return connection;
        }
        long Scalar(string sql, string? value = null)
        {
            using var connection = OpenDatabase(); using var command = connection.CreateCommand(); command.CommandText = sql;
            if (value is not null) command.Parameters.AddWithValue("$value", value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        string CharacterInventory(long account)
        {
            using var connection = OpenDatabase(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT Inventory FROM tsCharacter WHERE Account=$account"; command.Parameters.AddWithValue("$account", account);
            return (string?)command.ExecuteScalar() ?? throw new InvalidDataException("Missing actual SSC record.");
        }
    }

    private static IReadOnlyList<CapturedFrame> ReadSource(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Observed source file missing or outside capture budget.");
        using (var stream = File.OpenRead(path))
            if (Convert.ToHexString(SHA256.HashData(stream)) != SourceSha256) throw new InvalidDataException("Observed source file hash changed.");
        var expected = new (int Line, byte Packet, string Utc, string Hex)[]
        {
            (1626, 28, "2026-09-10T01:00:19.9415106+00:00", "0000E8030000803F0201"),
            (1627, 28, "2026-09-10T01:00:19.9423982+00:00", "0100E8030000803F0201"),
            (1628, 28, "2026-09-10T01:00:19.9424333+00:00", "0501E8030000803F0201"),
            (1629, 28, "2026-09-10T01:00:19.9424716+00:00", "0601E8030000803F0201"),
            (1689, 153, "2026-09-10T01:00:52.7326132+00:00", "00FF7F"),
            (1690, 153, "2026-09-10T01:00:52.7354809+00:00", "01FF7F"),
            (1691, 153, "2026-09-10T01:00:52.7354978+00:00", "05FF7F"),
            (1692, 153, "2026-09-10T01:00:52.7354982+00:00", "06FF7F")
        };
        var output = new List<CapturedFrame>(8);
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (lineNumber > expected[^1].Line) break;
            var spec = expected.FirstOrDefault(x => x.Line == lineNumber);
            if (spec.Line == 0) continue;
            using var record = JsonDocument.Parse(line); var payload = record.RootElement.GetProperty("payload");
            if (record.RootElement.GetProperty("kind").GetString() != "raw-client-experiment-frame" ||
                record.RootElement.GetProperty("utc").GetString() != spec.Utc || payload.GetProperty("packetId").GetInt32() != spec.Packet ||
                payload.GetProperty("payloadHex").GetString() != spec.Hex || payload.GetProperty("payloadBytes").GetInt32() != spec.Hex.Length / 2 ||
                payload.GetProperty("playerIndex").GetInt32() != 0 || payload.GetProperty("hookPriority").GetInt32() != 2000)
                throw new InvalidDataException("Observed source frame identity mismatch at line " + lineNumber);
            output.Add(new(lineNumber, spec.Utc, spec.Packet, Convert.FromHexString(spec.Hex)[0], spec.Hex,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)))));
        }
        if (output.Count != 8) throw new InvalidDataException("Missing observed source frames.");
        return output;
    }

    private static ReplayFrame Map(CapturedFrame source, byte target)
    {
        byte[] body = Convert.FromHexString(source.PayloadHex); body[0] = target;
        return new(source, target, Convert.ToHexString(body), Convert.ToHexString(LabClient.Packet(source.Packet, writer => writer.Write(body))));
    }
    private static JsonElement[] SelectedNpcs(JsonElement snapshot, IEnumerable<byte> indices)
        => indices.Order().Select(index => snapshot.GetProperty("npcs").EnumerateArray().Single(x => x.GetProperty("index").GetInt32() == index).Clone()).ToArray();
    private static List<Receipt> ReadReceipts(string report, byte packet, byte slot)
    {
        var output = new List<Receipt>(8);
        var paths = Directory.EnumerateFiles(report, "gameplay-events-*.jsonl").Take(2).ToArray();
        if (paths.Length != 1) throw new InvalidDataException("Expected this replay run's single existing gameplay capture.");
        if (new FileInfo(paths[0]).Length > 64 * 1024 * 1024) throw new InvalidDataException("Replay capture budget exceeded.");
        int lineNumber = 0;
        foreach (string line in File.ReadLines(paths[0]))
        {
            if (++lineNumber > 20000) throw new InvalidDataException("Replay capture line budget exceeded.");
            if (!line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal)) continue;
            using var record = JsonDocument.Parse(line); var payload = record.RootElement.GetProperty("payload");
            if (payload.GetProperty("packetId").GetInt32() != packet || payload.GetProperty("playerIndex").GetInt32() != slot) continue;
            if (output.Count >= 8) throw new InvalidDataException("Unexpected extra replay frames.");
            output.Add(new(Path.GetFileName(paths[0]), lineNumber, record.RootElement.GetProperty("utc").GetString()!, packet, slot,
                payload.GetProperty("payloadHex").GetString()!));
        }
        return output;
    }
    private static int AppliedIntentCount(string journal, long account, string rule = Rule)
    {
        var paths = Directory.EnumerateFiles(journal, "*.json").Take(3).ToArray();
        if (paths.Length > 2) throw new InvalidDataException("Unexpected replay intent count.");
        int found = 0;
        foreach (string path in paths)
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Oversized replay intent.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var record = JsonDocument.Parse(stream);
            if (record.RootElement.GetProperty("Intent").GetProperty("AccountId").GetInt64() == account &&
                record.RootElement.GetProperty("Intent").GetProperty("Evidence").GetProperty("RuleId").GetString() == rule &&
                record.RootElement.GetProperty("Applied").GetBoolean()) found++;
        }
        return found;
    }
    private static IReadOnlyList<CapturedProjectileFrame> ReadProjectileSource(string path)
    {
        if (new FileInfo(path).Length != 5630600) throw new InvalidDataException("Fixed GUI projectile prefix length mismatch.");
        using (var stream = File.OpenRead(path))
            if (Convert.ToHexString(SHA256.HashData(stream)) != ProjectilePrefixSha256)
                throw new InvalidDataException("Fixed GUI projectile prefix hash mismatch.");
        var expected = new (int Line, string Utc, string Stage, string Hex)[]
        {
            (943, "2026-09-10T01:29:22.8056098+00:00", "wooden-bow-default-type1-damage9", "000004000023034700309145E6C808413A9247C0010030090000000040"),
            (1039, "2026-09-10T01:30:53.3936831+00:00", "wooden-bow-damage1000-actual-type1-damage1005", "000008000023034700309145E6C808413A9247C0010030ED0300000040"),
            (1149, "2026-09-10T01:32:40.1605789+00:00", "wooden-bow-restored-ui490-actual-type1-damage9", "00000C000023034700309145E6C808413A9247C0010030090000000040"),
            (1222, "2026-09-10T01:33:38.2588008+00:00", "amethyst-staff-default-type121-damage15", "0000100000230347003091452160B440DD9503C0790031010000000F0000005040"),
            (1267, "2026-09-10T01:34:12.9437398+00:00", "amethyst-staff-ui490-actual-type490-damage15", "0005040000210347002091452160B440DD9503C0EA01300F0000005040")
        };
        var output = new List<CapturedProjectileFrame>(5);
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (++lineNumber > 1267) throw new InvalidDataException("Unexpected extra GUI prefix lines.");
            var spec = expected.FirstOrDefault(x => x.Line == lineNumber);
            if (spec.Line == 0) continue;
            using var record = JsonDocument.Parse(line); var payload = record.RootElement.GetProperty("payload");
            if (record.RootElement.GetProperty("kind").GetString() != "raw-client-experiment-frame" ||
                record.RootElement.GetProperty("utc").GetString() != spec.Utc || payload.GetProperty("packetId").GetInt32() != 27 ||
                payload.GetProperty("payloadHex").GetString() != spec.Hex || payload.GetProperty("payloadBytes").GetInt32() != spec.Hex.Length / 2 ||
                payload.GetProperty("playerIndex").GetInt32() != 0 || payload.GetProperty("hookPriority").GetInt32() != 2000)
                throw new InvalidDataException("GUI projectile source identity mismatch at line " + lineNumber);
            output.Add(new(lineNumber, spec.Utc, spec.Stage, spec.Hex, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)))));
        }
        if (lineNumber != 1267 || output.Count != 5) throw new InvalidDataException("Incomplete GUI projectile prefix.");
        return output;
    }
    private static ReplayProjectileFrame MapProjectile(CapturedProjectileFrame source, byte slot, int unusedIndex)
    {
        byte[] body = Convert.FromHexString(source.PayloadHex);
        uint originalKey = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint key = LabClient.ProjectileKey(slot, unusedIndex, checked((int)(originalKey >> 18)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, key);
        return new(source, originalKey, key, Convert.ToHexString(body), Convert.ToHexString(LabClient.Packet(27, writer => writer.Write(body))));
    }
    private static async Task Until(Func<bool> condition, string checkpoint)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Observed replay checkpoint: " + checkpoint);
            await Task.Delay(50);
        }
    }
}
