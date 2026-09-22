using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Actual TCP replay of private-bank frames produced by the separate actual native
/// sort/quick-stack and player-difference methods. Does not claim an attached stock client.</summary>
internal static class M18StorageScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host, string nativeProbeDll)
    {
        string directory = Path.Combine(host.ReportDirectory, "m18-storage"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(9); var observations = new List<object>(24);
        string status = "failed", failure = ""; int startLog = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(nativeProbeDll) && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-runtime-and-native-producer-present");
            await host.FixtureSnapshot(); var peer = await Actor("M18StoragePeer", false);
            for (int bank = 0; bank < 4; bank++)
            {
                string prefix = "bank" + bank; var actor = await Actor("M18Bank" + bank, false);
                var initial = await Command("bind " + actor.Name, prefix + "-prepared");
                Check(initial.GetProperty("currentActor").GetBoolean() && initial.GetProperty("witnessInstalled").GetBoolean() && initial.GetProperty("itemTableReady").GetBoolean(), prefix + "-actual-actor-witness-and-item-catalog");
                Check(initial.GetProperty("bankLengths").EnumerateArray().All(value => value.GetInt32() == 40), prefix + "-actual-four-allocated-bank-domains");
                await actor.WaitUntil(() => ReceivedMatches(actor, initial.GetProperty("native")), TimeSpan.FromSeconds(8));
                Check(ReceivedMatches(actor, initial.GetProperty("native")), prefix + "-full-real-s2c-preparation-with-favorite-flags");
                var originalSession = initial.GetProperty("session").Clone();
                var sorted = await Native(initial, bank, "sort", prefix + "-sort");
                Check(sorted.GetProperty("changedSlots").GetArrayLength() > 0 && sorted.GetProperty("changedSlots").EnumerateArray().Any(value => value.GetInt32() == initial.GetProperty("offsets")[bank].GetInt32() + 39), prefix + "-native-sort-includes-last-allocated-slot");
                var afterSort = await Apply(actor, sorted, prefix + "-sort");
                var alreadySorted = await Native(afterSort, bank, "sort", prefix + "-already-sorted");
                Check(alreadySorted.GetProperty("frames").GetArrayLength() == 0, prefix + "-already-sorted-native-zero-writes");
                await Apply(actor, alreadySorted, prefix + "-already-sorted");
                var quick = await Native(afterSort, bank, "quickstack", prefix + "-quickstack");
                Check(quick.GetProperty("changedSlots").EnumerateArray().Any(value => value.GetInt32() == 10) &&
                    quick.GetProperty("changedSlots").EnumerateArray().Any(value => value.GetInt32() == 11), prefix + "-actual-quick-stack-consumes-normal-sources");
                var afterQuick = await Apply(actor, quick, prefix + "-quickstack");
                var stableQuick = await Native(afterQuick, bank, "quickstack", prefix + "-quickstack-stable");
                Check(stableQuick.GetProperty("frames").GetArrayLength() == 0, prefix + "-repeated-quick-stack-native-zero-writes");
                await Apply(actor, stableQuick, prefix + "-quickstack-stable");
                Healthy(actor); await actor.DisposeAsync(); await Left(prefix + "-left");
                var restoredActor = await Actor(actor.Name, true); var restored = await Command("bind " + restoredActor.Name, prefix + "-reconnected");
                Check(restored.GetProperty("grantAccountCount").GetInt32() == initial.GetProperty("grantAccountCount").GetInt32(), prefix + "-reconnect-does-not-regrant-assets");
                Check(restored.GetProperty("account").GetInt32() == initial.GetProperty("account").GetInt32() && restored.GetProperty("session").GetRawText() != originalSession.GetRawText(), prefix + "-new-actual-session-same-authenticated-account");
                Check(EqualRows(restored.GetProperty("native"), afterQuick.GetProperty("native")) && EqualRows(restored.GetProperty("ssc"), afterQuick.GetProperty("native")), prefix + "-native-and-persisted-ssc-restored-without-duplicates");
                await restoredActor.WaitUntil(() => ReceivedMatches(restoredActor, afterQuick.GetProperty("native")), TimeSpan.FromSeconds(8));
                Check(ReceivedMatches(restoredActor, afterQuick.GetProperty("native")), prefix + "-actual-relogin-s2c-restores-all-target-slots-and-flags");
                var resumed = await Native(restored, bank, "sort", prefix + "-normal-after-reconnect");
                await Apply(restoredActor, resumed, prefix + "-normal-after-reconnect");
                Healthy(restoredActor); await restoredActor.DisposeAsync(); await Left(prefix + "-reconnected-left");
            }
            Healthy(peer); Check(!host.ConsoleLines().Skip(startLog).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "normal-storage-has-no-cheat-incident");
            status = "passed";

            async Task<JsonElement> Apply(LabClient actor, JsonElement corpus, string label)
            {
                byte[][] frames = corpus.GetProperty("frames").EnumerateArray().Select(value => Convert.FromHexString(value.GetString()!)).ToArray();
                var writes = frames.Where(frame => frame[2] == 5).ToArray();
                Check(frames.Length <= 220 && frames.All(frame => frame.Length == 12 && frame[2] == 5 || frame.Length == 3 && frame[2] == 138), label + "-bounded-actual-native-frames");
                var before = await Command("state", label + "-before"); await peer.Drain(TimeSpan.FromMilliseconds(100)); int peerStart = peer.InventoryUpdates.Count;
                if (frames.Length > 0) await actor.SendBatch(frames.SelectMany(frame => frame).ToArray());
                await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(120)); var after = await Command("state", label + "-after");
                long Delta(string name) => after.GetProperty(name).GetInt64() - before.GetProperty(name).GetInt64();
                Check(after.GetProperty("faults").GetInt64() == 0 && Delta("raw") == writes.Length && Delta("core") == writes.Length && Delta("setters") == writes.Length, label + "-all-real-five-frames-reach-core-and-native-setter");
                Check(Delta("tail138") == frames.Count(frame => frame[2] == 138) && (writes.Length == 0 || !after.GetProperty("lastTailHandled").GetBoolean()), label + "-real-native-tail138-not-cancelled");
                if (writes.Length > 0) Check(!after.GetProperty("last").GetProperty("HandledAfter").GetBoolean(), label + "-normal-current-frame-not-cancelled");
                var expected = Flatten(corpus.GetProperty("after"), corpus.GetProperty("offsets"));
                Check(EqualRows(after.GetProperty("native"), expected) && EqualRows(after.GetProperty("ssc"), expected), label + "-every-native-and-ssc-slot-matches-native-operation");
                if (writes.Length == 0) Check(EqualRows(before.GetProperty("native"), after.GetProperty("native")) && Delta("sends") == 0, label + "-native-no-op-does-not-create-network-writes");
                int[] offsets = after.GetProperty("offsets").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                foreach (var frame in writes)
                {
                    int slot = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(4, 2));
                    bool shouldRelay = slot < 59 || slot >= offsets[3] && slot < offsets[3] + 40;
                    var value = expected.EnumerateArray().Single(item => item.GetProperty("Slot").GetInt32() == slot);
                    var receipts = peer.InventoryUpdates.Skip(peerStart).Where(item => item.Player == actor.Slot && item.Slot == slot).ToArray();
                    Check(shouldRelay ? receipts.Any(item => item.Item == value.GetProperty("Type").GetInt32() && item.Stack == value.GetProperty("Stack").GetInt32() && item.Prefix == value.GetProperty("Prefix").GetInt32()) : receipts.Length == 0,
                        label + "-native-relay-policy-slot-" + slot);
                }
                Healthy(actor); observations.Add(new { label, nativeCorpus = corpus, before, after }); return after;
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, originalId = "v2:D03", observations,
                realTcpParserHookNativeSetter = true, separateNativeMethodProducer = true, actualClientNativeLoopConnected = false, stockClientGui = false,
                explicitGrantsArePreparation = true, fullAcquisitionProof = false, permanentBanQualificationAdded = false, wholeOriginalTaskComplete = false,
                remaining = "nearby-chest packet85 with native void-bag source locking/response reconciliation is separate; no full stock GUI or all item-source claim" }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool condition, string label) => host.Assert(condition, "m18-storage:" + label);
        async Task<LabClient> Actor(string name, bool existing)
        { var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); if (existing) await actor.Login(); else await actor.RegisterAndLogin();
            actor.PauseHeartbeat = true; Check(actor.Authenticated && actor.SscSlots.Count >= 350, name + "-real-auth-and-full-ssc"); return actor; }
        void Healthy(LabClient actor)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name"; query.Parameters.AddWithValue("$name", "acc:" + actor.Name);
            Check(actor.Authenticated && !actor.Closed && actor.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0, actor.Name + "-healthy-unbanned");
        }
        async Task Left(string label)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            { if (!(await Command("state", label)).GetProperty("currentActor").GetBoolean()) return; await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1)); }
            throw new TimeoutException("Prior native actor has not left: " + label);
        }
        async Task<JsonElement> Native(JsonElement state, int bank, string operation, string label)
        {
            string input = Path.Combine(directory, label + "-input.json"), output = Path.Combine(directory, label + "-native.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { bank, player = state.GetProperty("slot").GetInt32(), operation,
                inventory = state.GetProperty("inventory"), banks = state.GetProperty("banks"), provenance = "this-run actual server/native/SSC snapshot; initial grant matched full real S2C; independent native reconstruction, no connected native client" }));
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.GetFullPath(nativeProbeDll)); info.ArgumentList.Add("storage"); info.ArgumentList.Add(input); info.ArgumentList.Add(output);
            using var process = Process.Start(info) ?? throw new IOException("Native storage probe did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            await File.WriteAllTextAsync(Path.Combine(directory, label + "-native.log"), await stdout + Environment.NewLine + await stderr);
            Check(process.ExitCode == 0 && File.Exists(output), label + "-actual-native-method-process");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output)); var result = document.RootElement.Clone();
            Check(result.GetProperty("nativeDiffAndSerialization").GetBoolean() && !result.GetProperty("actualTcpConnection").GetBoolean() &&
                !result.GetProperty("stockExecutableOrGui").GetBoolean(), label + "-native-and-tcp-evidence-layers-explicit"); return result;
        }
        async Task<JsonElement> Command(string command, string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m18_storage " + command);
            string path = Path.Combine(host.ReportDirectory, "m18-storage-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                if (File.Exists(path)) try
                {
                    string text = await M17ContainerSortScenario.ReadSnapshotTextAsync(path); using var document = JsonDocument.Parse(text);
                    if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return document.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Fresh storage state missing: " + label);
        }
    }
    private static JsonElement Flatten(JsonElement state, JsonElement starts) => JsonSerializer.SerializeToElement(
        state.GetProperty("Inventory").EnumerateArray().Select(value => Row(value, value.GetProperty("Slot").GetInt32())).Concat(
            state.GetProperty("Banks").EnumerateArray().SelectMany((bank, index) => bank.EnumerateArray().Select(value => Row(value, starts[index].GetInt32() + value.GetProperty("Slot").GetInt32())))));
    private static object Row(JsonElement value, int slot) => new { Slot = slot, Type = value.GetProperty("Type").GetInt32(), Stack = value.GetProperty("Stack").GetInt32(),
        Prefix = value.GetProperty("Prefix").GetInt32(), Favorite = value.GetProperty("Favorite").GetBoolean() };
    private static bool EqualRows(JsonElement left, JsonElement right) => left.EnumerateArray().Zip(right.EnumerateArray()).All(pair =>
        new[] { "Slot", "Type", "Stack", "Prefix" }.All(key => pair.First.GetProperty(key).GetInt32() == pair.Second.GetProperty(key).GetInt32()) &&
        pair.First.GetProperty("Favorite").GetBoolean() == pair.Second.GetProperty("Favorite").GetBoolean()) && left.GetArrayLength() == right.GetArrayLength();
    private static bool ReceivedMatches(LabClient actor, JsonElement expected)
    {
        var received = actor.InventoryUpdates.Where(value => value.Player == actor.Slot).GroupBy(value => value.Slot).ToDictionary(group => group.Key, group => group.Last());
        return expected.EnumerateArray().All(value => received.TryGetValue(value.GetProperty("Slot").GetInt32(), out var current) &&
            current.Item == value.GetProperty("Type").GetInt32() && current.Stack == value.GetProperty("Stack").GetInt32() && current.Prefix == value.GetProperty("Prefix").GetInt32() &&
            actor.InventorySlotFlags.TryGetValue((actor.Slot, current.Slot), out byte flags) && ((flags & 1) != 0) == value.GetProperty("Favorite").GetBoolean());
    }
}
