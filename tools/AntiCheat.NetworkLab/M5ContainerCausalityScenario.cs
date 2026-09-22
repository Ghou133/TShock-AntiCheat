using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Real TCP against the ordinary server plugin set. Historical metadata locates existing chests;
// every gameplay value below comes from this run's complete, ordinary S2C32 chest response.
internal static class M5ContainerCausalityScenario
{
    private const string Rule = "B4.ContainerAuthorization";
    private const string Version = "1.1.0";
    private const string SourceRun = "artifacts/network-runs/network-adapter-20260910T042102521Z";
    private const string MetadataSha256 = "5EEA51BE563C7D63A86F5D5F60E5FFB4DB5F1D09674F34F230912AC743E675E9";
    private const string SourceInputsSha256 = "71438B2CA588250D29340DFA43CFE074353F77723CB23818B247F2B092F8CDBF";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record ChestMetadata(short Id, short X, short Y, int MaxItems, JsonElement HistoricalItems);
    private sealed record SlotValue(byte Slot, short Stack, byte Prefix, short Item);
    private sealed record GroupSnapshot(string Name, string Parent, string Commands);
    private sealed record Snapshot(string Label, string Client, short Chest, int ResponseStart, int ResponseEnd,
        DateTimeOffset ReceivedUtc, SlotValue[] Slots);

    public static async Task RunAsync(M4ObservedReplayHarness host, string expectedScope = "TestLab")
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string root = Directory.GetParent(run)?.Parent?.FullName ?? throw new InvalidOperationException("Missing repository root.");
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m5-container-causality");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string journal = Path.Combine(run, "tshock", "anticheat", "enforcement");
        var clients = new List<LabClient>(3);
        var snapshots = new List<Snapshot>(8);
        var frames = new List<object>(12);
        var timer = Stopwatch.StartNew();
        string status = "failed", failure = "";
        long account = 0, initialBanCount = 0;
        int logStart = host.ConsoleLines().Length;
        try
        {
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")), "owned-isolated-run-marker");
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(expectedScope is "TestLab" or "Production", "supported-explicit-execution-scope");
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "tshock", "anticheat.json"))))
                Check(config.RootElement.GetProperty("ExecutionScope").GetString() == expectedScope, "actual-" + expectedScope + "-scope");
            var pluginFiles = Directory.GetFiles(Path.Combine(run, "app", "ServerPlugins"), "*.dll")
                .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Check(pluginFiles.SequenceEqual(new[] { "AntiCheat.Plugin.TShock.dll", "TShockAPI.dll" }),
                "only-TShock-and-AntiCheat-plugin-files-no-scaffold");
            initialBanCount = Scalar("SELECT COUNT(*) FROM PlayerBans", "");
            Check(BanCount("M5B4Observer") == 0 && BanCount("M5B4FirstProof") == 0,
                "fresh-subject-and-observer-with-existing-ban-baseline-recorded");

            string metadataPath = Path.Combine(root, SourceRun, "m5-effects", "resize-targets-before.json");
            string sourceInputsPath = Path.Combine(root, SourceRun, "inputs.json");
            Check(Hash(metadataPath) == MetadataSha256 && Hash(sourceInputsPath) == SourceInputsSha256,
                "pinned-historical-location-metadata-and-source-inputs");
            using var sourceInputs = JsonDocument.Parse(await File.ReadAllTextAsync(sourceInputsPath));
            using var currentInputs = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.ReportDirectory, "inputs.json")));
            string? worldHash = sourceInputs.RootElement.GetProperty("worldSourceSha256").GetString();
            Check(worldHash is { Length: 64 } && worldHash == currentInputs.RootElement.GetProperty("worldSourceSha256").GetString(),
                "historical-and-current-world-source-hashes-identical");
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
            // The closest pair in this fixed, audited metadata is 8.54 tiles apart. Both fit well
            // inside the native 32-tile range rectangle from one fixed control position.
            var a = ReadChest(29);
            var b = ReadChest(44);
            float positionX = (a.X + b.X) * 8f, positionY = (a.Y + b.Y) * 8f;
            Check(a.Id != b.Id && a.MaxItems == 40 && b.MaxItems == 40 &&
                Math.Abs(a.X - positionX / 16) < 16 && Math.Abs(b.X - positionX / 16) < 16 &&
                Math.Abs(a.Y - positionY / 16) < 16 && Math.Abs(b.Y - positionY / 16) < 16,
                "distinct-normal-capacity-targets-with-one-nearby-position");
            await Write("source-and-targets.json", new
            {
                historicalMetadata = SourceRun + "/m5-effects/resize-targets-before.json", metadataSha256 = MetadataSha256,
                historicalInputsSha256 = SourceInputsSha256, worldSourceSha256 = worldHash,
                a, b, positionX, positionY, pluginFiles,
                historicalValuesAreCurrentState = false,
                targetAcceptance = "Each chest must actually return S2C33 and all 40 S2C32 slots to an ordinary default account. No key, permission, chest fixture or snapshot command is supplied."
            });

            var observer = await Actor("M5B4Observer");
            var actor = await Actor("M5B4FirstProof");
            account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            Check(account > 0, "server-account-root-established");
            var groups = ReadGroups();
            Check(groups.Count == 2 && groups.All(x => !x.Commands.Split(',').Any(p =>
                p.Contains('*') || p.StartsWith("tshock.ignore.", StringComparison.Ordinal) || p.StartsWith("anticheat", StringComparison.OrdinalIgnoreCase))),
                "default-and-guest-groups-have-no-wildcard-ignore-or-anticheat-permissions");
            await Write("account-groups.json", groups);
            await observer.MoveTo(positionX, positionY);
            await observer.PingAsync();
            await actor.MoveTo(positionX, positionY);
            await actor.PingAsync();

            var initialA = await OpenSnapshot(observer, a, "observer-initial-A");
            await Close(observer, a);
            var initialB = await OpenSnapshot(observer, b, "observer-initial-B");
            await Close(observer, b);
            CheckHealthy(observer, "ordinary-observer-opened-both-existing-chests");

            var actorA = await OpenSnapshot(actor, a, "actor-open-A");
            Check(Same(initialA, actorA), "actor-A-response-matches-current-observer-A-response");
            SlotValue echoA = actorA.Slots[0];
            await SendSlot(actor, a.Id, echoA, "normal-same-target32A-confirmation");
            await actor.PingAsync();
            CheckHealthy(actor, "normal-confirmed-A-write-unpunished");

            int raceStart = host.ConsoleLines().Length;
            int bResponseStart = actor.ChestItems.Count;
            int bAckStart = actor.PacketCount(33);
            Check(actor.ActiveChest == a.Id, "client-still-displays-A-before-sending31B");
            byte[] openB = LabClient.Packet(31, w => { w.Write(b.X); w.Write(b.Y); });
            byte[] oldA = SlotPacket(a.Id, echoA);
            frames.Add(new { label = "legal-switch-race", packets = new[] { Convert.ToHexString(openB), Convert.ToHexString(oldA) },
                ordering = "31B then unchanged32A in one TCP write; no S2C response processed between these C2S frames", oldDisplayedChest = actor.ActiveChest });
            await actor.SendBatch(openB, oldA);
            var actorB = await ReceiveSnapshot(actor, b, "actor-switch-B", bResponseStart, bAckStart);
            await actor.PingAsync();
            await Until(() => host.ConsoleLines().Skip(raceStart).Any(x => IsRuleLine(x, account) &&
                x.Contains("verdict=Unknown ") && x.Contains("reason=normal-container-switch-window ") &&
                x.Contains("canceled=False", StringComparison.OrdinalIgnoreCase)), "legal-race-actually-reported-Unknown-not-canceled");
            CheckHealthy(actor, "31B-plus-old32A-does-not-punish-first-legitimate-race");
            Check(!host.ConsoleLines().Skip(raceStart).Any(x => x.Contains("ANTICHEAT_INCIDENT ") &&
                x.Contains("accountId=" + account + " ")), "legal-race-produces-no-subject-incident");
            Check(Same(initialB, actorB), "real-current-B-snapshot-preserved-during-switch");

            // This ordered same-target input closes B's inbound causal acknowledgement. A ping
            // afterwards is only an observable processing barrier, never a lease proof premise.
            await SendSlot(actor, b.Id, actorB.Slots[0], "normal-first-same-target32B-confirmation");
            await actor.PingAsync();
            CheckHealthy(actor, "first32B-confirmation-unpunished");
            var beforeProofA = await OpenSnapshot(observer, a, "observer-A-before-first-proof");
            Check(Same(initialA, beforeProofA), "old-A-race-left-complete-actual-A-content-unchanged");

            // Distinct contents make the blocked-write observation meaningful. Ordinary Wood in
            // a real empty slot avoids a separate stack/prefix predicate on the proof frame.
            SlotValue empty = beforeProofA.Slots.FirstOrDefault(x => x.Item == 0 && x.Stack == 0)
                ?? throw new InvalidOperationException("No actual empty A slot for the attributable write attempt.");
            var attempted = new SlotValue(empty.Slot, 1, 0, 9);
            byte[] proof = SlotPacket(a.Id, attempted);
            int proofStart = host.ConsoleLines().Length;
            int batchesBeforeProof = actor.ProofBatches;
            int observerUpdatesBeforeProof = observer.ChestItems.Count;
            frames.Add(new { label = "first-proven-mismatch32A", packet = Convert.ToHexString(proof),
                previousValue = empty, attemptedValue = attempted, latestAcknowledgedTarget = b.Id });
            await actor.SendBatch(proof);
            await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed, TimeSpan.FromSeconds(8));
            Check(actor.ProofBatches == batchesBeforeProof + 1 && actor.DisconnectReason == "AntiCheat proven violation.",
                "first-post-confirmation-mismatch-disconnects-with-one-proof-frame");
            await Until(() => BanCount(actor.Name) == 1, "single-account-permanent-ban-persisted");
            await Until(() => IncidentLines(proofStart, account).Length == 1, "exactly-one-canceled-and-revoked-B4-incident");
            host.RecordProvenAccount(actor.Name, account, Rule);
            await observer.Drain(TimeSpan.FromMilliseconds(300));
            Check(!observer.ChestItems.Skip(observerUpdatesBeforeProof).Any(x => x.Chest == a.Id && x.Slot == attempted.Slot &&
                x.Stack == attempted.Stack && x.Prefix == attempted.Prefix && x.Item == attempted.Item),
                "attempted-A-write-not-exported-to-real-observer");
            await Close(observer, a);
            var afterProofA = await OpenSnapshot(observer, a, "observer-A-after-first-proof");
            Check(Same(beforeProofA, afterProofA), "all-forty-current-A-slots-unchanged-after-first-proof");
            CheckHealthy(observer, "normal-observer-not-attributed-or-punished");
            await Close(observer, a);
            await actor.DisposeAsync();

            var applied = await AppliedIntent(account);
            using (var facts = JsonDocument.Parse(applied.GetProperty("Intent").GetProperty("Evidence").GetProperty("PredicateFactsJson").GetString()!))
            {
                Check(facts.RootElement.GetProperty("leaseConfirmed").GetString() == "True" &&
                    facts.RootElement.GetProperty("serverChestAligned").GetString() == "True" &&
                    facts.RootElement.GetProperty("nativeContainerProtocol").GetString() == "True" &&
                    facts.RootElement.GetProperty("containerId").GetString() == a.Id.ToString() &&
                    facts.RootElement.GetProperty("openContainerId").GetString() == b.Id.ToString(),
                    "durable-proof-record-carries-real-ordered-B-grant-and-A-mismatch");
            }
            await Write("applied-intent.json", applied);
            await Task.Delay(200);
            var reconnect = await host.Connect(actor.Name); clients.Add(reconnect);
            await reconnect.Join(); await reconnect.LoginAndExpectRejected();
            Check(reconnect.IsBanRejection, "same-account-real-TCP-reconnect-rejected");
            Check(BanCount(actor.Name) == 1 && BanCount(observer.Name) == 0,
                "reconnect-retains-one-permanent-subject-ban-and-no-observer-ban");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans", "") == initialBanCount + 1,
                "exactly-one-new-ban-above-existing-qualified-rule-baseline");
            status = "passed";

            ChestMetadata ReadChest(short id)
            {
                var chest = metadata.RootElement.GetProperty("toolTargetChests").EnumerateArray().Single(x => x.GetProperty("id").GetInt16() == id);
                return new(id, chest.GetProperty("x").GetInt16(), chest.GetProperty("y").GetInt16(),
                    chest.GetProperty("maxItems").GetInt32(), chest.GetProperty("items").Clone());
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            await Write("frames.json", frames);
            await Write("snapshots.json", snapshots);
            await Write("summary.json", new
            {
                status, failure, rule = Rule, version = Version, expectedScope, initialBanCount,
                durationMs = timer.Elapsed.TotalMilliseconds, account,
                actualLoopbackTcp = true, actualClientUiThisRun = false, scaffoldLoaded = false,
                historicalMetadataUsedOnlyForLocations = true, gameplaySnapshotsFromCurrentS2C32 = true,
                clients = clients.Select(x => x.Evidence()), console = host.ConsoleLines().Skip(logStart).Where(x =>
                    x.Contains("ANTICHEAT_RULE_INPUT rule=" + Rule) || x.Contains("ANTICHEAT_INCIDENT ")).ToArray()
            });
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m5-container-causality:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(report, name), JsonSerializer.Serialize(value, Json));
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        long BanCount(string name) => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + name);
        void CheckHealthy(LabClient actor, string label) => Check(actor.Authenticated && !actor.Closed &&
            actor.DisconnectReason is null && BanCount(actor.Name) == 0, label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client);
            await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350 &&
                Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", name) == 1,
                name + "-authenticated-with-native-SSC-and-default-group");
            return client;
        }
        List<GroupSnapshot> ReadGroups()
        {
            using var db = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "SELECT GroupName, Parent, Commands FROM GroupList WHERE GroupName IN ('default','guest') ORDER BY GroupName";
            using var reader = command.ExecuteReader(); var result = new List<GroupSnapshot>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetString(2)));
            Check(result.Count == 2 && result[0].Parent == "guest" && result[1].Parent == "", "ordinary-default-to-guest-inheritance");
            return result;
        }
        async Task<Snapshot> OpenSnapshot(LabClient client, ChestMetadata chest, string label)
        {
            int start = client.ChestItems.Count, ack = client.PacketCount(33);
            await client.Send(31, w => { w.Write(chest.X); w.Write(chest.Y); });
            return await ReceiveSnapshot(client, chest, label, start, ack);
        }
        async Task<Snapshot> ReceiveSnapshot(LabClient client, ChestMetadata chest, string label, int start, int priorAck)
        {
            await client.WaitUntil(() => client.PacketCount(33) > priorAck && client.ActiveChest == chest.Id &&
                client.ChestItems.Skip(start).Where(x => x.Chest == chest.Id).Select(x => x.Slot).Distinct().Count() == chest.MaxItems,
                TimeSpan.FromSeconds(8));
            var response = client.ChestItems.Skip(start).Where(x => x.Chest == chest.Id).ToArray();
            var values = response.GroupBy(x => x.Slot).OrderBy(x => x.Key)
                .Select(x => x.Last()).Select(x => new SlotValue(x.Slot, x.Stack, x.Prefix, x.Item)).ToArray();
            Check(values.Select(x => (int)x.Slot).SequenceEqual(Enumerable.Range(0, chest.MaxItems)), label + "-real33-and-complete32-slots");
            var snapshot = new Snapshot(label, client.Name, chest.Id, start, client.ChestItems.Count, DateTimeOffset.UtcNow, values);
            snapshots.Add(snapshot); await Write(label + ".json", snapshot);
            return snapshot;
        }
        async Task Close(LabClient client, ChestMetadata chest)
        {
            await client.Send(33, w => { w.Write((short)-1); w.Write(chest.X); w.Write(chest.Y); w.Write((byte)0); });
            await client.PingAsync();
        }
        async Task SendSlot(LabClient client, short chest, SlotValue value, string label)
        {
            frames.Add(new { label, packet = Convert.ToHexString(SlotPacket(chest, value)), source = "current-run ordinary S2C32 snapshot" });
            await client.Send(32, w => WriteSlot(w, chest, value));
        }
        string[] IncidentLines(int start, long actorAccount) => host.ConsoleLines().Skip(start).Where(x =>
            x.Contains("ANTICHEAT_INCIDENT rule=" + Rule + " ") && x.Contains("accountId=" + actorAccount + " ") &&
            x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) && x.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)).ToArray();
        async Task Until(Func<bool> condition, string label)
        {
            var wait = Stopwatch.StartNew();
            while (!condition() && wait.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
            Check(condition(), label);
        }
        async Task<JsonElement> AppliedIntent(long actorAccount)
        {
            JsonElement? found = null;
            await Until(() =>
            {
                var matches = new List<JsonElement>(1);
                foreach (var path in Directory.GetFiles(journal, "*.json"))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(path));
                        if (document.RootElement.GetProperty("Intent").GetProperty("AccountId").GetInt64() == actorAccount)
                            matches.Add(document.RootElement.Clone());
                    }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
                }
                if (matches.Count != 1 || !matches[0].GetProperty("Applied").GetBoolean()) return false;
                found = matches[0]; return true;
            }, "one-applied-durable-intent-for-subject");
            var evidence = found!.Value.GetProperty("Intent").GetProperty("Evidence");
            Check(evidence.GetProperty("RuleId").GetString() == Rule && evidence.GetProperty("RuleVersion").GetString() == Version &&
                evidence.GetProperty("PacketId").GetInt32() == 32 && evidence.GetProperty("Preconditions").GetProperty("Complete").GetBoolean(),
                "version1.1.0-complete-preconditions-recorded-for-first-packet32-proof");
            if (expectedScope == "Production")
                Check(evidence.GetProperty("Qualification").GetInt32() == 2 &&
                    evidence.GetProperty("AuditReference").GetString() == "docs/m14-production-qualification.md",
                    "durable-first-proof-record-is-actually-ProductionQualified");
            return found.Value;
        }
    }

    private static bool IsRuleLine(string text, long account) => text.Contains("ANTICHEAT_RULE_INPUT rule=" + Rule + " ") && text.Contains("accountId=" + account + " ");
    private static bool Same(Snapshot left, Snapshot right) => left.Chest == right.Chest && left.Slots.SequenceEqual(right.Slots);
    private static byte[] SlotPacket(short chest, SlotValue value) => LabClient.Packet(32, w => WriteSlot(w, chest, value));
    private static void WriteSlot(BinaryWriter writer, short chest, SlotValue value)
    { writer.Write(chest); writer.Write(value.Slot); writer.Write(value.Stack); writer.Write(value.Prefix); writer.Write(value.Item); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
