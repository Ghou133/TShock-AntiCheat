using System.Reflection;
using System.Runtime.CompilerServices;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.UI;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

/// <summary>Actual target core permission handler and native world operation in their audited dispatch order.</summary>
[TestFixture, NonParallelizable]
public sealed class M5WorldBoundaryTests
{
    [TestCase(1)] [TestCase(-1)]
    public void ProtectedEndpointRejectsBeforeAnyWireWriteAndAuthorizedNativeOperationStillWorks(int facing)
    {
        const int slot = 15;
        var priorConfig = ServerTShock.Config; var priorRegions = ServerTShock.Regions; var priorLog = ServerTShock.Log;
        var priorPlayer = Main.player[slot]; var priorMode = Main.netMode; var priorLocal = Main.myPlayer;
        var priorWidth = Main.maxTilesX; var priorHeight = Main.maxTilesY; var priorTool = WiresUI.Settings.ToolMode;
        var priorHandlers = GetDataHandlers.MassWireOperation;
        var priorTiles = new List<(Point Point, ITile? Tile)>();
        var sends = new List<(int Type, int Number, int Number2)>();
        string logPath = Path.Combine(Path.GetTempPath(), "anticheat-m5-wire-" + Guid.NewGuid().ToString("N") + ".log");
        using var log = new TextLog(logPath, false);
        void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        {
            sends.Add((args.msgType, args.number, (int)args.number2));
            args.ContinueExecution = false; // Fixture transport sink only; native tile writes run normally.
        }
        HookEvents.Terraria.NetMessage.SendData += Sink;
        try
        {
            Main.maxTilesX = 500; Main.maxTilesY = 500; Main.netMode = 2; Main.myPlayer = 255;
            Main.player[slot] = new Player { whoAmI = slot, active = true, direction = facing, position = new(320, 320) };
            Main.player[slot].inventory[0].SetDefaults(ItemID.Wire); Main.player[slot].inventory[0].stack = 100;
            var actor = new TSPlayer(slot) { IsLoggedIn = true, Group = new Group("m5-wire", permissions: Permissions.canbuild),
                Account = new UserAccount { ID = 140, Name = "m5-wire" } };
            ServerTShock.Config = new TShockConfig(); ServerTShock.Log = log;
            ServerTShock.Config.Settings.DisableBuild = false; ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.RequireLogin = false; ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            // RegionManager.CanBuild reads only Regions. Avoid a constructor/database or a second server lifecycle.
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            var region = new Region(1, new Rectangle(22, 22, 1, 1), "protected-endpoint", "other-owner", true, Main.worldID.ToString(), 0);
            ServerTShock.Regions.Regions = [region];
            var bouncerType = typeof(TSPlayer).Assembly.GetType("TShockAPI.Bouncer")!;
            var bouncer = RuntimeHelpers.GetUninitializedObject(bouncerType);
            var permissionHandler = bouncerType.GetMethod("OnMassWireOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<GetDataHandlers.MassWireOperationEventArgs>>(bouncer);
            GetDataHandlers.MassWireOperation = new(); GetDataHandlers.MassWireOperation.Register(permissionHandler);
            var parser = typeof(GetDataHandlers).GetMethod("HandleMassWireOperation", BindingFlags.Static | BindingFlags.NonPublic)!;
            var start = new Point(20, 20); var end = new Point(22, 22);
            var path = TShockAPI.Utils.Instance.GetMassWireOperationRange(start, end, facing == 1);
            foreach (var point in path)
            {
                priorTiles.Add((point, Main.tile[point.X, point.Y])); Main.tile[point.X, point.Y] = new Tile();
            }
            var body = M5WorldCostTests.Wire(20, 20, 22, 22);
            Assert.That(M5WorldCost.ReadPayload(109, body, 500, 500).WorkUnits, Is.EqualTo(path.Count));
            Assert.That(actor.HasBuildPermission(start.X, start.Y, false), Is.True);
            Assert.That(actor.HasBuildPermission(end.X, end.Y, false), Is.False);
            bool Dispatch()
            {
                using var stream = new MemoryStream(body, writable: false);
                bool handled = (bool)parser.Invoke(null, [new GetDataHandlerArgs(actor, stream)])!;
                if (!handled)
                {
                    // The native packet109 branch runs only after the core parser returns false.
                    WiresUI.Settings.ToolMode = WiresUI.Settings.MultiToolMode.Red;
                    Wiring.MassWireOperation(start, end, actor.TPlayer);
                }
                return handled;
            }
            Assert.That(Dispatch(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(path.All(p => !Main.tile[p.X, p.Y].wire()), Is.True,
                    "A permission failure on the last endpoint must not leave a partly written path.");
                Assert.That(sends, Is.Empty); Assert.That(actor.TPlayer.inventory[0].stack, Is.EqualTo(100));
            });
            region.AllowedIDs.Add(actor.Account.ID);
            Assert.That(Dispatch(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(path.Count, Is.EqualTo(5)); Assert.That(path.All(p => Main.tile[p.X, p.Y].wire()), Is.True);
                Assert.That(sends.Count(s => s.Type == 17), Is.EqualTo(path.Count));
                Assert.That(sends.Where(s => s.Type == 110), Is.EqualTo(new[] { (110, ItemID.Wire, path.Count), (110, ItemID.Actuator, 0) }));
                Assert.That(actor.TPlayer.inventory[0].stack, Is.EqualTo(100),
                    "The target server requests consumption via110; local server inventory is not decremented here.");
            });
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendData -= Sink; GetDataHandlers.MassWireOperation = priorHandlers;
            foreach (var (point, tile) in priorTiles) Main.tile[point.X, point.Y] = tile!;
            ServerTShock.Config = priorConfig; ServerTShock.Regions = priorRegions; ServerTShock.Log = priorLog;
            Main.player[slot] = priorPlayer; Main.netMode = priorMode; Main.myPlayer = priorLocal;
            Main.maxTilesX = priorWidth; Main.maxTilesY = priorHeight; WiresUI.Settings.ToolMode = priorTool;
            log.Dispose(); File.Delete(logPath);
        }
    }
}
