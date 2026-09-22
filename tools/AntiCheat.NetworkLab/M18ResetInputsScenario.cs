using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M18ResetInputsScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host, Func<string, string, Task<LabClient>> reconnect)
    {
        string directory = Path.Combine(host.ReportDirectory, "m18-reset-inputs"); Directory.CreateDirectory(directory);
        string source = Path.Combine(host.RunDirectory, "tshock", "anticheat", "m18-reset-inputs", "reset-inputs.jsonl");
        var clients = new List<LabClient>(4); var checks = new List<object>(16);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")) && File.Exists(Path.Combine(host.RunDirectory, ".compatibility-isolated-test")), "owned-isolated-run");
            string[] plugins = Directory.GetFiles(Path.Combine(host.RunDirectory, "app", "ServerPlugins"), "*.dll").Select(Path.GetFileName).ToArray()!;
            Check(!File.Exists(Path.Combine(host.RunDirectory, "app", "ServerPlugins", "GameplayScaffold.dll")) &&
                plugins.All(name => name is "TShockAPI.dll" or "AntiCheat.Plugin.TShock.dll"), "no-scaffold-exact-staged-plugin-list");
            checks.Add(new { stagedPlugins = plugins });
            var peer = await Fresh("M18ResetPeer");
            var first = await Fresh("M18ResetActor"); long account = Account(first.Name);
            var firstState = await WaitState(account, first.Slot, 0, value => value.GetProperty("snapshotPresent").GetBoolean() &&
                !value.GetProperty("initialResetInputsAvailable").GetBoolean() && value.GetProperty("gap").GetString() != "none", "first-chat-login-legal-gap");
            Check(!Available(firstState), "first-chat-login-is-not-initial-reset-proof");
            Healthy(first); Healthy(peer); await OwnAction(first, peer, "first-chat-login");
            string uuid = first.Uuid; await first.DisposeAsync(); await peer.Drain(TimeSpan.FromMilliseconds(300));
            var second = await reconnect(first.Name, uuid); clients.Add(second); await second.Join();
            Check(second.Authenticated && second.SscSlots.Count == 350 && second.LoginAttempts == 0, "same-uuid-native-autologin-and-ssc-receipt");
            var secondState = await WaitState(account, second.Slot, firstState.GetProperty("sequence").GetInt64(), AvailableSummary, "same-uuid-actual-initial-reset");
            ValidateAvailable(secondState, account); Check(Session(firstState) != Session(secondState), "new-transport-has-new-session");
            Check(second.PacketCount(7) > 0 && second.PacketCount(49) > 0 && second.LoadoutUpdates.Any(x => x.Player == second.Slot) &&
                second.BuffLists.Any(x => x.Player == second.Slot), "actual-peer-received-world-ready-loadout-buffs");
            await OwnAction(second, peer, "same-uuid-initial-reset"); Healthy(second); Healthy(peer);
            await second.DisposeAsync(); await peer.Drain(TimeSpan.FromMilliseconds(300));
            var third = await reconnect(first.Name, uuid); clients.Add(third); await third.Join();
            var thirdState = await WaitState(account, third.Slot, secondState.GetProperty("sequence").GetInt64(),
                value => AvailableSummary(value) && value.GetProperty("session").GetRawText() != Session(secondState), "third-transport-fresh-reset");
            ValidateAvailable(thirdState, account);
            Check(Session(thirdState) != Session(secondState) && Session(thirdState) != Session(firstState), "third-generation-no-inherited-boundary");
            Check(third.Authenticated && third.SscSlots.Count == 350 && third.LoginAttempts == 0, "third-transport-actual-normal-autologin");
            await OwnAction(third, peer, "third-generation"); Healthy(third); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-cheat-incident");
            checks.Add(new { firstState, secondState, thirdState }); status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try
            {
            if (File.Exists(source)) await File.WriteAllTextAsync(Path.Combine(directory, "server-reset-inputs.jsonl"), await ReadShared(source));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, failure, checks,
                clients = clients.Select(client => client.Evidence()).ToArray(), syntheticLoopbackTcp = true, actualTShockSscAndHandshake = true,
                nativeHostRequired = "TShock+AntiCheat", scaffoldUsed = false, stockClientGui = false,
                clientStateDirectlyObserved = false, callbackMeansPeerDelivery = false, peerWireOrderCaptured = false,
                ordinaryDamageModelComplete = false, hardQualification = false,
                limits = "Production input entry only: real UUID authentication, native SSC sends and native8/12; no inferred stock-client application/clear or full attack-source closure. Existing method cancellation/overflow/socket tests remain a separate tier." }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { foreach (var client in clients) await client.DisposeAsync(); }
        }
        void Check(bool value, string label) => host.Assert(value, "m18-reset-inputs:" + label);
        async Task<LabClient> Fresh(string name)
        { var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); await actor.RegisterAndLogin(); Check(actor.Authenticated && actor.SscSlots.Count == 350, name + "-ordinary-auth-ssc"); return actor; }
        long Account(string name)
        {
            using var db = Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT ID FROM Users WHERE Username=$name";
            query.Parameters.AddWithValue("$name", name); return Convert.ToInt64(query.ExecuteScalar());
        }
        SqliteConnection Open()
        { var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open(); return db; }
        void Healthy(LabClient actor)
        {
            using var db = Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            query.Parameters.AddWithValue("$name", "acc:" + actor.Name);
            Check(actor.Authenticated && !actor.Closed && actor.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0, actor.Name + "-healthy-unbanned");
        }
        async Task OwnAction(LabClient actor, LabClient peer, string label)
        {
            int before = peer.Bubbles.Count;
            await actor.Send(120, writer => { writer.Write(actor.Slot); writer.Write((byte)0); });
            await actor.PingAsync();
            await peer.WaitUntil(() => peer.Bubbles.Skip(before).Any(value => value.Player == actor.Slot && value.Emote == 0), TimeSpan.FromSeconds(5));
            Check(peer.Bubbles.Skip(before).Any(value => value.Player == actor.Slot && value.Emote == 0), label + "-normal-own-emote-real-peer-receipt");
        }
        static bool AvailableSummary(JsonElement value) => value.GetProperty("initialResetInputsAvailable").ValueKind == JsonValueKind.True;
        static bool Available(JsonElement record) => AvailableSummary(record.GetProperty("summary"));
        static string Session(JsonElement record) => record.GetProperty("summary").GetProperty("session").GetRawText();
        void ValidateAvailable(JsonElement record, long account)
        {
            var value = record.GetProperty("summary");
            Check(value.GetProperty("boundAccount").GetInt64() == account && value.GetProperty("snapshotAccount").GetInt64() == account, "actual-account-binding");
            Check(value.GetProperty("nativeHost").GetBoolean() && value.GetProperty("collectorHealthy").GetBoolean() && value.GetProperty("nativeTransportIdentityVerified").GetBoolean(), "actual-supported-native-host-and-provider");
            Check(value.GetProperty("ownSlots").GetInt32() == 350 && value.GetProperty("completedSlots").GetInt32() == 350 && value.GetProperty("loadout").ValueKind == JsonValueKind.Number, "complete-finite-own-ssc-output");
            Check(value.GetProperty("worldInfoSequence").GetInt64() < value.GetProperty("firstSlotSequence").GetInt64() && value.GetProperty("lastSlotSequence").GetInt64() < value.GetProperty("readySequence").GetInt64(), "native-output-group-sequence");
            Check(value.GetProperty("sectionAccepted").GetBoolean() && value.GetProperty("spawnAccepted").GetBoolean() && value.GetProperty("requiredWritesCompleted").GetBoolean() && value.GetProperty("historicalResetSequenceObserved").GetBoolean() && !value.GetProperty("sscRestoreInProgress").GetBoolean() && value.GetProperty("gap").GetString() == "none", "actual-handshake-and-completion-boundary");
        }
        async Task<JsonElement> WaitState(long account, int slot, long afterSequence, Func<JsonElement, bool> predicate, string label)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                if (File.Exists(source))
                {
                    string[] lines;
                    try { lines = (await ReadShared(source)).Split('\n', StringSplitOptions.RemoveEmptyEntries); } catch (IOException) { lines = []; }
                    foreach (string line in lines.Reverse())
                    {
                        JsonDocument doc; try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                        using (doc)
                        {
                            var record = doc.RootElement;
                            Check(record.GetProperty("kind").GetString() == "snapshot", "diagnostic-without-truncation");
                            var value = record.GetProperty("summary");
                            if (record.GetProperty("sequence").GetInt64() <= afterSequence || value.GetProperty("boundAccount").ValueKind != JsonValueKind.Number || value.GetProperty("boundAccount").GetInt64() != account || value.GetProperty("session").GetProperty("slot").GetInt32() != slot) continue;
                            if (predicate(value)) { var found = record.Clone(); checks.Add(new { label, record = found }); return found; }
                        }
                    }
                }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("M18 reset diagnostic: " + label);
        }
        static async Task<string> ReadShared(string path)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream); return await reader.ReadToEndAsync();
        }
    }
}
