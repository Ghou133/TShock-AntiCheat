using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using FieldReference = Mono.Cecil.FieldReference;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.Net;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M18ArrowSourceResetIntervalTests
{
    [Test]
    public void M18_ActualNativeSectionAndSpawnBodiesFeedTheirOwnAcceptedBoundaries()
        => M18_FinalNativeSscDispatchDistinguishesSerializationCancellationFailureAndCompletion("native-phase-bodies");

    [TestCase("normal")]
    [TestCase("cancel-slot0")]
    [TestCase("cancel-world-info")]
    [TestCase("cancel-buffs")]
    [TestCase("cancel-loadout")]
    [TestCase("cancel-ready")]
    [TestCase("throw-slot0")]
    [TestCase("defer-callbacks")]
    [TestCase("native-canceled-spawn")]
    [TestCase("retired-callbacks")]
    [TestCase("socket-reused")]
    [TestCase("late-exports")]
    [TestCase("ttl")]
    [TestCase("unsupported-host")]
    [TestCase("restore-live-cache")]
    [TestCase("pending-write-overflow")]
    [TestCase("partial-install-failure")]
    public void M18_FinalNativeSscDispatchDistinguishesSerializationCancellationFailureAndCompletion(string mode)
    {
        using var language = new M18NativeLanguageScope();
        var oldConfig = TShockAPI.TShock.Config; var oldSscConfig = TShockAPI.TShock.ServerSideCharacterConfig;
        var oldLog = TShockAPI.TShock.Log; var oldTsPlayers = TShockAPI.TShock.Players;
        var oldBuffers = (MessageBuffer[])NetMessage.buffer.Clone(); var oldClients = (RemoteClient[])Netplay.Clients.Clone();
        var oldWorld = Main.ActiveWorldFileData; string oldName = Main.worldName; bool oldSsc = Main.ServerSideCharacter;
        var oldTiles = Main.tile; var oldItems = Main.item; var oldBestiary = Main.BestiaryTracker; var oldPylons = Main.PylonSystem;
        var oldHostFlags = (bool[])Main.countsAsHostForGameplay.Clone(); float oldMaxRaining = Main.maxRaining;
        int oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY, oldSpawnX = Main.spawnTileX, oldSpawnY = Main.spawnTileY;
        var socket = new M18CompletionSocket { ThrowSlot0 = mode == "throw-slot0", Defer = mode is "defer-callbacks" or "retired-callbacks" or "pending-write-overflow" };
        using var log = new TextLog(Path.Combine(TestContext.CurrentContext.WorkDirectory, "m18-interval-native.log"), false);
        M18ArrowSourceResetIntervals? observer = null; SessionKey observedSession = default;
        var inRestore = new List<M18ResetIntervalSnapshot>();
        void ObserveRestore(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        {
            if (args.msgType == 39 && args.remoteClient == Actor && args.number == 400 && observer?.Capture(observedSession) is { } value)
                inRestore.Add(value);
        }
        void Cancel(object? _, OTAPI.Hooks.NetMessage.SendBytesEventArgs args)
        {
            if (mode == "cancel-slot0" && args.RemoteClient == Actor && args.Size == 12 && args.Data[args.Offset + 2] == 5 &&
                BinaryPrimitives.ReadInt16LittleEndian(args.Data.AsSpan(args.Offset + 4)) == 0) args.Result = OTAPI.HookResult.Cancel;
            int canceledPacket = mode switch { "cancel-world-info" => 7, "cancel-buffs" => 50, "cancel-loadout" => 147, "cancel-ready" => 49, _ => -1 };
            if (args.RemoteClient == Actor && args.Size >= 3 && args.Data[args.Offset + 2] == canceledPacket) args.Result = OTAPI.HookResult.Cancel;
        }
        try
        {
            TShockAPI.TShock.Config = new TShockConfig(); TShockAPI.TShock.ServerSideCharacterConfig = new ServerSideConfig(); TShockAPI.TShock.Log = log;
            TShockAPI.TShock.Players = new TSPlayer[256]; Main.netMode = 2; Main.myPlayer = 255; Main.ServerSideCharacter = true;
            Main.ActiveWorldFileData = new WorldFileData("", false); Main.worldName = "m18-native-output";
            if (mode == "native-phase-bodies")
            {
                Main.BestiaryTracker = new Terraria.GameContent.Bestiary.BestiaryUnlocksTracker(); Main.PylonSystem = new Terraria.GameContent.TeleportPylonsSystem();
                Main.maxTilesX = Main.maxTilesY = 100; Main.spawnTileX = Main.spawnTileY = 50;
                Main.tile = new ModFramework.DefaultCollection<ITile>(100, 100); _ = Main.tile[0, 0];
                for (int x = 0; x < 100; x++) for (int y = 0; y < 100; y++) Main.tile[x, y] = new Tile();
                Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            }
            for (int index = 0; index < NetMessage.buffer.Length; index++) NetMessage.buffer[index] ??= new MessageBuffer();
            for (int index = 0; index < Netplay.Clients.Length; index++) Netplay.Clients[index] ??= new RemoteClient();
            Netplay.Clients[Actor] = new RemoteClient { Id = Actor, State = 2, Socket = socket };
            NetMessage.buffer[Actor].broadcast = false;
            var player = Main.player[Actor]; player.inventory[0].SetDefaults(ItemID.WoodenBow);
            player.inventory[54].SetDefaults(ItemID.WoodenArrow); player.inventory[54].stack = 100;
            var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Group = new Group("m18-native"),
                Account = new UserAccount { ID = 1807, Name = "m18-native" }, PlayerData = new PlayerData(false) };
            TShockAPI.TShock.Players[Actor] = actor; actor.PlayerData.CopyCharacter(actor);
            var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
            var snapshot = new SessionSnapshot(session, null, false, DateTimeOffset.UtcNow);
            using var context = new M18ArrowSourceResetIntervals(TargetRuntime.Fingerprint,
                slot => slot == Actor ? (snapshot, actor, true) : (null, null, false));
            if (mode == "partial-install-failure")
            {
                var sentinel = new InvalidOperationException("owned-second-hook-install-fault");
                ILHook? partiallyInstalled = null;
                var installedField = typeof(M18ArrowSourceResetIntervals).GetField("hook", BindingFlags.NonPublic | BindingFlags.Instance)!;
                using (var injectFailure = new ILHook(typeof(M18ArrowSourceResetIntervals).GetMethod(nameof(M18ArrowSourceResetIntervals.Install))!, il =>
                {
                    var cursor = new ILCursor(il);
                    cursor.GotoNext(MoveType.After, instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Stfld &&
                        instruction.Operand is FieldReference field && field.Name == "hook" && field.DeclaringType.FullName == typeof(M18ArrowSourceResetIntervals).FullName);
                    cursor.EmitDelegate<Action>(() =>
                    {
                        partiallyInstalled = (ILHook)installedField.GetValue(context)!;
                        Assert.That(partiallyInstalled, Is.Not.Null, "Actual first final-send ILHook was installed before failure.");
                        throw sentinel;
                    });
                }))
                {
                    Assert.That(Assert.Throws<InvalidOperationException>(context.Install), Is.SameAs(sentinel), "The source exception survives cleanup.");
                }
                Assert.That(context.Healthy, Is.False); Assert.That(installedField.GetValue(context), Is.Null);
                Assert.That(typeof(M18ArrowSourceResetIntervals).GetField("receiveHook", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context), Is.Null);
                Assert.That(partiallyInstalled!.IsApplied, Is.False, "Partial installation was actually unhooked.");
                NetMessage.SendData(7, Actor);
                Assert.That(socket.Sent.Count(frame => frame[2] == 7), Is.EqualTo(1), "Unhooked dispatch still performs the native write once.");
                socket.Sent.Clear();
            }
            context.Install(); context.Tick(1, true); context.Connected(session);
            observer = context; observedSession = session;
            HookEvents.Terraria.NetMessage.SendData += ObserveRestore;
            OTAPI.Hooks.NetMessage.SendBytes += Cancel;
            sent.Clear(); NetMessage.SendData(7, Actor);
            Assert.That(context.Capture(session)!.WorldInfoSequence > 0, Is.EqualTo(mode != "cancel-world-info"), "Only the final uncanceled real7 reaches the observer.");
            actor.PlayerData.RestoreCharacter(actor);
            var afterRestore = context.Capture(session)!;
            Assert.That(inRestore, Has.Count.EqualTo(1), "The actual final RestoreCharacter send was observed.");
            Assert.That(inRestore[0].SscRestoreInProgress, Is.True);
            Assert.That(afterRestore.SscRestoreInProgress, Is.False, "finally cleared the live flag without another tracked send.");
            Assert.That(afterRestore, Is.Not.SameAs(inRestore[0]));
            int serializedSlots = sent.Count(frame => frame[2] == 5);
            Assert.That(serializedSlots, Is.EqualTo(700));
            int actualSlots = socket.Sent.Count(frame => frame[2] == 5);
            Assert.That(actualSlots, Is.EqualTo(mode is "cancel-slot0" or "throw-slot0" ? 349 : 350));
            snapshot = snapshot with { AccountId = 1807 }; context.Authenticated(session, 1807);
            // Explicit unit inputs for the accepted native phase pair. No native8/12 body
            // ran here; the separate receive wrapper rejection below verifies cancellation.
            if (mode == "native-phase-bodies")
            {
                sent.Clear(); try { Receive([8, 255, 255, 255, 255, 255, 255, 255, 255, 0], Actor); } catch { TestContext.Out.WriteLine("bestiaryNull=" + (Main.BestiaryTracker is null) + ";killsNull=" + (Main.BestiaryTracker?.Kills is null) + ";pylonsNull=" + (Main.PylonSystem is null) + ";native8state=" + Netplay.Clients[Actor].State + "; sends=" + string.Join(",", sent.Take(80).Select(frame => frame[2]))); throw; }
                Assert.That(Netplay.Clients[Actor].State, Is.EqualTo(3));
                Assert.That(context.Capture(session)!.SectionAccepted, Is.True);
                Assert.That(socket.Sent.Any(frame => frame[2] == 49), Is.True);
                NetMessage.SendData(12, Actor, number: Actor, number2: (int)PlayerSpawnContext.SpawningIntoWorld);
                byte[] spawn = socket.Sent.Last(frame => frame[2] == 12); Receive(spawn[2..], Actor);
                Assert.That(Netplay.Clients[Actor].State, Is.EqualTo(10));
                Assert.That(context.Capture(session)!.SpawnAccepted, Is.True);
            }
            else
            {
                context.AcceptedHandshake(session, 8, 2, 3); Netplay.Clients[Actor].State = 3;
                NetMessage.SendData(49, Actor);
            }
            if (mode == "native-canceled-spawn")
            {
                NetMessage.SendData(12, Actor, number: Actor, number2: (int)PlayerSpawnContext.SpawningIntoWorld);
                byte[] nativeSpawn = socket.Sent.Last(frame => frame[2] == 12);
                void CancelSpawn(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
                { if (args.Instance.whoAmI == Actor) args.Result = OTAPI.HookResult.Cancel; }
                OTAPI.Hooks.MessageBuffer.GetData += CancelSpawn;
                try { Receive(nativeSpawn[2..], Actor); }
                finally { OTAPI.Hooks.MessageBuffer.GetData -= CancelSpawn; }
                Assert.That(Netplay.Clients[Actor].State, Is.EqualTo(3));
                Assert.That(context.Capture(session)!.SpawnAccepted, Is.False);
            }
            else if (mode != "native-phase-bodies") { context.AcceptedHandshake(session, 12, 3, 10); Netplay.Clients[Actor].State = 10; }
            var beforeCallbacks = context.Capture(session)!;
            if (socket.Defer) Assert.That(beforeCallbacks.RequiredWritesCompleted, Is.False);
            if (mode == "pending-write-overflow")
            {
                var arrow = new Projectile(); arrow.SetDefaults(ProjectileID.WoodenArrowFriendly);
                arrow.key = new Terraria.DataStructures.ProjectileKey(Actor, 3, 1); arrow.owner = Actor; arrow.active = true;
                Main.projectile[0] = arrow;
                for (int count = 0; count < 500; count++) NetMessage.SendData(27, Actor, number: 0);
                Assert.That(socket.Sent.Count(frame => frame[2] == 27), Is.EqualTo(500), "Observation overflow must not suppress native output.");
                Assert.That(context.Capture(session)!.Gap, Is.EqualTo("send-observation-capacity"));
                Assert.That(context.Capture(session)!.InitialResetInputsAvailable, Is.False);
            }
            if (mode == "retired-callbacks")
            {
                context.Forget(session); var next = session with { Generation = 2 };
                snapshot = new(next, 1807, false, DateTimeOffset.UtcNow); context.Connected(next);
                socket.ReleaseCallbacks();
                Assert.That(context.Capture(session), Is.Null);
                Assert.That(context.Capture(next)!.Slots, Is.Empty);
                Assert.That(context.Capture(next)!.HistoricalResetSequenceObserved, Is.False);
                Save("m18-reset-dispatch-retired-callbacks.json", new { source = "actual native async send callbacks after context.Forget/new session generation", retired = session, next,
                    oldSerializedSlots = serializedSlots, actualSlots, newSlots = 0, limits = "No native reconnect or TCP; exact late callback ownership tested." });
                return;
            }
            socket.ReleaseCallbacks();
            if (mode == "socket-reused")
            {
                Netplay.Clients[Actor].Socket = new M18CompletionSocket();
                Assert.That(context.Capture(session), Is.Null);
                Netplay.Clients[Actor].Socket = socket;
                Assert.That(context.Capture(session)!.Gap, Is.EqualTo("transport-or-native-player-changed"));
            }
            if (mode == "ttl") for (int count = 0; count <= M18ArrowSourceResetIntervals.IntervalTtlTicks; count++) context.Tick(1, true);
            if (mode == "unsupported-host") { context.Tick(1, false); context.Tick(1, true); }
            if (mode == "restore-live-cache")
            {
                var historical = context.Capture(session)!;
                Assert.That(historical.HistoricalResetSequenceObserved, Is.True);
                // Explicit model transition tests cache invalidation with no intervening send.
                actor.IgnoreSSCPackets = true;
                var liveOnly = context.Capture(session)!;
                Assert.That(liveOnly.HistoricalResetSequenceObserved, Is.True);
                Assert.That(liveOnly.SscRestoreInProgress, Is.True);
                Assert.That(liveOnly.InitialResetInputsAvailable, Is.False);
                actor.IgnoreSSCPackets = false;
                Assert.That(context.Capture(session)!.SscRestoreInProgress, Is.False);
                // Then exercise real RestoreCharacter, not only the flag model.
                actor.PlayerData.RestoreCharacter(actor);
                Assert.That(inRestore, Has.Count.EqualTo(2));
                Assert.That(inRestore[1].HistoricalResetSequenceObserved, Is.True);
                Assert.That(inRestore[1].SscRestoreInProgress, Is.True);
                Assert.That(inRestore[1].InitialResetInputsAvailable, Is.False);
                var repeated = context.Capture(session)!;
                Assert.That(repeated.HistoricalResetSequenceObserved, Is.True);
                Assert.That(repeated.SscRestoreInProgress, Is.False);
                Assert.That(repeated.InitialResetInputsAvailable, Is.False);
                Assert.That(repeated.Gap, Is.EqualTo("loadout-write-after-ready-boundary"), "Actual RestoreCharacter emits147 before its slot resets.");
            }
            if (mode == "late-exports")
            {
                Main.item[7].inner.damage = 1000; NetMessage.SendData(88, Actor, number: 7, number2: 2);
                actor.TPlayer.AddBuff(BuffID.Archery, 3600); NetMessage.SendData(50, Actor, number: Actor);
                var arrow = new Projectile(); arrow.SetDefaults(ProjectileID.WoodenArrowFriendly);
                arrow.key = new Terraria.DataStructures.ProjectileKey(Actor, 3, 1); arrow.owner = Actor; arrow.active = true;
                Main.projectile[0] = arrow; NetMessage.SendData(27, Actor, number: 0);
                var influence = context.Capture(session)!;
                Assert.That(influence.LaterItemExports, Is.EqualTo(1)); Assert.That(influence.LaterBuffExports, Is.EqualTo(1));
                Assert.That(influence.ServerProjectileExports, Is.EqualTo(1));
                Assert.That(influence.CompleteOrdinaryDamageModel, Is.False);
            }
            var result = context.Capture(session)!; if (mode == "native-phase-bodies") TestContext.Out.WriteLine($"gap={result.Gap};writes={result.RequiredWritesCompleted};section={result.SectionAccepted};spawn={result.SpawnAccepted}");
            Assert.That(result.Slots.Length, Is.EqualTo(mode is "ttl" or "cancel-world-info" ? 0 : mode == "cancel-slot0" ? 349 : 350));
            Assert.That(result.HistoricalResetSequenceObserved, Is.EqualTo(mode is not ("cancel-slot0" or "throw-slot0" or "native-canceled-spawn" or "cancel-world-info" or "cancel-buffs" or "cancel-loadout" or "cancel-ready" or "pending-write-overflow")));
            Assert.That(result.NativeTransportIdentityVerified, Is.False, "In-memory test socket is not a production transport premise.");
            Assert.That(result.InitialResetInputsAvailable, Is.False);
            Assert.That(result.CompleteOrdinaryDamageModel, Is.False);
            Assert.That(result.ClientStateDirectlyObserved, Is.False);
            Assert.That(result.Buffs, Is.Empty); Assert.That(result.Loadout, Is.EqualTo(mode == "cancel-loadout" ? (int?)null : player.CurrentLoadoutIndex));
            Save("m18-reset-dispatch-" + mode + ".json", new
            {
                mode, source = "actual TShock RestoreCharacter -> actual SendData/SendPacket/InvokeSendBytes -> instrumented final ISocket.AsyncSend -> in-memory socket completion",
                serializedSlots, actualSlots, result, inRestore,
                phaseEvidence = mode == "native-phase-bodies" ? "Actual native MessageBuffer.GetData8 and12 bodies change2→3→10 and are observed by the wrapper; isolated100x100 world fixture, not client world-clear/Spawn or real TCP."
                    : "Accepted8/12 pairs explicitly supplied as model inputs, not execution of native8/12 or real TCP.",
                limits = "Tests final native dispatch and callback semantics. No client delivery certificate, no ordinary damage closure, no sanction."
            });
        }
        finally
        {
            OTAPI.Hooks.NetMessage.SendBytes -= Cancel;
            HookEvents.Terraria.NetMessage.SendData -= ObserveRestore;
            TShockAPI.TShock.Config = oldConfig; TShockAPI.TShock.ServerSideCharacterConfig = oldSscConfig; TShockAPI.TShock.Log = oldLog;
            TShockAPI.TShock.Players = oldTsPlayers; Main.ServerSideCharacter = oldSsc; Main.ActiveWorldFileData = oldWorld; Main.worldName = oldName;
            Main.tile = oldTiles; Main.item = oldItems; Main.BestiaryTracker = oldBestiary; Main.PylonSystem = oldPylons; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.spawnTileX = oldSpawnX; Main.spawnTileY = oldSpawnY;
            Main.maxRaining = oldMaxRaining; Array.Copy(oldHostFlags, Main.countsAsHostForGameplay, oldHostFlags.Length);
            Array.Copy(oldBuffers, NetMessage.buffer, oldBuffers.Length);
            for (int index = 0; index < oldClients.Length; index++) if (oldClients[index] is null) Netplay.Clients[index]?.Socket?.Close();
            Array.Copy(oldClients, Netplay.Clients, oldClients.Length);
        }
    }

    private sealed class M18CompletionSocket : ISocket
    {
        public readonly List<byte[]> Sent = [];
        private readonly Queue<Action> pending = new();
        public bool ThrowSlot0, Defer;
        public void Close() { }
        public bool IsConnected() => true;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(IPAddress.Loopback, 18007);
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!)
        {
            if (ThrowSlot0 && size == 12 && data[offset + 2] == 5 && BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 4)) == 0)
                throw new IOException("m18-owned-write-failure");
            Assert.That(Sent.Count, Is.LessThan(1024)); Sent.Add(data.AsSpan(offset, size).ToArray());
            if (Defer) pending.Enqueue(() => callback(state)); else callback(state);
        }
        public void ReleaseCallbacks() { while (pending.TryDequeue(out var callback)) callback(); }
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() { }
    }
}






