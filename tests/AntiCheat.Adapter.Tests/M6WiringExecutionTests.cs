using System.Runtime.CompilerServices;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M6WiringExecutionTests
{
    [Test]
    public void NativeStatuePermissionChecksEntireFootprintBeforeCooldownAndNpcWrite()
    {
        RunScenario((actor, session, region) =>
        {
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (session, actor)); guard.Install();
            Wiring.SetCurrentUser(actor.Index);
            Assert.That(actor.HasBuildPermission(20, 20, false), Is.True);
            Assert.That(actor.HasBuildPermission(21, 22, false), Is.False);
            Wiring.HitWireSingle(20, 20);
            Assert.That(guard.PermissionBlocked, Is.EqualTo(1));
            Assert.That(Wiring._numMechs, Is.Zero); Assert.That(Main.npc.Any(n => n.active), Is.False);
            region.AllowedIDs.Add(actor.Account.ID);
            Wiring.HitWireSingle(20, 20);
            Assert.That(guard.Allowed, Is.EqualTo(1)); Assert.That(Wiring._numMechs, Is.EqualTo(1));
            Assert.That(Main.npc.Count(n => n.active && n.type == 1), Is.EqualTo(1));
            Assert.That(Main.npc.Single(n => n.active).SpawnedFromStatue, Is.True);
        });
    }

    [Test]
    public void TimerStatueActualExecutionBudgetCancelsBeforeCooldownAndNpcWriteWithoutActor()
    {
        RunScenario((actor, session, region) =>
        {
            var clock = new TestClock();
            using var guard = new M6WiringExecutionGuard(clock, _ => throw new AssertionException("Timer must not resolve a nearby player."),
                globalBudget: new(1, 1, 1, TimeSpan.FromMinutes(1))); guard.Install();
            Wiring.SetCurrentUser();
            Wiring.HitWireSingle(20, 20);
            Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(1));
            // A second distinct statue is a new actual operation, not another packet59 counter.
            Wiring.HitWireSingle(24, 20);
            Assert.That(guard.BudgetBlocked, Is.EqualTo(1)); Assert.That(Wiring._numMechs, Is.EqualTo(1));
            Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            Wiring.HitWireSingle(24, 20);
            Assert.That(guard.Allowed, Is.EqualTo(2)); Assert.That(Wiring._numMechs, Is.EqualTo(2));
            Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(2));
        });
    }

    [Test]
    public void CoreCancellationIsPreservedAndRevokedActorHasNoStatueSideEffects()
    {
        RunScenario((actor, session, region) =>
        {
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (session with { Revoked = true }, actor)); guard.Install();
            Wiring.SetCurrentUser(actor.Index); Wiring.HitWireSingle(20, 20);
            Assert.That(guard.PermissionBlocked, Is.EqualTo(1)); Assert.That(Wiring._numMechs, Is.Zero);
            Assert.That(Main.npc.Any(n => n.active), Is.False);
            void Cancel(object? _, HookEvents.Terraria.Wiring.HitWireSingleEventArgs e) => e.ContinueExecution = false;
            guard.Dispose(); HookEvents.Terraria.Wiring.HitWireSingle += Cancel; guard.Install();
            try { Wiring.HitWireSingle(20, 20); Assert.That(guard.PermissionBlocked, Is.EqualTo(1)); }
            finally { HookEvents.Terraria.Wiring.HitWireSingle -= Cancel; }
        });
    }

    [Test]
    public void RealSwitchGraphReachesStatueAndCannotBypassProtectedRemoteEndpoint()
    {
        RunScenario((actor, session, region) =>
        {
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (session, actor)); guard.Install();
            Main.tile[18, 20].active(true); Main.tile[18, 20].type = 135;
            for (int x = 18; x <= 20; x++) Main.tile[x, 20].wire(true);
            Wiring.SetCurrentUser(actor.Index); Wiring.HitSwitch(18, 20); Wiring.SetCurrentUser();
            Assert.That(guard.PermissionBlocked, Is.EqualTo(1)); Assert.That(Main.npc.Any(n => n.active), Is.False);
            Assert.That(Wiring._numMechs, Is.Zero);
            region.AllowedIDs.Add(actor.Account.ID);
            Wiring.SetCurrentUser(actor.Index); Wiring.HitSwitch(18, 20); Wiring.SetCurrentUser();
            Assert.That(guard.Allowed, Is.EqualTo(1)); Assert.That(Main.npc.Count(n => n.active && n.type == 1), Is.EqualTo(1));
            Assert.That(Wiring.CurrentUser, Is.EqualTo(255)); Assert.That(Wiring.running, Is.False);
        });
    }

    [Test]
    public void SlotReuseDoesNotInheritActorAttributionAndMaintenanceDoesNotAccuseTimers()
    {
        RunScenario((actor, session, region) =>
        {
            var current = session;
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => (current, actor), writesAvailable: () => false);
            guard.Install(); Wiring.SetCurrentUser(actor.Index);
            Wiring.HitWireSingle(20, 20);
            Assert.That(guard.PermissionBlocked, Is.EqualTo(1)); Assert.That(Wiring._numMechs, Is.Zero);
            current = current with { Key = current.Key with { Generation = 2 } };
            Wiring.HitWireSingle(20, 20);
            Assert.That(guard.UnknownActor, Is.EqualTo(1)); Assert.That(guard.PermissionBlocked, Is.EqualTo(1));
            Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(1));
            Wiring.SetCurrentUser(); Wiring.HitWireSingle(24, 20);
            Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(2));
        });
    }

    [Test]
    public void SourceObserverFaultLatchesOnceAndLeavesIndependentCancellationInstalled()
    {
        RunScenario((actor, session, region) =>
        {
            int reports = 0, independentCalls = 0;
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => throw new IOException("fixture resolver unavailable"));
            guard.IntegrityFault = _ => { reports++; throw new IOException("fixture reporter unavailable"); };
            guard.Install();
            void CoreProtection(object? _, HookEvents.Terraria.Wiring.HitWireSingleEventArgs args)
            { independentCalls++; args.ContinueExecution = false; }
            HookEvents.Terraria.Wiring.HitWireSingle += CoreProtection;
            try
            {
                Assert.DoesNotThrow(() => Wiring.SetCurrentUser(actor.Index));
                guard.Install(); // A plain re-install cannot clear a latched fault.
                Wiring.SetCurrentUser(actor.Index); Wiring.HitWireSingle(20, 20);
                Assert.That(reports, Is.EqualTo(1)); Assert.That(guard.ContractHealthy, Is.False);
                Assert.That(guard.FaultObserverFailed, Is.True); Assert.That(independentCalls, Is.EqualTo(1));
                Assert.That(Wiring._numMechs, Is.Zero); Assert.That(Main.npc.Any(n => n.active), Is.False);
            }
            finally { HookEvents.Terraria.Wiring.HitWireSingle -= CoreProtection; }
        });
    }

    [Test]
    public void ExecutionObserverFaultOnlyDisablesThisLocalGuardAndDoesNotInventViolation()
    {
        RunScenario((actor, session, region) =>
        {
            int calls = 0, reports = 0;
            using var guard = new M6WiringExecutionGuard(TimeProvider.System, _ => ++calls == 1
                ? (session, actor) : throw new IOException("fixture execution resolver failed"));
            guard.IntegrityFault = _ => reports++;
            guard.Install(); Wiring.SetCurrentUser(actor.Index);
            Assert.DoesNotThrow(() => Wiring.HitWireSingle(20, 20));
            Assert.That(reports, Is.EqualTo(1)); Assert.That(guard.ContractHealthy, Is.False);
            Assert.That(guard.PermissionBlocked, Is.Zero); Assert.That(guard.BudgetBlocked, Is.Zero);
            Assert.That(Main.npc.Count(n => n.active), Is.EqualTo(1), "Unknown local observer state cannot become a fabricated violation.");
        });
    }

    internal static void RunScenario(Action<TSPlayer, SessionSnapshot, Region> execute)
    {
        const int slot = 15;
        var oldPlayers = Main.player; var oldNpcs = Main.npc; var oldProjectiles = Main.projectile; var oldConfig = ServerTShock.Config; var oldRegions = ServerTShock.Regions;
        var oldMode = Main.netMode; var oldLocal = Main.myPlayer; var oldWidth = Main.maxTilesX; var oldHeight = Main.maxTilesY;
        var oldDed = Main.dedServ; var oldRandom = Main.rand; var oldUser = Wiring.CurrentUser;
        var oldWiring = typeof(Wiring).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => !field.IsInitOnly && !field.IsLiteral)
            .Select(field => (Field: field, Value: field.GetValue(null))).ToArray();
        var tiles = new List<(int X, int Y, ITile Tile)>();
        var sends = new List<int>();
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs e) { sends.Add(e.msgType); e.ContinueExecution = false; }
        HookEvents.Terraria.NetMessage.SendData += Sink;
        try
        {
            Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 500; Main.maxTilesY = 500; Main.dedServ = true;
            Main.rand = new Terraria.Utilities.UnifiedRandom(26);
            Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
            Main.player[slot].active = true; Main.player[slot].position = new(320, 320);
            Main.npc = Enumerable.Range(0, 201).Select(i => new NPC { whoAmI = i }).ToArray();
            Main.projectile = Enumerable.Range(0, 1000).Select(i => new Projectile { whoAmI = i }).ToArray();
            Wiring.Initialize(); Wiring._numMechs = 0;
            for (int x = 17; x <= 27; x++) for (int y = 19; y <= 24; y++)
            { tiles.Add((x, y, Main.tile[x, y])); Main.tile[x, y] = new Tile(); }
            foreach (int origin in new[] { 20, 24 })
                for (int x = origin; x < origin + 2; x++) for (int y = 20; y < 23; y++)
                {
                    var tile = new Tile { type = 105, frameX = (short)(4 * 36 + (x - origin) * 18), frameY = (short)((y - 20) * 18) };
                    tile.active(true); Main.tile[x, y] = tile;
                }
            ServerTShock.Config = new TShockConfig(); ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            var region = new Region(1, new Rectangle(21, 22, 1, 1), "statue-endpoint", "other-owner", true, Main.worldID.ToString(), 0);
            ServerTShock.Regions.Regions = [region];
            var actor = new TSPlayer(slot) { IsLoggedIn = true, Group = new Group("m6-wire", permissions: Permissions.canbuild), Account = new UserAccount { ID = 140, Name = "m6-wire" } };
            var session = new SessionSnapshot(new(Guid.NewGuid(), 1, slot, 1), 140, false, DateTimeOffset.UtcNow);
            execute(actor, session, region);
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendData -= Sink;
            foreach (var (x, y, tile) in tiles) Main.tile[x, y] = tile;
            Main.player = oldPlayers; Main.npc = oldNpcs; Main.projectile = oldProjectiles; ServerTShock.Config = oldConfig; ServerTShock.Regions = oldRegions;
            Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.dedServ = oldDed; Main.rand = oldRandom;
            foreach (var (field, value) in oldWiring) field.SetValue(null, value);
        }
    }
    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
}
