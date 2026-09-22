using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M6VitalsPluginHistoryTests
{
    private const int Sender = 7, Reassigned = 8;

    [TestCase(false)]
    [TestCase(true)]
    public void NativeSelfHurtAfterServerSlotReassignmentCannotReviveProofWhenPluginIsRemoved(bool bindingAppearsAfterTick)
    {
        var priorPlayers = Main.player; var priorText = Main.combatText;
        var priorFilePlayer = Main.ActivePlayerFileData.Player; var priorConnection = Netplay.Connection;
        int priorMode = Main.netMode, priorLocal = Main.myPlayer; bool priorDedServ = Main.dedServ;
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var unknownPlugin = new UnknownVitalsPlugin();
        var container = new PluginContainer(unknownPlugin);
        using var contexts = new M4VitalContexts(TargetRuntime.Fingerprint);
        var session = new SessionKey(Guid.NewGuid(), 1, Sender, 1);
        bool currentBindingVisible = !bindingAppearsAfterTick;
        var actor = new TSPlayer(Sender) { IsLoggedIn = true, HasSentInventory = true, ReceivedInfo = false,
            Group = new Group("m6-vitals-native"), Account = new UserAccount { ID = 81, Name = "m6-native" } };
        M4VitalObservation? output = null;
        void Capture(object? _, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
        {
            output = M4VitalPacketReader.ReadPayload(117,
                M5VitalsContextTests.HurtPayload(args.playerTargetIndex, args.pvp, args.reason,
                    (short)args.damage, (sbyte)args.hitContext)).Packet;
            args.ContinueExecution = false;
        }
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        try
        {
            contexts.Install(slot => currentBindingVisible && slot == Sender
                ? (new SessionSnapshot(session, 81, false, DateTimeOffset.UtcNow), actor) : (null, null));
            contexts.Tick(); currentBindingVisible = true; actor.ReceivedInfo = true;
            plugins.Add(container);
            Main.netMode = 2;
            HookEvents.Terraria.NetMessage.SendData += Sink;
            // Exercise the real outgoing hook while the plugin is present, without a tick.
            // It can rewrite the resulting raw3, which the native client consumes below.
            NetMessage.SendData(3, Sender);

            // Run the real client-side packet3 consumer. The server may already be in play;
            // vanilla still accepts its new local slot, which outlives the sending plugin.
            Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
            Main.combatText = Enumerable.Range(0, 100).Select(_ => new CombatText { active = true }).ToArray();
            Main.netMode = 1; Main.myPlayer = Sender; Main.dedServ = true;
            Netplay.Connection = new RemoteServer { State = 10, PendingTermination = true };
            var clientPlayer = Main.player[Sender]; clientPlayer.active = true;
            clientPlayer.statLife = clientPlayer.statLifeMax = clientPlayer.statLifeMax2 = 100;
            Main.ActivePlayerFileData.Player = clientPlayer;
            HookEvents.Terraria.NetMessage.SendPlayerHurt += Capture;
            var message = new MessageBuffer { whoAmI = 256 };
            message.readBuffer[0] = 3; message.readBuffer[1] = Reassigned; message.readBuffer[2] = 0;
            message.GetData(0, 3, out int messageType);
            Assert.That(messageType, Is.EqualTo(3)); Assert.That(Main.myPlayer, Is.EqualTo(Reassigned));
            Assert.That(Main.LocalPlayer, Is.SameAs(clientPlayer));

            plugins.Remove(container);
            Main.LocalPlayer.Hurt(PlayerDeathReason.ByOther(0), 20, 0, dodgeable: false);
            Assert.That(clientPlayer.statLife, Is.LessThan(100)); Assert.That(output, Is.Not.Null);
            Assert.That(output!.ClaimedSlot, Is.EqualTo(Reassigned)); Assert.That(output.Pvp, Is.False);

            // Evaluate the actual native output at the original server-side account binding.
            Main.player = priorPlayers; Main.netMode = 2; Main.myPlayer = 255;
            for (int tick = 0; tick < 100; tick++) contexts.Tick();
            var result = contexts.Evaluate(output, session, actor);
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(result.Facts["knownPluginSet"], Is.EqualTo("True"));
            Assert.That(result.Facts["hurtPluginContractComplete"], Is.EqualTo("False"));
            Assert.That(contexts.Evaluate(output with { ClaimedSlot = Sender }, session, actor).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(contexts.Evaluate(output with { Pvp = true }, session, actor).Verdict, Is.EqualTo(Verdict.Pass));
            session = session with { Generation = session.Generation + 1 }; actor.ReceivedInfo = false;
            contexts.Tick(); actor.ReceivedInfo = true;
            Assert.That(contexts.Evaluate(output, session, actor).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendData -= Sink;
            HookEvents.Terraria.NetMessage.SendPlayerHurt -= Capture;
            plugins.Remove(container);
            Main.player = priorPlayers; Main.combatText = priorText;
            Main.ActivePlayerFileData.Player = priorFilePlayer; Netplay.Connection = priorConnection;
            Main.netMode = priorMode; Main.myPlayer = priorLocal; Main.dedServ = priorDedServ;
        }
    }

    private sealed class UnknownVitalsPlugin() : TerrariaPlugin(null!)
    {
        public override void Initialize() { }
    }
}
