using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7WiringPumpExecutionTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void NativeSwitchCollectsPumpsAndTransfersEachNativeLiquid(int liquidType)
    {
        M6WiringExecutionTests.RunScenario((actor, session, region) =>
        {
            region.AllowedIDs.Add(actor.Account.ID);
            PrepareCircuit(liquidType);
            int frames = 0;
            void Frame(object? _, HookEvents.Terraria.WorldGen.SquareTileFrameEventArgs e) => frames++;
            HookEvents.Terraria.WorldGen.SquareTileFrame += Frame;
            try
            {
                using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (session, actor)); guard.Install();
                Wiring.SetCurrentUser(actor.Index); Wiring.HitSwitch(18, 20); Wiring.SetCurrentUser();
                Assert.That(guard.PumpAllowed, Is.EqualTo(1));
                Assert.That(Wiring._numInPump, Is.EqualTo(4)); Assert.That(Wiring._numOutPump, Is.EqualTo(4));
                Assert.That(Main.tile[20, 21].liquid, Is.Zero);
                Assert.That(Main.tile[24, 21].liquid, Is.EqualTo(200));
                Assert.That(Main.tile[24, 21].liquidType(), Is.EqualTo(liquidType));
                Assert.That(frames, Is.GreaterThan(0));
                Assert.That(Wiring.running, Is.False); Assert.That(Wiring.CurrentUser, Is.EqualTo(255));
                Assert.That(guard.PumpAdmittedWorkUnits, Is.EqualTo(304));
            }
            finally { HookEvents.Terraria.WorldGen.SquareTileFrame -= Frame; }
        });
    }

    [Test]
    public void RemoteOutletAndItsFrameNeighborhoodAreAuthorizedBeforeAnyTransfer()
    {
        M6WiringExecutionTests.RunScenario((actor, session, region) =>
        {
            PrepareCircuit(0);
            // The switch and pump tiles are allowed; a frame neighbor is protected.
            region.Area = new Rectangle(26, 22, 1, 1);
            int frames = 0;
            void Frame(object? _, HookEvents.Terraria.WorldGen.SquareTileFrameEventArgs e) => frames++;
            HookEvents.Terraria.WorldGen.SquareTileFrame += Frame;
            try
            {
                using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (session, actor)); guard.Install();
                Wiring.SetCurrentUser(actor.Index); Wiring.HitSwitch(18, 20);
                Assert.That(guard.PumpPermissionBlocked, Is.EqualTo(1));
                Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
                Assert.That(Main.tile[24, 21].liquid, Is.Zero); Assert.That(frames, Is.Zero);
                Assert.That(Wiring.running, Is.False, "The native circuit finishes normally after skipped transfer.");
                region.AllowedIDs.Add(actor.Account.ID);
                Wiring.HitSwitch(18, 20);
                Assert.That(Main.tile[24, 21].liquid, Is.EqualTo(200));
            }
            finally { HookEvents.Terraria.WorldGen.SquareTileFrame -= Frame; }
        });
    }

    [Test]
    public void TimerBudgetSkipsWholeTransferThenRefillsWithoutAttributingNearbyPlayer()
    {
        M6WiringExecutionTests.RunScenario((actor, session, region) =>
        {
            PrepareCircuit(0); var clock = new TestClock();
            using var guard = new M6WiringExecutionGuard(clock, _ => throw new AssertionException("No timer actor."),
                pumpGlobalBudget: new(1, 304, 304, TimeSpan.FromMinutes(1))); guard.Install();
            Wiring.SetCurrentUser(); Wiring.HitSwitch(18, 20);
            Assert.That(Main.tile[24, 21].liquid, Is.EqualTo(200));
            Main.tile[20, 21].liquid = 100;
            Wiring.HitSwitch(18, 20);
            Assert.That(guard.PumpBudgetBlocked, Is.EqualTo(1));
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(100)); Assert.That(Main.tile[24, 21].liquid, Is.EqualTo(200));
            Assert.That(Wiring.running, Is.False); Assert.That(guard.PumpUnknownActor, Is.Zero);
            clock.Advance(TimeSpan.FromSeconds(1)); Wiring.HitSwitch(18, 20);
            Assert.That(guard.PumpAllowed, Is.EqualTo(2));
            Assert.That(Main.tile[20, 21].liquid, Is.Zero);
            Assert.That(Main.tile[24, 21].liquid + Main.tile[25, 21].liquid, Is.EqualTo(300));
        });
    }

    [Test]
    public void RevokedSourceIsBlockedButReusedSlotDoesNotInheritOldAttribution()
    {
        M6WiringExecutionTests.RunScenario((actor, session, region) =>
        {
            PrepareCircuit(0); var current = session with { Revoked = true };
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (current, actor)); guard.Install();
            Wiring.SetCurrentUser(actor.Index); Wiring.HitSwitch(18, 20);
            Assert.That(guard.PumpPermissionBlocked, Is.EqualTo(1)); Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
            current = session with { Key = session.Key with { Generation = session.Key.Generation + 1 } };
            Wiring.HitSwitch(18, 20);
            Assert.That(guard.PumpUnknownActor, Is.EqualTo(1)); Assert.That(guard.PumpPermissionBlocked, Is.EqualTo(1));
            Assert.That(Main.tile[24, 21].liquid, Is.EqualTo(200));
        });
    }

    [Test]
    public void EarlierCancellationAndMalformedEndpointHaveNoLiquidOrFrameWrites()
    {
        M6WiringExecutionTests.RunScenario((actor, session, region) =>
        {
            PrepareCircuit(0); region.AllowedIDs.Add(actor.Account.ID);
            void Cancel(object? _, HookEvents.Terraria.Wiring.XferWaterEventArgs e) => e.ContinueExecution = false;
            HookEvents.Terraria.Wiring.XferWater += Cancel;
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (session, actor)); guard.Install();
            try
            {
                Wiring.SetCurrentUser(actor.Index); Wiring.HitSwitch(18, 20);
                Assert.That(guard.PumpAllowed, Is.Zero); Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200));
            }
            finally { HookEvents.Terraria.Wiring.XferWater -= Cancel; }
            Wiring._outPumpX[3] = Main.maxTilesX;
            Assert.DoesNotThrow(Wiring.XferWater);
            Assert.That(guard.PumpMalformedBlocked, Is.EqualTo(1));
            Assert.That(Main.tile[20, 21].liquid, Is.EqualTo(200)); Assert.That(Main.tile[24, 21].liquid, Is.Zero);
        });
    }

    private static void PrepareCircuit(int liquidType)
    {
        foreach (var (origin, type) in new[] { (20, 142), (24, 143) })
            for (int x = origin; x < origin + 2; x++) for (int y = 20; y < 22; y++)
            {
                var tile = new Tile { type = (ushort)type, frameX = (short)((x - origin) * 18), frameY = (short)((y - 20) * 18) };
                tile.active(true); Main.tile[x, y] = tile;
            }
        Main.tile[20, 21].liquid = 200; Main.tile[20, 21].liquidType(liquidType);
        Main.tile[18, 20].active(true); Main.tile[18, 20].type = 135;
        for (int x = 18; x <= 24; x++) Main.tile[x, 20].wire(true);
    }

    [Test]
    public void PumpObserverFaultWithdrawsOnlyPumpsAndLeavesStatueProtectionActive()
    {
        M6WiringExecutionTests.RunScenario((actor, session, region) =>
        {
            int calls = 0, faults = 0;
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => ++calls == 2
                ? throw new IOException("pump-only resolver fixture") : (session, actor));
            guard.PumpIntegrityFault = _ => { faults++; throw new IOException("pump reporter fixture"); };
            guard.Install(); Wiring.SetCurrentUser(actor.Index);
            Wiring._numInPump = Wiring._numOutPump = 1;
            Wiring._inPumpX[0] = 17; Wiring._inPumpY[0] = 20;
            Wiring._outPumpX[0] = 18; Wiring._outPumpY[0] = 20;
            Assert.DoesNotThrow(Wiring.XferWater);
            Assert.That(guard.PumpContractHealthy, Is.False); Assert.That(guard.ContractHealthy, Is.True);
            Assert.That(guard.PumpFaultObserverFailed, Is.True); Assert.That(faults, Is.EqualTo(1));
            Wiring.HitWireSingle(20, 20);
            Assert.That(guard.PermissionBlocked, Is.EqualTo(1)); Assert.That(Main.npc.Any(n => n.active), Is.False);
            region.AllowedIDs.Add(actor.Account.ID); Wiring.HitWireSingle(20, 20);
            Assert.That(guard.Allowed, Is.EqualTo(1)); Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(1));
            guard.Install(); Wiring.XferWater();
            Assert.That(faults, Is.EqualTo(1)); Assert.That(guard.PumpContractHealthy, Is.False);
        });
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
}
