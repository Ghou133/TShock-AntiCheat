using System.Reflection;
using System.Security.Cryptography;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using MonoMod.RuntimeDetour;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.NetModules;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M8LiquidExecutionTests
{
    [Test]
    public void NativePropagationMovesLiquidAndBroadcastsAfterPumpTransfer()
    {
        RunScenario(() =>
        {
            PrepareChambers();
            // Native transfer followed by its actual later AddWater/UpdateLiquid path. The guard
            // does not rely on a player, pump-source history, account or injected complete flag.
            Main.tile[20, 20].liquid = 200;
            Wiring._numInPump = Wiring._numOutPump = 1;
            Wiring._inPumpX[0] = 20; Wiring._inPumpY[0] = 20;
            Wiring._outPumpX[0] = 24; Wiring._outPumpY[0] = 20;
            Wiring.XferWater();
            Assert.That(Main.tile[20, 20].liquid, Is.Zero);
            Assert.That(Main.tile[24, 20].liquid, Is.EqualTo(200));
            Assert.That(Liquid.numLiquid, Is.EqualTo(1), "XferWater's real SquareTileFrame/TileFrame must enqueue its own destination.");
            Assert.That(Main.tile[24, 20].checkingLiquid(), Is.True);
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System);
            guard.Install(); guard.BindExecutionThread();
            Liquid.UpdateLiquid();
            Assert.That(Main.tile[24, 20].liquid, Is.Zero);
            Assert.That(Main.tile[24, 21].liquid, Is.EqualTo(200));
            Assert.That(guard.Allowed, Is.EqualTo(1));
            Assert.That(guard.LastObservedSchedulerWorkUnits, Is.EqualTo(4));
            Assert.That(guard.LastPaidSchedulerWorkUnits, Is.EqualTo(1));
            Assert.That(guard.UnpaidSchedulerWorkUnits, Is.EqualTo(3));
            Assert.That(NetLiquidModule._changesByChunkCoords.Values.SelectMany(chunk => chunk.DirtiedPackedTileCoords),
                Does.Contain((24 << 16) | 21), "The actual native send-batch preparation includes the propagated destination.");
            Assert.That(Liquid._netChangeSet, Is.Empty);
            Assert.That(Liquid._swapNetChangeSet, Is.Empty);
        });
    }

    [Test]
    public void ExhaustionPreservesEntireQueueCursorLiquidFlagsAndOutgoingBatchThenResumes()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 200);
            var clock = new TestClock();
            using var guard = new M8LiquidExecutionGuard(clock,
                schedulerBudget: new(1, 9, 9, TimeSpan.FromMinutes(2)));
            guard.Install(); guard.BindExecutionThread(); Liquid.UpdateLiquid(); Liquid.UpdateLiquid();
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
            Liquid.NetSendLiquid(20, 21);
            var before = Snapshot();
            for (int i = 0; i < 4; i++) Liquid.UpdateLiquid();
            Assert.That(guard.BudgetPaused, Is.EqualTo(4));
            Assert.That(Snapshot(), Is.EqualTo(before), "A refused complete step has no native cursor, tile, queue or send-list write.");
            Assert.That(Liquid._netChangeSet, Does.Contain((20 << 16) | 21));
            clock.Advance(TimeSpan.FromSeconds(1)); Liquid.UpdateLiquid();
            Assert.That(guard.Allowed, Is.EqualTo(3));
            Assert.That(Main.tile[20, 21].liquid, Is.Zero);
            Assert.That(Liquid._netChangeSet, Is.Empty);
            Assert.That(Main.tile[20, 22].liquid, Is.EqualTo(200));
            Assert.That(guard.ContractHealthy, Is.True);
        });
    }

    [Test]
    public void MultipleQueuedSourcesRetainOrderAndBothFinishAcrossRepeatedBudgetPauses()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 180); Seed(24, 20, 220);
            var clock = new TestClock();
            using var guard = new M8LiquidExecutionGuard(clock,
                schedulerBudget: new(1, 16, 16, TimeSpan.FromMinutes(2)));
            guard.Install(); guard.BindExecutionThread();
            for (int attempt = 0; attempt < 40; attempt++)
            {
                var before = Snapshot(); long paused = guard.BudgetPaused;
                Liquid.UpdateLiquid();
                if (guard.BudgetPaused != paused)
                {
                    Assert.That(Snapshot(), Is.EqualTo(before));
                    clock.Advance(TimeSpan.FromSeconds(1));
                }
                if (Main.tile[20, 23].liquid == 180 && Main.tile[24, 23].liquid == 220) break;
            }
            Assert.That(Main.tile[20, 23].liquid, Is.EqualTo(180));
            Assert.That(Main.tile[24, 23].liquid, Is.EqualTo(220));
            Assert.That(guard.BudgetPaused, Is.GreaterThan(0));
            Assert.That(guard.Allowed, Is.GreaterThanOrEqualTo(5));
            Assert.That(TotalLiquid(), Is.EqualTo(400));
        });
    }

    [Test]
    public void MaintenancePauseAndResumeUsesSameNativeStepWithoutRefundOrReplayQueue()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 200);
            bool writes = false;
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System, () => writes);
            guard.Install(); guard.BindExecutionThread(); var before = Snapshot();
            Liquid.UpdateLiquid(); Liquid.UpdateLiquid();
            Assert.That(guard.MaintenancePaused, Is.EqualTo(2));
            Assert.That(Snapshot(), Is.EqualTo(before));
            writes = true; Liquid.UpdateLiquid();
            Assert.That(guard.Allowed, Is.EqualTo(1));
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
            Assert.That(TotalLiquid(), Is.EqualTo(200));
        });
    }

    [Test]
    public void NoRuntimeThreadEvidencePassesNativeAndLaterRealBindingEnablesGuard()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 200);
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System, () => false);
            guard.Install(); Liquid.UpdateLiquid();
            Assert.That(guard.ContractHealthy, Is.False);
            Assert.That(guard.ContextUnavailablePassed, Is.EqualTo(1));
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
            guard.BindExecutionThread(); var before = Snapshot(); Liquid.UpdateLiquid();
            Assert.That(guard.ContractHealthy, Is.True);
            Assert.That(guard.MaintenancePaused, Is.EqualTo(1));
            Assert.That(Snapshot(), Is.EqualTo(before));
        });
    }

    [Test]
    public void EarlierNativeDetourCancellationIsNotReplayedOrOverridden()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 200); int innerCalls = 0;
            using var independent = new Hook(UpdateMethod, (Action<Action>)(_ => innerCalls++));
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System);
            guard.Install(); guard.BindExecutionThread(); var before = Snapshot(); Liquid.UpdateLiquid();
            Assert.That(innerCalls, Is.EqualTo(1));
            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(guard.Allowed, Is.EqualTo(1), "Admission is not a claim that a later independent hook executed native code.");
        });
    }

    [Test]
    public void ImpossibleBurstWithdrawsLocalGuardInsteadOfPermanentlyStarvingLegalQueue()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 200); int faults = 0;
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System,
                schedulerBudget: new(1, 1, 1, TimeSpan.FromMinutes(2)));
            guard.IntegrityFault = _ => { faults++; throw new IOException("fixture reporter"); };
            guard.Install(); guard.BindExecutionThread(); Liquid.UpdateLiquid(); Liquid.UpdateLiquid();
            Assert.That(guard.ContractHealthy, Is.False); Assert.That(faults, Is.EqualTo(1));
            Assert.That(guard.FaultObserverFailed, Is.True);
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
            guard.Install(); guard.BindExecutionThread(); Liquid.UpdateLiquid();
            Assert.That(Main.tile[20, 22].liquid, Is.EqualTo(200));
            Assert.That(faults, Is.EqualTo(1)); Assert.That(guard.BudgetPaused, Is.Zero);
        });
    }

    [Test]
    public void NativePanicRecoveryIsExplicitlyOutsideBudgetAndNotStarved()
    {
        RunScenario(() =>
        {
            int steps = 0;
            // A bounded existing panic row with no liquid still executes the real QuickWater
            // method. Avoid its final full-world WaterCheck, a separate unclaimed scope.
            var saved = new List<(int X, int Y, ITile Tile)>();
            for (int x = 4; x < Main.maxTilesX - 4; x++) for (int y = 30; y <= 34; y++)
            { saved.Add((x, y, Main.tile[x, y])); Main.tile[x, y] = new Tile(); }
            var method = typeof(Liquid).GetMethod(nameof(Liquid.QuickWater), [typeof(int), typeof(int), typeof(int)])!;
            using var observe = new Hook(method, (Action<Action<int, int, int>, int, int, int>)((original, verbose, min, max) =>
            { steps++; original(verbose, min, max); }));
            try
            {
                Liquid.panicMode = true; Liquid.panicY = 34;
                using var guard = new M8LiquidExecutionGuard(TimeProvider.System,
                    schedulerBudget: new(1, 1, 1, TimeSpan.FromMinutes(2)));
                guard.Install(); guard.BindExecutionThread(); Liquid.UpdateLiquid();
                Assert.That(steps, Is.EqualTo(5)); Assert.That(Liquid.panicY, Is.EqualTo(29));
                Assert.That(guard.RecoveryPassed, Is.EqualTo(1)); Assert.That(guard.Allowed, Is.Zero);
                Assert.That(guard.BudgetPaused, Is.Zero); Assert.That(guard.ContractHealthy, Is.True);
            }
            finally { foreach (var (x, y, tile) in saved) Main.tile[x, y] = tile; }
        });
    }

    [Test]
    public void NativeExceptionPropagatesOnceAndDisposeRestoresNativePropagation()
    {
        RunScenario(() =>
        {
            PrepareChambers(); Seed(20, 20, 200); int attempts = 0;
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System);
            guard.Install(); guard.BindExecutionThread();
            using (var failingNative = new Hook(UpdateMethod, (Action<Action>)(_ =>
            { attempts++; throw new IOException("fixture native failure"); })))
            {
                Assert.Throws<IOException>(Liquid.UpdateLiquid); Assert.That(attempts, Is.EqualTo(1));
                Assert.That(guard.ContractHealthy, Is.True, "A native exception is not an observer-integrity diagnosis.");
            }
            guard.Dispose(); Liquid.UpdateLiquid();
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
        });
    }

    [Test]
    public void DefaultQuotaLeavesContinuousSmallFlowIdenticalToUnprotectedNativeSteps()
    {
        string[] Run(bool protectedRun)
        {
            var states = new List<string>();
            RunScenario(() =>
            {
                PrepareChambers(); Seed(20, 20, 180); Seed(24, 20, 220);
                var clock = new TestClock();
                using var guard = new M8LiquidExecutionGuard(clock);
                if (protectedRun) { guard.Install(); guard.BindExecutionThread(); }
                for (int step = 0; step < 60; step++)
                {
                    Liquid.UpdateLiquid(); states.Add(Snapshot());
                    clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30));
                }
                Assert.That(guard.BudgetPaused, Is.Zero);
                Assert.That(TotalLiquid(), Is.EqualTo(400));
                if (protectedRun) Assert.That(guard.Allowed, Is.EqualTo(60));
            });
            return states.ToArray();
        }
        Assert.That(Run(true), Is.EqualTo(Run(false)));
    }

    [Test]
    public void DefaultQuotaActuallyThrottlesNativeFullQueueAndReachesIdenticalSettledState()
    {
        var control = LoadedNativeRun(false);
        var protectedRun = LoadedNativeRun(true);
        Assert.That(protectedRun.Pauses, Is.GreaterThan(0), "This must reach the actual default budget branch at ordinary 30Hz, not host over-calling.");
        Assert.That(protectedRun.Admissions, Is.EqualTo(101));
        Assert.That(protectedRun.Hash, Is.EqualTo(control.Hash));
        Assert.That(protectedRun.Amount, Is.EqualTo(24999 * 200));
        Assert.That(protectedRun.Remaining, Is.Zero);
        Assert.That(protectedRun.Observed - protectedRun.Paid, Is.EqualTo(protectedRun.Unpaid));
        Assert.That(protectedRun.Unpaid, Is.Zero, "A later natural empty step settles the final actual work without replaying a liquid operation.");
        TestContext.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            nativeSourceCells = 24999, initialLiquid = 24999 * 200, ordinaryAttemptsPerSecond = 30,
            protectedRun.Admissions, protectedRun.Pauses, protectedRun.Attempts, protectedRun.Observed,
            protectedRun.Paid, protectedRun.Unpaid, protectedRun.Amount, protectedRun.Remaining,
            sameNativeFinalState = protectedRun.Hash == control.Hash,
            costMeaning = "scheduler queue visits, including skipped updates and cleanup inspection; not CPU milliseconds"
        }));
    }

    private sealed record LoadedResult(string Hash, int Amount, int Remaining, int Attempts,
        long Admissions, long Pauses, long Observed, long Paid, int Unpaid);

    private static LoadedResult LoadedNativeRun(bool guarded)
    {
        LoadedResult? result = null;
        RunScenario(() =>
        {
            // One-time owned world fixture only. Every queue entry is created by real AddWater;
            // no duplicate coordinates, fake numLiquid, or per-tick replenishment is used.
            var cells = new List<(int X, int Y)>(24999);
            var saved = new List<(int X, int Y, ITile Tile)>(24999 * 5);
            int height = Main.maxTilesY; Main.maxTilesY = 1000; // Keep all water above native underworld evaporation.
            try
            {
                for (int ordinal = 0; ordinal < 24999; ordinal++)
                {
                    int x = 10 + ordinal % 159 * 3, y = 10 + ordinal / 159 * 3;
                    cells.Add((x, y));
                    foreach (var (dx, dy) in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1) })
                    {
                        saved.Add((x + dx, y + dy, Main.tile[x + dx, y + dy]));
                        var tile = new Tile { type = 1 }; tile.active(true); Main.tile[x + dx, y + dy] = tile;
                    }
                    Main.tile[x, y] = new Tile { liquid = 200 };
                    Liquid.AddWater(x, y);
                }
                Assert.That(Liquid.numLiquid, Is.EqualTo(24999)); Assert.That(LiquidBuffer.numLiquidBuffer, Is.Zero);
                var clock = new TestClock();
                using var guard = new M8LiquidExecutionGuard(clock);
                if (guarded) { guard.Install(); guard.BindExecutionThread(); }
                int attempts = 0, admissions = 0;
                while (admissions < 101 && attempts < 500)
                {
                    long before = guard.Allowed;
                    Liquid.UpdateLiquid(); attempts++;
                    if (!guarded || guard.Allowed > before) admissions++;
                    clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30));
                }
                Assert.That(admissions, Is.EqualTo(101));
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                writer.Write(Snapshot());
                foreach (var (x, y) in cells)
                {
                    var tile = Main.tile[x, y]; writer.Write(tile.liquid); writer.Write(tile.checkingLiquid()); writer.Write(tile.skipLiquid());
                }
                writer.Flush();
                result = new(Convert.ToHexString(SHA256.HashData(stream.ToArray())), cells.Sum(cell => (int)Main.tile[cell.X, cell.Y].liquid),
                    Liquid.numLiquid, attempts, admissions, guard.BudgetPaused, guard.ObservedSchedulerWorkUnits,
                    guard.AdmittedSchedulerWorkUnits, guard.UnpaidSchedulerWorkUnits);
            }
            finally
            {
                foreach (var (x, y, tile) in saved) Main.tile[x, y] = tile;
                Main.maxTilesY = height;
            }
        });
        return result!;
    }

    private static MethodInfo UpdateMethod => typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid), [])!;
    private static void PrepareChambers()
    {
        for (int x = 17; x <= 27; x++) for (int y = 19; y <= 24; y++)
        { Main.tile[x, y] = new Tile { type = 1 }; Main.tile[x, y].active(true); }
        foreach (int x in new[] { 20, 24 }) for (int y = 20; y <= 23; y++) Main.tile[x, y] = new Tile();
    }

    private static void Seed(int x, int y, byte amount)
    { Main.tile[x, y].liquid = amount; Liquid.AddWater(x, y); }

    private static int TotalLiquid()
    {
        int total = 0;
        for (int x = 17; x <= 27; x++) for (int y = 19; y <= 24; y++) total += Main.tile[x, y].liquid;
        return total;
    }

    private static string Snapshot()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var field in typeof(Liquid).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(int) || field.FieldType == typeof(bool)).OrderBy(field => field.Name))
            writer.Write(field.GetValue(null)!.ToString()!);
        writer.Write(LiquidBuffer.numLiquidBuffer);
        foreach (var cell in Main.liquid.Take(Liquid.numLiquid))
        { writer.Write(cell.x); writer.Write(cell.y); writer.Write(cell.kill); writer.Write(cell.delay); }
        foreach (var cell in Main.liquidBuffer.Take(LiquidBuffer.numLiquidBuffer))
        { writer.Write(cell.x); writer.Write(cell.y); }
        foreach (var value in Liquid._netChangeSet.Order()) writer.Write(value);
        foreach (var value in Liquid._swapNetChangeSet.Order()) writer.Write(value);
        for (int x = 17; x <= 27; x++) for (int y = 19; y <= 24; y++)
        {
            var tile = Main.tile[x, y]; writer.Write(tile.liquid); writer.Write(tile.liquidType());
            writer.Write(tile.checkingLiquid()); writer.Write(tile.skipLiquid()); writer.Write(tile.active()); writer.Write(tile.type);
        }
        foreach (var value in Main.tileSolid) writer.Write(value);
        writer.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void RunScenario(Action action) => M6WiringExecutionTests.RunScenario((_, _, _) =>
    {
        var savedFields = typeof(Liquid).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => !field.IsInitOnly && !field.IsLiteral).Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
        var oldLiquid = Main.liquid; var oldBuffer = Main.liquidBuffer; int oldBuffered = LiquidBuffer.numLiquidBuffer;
        var oldSolids = (bool[])Main.tileSolid.Clone(); var oldReduced = Main.Setting_UseReducedMaxLiquids;
        var oldGenerating = WorldGen.isGeneratingOrLoadingWorld;
        var oldClients = Netplay.Clients.ToArray();
        var oldChunks = NetLiquidModule._changesByChunkCoords;
        try
        {
            Main.Setting_UseReducedMaxLiquids = false; WorldGen.isGeneratingOrLoadingWorld = false;
            Main.liquid = new Liquid[Liquid.maxLiquid]; Main.liquidBuffer = new LiquidBuffer[Liquid.maxLiquidBuffer];
            Liquid.ReInit(); LiquidBuffer.numLiquidBuffer = 0;
            Liquid._netChangeSet = []; Liquid._swapNetChangeSet = [];
            NetLiquidModule._changesByChunkCoords = [];
            for (int i = 0; i < Netplay.Clients.Length; i++) Netplay.Clients[i] = new RemoteClient { Id = i };
            Main.tileSolid[1] = true;
            action();
        }
        finally
        {
            foreach (var (field, value) in savedFields) field.SetValue(null, value);
            Main.liquid = oldLiquid; Main.liquidBuffer = oldBuffer; LiquidBuffer.numLiquidBuffer = oldBuffered;
            oldSolids.CopyTo(Main.tileSolid, 0); Main.Setting_UseReducedMaxLiquids = oldReduced;
            WorldGen.isGeneratingOrLoadingWorld = oldGenerating; oldClients.CopyTo(Netplay.Clients, 0);
            NetLiquidModule._changesByChunkCoords = oldChunks;
        }
    });

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
}
