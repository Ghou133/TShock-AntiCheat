using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real TCP open/quick-stack/permission lifecycle plus separately identified
/// native SortChest corpus generation. Does not claim an attached stock client or GUI.</summary>
internal static class M17ContainerSortScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host, string nativeProbeDll)
    {
        string directory = Path.Combine(host.ReportDirectory, "m17-container-sort"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var observations = new List<object>(8);
        string status = "failed", failure = ""; int startLog = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) &&
                File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-locked-runtime");
            Check(File.Exists(nativeProbeDll), "independent-native-producer-tool-present");
            await host.FixtureSnapshot(); var actor = await Actor("M17SortActor"); var peer = await Actor("M17SortPeer");
            var preparedAt = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m17_sort " + actor.Name);
            var initial = await Fresh(preparedAt, "initial");
            Check(initial.GetProperty("witnessInstalled").GetBoolean() && initial.GetProperty("itemTableReady").GetBoolean(), "actual-product-catalog-and-passive-witnesses");
            var metadata = initial.GetProperty("chest"); short chest = metadata.GetProperty("id").GetInt16(), x = metadata.GetProperty("x").GetInt16(), y = metadata.GetProperty("y").GetInt16();
            Check(metadata.GetProperty("maxItems").GetInt32() == 40, "native-default-chest-domain");
            await Policy(preparedAt, "prepare", 1, "initial-policy-confirmed");
            await actor.MoveTo((x - 2) * 16, (y - 1) * 16); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(100));

            // Quick Stack has its own real native server operation, without an open chest.
            var quickBefore = await State("quick-before"); int quickPeer = peer.ChestItems.Count;
            await actor.Send(85, writer => { writer.Write(1); writer.Write((short)10); writer.Write(false); });
            await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(150));
            var quickAfter = await State("quick-after");
            Check(Wood(quickAfter) == Wood(quickBefore) + 7 && Item(quickAfter, "Inventory", 10).GetProperty("Stack").GetInt32() == 0,
                "native-quick-stack-source-and-target-conservation");
            Check(peer.ChestItems.Skip(quickPeer).Any(item => item.Chest == chest), "quick-stack-real-peer-receipt");
            observations.Add(new { label = "quick-stack", syntheticRequest = true, nativeServerExecution = true, before = quickBefore, after = quickAfter });

            var firstSnapshot = await Open("first-open");
            var first = await NativeSort(firstSnapshot, "first-native-sort");
            Check(first.GetProperty("frames").GetArrayLength() > 1, "native-multiple-changed-slot-sequence");
            await Apply(first, "first-sort", "allow");
            var stableSnapshot = await Reopen("sorted-reopen");
            var stable = await NativeSort(stableSnapshot, "already-sorted-native-control");
            Check(stable.GetProperty("frames").GetArrayLength() == 0, "native-already-sorted-produces-no-writes");

            await host.ConsoleCommand("qa_m17_sort_add_pending"); await actor.PingAsync();
            var pendingSnapshot = await Reopen("pending-open");
            var pending = await NativeSort(pendingSnapshot, "pending-native-sort");
            Check(pending.GetProperty("frames").GetArrayLength() > 0, "native-pending-sort-has-real-changes");
            var deniedAt = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m17_sort_policy deny");
            await Policy(deniedAt, "deny", 2, "permission-denied-confirmed");
            await Apply(pending, "permission-revoked-sort", "product-block");
            var restoredAt = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m17_sort_policy restore");
            await Policy(restoredAt, "restore", 3, "permission-restored-confirmed");
            var restoredSnapshot = await Reopen("restored-open");
            var restored = await NativeSort(restoredSnapshot, "restored-native-sort");
            await Apply(restored, "same-account-restored-sort", "allow");

            // A close changes the live authorization context. Its delayed32 is a normal race
            // counterexample: retain TShock's BLOCK; do not promote it to permanent cheating.
            await Close();
            var delayed = restored.Clone();
            await Apply(delayed, "delayed-after-native-close", "core-block", firstFrameOnly: true);
            await Open("final-normal-reopen"); Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(startLog).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-account-cheat-verdict");
            status = "passed";

            async Task Policy(DateTimeOffset requested, string action, int revision, string label)
            {
                var policy = await Fresh(requested, label, "m17-container-sort-policy-latest.json");
                bool denied = action == "deny";
                Check(policy.GetProperty("actor").GetString() == actor.Name && policy.GetProperty("chest").GetInt16() == chest &&
                    policy.GetProperty("policyAction").GetString() == action && policy.GetProperty("policyRevision").GetInt32() == revision,
                    label + "-fresh-specific-policy-ack");
                Check(policy.GetProperty("regionProtectChests").GetBoolean() && policy.GetProperty("allowed").GetBoolean() == !denied &&
                    (denied ? !string.IsNullOrEmpty(policy.GetProperty("denyRegion").GetString()) : policy.GetProperty("denyRegion").ValueKind == JsonValueKind.Null),
                    label + "-actual-native-permission");
                observations.Add(new { label, policy });
            }

            async Task<JsonElement> Open(string label)
            {
                int start = actor.ChestItems.Count, ack = actor.PacketCount(33);
                await actor.Send(31, writer => { writer.Write(x); writer.Write(y); });
                await actor.WaitUntil(() => actor.ActiveChest == chest && actor.PacketCount(33) > ack && actor.ChestItems.Skip(start)
                    .Where(item => item.Chest == chest).Select(item => item.Slot).Distinct().Count() == 40, TimeSpan.FromSeconds(8));
                var values = actor.ChestItems.Skip(start).Where(item => item.Chest == chest).GroupBy(item => item.Slot).OrderBy(group => group.Key)
                    .Select(group => group.Last()).Select(item => new { slot = (int)item.Slot, type = (int)item.Item, stack = (int)item.Stack, prefix = (int)item.Prefix, favorite = false }).ToArray();
                Check(values.Length == 40 && values.Select(item => item.slot).SequenceEqual(Enumerable.Range(0, 40)), label + "-complete-real-s2c32");
                var snapshot = JsonSerializer.SerializeToElement(new { chest, items = values, provenance = "this-run complete real S2C32 after native31/33 chest-open exchange; favorite is absent on protocol32" });
                await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), snapshot.GetRawText()); return snapshot;
            }
            async Task Close()
            { await actor.Send(33, writer => { writer.Write((short)-1); writer.Write(x); writer.Write(y); writer.Write((byte)0); }); await actor.PingAsync(); }
            async Task<JsonElement> Reopen(string label) { await Close(); return await Open(label); }
            async Task<JsonElement> NativeSort(JsonElement input, string label)
            {
                string source = Path.Combine(directory, label + "-input.json"), output = Path.Combine(directory, label + "-output.json");
                await File.WriteAllTextAsync(source, input.GetRawText());
                var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(Path.GetFullPath(nativeProbeDll)); info.ArgumentList.Add(source); info.ArgumentList.Add(output);
                using var process = Process.Start(info) ?? throw new IOException("Native sorting probe did not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                await File.WriteAllTextAsync(Path.Combine(directory, label + ".log"), await stdout + Environment.NewLine + await stderr);
                Check(process.ExitCode == 0 && File.Exists(output), label + "-real-native-sort-process");
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output)); var result = document.RootElement.Clone();
                Check(result.GetProperty("actualSerialization").GetBoolean() && !result.GetProperty("liveTcpConnection").GetBoolean() &&
                    result.GetProperty("nativeEntry").GetString() == "Terraria.UI.ItemSorting.SortChest", label + "-evidence-layer-explicit");
                return result;
            }
            async Task Apply(JsonElement corpus, string label, string expected, bool firstFrameOnly = false)
            {
                var frames = corpus.GetProperty("frames").EnumerateArray().Select(value => Convert.FromHexString(value.GetString()!)).ToArray();
                if (firstFrameOnly) frames = frames.Take(1).ToArray();
                Check(frames.Length is > 0 and <= 40 && frames.All(frame => frame.Length == 11 && frame[2] == 32), label + "-bounded-native-frames");
                await peer.Drain(TimeSpan.FromMilliseconds(100)); var before = await State(label + "-before"); int peerStart = peer.ChestItems.Count;
                await actor.SendBatch(frames.SelectMany(frame => frame).ToArray()); await actor.PingAsync(); await peer.Drain(TimeSpan.FromMilliseconds(150));
                var after = await State(label + "-after"); long Delta(string key) => after.GetProperty(key).GetInt64() - before.GetProperty(key).GetInt64();
                Check(Delta("rawRequests") == frames.Length && after.GetProperty("witnessFaults").GetInt64() == 0, label + "-complete-real-raw-requests");
                if (expected == "allow")
                {
                    Check(Delta("coreEntries") == frames.Length && Delta("nativeWrites") >= frames.Length && Delta("sends") == frames.Length, label + "-real-core-native-and-forward");
                    var target = corpus.GetProperty("after").EnumerateArray().ToArray();
                    for (int slot = 0; slot < 40; slot++) Check(EqualItem(Item(after, "Chest", slot), target[slot]), label + "-actual-chest-slot-" + slot);
                    foreach (var frame in frames)
                    {
                        int slot = frame[5]; var item = target[slot];
                        Check(peer.ChestItems.Skip(peerStart).Any(value => value.Chest == chest && value.Slot == slot && value.Item == item.GetProperty("Type").GetInt32() &&
                            value.Stack == item.GetProperty("Stack").GetInt32() && value.Prefix == item.GetProperty("Prefix").GetInt32()), label + "-actual-peer-slot-" + slot);
                    }
                }
                else
                {
                    Check(Delta("coreEntries") == (expected == "core-block" ? frames.Length : 0) && Delta("nativeWrites") == 0 && Delta("sends") == 0 &&
                        after.GetProperty("lastRaw").GetProperty("HandledAfter").GetBoolean(), label + "-correct-existing-cancel-boundary");
                    Check(before.GetProperty("assets").GetRawText() == after.GetProperty("assets").GetRawText() && !peer.ChestItems.Skip(peerStart).Any(item => item.Chest == chest), label + "-stored-assets-and-peer-unchanged");
                }
                Healthy(actor); observations.Add(new { label, expected, nativeCorpus = corpus, before, after,
                    serverRejectedDoesNotProveClientRollback = expected != "allow", realStockClient = false, actualTcpFrames = frames.Select(Convert.ToHexString).ToArray() });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, originalId = "v2:C05", observations,
                nativeMethodCorpus = true, syntheticLoopbackTcp = true, stockClientGui = false, clientNativeMethodInSeparateProcess = true,
                permanentBanQualificationAdded = false, wholeOriginalTaskComplete = false }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool condition, string label) => host.Assert(condition, "m17-container-sort:" + label);
        static JsonElement Item(JsonElement state, string kind, int slot) => state.GetProperty("assets").GetProperty(kind).EnumerateArray().Single(value => value.GetProperty("Slot").GetInt32() == slot);
        static int Wood(JsonElement state) => state.GetProperty("assets").GetProperty("Chest").EnumerateArray().Where(item => item.GetProperty("Type").GetInt32() == 9).Sum(item => item.GetProperty("Stack").GetInt32());
        static bool EqualItem(JsonElement left, JsonElement right) => new[] { "Type", "Stack", "Prefix" }.All(key => left.GetProperty(key).GetInt32() == right.GetProperty(key).GetInt32());
        async Task<LabClient> Actor(string name)
        { var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin(); Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-normal-auth-and-ssc"); return client; }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name"; query.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0, client.Name + "-healthy-unbanned");
        }
        async Task<JsonElement> State(string label)
        { var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m17_sort_state"); return await Fresh(requested, label); }
        async Task<JsonElement> Fresh(DateTimeOffset requested, string label, string fileName = "m16-item-structure-state-latest.json")
        {
            string path = Path.Combine(host.ReportDirectory, fileName); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                if (File.Exists(path)) try
                {
                    string text = await ReadSnapshotTextAsync(path); using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                    { await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), text); return doc.RootElement.Clone(); }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Fresh C05 witness missing: " + label + " (" + fileName + ")");
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
