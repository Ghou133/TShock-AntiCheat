using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Reflection;
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
public sealed class M9PaintRecoveryTests(bool nativeProvider)
{
    [Test]
    public void ActualPacketAndNativePaintEntersEffectiveBranchAndAuthorizedRepaintIsRetained()
    {
        Run(s =>
        {
            s.Paint(7); s.Paint(9); s.Paint(9);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(9));
            Assert.That(s.Guard.Allowed, Is.EqualTo(3));
            Assert.That(s.Guard.Unknown, Is.Zero);
            Assert.That(s.Guard.Snapshot().Select(x => x.Outcome), Is.EqualTo(new[] { "authorized-commit", "authorized-commit", "native-no-change" }));
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.ForwardedPaints, Is.EqualTo(3));
        });
    }

    [Test]
    public void LostPermissionBeforeNativeWriteBlocksColorAndRelayWithoutBan()
    {
        Run(s =>
        {
            s.Region.AllowedIDs.Clear(); s.Paint(7);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(2));
            Assert.That(s.Guard.BeforeWriteBlocked, Is.EqualTo(1));
            Assert.That(s.Guard.Allowed, Is.Zero);
            Assert.That(s.Guard.RelaySuppressed, Is.EqualTo(1));
            Assert.That(s.ForwardedPaints, Is.Zero);
            Assert.That(s.Session.Revoked, Is.False);
        });
    }

    [Test]
    public void PermissionChangedByWrapperBeforeNativeBodyIsBlockedWithoutCompensation()
    {
        Run(s =>
        {
            void Before(object? _, HookEvents.Terraria.WorldGen.paintTileEventArgs e) => s.Region.AllowedIDs.Clear();
            HookEvents.Terraria.WorldGen.paintTile += Before;
            try { s.Paint(7); }
            finally { HookEvents.Terraria.WorldGen.paintTile -= Before; }
            Assert.That(s.NativeEffectEntered, Is.False);
            Assert.That(s.Guard.BeforeWriteBlocked, Is.EqualTo(1));
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(2));
            Assert.That(s.ForwardedPaints, Is.Zero);
        });
    }

    [Test]
    public void PermissionLostInsideNativeCommitRestoresExactlyOneFieldBeforeRelayAndDoesNotReplay()
    {
        Run(s =>
        {
            s.DuringPaint = () => s.Region.AllowedIDs.Clear(); s.Paint(7);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(2));
            Assert.That(s.Guard.Restored, Is.EqualTo(1));
            Assert.That(s.Guard.Snapshot().Single().Revision, Is.EqualTo(1));
            Assert.That(s.Guard.Snapshot().Single().Outcome, Is.EqualTo("authorization-lost-restored"));
            Assert.That(s.Guard.RelaySuppressed, Is.EqualTo(1));
            Assert.That(s.ForwardedPaints, Is.Zero);
            s.Paint(7); // A distinct denied request cannot replay or repeat the previous recovery.
            Assert.That(s.Guard.Restored, Is.EqualTo(1));
            s.DuringPaint = null; s.Region.AllowedIDs.Add(140); s.Paint(8);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(8));
            Assert.That(s.ForwardedPaints, Is.EqualTo(1));
        });
    }

    [TestCase("paint-aba")]
    [TestCase("type-aba")]
    [TestCase("replace-aba")]
    [TestCase("observation-loss")]
    [TestCase("world-change")]
    [TestCase("session-reuse")]
    [TestCase("offthread-aba")]
    [TestCase("clear-aba")]
    public void InterveningEditsReplacementUnknownWorldOrIdentityRefuseCompensation(string change)
    {
        Run(s =>
        {
            s.DuringPaint = () =>
            {
                var tile = Main.tile[20, 20];
                switch (change)
                {
                    case "paint-aba": tile.color(12); tile.color(2); break;
                    case "type-aba": tile.type = 0; tile.type = 1; break;
                    case "replace-aba": Main.tile[20, 20] = new Tile(); Main.tile[20, 20] = tile; break;
                    case "observation-loss": s.Guard.InvalidateObservation(); break;
                    case "world-change": Main.ActiveWorldFileData.WorldId++; break;
                    case "session-reuse": s.Session = s.Session with { Key = s.Session.Key with { Generation = 2 } }; break;
                    case "offthread-aba":
                        var worker = new Thread(() => { tile.color(15); tile.color(2); }); worker.Start();
                        Assert.That(worker.Join(TimeSpan.FromSeconds(3)), Is.True); break;
                    case "clear-aba":
                        var snapshot = new Tile(); snapshot.CopyFrom(tile); tile.ClearEverything(); tile.CopyFrom(snapshot); break;
                }
                s.Region.AllowedIDs.Clear();
            };
            s.Paint(7);
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.Guard.RecoveryRefused, Is.EqualTo(1));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(7), "A changed dependency must not be overwritten by an earlier snapshot.");
            Assert.That(s.ForwardedPaints, Is.Zero);
        });
    }

    [Test]
    public void NativeCloneAliasesTheSameStorageAndMustNotEvadeRevisionTracking()
    {
        Run(s =>
        {
            s.DuringPaint = () =>
            {
                var alias = (Tile)Main.tile[20, 20].Clone(); alias.color(12); alias.color(2);
                s.Region.AllowedIDs.Clear();
            };
            s.Paint(7);
            Assert.That(s.Guard.Restored, Is.EqualTo(nativeProvider ? 0 : 1));
            Assert.That(s.Guard.RecoveryRefused, Is.EqualTo(nativeProvider ? 1 : 0));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(nativeProvider ? 7 : 2));
        });
    }

    [Test]
    public void PostCommitAuthorizationCallbackCannotHideLaterAbaOrOverwriteItsState()
    {
        Run(s =>
        {
            s.DuringPostcheck = () =>
            {
                var tile = Main.tile[20, 20]; tile.color(12); tile.color(7);
                s.Region.AllowedIDs.Clear();
            };
            s.Paint(7);
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.Guard.RecoveryRefused, Is.EqualTo(1));
            var evidence = s.Guard.Snapshot().Single();
            Assert.That(evidence.Revision, Is.EqualTo(3));
            Assert.That(evidence.CommittedState.Header & 31, Is.EqualTo(7));
            Assert.That(evidence.BeforeState.Header & 31, Is.EqualTo(2));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(7));
            Assert.That(s.ForwardedPaints, Is.Zero);
        });
    }

    [Test]
    public void OffThreadWriteBeforeRequestPermanentlyWithdrawsRecoveryInsteadOfEscapingScopePublication()
    {
        Run(s =>
        {
            var worker = new Thread(() => { var tile = Main.tile[20, 20]; tile.color(12); tile.color(2); });
            worker.Start(); Assert.That(worker.Join(TimeSpan.FromSeconds(3)), Is.True);
            Assert.That(s.Guard.Healthy, Is.False);
            s.Clock.Advance(TimeSpan.FromSeconds(31)); s.Guard.Tick(1);
            s.DuringPaint = () => s.Region.AllowedIDs.Clear(); s.Paint(7);
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.Guard.Snapshot(), Is.Empty);
            Assert.That(s.Guard.InvalidReason, Is.EqualTo("off-update-thread-world-writer"));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(7));
        });
    }

    [Test]
    public void FullJournalRefusesRestorationButStillSuppressesUnauthorizedRelay()
    {
        Run(s =>
        {
            for (int i = 0; i < M9PaintRecovery.Capacity; i++) s.Paint((byte)(i % 2 + 5));
            s.DuringPaint = () => s.Region.AllowedIDs.Clear(); s.Paint(9);
            Assert.That(s.Guard.Snapshot(), Has.Length.EqualTo(M9PaintRecovery.Capacity));
            Assert.That(s.Guard.Dropped, Is.EqualTo(1));
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.Guard.RecoveryRefused, Is.EqualTo(1));
            Assert.That(s.Guard.RelaySuppressed, Is.EqualTo(1));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(9));
        });
    }

    [Test]
    public void ServerLocalPaintCannotBorrowClientResponsibilityAndNativeExceptionIsNotRetried()
    {
        Run(s =>
        {
            WorldGen.paintTile(20, 20, 6, false, false);
            Assert.That(s.Guard.Snapshot(), Is.Empty);
            s.DuringPaint = () => throw new InvalidOperationException("native paint hook fault");
            Assert.Throws<InvalidOperationException>(() => s.Paint(7));
            Assert.That(s.Guard.NativeFailures, Is.EqualTo(1));
            Assert.That(s.Guard.Snapshot(), Is.Empty);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(6));
            Assert.That(s.Guard.Restored, Is.Zero);
        });
    }

    [Test]
    public void ObserverFailureAfterWriteRetainsCommittedEvidenceAndDoesNotPretendItBlockedWrite()
    {
        Run(s =>
        {
            var failure = new InvalidOperationException("postcheck failure");
            s.DuringPostcheck = () => throw failure;
            Assert.That(Assert.Throws<InvalidOperationException>(() => s.Paint(7)), Is.SameAs(failure));
            Assert.That(s.Guard.NativeFailures, Is.Zero);
            Assert.That(s.Guard.ObserverFailures, Is.EqualTo(1));
            Assert.That(s.Guard.Healthy, Is.False);
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(7));
            Assert.That(s.Guard.Snapshot().Single().Outcome, Is.EqualTo("postcommit-observer-failed-no-restore"));
            Assert.That(s.ForwardedPaints, Is.Zero, "The original GetData unwinds before its relay after the observer exception.");
        });
    }

    [Test]
    public void UnsupportedPluginHistoryWithdrawsOnlyRecoveryAndCannotBeErasedByUnloadOrTtl()
    {
        Run(s =>
        {
            var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            using var plugin = new UnknownWorldPlugin(); var container = new PluginContainer(plugin); plugins.Add(container);
            try { s.Paint(7); Assert.That(s.Guard.Healthy, Is.False); }
            finally { plugins.Remove(container); }
            s.Clock.Advance(TimeSpan.FromSeconds(31)); s.Guard.Tick(2);
            s.Session = s.Session with { Key = s.Session.Key with { WorldEpoch = 2 } };
            s.DuringPaint = () => s.Region.AllowedIDs.Clear(); s.Paint(8);
            Assert.That(s.Guard.Restored, Is.Zero);
            Assert.That(s.Guard.Snapshot(), Is.Empty);
            Assert.That(s.Guard.InvalidReason, Is.EqualTo("unsupported-host-observation-history"));
            Assert.That(Main.tile[20, 20].color(), Is.EqualTo(8), "No unknown plugin history is turned into permission or rollback authority.");
        });
    }

    [Test]
    public void JournalCapacityAndTtlNeverCreateRollbackAuthority()
    {
        Run(s =>
        {
            for (int i = 0; i < M9PaintRecovery.Capacity + 3; i++) s.Paint((byte)(i % 2 + 5));
            Assert.That(s.Guard.Snapshot(), Has.Length.EqualTo(M9PaintRecovery.Capacity));
            Assert.That(s.Guard.Dropped, Is.EqualTo(3));
            s.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.That(s.Guard.Snapshot(), Is.Empty);
            Assert.That(s.Guard.Restored, Is.Zero);
        });
    }

    private sealed class Scenario
    {
        public required TSPlayer Actor;
        public required Region Region;
        public required SessionSnapshot Session;
        public required M9PaintRecovery Guard;
        public required TestClock Clock;
        public Action? DuringPaint;
        public Action? DuringPostcheck;
        public bool NativeEffectEntered, PostcheckInvoked;
        public int ForwardedPaints;
        public void Paint(byte color)
        {
            NativeEffectEntered = PostcheckInvoked = false;
            var buffer = new MessageBuffer { whoAmI = 7 };
            buffer.readBuffer[0] = 63;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.readBuffer.AsSpan(1), 20);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.readBuffer.AsSpan(3), 20);
            buffer.readBuffer[5] = color; buffer.readBuffer[6] = 0;
            buffer.ResetReader(); buffer.GetData(0, 7, out int kind); Assert.That(kind, Is.EqualTo(63));
        }
    }
    private void Run(Action<Scenario> run)
    {
        var oldTiles = Main.tile; var oldPlayers = Main.player; var oldClients = Netplay.Clients;
        var oldConfig = ServerTShock.Config; var oldRegions = ServerTShock.Regions;
        int oldMode = Main.netMode, oldLocal = Main.myPlayer, oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY, oldWorld = Main.worldID;
        bool oldDed = Main.dedServ;
        Scenario? s = null;
        void Effects(object? _, HookEvents.Terraria.WorldGen.paintEffectEventArgs e)
        { if (s is not null) s.NativeEffectEntered = true; s?.DuringPaint?.Invoke(); e.ContinueExecution = false; } // Dust rendering isolated from color commit; no fake paint call.
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs e)
        { if (s is not null && e.ContinueExecution && e.msgType == 63 && e.ignoreClient == 7) s.ForwardedPaints++; e.ContinueExecution = false; }
        try
        {
            Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = Main.maxTilesY = 100; Main.dedServ = true;
            Main.tile = nativeProvider ? new ConstileationProvider() : new ModFramework.DefaultCollection<ITile>(100, 100);
            _ = Main.tile[0, 0];
            for (int x = 17; x <= 23; x++) for (int y = 17; y <= 23; y++) Main.tile[x, y] = new Tile();
            Main.tile[20, 20].active(true); Main.tile[20, 20].type = 1; Main.tile[20, 20].color(2);
            Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
            Main.player[7].active = true; Main.player[7].position = new(320, 320);
            Netplay.Clients = Enumerable.Range(0, 256).Select(i => new RemoteClient { Id = i, State = 10 }).ToArray();
            ServerTShock.Config = new TShockConfig(); ServerTShock.Config.Settings.SpawnProtection = false;
            ServerTShock.Config.Settings.SuppressPermissionFailureNotices = true;
            ServerTShock.Regions = (RegionManager)RuntimeHelpers.GetUninitializedObject(typeof(RegionManager));
            var region = new Region(1, new Rectangle(20, 20, 1, 1), "m9-paint", "owner", true, Main.worldID.ToString(), 0);
            region.AllowedIDs.Add(140); ServerTShock.Regions.Regions = [region];
            var actor = new TSPlayer(7) { IsLoggedIn = true, Group = new Group("m9-paint", permissions: Permissions.canbuild + "," + Permissions.canpaint), Account = new UserAccount { ID = 140, Name = "m9-paint" } };
            var session = new SessionSnapshot(new(Guid.NewGuid(), 1, 7, 1), 140, false, DateTimeOffset.UtcNow);
            var clock = new TestClock();
            using var guard = new M9PaintRecovery(clock, _ => (s?.Session ?? session, actor), () =>
            {
                if (s is { NativeEffectEntered: true, PostcheckInvoked: false })
                { s.PostcheckInvoked = true; s.DuringPostcheck?.Invoke(); }
                return true;
            }); guard.Install(); guard.Tick(1);
            s = new() { Actor = actor, Region = region, Session = session, Guard = guard, Clock = clock };
            HookEvents.Terraria.WorldGen.paintEffect += Effects; HookEvents.Terraria.NetMessage.SendData += Sink;
            run(s);
        }
        finally
        {
            HookEvents.Terraria.WorldGen.paintEffect -= Effects; HookEvents.Terraria.NetMessage.SendData -= Sink;
            Main.tile = oldTiles; Main.player = oldPlayers; Netplay.Clients = oldClients;
            ServerTShock.Config = oldConfig; ServerTShock.Regions = oldRegions;
            Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.ActiveWorldFileData.WorldId = oldWorld; Main.dedServ = oldDed;
        }
    }
    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    private sealed class UnknownWorldPlugin() : TerrariaPlugin(null!)
    {
        public override void Initialize() { }
    }
}
