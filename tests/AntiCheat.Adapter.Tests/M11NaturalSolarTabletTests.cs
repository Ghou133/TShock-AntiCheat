using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed partial class M11NaturalSolarTabletTests
{
    private const int Slot = 14;
    private int oldMode, oldLocal, oldWidth;
    private bool oldHard, oldGolem, oldPlantera, oldDay, oldEclipse, oldRemix, oldZenith;
    private Player oldPlayer = null!;
    private NPC[] oldNpcs = null!;
    private TSPlayer actor = null!;
    private M5ProgressionContexts contexts = null!;
    private SessionKey session;
    private readonly List<(int Packet, float Type)> sends = [];

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX;
        oldHard = Main.hardMode; oldGolem = NPC.downedGolemBoss; oldPlantera = NPC.downedPlantBoss;
        oldDay = Main.dayTime; oldEclipse = Main.eclipse; oldRemix = Main.remixWorld; oldZenith = Main.zenithWorld;
        oldPlayer = Main.player[Slot];
        oldNpcs = Main.npc;
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 500;
        Main.hardMode = NPC.downedGolemBoss = NPC.downedPlantBoss = Main.eclipse = false;
        Main.remixWorld = Main.zenithWorld = false; Main.dayTime = true;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        // The real generic SummonItemCheck enumerates every NPC even for a non-boss event item.
        Main.npc = Enumerable.Range(0, Main.maxNPCs + 1).Select(_ => new NPC()).ToArray();
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m11-solar-default"), Account = new UserAccount { ID = 141, Name = "m11-solar" } };
        session = new(Guid.NewGuid(), 5, Slot, 1);
        contexts = new(TargetRuntime.Fingerprint); contexts.Install(); contexts.Tick(session.WorldEpoch, Lookup);
        actor.ReceivedInfo = true;
        sends.Clear(); HookEvents.Terraria.NetMessage.SendData += Sink;
    }

    private (SessionSnapshot? Session, TSPlayer? Player) Lookup(int slot) => slot == Slot
        ? (new(session, actor.Account.ID, false, DateTimeOffset.UtcNow), actor) : (null, null);
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add((args.msgType, args.number2)); args.ContinueExecution = false; }
    private BusinessRuleResult Evaluate(short type = -6, short? sender = null)
    {
        byte[] body = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(body, sender ?? Slot);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), type);
        return contexts.Evaluate(M2ContractsTests.Packet((PacketTypes)61, body, Slot), session, actor, true)!;
    }
    private void Reconnect()
    {
        actor.ReceivedInfo = false; session = session with { Generation = session.Generation + 1 };
        contexts.ObserveConnection(session, actor); actor.ReceivedInfo = true; contexts.Tick(session.WorldEpoch, Lookup);
    }

    [TearDown]
    public void Cleanup()
    {
        contexts.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth;
        Main.hardMode = oldHard; NPC.downedGolemBoss = oldGolem; NPC.downedPlantBoss = oldPlantera;
        Main.dayTime = oldDay; Main.eclipse = oldEclipse; Main.remixWorld = oldRemix; Main.zenithWorld = oldZenith;
        Main.player[Slot] = oldPlayer;
        Main.npc = oldNpcs;
    }

    [TestCase(false, false)] [TestCase(true, false)] [TestCase(true, true)]
    public void NativeStartUseHonorsHardmodeButNotSourcePlanteraOrTemplePolicy(bool hard, bool remix)
    {
        Main.hardMode = hard; Main.remixWorld = remix; Reconnect();
        Main.netMode = 1; Main.myPlayer = Slot;
        var tablet = new Item(); tablet.SetDefaults(ItemID.SolarTablet);
        Assert.That(tablet.type, Is.EqualTo(2767));
        bool starts = actor.TPlayer.ItemCheck_TryStartUse(tablet);
        Assert.That(starts, Is.EqualTo(hard));
        if (starts)
        {
            actor.TPlayer.itemAnimation = tablet.useAnimation; actor.TPlayer.itemTime = 0;
            actor.TPlayer.ItemCheck_UseEventItems(tablet);
        }
        Assert.That(sends.Where(x => x.Packet == 61).Select(x => x.Type),
            Is.EqualTo(hard ? new[] { -6f } : Array.Empty<float>()));
        Assert.That(NPC.downedPlantBoss || NPC.downedGolemBoss, Is.False);
        Assert.That(Main.eclipse, Is.False, "Owner producer sends a request; this is not receiver execution.");
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(Evaluate().Verdict, Is.EqualTo(hard ? Verdict.Pass : Verdict.Unknown));
    }

    [Test]
    public void DirectEventMethodAloneBypassesTheStartGateAndIsNotACompleteClientPath()
    {
        Main.netMode = 1; Main.myPlayer = Slot;
        var tablet = new Item(); tablet.SetDefaults(ItemID.SolarTablet);
        Assert.That(actor.TPlayer.ItemCheck_TryStartUse(tablet), Is.False);
        actor.TPlayer.itemAnimation = 30; actor.TPlayer.itemTime = 0;
        actor.TPlayer.ItemCheck_UseEventItems(tablet);
        Assert.That(sends.Single(x => x.Packet == 61).Type, Is.EqualTo(-6f),
            "An artificial existing animation makes the inner producer send; do not mislabel this as legal use.");
    }

    [Test]
    public void GolemPlanteraAndTimeChangesDoNotSupplyAnUnrelatedSharedUnknown()
    {
        NPC.downedGolemBoss = NPC.downedPlantBoss = true;
        NetMessage.SendData(7, Slot);
        NPC.downedGolemBoss = NPC.downedPlantBoss = false;
        Main.dayTime = false; Main.eclipse = true;
        contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate(-8).Verdict, Is.EqualTo(Verdict.Unknown), "Existing Sigil's own Golem history remains invalid.");
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown), "World history cannot establish which item started the existing animation.");
        Assert.That(Evaluate().Facts["hardModeHistoryComplete"], Is.EqualTo("True"));
        Assert.That(Evaluate().Facts["startedItemIdentityComplete"], Is.EqualTo("False"));
        Assert.That(Evaluate().Facts["acquisitionHistoryClaimed"], Is.EqualTo("False"));
    }

    [Test]
    public void ExportedHardmodeThenResetAndManyTicksCannotManufactureClosedClientHistory()
    {
        Main.hardMode = true; NetMessage.SendData(7, Slot); Main.hardMode = false;
        for (int i = 0; i < 100; i++) contexts.Tick(session.WorldEpoch, Lookup);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(-16).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate().Reason, Is.EqualTo("solar-tablet-started-item-identity-history-unproved"));
    }

    [Test]
    public void StorageWrongSubjectLateAttachSscAndRevokedSessionAreNotUseProof()
    {
        actor.TPlayer.inventory[0].SetDefaults(ItemID.SolarTablet);
        Assert.That(contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerSlot, new byte[8], Slot), session, actor, true), Is.Null);
        Assert.That(Evaluate(sender: Slot + 1).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.HasSentInventory = false; Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown)); actor.HasSentInventory = true;
        contexts.Tick(session.WorldEpoch, slot => slot == Slot
            ? (new SessionSnapshot(session, actor.Account.ID, true, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        contexts.Tick(session.WorldEpoch, Lookup);
        session = session with { Generation = session.Generation + 1 };
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void UnknownHostExportCannotRecoverByUnloadingAndUnverifiedThreadCannotProve()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new UnsupportedHost(); var container = new PluginContainer(plugin);
        plugins.Add(container);
        try { NetMessage.SendData(7, Slot); } finally { plugins.Remove(container); }
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Reconnect(); Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Task.Run(() => Evaluate()).GetAwaiter().GetResult().Verdict, Is.EqualTo(Verdict.Unknown));
        var thread = new Thread(() => NetMessage.SendData(7, Slot)); thread.Start(); thread.Join();
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
    }
    private sealed class UnsupportedHost() : TerrariaPlugin(null!) { public override void Initialize() { } }
}
