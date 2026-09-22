using System.Text.Json;
using Microsoft.Data.Sqlite;

// Narrow live slice for the existing M18 direct-world-edit candidate. It uses
// the existing loopback NetworkLab, TShock native receiver, and QA journal.
// No GUI, public endpoint, deployment, or permanent sanction is involved.
internal static class M18WorldEditScenario
{
    private const byte TilePacket = 17;
    private const byte LiquidPacket = 48;
    private const short DirtTile = 0;
    private const short StoneTile = 1;
    private const short DirtBlockItem = 2;
    private const short StoneBlockItem = 3;
    private const short WoodWall = 4;
    private const short DirtWall = 16;
    private const short WoodWallItem = 93;
    private const short DirtWallItem = 30;
    private const short IronHammerItem = 7;
    private const string LiquidBypassPermission = "tshock.ignoreliquidsetdetection";
    private const int CandidateWorkBudget = 2048;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Narrow P0-B follow-up: the brush source expands its selection into
    // individual native packet17 requests. This run sends a small legal wall
    // batch, then a bounded packet17 stop-loss batch, without reusing the
    // suspended movement/range probe from the full world-edit scenario.
    public static async Task RunBatchAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-b-batch");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "";
        JsonElement? preparedRoom = null;
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0BBatchObserver");
            await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0BBatchActor");
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            await host.ConsoleCommand("qa_prepare " + actor.Name);
            preparedRoom = await host.FixtureSnapshot();
            string roomFile = Path.Combine(run, "qa-scaffold-room.json");
            Check(File.Exists(roomFile), "existing-qa-room-recorded-inside-run");
            using var roomDocument = JsonDocument.Parse(await File.ReadAllTextAsync(roomFile));
            var room = roomDocument.RootElement;
            Check(room.GetProperty("State").GetString() == "prepared", "qa-room-prepared-state");
            int left = room.GetProperty("Left").GetInt32();
            int floor = room.GetProperty("FloorY").GetInt32();

            await host.ConsoleCommand("qa_m18_wall_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == WoodWallItem && x.Stack >= 8) && actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtWallItem && x.Stack >= 4) && actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == IronHammerItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var woodWall = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == WoodWallItem && x.Stack > 0);
            var dirtWall = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtWallItem && x.Stack > 0);
            var hammer = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == IronHammerItem && x.Stack > 0);
            Check(woodWall.Item == WoodWallItem && dirtWall.Item == DirtWallItem &&
                hammer.Item == IronHammerItem && woodWall.Slot is >= 0 and < 59 &&
                dirtWall.Slot is >= 0 and < 59 && hammer.Slot is >= 0 and < 59,
                "qa-prepare-issued-materials-for-batched-native-wall-chain");

            int batchY = floor - 1;
            // left+12 is the scaffold workbench; keep the batch on four
            // otherwise empty cells in the same legal in-room range.
            int[] batchX = Enumerable.Range(left + 15, 4).ToArray();
            await actor.MoveTo((left + 7) * 16, (floor - 2) * 16);
            await actor.PingAsync();
            var batchBefore = new Dictionary<int, JsonElement>();
            foreach (int x in batchX)
                batchBefore[x] = await Cell(x, batchY, "batch-wall-before-" + (x - left));
            Check(batchBefore.Values.All(cell => !cell.GetProperty("active").GetBoolean() &&
                cell.GetProperty("wall").GetInt32() == 0),
                "batched-wall-targets-start-empty");

            await actor.SelectItem(checked((byte)woodWall.Slot));
            int batchEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            var wallBatch = batchX.Select(x => LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)3); // PlaceWall
                writer.Write((short)x); writer.Write((short)batchY);
                writer.Write(WoodWall); writer.Write((byte)0);
            })).ToArray();
            await actor.SendBatch(wallBatch);
            await actor.PingAsync();
            await Task.Delay(400);
            var batchAfter = new Dictionary<int, JsonElement>();
            foreach (int x in batchX)
                batchAfter[x] = await Cell(x, batchY, "batch-wall-after-" + (x - left));
            int batchEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(batchAfter.Values.All(cell => cell.GetProperty("wall").GetInt32() == WoodWall),
                "each-wall-frame-in-one-wire-batch-reaches-native-world-state");
            Check(batchEventAfter - batchEventBefore == wallBatch.Length,
                "one-wire-batch-is-counted-as-individual-tile-hook-events");
            Check(Healthy(actor, database), "batched-wall-place-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "native-wall-place-batch",
                packet = TilePacket,
                operation = "PlaceWall",
                frameCount = wallBatch.Length,
                frames = wallBatch.Select(Convert.ToHexString).ToArray(),
                targets = batchX.Select(x => new { x, y = batchY }).ToArray(),
                before = batchBefore,
                after = batchAfter,
                qaTileEvents = batchEventAfter - batchEventBefore,
                workUnits = wallBatch.Length,
                accounting = "each complete packet17 is one work unit; no rectangle or UI setting is inferred"
            });

            await actor.SelectItem(checked((byte)dirtWall.Slot));
            int replaceEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            int[] replaceX = batchX[..2];
            var replaceBatch = replaceX.Select(x => LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)22); // ReplaceWall
                writer.Write((short)x); writer.Write((short)batchY);
                writer.Write(DirtWall); writer.Write((byte)0);
            })).ToArray();
            await actor.SendBatch(replaceBatch);
            await actor.PingAsync();
            await Task.Delay(300);
            var replaceAfter = new Dictionary<int, JsonElement>();
            foreach (int x in replaceX)
                replaceAfter[x] = await Cell(x, batchY, "batch-wall-replaced-" + (x - left));
            int replaceEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(replaceAfter.Values.All(cell => cell.GetProperty("wall").GetInt32() == DirtWall),
                "replace-wall-frames-in-one-wire-batch-reach-native-world-state");
            Check(replaceEventAfter - replaceEventBefore == replaceBatch.Length,
                "replace-wall-batch-reaches-one-hook-per-complete-frame");
            Check(Healthy(actor, database), "batched-wall-replace-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "native-wall-replace-batch",
                packet = TilePacket,
                operation = "ReplaceWall",
                frameCount = replaceBatch.Length,
                frames = replaceBatch.Select(Convert.ToHexString).ToArray(),
                targets = replaceX.Select(x => new { x, y = batchY }).ToArray(),
                after = replaceAfter,
                qaTileEvents = replaceEventAfter - replaceEventBefore,
                workUnits = replaceBatch.Length * 2,
                accounting = "ReplaceWall costs two queue units per complete packet17"
            });

            // An empty in-room wall is a no-op control for the native receiver.
            // The candidate must still count each complete packet17 and stop
            // before the 2049th retained event; no player sanction is implied.
            await actor.SelectItem(checked((byte)hammer.Slot));
            int noOpX = left + 14;
            var noOpBefore = await Cell(noOpX, batchY, "packet17-stop-loss-before");
            Check(!noOpBefore.GetProperty("active").GetBoolean() &&
                noOpBefore.GetProperty("wall").GetInt32() == 0,
                "packet17-stop-loss-control-starts-as-empty-wall");
            const int priorWorkUnits = 8; // four PlaceWall + two ReplaceWall * 2
            int acceptedBeforeBlock = CandidateWorkBudget - priorWorkUnits;
            int floodFrames = acceptedBeforeBlock + 1;
            int floodEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            int logStart = host.ConsoleLines().Length;
            var noOpBatch = Enumerable.Range(0, floodFrames)
                .Select(_ => LabClient.Packet(TilePacket, writer =>
                {
                    writer.Write((byte)2); // KillWall on an empty wall: no-op native control
                    writer.Write((short)noOpX); writer.Write((short)batchY);
                    writer.Write((short)0); writer.Write((byte)0);
                })).ToArray();
            await actor.SendBatch(noOpBatch);
            await actor.PingAsync();
            await Task.Delay(2500);
            var noOpAfter = await Cell(noOpX, batchY, "packet17-stop-loss-after");
            var floodConsole = host.ConsoleLines().Skip(logStart).ToArray();
            int floodEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            string? resourceLine = floodConsole.FirstOrDefault(x =>
                x.Contains("rule=F07.WorldEditDensityBudget", StringComparison.Ordinal) &&
                x.Contains("verdict=ResourceAbuse", StringComparison.Ordinal) &&
                x.Contains("reason=world-edit-window-", StringComparison.Ordinal) &&
                x.Contains("packet=17", StringComparison.Ordinal) &&
                x.Contains("action=Block", StringComparison.Ordinal) &&
                x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase));
            Check(resourceLine is not null,
                "packet17-bounded-queue-block-is-written-before-native-receiver");
            Check(floodEventAfter - floodEventBefore == acceptedBeforeBlock,
                "packet17-batch-retains-only-pre-budget-hook-events");
            Check(noOpAfter.GetProperty("active").GetBoolean() == noOpBefore.GetProperty("active").GetBoolean() &&
                noOpAfter.GetProperty("wall").GetInt32() == noOpBefore.GetProperty("wall").GetInt32() &&
                noOpAfter.GetProperty("liquid").GetInt32() == noOpBefore.GetProperty("liquid").GetInt32(),
                "blocked-packet17-no-op-control-has-no-world-side-effect");
            Check(Healthy(actor, database), "packet17-stop-loss-keeps-actor-connected");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "packet17-stop-loss-does-not-ban");
            observations.Add(new
            {
                kind = "packet17-batch-density-stop-loss",
                packet = TilePacket,
                operation = "KillWall",
                frameCount = noOpBatch.Length,
                frames = noOpBatch.Take(4).Select(Convert.ToHexString).ToArray(),
                framesOmittedFromEvidence = noOpBatch.Length - Math.Min(4, noOpBatch.Length),
                target = new { x = noOpX, y = batchY },
                before = noOpBefore,
                after = noOpAfter,
                candidateWorkBudget = CandidateWorkBudget,
                priorWorkUnits,
                acceptedBeforeBlock,
                qaTileEvents = floodEventAfter - floodEventBefore,
                resourceReason = resourceLine,
                console = floodConsole.Where(x => x.Contains("F07.WorldEditDensityBudget", StringComparison.Ordinal)).Take(4).ToArray(),
                sanction = "none; ResourceAbuse is a bounded stop-loss, not account-cheat proof"
            });

            Check(Healthy(actor, database), "actor-healthy-after-packet17-batch-slice");
            await WriteEvidence(report, run, preparedRoom, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteEvidence(report, run, preparedRoom, observations, "failed", failure);
            throw;
        }
        finally
        {
            await CopyRelevantQaEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-batch-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-b-batch:" + name);

        async Task<JsonElement> Cell(int x, int y, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_cell " + x + " " + y);
            string path = Path.Combine(run, "m18-world-cell-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 batch cell witness timed out: " + label);
        }
    }

    // Narrow P0-B follow-up: TerraAngel can precede a native PlaceTile request
    // with a custom finite PlayerControls (13) position. This slice records one
    // unmoved distant control and one position-declared request on the existing
    // disposable room. It does not use MoveTo, a GUI, a Bypass permission, or a
    // sanction; the position witness is read-only and the target is bounded.
    public static async Task RunPositionInjectionAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-b-position");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "";
        JsonElement? preparedRoom = null;
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0BPosObs");
            await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0BPosAct");
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            await host.ConsoleCommand("qa_prepare " + actor.Name);
            preparedRoom = await host.FixtureSnapshot();
            string roomFile = Path.Combine(run, "qa-scaffold-room.json");
            Check(File.Exists(roomFile), "existing-qa-room-recorded-inside-run");
            using var roomDocument = JsonDocument.Parse(await File.ReadAllTextAsync(roomFile));
            var room = roomDocument.RootElement;
            Check(room.GetProperty("State").GetString() == "prepared", "qa-room-prepared-state");
            int left = room.GetProperty("Left").GetInt32();
            int floor = room.GetProperty("FloorY").GetInt32();

            await host.ConsoleCommand("qa_m18_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var dirt = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0);
            Check(dirt.Item == DirtBlockItem && dirt.Slot is >= 0 and < 59,
                "qa-prepare-issued-real-dirt-block-for-position-place");
            await actor.SelectItem(checked((byte)dirt.Slot));
            await host.ConsoleCommand("qa_capture 13");

            var baseline = await PositionState(actor, "position-baseline");
            int startTileX = baseline.GetProperty("tileX").GetInt32();
            int startTileY = baseline.GetProperty("tileY").GetInt32();
            Check(startTileX >= left + 18 && startTileX <= left + 22 &&
                startTileY >= floor - 5 && startTileY <= floor,
                "actor-starts-at-recorded-room-origin-without-move-baseline");
            actor.PauseHeartbeat = true;

            // Prefer a bounded natural support cell outside the scaffold. The
            // probe avoids claiming a legal native placement on an unsupported
            // air pocket while leaving a margin beyond the observed native
            // range; the first 34-tile control is retained as a failed run.
            JsonElement targetBefore = default;
            JsonElement supportBefore = default;
            int targetX = 0, targetY = 0;
            var positionProbes = new List<object>();
            foreach (int offset in new[] { 80, 100, 120 })
            {
                foreach (int yOffset in new[] { -1, 0, 1, 2, 3, 4, 5, 6, 8, 12, 16, 24 })
                {
                    int x = left + offset;
                    int y = floor + yOffset;
                    if (y < room.GetProperty("Top").GetInt32() - 2 || y + 1 > floor + 32) continue;
                    var candidate = await PositionCell(x, y, "position-probe-" + offset + "-" + y);
                    var support = await PositionCell(x, y + 1, "position-support-probe-" + offset + "-" + y);
                    positionProbes.Add(new
                    {
                        x, y,
                        target = candidate,
                        support,
                        distanceTiles = x - startTileX,
                        targetEmpty = !candidate.GetProperty("active").GetBoolean() &&
                            candidate.GetProperty("wall").GetInt32() == 0 &&
                            candidate.GetProperty("liquid").GetInt32() == 0,
                        supportActive = support.GetProperty("active").GetBoolean()
                    });
                    if (x - startTileX > 48 &&
                        !candidate.GetProperty("active").GetBoolean() &&
                        candidate.GetProperty("wall").GetInt32() == 0 &&
                        candidate.GetProperty("liquid").GetInt32() == 0 &&
                        support.GetProperty("active").GetBoolean())
                    {
                        targetX = x; targetY = y; targetBefore = candidate; supportBefore = support;
                        break;
                    }
                }
                if (targetX != 0) break;
            }
            Check(targetX != 0, "bounded-distant-target-has-empty-cell-and-native-support");
            Check(targetX - startTileX > 48, "distant-target-exceeds-observed-native-range-margin");

            int directEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            int directRaw13Before = CountRawFrames(host.ReportDirectory, 13, actor.Slot);
            byte[] directFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)1); // PlaceTile
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(DirtTile); writer.Write((byte)0);
            });
            await actor.SendBatch(directFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var directAfter = await PositionCell(targetX, targetY, "position-direct-after");
            int directEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(!directAfter.GetProperty("active").GetBoolean() &&
                directAfter.GetProperty("type").GetInt32() == 0 &&
                directAfter.GetProperty("wall").GetInt32() == 0 &&
                directAfter.GetProperty("liquid").GetInt32() == 0,
                "unmoved-distant-place-is-rejected-without-world-side-effect");
            Check(directEventAfter > directEventBefore,
                "unmoved-distant-place-reaches-tile-hook");
            Check(Healthy(actor, database), "unmoved-distant-place-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "unmoved-distant-place-control",
                packet = TilePacket,
                operation = "PlaceTile",
                frameHex = Convert.ToHexString(directFrame),
                target = new { x = targetX, y = targetY },
                targetBefore,
                supportBefore,
                targetAfter = directAfter,
                distanceTiles = targetX - startTileX,
                qaTileEvents = directEventAfter - directEventBefore,
                rawPacket13Before = directRaw13Before,
                sourceBoundary = "native range control; this slice does not infer Bypass or responsibility"
            });

            // Exact TerraAngel-style finite packet13 declaration: four zero
            // flag bytes, selected item, then position in pixels. No optional
            // velocity, mount, return-potion or camera fields are included.
            byte[] positionFrame = LabClient.Packet(13, writer =>
            {
                writer.Write(actor.Slot);
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write(checked((byte)dirt.Slot));
                writer.Write(targetX * 16f); writer.Write(targetY * 16f);
            });
            int positionRaw13Before = CountRawFrames(host.ReportDirectory, 13, actor.Slot);
            await actor.SendBatch(positionFrame);
            await actor.PingAsync();
            await Task.Delay(150);
            var afterPosition = await PositionState(actor, "position-after-custom-packet13");
            int positionRaw13After = CountRawFrames(host.ReportDirectory, 13, actor.Slot);
            Check(positionRaw13After > positionRaw13Before,
                "custom-packet13-is-retained-in-raw-client-evidence");
            var lastNet = afterPosition.GetProperty("lastNetPosition");
            double expectedX = targetX * 16d, expectedY = targetY * 16d;
            Check(Math.Abs(lastNet.GetProperty("x").GetDouble() - expectedX) <= 1d &&
                Math.Abs(lastNet.GetProperty("y").GetDouble() - expectedY) <= 1d,
                "target-bouncer-accepts-custom-finite-position-declaration");
            var nativePosition = afterPosition.GetProperty("position");
            var baselinePosition = baseline.GetProperty("position");
            bool nativePositionUnchanged =
                Math.Abs(nativePosition.GetProperty("x").GetDouble() - baselinePosition.GetProperty("x").GetDouble()) <= 1d &&
                Math.Abs(nativePosition.GetProperty("y").GetDouble() - baselinePosition.GetProperty("y").GetDouble()) <= 1d &&
                afterPosition.GetProperty("tileX").GetInt32() == startTileX &&
                afterPosition.GetProperty("tileY").GetInt32() == startTileY;
            Check(nativePositionUnchanged,
                "custom-position-does-not-replace-native-player-position");

            int injectedEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            await actor.SendBatch(directFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var afterInjection = await PositionCell(targetX, targetY, "position-injected-after");
            int injectedEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            bool targetRemainsEmpty = !afterInjection.GetProperty("active").GetBoolean() &&
                afterInjection.GetProperty("type").GetInt32() == 0 &&
                afterInjection.GetProperty("wall").GetInt32() == 0 &&
                afterInjection.GetProperty("liquid").GetInt32() == 0;
            Check(injectedEventAfter > injectedEventBefore,
                "custom-position-followup-reaches-tile-hook");
            Check(targetRemainsEmpty,
                "custom-position-followup-remains-native-range-rejected-without-world-side-effect");
            Check(Healthy(actor, database), "custom-position-place-keeps-actor-healthy");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "custom-position-place-does-not-ban");
            observations.Add(new
            {
                kind = "custom-position-then-place-range-rejection",
                positionPacket = 13,
                positionFrameHex = Convert.ToHexString(positionFrame),
                tilePacket = TilePacket,
                tileFrameHex = Convert.ToHexString(directFrame),
                target = new { x = targetX, y = targetY },
                before = targetBefore,
                afterPosition,
                after = afterInjection,
                nativePositionUnchanged,
                lastNetPosition = lastNet,
                changed = !targetRemainsEmpty,
                qaTileEvents = injectedEventAfter - injectedEventBefore,
                rawPacket13Delta = positionRaw13After - positionRaw13Before,
                nativeRangeOwner = "TPlayer.position/TileX remains at the room baseline; LastNetPosition records the finite declaration",
                boundary = "no native-range bypass observed; accepted packet13 state is not legality, responsibility, or sanction proof",
                sanction = "none"
            });

            observations.Add(new
            {
                kind = "bounded-position-probes",
                probes = positionProbes,
                selection = new { x = targetX, y = targetY, support = new { x = targetX, y = targetY + 1 } },
                scope = "read-only bounded outer ring around the disposable room"
            });
            await WritePositionEvidence(report, run, preparedRoom, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WritePositionEvidence(report, run, preparedRoom, observations, "failed", failure);
            throw;
        }
        finally
        {
            await CopyPositionEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-position-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-b-position:" + name);

        async Task<JsonElement> PositionCell(int x, int y, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_position_cell " + x + " " + y);
            string path = Path.Combine(run, "m18-world-position-cell-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 position cell witness timed out: " + label);
        }

        async Task<JsonElement> PositionState(LabClient subject, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_position_state " + subject.Name);
            string path = Path.Combine(run, "m18-world-position-state-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 position state witness timed out: " + label);
        }
    }

    // Narrow P0-B follow-up: exercise one native ReplaceWall legality guard.
    // The actor first creates a WoodWall with the matching item, then keeps that
    // item selected while requesting DirtWall. The request is observed at the
    // TileEdit hook but must not replace the existing wall. No GUI, Bypass,
    // rectangle inference, or sanction is involved.
    public static async Task RunReplacementLegalityAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-b-replacement");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "";
        JsonElement? preparedRoom = null;
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0BRepObs");
            await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0BRepAct");
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            await host.ConsoleCommand("qa_prepare " + actor.Name);
            preparedRoom = await host.FixtureSnapshot();
            string roomFile = Path.Combine(run, "qa-scaffold-room.json");
            Check(File.Exists(roomFile), "existing-qa-room-recorded-inside-run");
            using var roomDocument = JsonDocument.Parse(await File.ReadAllTextAsync(roomFile));
            var room = roomDocument.RootElement;
            Check(room.GetProperty("State").GetString() == "prepared", "qa-room-prepared-state");
            int left = room.GetProperty("Left").GetInt32();
            int floor = room.GetProperty("FloorY").GetInt32();

            await host.ConsoleCommand("qa_m18_wall_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == WoodWallItem && x.Stack > 0) && actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtWallItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var woodWall = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == WoodWallItem && x.Stack > 0);
            var dirtWall = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtWallItem && x.Stack > 0);
            Check(woodWall.Item == WoodWallItem && dirtWall.Item == DirtWallItem &&
                woodWall.Slot is >= 0 and < 59 && dirtWall.Slot is >= 0 and < 59,
                "qa-prepare-issued-matching-and-mismatching-wall-materials");

            int targetX = left + 21, targetY = floor - 1;
            await actor.MoveTo((targetX - 2) * 16, (targetY - 1) * 16);
            await actor.PingAsync();
            var before = await Cell(targetX, targetY, "replacement-before");
            Check(!before.GetProperty("active").GetBoolean() &&
                before.GetProperty("wall").GetInt32() == 0,
                "replacement-target-starts-as-empty-room-cell");

            await actor.SelectItem(checked((byte)woodWall.Slot));
            int placeEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] placeFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)3); // PlaceWall
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(WoodWall); writer.Write((byte)0);
            });
            await actor.SendBatch(placeFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var placed = await Cell(targetX, targetY, "replacement-placed");
            int placeEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(placed.GetProperty("wall").GetInt32() == WoodWall,
                "matching-wall-place-establishes-native-replacement-baseline");
            Check(placeEventAfter > placeEventBefore,
                "matching-wall-place-reaches-tile-hook");

            // Deliberately keep WoodWall selected. TShock's native Bouncer checks
            // ReplaceWall against SelectedItem.createWall before the receiver.
            int invalidEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] invalidFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)22); // ReplaceWall
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(DirtWall); writer.Write((byte)0);
            });
            await actor.SendBatch(invalidFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var afterInvalid = await Cell(targetX, targetY, "replacement-invalid-after");
            int invalidEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(afterInvalid.GetProperty("active").GetBoolean() == placed.GetProperty("active").GetBoolean() &&
                afterInvalid.GetProperty("type").GetInt32() == placed.GetProperty("type").GetInt32() &&
                afterInvalid.GetProperty("wall").GetInt32() == WoodWall &&
                afterInvalid.GetProperty("liquid").GetInt32() == placed.GetProperty("liquid").GetInt32(),
                "mismatched-replace-wall-has-no-world-side-effect");
            Check(invalidEventAfter > invalidEventBefore,
                "mismatched-replace-wall-reaches-tile-hook");
            Check(Healthy(actor, database), "mismatched-replace-wall-keeps-actor-healthy");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "mismatched-replace-wall-does-not-ban");
            observations.Add(new
            {
                kind = "replace-wall-selected-item-mismatch",
                packet = TilePacket,
                operation = "ReplaceWall",
                matchingFrameHex = Convert.ToHexString(placeFrame),
                invalidFrameHex = Convert.ToHexString(invalidFrame),
                target = new { x = targetX, y = targetY },
                before,
                placed,
                afterInvalid,
                selectedItem = new { slot = woodWall.Slot, type = woodWall.Item, createWall = WoodWall },
                requestedReplacement = new { type = DirtWall, itemSlot = dirtWall.Slot, itemType = dirtWall.Item },
                qaTileEvents = new { matchingPlace = placeEventAfter - placeEventBefore, invalidReplace = invalidEventAfter - invalidEventBefore },
                nativeBoundary = "ReplaceWall editData must match SelectedItem.createWall; rejection is not Bypass or cheat attribution",
                sanction = "none"
            });

            // Now select the requested wall item and exercise the matching
            // ReplaceWall control against the wall established above. This
            // keeps the positive and negative legality observations in the
            // same authenticated session without inferring brush ownership.
            await actor.SelectItem(checked((byte)dirtWall.Slot));
            int validEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] validFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)22); // ReplaceWall
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(DirtWall); writer.Write((byte)0);
            });
            await actor.SendBatch(validFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var afterValid = await Cell(targetX, targetY, "replacement-valid-after");
            int validEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(afterValid.GetProperty("active").GetBoolean() == placed.GetProperty("active").GetBoolean() &&
                afterValid.GetProperty("type").GetInt32() == placed.GetProperty("type").GetInt32() &&
                afterValid.GetProperty("wall").GetInt32() == DirtWall &&
                afterValid.GetProperty("liquid").GetInt32() == placed.GetProperty("liquid").GetInt32(),
                "matching-replace-wall-has-native-world-side-effect");
            Check(validEventAfter > validEventBefore,
                "matching-replace-wall-reaches-tile-hook");
            Check(Healthy(actor, database), "matching-replace-wall-keeps-actor-healthy");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "matching-replace-wall-does-not-ban");
            observations.Add(new
            {
                kind = "replace-wall-selected-item-match",
                packet = TilePacket,
                operation = "ReplaceWall",
                validFrameHex = Convert.ToHexString(validFrame),
                target = new { x = targetX, y = targetY },
                before = placed,
                after = afterValid,
                selectedItem = new { slot = dirtWall.Slot, type = dirtWall.Item, createWall = DirtWall },
                requestedReplacement = new { type = DirtWall, itemSlot = dirtWall.Slot, itemType = dirtWall.Item },
                qaTileEvents = validEventAfter - validEventBefore,
                nativeBoundary = "matching SelectedItem.createWall permits the native ReplaceWall transition; this is not brush ownership or Bypass proof",
                sanction = "none"
            });

            await WriteReplacementEvidence(report, run, preparedRoom, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteReplacementEvidence(report, run, preparedRoom, observations, "failed", failure);
            throw;
        }
        finally
        {
            await CopyRelevantQaEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-replacement-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-b-replacement:" + name);

        async Task<JsonElement> Cell(int x, int y, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_cell " + x + " " + y);
            string path = Path.Combine(run, "m18-world-cell-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 replacement cell witness timed out: " + label);
        }
    }

    // Narrow P0-B follow-up: exercise one matching native ReplaceTile control
    // and its selected-item mismatch boundary. The actor first places DirtTile
    // with DirtBlock selected, then uses matching StoneBlock to ReplaceTile to
    // Stone, then keeps StoneBlock selected while requesting Dirt. The final
    // request may reach the TileEdit hook, but must not replace the existing
    // tile. No GUI, Bypass, rectangle inference, or sanction is involved.
    public static async Task RunTileReplacementLegalityAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-b-tile-replacement");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "";
        JsonElement? preparedRoom = null;
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0BTileRepObs");
            await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0BTileRepAct");
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            await host.ConsoleCommand("qa_prepare " + actor.Name);
            preparedRoom = await host.FixtureSnapshot();
            string roomFile = Path.Combine(run, "qa-scaffold-room.json");
            Check(File.Exists(roomFile), "existing-qa-room-recorded-inside-run");
            using var roomDocument = JsonDocument.Parse(await File.ReadAllTextAsync(roomFile));
            var room = roomDocument.RootElement;
            Check(room.GetProperty("State").GetString() == "prepared", "qa-room-prepared-state");
            int left = room.GetProperty("Left").GetInt32();
            int floor = room.GetProperty("FloorY").GetInt32();

            await host.ConsoleCommand("qa_m18_tile_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0) && actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == StoneBlockItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var dirt = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0);
            var stone = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == StoneBlockItem && x.Stack > 0);
            Check(dirt.Item == DirtBlockItem && dirt.Slot is >= 0 and < 59,
                "qa-prepare-issued-real-dirt-block-for-native-tile-replacement");
            Check(stone.Item == StoneBlockItem && stone.Slot is >= 0 and < 59,
                "qa-prepare-issued-real-stone-block-for-native-tile-replacement");

            int targetX = left + 23, targetY = floor - 1;
            await actor.MoveTo((targetX - 2) * 16, (targetY - 1) * 16);
            await actor.PingAsync();
            var before = await Cell(targetX, targetY, "tile-replacement-before");
            Check(!before.GetProperty("active").GetBoolean() &&
                before.GetProperty("wall").GetInt32() == 0 &&
                before.GetProperty("liquid").GetInt32() == 0,
                "tile-replacement-target-starts-as-empty-dry-room-cell");

            await actor.SelectItem(checked((byte)dirt.Slot));
            int placeEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] placeFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)1); // PlaceTile
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(DirtTile); writer.Write((byte)0);
            });
            await actor.SendBatch(placeFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var placed = await Cell(targetX, targetY, "tile-replacement-placed");
            int placeEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(placed.GetProperty("active").GetBoolean() &&
                placed.GetProperty("type").GetInt32() == DirtTile,
                "matching-tile-place-establishes-native-replacement-baseline");
            Check(placeEventAfter > placeEventBefore,
                "matching-tile-place-reaches-tile-hook");

            // First use the matching StoneBlock. This is a legal native
            // ReplaceTile control and proves the target can change through
            // the same operation before the mismatch case.
            await actor.SelectItem(checked((byte)stone.Slot));
            int validEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] validFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)21); // ReplaceTile
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(StoneTile); writer.Write((byte)0);
            });
            await actor.SendBatch(validFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var replaced = await Cell(targetX, targetY, "tile-replacement-valid-after");
            int validEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(replaced.GetProperty("active").GetBoolean() &&
                replaced.GetProperty("type").GetInt32() == StoneTile,
                "matching-replace-tile-reaches-native-world-state");
            Check(validEventAfter > validEventBefore,
                "matching-replace-tile-reaches-tile-hook");

            // Deliberately keep StoneBlock selected. TShock's native Bouncer
            // checks ReplaceTile against SelectedItem.createTile before the
            // receiver. The requested DirtTile must therefore be rejected.
            int invalidEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] invalidFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)21); // ReplaceTile
                writer.Write((short)targetX); writer.Write((short)targetY);
                writer.Write(DirtTile); writer.Write((byte)0);
            });
            await actor.SendBatch(invalidFrame);
            await actor.PingAsync();
            await Task.Delay(250);
            var afterInvalid = await Cell(targetX, targetY, "tile-replacement-invalid-after");
            int invalidEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(afterInvalid.GetProperty("active").GetBoolean() == replaced.GetProperty("active").GetBoolean() &&
                afterInvalid.GetProperty("type").GetInt32() == StoneTile &&
                afterInvalid.GetProperty("wall").GetInt32() == replaced.GetProperty("wall").GetInt32() &&
                afterInvalid.GetProperty("liquid").GetInt32() == replaced.GetProperty("liquid").GetInt32(),
                "mismatched-replace-tile-has-no-world-side-effect");
            Check(invalidEventAfter > invalidEventBefore,
                "mismatched-replace-tile-reaches-tile-hook");
            Check(Healthy(actor, database), "mismatched-replace-tile-keeps-actor-healthy");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "mismatched-replace-tile-does-not-ban");
            observations.Add(new
            {
                kind = "replace-tile-selected-item-mismatch",
                packet = TilePacket,
                operation = "ReplaceTile",
                matchingFrameHex = Convert.ToHexString(placeFrame),
                invalidFrameHex = Convert.ToHexString(invalidFrame),
                target = new { x = targetX, y = targetY },
                before,
                placed,
                replaced,
                afterInvalid,
                selectedItems = new
                {
                    place = new { slot = dirt.Slot, type = dirt.Item, createTile = DirtTile },
                    replace = new { slot = stone.Slot, type = stone.Item, createTile = StoneTile }
                },
                requestedReplacements = new
                {
                    valid = new { type = StoneTile, style = 0 },
                    invalid = new { type = DirtTile, style = 0 }
                },
                qaTileEvents = new
                {
                    matchingPlace = placeEventAfter - placeEventBefore,
                    matchingReplace = validEventAfter - validEventBefore,
                    invalidReplace = invalidEventAfter - invalidEventBefore
                },
                nativeBoundary = "ReplaceTile editData must match SelectedItem.createTile for the legal replacement; the mismatched request is rejected without world mutation and is not Bypass or cheat attribution",
                sanction = "none"
            });

            await WriteTileReplacementEvidence(report, run, preparedRoom, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteTileReplacementEvidence(report, run, preparedRoom, observations, "failed", failure);
            throw;
        }
        finally
        {
            await CopyRelevantQaEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-tile-replacement-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-b-tile-replacement:" + name);

        async Task<JsonElement> Cell(int x, int y, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_cell " + x + " " + y);
            string path = Path.Combine(run, "m18-world-cell-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 tile replacement cell witness timed out: " + label);
        }
    }

    // Narrow P0-B follow-up: isolate the protected-region tile permission
    // branch from the older movement/range probe. A normal actor sends one
    // real PlaceTile request into the named denied region; the native cell
    // must remain unchanged while the TileEdit hook records the request.
    public static async Task RunProtectedTilePermissionAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-b-region-tile");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string status = "failed", failure = "";
        JsonElement? preparedRoom = null;
        var observations = new List<object>();
        LabClient? observer = null;
        LabClient? actor = null;
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "fresh-isolated-ban-table");

            observer = await host.Connect("M18P0BRegionObs");
            await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            actor = await host.Connect("M18P0BRegionAct");
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            await host.ConsoleCommand("qa_prepare " + actor.Name);
            preparedRoom = await host.FixtureSnapshot();
            using var roomDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(run, "qa-scaffold-room.json")));
            var room = roomDocument.RootElement;
            Check(room.GetProperty("State").GetString() == "prepared", "qa-room-prepared-state");
            int protectedX = room.GetProperty("RegionX").GetInt32() + 1;
            int protectedY = room.GetProperty("RegionY").GetInt32() + 1;
            bool actorCanBuild = preparedRoom.Value.GetProperty("players").EnumerateArray()
                .Single(x => x.GetProperty("Name").GetString() == actor.Name)
                .GetProperty("regionCanBuild").GetBoolean();
            Check(!actorCanBuild, "actor-is-denied-by-existing-qa-region");

            await host.ConsoleCommand("qa_m18_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var dirt = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0);
            Check(dirt.Item == DirtBlockItem && dirt.Slot is >= 0 and < 59,
                "qa-prepare-issued-real-dirt-block");
            await actor.SelectItem(checked((byte)dirt.Slot));

            var before = await Cell(protectedX, protectedY, "region-tile-before");
            int tileEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] frame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)1); // PlaceTile
                writer.Write((short)protectedX); writer.Write((short)protectedY);
                writer.Write(DirtTile); writer.Write((byte)0);
            });
            await actor.SendBatch(frame);
            await actor.PingAsync();
            var after = await Cell(protectedX, protectedY, "region-tile-after");
            int tileEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(after.GetProperty("active").GetBoolean() == before.GetProperty("active").GetBoolean() &&
                after.GetProperty("type").GetInt32() == before.GetProperty("type").GetInt32() &&
                after.GetProperty("wall").GetInt32() == before.GetProperty("wall").GetInt32() &&
                after.GetProperty("liquid").GetInt32() == before.GetProperty("liquid").GetInt32(),
                "denied-region-tile-request-has-no-world-side-effect");
            Check(tileEventAfter > tileEventBefore,
                "denied-region-tile-request-reaches-tile-hook");
            Check(Healthy(actor, database), "denied-region-tile-request-keeps-actor-healthy");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "denied-region-tile-request-does-not-ban");
            observations.Add(new
            {
                kind = "region-permission-tile-rejection-isolated",
                packet = TilePacket,
                operation = "PlaceTile",
                frameHex = Convert.ToHexString(frame),
                target = new { x = protectedX, y = protectedY },
                before,
                after,
                actorCanBuild,
                itemSlot = dirt.Slot,
                itemType = dirt.Item,
                tileType = DirtTile,
                qaTileEvents = tileEventAfter - tileEventBefore,
                boundary = "TShock regionCanBuild=false is the legal rejection control; hook visibility does not establish Bypass or cheating attribution",
                sanction = "none"
            });

            await WriteRegionTilePermissionEvidence(report, run, preparedRoom, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteRegionTilePermissionEvidence(report, run, preparedRoom, observations, "failed", failure);
            throw;
        }
        finally
        {
            if (actor is not null) await actor.DisposeAsync();
            if (observer is not null) await observer.DisposeAsync();
            await CopyRelevantQaEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-region-tile-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-b-region-tile:" + name);

        async Task<JsonElement> Cell(int x, int y, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_cell " + x + " " + y);
            string path = Path.Combine(run, "m18-world-cell-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 region tile cell witness timed out: " + label);
        }
    }

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-b");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "";
        JsonElement? preparedRoom = null;
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0BObserver");
            await observer.Join(); await observer.RegisterAndLogin();
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0BActor");
            await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            // The room is the existing disposable scaffold. It is not a second
            // world-edit fixture and does not clear or rewrite any prior world.
            await host.ConsoleCommand("qa_prepare " + actor.Name);
            preparedRoom = await host.FixtureSnapshot();
            var roomFile = Path.Combine(run, "qa-scaffold-room.json");
            Check(File.Exists(roomFile), "existing-qa-room-recorded-inside-run");
            using var roomDocument = JsonDocument.Parse(await File.ReadAllTextAsync(roomFile));
            var room = roomDocument.RootElement;
            Check(room.GetProperty("State").GetString() == "prepared", "qa-room-prepared-state");
            int left = room.GetProperty("Left").GetInt32();
            int floor = room.GetProperty("FloorY").GetInt32();

            await actor.Drain(TimeSpan.FromMilliseconds(500));
            await host.ConsoleCommand("qa_m18_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var dirt = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtBlockItem && x.Stack > 0);
            Check(dirt.Item == DirtBlockItem && dirt.Slot is >= 0 and < 59,
                "qa_prepare-issued-real-dirt-block-for-native-place");
            await actor.SelectItem(checked((byte)dirt.Slot));

            await host.ConsoleCommand("qa_m18_wall_materials " + actor.Name);
            await actor.WaitUntil(() => actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == WoodWallItem && x.Stack > 0) && actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == DirtWallItem && x.Stack > 0) && actor.InventoryUpdates.Any(x => x.Player == actor.Slot &&
                x.Item == IronHammerItem && x.Stack > 0), TimeSpan.FromSeconds(8));
            var woodWall = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == WoodWallItem && x.Stack > 0);
            var dirtWall = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == DirtWallItem && x.Stack > 0);
            var hammer = actor.InventoryUpdates.LastOrDefault(x => x.Player == actor.Slot &&
                x.Item == IronHammerItem && x.Stack > 0);
            Check(woodWall.Item == WoodWallItem && dirtWall.Item == DirtWallItem &&
                hammer.Item == IronHammerItem && woodWall.Slot is >= 0 and < 59 &&
                dirtWall.Slot is >= 0 and < 59 && hammer.Slot is >= 0 and < 59,
                "qa_prepare-issued-wall-materials-and-hammer-for-native-chain");

            // The scaffold's named region is a legal protected-world
            // counterexample. It exercises the same native packet17 wall
            // entry with a real item, but must not mutate the protected cell.
            int protectedX = room.GetProperty("RegionX").GetInt32() + 1;
            int protectedY = room.GetProperty("RegionY").GetInt32() + 1;
            bool actorCanBuildProtected = preparedRoom.Value.GetProperty("players").EnumerateArray()
                .Single(x => x.GetProperty("Name").GetString() == actor.Name)
                .GetProperty("regionCanBuild").GetBoolean();
            Check(!actorCanBuildProtected, "actor-is-denied-by-existing-qa-region");
            var protectedBefore = await Cell(protectedX, protectedY, "region-protected-before");
            await actor.SelectItem(checked((byte)woodWall.Slot));
            int protectedEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] protectedFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)3); // PlaceWall
                writer.Write((short)protectedX); writer.Write((short)protectedY);
                writer.Write(WoodWall); writer.Write((byte)0);
            });
            await actor.SendBatch(protectedFrame);
            await actor.PingAsync();
            var protectedAfter = await Cell(protectedX, protectedY, "region-protected-after");
            int protectedEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(protectedAfter.GetProperty("active").GetBoolean() == protectedBefore.GetProperty("active").GetBoolean() &&
                protectedAfter.GetProperty("type").GetInt32() == protectedBefore.GetProperty("type").GetInt32() &&
                protectedAfter.GetProperty("wall").GetInt32() == protectedBefore.GetProperty("wall").GetInt32() &&
                protectedAfter.GetProperty("liquid").GetInt32() == protectedBefore.GetProperty("liquid").GetInt32(),
                "region-protected-wall-request-has-no-world-side-effect");
            Check(protectedEventAfter > protectedEventBefore,
                "region-protected-wall-request-reaches-tile-hook");
            Check(Healthy(actor, database), "region-protected-wall-request-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "region-permission-wall-rejection",
                packet = TilePacket,
                operation = "PlaceWall",
                frameHex = Convert.ToHexString(protectedFrame),
                target = new { x = protectedX, y = protectedY },
                before = protectedBefore,
                after = protectedAfter,
                actorCanBuildProtected,
                qaTileEvents = protectedEventAfter - protectedEventBefore,
                boundary = "existing TShock region permission is a legal rejection control; no Bypass marker is inferred",
                sanction = "none"
            });

            // Keep the same ordinary actor and protected coordinate, but use
            // the tile branch with a real DirtBlock. This is a narrow
            // permission counterexample: the packet must reach the audited
            // TileEdit hook, while the region gate prevents any tile, wall or
            // liquid mutation. It does not infer Bypass or cheating.
            await actor.SelectItem(checked((byte)dirt.Slot));
            int protectedTileEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] protectedTileFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)1); // PlaceTile
                writer.Write((short)protectedX); writer.Write((short)protectedY);
                writer.Write(DirtTile); writer.Write((byte)0);
            });
            await actor.SendBatch(protectedTileFrame);
            await actor.PingAsync();
            var protectedTileAfter = await Cell(protectedX, protectedY, "region-protected-tile-after");
            int protectedTileEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(protectedTileAfter.GetProperty("active").GetBoolean() == protectedBefore.GetProperty("active").GetBoolean() &&
                protectedTileAfter.GetProperty("type").GetInt32() == protectedBefore.GetProperty("type").GetInt32() &&
                protectedTileAfter.GetProperty("wall").GetInt32() == protectedBefore.GetProperty("wall").GetInt32() &&
                protectedTileAfter.GetProperty("liquid").GetInt32() == protectedBefore.GetProperty("liquid").GetInt32(),
                "region-protected-tile-request-has-no-world-side-effect");
            Check(protectedTileEventAfter > protectedTileEventBefore,
                "region-protected-tile-request-reaches-tile-hook");
            Check(Healthy(actor, database), "region-protected-tile-request-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "region-permission-tile-rejection",
                packet = TilePacket,
                operation = "PlaceTile",
                frameHex = Convert.ToHexString(protectedTileFrame),
                target = new { x = protectedX, y = protectedY },
                before = protectedBefore,
                after = protectedTileAfter,
                actorCanBuildProtected,
                qaTileEvents = protectedTileEventAfter - protectedTileEventBefore,
                itemSlot = dirt.Slot,
                itemType = dirt.Item,
                tileType = DirtTile,
                boundary = "existing TShock region permission is a legal tile rejection control; no Bypass marker is inferred",
                sanction = "none"
            });

            int wallX = left + 21, wallY = floor - 1;
            await actor.PingAsync();
            var wallBefore = await Cell(wallX, wallY, "wall-before");
            Check(!wallBefore.GetProperty("active").GetBoolean() &&
                wallBefore.GetProperty("wall").GetInt32() == 0,
                "direct-wall-target-is-fresh-empty-room-cell");

            await actor.SelectItem(checked((byte)woodWall.Slot));
            int wallPlaceEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] wallPlaceFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)3); // PlaceWall
                writer.Write((short)wallX); writer.Write((short)wallY);
                writer.Write(WoodWall); writer.Write((byte)0);
            });
            await actor.SendBatch(wallPlaceFrame);
            await actor.PingAsync();
            var wallPlaced = await Cell(wallX, wallY, "wall-placed");
            int wallPlaceEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(wallPlaced.GetProperty("wall").GetInt32() == WoodWall,
                "direct-wall-place-reaches-native-world-state");
            Check(wallPlaceEventAfter > wallPlaceEventBefore,
                "direct-wall-place-reaches-tshock-tile-hook");
            Check(Healthy(actor, database), "direct-wall-place-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "direct-wall-place",
                packet = TilePacket,
                operation = "PlaceWall",
                frameHex = Convert.ToHexString(wallPlaceFrame),
                target = new { x = wallX, y = wallY },
                before = wallBefore,
                after = wallPlaced,
                qaTileEvents = wallPlaceEventAfter - wallPlaceEventBefore,
                itemSlot = woodWall.Slot,
                itemType = woodWall.Item,
                wallType = WoodWall
            });

            await actor.SelectItem(checked((byte)dirtWall.Slot));
            int wallReplaceEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] wallReplaceFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)22); // ReplaceWall
                writer.Write((short)wallX); writer.Write((short)wallY);
                writer.Write(DirtWall); writer.Write((byte)0);
            });
            await actor.SendBatch(wallReplaceFrame);
            await actor.PingAsync();
            var wallReplaced = await Cell(wallX, wallY, "wall-replaced");
            int wallReplaceEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(wallReplaced.GetProperty("wall").GetInt32() == DirtWall,
                "direct-wall-replace-reaches-native-world-state");
            Check(wallReplaceEventAfter > wallReplaceEventBefore,
                "direct-wall-replace-reaches-tshock-tile-hook");
            Check(Healthy(actor, database), "direct-wall-replace-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "direct-wall-replace",
                packet = TilePacket,
                operation = "ReplaceWall",
                frameHex = Convert.ToHexString(wallReplaceFrame),
                target = new { x = wallX, y = wallY },
                before = wallPlaced,
                after = wallReplaced,
                qaTileEvents = wallReplaceEventAfter - wallReplaceEventBefore,
                itemSlot = dirtWall.Slot,
                itemType = dirtWall.Item,
                wallType = DirtWall,
                workUnits = 2
            });

            await actor.SelectItem(checked((byte)hammer.Slot));
            int wallKillEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] wallKillFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)2); // KillWall
                writer.Write((short)wallX); writer.Write((short)wallY);
                writer.Write((short)0); writer.Write((byte)0);
            });
            await actor.SendBatch(wallKillFrame);
            await actor.PingAsync();
            var wallKilled = await Cell(wallX, wallY, "wall-killed");
            int wallKillEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(wallKilled.GetProperty("wall").GetInt32() == 0,
                "direct-wall-kill-reaches-native-world-state");
            Check(wallKillEventAfter > wallKillEventBefore,
                "direct-wall-kill-reaches-tshock-tile-hook");
            Check(Healthy(actor, database), "direct-wall-kill-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "direct-wall-kill",
                packet = TilePacket,
                operation = "KillWall",
                frameHex = Convert.ToHexString(wallKillFrame),
                target = new { x = wallX, y = wallY },
                before = wallReplaced,
                after = wallKilled,
                qaTileEvents = wallKillEventAfter - wallKillEventBefore,
                itemSlot = hammer.Slot,
                itemType = hammer.Item
            });

            int tileX = left + 18, tileY = floor - 1;
            await actor.SelectItem(checked((byte)dirt.Slot));
            int liquidX = left + 19, liquidY = floor - 1;
            await actor.PingAsync();
            var tileBefore = await Cell(tileX, tileY, "tile-before");
            Check(!tileBefore.GetProperty("active").GetBoolean() &&
                tileBefore.GetProperty("liquid").GetInt32() == 0,
                "direct-tile-target-is-fresh-dry-room-cell");

            int tileRelayBefore = observer.PacketCount(20);
            int tileEventBefore = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            byte[] tileFrame = LabClient.Packet(TilePacket, writer =>
            {
                writer.Write((byte)1); // PlaceTile
                writer.Write((short)tileX); writer.Write((short)tileY);
                writer.Write(DirtTile); writer.Write((byte)0);
            });
            await actor.SendBatch(tileFrame);
            await actor.PingAsync();
            var tileAfter = await Cell(tileX, tileY, "tile-after");
            int tileEventAfter = CountQaEvents(host.ReportDirectory, "tile-edit-packet");
            Check(tileAfter.GetProperty("active").GetBoolean() &&
                tileAfter.GetProperty("type").GetInt32() == DirtTile,
                "direct-tile-place-reaches-native-world-state");
            Check(tileEventAfter > tileEventBefore,
                "direct-tile-place-reaches-tshock-tile-hook");
            observations.Add(new
            {
                kind = "direct-tile-place",
                packet = TilePacket,
                operation = "PlaceTile",
                frameHex = Convert.ToHexString(tileFrame),
                target = new { x = tileX, y = tileY },
                before = tileBefore,
                after = tileAfter,
                observerTileSquarePackets = observer.PacketCount(20) - tileRelayBefore,
                qaTileEvents = tileEventAfter - tileEventBefore,
                itemSlot = dirt.Slot,
                itemType = dirt.Item
            });
            Check(Healthy(actor, database), "direct-tile-place-keeps-actor-healthy");

            await actor.PingAsync();
            var liquidBefore = await Cell(liquidX, liquidY, "liquid-before");
            int liquidEventBefore = CountQaEvents(host.ReportDirectory, "liquid-set-packet");
            byte[] liquidFrame = LabClient.Packet(LiquidPacket, writer =>
            {
                writer.Write((short)liquidX); writer.Write((short)liquidY);
                writer.Write((byte)0); // removal: no bucket provenance is inferred
                writer.Write((byte)255); // native removal type
            });
            await actor.SendBatch(liquidFrame);
            await actor.PingAsync();
            var liquidAfter = await Cell(liquidX, liquidY, "liquid-after");
            int liquidEventAfter = CountQaEvents(host.ReportDirectory, "liquid-set-packet");
            Check(liquidEventAfter > liquidEventBefore,
                "direct-liquid-removal-reaches-tshock-liquid-hook");
            Check(liquidAfter.GetProperty("liquid").GetInt32() == liquidBefore.GetProperty("liquid").GetInt32() &&
                liquidAfter.GetProperty("active").GetBoolean() == liquidBefore.GetProperty("active").GetBoolean(),
                "direct-liquid-removal-keeps-dry-cell-state");
            observations.Add(new
            {
                kind = "direct-liquid-removal",
                packet = LiquidPacket,
                frameHex = Convert.ToHexString(liquidFrame),
                target = new { x = liquidX, y = liquidY },
                before = liquidBefore,
                after = liquidAfter,
                qaLiquidEvents = liquidEventAfter - liquidEventBefore,
                removalType = 255
            });
            Check(Healthy(actor, database), "direct-liquid-keeps-actor-healthy");

            // The native liquid threshold is deliberately isolated with the
            // existing named permission so this slice measures F07 rather than
            // TShock's independent anti-liquid threshold. The group is created
            // only after a fresh-database check and is restored in finally.
            const string group = "M18WorldEditLiquidOnly";
            Check(Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", group) == 0,
                "fresh-temporary-liquid-threshold-exception-group");
            bool groupAssigned = false;
            try
            {
                await host.ConsoleCommand("group add " + group + " " + LiquidBypassPermission);
                await Until(() => Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", group) == 1,
                    "temporary-liquid-group-created");
                await host.ConsoleCommand("group parent " + group + " default");
                await Until(() => Scalar(database, "SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default'", group) == 1,
                    "temporary-liquid-group-parented");
                await host.ConsoleCommand("user group " + actor.Name + " " + group);
                groupAssigned = true;
                await Until(() => Scalar(database, "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup=$group", actor.Name, group) == 1,
                    "actor-temporarily-has-only-liquid-threshold-exception");

                int floodX = left + 20, floodY = floor - 1;
                await actor.PingAsync();
                var floodBefore = await Cell(floodX, floodY, "flood-before");
                var floodStatsBefore = CountLiquidEvents(host.ReportDirectory);
                int logStart = host.ConsoleLines().Length;
                const int priorWorkUnits = 6; // wall place + wall replace(2) + wall kill + tile + liquid
                int acceptedBeforeBlock = CandidateWorkBudget - priorWorkUnits;
                int floodFrames = acceptedBeforeBlock + 1; // last frame must be stopped
                var packets = Enumerable.Range(0, floodFrames)
                    .Select(_ => LabClient.Packet(LiquidPacket, writer =>
                    {
                        writer.Write((short)floodX); writer.Write((short)floodY);
                        writer.Write((byte)0); writer.Write((byte)255);
                    }))
                    .ToArray();
                await actor.SendBatch(packets);
                await actor.PingAsync();
                // Do not read the journal while GameplayScaffold is appending
                // it: a concurrent reader can stop the passive recorder. The
                // native adapter has already parsed the batch; this quiet
                // interval lets the bounded QA observer drain before the
                // post-batch count is taken.
                await Task.Delay(2500);
                var floodAfter = await Cell(floodX, floodY, "flood-after");
                var floodConsole = host.ConsoleLines().Skip(logStart).ToArray();
                var floodStatsAfter = CountLiquidEvents(host.ReportDirectory);
                int floodHookEvents = floodStatsAfter.Total - floodStatsBefore.Total;
                int floodHandledEvents = floodStatsAfter.Handled - floodStatsBefore.Handled;
                int floodUnhandledEvents = floodStatsAfter.Unhandled - floodStatsBefore.Unhandled;
                string? resourceLine = floodConsole.FirstOrDefault(x =>
                    x.Contains("rule=F07.WorldEditDensityBudget", StringComparison.Ordinal) &&
                    x.Contains("verdict=ResourceAbuse", StringComparison.Ordinal) &&
                    x.Contains("reason=world-edit-window-", StringComparison.Ordinal) &&
                    x.Contains("packet=48", StringComparison.Ordinal) &&
                    x.Contains("action=Block", StringComparison.Ordinal) &&
                    x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase));
                Check(resourceLine is not null,
                    "liquid-bounded-queue-capacity-or-work-block-is-written-before-native-receiver");
                Check(floodHookEvents > 0 && floodHookEvents <= acceptedBeforeBlock &&
                    floodHookEvents < floodFrames && floodHandledEvents > 0 && floodUnhandledEvents > 0,
                    "only-observed-pre-budget-liquid-frames-reach-tshock-hook");
                Check(floodAfter.GetProperty("active").GetBoolean() == floodBefore.GetProperty("active").GetBoolean() &&
                    floodAfter.GetProperty("liquid").GetInt32() == floodBefore.GetProperty("liquid").GetInt32(),
                    "blocked-liquid-frame-has-no-world-side-effect");
                Check(Healthy(actor, database), "liquid-work-stop-loss-keeps-actor-connected");
                Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                    "world-edit-density-stop-loss-does-not-ban");
                observations.Add(new
                {
                    kind = "liquid-density-stop-loss",
                    packet = LiquidPacket,
                    candidateWorkBudget = CandidateWorkBudget,
                    priorWorkUnits,
                    acceptedBeforeBlock,
                    floodFrames,
                    target = new { x = floodX, y = floodY },
                    before = floodBefore,
                    after = floodAfter,
                    qaLiquidEvents = floodHookEvents,
                    qaLiquidHandledEvents = floodHandledEvents,
                    qaLiquidUnhandledEvents = floodUnhandledEvents,
                    deliveryGap = floodFrames - floodHookEvents,
                    evidenceRelation = "hook-event count is a lower bound on TCP frames observed before the bounded drain; it must not exceed the pre-block work budget",
                    resourceReason = resourceLine,
                    console = floodConsole.Where(x => x.Contains("F07.WorldEditDensityBudget", StringComparison.Ordinal)).Take(4).ToArray(),
                    thresholdPermission = LiquidBypassPermission,
                    sanction = "none; ResourceAbuse is a bounded stop-loss, not account-cheat proof"
                });
            }
            finally
            {
                if (groupAssigned)
                {
                    await host.ConsoleCommand("user group " + actor.Name + " default");
                    await Until(() => Scalar(database, "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                        "actor-liquid-threshold-exception-restored");
                }
            }

            Check(Scalar(database, "SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                "actor-group-restored-after-density-slice");
            Check(Healthy(actor, database), "actor-healthy-after-group-restoration");
            await WriteEvidence(report, run, preparedRoom, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteEvidence(report, run, preparedRoom, observations, "failed", failure);
            throw;
        }
        finally
        {
            await CopyRelevantQaEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-b:" + name);

        async Task<JsonElement> Cell(int x, int y, string label)
        {
            DateTimeOffset requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m18_cell " + x + " " + y);
            string path = Path.Combine(run, "m18-world-cell-latest.json");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(path);
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        {
                            await File.WriteAllTextAsync(Path.Combine(report, label + ".json"), text);
                            return document.RootElement.Clone();
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("M18 cell witness timed out: " + label);
        }
    }

    private static bool Healthy(LabClient actor, string database)
        => actor.Authenticated && !actor.Closed && actor.DisconnectReason is null &&
            Scalar(database, "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0;

    private static int CountQaEvents(string report, string kind)
    {
        string needle = "\"kind\":\"" + kind + "\"";
        return Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines)
            .Count(line => line.Contains(needle, StringComparison.Ordinal));
    }

    private sealed record LiquidEventCounts(int Total, int Handled, int Unhandled);

    private static LiquidEventCounts CountLiquidEvents(string report)
    {
        int total = 0, handled = 0;
        foreach (string line in Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines))
        {
            if (!line.Contains("\"kind\":\"liquid-set-packet\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            bool wasHandled = document.RootElement.GetProperty("payload")
                .GetProperty("handledObserved").GetBoolean();
            total++;
            if (wasHandled) handled++;
        }
        return new(total, handled, total - handled);
    }

    private static int CountRawFrames(string report, int packetId, int playerIndex)
    {
        int count = 0;
        foreach (string line in Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines))
        {
            if (!line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            var payload = document.RootElement.GetProperty("payload");
            if (payload.GetProperty("packetId").GetInt32() == packetId &&
                payload.GetProperty("playerIndex").GetInt32() == playerIndex) count++;
        }
        return count;
    }

    private static async Task WriteEvidence(string report, string run, JsonElement? preparedRoom,
        IReadOnlyList<object> observations, string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / C2S packet17 and packet48",
                source = "TerraAngel WorldEditBrush.cs -> native Tile(17)/LiquidSet(48) requests",
                qaPreparation = "existing GameplayScaffold qa_prepare; read-only qa_m18_cell witness",
                preparedRoom, observations,
                sanctionBoundary = "F07 resource stop-loss only; no permission verdict, permanent ban, or formal qualification"
            }, Json));

    private static async Task WritePositionEvidence(string report, string run, JsonElement? preparedRoom,
        IReadOnlyList<object> observations, string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-position-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / C2S packet13 PlayerControls and packet17 Tile",
                source = "TerraAngel WorldEditBrush.cs -> custom packet13 position -> native Tile(17) request",
                qaPreparation = "existing GameplayScaffold qa_prepare; bounded read-only qa_m18_position_cell and qa_m18_position_state witnesses",
                preparedRoom, observations,
                sanctionBoundary = "finite packet13 acceptance is a source/runtime observation only; no Bypass, permission verdict, permanent ban, or formal qualification"
            }, Json));

    private static async Task WriteReplacementEvidence(string report, string run, JsonElement? preparedRoom,
        IReadOnlyList<object> observations, string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-replacement-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / C2S packet17 Tile",
                source = "TShock Bouncer.OnTileEdit -> ReplaceWall selected-item createWall legality guard",
                qaPreparation = "existing GameplayScaffold qa_prepare; read-only qa_m18_cell witness",
                preparedRoom, observations,
                sanctionBoundary = "native replacement legality observation only; no Bypass, permanent ban, or formal qualification"
            }, Json));

    private static async Task WriteTileReplacementEvidence(string report, string run, JsonElement? preparedRoom,
        IReadOnlyList<object> observations, string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-tile-replacement-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / C2S packet17 Tile",
                source = "TShock Bouncer.OnTileEdit -> ReplaceTile selected-item createTile legality guard",
                qaPreparation = "existing GameplayScaffold qa_prepare and qa_m18_tile_materials; read-only qa_m18_cell witness",
                preparedRoom, observations,
                sanctionBoundary = "native replacement legality observation only; no Bypass, permanent ban, or formal qualification"
            }, Json));

    private static async Task WriteRegionTilePermissionEvidence(string report, string run,
        JsonElement? preparedRoom, IReadOnlyList<object> observations, string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-b-region-tile-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / C2S packet17 Tile",
                source = "TShock regionCanBuild -> Bouncer.OnTileEdit -> native Tile(17)",
                qaPreparation = "existing GameplayScaffold qa_prepare + qa_m18_materials; read-only qa_m18_cell witness",
                preparedRoom, observations,
                sanctionBoundary = "legal region permission rejection observation only; no Bypass, permanent ban, or formal qualification"
            }, Json));

    private static async Task CopyRelevantQaEvents(string report, string destination)
    {
        string[] lines = Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("\"kind\":\"tile-edit-packet\"", StringComparison.Ordinal) ||
                line.Contains("\"kind\":\"liquid-set-packet\"", StringComparison.Ordinal))
            .Take(4096)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(destination, "qa-world-edit-events.jsonl"), lines);
    }

    private static async Task CopyPositionEvents(string report, string destination)
    {
        string[] lines = Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("\"kind\":\"m18-world-position-", StringComparison.Ordinal) ||
                line.Contains("\"kind\":\"tile-edit-packet\"", StringComparison.Ordinal) ||
                line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal) &&
                line.Contains("\"packetId\":13", StringComparison.Ordinal))
            .Take(4096)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(destination, "qa-world-position-events.jsonl"), lines);
    }

    private static long Scalar(string database, string sql, params string[] values)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$name", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$name", values.ElementAtOrDefault(0));
        if (sql.Contains("$group", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$group", values.ElementAtOrDefault(1));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task Until(Func<bool> predicate, string label)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }
}
