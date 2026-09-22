using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Owned loopback65/96 into the real raw guard, core, native body and real peer.
/// This is parameter safety validation, not a natural teleport authorization or GUI experiment.</summary>
internal static class M9TeleportScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        const string rule = "G05.PlayerTeleportParameters";
        var json = new JsonSerializerOptions { WriteIndented = true };
        string directory = Path.Combine(host.ReportDirectory, "m9-teleport"); Directory.CreateDirectory(directory);
        var clients = new List<LabClient>(2); var frames = new List<object>(8); var results = new List<object>(8);
        string status = "failed", failure = ""; int logStart = host.ConsoleLines().Length;
        try
        {
            Check(host.RuntimeVerified(), "locked-target-runtime");
            Check(File.Exists(Path.Combine(host.RunDirectory, ".anticheat-lab")), "owned-isolated-run");
            await host.FixtureSnapshot();
            var observer = await Actor("M9TeleportPeer"); var actor = await Actor("M9TeleportActor");
            await host.ConsoleCommand("qa_m9_teleport " + actor.Name + " " + observer.Name);
            var initial = await ReadFresh(DateTimeOffset.MinValue, "prepared");
            Check(initial.GetProperty("guardPresent").GetBoolean() && initial.GetProperty("witnessInstalled").GetBoolean(), "real-guard-and-native-body-witness");
            var initialActor = StateActor(initial, actor);
            Check(!initialActor.GetProperty("bypass").GetBoolean() && initialActor.GetProperty("samePlayer").GetBoolean(), "ordinary-current-actor");
            float x = initialActor.GetProperty("x").GetSingle() + 16, y = initialActor.GetProperty("y").GetSingle();
            Check(!initialActor.GetProperty("rodPermission").GetBoolean(), "no-core-rod-permission-control-precondition");
            await Exchange("core-denied-item-teleport", 65, Teleport(actor.Slot, 0, x + 64, y), false, 0, 0, coreDenied: true);
            var grantRequested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m10_rod " + actor.Name + " allow");
            var granted = StateActor(await ReadFresh(grantRequested, "minimum-rod-permission"), actor);
            Check(granted.GetProperty("rodPermission").GetBoolean() &&
                granted.GetProperty("directPermissions").GetString() == "tshock.tp.rod" &&
                granted.GetProperty("groupParent").GetString() == initialActor.GetProperty("group").GetString() &&
                granted.GetProperty("temporaryGroup").ValueKind == JsonValueKind.Null &&
                !granted.GetProperty("sscBypass").GetBoolean() && !granted.GetProperty("bypass").GetBoolean(),
                "minimum-core-rod-permission-preserves-ordinary-parent-and-SSC");
            await Exchange("legal-item-teleport", 65, Teleport(actor.Slot, 0, x + 16, y), true, x + 16, y);
            await Exchange("legal-portal", 96, Portal(actor.Slot, x, y, 3, -2), true, x, y, portalVelocity: true);
            await Exchange("nonfinite-portal-destination", 96, Portal(actor.Slot, float.NaN, y, 3, -2), false, 0, 0,
                expectedReason: "player-teleport-nonfinite-consumed-destination");
            await Exchange("nonfinite-portal-velocity", 96, Portal(actor.Slot, x, y, float.PositiveInfinity, -2), false, 0, 0,
                expectedReason: "player-portal-nonfinite-consumed-velocity");
            await Exchange("target-index-before-core-dereference", 65, Teleport(short.MinValue, 4, x, y), false, 0, 0,
                expectedReason: "teleport-target-position-index-out-of-range");
            await Exchange("nonfinite-item-destination", 65, Teleport(actor.Slot, 0, float.NaN, y), false, 0, 0,
                expectedReason: "player-teleport-nonfinite-consumed-destination");
            await Exchange("legal-portal-after-rejections", 96, Portal(actor.Slot, x + 32, y, 3, -2), true, x + 32, y, portalVelocity: true);
            var restoreRequested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m10_rod " + actor.Name + " restore");
            var restored = StateActor(await ReadFresh(restoreRequested, "original-group-restored"), actor);
            Check(!restored.GetProperty("rodPermission").GetBoolean() &&
                restored.GetProperty("group").GetString() == initialActor.GetProperty("group").GetString(), "ordinary-group-restored");
            await observer.PingAsync(); await actor.PingAsync();
            Healthy(actor, "actor-no-sanction"); Healthy(observer, "uninvolved-observer-no-sanction");
            Check(!host.ConsoleLines().Skip(logStart).Any(x => x.Contains("ANTICHEAT_INCIDENT ")), "parameter-safety-no-cheat-incident");
            status = "passed";

            async Task Exchange(string label, int id, byte[] frame, bool permitted, float expectedX, float expectedY,
                bool portalVelocity = false, string? expectedReason = null, bool coreDenied = false)
            {
                await observer.Drain(TimeSpan.FromMilliseconds(100));
                var before = await Snapshot(label + "-before"); int peerBefore = observer.PacketCount((byte)id);
                var beforeActor = StateActor(before, actor); var beforePeer = StateActor(before, observer);
                frames.Add(new { label, source = "synthetic-loopback-tcp", hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame); await actor.PingAsync(); await observer.Drain(TimeSpan.FromMilliseconds(150));
                var after = await Snapshot(label + "-after"); var afterActor = StateActor(after, actor); var afterPeer = StateActor(after, observer);
                long entryDelta = afterActor.GetProperty("NativeEntries").GetInt64() - beforeActor.GetProperty("NativeEntries").GetInt64();
                string sendCounter = id == 65 ? "Send65" : "Send96";
                long sendDelta = afterActor.GetProperty(sendCounter).GetInt64() - beforeActor.GetProperty(sendCounter).GetInt64();
                int peerDelta = observer.PacketCount((byte)id) - peerBefore;
                var raw = afterActor.GetProperty("LastRaw");
                Check(afterActor.GetProperty("RawRequests").GetInt64() == beforeActor.GetProperty("RawRequests").GetInt64() + 1 &&
                    raw.GetProperty("Sequence").GetInt64() == afterActor.GetProperty("RawRequests").GetInt64() &&
                    raw.GetProperty("Packet").GetInt32() == id && afterActor.GetProperty("RawWitnessFaults").GetInt64() == 0 &&
                    raw.GetProperty("SamePlayer").GetBoolean() && raw.GetProperty("SameThread").GetBoolean() &&
                    !raw.GetProperty("HandledBefore").GetBoolean() && raw.GetProperty("HandledAfter").GetBoolean() == !permitted,
                    label + "-same-raw-dispatch-before-after-witness");
                if (coreDenied)
                {
                    Check(entryDelta == 1 && sendDelta == 1 && peerDelta == 1, label + "-core-single-corrective-teleport");
                    var relay = afterActor.GetProperty("LastRelay"); var rawBefore = raw.GetProperty("Before");
                    Check(relay.GetProperty("x").GetSingle() == rawBefore.GetProperty("X").GetSingle() &&
                        relay.GetProperty("y").GetSingle() == rawBefore.GetProperty("Y").GetSingle(), label + "-core-corrects-to-existing-position");
                    Check(!beforeActor.GetProperty("rodPermission").GetBoolean(), label + "-actual-core-permission-refusal");
                    Check(host.ConsoleLines().Skip(logStart).Any(x => x.Contains("rule=" + rule + " verdict=Pass ") &&
                        x.Contains("slot=" + actor.Slot + " ") && x.Contains("action=Pass canceled=False")), label + "-product-pass-before-core-refusal");
                }
                else if (permitted)
                {
                    Check(entryDelta == 1 && sendDelta == 1 && peerDelta == 1, label + "-actual-single-native-entry-and-peer-relay");
                    var relay = afterActor.GetProperty("LastRelay");
                    Check(relay.GetProperty("x").GetSingle() == expectedX && relay.GetProperty("y").GetSingle() == expectedY,
                        label + "-native-post-write-position");
                    if (portalVelocity) Check(relay.GetProperty("velocityX").GetSingle() == 3 && relay.GetProperty("velocityY").GetSingle() == -2 &&
                        relay.GetProperty("lastPortalColorIndex").GetInt32() == 3, label + "-native-post-write-portal-velocity-and-color");
                    Check(host.ConsoleLines().Skip(logStart).Any(x => x.Contains("rule=" + rule + " verdict=Pass ") &&
                        x.Contains("slot=" + actor.Slot + " ") && x.Contains("action=Pass canceled=False")), label + "-product-pass-branch-entered");
                }
                else
                {
                    Check(entryDelta == 0 && sendDelta == 0 && peerDelta == 0, label + "-before-native-body-and-outgoing-relay");
                    var rawBefore = raw.GetProperty("Before"); var rawAfter = raw.GetProperty("After");
                    Check(rawBefore.GetProperty("X").GetSingle() == rawAfter.GetProperty("X").GetSingle() &&
                        rawBefore.GetProperty("Y").GetSingle() == rawAfter.GetProperty("Y").GetSingle() &&
                        rawBefore.GetProperty("VelocityX").GetSingle() == rawAfter.GetProperty("VelocityX").GetSingle() &&
                        rawBefore.GetProperty("VelocityY").GetSingle() == rawAfter.GetProperty("VelocityY").GetSingle() &&
                        rawBefore.GetProperty("PortalColor").GetInt32() == rawAfter.GetProperty("PortalColor").GetInt32(),
                        label + "-same-raw-dispatch-position-velocity-color-unchanged");
                    Check(host.ConsoleLines().Skip(logStart).Any(x => x.Contains("rule=" + rule + " verdict=UnsafeInput ") &&
                        x.Contains("reason=" + expectedReason + " ") && x.Contains("slot=" + actor.Slot + " ") &&
                        x.Contains("action=Block canceled=True")), label + "-exact-product-rejection");
                }
                Check(beforePeer.GetProperty("NativeEntries").GetInt64() == afterPeer.GetProperty("NativeEntries").GetInt64(), label + "-no-other-player-native-teleport");
                Healthy(actor, label + "-actor-remains-writable");
                results.Add(new { label, permitted, coreDenied, before, after, entryDelta, sendDelta, peerDelta });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await Write("frames.json", frames); await Write("results.json", results);
            await Write("summary.json", new { status, failure, actualLoopbackTcp = true, actualClientUiThisRun = false,
                scaffoldLoaded = true, productionHardQualificationClaimed = false,
                scope = "G05/A01 player65/96 consumed-parameter BLOCK; no authorization/cause/hard-ban claim",
                clients = clients.Select(x => x.Evidence()),
                console = host.ConsoleLines().Skip(logStart).Where(x => x.Contains("ANTICHEAT_") || x.Contains("qa_m9_teleport")).ToArray() });
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m9-teleport:" + label);
        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
        async Task<LabClient> Actor(string name)
        {
            var client = await host.Connect(name); clients.Add(client); await client.Join(); await client.RegisterAndLogin();
            Check(client.Authenticated && client.SscSlots.Count >= 350, name + "-ordinary-SSC-account"); return client;
        }
        void Healthy(LabClient client, string label)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name";
            command.Parameters.AddWithValue("$name", "acc:" + client.Name);
            Check(client.Authenticated && !client.Closed && client.DisconnectReason is null && Convert.ToInt64(command.ExecuteScalar()) == 0, label);
        }
        async Task<JsonElement> Snapshot(string label)
        { var requested = DateTimeOffset.UtcNow; await host.ConsoleCommand("qa_m9_teleport_state"); return await ReadFresh(requested, label); }
        async Task<JsonElement> ReadFresh(DateTimeOffset requested, string label)
        {
            string path = Path.Combine(host.ReportDirectory, "m9-teleport-state-latest.json"); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                    try
                    {
                        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        if (data.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { var snapshot = data.RootElement.Clone(); await Write(label + ".json", snapshot); return snapshot; }
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                await Task.Delay(40);
            }
            throw new TimeoutException("Fresh teleport witness state missing: " + label);
        }
    }
    private static JsonElement StateActor(JsonElement state, LabClient actor) => state.GetProperty("actors").EnumerateArray()
        .Single(x => x.GetProperty("slot").GetInt32() == actor.Slot);
    private static byte[] Teleport(short slot, byte flags, float x, float y) => LabClient.Packet(65,
        w => { w.Write(flags); w.Write(slot); w.Write(x); w.Write(y); w.Write((byte)0); });
    private static byte[] Portal(byte slot, float x, float y, float vx, float vy) => LabClient.Packet(96,
        w => { w.Write(slot); w.Write((short)3); w.Write(x); w.Write(y); w.Write(vx); w.Write(vy); });
}
