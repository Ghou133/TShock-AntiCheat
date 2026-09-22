using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Owned real TCP at unchanged product/native deadlines; no timer or cleanup mutation.</summary>
internal static class M16ConnectionTimeoutScenario
{
    private sealed record Target(string Label, LabClient Client, int NativeState, int Seconds,
        JsonElement Initial, long BeganTimestamp, int Slot);

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-connection-timeouts"); Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(16); var targets = new List<Target>(7);
        var incompleteHelloWrites = new List<object>(16);
        var completed = new Dictionary<string, object>(); string status = "failed", failure = "";
        Task legalJoin = Task.CompletedTask;
        var totalElapsed = Stopwatch.StartNew();
        long initialBans = BanCount(); int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime"); await host.FixtureSnapshot();
            var observer = await Connect("M16TimeoutPeer"); await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350, "normal-authenticated-SSC-peer");
            foreach (var plan in new[] { ("hello0", 0, 120), ("partial0", 0, 120), ("password", -1, 120), ("auth1", 1, 300), ("auth2", 2, 300), ("world3", 3, 600), ("playing10", 10, 300) })
            {
                if (plan.Item2 == -1) await State("password-on");
                var client = await Connect("M16Timeout" + plan.Item1);
                if (plan.Item2 == 10)
                { await client.Join(); await client.RegisterAndLogin(); client.PauseHeartbeat = true; }
                else await client.Join(stopAtNativeState: plan.Item2);
                if (plan.Item1 == "partial0") incompleteHelloWrites.Add(new { utc = DateTimeOffset.UtcNow,
                    bytes = await client.SendIncompleteHelloFragment(), completeFrame = false });
                await client.Drain(TimeSpan.FromMilliseconds(300));
                JsonElement state = await Track(plan.Item1, client);
                if (plan.Item2 == -1) await State("password-off");
                var row = Row(state, plan.Item1);
                Check(row.GetProperty("nativeState").GetInt32() == plan.Item2, plan.Item1 + "-actual-accepted-native-state");
                if (plan.Item2 == 0)
                    Check(row.GetProperty("phase").ValueKind == JsonValueKind.Null && row.GetProperty("session").ValueKind == JsonValueKind.Null &&
                        row.GetProperty("nativeOnly").GetBoolean() && row.GetProperty("rootBindingAtSlotNull").GetBoolean() && row.GetProperty("actorAtSlotNull").GetBoolean(),
                        plan.Item1 + "-pre-Hello-has-no-account-session-or-phase");
                else Check(row.GetProperty("phase").ValueKind == JsonValueKind.Object && row.GetProperty("rootBindingMatches").GetBoolean(),
                    plan.Item1 + "-actual-root-phase-and-binding");
                Check(row.GetProperty("currentSocketMatches").GetBoolean(), plan.Item1 + "-exact-current-transport");
                targets.Add(new(plan.Item1, client, plan.Item2, plan.Item3, row.Clone(), state.GetProperty("timestamp").GetInt64(), row.GetProperty("Slot").GetInt32()));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "initial.json"), JsonSerializer.Serialize(targets.Select(t => new
            { t.Label, t.NativeState, t.Seconds, t.Initial, client = t.Client.Evidence() }), options));

            // A finite delayed world-sync client completes through the unchanged normal Join path.
            // Its 45-second pause stays below both default independent deadlines and has no synthetic heartbeat.
            var legal = await Connect("M16TimeoutLegal");
            int delayed = 0;
            legalJoin = legal.Join(pauseAcceptedPhase: async native =>
            {
                if (native != 3) return;
                delayed++;
                await legal.Drain(TimeSpan.FromSeconds(45));
                Check(!legal.Closed && legal.DisconnectReason is null, "legal-45-second-world-pause-still-open");
            });
            var elapsed = Stopwatch.StartNew(); double nextDuplicate = 0, nextSnapshot = 0;
            bool legalCompleted = false;
            while (completed.Count != targets.Count)
            {
                DevRunLifecycle.Check();
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(650), "all-default-deadlines-bounded");
                if (elapsed.Elapsed.TotalSeconds >= nextDuplicate)
                {
                    nextDuplicate += 10;
                    var partial = targets.Single(t => t.Label == "partial0");
                    if (!completed.ContainsKey(partial.Label) && !partial.Client.Closed)
                    {
                        try { incompleteHelloWrites.Add(new { utc = DateTimeOffset.UtcNow,
                            bytes = await partial.Client.SendIncompleteHelloFragment(), completeFrame = false }); }
                        catch (IOException)
                        { await partial.Client.Drain(TimeSpan.FromMilliseconds(100)); if (!partial.Client.Closed) throw; }
                    }
                    foreach (var target in targets.Where(t => t.NativeState > 0 && !completed.ContainsKey(t.Label) && !t.Client.Closed))
                    {
                        try
                        {
                            // TSAPI InvokeServerConnect returns before its handlers when State != 0;
                            // the target326 native Hello likewise returns as a no-op.
                            // These frames refresh its native idle timer, never the plugin's phase timer.
                            // A playing client uses a real ping, which is not a PlayerUpdate heartbeat.
                            if (target.NativeState == 10) await target.Client.Send(154, _ => { });
                            else await target.Client.Send(1, w => w.Write("Terraria326"));
                        }
                        catch (IOException)
                        { await target.Client.Drain(TimeSpan.FromMilliseconds(100)); if (!target.Client.Closed) throw; }
                    }
                }
                if (!legalCompleted && legalJoin.IsCompleted)
                {
                    await legalJoin; await legal.RegisterAndLogin(); legalCompleted = true;
                    Check(delayed == 1 && legal.Authenticated && legal.SscSlots.Count >= 350, "legal-delayed-join-authenticated-with-SSC");
                    await legal.PingAsync();
                }
                foreach (var target in targets) await target.Client.Drain(TimeSpan.FromMilliseconds(2));
                if (elapsed.Elapsed.TotalSeconds >= nextSnapshot)
                {
                    nextSnapshot += 2;
                    var state = await State();
                    foreach (var target in targets.Where(t => !completed.ContainsKey(t.Label)))
                    {
                        var row = Row(state, target.Label);
                        if (!row.GetProperty("capturedConnectionClosed").GetBoolean())
                        {
                            // Genuine currently-active phase evidence; ongoing no-op traffic cannot reset the phase origin.
                            if (row.GetProperty("phase").ValueKind == JsonValueKind.Object)
                                Check(row.GetProperty("phase").GetProperty("PhaseEnteredAt").GetInt64() == target.Initial.GetProperty("phase").GetProperty("PhaseEnteredAt").GetInt64(),
                                    target.Label + "-duplicate-frames-did-not-renew-phase");
                            continue;
                        }
                        if (!row.GetProperty("rootBindingAtSlotNull").GetBoolean() || !row.GetProperty("actorAtSlotNull").GetBoolean() ||
                            !row.GetProperty("nativeSocketNull").GetBoolean() || row.GetProperty("nativeState").GetInt32() != 0) continue;
                        Check(target.Client.Closed, target.Label + "-actual-peer-EOF-or-reset");
                        Check(row.GetProperty("phase").ValueKind == JsonValueKind.Null, target.Label + "-phase-and-budget-released");
                        if (target.NativeState > 0)
                        {
                            Check(row.GetProperty("timeoutRequested").GetBoolean() && row.GetProperty("authorized").GetBoolean() && row.GetProperty("finished").GetBoolean(),
                                target.Label + "-plugin-default-deadline-actually-retired-owned-TCP");
                            Check(row.GetProperty("closeAttempts").GetInt32() == 1, target.Label + "-one-owned-TCP-close");
                            long basis = target.Initial.GetProperty("phase").GetProperty("PhaseEnteredAt").GetInt64();
                            if (target.NativeState == 10) basis = Math.Max(basis, target.Initial.GetProperty("phase").GetProperty("LastPlayerUpdateAt").GetInt64());
                            double seconds = (state.GetProperty("timestamp").GetInt64() - basis) / (double)state.GetProperty("timestampFrequency").GetInt64();
                            Check(seconds >= target.Seconds && seconds < target.Seconds + 15, target.Label + "-unchanged-default-monotonic-deadline-window");
                        }
                        else
                        {
                            double seconds = (state.GetProperty("timestamp").GetInt64() - target.BeganTimestamp) / (double)state.GetProperty("timestampFrequency").GetInt64();
                            Check(seconds >= 0 && seconds < target.Seconds + 15, target.Label + "-native-or-plugin-bounded-handshake-release");
                        }
                        // State0/password may be removed earlier by the enabled native7200-update timer
                        // (State0 adds4 per update). Partial reads do not reset that native counter.
                        // Its bound is valid; it is not misreported as a plugin timeout victory.
                        completed.Add(target.Label, new { target.NativeState, target.Seconds, closed = row.Clone(), elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                            terminationOwner = row.GetProperty("timeoutRequested").GetBoolean() ? "plugin-owned-TCP" : "enabled-native-timeout", client = target.Client.Evidence() });
                        await File.WriteAllTextAsync(Path.Combine(directory, target.Label + "-released.json"), JsonSerializer.Serialize(completed[target.Label], options));
                    }
                    await observer.PingAsync();
                }
                await Task.Delay(100);
            }
            await legalJoin;
            Check(legalCompleted && !legal.Closed && !observer.Closed, "unrelated-and-legal-delayed-clients-survive");
            Check(BanCount() == initialBans, "timeout-never-created-an-account-ban");
            Check(!host.ConsoleLines().Skip(logStart).Any(l => l.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-proven-cheat-incident-from-time");
            var final = await State();
            var receiveWait = Stopwatch.StartNew();
            while (final.GetProperty("receive").GetProperty("Outstanding").GetInt32() != 0 && receiveWait.Elapsed < TimeSpan.FromSeconds(8))
            { await Task.Delay(100); final = await State(); }
            Check(final.GetProperty("receive").GetProperty("Enabled").GetBoolean() && final.GetProperty("receive").GetProperty("Outstanding").GetInt32() == 0,
                "actual-receive-operations-completed-without-retained-buffers");
            Check(final.GetProperty("queue").GetProperty("Pending").GetInt32() == 0 && final.GetProperty("queue").GetProperty("Failed").GetInt64() == 0,
                "owned-retirements-completed-without-retained-retries");
            foreach (var target in targets)
            {
                var next = await Connect("M16New" + target.Label); await next.Join(); await next.RegisterAndLogin();
                var state = await Track("new-" + target.Label, next); var row = Row(state, "new-" + target.Label);
                var prior = targets.SingleOrDefault(t => t.Slot == next.Slot);
                Check(prior is not null, target.Label + "-released-native-slot-actually-reused");
                if (prior!.Initial.GetProperty("session").ValueKind == JsonValueKind.Object)
                    Check(row.GetProperty("session").GetProperty("Generation").GetInt64() > prior.Initial.GetProperty("session").GetProperty("Generation").GetInt64(),
                        target.Label + "-new-root-generation");
                else Check(row.GetProperty("session").GetProperty("Generation").GetInt64() > 0,
                    target.Label + "-first-root-session-after-native-only-slot-release");
                Check(!row.GetProperty("timeoutRequested").GetBoolean() && row.GetProperty("phase").GetProperty("Phase").GetInt32() == 3,
                    target.Label + "-new-connection-does-not-inherit-timeout");
                await next.PingAsync();
            }
            Check(BanCount() == initialBans, "all-recovered-accounts-still-unbanned"); status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, completed, incompleteHelloWrites, defaultDeadlinesChanged = false, nativeTimeoutDisabled = false,
                accountBan = false, realNativeServer = true, syntheticTcp = true, stockClient = false,
                scope = "I02 actual phase expiry, captured transport close, native reset and slot reuse; no SSC atomic stock-client claim"
            }, options));
            foreach (var client in clients) await client.DisposeAsync();
            try { await legalJoin; } catch when (status != "passed") { }
        }
        void Check(bool condition, string label) => host.Assert(condition, "m16-timeouts:" + label);
        void WithinRunBound()
        { if (totalElapsed.Elapsed > TimeSpan.FromSeconds(880)) throw new TimeoutException("Owned I02 scenario exceeded its 880-second operation deadline."); }
        async Task<LabClient> Connect(string name) { WithinRunBound(); var client = await host.Connect(name); clients.Add(client); return client; }
        async Task<JsonElement> Track(string label, LabClient client)
        {
            int port = JsonSerializer.SerializeToElement(client.Evidence()).GetProperty("localPort").GetInt32();
            return await State("track " + label + " " + port);
        }
        async Task<JsonElement> State(string command = "state")
        {
            WithinRunBound();
            var utc = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m16_timeouts " + command);
            string path = Path.Combine(host.ReportDirectory, "m16-timeout-state-latest.json"); var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path)) try
                {
                    using var doc = JsonDocument.Parse(await ReadSnapshotTextAsync(path));
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= utc) return doc.RootElement.Clone();
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Missing fresh read-only timeout state: " + command);
        }
        static JsonElement Row(JsonElement state, string label) => state.GetProperty("observations").EnumerateArray().Single(row => row.GetProperty("Label").GetString() == label);
        long BanCount()
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans"; return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    internal static async Task<string> ReadSnapshotTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
