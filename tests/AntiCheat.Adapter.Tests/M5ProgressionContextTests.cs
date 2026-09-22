using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M5ProgressionContextTests
{
    private const int Slot = 14;
    private int oldMode, oldLocal, oldWidth, oldWorld;
    private bool oldZenith, oldRemix, oldWorthy;
    private NPC[] oldNpcs = null!;
    private Player oldPlayer = null!;
    private SessionKey session;
    private TSPlayer actor = null!;
    private M5ProgressionContexts contexts = null!;
    private readonly List<(int Id, int Player, float Type)> sends = [];

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX; oldWorld = Main.worldID;
        oldZenith = Main.zenithWorld; oldRemix = Main.remixWorld; oldWorthy = Main.getGoodWorld;
        oldNpcs = Main.npc; oldPlayer = Main.player[Slot];
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 500;
        Main.zenithWorld = Main.remixWorld = Main.getGoodWorld = false;
        Main.npc = Enumerable.Range(0, Main.maxNPCs + 1).Select(_ => new NPC()).ToArray();
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m5-progression-default"), Account = new UserAccount { ID = 140, Name = "m5-progress" } };
        session = new(Guid.NewGuid(), 5, Slot, 1); contexts = new(TargetRuntime.Fingerprint);
        contexts.Install();
        contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true;
        sends.Clear(); HookEvents.Terraria.NetMessage.SendData += Sink;
    }
    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, actor.Account.ID, false, DateTimeOffset.UtcNow), actor) : (null, null);
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add((args.msgType, args.number, args.number2)); args.ContinueExecution = false; }
    [TearDown]
    public void Cleanup()
    {
        contexts.Dispose();
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth; Main.ActiveWorldFileData.WorldId = oldWorld;
        Main.zenithWorld = oldZenith; Main.remixWorld = oldRemix; Main.getGoodWorld = oldWorthy;
        Main.npc = oldNpcs; Main.player[Slot] = oldPlayer;
    }
    private BusinessRuleResult Evaluate(short type = -16, short sender = Slot)
    {
        byte[] body = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(body, sender);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), type);
        return contexts.Evaluate(M2ContractsTests.Packet((PacketTypes)61, body, Slot), session, actor, true)!;
    }

    [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
    public void NativeClientDoesNotSendInWorldsWithoutTheActualCombinedFeature(bool remix, bool worthy)
    {
        Main.remixWorld = remix; Main.getGoodWorld = worthy; Main.netMode = 1; Main.myPlayer = Slot;
        Assert.That(SpecialSeedFeatures.Mechdusa, Is.False);
        Assert.That(NPC.SpawnMechQueen(Slot), Is.False);
        Assert.That(sends, Is.Empty, "Real native producer returns before any packet61 send.");
    }

    [Test]
    public void CombinedSeedWithoutZenithIsANativeLegalRequestAndNeverBanned()
    {
        Main.remixWorld = Main.getGoodWorld = true; Main.zenithWorld = false;
        Main.netMode = 1; Main.myPlayer = Slot;
        Assert.That(NPC.SpawnMechQueen(Slot), Is.True);
        Assert.That(sends.Single(), Is.EqualTo((61, Slot, -16f)));
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Pass));
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, true)]
    public void M6ActualRazorItemUseEntryChecksWorldBeforeSendingOrApplyingItemTime(bool remix, bool worthy, bool sendsRequest)
    {
        Main.remixWorld = remix; Main.getGoodWorld = worthy; Main.zenithWorld = false;
        Main.netMode = 1; Main.myPlayer = Slot;
        var player = actor.TPlayer;
        player.itemAnimation = 30; player.itemTime = 0;
        var razor = new Item(); razor.SetDefaults(5334);
        player.ItemCheck_UseBossSpawners(Slot, razor);
        Assert.That(sends.Where(x => x.Id == 61).Select(x => x.Type),
            Is.EqualTo(sendsRequest ? new[] { -16f } : Array.Empty<float>()));
        Assert.That(player.itemTime > 0, Is.EqualTo(sendsRequest));
        Assert.That(Main.npc.All(x => !x.active), Is.True, "Native client branch sends a request and cannot spawn server NPCs here.");
        Main.netMode = 2; Main.myPlayer = 255;
        if (sendsRequest) Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void M6RazorStorageAndUnanimatedUseDoNotProduceASummonAttempt()
    {
        Main.remixWorld = Main.getGoodWorld = true;
        Main.netMode = 1; Main.myPlayer = Slot;
        var player = actor.TPlayer; player.inventory[0].SetDefaults(5334);
        player.itemAnimation = 0; player.itemTime = 0;
        player.ItemCheck_UseBossSpawners(Slot, player.inventory[0]);
        Assert.That(sends, Is.Empty);
        Assert.That(player.inventory[0].type, Is.EqualTo(5334));
        Assert.That(player.inventory[0].stack, Is.EqualTo(1));
    }

    [Test]
    public void NativeServerRejectsEffectInForbiddenWorldAndFirstRawRequestIsACompleteProof()
    {
        Assert.That(NPC.SpawnMechQueen(Slot), Is.False);
        Assert.That(Main.npc.All(x => !x.active), Is.True); Assert.That(sends, Is.Empty);
        var proof = Evaluate();
        Assert.That(proof.RuleId, Is.EqualTo(M5ProgressionRules.RuleId));
        Assert.That(proof.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(proof.Action, Is.EqualTo(ControlAction.Block));
    }

    [Test]
    public void ImportedRazorAndOtherPlayersAssetsDoNotAlterTheNativeWorldGate()
    {
        actor.TPlayer.inventory[0].SetDefaults(5334);
        Main.netMode = 1; Main.myPlayer = Slot;
        Assert.That(NPC.SpawnMechQueen(Slot), Is.False); Assert.That(sends, Is.Empty);
        // Inventory possession is not itself evaluated or sanctioned by this producer.
        Assert.That(contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerSlot, new byte[8], Slot), session, actor, true), Is.Null);
    }

    [Test]
    public void SameTickWorldChangesAndChangeBackPermanentlyWithdrawOnlyThisSessionsHardProof()
    {
        Main.getGoodWorld = true; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Main.getGoodWorld = false;
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(50).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void BossDeathProgressChangeDoesNotAffectThisWorldFeatureInvariant()
    {
        bool old = NPC.downedMechBoss1;
        try { NPC.downedMechBoss1 = !old; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat)); }
        finally { NPC.downedMechBoss1 = old; }
    }

    [Test]
    public void ReusedSlotLateAttachAndIncompleteSyncCannotInheritProof()
    {
        session = session with { Generation = session.Generation + 1 };
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        actor.ReceivedInfo = false; session = session with { Generation = session.Generation + 1 };
        contexts.Tick(session.WorldEpoch, Lookup); actor.ReceivedInfo = true; actor.IgnoreSSCPackets = true;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IgnoreSSCPackets = false;
        Assert.That(Evaluate(sender: Slot + 1).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ConnectBeforeFirstTickKeepsItsFromStartBaselineAfterPlayerInfoArrives()
    {
        contexts.Dispose(); contexts = new(TargetRuntime.Fingerprint); contexts.Install(); actor.ReceivedInfo = false;
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true;
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void BadFrameAndRuntimeAreNotInterpretedAsProgressionCheating()
    {
        Assert.That(contexts.Evaluate(M2ContractsTests.Packet((PacketTypes)61, new byte[3], Slot), session, actor, true), Is.Null);
        Assert.That(contexts.Evaluate(M2ContractsTests.Packet((PacketTypes)61, new byte[4], Slot), session, actor, false), Is.Null);
    }

    [Test]
    public void M6ProofFactsBindTheActualWorldAndCurrentActorObject()
    {
        var proof = Evaluate();
        Assert.That(proof.Facts["worldId"], Is.EqualTo(Main.worldID.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(proof.Facts["worldEpoch"], Is.EqualTo(session.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(proof.Facts["currentAccountAndActorBound"], Is.EqualTo("True"));
        var otherActor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = true,
            Account = actor.Account, Group = actor.Group };
        byte[] body = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(body, Slot);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), -16);
        var result = contexts.EvaluatePayload(body, session, otherActor)!;
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(result.Facts["currentAccountAndActorBound"], Is.EqualTo("False"));
    }

    [Test]
    public void M6RevokedAccountMismatchedIdentityAndWrongThreadCannotInheritAProof()
    {
        contexts.Tick(session.WorldEpoch, slot => slot == Slot
            ? (new SessionSnapshot(session, actor.Account.ID, true, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        contexts.Tick(session.WorldEpoch, slot => slot == Slot
            ? (new SessionSnapshot(session, actor.Account.ID + 1, false, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Task.Run(() => Evaluate().Verdict).GetAwaiter().GetResult(), Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void M6WorldReplacementAndEpochReplacementInvalidateTheOldConnectionBaseline()
    {
        Main.ActiveWorldFileData.WorldId++;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Main.ActiveWorldFileData.WorldId--;
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        session = session with { WorldEpoch = session.WorldEpoch + 1 };
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        actor.ReceivedInfo = false;
        session = session with { Generation = session.Generation + 1 };
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true;
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void M6UnknownPluginRemovalCannotRestoreTheCurrentClientsWorldContract()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnknownProgressionPlugin();
        var container = new PluginContainer(plugin);
        plugins.Add(container);
        try
        {
            contexts.Tick(session.WorldEpoch, Lookup);
            Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        }
        finally { plugins.Remove(container); }
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        var result = Evaluate();
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(result.Facts["pluginContractComplete"], Is.EqualTo("False"));
        Assert.That(Evaluate(50).Verdict, Is.EqualTo(Verdict.Pass));
        session = session with { Generation = session.Generation + 1 }; actor.ReceivedInfo = false;
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true; contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void M6UnknownWorldExportBetweenTicksCannotBlameTheNativeRazorProducerAfterUnload()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnknownProgressionPlugin();
        var container = new PluginContainer(plugin);
        plugins.Add(container);
        try { NetMessage.SendData(7, Slot); } // Actual installed outgoing hook; no Tick while loaded.
        finally { plugins.Remove(container); }

        // Native packet7 assigns these client fields (audited GetData7). Run the real Razor
        // producer against that exported-state fixture, separately from the server baseline.
        Main.netMode = 1; Main.myPlayer = Slot; Main.remixWorld = Main.getGoodWorld = true;
        var player = actor.TPlayer; player.itemAnimation = 30; player.itemTime = 0;
        var razor = new Item(); razor.SetDefaults(5334);
        player.ItemCheck_UseBossSpawners(Slot, razor);
        Assert.That(sends.Single(x => x.Id == 61), Is.EqualTo((61, Slot, -16f)));
        Main.netMode = 2; Main.myPlayer = 255; Main.remixWorld = Main.getGoodWorld = false;
        var result = Evaluate();
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(result.Facts["pluginContractComplete"], Is.EqualTo("False"));
        for (int tick = 0; tick < 100; tick++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(50).Verdict, Is.EqualTo(Verdict.Pass));

        session = session with { Generation = session.Generation + 1 }; actor.ReceivedInfo = false;
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true; contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    private sealed class UnknownProgressionPlugin() : TerrariaPlugin(null!)
    {
        public override void Initialize() { }
    }
}
