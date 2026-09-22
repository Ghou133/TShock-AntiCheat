using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

// The shared M7 fixture isolates the real locked engine and its transport. These are native
// producer/consumer tests; final real TCP/TSAPI plugin evidence is obtained separately.
public sealed partial class M7CombatNativeEvidenceTests
{
    private NPC M16PrepareCore(bool vulnerable)
    {
        var core = Main.npc[TargetSlot]; core.SetDefaults(NPCID.MoonLordCore);
        core.active = true; core.generation = 3; core.life = core.lifeMax = 100000;
        core.position = new(400, 400); core.defense = 0; core.knockBackResist = 0;
        core.localAI[3] = 1; core.ai[0] = 0;
        for (int index = 0; index < 3; index++)
        {
            var part = Main.npc[20 + index]; part.SetDefaults(index == 2 ? NPCID.MoonLordHead : NPCID.MoonLordHand);
            part.active = true; part.ai[0] = vulnerable ? -2 : 0; core.localAI[index] = part.whoAmI;
        }
        core.dontTakeDamage = false; // The real AI, not this assignment, must establish immunity below.
        core.AI_077_MoonLordCore();
        Assert.That(core.active, Is.True);
        Assert.That(core.ai[0], Is.EqualTo(vulnerable ? 1 : 0));
        Assert.That(core.dontTakeDamage, Is.EqualTo(!vulnerable));
        core.justHit = false;
        sent.Clear();
        return core;
    }

    private static byte[] M16StrikeFrame(NPC core, short damage, bool critical = false, int generation = -1)
    {
        byte[] frame = new byte[11]; frame[0] = 28; frame[1] = (byte)core.whoAmI;
        frame[2] = (byte)(generation < 0 ? core.generation : generation);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(3), damage); frame[9] = 2;
        frame[10] = critical ? (byte)1 : (byte)0;
        return frame;
    }

    [Test]
    public void M16_RealNativeMeleeRespectsCoreShieldAndBecomesEffectiveAfterNativePhaseTransition()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var core = M16PrepareCore(false);
        Main.netMode = 1; Main.myPlayer = Actor;
        var player = Main.player[Actor]; var sword = player.inventory[0]; sword.SetDefaults(ItemID.WoodenSword);
        int before = core.life;
        player.ProcessHitAgainstNPC(sword, core.Hitbox, 9, 0, TargetSlot);
        Assert.That(core.life, Is.EqualTo(before));
        Assert.That(sent.Any(frame => frame[2] == 28), Is.False);
        Main.netMode = 2; Main.myPlayer = 255;
        for (int index = 0; index < 3; index++) Main.npc[20 + index].ai[0] = -2;
        core.AI_077_MoonLordCore();
        Assert.That(core.ai[0], Is.EqualTo(1)); Assert.That(core.dontTakeDamage, Is.False);
        Main.netMode = 1; Main.myPlayer = Actor; sent.Clear();
        player.ProcessHitAgainstNPC(sword, core.Hitbox, 9, 0, TargetSlot);
        Assert.That(core.life, Is.LessThan(before));
        Assert.That(sent.Any(frame => frame[2] == 28), Is.True);
        TestContext.Out.WriteLine("Actual core AI derives shield from living parts; actual melee produces no28 while immune. Native part-defeat transition opens core; identical real melee then sends28 and changes life. Native-method client, not GUI.");
    }

    [TestCase((short)9, false)] [TestCase((short)1005, false)] [TestCase(short.MaxValue, true)]
    public void M16_ActualReceiverAcceptsVulnerableDamageRejectsCurrentShieldAndRecovers(short damage, bool critical)
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var core = M16PrepareCore(true);
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        var guard = new M16CombatNpcImmunityGuard(TargetRuntime.Fingerprint,
            slot => slot == Actor ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));
        guard.Tick(1);
        BusinessRuleResult? last = null; int strikeCalls = 0, lootCalls = 0;
        void Request(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor || args.Instance.readBuffer[0] != 28) return;
            var read = M6NpcStrikeReader.ReadPayload(args.Instance.readBuffer.AsSpan(1, 10));
            last = guard.Evaluate(read.Packet!, session, actor);
            if (last.Action == ControlAction.Block) args.Result = OTAPI.HookResult.Cancel;
        }
        void Strike(NPC _, HookEvents.Terraria.NPC.StrikeNPCEventArgs args) => strikeCalls++;
        void Loot(NPC _, HookEvents.Terraria.NPC.NPCLootEventArgs args) => lootCalls++;
        OTAPI.Hooks.MessageBuffer.GetData += Request; HookEvents.Terraria.NPC.StrikeNPC += Strike;
        HookEvents.Terraria.NPC.NPCLoot += Loot;
        try
        {
            int before = core.life; Receive(M16StrikeFrame(core, damage, critical), Actor);
            Assert.That(last!.Action, Is.EqualTo(ControlAction.Pass)); Assert.That(core.life, Is.LessThan(before));
            Assert.That(strikeCalls, Is.EqualTo(1)); Assert.That(sent.Any(frame => frame[2] == 28), Is.True);
            // Explicitly prepare the native protected phase again; AI establishes the final target flag.
            core = M16PrepareCore(false); before = core.life; strikeCalls = 0; lootCalls = 0;
            Array.Clear(core.playerInteraction); sent.Clear();
            Receive(M16StrikeFrame(core, damage, critical), Actor);
            Assert.That(last!.Action, Is.EqualTo(ControlAction.Block)); Assert.That(last.PredicateSatisfied, Is.False);
            Assert.That(core.life, Is.EqualTo(before)); Assert.That(core.justHit, Is.False);
            Assert.That(core.playerInteraction[Actor], Is.False); Assert.That(core.active, Is.True);
            Assert.That(strikeCalls, Is.Zero); Assert.That(lootCalls, Is.Zero);
            Assert.That(sent.Any(frame => frame[2] is 28 or 23 or 162), Is.False,
                "Whole raw action is cancelled before receipt ack, interaction credit, StrikeNPC or relay.");
            // Same account and same object regain ordinary damage as soon as the native phase opens.
            for (int index = 0; index < 3; index++) Main.npc[20 + index].ai[0] = -2;
            core.AI_077_MoonLordCore(); sent.Clear();
            Receive(M16StrikeFrame(core, damage, critical), Actor);
            Assert.That(last!.Action, Is.EqualTo(ControlAction.Pass)); Assert.That(core.life, Is.LessThan(before));
            Assert.That(strikeCalls, Is.EqualTo(1));
        }
        finally
        {
            OTAPI.Hooks.MessageBuffer.GetData -= Request; HookEvents.Terraria.NPC.StrikeNPC -= Strike;
            HookEvents.Terraria.NPC.NPCLoot -= Loot;
        }
    }

    [TestCase((short)-1)] [TestCase((short)0)] [TestCase((short)9)]
    public void M16_UnprotectedNativeReceiverDemonstratesRealShieldBypassEvenAtNonpositiveDamage(short damage)
    {
        Main.netMode = 2; Main.myPlayer = 255; var core = M16PrepareCore(false);
        int before = core.life; Receive(M16StrikeFrame(core, damage), Actor);
        Assert.That(core.life, Is.LessThan(before)); Assert.That(core.justHit, Is.True);
        Assert.That(sent.Any(frame => frame[2] == 28), Is.True);
        TestContext.Out.WriteLine($"Unprotected locked receiver consumed wire{damage} while native core shield=true; life {before}->{core.life}. No detection/ban directly invoked.");
    }

    [Test]
    public void M16_NoSnapshotCrossEpochOffThreadAndNativeServerStrikeNeverBecomeAnAccountProof()
    {
        Main.netMode = 2; Main.myPlayer = 255; var core = M16PrepareCore(false);
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        var hit = new NpcStrikeObservation(TargetSlot, core.generation, 9, 0, 2, 0);
        var guard = new M16CombatNpcImmunityGuard(TargetRuntime.Fingerprint,
            slot => slot == Actor ? (new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null));
        Assert.That(guard.Evaluate(hit, session, actor).Action, Is.EqualTo(ControlAction.Unknown));
        guard.Tick(1);
        Assert.That(guard.Evaluate(hit, session with { WorldEpoch = 2 }, actor).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(Task.Run(() => guard.Evaluate(hit, session, actor)).Result.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(guard.Evaluate(hit with { TargetGeneration = core.generation - 1 }, session, actor).Action, Is.EqualTo(ControlAction.Pass));
        actor.IsLoggedIn = false;
        Assert.That(guard.Evaluate(hit, session, actor).Action, Is.EqualTo(ControlAction.Unknown));
        actor.IsLoggedIn = true;
        int before = core.life;
        void HostTransportSink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        { if (args.msgType == 28) args.ContinueExecution = false; }
        HookEvents.Terraria.NetMessage.SendData += HostTransportSink;
        try { core.StrikeNPC(9, 0, 0, false, false, 255, null); }
        finally { HookEvents.Terraria.NetMessage.SendData -= HostTransportSink; }
        Assert.That(core.life, Is.LessThan(before), "The new raw-client policy does not intercept an explicit native host/plugin damage call.");
    }

    [TestCase("account")] [TestCase("revoked")] [TestCase("session-generation")]
    [TestCase("binding-player")] [TestCase("fake-player")] [TestCase("inventory")]
    [TestCase("ssc-ignored")] [TestCase("npc-slot")] [TestCase("runtime")]
    public void M16_MissingCurrentAttributionOrNativeTargetDisablesOnlyPolicyAndRecovers(string missing)
    {
        Main.netMode = 2; Main.myPlayer = 255; var core = M16PrepareCore(false);
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        SessionSnapshot snapshot = new(session, 37, false, DateTimeOffset.UtcNow);
        TSPlayer boundPlayer = actor;
        var guard = new M16CombatNpcImmunityGuard(missing == "runtime" ? "unverified-runtime" : TargetRuntime.Fingerprint,
            slot => slot == Actor ? (snapshot, boundPlayer) : (null, null));
        guard.Tick(1);
        var hit = new NpcStrikeObservation(TargetSlot, core.generation, 9, 0, 2, 0);
        // Legal vulnerable state before testing the missing prerequisite.
        core.ai[0] = 1; core.dontTakeDamage = false;
        Assert.That(guard.Evaluate(hit, session, actor).Action,
            Is.EqualTo(missing == "runtime" ? ControlAction.Unknown : ControlAction.Pass));
        core.ai[0] = 0; core.dontTakeDamage = true;
        var fake = typeof(TSPlayer).GetField("FakePlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        void SetFake(Player? player)
        {
            var previousConfig = TShockAPI.TShock.Config;
            try { TShockAPI.TShock.Config ??= new TShockAPI.Configuration.TShockConfig(); fake.SetValue(actor, player); }
            finally { TShockAPI.TShock.Config = previousConfig; }
        }
        switch (missing)
        {
            case "account": snapshot = snapshot with { AccountId = 38 }; break;
            case "revoked": snapshot = snapshot with { Revoked = true }; break;
            case "session-generation": snapshot = snapshot with { Key = session with { Generation = session.Generation + 1 } }; break;
            case "binding-player": boundPlayer = new TSPlayer(Actor); break;
            case "fake-player": SetFake(new Player { whoAmI = Actor }); break;
            case "inventory": actor.HasSentInventory = false; break;
            case "ssc-ignored": actor.IgnoreSSCPackets = true; break;
            case "npc-slot": core.whoAmI = TargetSlot + 1; break;
        }
        Assert.That(guard.Evaluate(hit, session, actor).Action, Is.EqualTo(ControlAction.Unknown));
        snapshot = new(session, 37, false, DateTimeOffset.UtcNow); boundPlayer = actor;
        if (missing == "fake-player") SetFake(null);
        actor.HasSentInventory = true; actor.IgnoreSSCPackets = false; core.whoAmI = TargetSlot;
        var restored = new M16CombatNpcImmunityGuard(TargetRuntime.Fingerprint, _ => (snapshot, boundPlayer)); restored.Tick(1);
        var result = restored.Evaluate(hit, session, actor);
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block)); Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(core.life, Is.EqualTo(100000));
    }
}
