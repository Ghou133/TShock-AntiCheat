using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Plugin.TShock;
using CompatibilityAudit;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.NetModules;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M11LiquidInvestigationTests
{
    // Same 240-column/180-row checkerboard and four Glass walls as the retained server
    // facility; translated into owned test storage, above native underworld evaporation.
    private const int Left = 100, Top = 58, Width = 242, Height = 202;

    [Test]
    public void NativePerPlayerLiquidSerializationAcceptsNormalBatchButCannotEncodeTheWholeCheckerboard()
    {
        Fixture(() =>
        {
            var previousCache = NetLiquidModule._changesForPlayerCache;
            NetLiquidModule._changesForPlayerCache = [];
            try
            {
                var sections = Netplay.Clients[0].TileSections;
                for (int x = 0; x < sections.GetLength(0); x++) for (int y = 0; y < sections.GetLength(1); y++) sections[x, y] = true;
                NetLiquidModule.PrepareChunks(Liquid._netChangeSet.Take(100).ToHashSet());
                var ordinary = NetLiquidModule.SerializeForPlayer(0);
                try { ordinary.ShrinkToFit(); Assert.That(ordinary.Length, Is.EqualTo(5 + 2 + 100 * 6)); }
                finally { ordinary.Recycle(); }
                NetLiquidModule.PrepareChunks(Liquid._netChangeSet);
                var error = Assert.Throws<NotSupportedException>(() => NetLiquidModule.SerializeForPlayer(0));
                Assert.That(NetLiquidModule._changesForPlayerCache, Has.Count.EqualTo(21600));
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m11-liquid-native-serialization.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    layer = "controlled-native-method", normalCount = 100, normalPacketBytes = 607,
                    checkerboardCount = 21600, requiredBytes = 5 + 2 + 21600 * 6, maximumPacketBytes = 65535,
                    actualBufferBytes = Terraria.DataStructures.BufferPool.HUGE_BUFFER_SIZE,
                    exception = error!.ToString(), sourceMethod = "NetLiquidModule.SerializeForPlayer",
                    copiedOrReplacedNativeMethod = false, accountVerdict = false,
                    runtimeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Liquid).Assembly.Location)))
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddTestAttachment(path, "Native per-player liquid serialization counterexample");
                TestContext.Out.WriteLine(path);
            }
            finally { NetLiquidModule._changesForPlayerCache = previousCache; }
        });
    }

    [Test]
    public void DeferredInitializationLeavesPendingNativeCallsIntactAndCountsOnlyTheActualInitializedEntry()
    {
        Fixture(() =>
        {
            bool ready = false; int initializations = 0;
            using var trace = new M11LiquidStepTrace(Left, Top, Width, Height, () => 0, () => 0,
                () => initializations++, () => ready);
            trace.Install(); Liquid.UpdateLiquid();
            Assert.That(Liquid.wetCounter, Is.EqualTo(1), "Pending fixture setup must preserve the ordinary native full step.");
            Assert.That(initializations, Is.Zero); Assert.That(trace.Attempts, Is.Zero);
            Assert.That(trace.NativeBodyEntries, Is.Zero); Assert.That(trace.FirstBodyEntry, Is.Null);
            ready = true; Liquid.UpdateLiquid(); Liquid.UpdateLiquid();
            Assert.That(initializations, Is.EqualTo(1)); Assert.That(trace.Attempts, Is.EqualTo(2));
            Assert.That(trace.NativeBodyEntries, Is.EqualTo(2)); Assert.That(trace.NativeBodyReturns, Is.EqualTo(2));
            Assert.That(Liquid.wetCounter, Is.EqualTo(3));
            Save("deferred-actual-entry", trace);
        });
    }

    [Test]
    public void SameCheckerboardInitialStateMaintenanceWritesZeroThenAdmitsOneCompleteNativeBody()
    {
        M11LiquidStepTrace.State? controlBefore = null, controlAfter = null;
        Fixture(() =>
        {
            using var trace = new M11LiquidStepTrace(Left, Top, Width, Height, () => 0, () => 0);
            trace.Install(); controlBefore = trace.Snapshot(); Liquid.UpdateLiquid(); controlAfter = trace.Snapshot();
            Assert.That(trace.Steps.Single().BodyEntries, Is.EqualTo(1));
            Assert.That(trace.Steps.Single().BodyReturns, Is.EqualTo(1));
            Save("control-single-step", trace);
        });
        Fixture(() =>
        {
            bool writes = false;
            using var guard = new M8LiquidExecutionGuard(TimeProvider.System, () => writes);
            guard.Install(); guard.BindExecutionThread();
            using var trace = new M11LiquidStepTrace(Left, Top, Width, Height, () => guard.Allowed, () => guard.MaintenancePaused);
            trace.Install(); Assert.That(trace.Snapshot(), Is.EqualTo(controlBefore));
            Liquid.UpdateLiquid();
            var pause = trace.Steps.Single();
            Assert.That(pause.Paused, Is.EqualTo(1)); Assert.That(pause.BodyEntries, Is.Zero);
            Assert.That(pause.BodyReturns, Is.Zero); Assert.That(pause.Before, Is.EqualTo(pause.After));
            writes = true; Liquid.UpdateLiquid();
            var resumed = trace.Steps.Last();
            Assert.That(resumed.Allowed, Is.EqualTo(1)); Assert.That(resumed.BodyEntries, Is.EqualTo(1));
            Assert.That(resumed.BodyReturns, Is.EqualTo(1)); Assert.That(resumed.After, Is.EqualTo(controlAfter));
            Assert.That(guard.ContractHealthy, Is.True); Save("maintenance-and-single-step", trace);
        });
    }

    [Test]
    public void SameCheckerboardDefaultBudgetPausesHaveNoNativeWritesAndEveryAdmissionRunsOnce()
    {
        Fixture(() =>
        {
            var clock = new Clock(); using var guard = new M8LiquidExecutionGuard(clock);
            guard.Install(); guard.BindExecutionThread();
            using var trace = new M11LiquidStepTrace(Left, Top, Width, Height, () => guard.Allowed, () => guard.BudgetPaused);
            trace.Install();
            for (int attempt = 0; attempt < M11LiquidStepTrace.MaximumSteps; attempt++)
            { Liquid.UpdateLiquid(); clock.Advance(); }
            Assert.That(trace.Steps.Any(step => step.Paused == 1), Is.True, "Actual default quota must pause this same checkerboard.");
            foreach (var step in trace.Steps)
            {
                Assert.That(step.Completed, Is.True);
                if (step.Paused == 1)
                { Assert.That(step.Before, Is.EqualTo(step.After)); Assert.That(step.BodyEntries, Is.Zero); Assert.That(step.BodyReturns, Is.Zero); }
                else
                { Assert.That(step.Allowed, Is.EqualTo(1)); Assert.That(step.BodyEntries, Is.EqualTo(1)); Assert.That(step.BodyReturns, Is.EqualTo(1)); }
            }
            Assert.That(guard.ContractHealthy, Is.True); Save("default-budget-checkerboard", trace);
        });
    }

    private static void Save(string name, M11LiquidStepTrace trace)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m11-liquid-" + name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            layer = "controlled-native-method", fixture = "translated-historical-checkerboard-240x180",
            runtimeFile = typeof(Liquid).Assembly.Location,
            runtimeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Liquid).Assembly.Location))),
            trace.Attempts, trace.NativeBodyEntries, trace.NativeBodyReturns, trace.Steps,
            trace.FirstBodyEntry, trace.FirstBodyExit, trace.RandomCalls, trace.RandomSamples,
            trace.FirstNativeException, trace.NativeExceptions,
            naturalScheduler = false, productConservationQualified = false, stockGui = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(path, "M11 raw liquid method trace");
        TestContext.Out.WriteLine(path);
    }

    private static void Fixture(Action action) => M6WiringExecutionTests.RunScenario((_, _, _) =>
    {
        var savedFields = typeof(Liquid).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => !field.IsInitOnly && !field.IsLiteral).Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
        var oldLiquid = Main.liquid; var oldBuffer = Main.liquidBuffer; int oldBuffered = LiquidBuffer.numLiquidBuffer;
        var oldSolids = (bool[])Main.tileSolid.Clone(); var oldReduced = Main.Setting_UseReducedMaxLiquids;
        var oldGenerating = WorldGen.isGeneratingOrLoadingWorld; var oldClients = Netplay.Clients.ToArray();
        var oldChunks = NetLiquidModule._changesByChunkCoords; int oldHeight = Main.maxTilesY;
        double oldTime = Main.time; bool oldDay = Main.dayTime, oldRaining = Main.raining;
        var tiles = new List<(int X, int Y, ITile Tile)>((Width + 8) * (Height + 8));
        try
        {
            Main.maxTilesY = 1200; Main.Setting_UseReducedMaxLiquids = false; WorldGen.isGeneratingOrLoadingWorld = false;
            Main.time = 15000; Main.dayTime = true; Main.raining = false;
            Main.rand = new Terraria.Utilities.UnifiedRandom(0x4D31304C);
            Main.player[0].active = Main.player[1].active = true;
            Main.liquid = new Liquid[Liquid.maxLiquid]; Main.liquidBuffer = new LiquidBuffer[Liquid.maxLiquidBuffer];
            Liquid.ReInit(); LiquidBuffer.numLiquidBuffer = 0;
            Liquid.skipCount = 2; Liquid.wetCounter = 0; Liquid.cycles = 10; Liquid.curMaxLiquid = 24500;
            Liquid.quickSettle = Liquid.quickFall = Liquid.panicMode = Liquid.stuck = false;
            Liquid.panicCounter = Liquid.panicY = Liquid.stuckCount = Liquid.stuckAmount = 0;
            Liquid._netChangeSet = []; Liquid._swapNetChangeSet = []; NetLiquidModule._changesByChunkCoords = [];
            for (int i = 0; i < Netplay.Clients.Length; i++) Netplay.Clients[i] = new RemoteClient { Id = i };
            Main.tileSolid[TileID.Glass] = true;
            for (int x = Left - 4; x < Left + Width + 4; x++) for (int y = Top - 4; y < Top + Height + 4; y++)
            {
                tiles.Add((x, y, Main.tile[x, y])); var tile = new Tile(); Main.tile[x, y] = tile;
                if (x >= Left && x < Left + Width && y >= Top && y < Top + Height &&
                    (x == Left || x == Left + Width - 1 || y == Top || y == Top + Height - 1))
                { tile.active(true); tile.type = TileID.Glass; }
            }
            for (int x = Left + 1; x < Left + Width - 1; x++) for (int y = Top + 1; y <= Top + 180; y++)
                if (((x + y) & 1) == 0) { Main.tile[x, y].liquid = 255; Liquid.AddWater(x, y); }
            Assert.That(Liquid.numLiquid, Is.EqualTo(21600)); action();
        }
        finally
        {
            foreach (var (x, y, tile) in tiles) Main.tile[x, y] = tile;
            foreach (var (field, value) in savedFields) field.SetValue(null, value);
            Main.liquid = oldLiquid; Main.liquidBuffer = oldBuffer; LiquidBuffer.numLiquidBuffer = oldBuffered;
            oldSolids.CopyTo(Main.tileSolid, 0); Main.Setting_UseReducedMaxLiquids = oldReduced;
            WorldGen.isGeneratingOrLoadingWorld = oldGenerating; oldClients.CopyTo(Netplay.Clients, 0);
            NetLiquidModule._changesByChunkCoords = oldChunks; Main.maxTilesY = oldHeight;
            Main.time = oldTime; Main.dayTime = oldDay; Main.raining = oldRaining;
        }
    });

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance() => ticks += TimeSpan.TicksPerSecond / 30;
    }
}
