using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real loopback peers distinguish live29 relay from TShock's retained stale-key gate.
/// Native-method tests separately prove the relay available below that gate; they do not prove end-to-end cleanup.</summary>
internal static class M7ProjectileCleanupScenario
{
    private const string Rule = "C1.ProjectileAuthority";

    public static async Task RunAsync(M4ObservedReplayHarness host, LabClient observer, bool withAntiCheat = true)
    {
        string directory = Path.Combine(host.ReportDirectory, "m7-projectile-cleanup");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite");
        var json = new JsonSerializerOptions { WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var frames = new List<object>(8);
        var boundaries = new List<object>(2);
        LabClient? actor = null;
        long account = 0;
        uint ownKey = 0, missingKey = 0, foreignKey = 0;
        string status = "failed", assertions = "failed", failure = "";
        bool liveOwnRelay = false, inactiveCoreBoundary = false, missingCoreBoundary = false, foreignLiveBlocked = false;
        int logStart = host.ConsoleLines().Length;
        int ownReceiptsStart = observer.ProjectileRemovals.Count;
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-isolated-run");
            Check(File.Exists(Path.Combine(host.RunDirectory, "app", "ServerPlugins", "AntiCheat.Plugin.TShock.dll")) == withAntiCheat,
                "actual-plugin-presence-matches-comparison-arm");
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.RunDirectory, "tshock", "anticheat.json"))))
                Check(config.RootElement.GetProperty("ExecutionScope").GetString() == "TestLab", "actual-TestLab-scope-C1-remains-unadmitted-in-Production");
            Check(observer.Authenticated && !observer.Closed, "independent-real-peer-ready");
            actor = await host.Connect("M7CleanupNativeRelay");
            await actor.Join(); await actor.RegisterAndLogin();
            account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            Check(actor.Authenticated && actor.SscSlots.Count >= 350 && account > 0, "current-authenticated-SSC-actor");
            Check(observer.ProjectileRemovals.Count < 9990 && actor.ProjectileRemovals.Count < 9990,
                "bounded-peer29-evidence-has-capacity");

            ownKey = LabClient.ProjectileKey(actor.Slot, 920, 37);
            byte[] create = LabClient.Packet(27, w => actor.WriteProjectileDamage(w, ownKey, 1, 9));
            float x = BinaryPrimitives.ReadSingleLittleEndian(create.AsSpan(7));
            float y = BinaryPrimitives.ReadSingleLittleEndian(create.AsSpan(11));
            int createsBefore = observer.ProjectileClaims.Count;
            frames.Add(new { label = "own-type1-created", source = "synthetic-TCP", frame = Convert.ToHexString(create) });
            await actor.SendBatch(create);
            await observer.WaitUntil(() => observer.ProjectileClaims.Skip(createsBefore).Any(p => p.Key == ownKey && p.Type == 1 && p.Damage == 9),
                TimeSpan.FromSeconds(5));
            Check(true, "new-real-peer27-confirms-own-creation");

            // A finite native cleanup first ends the server copy. This explicitly controlled
            // two-request sequence is not labeled as the old GUI's natural timing.
            await CleanupAndObserve(actor, observer, ownKey, x + 1, y + 1, "first-live-own-cleanup");
            await Outcome(account, "Pass", "current-session-owns-projectile", allowLiveUnknown: true);
            liveOwnRelay = true;
            await CleanupAtCoreBoundary(ownKey, x + 2, y + 2, "native-cleanup-server-inactive", "same-inactive-key-core-retained");
            inactiveCoreBoundary = true;

            missingKey = LabClient.ProjectileKey(actor.Slot, 922, 37);
            await CleanupAtCoreBoundary(missingKey, x + 3, y + 3, "native-cleanup-key-missing", "missing-own-key-core-retained");
            missingCoreBoundary = true;

            // The other peer now owns a real active key. Historical self ownership or an
            // absent-key allowance must never authorize this actor to destroy that object.
            foreignKey = LabClient.ProjectileKey(observer.Slot, 921, 37);
            int foreignCreatesBefore = actor.ProjectileClaims.Count;
            byte[] foreignCreate = LabClient.Packet(27, w => observer.WriteProjectileDamage(w, foreignKey, 1, 9));
            frames.Add(new { label = "independent-peer-live-key", source = "synthetic-TCP", frame = Convert.ToHexString(foreignCreate) });
            await observer.SendBatch(foreignCreate);
            await actor.WaitUntil(() => actor.ProjectileClaims.Skip(foreignCreatesBefore).Any(p => p.Key == foreignKey && p.Type == 1),
                TimeSpan.FromSeconds(5));
            int ownerRemovalsBefore = observer.ProjectileRemovals.Count, actorRemovalsBefore = actor.ProjectileRemovals.Count;
            int bubblesBefore = observer.Bubbles.Count;
            byte[] foreignCleanup = Destroy(foreignKey, x + 4, y + 4);
            frames.Add(new { label = "foreign-live-key-cleanup-rejected", source = "synthetic-TCP", frame = Convert.ToHexString(foreignCleanup) });
            await actor.SendBatch(foreignCleanup, LabClient.Packet(120, w => { w.Write(actor.Slot); w.Write((byte)13); }));
            await observer.WaitUntil(() => observer.Bubbles.Skip(bubblesBefore).Any(p => p.Player == actor.Slot && p.Emote == 13),
                TimeSpan.FromSeconds(5));
            await actor.Drain(TimeSpan.FromMilliseconds(100));
            await Outcome(account, "UnsafeInput", "foreign-owner-projectile-destroy-native-noop");
            Check(observer.ProjectileRemovals.Skip(ownerRemovalsBefore).All(p => p.Key != foreignKey) &&
                actor.ProjectileRemovals.Skip(actorRemovalsBefore).All(p => p.Key != foreignKey),
                "foreign-live-cleanup-has-no-peer29-after-real-later-packet-barrier");
            await CleanupAndObserve(observer, actor, foreignKey, x + 5, y + 5, "actual-owner-can-clean-its-key-after-foreign-rejection");
            foreignLiveBlocked = true;
            Check(actor.Authenticated && !actor.Closed && actor.DisconnectReason is null &&
                observer.Authenticated && !observer.Closed && observer.DisconnectReason is null,
                "both-real-peers-remain-usable");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0 &&
                Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + observer.Name) == 0,
                "no-account-ban-for-normal-cleanup-or-foreign-noop-safety-block");
            // The bounded assertions can pass while full inactive/missing peer cleanup remains unavailable.
            assertions = "passed";
            status = "partial";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(frames, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, assertions, failure, rule = Rule, version = "1.2.0", executionScope = withAntiCheat ? "TestLab" : "core-only-control", account,
                withAntiCheat, coreProtectionsRetained = true,
                actualLoopbackTcp = true, actualClientUiThisRun = false, scaffoldRequired = false,
                source = "synthetic-TCP-through-two-independent-LabClient-connections",
                lifecyclePreparation = "first finite native29 ends the server copy; second same-key29 and an unseen own key test the retained TShock stale-key gate; plugin result is separately reported when loaded",
                liveOwnCleanup = liveOwnRelay ? "actual-peer29-observed" : "not-established",
                endToEndInactiveCleanup = inactiveCoreBoundary ? "blocked-by-TShock-core" : "not-established-this-run",
                endToEndMissingCleanup = missingCoreBoundary ? "blocked-by-TShock-core" : "not-established-this-run",
                foreignLiveCleanup = foreignLiveBlocked ? "blocked-and-actual-owner-cleanup-preserved" : "not-established",
                coreBoundary = new
                {
                    contract = "GetDataHandlers.HandleProjectileKill returns true for !TryGet or !active before OnProjectileKill/Bouncer; TShock.OnGetData assigns it to Handled",
                    getDataHandlersSourceSha256 = "965A267B40CC0562A1C4470433CF8A750CCE86C51F2A41A92671C26B274D3E64",
                    handlerLines = "3453-3456", tShockLines = "1730-1734",
                    classificationBasis = "locked source audit plus real plugin Unknown/canceled=False and zero exact-key peer29 after a later packet barrier; no direct core hook instrumentation",
                    retainedFailedExpectation = "artifacts/network-runs/network-adapter-20260911T005036944Z/m7-projectile-cleanup/summary.json"
                },
                boundaries,
                nativeServerKillAndStillActivePeer = "separate M5ProjectileDestroyTests native-method evidence; not falsely inferred from these two cleanup requests",
                ownKey, missingKey, foreignKey,
                observerReceived29 = observer.ProjectileRemovals.Skip(ownReceiptsStart).Select(p => new { key = p.Key, x = p.X, y = p.Y }),
                actorReceived29 = actor?.ProjectileRemovals.Select(p => new { key = p.Key, x = p.X, y = p.Y }),
                actor = actor?.Evidence(), observer = observer.Evidence(),
                console = host.ConsoleLines().Skip(logStart).Where(p => p.Contains("rule=" + Rule + " ")).ToArray()
            }, json));
            if (actor is not null) await actor.DisposeAsync();
        }

        void Check(bool condition, string label) => host.Assert(condition, "m7-projectile-cleanup:" + label);
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        async Task CleanupAndObserve(LabClient sender, LabClient receiver, uint key, float x, float y, string label)
        {
            int before = receiver.ProjectileRemovals.Count;
            byte[] frame = Destroy(key, x, y);
            frames.Add(new { label, source = "synthetic-TCP", sender = sender.Name, frame = Convert.ToHexString(frame) });
            await sender.SendBatch(frame);
            await receiver.WaitUntil(() => receiver.ProjectileRemovals.Skip(before).Any(p => p.Key == key && p.X == x && p.Y == y),
                TimeSpan.FromSeconds(5));
            Check(true, label + "-new-real-peer29-with-exact-key-and-position");
        }
        async Task CleanupAtCoreBoundary(uint key, float x, float y, string reason, string label)
        {
            int receiptsBefore = observer.ProjectileRemovals.Count, bubblesBefore = observer.Bubbles.Count;
            byte[] frame = Destroy(key, x, y);
            frames.Add(new { label, source = "synthetic-TCP", sender = actor!.Name, frame = Convert.ToHexString(frame) });
            await actor.SendBatch(frame, LabClient.Packet(120, w => { w.Write(actor.Slot); w.Write((byte)13); }));
            await observer.WaitUntil(() => observer.Bubbles.Skip(bubblesBefore).Any(p => p.Player == actor.Slot && p.Emote == 13),
                TimeSpan.FromSeconds(5));
            await observer.Drain(TimeSpan.FromMilliseconds(100));
            await Outcome(account, "Unknown", reason);
            int keyReceipts = observer.ProjectileRemovals.Skip(receiptsBefore).Count(p => p.Key == key);
            Check(keyReceipts == 0, label + "-no-peer29-after-real-later-packet-barrier");
            boundaries.Add(new { label, key, expectation = "core-retained", pluginOutcome = withAntiCheat ? "Unknown" : "not-loaded", pluginCanceled = false,
                laterPacketBarrierObserved = true, newExactKeyPeer29 = keyReceipts,
                endToEndCleanup = "blocked-by-TShock-core", attribution = "locked-source-and-observed-pipeline-boundary" });
        }
        async Task Outcome(long id, string verdict, string reason, bool allowLiveUnknown = false)
        {
            if (!withAntiCheat) return;
            bool Match(string line) => line.Contains("ANTICHEAT_RULE_INPUT ") && line.Contains("rule=" + Rule + " ") &&
                line.Contains("accountId=" + id + " ") && line.Contains("packet=29 ") &&
                line.Contains(verdict == "UnsafeInput" ? " canceled=True " : " canceled=False ") &&
                line.Contains("alreadyCanceled=False") &&
                (line.Contains("verdict=" + verdict + " ") && line.Contains("reason=" + reason + " ") ||
                    allowLiveUnknown && line.Contains("verdict=Unknown ") && line.Contains("reason=projectile-owner-session-provenance-unavailable "));
            var timer = Stopwatch.StartNew();
            while (!host.ConsoleLines().Skip(logStart).Any(Match) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Check(host.ConsoleLines().Skip(logStart).Any(Match), reason + "-actual-rule-result");
        }
    }

    private static byte[] Destroy(uint key, float x, float y) => LabClient.Packet(29, w => { w.Write(key); w.Write(x); w.Write(y); });
}
