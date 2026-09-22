using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class M14ObjectSafetyScenario
{
    private const ushort WoodHelmet = 727, BorealWoodHelmet = 2509, RedDye = 1007;

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m14-object"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var clients = new List<LabClient>(2); var outcomes = new List<object>(20); var frames = new List<object>(20);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length; bool prepared = false;
        try
        {
            Check(host.RuntimeVerified() && File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-locked-target");
            var actor = await Actor("M14HatActor"); var peer = await Actor("M14HatPeer");
            await host.ConsoleCommand("qa_m14_object setup " + actor.Name + " " + peer.Name);
            var initial = await Snapshot("prepared"); prepared = true;
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("currentRegisteredObject").GetBoolean() &&
                initial.GetProperty("tileValid").GetBoolean(), "actual-native-hat-rack-and-product-guard");
            Check(!initial.GetProperty("actor").GetProperty("bypass").GetBoolean() &&
                !initial.GetProperty("actor").GetProperty("sscBypass").GetBoolean() &&
                initial.GetProperty("actor").GetProperty("regionAllowed").GetBoolean() &&
                !initial.GetProperty("peer").GetProperty("regionAllowed").GetBoolean(), "specific-region-grant-only-to-ordinary-actor");
            int id = initial.GetProperty("id").GetInt32();

            await Open("normal-open-native122", actor, true);
            await Step("normal-first-hat", actor, peer, 0, WoodHelmet, "write");
            await Step("normal-second-hat", actor, peer, 1, BorealWoodHelmet, "write");
            await Step("normal-first-dye", actor, peer, 2, RedDye, "write");
            await Step("normal-second-dye", actor, peer, 3, RedDye, "write");
            await Step("normal-take-hat", actor, peer, 0, 0, "write", stack: 0);
            await Step("normal-take-dye", actor, peer, 3, 0, "write", stack: 0);
            await Step("normal-replace-hat-ignored-wire-sender", actor, peer, 0, WoodHelmet, "write", wireSender: 255);

            await host.ConsoleCommand("qa_m14_object deny");
            var denied = await Snapshot("permission-revoked-after-open");
            Check(!denied.GetProperty("actor").GetProperty("regionAllowed").GetBoolean(), "real-region-permission-revoked");
            Check(denied.GetProperty("actor").GetProperty("anchor").GetInt32() == id, "previous-native-opening-does-not-clear-on-region-change");
            await Step("opened-then-revoked-hat-write", actor, peer, 0, BorealWoodHelmet, "safety-block");
            await Step("opened-then-revoked-dye-write", actor, peer, 3, RedDye, "safety-block");
            await Step("opened-then-revoked-take-write", actor, peer, 1, 0, "safety-block", stack: 0);
            await Open("existing-opening-authorization-rejection-still-effective", actor, false);
            await Step("existing-native-slot4-noop", actor, peer, 4, WoodHelmet, "native-noop");
            await Step("existing-native-slot255-noop", actor, peer, 255, WoodHelmet, "native-noop");
            await Step("missing-target-native-noop", actor, peer, 0, WoodHelmet, "native-noop", entity: -1);
            await Step("truncated-item-body", actor, peer, 0, WoodHelmet, "safety-block", bodyAdjustment: -1);
            await Step("trailing-item-body", actor, peer, 0, WoodHelmet, "safety-block", bodyAdjustment: 1);

            await host.ConsoleCommand("qa_m14_object allow");
            Check((await Snapshot("region-allowed-again")).GetProperty("actor").GetProperty("regionAllowed").GetBoolean(), "ordinary-region-grant-restored");
            await Step("same-account-write-recovers-after-denials", actor, peer, 0, BorealWoodHelmet, "write");
            await Step("peer-cannot-borrow-wire-sender-account", peer, actor, 0, WoodHelmet, "safety-block", wireSender: actor.Slot);
            await Step("original-account-still-writes-after-peer-denial", actor, peer, 1, WoodHelmet, "write");
            await Step("same-account-dye-recovers", actor, peer, 3, RedDye, "write");
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(logStart).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)), "no-account-proof-from-object-safety");
            status = "passed";

            async Task Open(string label, LabClient subject, bool allowed)
            {
                var before = await Snapshot(label + "-before");
                int firstLine = host.ConsoleLines().Length;
                byte[] frame = LabClient.Packet(122, writer => { writer.Write(id); writer.Write(subject.Slot); });
                frames.Add(new { label, subject = subject.Name, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
                await subject.SendBatch(frame); await subject.PingAsync(); await peer.PingAsync();
                var after = await Snapshot(label + "-after"); var raw = after.GetProperty("lastOpen");
                Check(after.GetProperty("openRaw").GetInt64() == before.GetProperty("openRaw").GetInt64() + 1 &&
                    raw.GetProperty("handled").GetBoolean() == !allowed && raw.GetProperty("receivingSlot").GetInt32() == subject.Slot,
                    label + "-actual-opening-cancellation");
                if (allowed)
                    Check(after.GetProperty("openEvents").GetInt64() == before.GetProperty("openEvents").GetInt64() + 1,
                        label + "-existing-core-permission-event");
                else
                {
                    // Existing M3 WorldContexts applies the same core permission contract on raw122,
                    // before TShock's parser. Preserve that early cancellation instead of expecting
                    // its already-canceled frame to reach a later public opening callback.
                    long account = before.GetProperty("actor").GetProperty("account").GetInt64();
                    Check(host.ConsoleLines().Skip(firstLine).Count(line =>
                        line.Contains("ANTICHEAT_RULE_INPUT rule=WORLD-AUTHORIZATION ", StringComparison.Ordinal) &&
                        line.Contains("verdict=UnsafeInput ", StringComparison.Ordinal) &&
                        line.Contains("reason=world-operation-permission-or-range-denied ", StringComparison.Ordinal) &&
                        line.Contains("accountId=" + account + " ", StringComparison.Ordinal) &&
                        line.Contains("packet=122 action=Block canceled=True alreadyCanceled=False", StringComparison.Ordinal)) == 1,
                        label + "-exact-current-preexisting-world-authorization-rejection");
                    Check(after.GetProperty("openEvents").GetInt64() == before.GetProperty("openEvents").GetInt64(),
                        label + "-early-canceled122-never-reaches-core-opening-event");
                    Check(after.GetProperty("actor").GetProperty("anchor").GetInt32() == before.GetProperty("actor").GetProperty("anchor").GetInt32() &&
                        Contents(before) == Contents(after), label + "-rejected-open-preserves-current-anchor-and-object");
                }
                Check(after.GetProperty("safetyBlocked").GetInt64() == before.GetProperty("safetyBlocked").GetInt64(), label + "-no-new124-filter-on122");
                if (allowed) Check(after.GetProperty("actor").GetProperty("anchor").GetInt32() == id, label + "-actual-native-interaction-anchor");
                outcomes.Add(new { label, action = "existing-core-opening", allowed, before, after });
            }

            async Task Step(string label, LabClient subject, LabClient witness, byte wireSlot, ushort type, string outcome,
                ushort stack = 1, byte? wireSender = null, int? entity = null, int bodyAdjustment = 0)
            {
                bool write = outcome == "write", denied = outcome == "safety-block";
                var before = await Snapshot(label + "-before"); await witness.PingAsync(); int peerBefore = witness.PacketCount(124);
                byte[] body;
                using (var memory = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(wireSender ?? subject.Slot); writer.Write(entity ?? id); writer.Write(wireSlot);
                        writer.Write(type); writer.Write(stack); writer.Write((byte)0);
                    }
                    body = memory.ToArray();
                }
                if (bodyAdjustment != 0) Array.Resize(ref body, body.Length + bodyAdjustment);
                byte[] frame = LabClient.Packet(124, writer => writer.Write(body));
                frames.Add(new { label, subject = subject.Name, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
                await subject.SendBatch(frame); await subject.PingAsync(); await witness.PingAsync();
                var after = await Snapshot(label + "-after"); var raw = after.GetProperty("lastRaw");
                Check(after.GetProperty("raw").GetInt64() == before.GetProperty("raw").GetInt64() + 1 &&
                    raw.GetProperty("receivingSlot").GetInt32() == subject.Slot && raw.GetProperty("wireSlot").GetInt32() == wireSlot &&
                    raw.GetProperty("payloadHex").GetString() == Convert.ToHexString(body) && raw.GetProperty("handled").GetBoolean() == denied,
                    label + "-actual-current-frame-and-cancellation");
                Check(after.GetProperty("safetyBlocked").GetInt64() == before.GetProperty("safetyBlocked").GetInt64() + (denied ? 1 : 0),
                    label + "-specific-product-safety-counter");
                Check(after.GetProperty("sends").GetInt64() == before.GetProperty("sends").GetInt64() + (write ? 1 : 0) &&
                    witness.PacketCount(124) == peerBefore + (write ? 1 : 0), label + "-native-relay-and-independent-peer");
                if (write)
                {
                    string array = wireSlot < 2 ? "items" : "dyes"; int index = wireSlot % 2;
                    Check(after.GetProperty(array)[index].GetProperty("type").GetInt32() == type &&
                        after.GetProperty(array)[index].GetProperty("stack").GetInt32() == stack, label + "-actual-hat-or-dye-slot-write");
                }
                else Check(Contents(before) == Contents(after), label + "-all-four-object-slots-preserved");
                Check(after.GetProperty("openEvents").GetInt64() == before.GetProperty("openEvents").GetInt64(), label + "-124-is-independent-of-opening-event");
                Healthy(subject);
                outcomes.Add(new { label, subject = subject.Name, outcome, wireSlot, type, stack, raw,
                    peer124Delta = witness.PacketCount(124) - peerBefore, before, after });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            bool successfulBeforeCleanup = status == "passed";
            if (prepared)
            {
                try
                {
                    await host.ConsoleCommand("qa_m14_object finish"); var finished = await Snapshot("finished-region-restored");
                    Check(!finished.GetProperty("observersInstalled").GetBoolean() &&
                        finished.GetProperty("currentAllowed").GetRawText() == finished.GetProperty("originalAllowed").GetRawText(),
                        "owned-region-grant-and-observers-restored");
                }
                catch (Exception cleanup) { failure += "\nfixture restore: " + cleanup; status = "failed"; }
            }
            await Write("frames.json", frames);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientGui = false,
                scope = "packet124 current hat-rack permission at item commit; existing122 opening permissions and native safe noops retained",
                expectedAccountBan = false, outcomes, clients = clients.Select(client => client.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(line => line.Contains("ANTICHEAT_") || line.Contains("qa_m14_object")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
            if (successfulBeforeCleanup) Check(status == "passed", "scenario-and-owned-fixture-restoration-completed");
        }

        void Check(bool condition, string label) => host.Assert(condition, "m14-object:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-ordinary-SSC-account"); return client;
        }
        void Healthy(LabClient client)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"); db.Open();
            using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            query.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(query.ExecuteScalar()) == 0,
                client.Name + "-usable-without-permanent-ban");
        }
        async Task<JsonElement> Snapshot(string label)
        {
            var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m14_object state");
            string path = Path.Combine(host.ReportDirectory, "m14-object-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { var result = document.RootElement.Clone(); await Write(label + ".json", result); return result; }
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                await Task.Delay(40);
            }
            throw new TimeoutException("Fresh bounded object snapshot unavailable: " + label);
        }
    }

    private static string Contents(JsonElement snapshot) => snapshot.GetProperty("items").GetRawText() + "|" + snapshot.GetProperty("dyes").GetRawText();
}
