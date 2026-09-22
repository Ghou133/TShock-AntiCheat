using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using TShockAPI.Sockets;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M18ArrowSourceResetIntervalTests
{
    [TestCase("normal")]
    [TestCase("wrapper-replaced")]
    [TestCase("connection-replaced")]
    [TestCase("raw-socket-replaced")]
    [TestCase("revoked")]
    [TestCase("account-changed")]
    [TestCase("native-player-replaced")]
    [TestCase("tsplayer-replaced")]
    [TestCase("observer-resolver-fault")]
    public void M18_ActualLinuxNativeWritesHaveCapturedTransportOwnership(string mode)
    {
        using var language = new M18NativeLanguageScope();
        var oldConfig = TShockAPI.TShock.Config; var oldSscConfig = TShockAPI.TShock.ServerSideCharacterConfig;
        var oldLog = TShockAPI.TShock.Log; var oldTsPlayers = TShockAPI.TShock.Players;
        var oldBuffers = (MessageBuffer[])NetMessage.buffer.Clone(); var oldClients = (RemoteClient[])Netplay.Clients.Clone();
        var oldWorld = Main.ActiveWorldFileData; string oldName = Main.worldName; bool oldSsc = Main.ServerSideCharacter;
        float oldRaining = Main.maxRaining;
        using var log = new TextLog(Path.Combine(TestContext.CurrentContext.WorkDirectory, "m18-native-transport.log"), false);
        // This owned loopback pair exercises the real provider and OS write callbacks.
        // No TShock server process or game is launched, and no client login is claimed.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var peer = new TcpClient(); peer.Connect((IPEndPoint)listener.LocalEndpoint);
        using var accepted = listener.AcceptTcpClient(); listener.Stop();
        using var replacement = new TcpClient();
        var originalRaw = accepted.Client;
        var provider = new LinuxTcpSocket(accepted);
        try
        {
            TShockAPI.TShock.Config = new TShockConfig(); TShockAPI.TShock.ServerSideCharacterConfig = new ServerSideConfig(); TShockAPI.TShock.Log = log;
            TShockAPI.TShock.Players = new TSPlayer[256]; Main.netMode = 2; Main.myPlayer = 255; Main.ServerSideCharacter = true;
            Main.ActiveWorldFileData = new WorldFileData("", false); Main.worldName = "m18-native-transport";
            for (int index = 0; index < NetMessage.buffer.Length; index++) NetMessage.buffer[index] ??= new MessageBuffer();
            for (int index = 0; index < Netplay.Clients.Length; index++) Netplay.Clients[index] ??= new RemoteClient();
            var remote = new RemoteClient { Id = Actor, State = 2, Socket = provider }; Netplay.Clients[Actor] = remote;
            NetMessage.buffer[Actor].broadcast = false;
            var player = Main.player[Actor]; player.inventory[0].SetDefaults(ItemID.WoodenBow);
            player.inventory[54].SetDefaults(ItemID.WoodenArrow); player.inventory[54].stack = 100;
            var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Group = new Group("m18-native"),
                Account = new UserAccount { ID = 1807, Name = "m18-native" }, PlayerData = new PlayerData(false) };
            TShockAPI.TShock.Players[Actor] = actor; actor.PlayerData.CopyCharacter(actor);
            var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
            var snapshot = new SessionSnapshot(session, null, false, DateTimeOffset.UtcNow);
            bool resolverFault = false;
            using var context = new M18ArrowSourceResetIntervals(TargetRuntime.Fingerprint,
                slot => resolverFault ? throw new InvalidOperationException("owned-resolver-fault") : slot == Actor ? (snapshot, actor, !snapshot.Revoked) : (null, null, false));
            context.Install(); context.Tick(1, true); context.Connected(session);
            sent.Clear(); NetMessage.SendData(7, Actor); actor.PlayerData.RestoreCharacter(actor);
            snapshot = snapshot with { AccountId = 1807 }; context.Authenticated(session, 1807);
            // These accepted pairs are explicit models. The separate native-phase test
            // executes8/12 bodies. The current test's TCP endpoint only reads bytes.
            context.AcceptedHandshake(session, 8, 2, 3); remote.State = 3; NetMessage.SendData(49, Actor);
            context.AcceptedHandshake(session, 12, 3, 10); remote.State = 10;
            var received = new List<byte[]>(); var stream = peer.GetStream(); stream.ReadTimeout = 3000;
            for (int count = 0; count < 400; count++)
            {
                byte[] prefix = new byte[2]; stream.ReadExactly(prefix);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(prefix); Assert.That(length, Is.InRange(3, 4096));
                byte[] frame = new byte[length]; prefix.CopyTo(frame, 0); stream.ReadExactly(frame.AsSpan(2)); received.Add(frame);
                if (frame[2] == 49) break;
            }
            Assert.That(received.Last()[2], Is.EqualTo(49));
            Assert.That(received.Count(frame => frame[2] == 5), Is.EqualTo(350));
            Assert.That(SpinWait.SpinUntil(() => context.Capture(session)?.RequiredWritesCompleted == true, 3000), Is.True);
            var before = context.Capture(session)!;
            Assert.That(before.HistoricalResetSequenceObserved, Is.True); Assert.That(before.NativeTransportIdentityVerified, Is.True);
            Assert.That(before.InitialResetInputsAvailable, Is.True); Assert.That(before.CompleteOrdinaryDamageModel, Is.False);
            if (mode == "normal")
            {
                actor.IgnoreSSCPackets = true; var liveOnly = context.Capture(session)!;
                Assert.That(liveOnly.HistoricalResetSequenceObserved, Is.True);
                Assert.That(liveOnly.SscRestoreInProgress, Is.True);
                Assert.That(liveOnly.InitialResetInputsAvailable, Is.False, "The native transport's previously available cached input updates with no send.");
                actor.IgnoreSSCPackets = false;
                Assert.That(context.Capture(session)!.InitialResetInputsAvailable, Is.True);
            }
            if (mode != "normal")
            {
                switch (mode)
                {
                    case "wrapper-replaced": remote.Socket = new M18CompletionSocket(); break;
                    case "connection-replaced": provider._connection = replacement; break;
                    case "raw-socket-replaced": accepted.Client = replacement.Client; break;
                    case "revoked": snapshot = snapshot with { Revoked = true }; break;
                    case "account-changed": actor.Account.ID = 1808; break;
                    case "native-player-replaced": Main.player[Actor] = new Player { whoAmI = Actor }; break;
                    case "tsplayer-replaced": TShockAPI.TShock.Players[Actor] = new TSPlayer(Actor); break;
                    case "observer-resolver-fault": resolverFault = true; break;
                }
                Assert.That(context.Capture(session), Is.Null);
                if (mode == "observer-resolver-fault")
                {
                    Assert.That(context.Healthy, Is.False); Assert.That(context.LastFault, Is.EqualTo(nameof(InvalidOperationException)));
                    Assert.That(snapshot.Revoked, Is.False);
                    Save("m18-native-transport-" + mode + ".json", new { before, context.Healthy, context.LastFault,
                        scope = "actual native Linux writes precede an explicit resolver fault; only this observer fails, and no exception escapes Capture or revokes the account" });
                    return;
                }
                remote.Socket = provider; provider._connection = accepted; accepted.Client = originalRaw;
                snapshot = snapshot with { Revoked = false }; actor.Account.ID = 1807;
                Main.player[Actor] = player; TShockAPI.TShock.Players[Actor] = actor;
                var recovered = context.Capture(session)!;
                Assert.That(recovered.HistoricalResetSequenceObserved, Is.True, "Old observed sequence remains history.");
                Assert.That(recovered.InitialResetInputsAvailable, Is.False, "Restoring references cannot heal an identity gap.");
                Assert.That(recovered.Gap, Is.Not.EqualTo("none"));
            }
            Save("m18-native-transport-" + mode + ".json", new { mode,
                source = "actual TShock RestoreCharacter/SendData -> native LinuxTcpSocket.BeginWrite/EndWrite -> owned OS loopback receiver",
                receivedSlots = received.Count(frame => frame[2] == 5), receivedPackets = received.Select(frame => (int)frame[2]).ToArray(),
                before, after = context.Capture(session),
                mutationTier = "Replacement/revocation conditions are explicit fixture mutations after actual native writes.",
                phaseTier = "Accepted8/12 states are model inputs, not client-generated login or gameplay; no TShock process was started.",
                damageModelComplete = false });
        }
        finally
        {
            provider._connection = accepted; accepted.Client = originalRaw;
            TShockAPI.TShock.Config = oldConfig; TShockAPI.TShock.ServerSideCharacterConfig = oldSscConfig; TShockAPI.TShock.Log = oldLog;
            TShockAPI.TShock.Players = oldTsPlayers; Main.ServerSideCharacter = oldSsc; Main.ActiveWorldFileData = oldWorld; Main.worldName = oldName; Main.maxRaining = oldRaining;
            Array.Copy(oldBuffers, NetMessage.buffer, oldBuffers.Length);
            for (int index = 0; index < oldClients.Length; index++) if (oldClients[index] is null) Netplay.Clients[index]?.Socket?.Close();
            Array.Copy(oldClients, Netplay.Clients, oldClients.Length);
        }
    }
}
