using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

// Reuses the M7 isolated native engine setup and teardown, transport sinks and real packet consumer.
public sealed partial class M7CombatNativeEvidenceTests
{
    [Test]
    public void M8_ActualSerialized88And50FeedRecipientScopedUnionWithNativeDamageProjection()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        var faults = new List<Exception>();
        using var context = new M6ArrowCandidateContexts(TargetRuntime.Fingerprint) { IntegrityFault = faults.Add };
        context.Install(); context.Connected(session);
        (SessionSnapshot?, TSPlayer?) Targets(int slot) => slot == Actor ? (new(session, 37, false, DateTimeOffset.UtcNow), actor) : (null, null);
        context.Tick(1, Targets, true);
        Assert.That(context.Healthy, Is.True, string.Join('\n', faults));
        M2Packet Shot(short damage)
        {
            byte[] payload = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(payload, new ProjectileKey(Actor, 99, 1).bits);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(20), 1); payload[22] = 16;
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(23), damage); return new(M2PacketKind.ProjectileNew, payload);
        }
        var first = context.Observe(Shot(1005), session, actor)!;
        Assert.That(first.DamageResults, Is.Not.Null);
        Assert.That(first.DamageResults!.SupportedResults.Contains(1005), Is.False);
        Assert.That(first.DamageResults.AllowedResults.IsFull, Is.True, "No-export configuration must not manufacture a clean historic baseline.");

        Main.item[7].inner.damage = 1000;
        NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
        var emitted = sent.Last(x => x[2] == 88);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(emitted.AsSpan(6)), Is.EqualTo(1000));
        var after = context.Observe(Shot(1005), session, actor)!;
        Assert.That(after.CombatExports.Single().WireDamage, Is.EqualTo(1000));
        Assert.That(after.DamageResults!.SupportedResults.Contains(1005), Is.True, "The actual host-custom source participates in the calculation.");
        Assert.That(after.DamageResults.CanExclude(1005), Is.False);
        Assert.That(after.CombatExports.Single().PacketId, Is.EqualTo(88));

        // Changing only the real calculated effect, with inventory fixed, must create a new source interval.
        actor.TPlayer.rangedDamage = 2.5f;
        context.Tick(1, Targets, true);
        after = context.Observe(Shot(2500), session, actor)!;
        Assert.That(after.RecentSnapshots.Last().ObservedBowMultiplier, Is.EqualTo(2.5f));
        Assert.That(after.DamageResults!.SupportedResults.Contains(2500), Is.True);
        Assert.That(after.DamageResults.AllowedResults.IsFull, Is.True, "A native accepted-state effect is not all client effects.");

        // The real50 writer emits a compact zero-terminated list, retaining duplicate IDs from SSC arrays.
        Array.Clear(actor.TPlayer.buffType); Array.Clear(actor.TPlayer.buffTime);
        actor.TPlayer.buffType[0] = actor.TPlayer.buffType[1] = BuffID.Archery;
        actor.TPlayer.buffTime[0] = actor.TPlayer.buffTime[1] = 100;
        NetMessage.SendData(50, Actor, -1, number: Actor);
        var buffs = context.Observe(Shot(9), session, actor)!.CombatExports.Last(x => x.PacketId == 50);
        Assert.That(buffs.BuffTypes, Is.EqualTo(new[] { BuffID.Archery, BuffID.Archery }));
        var projection = M8ArrowBuffProjection.FromExport(buffs);
        actor.TPlayer.ResetEffects(); actor.TPlayer.UpdateBuffs(Actor);
        Assert.That(projection.BowMultiplier, Is.EqualTo(actor.TPlayer.bowEffectiveDamage));
        Assert.That(projection.AllBuffsSupported, Is.True);
        Assert.That(projection.PreservedSlots, Is.EqualTo(2));
        Assert.That(context.Observe(Shot(1200), session, actor)!.DamageResults!.SupportedResults.Contains(1200), Is.True);
        for (int index = 0; index < 12; index++) NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
        after = context.Observe(Shot(1005), session, actor)!;
        Assert.That(after.CombatExports, Has.Length.EqualTo(M6ArrowCandidateContexts.ExportCapacity));
        Assert.That(context.DroppedExports, Is.GreaterThan(0));
        Assert.That(after.DamageResults!.MissingPremises, Does.Contain("bounded-export-payload-history-lost-influence-not-cleared"));
        for (int i = 0; i <= M6ArrowCandidateContexts.SnapshotTtlTicks; i++) context.Tick(1, Targets, true);
        after = context.Observe(Shot(1005), session, actor)!;
        Assert.That(after.CombatExports, Is.Empty);
        Assert.That(after.Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.True);
        Assert.That(after.DamageResults!.Complete, Is.False);
        session = session with { Generation = 2 }; context.Connected(session); context.Tick(1, Targets, true);
        Assert.That(context.Observe(Shot(1005), session, actor)!.DamageResults!.MissingPremises,
            Does.Contain("pre-observation-item-and-effect-history-not-established"), "Reconnect cannot wash persistent exported custom items into a clean baseline.");
        Assert.That(faults, Is.Empty);
    }

    [TestCase((short)9, false)]
    [TestCase((short)9, true)]
    [TestCase((short)-1, false)]
    public void M8_ActualClient28ConsumerRecordsSameGenerationLifeAndCanonicalRelayWithout27(short wireDamage, bool critical)
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        var npc = Main.npc[TargetSlot]; npc.defense = 3;
        using var context = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint);
        context.Install(); context.Tick(1);
        var request = new NpcStrikeObservation(TargetSlot, npc.generation, wireDamage, 0, 2, critical ? 1 : 0);
        var result = context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor);
        int lifeBefore = npc.life;
        byte[] frame = new byte[11]; frame[0] = 28; frame[1] = TargetSlot; frame[2] = (byte)npc.generation;
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(3), wireDamage); frame[9] = 2; frame[10] = critical ? (byte)1 : (byte)0;
        Receive(frame, Actor);
        var completion = context.CaptureClientStrike(session);
        Assert.That(completion, Is.Not.Null);
        Assert.That(completion!.LifeBefore, Is.EqualTo(lifeBefore));
        Assert.That(completion.LifeAfter, Is.EqualTo(npc.life));
        Assert.That(completion.ObservedLifeChange, Is.GreaterThan(0));
        Assert.That(completion.NativeStrikeEntryObserved, Is.True);
        Assert.That(completion.ReceiverDamage, Is.EqualTo(Math.Max(0, (int)wireDamage)));
        Assert.That(completion.TargetGeneration, Is.EqualTo(npc.generation));
        Assert.That(completion.NativeDefense, Is.EqualTo(3));
        Assert.That(completion.ClientAttackAuthorized, Is.False);
        Assert.That(completion.LootItemsVerified, Is.False);
        Assert.That(sent.Any(x => x[2] == 27), Is.False, "The consumer does not require a projectile key for legitimate melee28.");
        var relay = sent.Last(x => x[2] == 28);
        Assert.That(BinaryPrimitives.ReadInt16LittleEndian(relay.AsSpan(5)), Is.EqualTo(Math.Max(0, (int)wireDamage)));
        Assert.That(context.BuildClientStrikeResults(session).AllowedResults.IsFull, Is.True);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(context.CaptureClientStrike(session with { Generation = 2 }), Is.Null);
        for (int index = 0; index <= M7NpcStrikeCauseContexts.TtlTicks; index++) context.Tick(1);
        Assert.That(context.CaptureClientStrike(session), Is.Null);
    }

    [Test]
    public void M8_CancelledClientStrikeDoesNotInheritAConsoleHitOrAnOlderTargetGeneration()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        using var context = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint);
        context.Install(); context.Tick(1);
        var target = Main.npc[TargetSlot];
        var request = new NpcStrikeObservation(TargetSlot, target.generation, 9, 0, 2, 0);
        context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor);
        // No raw request enters the receiver, as when the existing core cancels it.
        void LocalExportSink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        { if (args.msgType == 28) args.ContinueExecution = false; }
        HookEvents.Terraria.NetMessage.SendData += LocalExportSink;
        try
        {
            target.StrikeNPC(9, 0, 1, fromNet: false, owner: Actor, entity: actor.TPlayer);
            Assert.That(context.CaptureClientStrike(session), Is.Null);
            target.generation++;
            target.StrikeNPC(9, 0, 1, fromNet: true, owner: Actor, entity: actor.TPlayer);
            NetMessage.SendData(28, -1, Actor, number: TargetSlot, number2: 9, number3: 0, number4: 1);
        }
        finally { HookEvents.Terraria.NetMessage.SendData -= LocalExportSink; }
        Assert.That(context.CaptureClientStrike(session), Is.Null);
    }

    [Test]
    public void M8_StandaloneLootAfterNativeStrikeReturnsCannotAttachToPendingClientRequest()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, Account = new UserAccount { ID = 37 } };
        using var context = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint);
        context.Install(); context.Tick(1);
        var target = Main.npc[TargetSlot];
        var request = new NpcStrikeObservation(TargetSlot, target.generation, 9, 0, 2, 0);
        context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor);
        void LootSink(NPC _, HookEvents.Terraria.NPC.NPCLootEventArgs args) => args.ContinueExecution = false;
        void TransportSink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        HookEvents.Terraria.NPC.NPCLoot += LootSink;
        HookEvents.Terraria.NetMessage.SendData += TransportSink;
        try
        {
            target.StrikeNPC(9, 0, 1, fromNet: true, owner: Actor, entity: actor.TPlayer);
            target.NPCLoot(); // A separate host call on the same target in the same tick.
            NetMessage.SendData(28, -1, Actor, number: TargetSlot, number2: 9, number4: 1);
            Assert.That(context.CaptureClientStrike(session), Is.Not.Null);
            Assert.That(context.CaptureClientStrike(session)!.LootMethodEntryObserved, Is.False);
            Assert.That(context.BuildClientStrikeResults(session).SupportedResults.Intervals, Is.Empty,
                "A real client28 life change is consumption evidence, never a legitimate attack-source input.");
        }
        finally
        {
            HookEvents.Terraria.NPC.NPCLoot -= LootSink;
            HookEvents.Terraria.NetMessage.SendData -= TransportSink;
        }
    }
}
