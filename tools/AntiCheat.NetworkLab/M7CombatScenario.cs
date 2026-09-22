using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Actual isolated TCP through the ordinary plugin set. The frames are synthetic protocol inputs;
/// native producer and GUI evidence remain separately identified.</summary>
internal static class M7CombatScenario
{
    private const string Rule = "C7.WoodenArrowDamageProjection";
    private const string LegacyRule = "C6.WoodenArrowDamageEscalation";

    public static async Task RunAsync(M4ObservedReplayHarness host, LabClient observer, string expectedScope = "TestLab")
    {
        if (expectedScope is not ("TestLab" or "Production"))
            throw new ArgumentOutOfRangeException(nameof(expectedScope), "C7 validation requires TestLab or Production.");
        bool testLab = expectedScope == "TestLab";
        string directory = Path.Combine(host.ReportDirectory, "m7-combat"); Directory.CreateDirectory(directory);
        string database = Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite");
        var clients = new List<LabClient>(3); var frames = new List<object>(12);
        var json = new JsonSerializerOptions { WriteIndented = true };
        int logStart = host.ConsoleLines().Length;
        string status = "failed", failure = ""; long account = 0;
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-isolated-run");
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(host.RunDirectory, "tshock", "anticheat.json"))))
                Check(config.RootElement.GetProperty("ExecutionScope").GetString() == expectedScope, "actual-" + expectedScope + "-scope");

            var legal = await Actor("M7ArrowBoundary");
            long legalAccount = Account(legal.Name);
            uint firstHigh = LabClient.ProjectileKey(legal.Slot, 301, 7);
            await ClaimAndObserve(legal, firstHigh, 1005, "first1005-current-uncovered");
            await Outcome(legalAccount, Rule, "Unknown", "arrow-projection-lifecycle-unavailable");
            await ClaimAndObserve(legal, firstHigh, 1005, "same1005-projection-context-actually-ready");
            await Outcome(legalAccount, Rule, "Pass", "arrow-damage-has-native-int32-preimage");
            CheckHealthy(legal, "first1005-does-not-invent-a-complete-source-upper-bound");
            if (testLab) Check(host.ConsoleLines().Any(x => x.Contains("rule=" + LegacyRule + " ") &&
                x.Contains("accountId=" + legalAccount + " ") && x.Contains("canonicalSources=43 finalDamageBound=unproved") &&
                x.Contains("allowedDamageUnion=supportedContains=") && x.Contains(";allowed=all-int16;complete=False")),
                "first1005-reaches-existing-actual-candidate-source-producer");

            uint wrap = LabClient.ProjectileKey(legal.Slot, 302, 7);
            await ClaimAndObserve(legal, wrap, 4, "native-wrap-counterexample-first4");
            await ClaimAndObserve(legal, wrap, 16385, "native-wrap-counterexample-after-reflection16385");
            CheckHealthy(legal, "4-to16385-counterexample-remains-allowed");
            if (testLab) await Outcome(legalAccount, LegacyRule, "Unknown", "arrow-native-internal-int16-range-unproved");
            // Keep the same account: a native wrap must not become a session-lifetime disable of C7.
            // The adapter test invokes Tick explicitly; this TCP wait also allows ordinary server updates between actions.
            await Task.Delay(250);
            var actor = legal; account = legalAccount;
            uint key = LabClient.ProjectileKey(actor.Slot, 303, 7);
            await ClaimAndObserve(actor, key, 9, "first9-native-relay-witness");
            await ClaimAndObserve(actor, key, 9, "same9-actual-complete-context-pass");
            await Outcome(account, Rule, "Pass", "arrow-damage-has-native-int32-preimage");
            CheckHealthy(actor, "same-account-native-wrap-and-ordinary-same9-not-sanctioned-before-first-proof");
            int proofStart = host.ConsoleLines().Length, claimsStart = observer.ProjectileClaims.Count;
            int inventoryStart = observer.InventoryUpdates.Count;
            string inventoryBeforeProof = CharacterInventory(account);
            var slotsBeforeProof = ParseInventory(inventoryBeforeProof);
            var receivedSscBeforeProof = actor.InventoryUpdates.Where(x => x.Player == actor.Slot)
                .GroupBy(x => x.Slot).ToDictionary(x => (int)x.Key, x => x.Last().Item);
            await File.WriteAllTextAsync(Path.Combine(directory, "inventory-before-proof.json"), JsonSerializer.Serialize(new
            {
                account, actor.Slot, source = "actual-SQLite-read-before-proof-send", inventory = inventoryBeforeProof,
                slots = slotsBeforeProof, actualOwnPacket5ItemIds = receivedSscBeforeProof
            }, json));
            Check(slotsBeforeProof.Length == 350 && slotsBeforeProof[10].Raw == "0,0,0,0",
                "followup-target-is-empty-before-proof");
            byte[] proof = LabClient.Packet(27, w => actor.WriteProjectileDamage(w, key, 1, 1005));
            byte[] followup = LabClient.Packet(5, w => LabClient.WriteInventory(w, actor.Slot, 1, 9));
            frames.Add(new { label = "first-complete9-to1005-proof-with-followup", source = "synthetic-tcp", key,
                proof = Convert.ToHexString(proof), followup = Convert.ToHexString(followup) });
            await actor.SendBatch(proof, followup);
            await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed, TimeSpan.FromSeconds(8));
            Check(actor.DisconnectReason == "AntiCheat proven violation.", "first-C7-proof-disconnects");
            await Until(() => BanCount(actor.Name) == 1, "unique-account-permanent-SQLite-ban");
            if (testLab) await Outcome(account, LegacyRule, "Unknown", "arrow-native-internal-int16-range-unproved");
            await Outcome(account, Rule, "ProvenCheat", "arrow-damage-outside-native-internal-projection");
            Check(host.ConsoleLines().Skip(proofStart).Count(x => x.Contains("ANTICHEAT_INCIDENT ") &&
                x.Contains("rule=" + Rule + " ") && x.Contains("accountId=" + account + " ") &&
                x.Contains("canceled=True") && x.Contains("revoked=True")) == 1,
                "exactly-one-canceled-revoked-new-C7-incident-correct-account");
            host.RecordProvenAccount(actor.Name, account, Rule);
            await observer.Drain(TimeSpan.FromMilliseconds(400));
            Check(observer.ProjectileClaims.Skip(claimsStart).All(x => x.Key != key || x.Damage != 1005),
                "proven-update-never-reaches-real-peer27-output");
            if (testLab) Check(host.ConsoleLines().Skip(proofStart).Any(x => x.Contains("ANTICHEAT_REVOKED_PACKET ") &&
                x.Contains("slot=" + actor.Slot + " ") && x.Contains("packet=5")), "coalesced-followup-write-is-revoked");
            string inventoryAfterProof = CharacterInventory(account);
            var slotsAfterProof = ParseInventory(inventoryAfterProof);
            var changedSlots = slotsBeforeProof.Zip(slotsAfterProof).Where(x => x.First.Raw != x.Second.Raw)
                .Select(x => new
                {
                    before = x.First, after = x.Second,
                    classification = IsObservedStarterIdNormalization(x.First, x.Second, receivedSscBeforeProof)
                        ? "native-legacy-starting-item-id-normalization-observed-before-proof" : "unexpected-change"
                }).ToArray();
            var peerInventoryAfterProof = observer.InventoryUpdates.Skip(inventoryStart)
                .Where(x => x.Player == actor.Slot).Select(x => new { x.Player, x.Slot, x.Item }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(directory, "inventory-comparison.json"), JsonSerializer.Serialize(new
            {
                account, actor.Slot, source = "actual-SQLite-reads-around-proof-and-disconnect",
                beforeInventory = inventoryBeforeProof, afterInventory = inventoryAfterProof,
                beforeSlots = slotsBeforeProof, afterSlots = slotsAfterProof, changedSlots, peerInventoryAfterProof,
                normalizationAudit = "Terraria.Item.netDefaults: -15=>3507, -13=>3509, -16=>3506; TShock SeedInitialData keeps configured negative IDs, RestoreCharacter calls netDefaults, CopyCharacter persists Item.type. Only slots 0..2, the exact audited mapping, unchanged stack/prefix/favorite, and matching own SSC packet5 observed before proof are accepted."
            }, json));
            Check(peerInventoryAfterProof.All(x => x.Slot != 10 || x.Item != 9), "followup-has-no-real-peer5-output");
            Check(slotsAfterProof.Length == slotsBeforeProof.Length && slotsAfterProof[10].Raw == slotsBeforeProof[10].Raw,
                "followup-target-slot10-has-no-SSC-write");
            Check(slotsAfterProof.Length == slotsBeforeProof.Length && changedSlots.All(x =>
                x.classification == "native-legacy-starting-item-id-normalization-observed-before-proof"),
                "all-SSC-slots-unchanged-except-audited-native-starting-id-normalization");
            CheckHealthy(observer, "observer-is-not-punished");
            await WriteAppliedIntent(account, actor.Slot);
            await actor.DisposeAsync(); await Task.Delay(200);
            var reconnect = await host.Connect(actor.Name); clients.Add(reconnect);
            await reconnect.Join(); await reconnect.LoginAndExpectRejected();
            Check(reconnect.IsBanRejection, "same-account-real-TCP-reconnect-rejected");
            Check(BanCount(actor.Name) == 1, "reconnect-does-not-duplicate-ban");
            status = "passed";
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "frames.json"), JsonSerializer.Serialize(frames, json));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, rule = Rule, version = "1.0.0", account, source = "synthetic-tcp", executionScope = expectedScope,
                actualLoopbackTcp = true, actualClientUiThisRun = false, scaffoldRequired = false,
                firstCreation1005 = "uncovered-Unknown-no-complete-canonical-source-bound",
                qualifiedC7DiagnosticsChecked = true, withdrawnC6DiagnosticsChecked = testLab,
                sameValueReadiness = "new-real-peer27-after-each-send-index",
                ordinary28 = "not-tested-here-native-cause-overlap-separately-recorded",
                sameEntity9To1005 = "independent-C7-exact-internal-projection-proof",
                legalWrapAndProvenNewEntityShareAccount = true,
                oldC6 = "1.1.0-withdrawn-no-internal-no-wrap-premise",
                restartAdmission = "delegated-to-existing-NetworkLab-provedAccounts-restart-loop",
                clients = clients.Select(x => x.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(x => x.Contains("rule=" + Rule + " ") ||
                    x.Contains("rule=" + LegacyRule + " ") || x.Contains("ANTICHEAT_REVOKED_PACKET")).ToArray()
            }, json));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m7-combat:" + label);
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        long Account(string name) => Scalar("SELECT ID FROM Users WHERE Username=$name", name);
        long BanCount(string name) => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + name);
        string CharacterInventory(long id)
        {
            using var db = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "SELECT Inventory FROM tsCharacter WHERE Account=$id";
            command.Parameters.AddWithValue("$id", id); return Convert.ToString(command.ExecuteScalar()) ?? throw new InvalidOperationException("Missing SSC row.");
        }
        void CheckHealthy(LabClient client, string label) => Check(client.Authenticated && !client.Closed &&
            client.DisconnectReason is null && BanCount(client.Name) == 0, label);
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350 && Account(name) > 0, name + "-authenticated-SSC-session");
            return client;
        }
        async Task ClaimAndObserve(LabClient client, uint key, short damage, string label)
        {
            int prior = observer.ProjectileClaims.Count;
            byte[] frame = LabClient.Packet(27, w => client.WriteProjectileDamage(w, key, 1, damage));
            frames.Add(new { label, source = "synthetic-tcp", client = client.Name, key, damage, frame = Convert.ToHexString(frame) });
            await client.SendBatch(frame);
            // A prior same-value declaration cannot satisfy readiness for this action.
            // Readiness is based on the actual receive/relay action, independently of
            // bounded diagnostics that can de-duplicate an earlier same-value result.
            await observer.WaitUntil(() => observer.ProjectileClaims.Skip(prior).Any(x => x.Key == key && x.Type == 1 && x.Damage == damage),
                TimeSpan.FromSeconds(5));
            Check(true, label + "-new-real-peer27-observed");
        }
        Task Outcome(long id, string rule, string verdict, string reason) => Until(() => host.ConsoleLines().Any(x =>
            x.Contains("ANTICHEAT_RULE_INPUT ") && x.Contains("accountId=" + id + " ") && x.Contains("rule=" + rule + " ") &&
            x.Contains("verdict=" + verdict + " ") && x.Contains("reason=" + reason + " ")), rule + "-" + verdict + "-" + reason);
        async Task Until(Func<bool> predicate, string label)
        {
            var timer = Stopwatch.StartNew(); while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(40);
            Check(predicate(), label);
        }
        async Task WriteAppliedIntent(long id, int slot)
        {
            JsonElement? found = null;
            await Until(() =>
            {
                var matches = new List<JsonElement>(1);
                foreach (string file in Directory.GetFiles(Path.Combine(host.RunDirectory, "tshock", "anticheat", "enforcement"), "*.json"))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    if (document.RootElement.GetProperty("Intent").GetProperty("AccountId").GetInt64() == id) matches.Add(document.RootElement.Clone());
                }
                if (matches.Count != 1 || !matches[0].GetProperty("Applied").GetBoolean()) return false;
                found = matches[0]; return true;
            }, "one-Applied-intent-for-projection-actor");
            var evidence = found!.Value.GetProperty("Intent").GetProperty("Evidence");
            Check(evidence.GetProperty("RuleId").GetString() == Rule && evidence.GetProperty("RuleVersion").GetString() == "1.0.0" &&
                evidence.GetProperty("AccountId").GetInt64() == id && evidence.GetProperty("ServerSlot").GetInt32() == slot &&
                evidence.GetProperty("Session").GetProperty("Slot").GetInt32() == slot &&
                evidence.GetProperty("Qualification").GetInt32() == (testLab ? 1 : 2) &&
                evidence.GetProperty("PacketId").GetInt32() == 27 && evidence.GetProperty("Preconditions").GetProperty("Complete").GetBoolean(),
                "durable-intent-has-new-versioned-complete-packet27-proof");
            using (var facts = JsonDocument.Parse(evidence.GetProperty("PredicateFactsJson").GetString()!))
                Check(facts.RootElement.GetProperty("initialWireDamage").GetString() == "9" &&
                    facts.RootElement.GetProperty("damage").GetString() == "1005" &&
                    facts.RootElement.GetProperty("keyIndex").GetString() == "303" &&
                    facts.RootElement.GetProperty("keyGeneration").GetString() == "7" &&
                    facts.RootElement.GetProperty("initialWitness").GetString() == "raw-fresh-key+post-write-vanilla-relay" &&
                    facts.RootElement.GetProperty("internalDomain").GetString() == "all-signed-int32-preimages" &&
                    facts.RootElement.GetProperty("sourceException").GetString() == "False",
                    "durable-proof-keeps-actual-initial-witness-and-full-int32-projection");
            await File.WriteAllTextAsync(Path.Combine(directory, "applied-intent.json"), JsonSerializer.Serialize(found.Value, json));
        }
    }

    private sealed record InventorySlot(int Slot, string Raw, int Item, int Stack, int Prefix, int Favorite);

    private static InventorySlot[] ParseInventory(string inventory) => inventory.Split('~').Select((raw, slot) =>
    {
        string[] fields = raw.Split(',');
        if (fields.Length != 4) throw new InvalidDataException("Expected four fields in actual SSC inventory slot " + slot);
        int Parse(string value) => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return new InventorySlot(slot, raw, Parse(fields[0]), Parse(fields[1]), Parse(fields[2]), Parse(fields[3]));
    }).ToArray();

    private static bool IsObservedStarterIdNormalization(InventorySlot before, InventorySlot after,
        IReadOnlyDictionary<int, int> observedOwnSscItems)
    {
        int expectedType = (before.Slot, before.Item) switch { (0, -15) => 3507, (1, -13) => 3509, (2, -16) => 3506, _ => -1 };
        return expectedType > 0 && before.Slot == after.Slot && after.Item == expectedType &&
            before.Stack == after.Stack && before.Prefix == after.Prefix && before.Favorite == after.Favorite &&
            observedOwnSscItems.TryGetValue(before.Slot, out int observedType) && observedType == expectedType;
    }
}
