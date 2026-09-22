using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture(false), TestFixture(true), NonParallelizable]
public sealed class M16WorldPaintTests(bool nativeProvider)
{
    [Test]
    public void WallOnlyCellAndRepaintUseReal64AndPreserveAllOtherState()
    {
        Run(s =>
        {
            Main.tile[20, 20].active(false);
            var before = M9PaintTileState.Capture(Main.tile[20, 20]);
            s.Paint(7); s.Paint(7);
            Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(7));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(3));
            Assert.That(Main.tile[20, 20].active(), Is.False);
            Assert.That(Main.tile[20, 20].wall, Is.EqualTo(before.Wall));
            Assert.That(s.Guard.Allowed, Is.EqualTo(2));
            Assert.That(s.Guard.Unknown, Is.Zero);
            Assert.That(s.Relays, Is.EqualTo(2));
            Assert.That(s.Guard.Snapshot().Select(x => x.PacketType), Is.EqualTo(new[] { 64, 64 }));
            Assert.That(s.Guard.Snapshot().Select(x => x.Outcome), Is.EqualTo(new[] { "authorized-commit", "native-no-change" }));
        });
    }

    [Test]
    public void WallPermissionLostAfterCoreBeforeBodyBlocksWriteAndRelayWithoutBan()
    {
        Run(s =>
        {
            void Before(object? _, HookEvents.Terraria.WorldGen.paintWallEventArgs e) => s.Region.AllowedIDs.Clear();
            HookEvents.Terraria.WorldGen.paintWall += Before;
            try { s.Paint(7); }
            finally { HookEvents.Terraria.WorldGen.paintWall -= Before; }
            Assert.That(s.Guard.BeforeWriteBlocked, Is.EqualTo(1));
            Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(2));
            Assert.That(s.EffectEntered, Is.False);
            Assert.That(s.Relays, Is.Zero);
            Assert.That(s.Guard.RelaySuppressed, Is.EqualTo(1));
            Assert.That(s.Resyncs, Is.EqualTo(1));
            Assert.That(s.Session.Revoked, Is.False);
        });
    }

    [Test]
    public void WallNativeWritesBeforeEffectAndPermissionLossRestoresOnlyWallColor()
    {
        Run(s =>
        {
            var before = M9PaintTileState.Capture(Main.tile[20, 20]);
            s.DuringPaint = () =>
            {
                Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(7), "Wall paint commits before paintEffect, unlike block paint.");
                s.Region.AllowedIDs.Clear();
            };
            s.Paint(7);
            Assert.That(M9PaintTileState.Capture(Main.tile[20, 20]), Is.EqualTo(before));
            var result = s.Guard.Snapshot().Single();
            Assert.That(result.Outcome, Is.EqualTo("authorization-lost-restored"));
            Assert.That(result.Revision, Is.EqualTo(1));
            Assert.That(result.Before, Is.EqualTo(2));
            Assert.That(result.Current, Is.EqualTo(2));
            Assert.That(result.PacketType, Is.EqualTo(64));
            Assert.That(s.Relays, Is.Zero);
            Assert.That(s.Resyncs, Is.EqualTo(1));
            s.Paint(7);
            Assert.That(s.Guard.Restored, Is.EqualTo(1), "A new denied request cannot repeat compensation.");
            s.DuringPaint = null; s.Region.AllowedIDs.Add(140); s.Paint(8);
            Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(8));
            Assert.That(s.Relays, Is.EqualTo(1));
            Assert.That(s.Session.Revoked, Is.False);
        });
    }

    [TestCase("wall-color-aba", 7)]
    [TestCase("wall-type-aba", 7)]
    [TestCase("block-color-aba", 7)]
    [TestCase("replacement", 7)]
    [TestCase("later-legal-paint", 13)]
    [TestCase("world-change", 7)]
    [TestCase("session-reuse", 7)]
    public void LaterWorldWritesAndIdentityChangesCannotBeOverwritten(string change, byte expected)
    {
        Run(s =>
        {
            s.DuringPaint = () =>
            {
                var tile = Main.tile[20, 20];
                switch (change)
                {
                    case "wall-color-aba": tile.wallColor(13); tile.wallColor(7); break;
                    case "wall-type-aba": tile.wall = 2; tile.wall = 1; break;
                    case "block-color-aba": tile.color(12); tile.color(3); break;
                    case "replacement":
                        var copy = new Tile(); copy.CopyFrom(tile); Main.tile[20, 20] = new Tile(); Main.tile[20, 20] = copy; break;
                    case "later-legal-paint": tile.wallColor(13); break;
                    case "world-change": Main.ActiveWorldFileData.WorldId++; break;
                    case "session-reuse": s.Session = s.Session with { Key = s.Session.Key with { Generation = 2 } }; break;
                }
                s.Region.AllowedIDs.Clear();
            };
            s.Paint(7);
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.Guard.RecoveryRefused, Is.EqualTo(1));
            Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(expected));
            Assert.That(s.Relays, Is.Zero);
            Assert.That(s.Session.Revoked, Is.False);
        });
    }

    [Test]
    public void TilePaintCalledInsideWallEffectCannotBorrowWallRequest()
    {
        Run(s =>
        {
            s.DuringPaint = () => { WorldGen.paintTile(20, 20, 12, false, false); s.Region.AllowedIDs.Clear(); };
            s.Paint(7);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(12));
            Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(7));
            Assert.That(s.Guard.Allowed, Is.EqualTo(1));
            Assert.That(s.Guard.RecoveryRefused, Is.EqualTo(1));
            Assert.That(s.Guard.Snapshot(), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void MissingWallAndObservationLossStayUnknownWithoutBlockingNormalRequest()
    {
        Run(s =>
        {
            Main.tile[20, 20].wall = 0; s.Paint(7);
            Assert.That(s.Guard.Unknown, Is.EqualTo(1));
            Assert.That(s.Relays, Is.EqualTo(1));
            Main.tile[20, 20].wall = 1; s.Guard.InvalidateObservation(); s.Paint(8);
            Assert.That(Main.tile[20, 20].wallColor(), Is.EqualTo(8));
            Assert.That(s.Relays, Is.EqualTo(2));
            Assert.That(s.Guard.BeforeWriteBlocked, Is.Zero);
            Assert.That(s.Guard.Restored, Is.Zero);
        });
    }

    private sealed class Scenario
    {
        public required Region Region;
        public required SessionSnapshot Session;
        public required M9PaintRecovery Guard;
        public Action? DuringPaint;
        public int Relays, Resyncs;
        public bool EffectEntered;
        public void Paint(byte color)
        {
            EffectEntered = false;
            var buffer = new MessageBuffer { whoAmI = 7 };
            buffer.readBuffer[0] = 64;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.readBuffer.AsSpan(1), 20);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.readBuffer.AsSpan(3), 20);
            buffer.readBuffer[5] = color; buffer.readBuffer[6] = 0;
            buffer.ResetReader(); buffer.GetData(0, 7, out int kind); Assert.That(kind, Is.EqualTo(64));
        }
    }
    private void Run(Action<Scenario> run)
    {
        var oldTiles = Main.tile; var oldPlayers = Main.player; var oldClients = Netplay.Clients;
        var oldConfig = ServerTShock.Config; var oldRegions = ServerTShock.Regions;
        int oldMode = Main.netMode, oldLocal = Main.myPlayer, oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY, oldWorld = Main.worldID;
        bool oldDed = Main.dedServ;
        Scenario? s = null;
        void Effect(object? _, HookEvents.Terraria.WorldGen.paintEffectEventArgs e)
        { if (s is not null) s.EffectEntered = true; s?.DuringPaint?.Invoke(); e.ContinueExecution = false; }
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs e)
        {
            if (s is not null && e.ContinueExecution && e.msgType == 64 && e.ignoreClient == 7) s.Relays++;
            if (s is not null && e.ContinueExecution && e.msgType == 20) s.Resyncs++;
            e.ContinueExecution = false;
        }
        try
        {
            Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = Main.maxTilesY = 100; Main.dedServ = true;
            Main.tile = nativeProvider ? new ConstileationProvider() : new ModFramework.DefaultCollection<ITile>(100, 100);
            _ = Main.tile[0, 0];
            for (int x = 17; x <= 23; x++) for (int y = 17; y <= 23; y++) Main.tile[x, y] = new Tile();
            Main.tile[20, 20].active(true); Main.tile[20, 20].type = 1; Main.tile[20, 20].wall = 1;
            Main.tile[20, 20].color(3); Main.tile[20, 20].wallColor(2);
            Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
            Main.player[7].active = true; Main.player[7].position = new(320, 320);
            Netplay.Clients = Enumerable.Range(0, 256).Select(i => new RemoteClient { Id = i, State = 10 }).ToArray();
            ServerTShock.Config = new TShockConfig(); ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            var region = new Region(1, new Rectangle(20, 20, 1, 1), "m16-wall", "owner", true, Main.worldID.ToString(), 0);
            region.AllowedIDs.Add(140); ServerTShock.Regions.Regions = [region];
            var actor = new TSPlayer(7) { IsLoggedIn = true, Group = new Group("m16-wall", permissions: Permissions.canbuild + "," + Permissions.canpaint), Account = new UserAccount { ID = 140, Name = "m16-wall" } };
            var session = new SessionSnapshot(new(Guid.NewGuid(), 1, 7, 1), 140, false, DateTimeOffset.UtcNow);
            using var guard = new M9PaintRecovery(TimeProvider.System, _ => (s?.Session ?? session, actor));
            guard.Install(); guard.Tick(1);
            s = new() { Region = region, Session = session, Guard = guard };
            HookEvents.Terraria.WorldGen.paintEffect += Effect; HookEvents.Terraria.NetMessage.SendData += Sink;
            run(s);
        }
        finally
        {
            HookEvents.Terraria.WorldGen.paintEffect -= Effect; HookEvents.Terraria.NetMessage.SendData -= Sink;
            Main.tile = oldTiles; Main.player = oldPlayers; Netplay.Clients = oldClients;
            ServerTShock.Config = oldConfig; ServerTShock.Regions = oldRegions;
            Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.ActiveWorldFileData.WorldId = oldWorld; Main.dedServ = oldDed;
        }
    }
}
