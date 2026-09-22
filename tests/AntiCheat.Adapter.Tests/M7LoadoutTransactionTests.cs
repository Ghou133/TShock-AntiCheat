using System.Reflection;
using MonoMod.RuntimeDetour;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed partial class M7LoadoutTransactionTests
{
    private const int Slot = 12;
    private Player oldPlayer = null!;
    private Player? oldServerPlayer;
    private RemoteClient oldClient = null!;
    private TShockConfig oldConfig = null!;
    private ServerSideConfig oldSscConfig = null!;
    private int oldMode, oldLocal, oldGameMode;
    private TSPlayer actor = null!;
    private readonly List<(int Packet, int Sender, float Loadout)> exports = [];
    private int coreEntries;
    private SessionKey session;
    private M7LoadoutTransactionObserver observer = null!;
    private M8EquipmentExecutionObserver execution = null!;
    private readonly TestClock clock = new();

    [SetUp]
    public void Setup()
    {
        oldPlayer = Main.player[Slot]; oldClient = Netplay.Clients[Slot]; oldConfig = ServerTShock.Config;
        oldSscConfig = ServerTShock.ServerSideCharacterConfig;
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldGameMode = Main.GameMode;
        Main.netMode = 2; Main.myPlayer = 255; Main.GameMode = 0;
        oldServerPlayer = Main.player[255]; Main.player[255] ??= new Player { whoAmI = 255 };
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Netplay.Clients[Slot] = new RemoteClient { State = 10 }; ServerTShock.Config = new TShockConfig();
        ServerTShock.ServerSideCharacterConfig = new ServerSideConfig();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = true,
            Group = new Group("m7-loadout-default"), Account = new UserAccount { ID = 14712, Name = "m7-loadout" },
            PlayerData = new PlayerData(false) };
        actor.TPlayer.armor[0].SetDefaults(ItemID.WoodHelmet);
        actor.TPlayer.Loadouts[1].Armor[0].SetDefaults(ItemID.CopperHelmet);
        actor.TPlayer.Loadouts[1].Armor[3].SetDefaults(ItemID.AvengerEmblem);
        actor.TPlayer.Loadouts[2].Armor[0].SetDefaults(ItemID.IronHelmet);
        actor.TPlayer.armor[13].SetDefaults(ItemID.AvengerEmblem);
        actor.TPlayer.armor[8].SetDefaults(ItemID.AvengerEmblem);
        actor.PlayerData.CopyCharacter(actor);
        exports.Clear(); coreEntries = 0;
        session = new(Guid.NewGuid(), 1, Slot, 1);
        observer = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), clock);
        observer.Install();
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
        execution.Install(); execution.Update();
        HookEvents.Terraria.NetMessage.SendData += Sink;
    }
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { exports.Add((args.msgType, args.number, args.number2)); args.ContinueExecution = false; }

    [TearDown]
    public void Cleanup()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        execution?.Dispose();
        observer?.Dispose();
        Main.player[Slot] = oldPlayer; Netplay.Clients[Slot] = oldClient; ServerTShock.Config = oldConfig;
        ServerTShock.ServerSideCharacterConfig = oldSscConfig;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.GameMode = oldGameMode;
        Main.player[255] = oldServerPlayer!;
    }

    private bool Dispatch(byte[] body, bool guarded = true, ushort staleVisibility = 0)
    {
        var args = M2ContractsTests.Packet((PacketTypes)147, body, Slot);
        if (guarded && M7LoadoutPacketSafety.Read(args, true) is { RejectMalformed: true }) return false;
        observer.ObserveIncoming(session, actor, args);
        execution.ObserveIncoming(session, args);
        // Real target core and native methods in their audited dispatch order; no fake SSC swap.
        coreEntries++;
        var handler = typeof(GetDataHandlers).GetMethod("HandleSyncLoadout", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var stream = new MemoryStream(body, writable: false);
        bool handled = (bool)handler.Invoke(null, [new GetDataHandlerArgs(actor, stream)])!;
        if (handled) return false;
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = 147;
        buffer.readBuffer[3] = (byte)staleVisibility; buffer.readBuffer[4] = (byte)(staleVisibility >> 8);
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out int packet); Assert.That(packet, Is.EqualTo(147));
        return true;
    }

    [Test]
    public void NativeControlDemonstratesShort147SwapsEquipmentAndReadsUnsentBufferTail()
    {
        Assert.That(Dispatch([(byte)Slot, 1], guarded: false, staleVisibility: ushort.MaxValue), Is.True);
        Assert.That(coreEntries, Is.EqualTo(1)); Assert.That(actor.TPlayer.CurrentLoadoutIndex, Is.EqualTo(1));
        Assert.That(actor.TPlayer.armor[0].type, Is.EqualTo(ItemID.CopperHelmet));
        Assert.That(actor.PlayerData.inventory[NetItem.ArmorIndex.Item1].NetId, Is.EqualTo(ItemID.CopperHelmet));
        Assert.That(actor.TPlayer.hideVisibleAccessory.All(x => x), Is.True, "No visibility bytes were in the frame; native reader consumed the existing buffer tail.");
        Assert.That(exports.Single(x => x.Packet == 147).Loadout, Is.EqualTo(1));
        TestContext.Out.WriteLine("Actual target core/native short147 swaps SSC and native armor, then reads UInt16 visibility outside declared frame. The guarded path must reject before both swaps.");
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(5)]
    public void IncompleteOrTrailingFrameCannotStartSscOrNativeEquipmentTransaction(int length)
    {
        var body = new byte[length]; if (length > 0) body[0] = Slot; if (length > 1) body[1] = 1;
        var before = actor.PlayerData.inventory.Select(x => x.ToString()).ToArray();
        Assert.That(Dispatch(body), Is.False); Assert.That(coreEntries, Is.Zero);
        Assert.That(actor.TPlayer.CurrentLoadoutIndex, Is.Zero);
        Assert.That(actor.TPlayer.armor[0].type, Is.EqualTo(ItemID.WoodHelmet));
        Assert.That(actor.PlayerData.inventory.Select(x => x.ToString()), Is.EqualTo(before));
        Assert.That(actor.TPlayer.hideVisibleAccessory.Any(x => x), Is.False); Assert.That(exports, Is.Empty);
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True, "Next complete operation is accepted; malformed frame is not an account sanction.");
    }

    [Test]
    public void CompleteThreePageTransactionsMatchSscNativeEffectsAndActualExportWithoutTickWait()
    {
        foreach (byte loadout in new byte[] { 1, 2, 0 })
        {
            int exportsBefore = exports.Count;
            Assert.That(Dispatch([Slot, loadout, 1, 0]), Is.True);
            Assert.That(actor.TPlayer.CurrentLoadoutIndex, Is.EqualTo(loadout));
            Assert.That(actor.PlayerData.inventory[NetItem.ArmorIndex.Item1].NetId, Is.EqualTo(actor.TPlayer.armor[0].type));
            Assert.That(exports.Skip(exportsBefore).Single(x => x.Packet == 147), Is.EqualTo((147, Slot, (float)loadout)));
            var completion = observer.Capture(session);
            Assert.That(completion, Is.Not.Null);
            Assert.That(completion!.ServerPermutationObserved && completion.SscPermutationObserved && completion.VisibilityObserved, Is.True);
            Assert.That(completion.AttributionComplete, Is.True);
            Assert.That(completion.AppliedLoadout, Is.EqualTo(loadout));
            Assert.That(completion.ClientEquipmentTransitionComplete, Is.False);
            Assert.That(completion.RuntimeFingerprint, Is.EqualTo(TargetRuntime.Fingerprint));
            int defense = actor.TPlayer.statDefense; float damage = actor.TPlayer.meleeDamage;
            actor.TPlayer.UpdateEquips(Slot);
            var applied = execution.Capture(session);
            Assert.That(applied, Is.Not.Null);
            Assert.That(applied!.NativeCalculationReturned && applied.ProjectionMatchesServerCommit, Is.True);
            Assert.That(applied.LoadoutRevision, Is.EqualTo(completion.Revision));
            Assert.That(applied.DefenseDelta, Is.EqualTo(actor.TPlayer.armor[0].defense));
            Assert.That(applied.MeleeDamageDelta, Is.EqualTo(loadout == 1 ? 0.12f : 0f).Within(0.0001));
            Assert.That(applied.ClientMultiSlotCompletionObserved || applied.ItemAcquisitionProven, Is.False);
            Assert.That(actor.TPlayer.statDefense - defense, Is.EqualTo(actor.TPlayer.armor[0].defense));
            Assert.That(actor.TPlayer.meleeDamage - damage, Is.EqualTo(loadout == 1 ? 0.12f : 0f).Within(0.0001));
            var snapshot = M5EquipmentContexts.Capture(new(Guid.NewGuid(), 1, Slot, 1), actor.TPlayer);
            Assert.That(snapshot.AtomicClientTransitionComplete, Is.False, "A server loadout transaction does not certify a different multi-slot equipment edit finished.");
            Assert.That(actor.TPlayer.hideVisibleAccessory[0], Is.True);
        }
    }

    [Test]
    public void UnverifiedRuntimeAndOtherPacketKindsAreNotReinterpretedAsLoadoutTransactions()
    {
        Assert.That(M7LoadoutPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)147, [Slot, 1], Slot), false), Is.Null);
        Assert.That(M7LoadoutPacketSafety.Read(M2ContractsTests.Packet((PacketTypes)5, [Slot, 1], Slot), true), Is.Null);
    }

    [Test]
    public void AForwarded147AloneDoesNotProveEquipmentOrSscCommit()
    {
        observer.ObserveIncoming(session, actor, M2ContractsTests.Packet((PacketTypes)147, [Slot, 1, 0, 0], Slot));
        NetMessage.SendData(147, -1, Slot, number: Slot, number2: 1);
        var completion = observer.Capture(session);
        Assert.That(completion, Is.Not.Null);
        Assert.That(completion!.ServerPermutationObserved, Is.False);
        Assert.That(completion.SscPermutationObserved, Is.False);
        Assert.That(completion.AttributionComplete, Is.False);
        Assert.That(observer.Completed, Is.Zero); Assert.That(observer.Mismatched, Is.EqualTo(1));
        Assert.That(actor.TPlayer.CurrentLoadoutIndex, Is.Zero);
    }

    [Test]
    public void SlotReuseAndExpiredCorrelationCannotInheritTheOldTransaction()
    {
        observer.ObserveIncoming(session, actor, M2ContractsTests.Packet((PacketTypes)147, [Slot, 1, 0, 0], Slot));
        var old = session; session = session with { Generation = 2 };
        NetMessage.SendData(147, -1, Slot, number: Slot, number2: 1);
        Assert.That(observer.Capture(old), Is.Null); Assert.That(observer.Capture(session), Is.Null);
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True); Assert.That(observer.Capture(session), Is.Not.Null);
        clock.Advance(TimeSpan.FromSeconds(11)); observer.Update();
        Assert.That(observer.Capture(session), Is.Null);
    }

    [Test]
    public void M8NativeEffectsRequireActualCalculationAndExpireWithTheServerTransaction()
    {
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True);
        Assert.That(execution.Capture(session), Is.Null, "Forwarding does not run UpdateEquips.");
        actor.TPlayer.UpdateEquips(Slot);
        Assert.That(execution.Capture(session)?.NativeCalculationReturned, Is.True);
        long count = execution.Observed;
        actor.TPlayer.UpdateEquips(Slot);
        Assert.That(execution.Observed, Is.EqualTo(count), "At most one immutable sample for an unchanged transaction.");
        clock.Advance(TimeSpan.FromSeconds(11)); observer.Update();
        Assert.That(execution.Capture(session), Is.Null, "TTL invalidates evidence, never completes a client edit.");
    }

    [Test]
    public void M8IntermediateDuplicateIsObservedWithoutCertifyingClientCompletionOrPunishingStorage()
    {
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True);
        actor.TPlayer.armor[4].SetDefaults(ItemID.AvengerEmblem);
        execution.ObserveIncoming(session, M2ContractsTests.Packet((PacketTypes)5, [], Slot));
        float before = actor.TPlayer.meleeDamage;
        actor.TPlayer.UpdateEquips(Slot);
        var applied = execution.Capture(session);
        Assert.That(applied, Is.Not.Null);
        Assert.That(applied!.ProjectionMatchesServerCommit, Is.False);
        Assert.That(applied.ClientMultiSlotCompletionObserved || applied.ItemAcquisitionProven, Is.False);
        Assert.That(execution.AvengerEffectGuardHealthy, Is.True, execution.AvengerEffectFailureType);
        Assert.That(actor.TPlayer.meleeDamage - before, Is.EqualTo(0.12f).Within(0.0001),
            "M11 preserves a legal move's two stored slots but applies the type935 functional bonus once.");
        Assert.That(execution.AvengerEffectBlocks, Is.EqualTo(1));
        Assert.That(actor.TPlayer.armor[4].type, Is.EqualTo(ItemID.AvengerEmblem));
        // Out-of-band host mutation also invalidates the old sample without a client packet.
        actor.TPlayer.armor[4].TurnToAir();
        Assert.That(execution.Capture(session), Is.Null);
    }

    [Test]
    public void M8EffectObservationDoesNotCrossSessionOrUnverifiedExecutionThread()
    {
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True);
        actor.TPlayer.UpdateEquips(Slot);
        Assert.That(execution.Capture(session), Is.Not.Null);
        var old = session; session = session with { Generation = session.Generation + 1 };
        Assert.That(execution.Capture(old), Is.Null); Assert.That(execution.Capture(session), Is.Null);
        Assert.That(Dispatch([Slot, 2, 0, 0]), Is.True);
        // Calling the real native method on another thread is a host action, not client cheating.
        var thread = new Thread(() => actor.TPlayer.UpdateEquips(Slot)); thread.Start(); thread.Join();
        Assert.That(execution.Capture(session), Is.Null);
        execution.Update(); actor.TPlayer.UpdateEquips(Slot);
        Assert.That(execution.Capture(session), Is.Not.Null);
    }

    [Test]
    public void M8EarlierDetourReturningDoesNotForgeNativeBodyExecution()
    {
        // Reinstall outer observation after the inner cancelling hook so the exact call chain is exercised.
        execution.Dispose();
        var method = typeof(Player).GetMethod(nameof(Player.UpdateEquips), [typeof(int)])!;
        using var cancel = new Hook(method, (Action<Action<Player, int>, Player, int>)((original, player, slot) => { }));
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
        execution.Install(); execution.Update();
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True);
        int before = actor.TPlayer.statDefense;
        actor.TPlayer.UpdateEquips(Slot);
        var result = execution.Capture(session);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.NativeCallChainReturned, Is.True);
        Assert.That(result.NativeCalculationReturned, Is.False);
        Assert.That(actor.TPlayer.statDefense, Is.EqualTo(before));
        execution.Dispose();
    }

    [Test]
    public void M8SameShapeHostMutationAndSharedItemReplacementInvalidateRecordedProjection()
    {
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True);
        actor.TPlayer.UpdateEquips(Slot);
        Assert.That(execution.Capture(session), Is.Not.Null);
        actor.TPlayer.armor[0].defense++;
        Assert.That(execution.Capture(session), Is.Null);
        actor.TPlayer.armor[0].defense--;
        // Favorited armor is shared from an inactive page into an empty active slot.
        actor.TPlayer.armor[0].TurnToAir();
        actor.TPlayer.Loadouts[2].Armor[0].favorited = true;
        execution.ObserveIncoming(session, M2ContractsTests.Packet((PacketTypes)5, [], Slot));
        actor.TPlayer.UpdateEquips(Slot);
        Assert.That(execution.Capture(session)?.EffectiveInputs.Slots[0].SharedFromOtherLoadout, Is.True);
        actor.TPlayer.Loadouts[2].Armor[0] = actor.TPlayer.Loadouts[2].Armor[0].Clone();
        Assert.That(execution.Capture(session), Is.Null, "Same net fields do not retain object identity.");
    }

    [Test]
    public void M11AvengerEffectUsesEachNativeCalculationWithout147AndPreservesPrefixesAndMoveCompletion()
    {
        Assert.That(execution.AvengerEffectGuardHealthy, Is.True, execution.AvengerEffectFailureType);
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[3].Prefix(PrefixID.Warding);
        player.armor[4].SetDefaults(ItemID.AvengerEmblem); player.armor[4].Prefix(PrefixID.Menacing);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            float before = player.meleeDamage; int defense = player.statDefense;
            player.UpdateEquips(Slot);
            Assert.That(player.meleeDamage - before, Is.EqualTo(.16f).Within(.0001), "One functional .12 and the separate native Menacing .04.");
            Assert.That(player.statDefense - defense, Is.EqualTo(player.armor[0].defense + 4));
            Assert.That(player.armor[3].type, Is.EqualTo(ItemID.AvengerEmblem));
            Assert.That(player.armor[4].type, Is.EqualTo(ItemID.AvengerEmblem));
            Assert.That(execution.Capture(session), Is.Null, "Filtering does not forge a 147 transaction or item history.");
        }
        Assert.That(execution.AvengerEffectBlocks, Is.EqualTo(2), "No per-session carry-over count controls the effect.");
        player.armor[3].TurnToAir(); // The final legitimate source-slot removal is accepted unchanged.
        float final = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - final, Is.EqualTo(.16f).Within(.0001));
        Assert.That(execution.AvengerEffectBlocks, Is.EqualTo(2));
    }

    [Test]
    public void M11DistinctEmblemsLockedStorageAndDirectHostCallsRetainNativeEffects()
    {
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[4].SetDefaults(ItemID.WarriorEmblem);
        player.armor[8].SetDefaults(ItemID.AvengerEmblem);
        float before = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.27f).Within(.0001));
        Assert.That(execution.AvengerEffectBlocks, Is.Zero);
        // A direct host call has no native UpdateEquips scope and cannot inherit its prior item.
        before = player.meleeDamage;
        player.ApplyEquipFunctional(3, player.armor[3]); player.ApplyEquipFunctional(3, player.armor[3]);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.24f).Within(.0001));
        Assert.That(execution.AvengerEffectBlocks, Is.Zero);
    }

    [Test]
    public void M11WholeNativeControlAndRejectedOrThrowingCallNeverLeaveAnEffectReservation()
    {
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[4].SetDefaults(ItemID.AvengerEmblem);
        execution.Dispose();
        float before = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.24f).Within(.0001), "Original entire UpdateEquips duplicate control is retained.");
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
        execution.Install(); execution.Update();
        var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])!;
        using (var fault = new Hook(method, (Action<Action<Player, int, Item>, Player, int, Item>)((original, p, s, i) =>
        { if (s == 3) throw new InvalidOperationException("owned equipment fault"); original(p, s, i); })))
            Assert.Throws<InvalidOperationException>(() => player.UpdateEquips(Slot));
        before = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.12f).Within(.0001));
        Assert.That(execution.AvengerEffectBlocks, Is.EqualTo(1));
        execution.Dispose();
        var update = typeof(Player).GetMethod(nameof(Player.UpdateEquips), [typeof(int)])!;
        using (var cancel = new Hook(update, (Action<Action<Player, int>, Player, int>)((original, p, i) => { })))
        {
            execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
            execution.Install(); execution.Update();
            before = player.meleeDamage; player.UpdateEquips(Slot);
            Assert.That(player.meleeDamage, Is.EqualTo(before));
            Assert.That(execution.AvengerEffectBlocks, Is.Zero, "No native body, no alleged extra effect.");
            execution.Dispose();
        }
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
        execution.Install(); execution.Update();
        before = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.12f).Within(.0001));
    }

    [Test]
    public void M11UnknownHostOtherPlayerAndOffThreadCallsDoNotBecomeEquipmentProofs()
    {
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[4].SetDefaults(ItemID.AvengerEmblem);
        float before = player.meleeDamage;
        var thread = new Thread(() => player.UpdateEquips(Slot)); thread.Start(); thread.Join();
        Assert.That(player.meleeDamage - before, Is.EqualTo(.24f).Within(.0001));
        var other = new Player { whoAmI = Slot + 1 };
        other.armor[3].SetDefaults(ItemID.AvengerEmblem); other.armor[4].SetDefaults(ItemID.AvengerEmblem);
        before = other.meleeDamage; other.UpdateEquips(Slot + 1);
        Assert.That(other.meleeDamage - before, Is.EqualTo(.24f).Within(.0001));
        var plugins = (List<TerrariaApi.Server.PluginContainer>)typeof(TerrariaApi.Server.ServerApi)
            .GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var host = new M11UnsupportedEquipmentHost(); var container = new TerrariaApi.Server.PluginContainer(host);
        plugins.Add(container);
        try
        {
            before = player.meleeDamage; player.UpdateEquips(Slot);
            Assert.That(player.meleeDamage - before, Is.EqualTo(.24f).Within(.0001));
            Assert.That(execution.AvengerEffectBlocks, Is.Zero);
        }
        finally { plugins.Remove(container); }
        session = session with { Generation = session.Generation + 1 };
        execution.Update(); before = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.12f).Within(.0001));
    }
    private sealed class M11UnsupportedEquipmentHost() : TerrariaApi.Server.TerrariaPlugin(null!)
    { public override void Initialize() { } }

    [Test]
    public void M11NestedNativeCalculationsHaveIndependentScopesAndContextFaultWithdrawsOnlyEffectGuard()
    {
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(ItemID.AvengerEmblem); player.armor[4].SetDefaults(ItemID.AvengerEmblem);
        var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])!;
        bool nested = false;
        float before = player.meleeDamage;
        using (var reenter = new Hook(method, (Action<Action<Player, int, Item>, Player, int, Item>)((original, p, s, i) =>
        {
            if (!nested && s == 3) { nested = true; p.UpdateEquips(Slot); }
            original(p, s, i);
        }))) player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - before, Is.EqualTo(.24f).Within(.0001), "Two complete native calculations each retain exactly one bonus.");
        Assert.That(execution.AvengerEffectBlocks, Is.EqualTo(2));
        execution.Dispose(); int lookups = 0;
        execution = new(TargetRuntime.Fingerprint, slot =>
        {
            if (++lookups == 2) throw new InvalidOperationException("owned lookup failure at functional effect boundary");
            return slot == Slot ? (session, actor, true) : (null, null, false);
        }, observer);
        execution.Install(); execution.Update();
        before = player.meleeDamage; Assert.DoesNotThrow(() => player.UpdateEquips(Slot));
        Assert.That(player.meleeDamage - before, Is.EqualTo(.24f).Within(.0001));
        Assert.That(execution.Healthy, Is.True, "The independent M8 calculation observation remains installed.");
        Assert.That(execution.AvengerEffectGuardHealthy, Is.False);
        Assert.That(execution.AvengerEffectFailureReason, Does.Contain("owned lookup failure"));
    }

    [Test]
    public void M11ShapeEvidenceIsBoundedAcrossItemAndContainerTransitionsAndNeverControlsEffects()
    {
        var observations = new List<M11EquipmentEffectObservation>(); execution.EffectObservation = observations.Add;
        var player = actor.TPlayer; player.armor[3].SetDefaults(ItemID.AvengerEmblem);
        for (int index = 0; index < 50; index++)
        {
            if ((index & 1) == 0) player.armor[4].SetDefaults(ItemID.AvengerEmblem); else player.armor[4].TurnToAir();
            execution.ObserveIncoming(session, M2ContractsTests.Packet((PacketTypes)5, [], Slot));
            execution.Forget(session, endSession: false);
            player.UpdateEquips(Slot);
        }
        Assert.That(observations.Count, Is.EqualTo(32));
        Assert.That(observations.All(value => value.NativeBodyReturned && value.InputsStable && value.GuardHealthy), Is.True);
        Assert.That(observations[0].AvengerBonusesBlocked, Is.EqualTo(1));
        Assert.That(observations[1].AvengerBonusesBlocked, Is.Zero);
        Assert.That(execution.AvengerEffectBlocks, Is.EqualTo(25), "Logging capacity cannot turn off resource filtering.");
        var old = session; execution.Forget(old); session = session with { Generation = session.Generation + 1 };
        player.UpdateEquips(Slot);
        Assert.That(observations.Count, Is.EqualTo(33));
        Assert.That(observations[^1].Session, Is.EqualTo(session)); Assert.That(observations[^1].Sequence, Is.EqualTo(1));
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
}
