using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Bounded, one-time reconstruction of the retained M9 checkerboard fixture.
/// Advances only through the ordinary server loop; the off control disposes one guard.</summary>
public sealed partial class GameplayScaffold
{
    private const int M10LiquidLeft = 2137, M10LiquidTop = 58, M10LiquidWidth = 242,
        M10LiquidHeight = 202, M10LiquidHalo = 4;
    private const long M10LiquidExpected = 21600L * 255;
    private const int M10LiquidRandomSeed = 0x4D31304C;
    private bool m10LiquidAttempted, m10LiquidSeeded, m10LiquidFinished, m10LiquidGuardEnabled;
    private Hook? m10LiquidTiming;
    private object? m10LiquidGuard;
    private string? m10LiquidOutput;
    private long m10LiquidStart;
    private TimeSpan m10LiquidCpuStart;
    private readonly List<double> m10LiquidTimes = new(4096);
    private int m10LiquidTimesDropped, m10LiquidQueuePeak, m10LiquidBufferPeak;
    private int m10LiquidRandomResetCount;
    private readonly List<object> m10LiquidCheckpoints = new(4);
    private object? m10LiquidSeedOrder;
    private M11LiquidStepTrace? m11LiquidTrace;

    private static bool M10LiquidQuiescent() => Liquid.numLiquid == 0 &&
        LiquidBuffer.numLiquidBuffer == 0 && Liquid.wetCounter == 0 && !Liquid.panicMode &&
        !Liquid.quickSettle && !Liquid.stuck;

    private void PrepareM10LiquidControl(string[] args)
    {
        Require(args.Length == 3 && args[0] is "on" or "off" && !m10LiquidAttempted,
            "Use qa_m10_liquid_control <on|off> <ordinary actor> <ordinary peer>, once per run.");
        var actor = ResolvePlayer(args[1]); var peer = ResolvePlayer(args[2]);
        Require(actor.Index != peer.Index && actor.Index < 15 && peer.Index < 15 &&
            Main.player.Count(p => p is { active: true }) == 2, "Exactly two ordinary active players required.");
        foreach (var player in new[] { actor, peer })
            Require(player.HasSentInventory && !player.HasPermission("anticheat.bypass") &&
                !player.HasPermission(Permissions.bypassssc), "Authenticated ordinary SSC players required.");
        Require(room is null && m9Facility is null, "Fresh isolated world without another scaffold room or facility required.");
        EnsureM11LiquidPreparationTrace();
        Require(Main.netMode == 2 && !WorldGen.isGeneratingOrLoadingWorld && !Main.Setting_UseReducedMaxLiquids &&
            Liquid.cycles == 10 && Liquid.curMaxLiquid == 24500, "Ordinary two-player native scheduler configuration required.");
        Require(M10LiquidLeft - M10LiquidHalo >= 20 && M10LiquidTop - M10LiquidHalo >= 20 &&
            M10LiquidLeft + M10LiquidWidth + M10LiquidHalo < Main.maxTilesX - 20 &&
            M10LiquidTop + M10LiquidHeight + M10LiquidHalo < Main.maxTilesY - 20, "Historical rectangle unavailable.");
        var area = new Microsoft.Xna.Framework.Rectangle(M10LiquidLeft - 1, M10LiquidTop - 1,
            M10LiquidWidth + 2, M10LiquidHeight + 2);
        Require(!Main.chest.Any(c => c is not null && area.Contains(c.x, c.y)) &&
            !Main.sign.Any(s => s is not null && area.Contains(s.x, s.y)) &&
            !TileEntity.ByPosition.Keys.Any(p => area.Contains(p.X, p.Y)) &&
            !TShock.Regions.Regions.Any(r => r.Area.Intersects(area)), "No stored objects or protected regions may overlap.");
        for (int x = M10LiquidLeft; x < M10LiquidLeft + M10LiquidWidth; x++)
            for (int y = M10LiquidTop; y < M10LiquidTop + M10LiquidHeight; y++)
                Require(Main.tile[x, y] is { liquid: 0 } tile && !tile.checkingLiquid(), "Historical setup requires dry unqueued cells.");

        var plugin = M5Plugin();
        Require(plugin.GetType().GetField("_scope", PrivateM5)?.GetValue(plugin)?.ToString() == "TestLab",
            "Liquid control is available only in the validated isolated TestLab scope.");
        m10LiquidGuard = plugin.GetType().GetField("_liquidExecution", PrivateM5)?.GetValue(plugin);
        Require(m10LiquidGuard is IDisposable && Equals(M10LiquidGuardValue("ContractHealthy"), true),
            "The locked, healthy liquid execution guard must exist before either run.");
        m10LiquidAttempted = true; m10LiquidGuardEnabled = args[0] == "on";
        m10LiquidOutput = Path.Combine(output!, "m10-liquid-control-native"); Directory.CreateDirectory(m10LiquidOutput);
        if (!m10LiquidGuardEnabled) ((IDisposable)m10LiquidGuard!).Dispose();
        Record("m10-liquid-control-treatment", new { guardEnabled = m10LiquidGuardEnabled,
            disposedOnly = m10LiquidGuardEnabled ? null : "AntiCheatPlugin._liquidExecution",
            actor = actor.Account.ID, peer = peer.Account.ID, fixtureArtificial = true,
            randomSeed = M10LiquidRandomSeed, randomResetCount = 1,
            randomScope = "M11 sets the actual RNG and world clock once at first naturally scheduled UpdateLiquid entry; later scopes and clocks advance naturally" });
        m11LiquidTrace = new(M10LiquidLeft, M10LiquidTop, M10LiquidWidth, M10LiquidHeight,
            () => m10LiquidGuardEnabled ? Convert.ToInt64(M10LiquidGuardValue("Allowed")) : 0,
            () => m10LiquidGuardEnabled ? Convert.ToInt64(M10LiquidGuardValue("BudgetPaused")) : 0,
            SeedM11LiquidAtActualEntry, M11LiquidPreparationReady);
        m11LiquidTrace.Install();
        m10LiquidTiming = new Hook(typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid))!, (Action<Action>)(original =>
        {
            long start = Stopwatch.GetTimestamp();
            try { original(); }
            finally
            {
                if (m10LiquidSeeded)
                {
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (m10LiquidTimes.Count < 8192) m10LiquidTimes.Add(elapsed); else m10LiquidTimesDropped++;
                    m10LiquidQueuePeak = Math.Max(m10LiquidQueuePeak, Liquid.numLiquid);
                    m10LiquidBufferPeak = Math.Max(m10LiquidBufferPeak, LiquidBuffer.numLiquidBuffer);
                }
            }
        }));
    }

    private void SeedM11LiquidAtActualEntry()
    {
        Require(!m10LiquidSeeded && M10LiquidQuiescent(), "First actual scheduler entry must still have naturally empty queues.");
        DisposeM11LiquidPreparationTrace();
        for (int x = M10LiquidLeft; x < M10LiquidLeft + M10LiquidWidth; x++)
            for (int y = M10LiquidTop; y < M10LiquidTop + M10LiquidHeight; y++)
                Require(Main.tile[x, y].liquid == 0 && !Main.tile[x, y].checkingLiquid(), "Actual entry must retain the dry unqueued initial rectangle.");
        CaptureM10LiquidCheckpoint("before-layout");
        // One artificial initial condition at the actual native entrance, after WorldGen's
        // RandomSwap scope was entered. No future clock/RNG reset, freeze or queue clearing.
        Main.time = 15000; Main.dayTime = true;
        var controlledRandom = new Terraria.Utilities.UnifiedRandom(M10LiquidRandomSeed);
        Main.rand = controlledRandom; m10LiquidRandomResetCount = 1;
        Require(ReferenceEquals(WorldGen.genRand, controlledRandom), "Actual native liquid RNG must be the initialized object.");

        // Exact loop shape recovered from retained GameplayScaffold EF02EF47...0704EAA,
        // PrepareM9LiquidFacility: outer X ascending, inner Y ascending, four glass walls.
        for (int x = M10LiquidLeft; x < M10LiquidLeft + M10LiquidWidth; x++)
            for (int y = M10LiquidTop; y < M10LiquidTop + M10LiquidHeight; y++)
            {
                var tile = Main.tile[x, y]; tile.ClearEverything();
                if (x == M10LiquidLeft || x == M10LiquidLeft + M10LiquidWidth - 1 ||
                    y == M10LiquidTop || y == M10LiquidTop + M10LiquidHeight - 1)
                { tile.active(true); tile.type = TileID.Glass; }
            }
        CaptureM10LiquidCheckpoint("before-seed");
        using var seedBytes = new MemoryStream(21600 * 8);
        using (var writer = new BinaryWriter(seedBytes, Encoding.UTF8, true))
            for (int x = M10LiquidLeft + 1; x < M10LiquidLeft + M10LiquidWidth - 1; x++)
                for (int y = M10LiquidTop + 1; y <= M10LiquidTop + 180; y++)
                    if (((x + y) & 1) == 0)
                    {
                        Main.tile[x, y].liquid = 255; Liquid.AddWater(x, y);
                        writer.Write(x); writer.Write(y);
                    }
        Require(seedBytes.Length == 21600 * 8, "Exact historical seed count required.");
        m10LiquidSeedOrder = WriteM10LiquidBytes("seed-order-i32le-xy.bin", seedBytes.ToArray());
        m10LiquidSeeded = true;
        CaptureM10LiquidCheckpoint("after-seed");
        using (var process = Process.GetCurrentProcess()) m10LiquidCpuStart = process.TotalProcessorTime;
        m10LiquidStart = Stopwatch.GetTimestamp();
        m10LiquidQueuePeak = Liquid.numLiquid; m10LiquidBufferPeak = LiquidBuffer.numLiquidBuffer;
        WriteM10LiquidControlState();
    }

    private object? M10LiquidGuardValue(string name) => m10LiquidGuard?.GetType().GetProperty(name)?.GetValue(m10LiquidGuard);

    private void FinishM10LiquidControl()
    {
        Require(m10LiquidSeeded, "A seeded liquid control is required.");
        if (!m10LiquidFinished)
        {
            CaptureM10LiquidCheckpoint("terminal");
            WriteM10LiquidBytes("scheduler-call-wall-ms.json", JsonSerializer.SerializeToUtf8Bytes(m10LiquidTimes, jsonOptions));
            WriteM10LiquidBytes("m11-step-trace.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                m11LiquidTrace!.Attempts, m11LiquidTrace.NativeBodyEntries, m11LiquidTrace.NativeBodyReturns,
                m11LiquidTrace.UnhashedAttempts, m11LiquidTrace.FirstBodyEntry, m11LiquidTrace.FirstBodyExit,
                m11LiquidTrace.FirstNativeException, m11LiquidTrace.NativeExceptions,
                m11LiquidTrace.Steps, m11LiquidTrace.RandomCalls, m11LiquidTrace.RandomSamples,
                maximumHashedSteps = M11LiquidStepTrace.MaximumSteps, maximumRandomSamples = M11LiquidStepTrace.MaximumRandomSamples,
                observationOnly = true, rngCallUnmodified = true, naturalTimeAfterSingleInitialization = true
            }, jsonOptions));
            // Detach this measurement only. Native simulation continues until owned server shutdown.
            m10LiquidTiming?.Dispose(); m10LiquidTiming = null;
            m11LiquidTrace?.Dispose(); m10LiquidFinished = true;
        }
        WriteM10LiquidControlState();
    }

    private void DisposeM10LiquidControl()
    { m10LiquidTiming?.Dispose(); m10LiquidTiming = null; m11LiquidTrace?.Dispose(); m11LiquidTrace = null; DisposeM11LiquidPreparationTrace(); }

    private static object M10LiquidGlobals() => new
    {
        Liquid.skipCount, Liquid.stuckCount, Liquid.stuckAmount, Liquid.cycles, Liquid.curMaxLiquid,
        Liquid.numLiquid, Liquid.stuck, Liquid.quickFall, Liquid.quickSettle, Liquid.wetCounter,
        Liquid.panicCounter, Liquid.panicMode, Liquid.panicY, LiquidBuffer.numLiquidBuffer,
        Main.netMode, Main.maxTilesX, Main.maxTilesY, Main.UnderworldLayer, Main.Setting_UseReducedMaxLiquids,
        WorldGen.isGeneratingOrLoadingWorld, activeFirstFifteen = Main.player.Take(15).Count(p => p is { active: true })
    };

    private object WriteM10LiquidBytes(string name, byte[] bytes)
    {
        Require(bytes.Length <= 4 * 1024 * 1024, "Bounded liquid checkpoint exceeded 4 MiB.");
        File.WriteAllBytes(Path.Combine(m10LiquidOutput!, name), bytes);
        return new { name, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    }

    private void CaptureM10LiquidCheckpoint(string label)
    {
        Require(m10LiquidCheckpoints.Count < 4, "At most four complete liquid checkpoints may be retained.");
        var files = new List<object>(8);
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                for (int x = M10LiquidLeft - M10LiquidHalo; x < M10LiquidLeft + M10LiquidWidth + M10LiquidHalo; x++)
                    for (int y = M10LiquidTop - M10LiquidHalo; y < M10LiquidTop + M10LiquidHeight + M10LiquidHalo; y++)
                    {
                        var tile = Main.tile[x, y];
                        writer.Write(tile.type); writer.Write(tile.wall); writer.Write(tile.liquid); writer.Write(tile.sTileHeader);
                        writer.Write(tile.bTileHeader); writer.Write(tile.bTileHeader2); writer.Write(tile.bTileHeader3);
                        writer.Write(tile.frameX); writer.Write(tile.frameY);
                    }
            files.Add(WriteM10LiquidBytes(label + "-tiles.bin", bytes.ToArray()));
        }
        Require(Liquid.numLiquid is >= 0 and <= 25000 && LiquidBuffer.numLiquidBuffer is >= 0 and <= 50000,
            "Native queue count outside audited storage bounds.");
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                for (int i = 0; i < Liquid.numLiquid; i++)
                { var entry = Main.liquid[i]; writer.Write(entry.x); writer.Write(entry.y); writer.Write(entry.kill); writer.Write(entry.delay); }
            files.Add(WriteM10LiquidBytes(label + "-queue-i32le-xy-kill-delay.bin", bytes.ToArray()));
        }
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                for (int i = 0; i < LiquidBuffer.numLiquidBuffer; i++)
                { var entry = Main.liquidBuffer[i]; writer.Write(entry.x); writer.Write(entry.y); }
            files.Add(WriteM10LiquidBytes(label + "-buffer-i32le-xy.bin", bytes.ToArray()));
        }
        files.Add(WriteM10LiquidBytes(label + "-globals.json", JsonSerializer.SerializeToUtf8Bytes(M10LiquidGlobals(), jsonOptions)));
        files.Add(WriteM10LiquidBytes(label + "-solidity.json", JsonSerializer.SerializeToUtf8Bytes(new
            { Main.tileSolid, Main.tileSolidTop, Main.tileWaterDeath, Main.tileLavaDeath }, jsonOptions)));
        int[] pendingNet, swapNet;
        lock (Liquid._netChangeSet) pendingNet = Liquid._netChangeSet.ToArray();
        lock (Liquid._swapNetChangeSet) swapNet = Liquid._swapNetChangeSet.ToArray();
        files.Add(WriteM10LiquidBytes(label + "-network-change-order.json", JsonSerializer.SerializeToUtf8Bytes(new { pendingNet, swapNet }, jsonOptions)));
        // Native liquid rounding calls WorldGen.genRand. Save state, including the original
        // before-layout state and the controlled before/after-seed state, without consuming it.
        object random = WorldGen.genRand;
        var rngFields = new SortedDictionary<string, object?>(StringComparer.Ordinal); bool rngComplete = true;
        for (Type? type = random.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(random);
                if (value is null || value is int or uint or long or ulong or bool || value is int[])
                    rngFields[type.FullName + "." + field.Name] = value is int[] integers ? integers.ToArray() : value;
                else { rngComplete = false; rngFields[type.FullName + "." + field.Name] = "unsupported:" + field.FieldType.FullName; }
            }
        files.Add(WriteM10LiquidBytes(label + "-worldgen-rng.json", JsonSerializer.SerializeToUtf8Bytes(new
            { type = random.GetType().AssemblyQualifiedName, complete = rngComplete, fields = rngFields }, jsonOptions)));
        var checkpoint = new { label, utc = DateTimeOffset.UtcNow, files, rngComplete,
            uncontrolledClocks = new { Main.time, Main.dayTime, Main.raining },
            tileStorage = "all nine ITile fields, little endian, 14 bytes per cell, X outer then Y inner",
            tileRectangle = new { left = M10LiquidLeft - M10LiquidHalo, top = M10LiquidTop - M10LiquidHalo,
                width = M10LiquidWidth + 2 * M10LiquidHalo, height = M10LiquidHeight + 2 * M10LiquidHalo },
            extent = "owned facility plus four-cell halo; complete active native queues; no whole-world memory replay" };
        m10LiquidCheckpoints.Add(checkpoint);
        File.WriteAllText(Path.Combine(m10LiquidOutput!, "checkpoints.json"), JsonSerializer.Serialize(m10LiquidCheckpoints, jsonOptions));
    }

    private void WriteM10LiquidControlState()
    {
        EnsureM11LiquidPreparationTrace();
        long total = 0, haloLiquid = 0; int checking = 0, occupied = 0;
        long[] liquidByType = new long[4]; bool boundaryIntact = true;
        var preparationQueueHead = new List<object>(64);
        if (!m10LiquidAttempted && Liquid.numLiquid >= 0 && Liquid.numLiquid <= Main.liquid.Length)
            for (int i = 0; i < Math.Min(64, Liquid.numLiquid); i++)
            {
                var entry = Main.liquid[i];
                bool inBounds = entry.x >= 0 && entry.x < Main.maxTilesX && entry.y >= 0 && entry.y < Main.maxTilesY;
                var tile = inBounds ? Main.tile[entry.x, entry.y] : null;
                preparationQueueHead.Add(new { index = i, entry.x, entry.y, entry.kill, entry.delay,
                    liquid = tile is null ? (int?)null : tile.liquid,
                    liquidType = tile is null ? (int?)null : tile.liquidType(),
                    checking = tile is null ? (bool?)null : tile.checkingLiquid(),
                    neighborhood = i < 4 && inBounds ? M11LiquidPreparationNeighborhood(entry.x, entry.y) : null });
            }
        if (m10LiquidSeeded)
            for (int x = M10LiquidLeft - M10LiquidHalo; x < M10LiquidLeft + M10LiquidWidth + M10LiquidHalo; x++)
                for (int y = M10LiquidTop - M10LiquidHalo; y < M10LiquidTop + M10LiquidHeight + M10LiquidHalo; y++)
                {
                    var tile = Main.tile[x, y];
                    if (x < M10LiquidLeft || x >= M10LiquidLeft + M10LiquidWidth || y < M10LiquidTop || y >= M10LiquidTop + M10LiquidHeight)
                    { haloLiquid += tile.liquid; continue; }
                    total += tile.liquid; liquidByType[tile.liquidType()] += tile.liquid;
                    if (tile.checkingLiquid()) checking++; if (tile.liquid > 0) occupied++;
                    if (x == M10LiquidLeft || x == M10LiquidLeft + M10LiquidWidth - 1 ||
                        y == M10LiquidTop || y == M10LiquidTop + M10LiquidHeight - 1)
                        boundaryIntact &= tile.active() && tile.type == TileID.Glass && !tile.inActive();
                }
        var times = m10LiquidTimes.Order().ToArray();
        double Quantile(double q) => times.Length == 0 ? 0 : times[Math.Min(times.Length - 1, (int)Math.Ceiling(times.Length * q) - 1)];
        using var process = Process.GetCurrentProcess();
        var payload = new { utc = DateTimeOffset.UtcNow, attempted = m10LiquidAttempted, seeded = m10LiquidSeeded,
            finished = m10LiquidFinished, guardEnabled = m10LiquidGuardEnabled, quiescent = M10LiquidQuiescent(),
            elapsedMs = m10LiquidSeeded ? Stopwatch.GetElapsedTime(m10LiquidStart).TotalMilliseconds : 0,
            layout = "historical-240-column-alternating-checkerboard", left = M10LiquidLeft, top = M10LiquidTop,
            width = M10LiquidWidth, height = M10LiquidHeight, total, expectedTotal = M10LiquidExpected,
            quantityDifference = m10LiquidSeeded ? total - M10LiquidExpected : 0, checking, occupied,
            water = liquidByType[0], lava = liquidByType[1], honey = liquidByType[2], shimmer = liquidByType[3],
            haloLiquid, boundaryIntact, worldClock = new { Main.time, Main.dayTime, Main.raining },
            Liquid.numLiquid, LiquidBuffer.numLiquidBuffer, Liquid.panicMode, Liquid.panicCounter,
            preparationQueueHead, preparationNativeTrace = m10LiquidSeeded ? null : M11LiquidPreparationTrace(),
            m10LiquidQueuePeak, m10LiquidBufferPeak, globals = M10LiquidGlobals(),
            healthy = m10LiquidGuardEnabled ? M10LiquidGuardValue("ContractHealthy") : null,
            allowed = m10LiquidGuardEnabled ? M10LiquidGuardValue("Allowed") : null,
            budgetPaused = m10LiquidGuardEnabled ? M10LiquidGuardValue("BudgetPaused") : null,
            observedWork = m10LiquidGuardEnabled ? M10LiquidGuardValue("ObservedSchedulerWorkUnits") : null,
            admittedWork = m10LiquidGuardEnabled ? M10LiquidGuardValue("AdmittedSchedulerWorkUnits") : null,
            unpaid = m10LiquidGuardEnabled ? M10LiquidGuardValue("UnpaidSchedulerWorkUnits") : null,
            schedulerCalls = times.Length, m10LiquidTimesDropped, schedulerWallTotalMs = times.Sum(),
            schedulerWallP50Ms = Quantile(.5), schedulerWallP95Ms = Quantile(.95), schedulerWallP99Ms = Quantile(.99),
            schedulerWallMaxMs = times.Length == 0 ? 0 : times[^1],
            processCpuMilliseconds = m10LiquidSeeded ? (process.TotalProcessorTime - m10LiquidCpuStart).TotalMilliseconds : 0,
            process.WorkingSet64, checkpoints = m10LiquidCheckpoints, fixtureArtificial = true,
            seedOrder = m10LiquidSeedOrder, randomSeed = M10LiquidRandomSeed,
            randomResetCount = m10LiquidRandomResetCount, measurementVersion = "M11.actual-native-entry/1",
            clockInitializations = m10LiquidRandomResetCount, initialWorldTime = 15000,
            nativeBodyEntries = m11LiquidTrace?.NativeBodyEntries, nativeBodyReturns = m11LiquidTrace?.NativeBodyReturns,
            hashedNativeSteps = m11LiquidTrace?.Steps.Count, actualLiquidRandomCalls = m11LiquidTrace?.RandomCalls,
            firstNativeException = m11LiquidTrace?.FirstNativeException, nativeExceptions = m11LiquidTrace?.NativeExceptions,
            directNativeUpdateCalls = 0, artificialTickAdvance = false, queueReset = false, refill = false,
            comparisonQualification = "measurements only; compare saved initial tile, queues, globals and RNG before causal attribution" };
        File.WriteAllText(Path.Combine(output!, "m10-liquid-control-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
}
