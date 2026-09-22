using System.Buffers.Binary;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M8MovementObservationTests
{
    private const int Slot = 18;
    private Player oldPlayer = null!;
    private RemoteClient oldClient = null!;
    private int oldMode, oldLocal, oldWorld;
    private TSPlayer actor = null!;
    private SessionKey session;
    private bool revoked;
    private TestClock clock = null!;
    private M8MovementObservations observer = null!;
    private readonly List<int> exports = [];

    [SetUp]
    public void Setup()
    {
        oldPlayer = Main.player[Slot]; oldClient = Netplay.Clients[Slot];
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWorld = Main.worldID;
        Main.netMode = 2; Main.myPlayer = 255;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true, position = new(32, 48), velocity = new(1, 2) };
        Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = true,
            Account = new UserAccount { ID = 118, Name = "m8-movement" } };
        session = new(Guid.NewGuid(), 6, Slot, 1); revoked = false;
        clock = new(); observer = new(clock, TargetRuntime.Fingerprint);
        observer.Install(Lookup); observer.Tick(session.WorldEpoch);
        exports.Clear(); HookEvents.Terraria.NetMessage.SendData += Sink;
        HookEvents.Terraria.NetMessage.SendPlayerHurt += HurtSink;
    }

    [TearDown]
    public void Cleanup()
    {
        observer.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        HookEvents.Terraria.NetMessage.SendPlayerHurt -= HurtSink;
        Main.player[Slot] = oldPlayer; Netplay.Clients[Slot] = oldClient;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.ActiveWorldFileData.WorldId = oldWorld;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, actor.Account.ID, revoked, clock.Now), actor) : (null, null);
    private void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { exports.Add(args.msgType); args.ContinueExecution = false; }
    private void HurtSink(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args) => args.ContinueExecution = false;

    private static M2Packet Packet(float x = 100, float y = 200, Vector2? velocity = null, ushort? mount = null,
        bool gravityDown = true, bool returnPositions = false, bool camera = false)
    {
        byte[] body = new byte[14 + (velocity.HasValue ? 8 : 0) + (mount.HasValue ? 2 : 0) +
            (returnPositions ? 16 : 0) + (camera ? 8 : 0)];
        body[0] = Slot; body[2] = (byte)((velocity.HasValue ? 4 : 0) | (mount.HasValue ? 128 : 0) | (gravityDown ? 16 : 0));
        body[3] = returnPositions ? (byte)64 : (byte)0; body[4] = camera ? (byte)32 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(6), x); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(10), y);
        int offset = 14;
        if (velocity is { } v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(offset), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(offset + 4), v.Y); offset += 8;
        }
        if (mount.HasValue) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset), mount.Value);
        var parsed = M2PacketReader.Read(M2ContractsTests.Packet(PacketTypes.PlayerUpdate, body, Slot), true);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed)); return parsed.Packet!;
    }
    private void Observe(M2Packet? packet = null, bool cancelled = false) => observer.Observe(packet ?? Packet(), session, actor, cancelled);
    private M8MovementSessionSnapshot Snapshot() => observer.Capture(session)!;

    private static void ConsumeNative(M2Packet packet)
    {
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = 13; packet.Payload.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, packet.Payload.Length + 1, out int type); Assert.That(type, Is.EqualTo(13));
    }

    [Test]
    public void ActualNativePacket13ConsumptionKeepsDeclarationsSeparateFromPriorAcceptedState()
    {
        var packet = Packet(100, 200, new(3, 4));
        Observe(packet); ConsumeNative(packet);
        var sample = Snapshot().Samples.Single();
        Assert.That(sample.ClientPositionX, Is.EqualTo(100));
        Assert.That(sample.RuntimeBeforeCandidate.PositionX, Is.EqualTo(32));
        Assert.That(sample.RuntimeBeforeCandidate.VelocityX, Is.EqualTo(1));
        Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(100, 200)));
        Assert.That(actor.TPlayer.velocity, Is.EqualTo(new Vector2(3, 4)));
        Assert.That(exports, Does.Contain(13)); Assert.That(Snapshot().CanSupplyHardProof, Is.False);
        Assert.That(sample.RuntimeBeforeCandidate.Source, Does.Contain("not-legality"));
    }

    [Test]
    public void NativeUnacknowledgedTeleportCanIgnoreADeclaredPositionWithoutAnyMovementAccusation()
    {
        actor.TPlayer.unacknowledgedTeleports = 1;
        var packet = Packet(10000, 20000, new(100, 200));
        Observe(packet); ConsumeNative(packet);
        Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(32, 48)));
        Assert.That(actor.TPlayer.velocity, Is.EqualTo(new Vector2(1, 2)));
        var sample = Snapshot().Samples.Single();
        Assert.That(sample.ClientPositionX, Is.EqualTo(10000));
        Assert.That(sample.RuntimeBeforeCandidate.UnacknowledgedTeleports, Is.EqualTo(1));
        Assert.That(Snapshot().CanSupplyHardProof, Is.False);
    }

    [Test]
    public void EveryOptionalLayoutUsesExistingParserAndPreservesClientAndRuntimeMountGravityContexts()
    {
        actor.TPlayer.mount._active = true; actor.TPlayer.mount._type = 3;
        actor.TPlayer.gravDir = -1; actor.TPlayer.grapCount = 2; actor.TPlayer.noKnockback = true;
        actor.TPlayer.immune = true; actor.TPlayer.immuneTime = 8;
        for (int mask = 0; mask < 16; mask++)
        {
            clock.Advance(M8MovementObservations.SampleSpacing);
            Observe(Packet(100, 200, (mask & 1) != 0 ? new Vector2(7, 8) : null,
                (mask & 2) != 0 ? (ushort)33 : null, false, (mask & 4) != 0, (mask & 8) != 0));
            var sample = Snapshot().Samples.Last();
            Assert.That(sample.ClientVelocityPresent, Is.EqualTo((mask & 1) != 0));
            Assert.That(sample.ClientMountType, Is.EqualTo((mask & 2) != 0 ? (int?)33 : null));
            Assert.That(sample.ClientGravityDirection, Is.EqualTo(-1));
            Assert.That(sample.RuntimeBeforeCandidate.MountActive, Is.True);
            Assert.That(sample.RuntimeBeforeCandidate.MountType, Is.EqualTo(3));
            Assert.That(sample.RuntimeBeforeCandidate.GrapplingCount, Is.EqualTo(2));
            Assert.That(sample.RuntimeBeforeCandidate.NoKnockback, Is.True);
            Assert.That(sample.RuntimeBeforeCandidate.ImmuneTime, Is.EqualTo(8));
        }
        Assert.That(actor.TPlayer.mount._type, Is.EqualTo(3)); Assert.That(exports, Is.Empty);
        Assert.That(Snapshot().Samples, Has.Length.EqualTo(16));
    }

    [Test]
    public void ReceiptRateIsLabelledAsAClientDeclarationAndNeverUsesASpeedThresholdOrScore()
    {
        Observe(Packet(0, 0)); clock.Advance(TimeSpan.FromMilliseconds(250));
        Observe(Packet(300, 400), cancelled: true);
        var sample = Snapshot().Samples.Last();
        Assert.That(sample.ReceiptIntervalMilliseconds, Is.EqualTo(250));
        Assert.That(sample.ClientDisplacementPixels, Is.EqualTo(500));
        Assert.That(sample.ApparentPixelsPerReceiptSecond, Is.EqualTo(2000));
        Assert.That(sample.CancelledAtObservation, Is.True); Assert.That(sample.PreviousCancelledAtObservation, Is.False);
        for (int i = 0; i < 50; i++) { clock.Advance(TimeSpan.FromMilliseconds(100)); Observe(Packet(i * 1e30f, 0)); }
        Assert.That(Snapshot().CanSupplyHardProof, Is.False); Assert.That(actor.IsLoggedIn, Is.True);
        Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(32, 48))); Assert.That(exports, Is.Empty);
    }

    [Test]
    public void ServerTeleportAndHurtSendAttemptsAreContextWithoutDeliveryOrAuthorizationClaims()
    {
        NetMessage.SendData(65, -1, -1, null, 0, Slot, 4000, 5000, 4);
        NetMessage.SendPlayerHurt(Slot, PlayerDeathReason.ByOther(0), 20, -1, false, false, 0);
        Observe();
        var sample = Snapshot().Samples.Single();
        Assert.That(sample.RecentTeleportAttempt!.AddressedToSubject, Is.True);
        Assert.That(sample.RecentTeleportAttempt.Source, Does.Contain("not-consumption-or-authorization"));
        Assert.That(sample.RecentTeleportAttempt.DestinationX, Is.EqualTo(4000));
        Assert.That(sample.RecentHurtAttempt!.HurtDamageArgument, Is.EqualTo(20));
        Assert.That(sample.RecentHurtAttempt.Source, Does.Contain("not-applied-effect"));
        Assert.That(actor.TPlayer.unacknowledgedTeleports, Is.Zero,
            "The sink cancelled before native serialization; observing the send attempt cannot invent its effect.");
        clock.Advance(M8MovementObservations.NoticeRetention + TimeSpan.FromTicks(1)); Observe();
        Assert.That(Snapshot().Samples.Last().RecentTeleportAttempt, Is.Null);
        Assert.That(Snapshot().Samples.Last().RecentHurtAttempt, Is.Null);
    }

    [Test]
    public void PeerOnlyRelayAndPortalExportRemainDistinctFromSelfDirectedTeleport()
    {
        NetMessage.SendData(65, -1, Slot, null, 0, Slot, 3000, 4000, 1, 1);
        Observe();
        var first = Snapshot().Samples.Last().RecentTeleportAttempt!;
        Assert.That(first.AddressedToSubject, Is.False); Assert.That(first.ExplicitDestination, Is.False);
        Assert.That(first.Style, Is.EqualTo(1)); Assert.That(first.PortalIndex, Is.Null);
        clock.Advance(M8MovementObservations.SampleSpacing);
        NetMessage.SendData(96, -1, Slot, null, Slot, 6000, 7000, 2); Observe();
        var second = Snapshot().Samples.Last().RecentTeleportAttempt!;
        Assert.That(second.Source, Does.Contain("96")); Assert.That(second.AddressedToSubject, Is.False);
        Assert.That(second.Style, Is.EqualTo(4)); Assert.That(second.PortalIndex, Is.EqualTo(2));
        Assert.That(second.DestinationX, Is.EqualTo(6000)); Assert.That(Snapshot().CanSupplyHardProof, Is.False);
    }

    [TestCase(2)]
    [TestCase(7)]
    [TestCase(-2)]
    public void PortalSendArgumentIsAnIndependentSignedIndexWithFixedNativeTeleportStyle(int portalIndex)
    {
        NetMessage.SendData(96, Slot, -1, null, Slot, 6000, 7000, portalIndex); Observe();
        var notice = Snapshot().Samples.Single().RecentTeleportAttempt!;
        Assert.That(notice.Style, Is.EqualTo(4)); Assert.That(notice.PortalIndex, Is.EqualTo(portalIndex));
        Assert.That(notice.AddressedToSubject, Is.True);
        Assert.That(notice.Source, Does.Contain("not-consumption-or-authorization"));
        Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(32, 48)));
        Assert.That(Snapshot().CanSupplyHardProof, Is.False);
    }

    [TestCase(Slot, Slot, true)]
    [TestCase(-1, Slot, false)]
    [TestCase(-1, -1, true)]
    [TestCase(Slot + 1, Slot, false)]
    public void ExplicitRecipientTakesPrecedenceAndIgnoreOnlyAppliesToBroadcast(int remote, int ignored, bool addressed)
    {
        NetMessage.SendData(65, remote, ignored, null, 0, Slot, 3000, 4000, 1);
        NetMessage.SendPlayerHurt(Slot, PlayerDeathReason.ByOther(0), 20, -1, false, false, 0,
            remoteClient: remote, ignoreClient: ignored);
        Observe(); var sample = Snapshot().Samples.Single();
        Assert.That(sample.RecentTeleportAttempt!.AddressedToSubject, Is.EqualTo(addressed));
        Assert.That(sample.RecentHurtAttempt!.AddressedToSubject, Is.EqualTo(addressed));
        Assert.That(sample.RecentTeleportAttempt.RemoteClient, Is.EqualTo(remote));
        Assert.That(sample.RecentTeleportAttempt.IgnoreClient, Is.EqualTo(ignored));
        Assert.That(actor.TPlayer.unacknowledgedTeleports, Is.Zero, "An intended recipient remains distinct from actual native execution or delivery.");
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void RuntimeOrAccountReplacementInvalidatesBeforeAnotherIncomingSample(bool replaceAccount, bool useTick)
    {
        Observe(); NetMessage.SendData(65, -1, -1, null, 0, Slot, 30, 40);
        var previousPlayer = Main.player[Slot]; int previousAccount = actor.Account.ID;
        if (replaceAccount) actor.Account.ID++;
        else Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        if (useTick) observer.Tick(session.WorldEpoch);
        else Assert.That(observer.Capture(session), Is.Null,
            "Capture must reject stored identity/runtime witnesses immediately, without waiting for another13.");
        // Both paths discard the stale state, rather than merely hiding it until an old object/ID is restored.
        actor.Account.ID = previousAccount; Main.player[Slot] = previousPlayer;
        Assert.That(observer.Capture(session), Is.Null);
        Observe(); var snapshot = Snapshot();
        Assert.That(snapshot.Received, Is.EqualTo(1));
        Assert.That(snapshot.Samples.Single().ReceiptIntervalMilliseconds, Is.Null);
        Assert.That(snapshot.Samples.Single().RecentTeleportAttempt, Is.Null);
        Assert.That(snapshot.CanSupplyHardProof, Is.False);
    }

    [Test]
    public void ReusedSlotWorldReplacementAndRuntimePlayerReplacementCannotInheritSamplesOrNotices()
    {
        Observe(); NetMessage.SendData(65, -1, -1, null, 0, Slot, 30, 40);
        var oldSession = session; session = session with { Generation = session.Generation + 1 };
        Observe(); Assert.That(observer.Capture(oldSession), Is.Null); Assert.That(Snapshot().Samples, Has.Length.EqualTo(1));
        Assert.That(Snapshot().Samples.Single().ReceiptIntervalMilliseconds, Is.Null);
        Assert.That(Snapshot().Samples.Single().RecentTeleportAttempt, Is.Null);
        Main.ActiveWorldFileData.WorldId++; Assert.That(observer.Capture(session), Is.Null);
        session = session with { WorldEpoch = session.WorldEpoch + 1 }; observer.Tick(session.WorldEpoch); Observe();
        Assert.That(Snapshot().Samples, Has.Length.EqualTo(1));
        Main.player[Slot] = new Player { whoAmI = Slot, active = true }; Observe();
        Assert.That(Snapshot().Received, Is.EqualTo(1)); Assert.That(Snapshot().Samples.Single().ReceiptIntervalMilliseconds, Is.Null);
    }

    [Test]
    public void InvalidBindingsMalformedDirectModelsAndWrongThreadNeverObserveAnotherAccount()
    {
        revoked = true; Observe(); Assert.That(observer.Capture(session), Is.Null);
        revoked = false;
        var packet = Packet(); packet.Payload[0] = Slot + 1; Observe(packet);
        observer.Observe(new(M2PacketKind.PlayerUpdate, new byte[13]), session, actor, false);
        Task.Run(() => Observe()).GetAwaiter().GetResult();
        Assert.That(observer.TotalReceived, Is.Zero); Assert.That(observer.InvalidBindingNotObserved, Is.EqualTo(2));
        Assert.That(observer.MalformedNotObserved, Is.EqualTo(1)); Assert.That(observer.WrongContextNotObserved, Is.EqualTo(1));
        Assert.That(observer.Healthy, Is.True);
    }

    [Test]
    public void SamplingAndRetentionAreBoundedAndUseMonotonicTime()
    {
        for (int i = 0; i < 100; i++) Observe(Packet(i, 0));
        Assert.That(Snapshot().Received, Is.EqualTo(100)); Assert.That(Snapshot().SamplesWritten, Is.EqualTo(1));
        Assert.That(Snapshot().SampledOut, Is.EqualTo(99));
        for (int i = 0; i < 35; i++) { clock.Advance(M8MovementObservations.SampleSpacing); Observe(Packet(i, 0)); }
        Assert.That(Snapshot().Samples, Has.Length.EqualTo(M8MovementObservations.SamplesPerSession));
        Assert.That(Snapshot().OverwrittenUnexpired, Is.EqualTo(4));
        clock.Now -= TimeSpan.FromDays(2);
        Assert.That(Snapshot().Samples, Has.Length.EqualTo(M8MovementObservations.SamplesPerSession));
        clock.Timestamp += M8MovementObservations.Retention.Ticks + 1;
        Assert.That(Snapshot().Samples, Is.Empty);
        Observe(); Assert.That(Snapshot().Samples.Single().ReceiptIntervalMilliseconds, Is.Null);
    }

    [Test]
    public void NonFiniteDeclarationsRemainSerializableDiagnosticsAndNeverAStandalonePenalty()
    {
        Observe(Packet(float.NaN, float.PositiveInfinity, new(float.NaN, 0)));
        var sample = Snapshot().Samples.Single();
        Assert.That(sample.ClientValuesFinite, Is.False); Assert.That(sample.ClientPositionX, Is.Null);
        Assert.That(sample.ClientVelocityX, Is.Null); Assert.That(sample.ApparentPixelsPerReceiptSecond, Is.Null);
        Assert.DoesNotThrow(() => JsonSerializer.Serialize(Snapshot()));
        Assert.That(Snapshot().NonFiniteDeclarations, Is.EqualTo(1)); Assert.That(Snapshot().CanSupplyHardProof, Is.False);
    }

    [Test]
    public void ObserverFaultDoesNotMutatePacketPlayerOrPropagateThroughTheRawEntry()
    {
        var packet = Packet(); var before = packet.Payload.ToArray(); clock.Throw = true;
        int faults = 0; observer.IntegrityFault = _ => { faults++; throw new IOException("reporter failed"); };
        Assert.DoesNotThrow(() => Observe(packet));
        Assert.That(packet.Payload, Is.EqualTo(before)); Assert.That(observer.Healthy, Is.False);
        Assert.That(observer.FaultObserverFailed, Is.True); Assert.That(faults, Is.EqualTo(1));
        Assert.That(actor.TPlayer.position, Is.EqualTo(new Vector2(32, 48)));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
        public long Timestamp;
        public bool Throw;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Throw ? throw new IOException("observer clock failed") : Timestamp;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan value) { Timestamp += value.Ticks; Now += value; }
    }
}
