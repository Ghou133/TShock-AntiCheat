using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M7NpcStrikeCauseContextTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ActualBackgroundServerStrikeInvalidatesOldCauseWithoutDisablingLaterMainThreadObservation(bool invalidateViaTick)
    {
        var oldNpc = Main.npc[11]; var oldPlayer = Main.player[7]; int oldMode = Main.netMode;
        var oldConfig = ServerTShock.Config;
        var session = new SessionKey(Guid.NewGuid(), 1, 7, 1);
        int mainThread = Environment.CurrentManagedThreadId, backgroundThread = 0, backgroundInputs = 0, backgroundExports = 0, faults = 0;
        Exception? backgroundError = null;
        using var context = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint) { IntegrityFault = _ => faults++ };
        void Stop(NPC _, HookEvents.Terraria.NPC.StrikeNPCEventArgs args)
        {
            if (Environment.CurrentManagedThreadId != mainThread) backgroundInputs++;
            args.ContinueExecution = false; // This observer test captures actual entries, not committed NPC damage.
        }
        void StopExport(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        {
            if (Environment.CurrentManagedThreadId != mainThread && args.msgType == 28) backgroundExports++;
            args.ContinueExecution = false;
        }
        try
        {
            ServerTShock.Config = new TShockAPI.Configuration.TShockConfig();
            Main.netMode = 2; Main.npc[11] = new NPC { whoAmI = 11, active = true, generation = 3, life = 5000 };
            Main.player[7] = new Player { whoAmI = 7, active = true };
            var actor = new TSPlayer(7); var server = new TSServerPlayer();
            var request = new AntiCheat.Rules.NpcStrikeObservation(11, 3, 1000, 0, 2, 1);
            context.Install(); context.Tick(1);
            HookEvents.Terraria.NPC.StrikeNPC += Stop;
            HookEvents.Terraria.NetMessage.SendData += StopExport;
            Main.npc[11].StrikeNPC(7, 0, 1, fromNet: false, owner: 7, entity: Main.player[7]);
            var enriched = context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor);
            Assert.That(enriched.Facts["nativeCauseInputDamage"], Is.EqualTo("7"));

            var worker = new Thread(() =>
            {
                try
                {
                    backgroundThread = Environment.CurrentManagedThreadId;
                    // Actual locked TSServerPlayer wrapper used by console Butcher, including its native28 export entry.
                    // Repeated entries coalesce into one bounded invalidation, never a cause queue.
                    for (int i = 0; i < 64; i++) server.StrikeNPC(11, 4000 + i, 0, 1);
                }
                catch (Exception error) { backgroundError = error; }
            }) { IsBackground = true, Name = "M7 isolated console-strike regression" };
            worker.Start();
            Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(backgroundError, Is.Null);
            Assert.That(backgroundThread, Is.Not.EqualTo(mainThread));
            Assert.That(backgroundInputs, Is.EqualTo(64));
            Assert.That(backgroundExports, Is.EqualTo(64));
            Assert.That(faults, Is.Zero, "An expected native console entry is not an integrity failure.");
            if (invalidateViaTick) context.Tick(1);
            enriched = context.Enrich(enriched, request, session, actor);
            Assert.That(enriched.Facts["recentServerNativeCause"], Is.EqualTo("none-for-current-target-generation"));
            Assert.That(enriched.Facts.ContainsKey("nativeCauseInputDamage"), Is.False, "The previous main-thread cause must not survive the unobserved host action.");

            Main.npc[11].StrikeNPC(9, 0, 1, fromNet: false, owner: 7, entity: Main.player[7]);
            enriched = context.Enrich(enriched, request, session, actor);
            Assert.That(enriched.Facts["nativeCauseInputDamage"], Is.EqualTo("9"), "Fresh main-thread observations remain available.");
            Assert.That(enriched.Facts["nativeCauseIsClientAuthorization"], Is.EqualTo("False"));
            Assert.That(enriched.Facts["exclusiveAttackCauseAvailable"], Is.EqualTo("False"));
            Assert.That(enriched.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(enriched.Facts.Count, Is.LessThanOrEqualTo(23));
            Assert.That(Main.npc[11].life, Is.EqualTo(5000));
            Assert.That(faults, Is.Zero);
        }
        finally
        {
            HookEvents.Terraria.NPC.StrikeNPC -= Stop;
            HookEvents.Terraria.NetMessage.SendData -= StopExport;
            Main.npc[11] = oldNpc; Main.player[7] = oldPlayer; Main.netMode = oldMode;
            ServerTShock.Config = oldConfig;
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ActualVerifiedThreadStrikeStillDisablesObserverForInvalidFingerprintOrEpoch(bool invalidFingerprint)
    {
        var oldNpc = Main.npc[11]; int oldMode = Main.netMode, faults = 0;
        using var context = new M7NpcStrikeCauseContexts(invalidFingerprint ? "unverified-target" : TargetRuntime.Fingerprint)
        { IntegrityFault = error => { Assert.That(error, Is.TypeOf<InvalidOperationException>()); faults++; } };
        void Stop(NPC _, HookEvents.Terraria.NPC.StrikeNPCEventArgs args) => args.ContinueExecution = false;
        try
        {
            Main.netMode = 2; Main.npc[11] = new NPC { whoAmI = 11, active = true, generation = 3, life = 5000 };
            context.Install(); context.Tick(invalidFingerprint ? 1 : 0);
            HookEvents.Terraria.NPC.StrikeNPC += Stop;
            Main.npc[11].StrikeNPC(7, 0, 1);
            context.Tick(1);
            Main.npc[11].StrikeNPC(9, 0, 1);
            Assert.That(faults, Is.EqualTo(1), "A real contract failure still latches the observer off.");
        }
        finally
        {
            HookEvents.Terraria.NPC.StrikeNPC -= Stop;
            Main.npc[11] = oldNpc; Main.netMode = oldMode;
        }
    }

    [Test]
    public void ActualLocalNativeCauseIsGenerationBoundAndReceived28CannotLaunderItIntoAuthorization()
    {
        var oldNpc = Main.npc[11]; var oldPlayer = Main.player[7]; int oldMode = Main.netMode;
        var session = new SessionKey(Guid.NewGuid(), 1, 7, 1);
        using var context = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint);
        void Stop(NPC _, HookEvents.Terraria.NPC.StrikeNPCEventArgs args) => args.ContinueExecution = false;
        try
        {
            Main.netMode = 2; Main.npc[11] = new NPC { whoAmI = 11, active = true, generation = 3, life = 5000 };
            Main.player[7] = new Player { whoAmI = 7, active = true };
            var actor = new TSPlayer(7); var request = new AntiCheat.Rules.NpcStrikeObservation(11, 3, 1000, 0, 2, 1);
            context.Install(); context.Tick(1);
            // This sink runs after the observer: it avoids mutation/transport while the genuine entry arguments are inspected.
            HookEvents.Terraria.NPC.StrikeNPC += Stop;
            Main.npc[11].StrikeNPC(7, 0, 1, fromNet: false, owner: 7, entity: Main.player[7]);
            var enriched = context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor);
            Assert.That(enriched.Facts["recentServerNativeCause"], Is.EqualTo("server-native-player"));
            Assert.That(enriched.Facts["nativeCauseInputDamage"], Is.EqualTo("7"));
            Assert.That(enriched.Facts["nativeCauseIsClientAuthorization"], Is.EqualTo("False"));
            Assert.That(enriched.Facts.Count, Is.LessThanOrEqualTo(23), "Reserve the root cancellation field within the 24-field evidence limit.");
            Assert.That(enriched.Verdict, Is.EqualTo(Verdict.Unknown));
            Main.npc[11].StrikeNPC(1000, 0, 1, fromNet: true, owner: 7, entity: Main.player[7]);
            enriched = context.Enrich(enriched, request, session, actor);
            Assert.That(enriched.Facts["nativeCauseInputDamage"], Is.EqualTo("7"), "The receiver's Player placeholder is not a real attack cause.");
            for (int i = 0; i <= M7NpcStrikeCauseContexts.TtlTicks; i++) context.Tick(1);
            Assert.That(context.Enrich(enriched, request, session, actor).Facts.ContainsKey("nativeCauseInputDamage"), Is.False);
            Assert.That(context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor)
                .Facts["recentServerNativeCause"], Is.EqualTo("none-for-current-target-generation"));
            Main.npc[11].StrikeNPC(9, 0, 1, fromNet: false, owner: 7, entity: Main.player[7]);
            Main.npc[11].generation = 4;
            Assert.That(context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request, session, actor)
                .Facts["recentServerNativeCause"], Is.EqualTo("none-for-current-target-generation"));
            context.Tick(2);
            Assert.That(context.Enrich(M6NpcStrikeReader.Evaluate(request, session, TargetRuntime.Fingerprint), request,
                session with { WorldEpoch = 2 }, actor).Facts["recentServerNativeCause"], Is.EqualTo("none-for-current-target-generation"));
        }
        finally
        {
            HookEvents.Terraria.NPC.StrikeNPC -= Stop;
            Main.npc[11] = oldNpc; Main.player[7] = oldPlayer; Main.netMode = oldMode;
        }
    }
}
