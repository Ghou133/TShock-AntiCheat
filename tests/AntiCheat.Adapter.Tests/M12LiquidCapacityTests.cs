using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.NetModules;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaApi.Server.Hooking;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M12LiquidCapacityTests
{
    [Test]
    public void ExceptionAfterNativeSwapRetainsBothSourceSetsAndReplaysOnlyLatestValues()
    {
        Fixture((patch, first, second) => NativeLiquidStorage(() =>
        {
            int a = (100 << 16) | 50, b = (101 << 16) | 50, later = (350 << 16) | 200;
            Main.tile[100, 50].liquid = 11; Main.tile[101, 50].liquid = 12; Main.tile[350, 200].liquid = 13;
            Liquid.NetSendLiquid(100, 50); Liquid.NetSendLiquid(101, 50);
            void FailAfterHandoff(NetManager _, HookEvents.Terraria.Net.NetManager.SendDataEventArgs e)
            {
                Liquid.NetSendLiquid(350, 200);
                throw new IOException("Owned failure after native source swap and before original Clear");
            }
            HookEvents.Terraria.Net.NetManager.SendData += FailAfterHandoff;
            try { Assert.Throws<IOException>(Liquid.UpdateLiquid); }
            finally { HookEvents.Terraria.Net.NetManager.SendData -= FailAfterHandoff; }
            Assert.That(Liquid._swapNetChangeSet, Is.EquivalentTo(new[] { a, b }));
            Assert.That(Liquid._netChangeSet, Is.EquivalentTo(new[] { later }));
            Assert.That(patch.Snapshot(0).Pending, Is.EqualTo(2));
            Main.tile[100, 50].liquid = 77; Liquid.NetSendLiquid(100, 50);
            for (int round = 0; round < 3; round++) { Liquid.UpdateLiquid(); Drain(patch, first, second); }
            var decoded = Decode(first.Frames);
            Assert.That(decoded.Keys, Is.EquivalentTo(new[] { a, b, later }));
            Assert.That(decoded[a].Amount, Is.EqualTo(77)); Assert.That(decoded[b].Amount, Is.EqualTo(12));
            Assert.That(decoded[later].Amount, Is.EqualTo(13));
            Assert.That(Liquid._netChangeSet, Is.Empty); Assert.That(Liquid._swapNetChangeSet, Is.Empty);
            SaveFrames("exception-after-source-swap", first.Frames);
            Save("exception-after-source-swap", new { retainedSources = 2, latestA = decoded[a].Amount,
                allUniqueCoordinates = decoded.Count, boundedReplayRounds = 3, kind = "fix-assertion" });
        }));
    }

    [Test]
    public void NativeBackgroundProducerAndSourceSwapShareAnAtomicBoundaryWithoutHoldingItDuringSend()
    {
        Fixture((patch, first, second) => NativeLiquidStorage(() =>
        {
            object sourceGate = LiquidPatchFacade.Implementation.GetField("SourceGate", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? throw new InvalidOperationException("This fix assertion requires the revision2 source ownership contract.");
            using var atAdd = new ManualResetEventSlim(); using var releaseAdd = new ManualResetEventSlim();
            using var workerDone = new ManualResetEventSlim(); using var atSwap = new ManualResetEventSlim();
            int target = (350 << 16) | 200, seed = (100 << 16) | 50, afterSwap = (355 << 16) | 200;
            Main.tile[350, 200].liquid = 17; Main.tile[100, 50].liquid = 22; Main.tile[355, 200].liquid = 23;
            Liquid._netChangeSet.Add(seed); Liquid.wetCounter = 1;
            int producerThread = 0, updaterThread = 0, adds = 0;
            Exception? backgroundError = null;
            using var addBarrier = new ILHook(typeof(Liquid).GetMethod(nameof(Liquid.NetSendLiquid))!, context =>
            {
                var cursor = new ILCursor(context);
                if (!cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m && m.Name == "Add" && m.DeclaringType.Name.StartsWith("HashSet")))
                    throw new InvalidOperationException("Expected native HashSet.Add site missing.");
                cursor.EmitDelegate<Action>(() =>
                {
                    if (Interlocked.Increment(ref adds) != 1) return;
                    producerThread = Environment.CurrentManagedThreadId; atAdd.Set();
                    if (!releaseAdd.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled source producer barrier timed out.");
                });
            });
            using var swapBarrier = new ILHook(typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid))!, context =>
            {
                var cursor = new ILCursor(context);
                if (!cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ldsflda && i.Operand is FieldReference f && f.Name == nameof(Liquid._netChangeSet)))
                    throw new InvalidOperationException("Expected native source reference handoff missing.");
                cursor.EmitDelegate<Action>(() => { updaterThread = Environment.CurrentManagedThreadId; atSwap.Set(); });
            });
            WorldGen.TransformWorldOnBackgroundThread(() =>
            {
                try { WorldGen.SquareTileFrame(350, 200); }
                catch (Exception error) { backgroundError = error; }
                finally { workerDone.Set(); }
            }, null!);
            Task? updating = null;
            try
            {
                Assert.That(atAdd.Wait(TimeSpan.FromSeconds(5)), Is.True);
                first.OnSubmit = () =>
                {
                    Assert.That(Monitor.IsEntered(sourceGate), Is.False, "No send may retain the source metadata lock.");
                    // A later native producer must be able to complete while AsyncSend is still
                    // on the call stack; it belongs to the new source set and cannot be cleared.
                    Task.Run(() => Liquid.NetSendLiquid(355, 200)).GetAwaiter().GetResult();
                    first.OnSubmit = null;
                };
                updating = Task.Run(Liquid.UpdateLiquid);
                Assert.That(atSwap.Wait(TimeSpan.FromSeconds(5)), Is.True);
                bool acquired = Monitor.TryEnter(sourceGate);
                if (acquired) Monitor.Exit(sourceGate);
                Assert.That(acquired, Is.False, "The in-progress Add must own the same boundary used for source exchange.");
                releaseAdd.Set(); Assert.That(workerDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(updating.Wait(TimeSpan.FromSeconds(5)), Is.True); updating.GetAwaiter().GetResult();
                Assert.That(backgroundError, Is.Null); Assert.That(producerThread, Is.Not.EqualTo(updaterThread));
                Assert.That(Liquid._netChangeSet.Contains(afterSwap), Is.True);
                Drain(patch, first, second);
                Assert.That(Decode(first.Frames)[target].Amount, Is.EqualTo(17), "The producer preceding atomic handoff is included in the actual batch.");
                SaveFrames("revision2-native-source-atomic", first.Frames);
                Save("revision2-native-source-atomic", new { producerThread, updaterThread, target,
                    targetReceived = true, sourceSetRetainsNewDuringSend = true, metadataLockHeldDuringSend = false,
                    kind = "fix-assertion", actualNativeBackgroundDispatcher = true });
            }
            finally { releaseAdd.Set(); workerDone.Wait(TimeSpan.FromSeconds(5)); updating?.Wait(TimeSpan.FromSeconds(5)); }
        }));
    }

    [Test, Explicit("Retained revision1 native source-swap defect witness; never counted as a fixed-runtime assertion.")]
    public void Revision1NativeBackgroundFramingCanAddToTheAlreadySwappedSetBeforeItsClear()
    {
        Fixture((patch, first, second) => NativeLiquidStorage(() =>
        {
            using var atAdd = new ManualResetEventSlim(); using var releaseAdd = new ManualResetEventSlim();
            using var workerDone = new ManualResetEventSlim();
            int target = (350 << 16) | 200, seed = (100 << 16) | 50;
            Main.tile[350, 200].liquid = 17; Main.tile[100, 50].liquid = 22;
            Liquid._netChangeSet.Add(seed);
            Liquid.wetCounter = 1; // Native next segment excludes the just-added index 0; no queue is cleared or removed.
            int gameThread = Environment.CurrentManagedThreadId, producerThread = 0;
            Exception? backgroundError = null;
            using var barrier = new ILHook(typeof(Liquid).GetMethod(nameof(Liquid.NetSendLiquid))!, context =>
            {
                var cursor = new ILCursor(context);
                if (!cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m && m.Name == "Add" && m.DeclaringType.Name.StartsWith("HashSet")))
                    throw new InvalidOperationException("Expected native HashSet.Add site missing.");
                cursor.EmitDelegate<Action>(() =>
                {
                    if (Environment.CurrentManagedThreadId == gameThread) return;
                    producerThread = Environment.CurrentManagedThreadId; atAdd.Set();
                    if (!releaseAdd.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled source producer barrier timed out.");
                });
            });
            // The real native background dispatcher used by StartHardmode executes a real
            // SquareTileFrame -> AddWater -> NetSendLiquid path with a wet owned tile.
            WorldGen.TransformWorldOnBackgroundThread(() =>
            {
                try { WorldGen.SquareTileFrame(350, 200); }
                catch (Exception error) { backgroundError = error; }
                finally { workerDone.Set(); }
            }, null!);
            try
            {
                Assert.That(atAdd.Wait(TimeSpan.FromSeconds(5)), Is.True);
                first.OnSubmit = () =>
                {
                    releaseAdd.Set(); Assert.That(workerDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(Liquid._swapNetChangeSet.Contains(target), Is.True, "The producer wrote the retired source after the batch snapshot.");
                    first.OnSubmit = null;
                };
                Liquid.UpdateLiquid();
                Assert.That(backgroundError, Is.Null);
                Assert.That(producerThread, Is.Not.EqualTo(gameThread));
                Assert.That(Liquid._netChangeSet.Contains(target), Is.False);
                Assert.That(Liquid._swapNetChangeSet.Contains(target), Is.False);
                Drain(patch, first, second);
                Assert.That(Decode(first.Frames).ContainsKey(target), Is.False, "Revision1 witness: valid producer state was cleared without dispatch.");
                Save("revision1-native-source-loss-witness", new { gameThread, producerThread, target,
                    nativeBackgroundEntry = "TransformWorldOnBackgroundThread -> SquareTileFrame -> AddWater -> NetSendLiquid",
                    initialSetCapturedBeforeSwap = true, addedDuringSendThenCleared = true, targetReceived = false,
                    kind = "defect-witness-not-fix-pass", sourcePatch = "revision1" });
            }
            finally { releaseAdd.Set(); workerDone.Wait(TimeSpan.FromSeconds(5)); }
        }));
    }

    [TestCase(0)] [TestCase(100)] [TestCase(10920)] [TestCase(10921)] [TestCase(10922)] [TestCase(21600)] [TestCase(70000)]
    public void ProductPerPlayerPathBatchesTheActualProtocolAndIndependentReceiverMatchesEveryCell(int count)
    {
        Fixture((patch, first, second) =>
        {
            var changes = Seed(count);
            NetLiquidModule.PrepareChunks(changes);
            NetLiquidModule.PrepareAndSendToEachPlayerSeparately();
            Drain(patch, first, second);
            var decoded = Decode(first.Frames);
            Assert.That(decoded.Keys, Is.EquivalentTo(changes));
            foreach (int cell in changes)
                Assert.That(decoded[cell], Is.EqualTo((Main.tile[cell >> 16, cell & 65535].liquid, Main.tile[cell >> 16, cell & 65535].liquidType())));
            Assert.That(patch.Snapshot(0).Pending, Is.Zero);
            Assert.That(first.Frames.All(frame => frame.Length <= ushort.MaxValue), Is.True);
            Assert.That(first.Frames.Count, Is.EqualTo((count + LiquidPatchFacade.MaximumCellsPerPacket - 1) / LiquidPatchFacade.MaximumCellsPerPacket));
            SaveFrames("capacity-" + count, first.Frames);
            // Native public single-frame API counterexample remains historical; real product
            // dispatch now reaches this batching implementation, not a test-only batching loop.
            Save("capacity-" + count, new { count, frames = first.Frames.Select(FrameInfo), actual = decoded.Count,
                patch.SubmittedPackets, patch.CompletedWrites, state = patch.Snapshot(0), independentDecodedFinalState = true });
        });
    }

    [Test]
    public void EmptyBatchSerializesAValidSevenByteOriginalProtocolFrame()
    {
        Fixture((patch, first, second) =>
        {
            var packet = LiquidPatchFacade.Serialize([]);
            try
            {
                byte[] bytes = packet.Buffer.Data.AsSpan(0, packet.Length).ToArray();
                Assert.That(bytes.Length, Is.EqualTo(7)); Assert.That(Decode([bytes]), Is.Empty);
                SaveFrames("empty-serialized-batch", [bytes]);
            }
            finally { packet.Recycle(); }
        });
    }

    [Test]
    public void EachPlayersVisibleSetIsIndependentAndInFlightBytesSurvivePoolReuseAndNewRevision()
    {
        Fixture((patch, first, second) =>
        {
            Netplay.Clients[1].TileSections[0, 0] = false;
            var changes = Seed(21600);
            NetLiquidModule.CreateAndBroadcastByChunk(changes);
            Assert.That(first.Pending.Count, Is.EqualTo(1));
            var oldBytes = first.Pending.Single().Bytes.ToArray();
            // An actual outstanding ISocket buffer is retained, not copied by this receiver.
            // Force ordinary native packet pool reuse before completing the prior async send.
            var unrelated = NetModule.CreatePacket<NetLiquidModule>();
            Array.Fill(unrelated.Buffer.Data, (byte)0xCE); unrelated.Recycle();
            Assert.That(first.Pending.Single().Bytes, Is.EqualTo(oldBytes));
            int revised = changes.First();
            Main.tile[revised >> 16, revised & 65535].liquid = 19;
            int added = (350 << 16) | 200;
            Main.tile[350, 200].liquid = 71;
            NetLiquidModule.CreateAndBroadcastByChunk([revised, added]);
            Assert.That(first.Pending.Count, Is.EqualTo(1), "Only one write can be outstanding per binding.");
            Drain(patch, first, second);
            var a = Decode(first.Frames); var b = Decode(second.Frames);
            SaveFrames("visibility-first", first.Frames); SaveFrames("visibility-second", second.Frames);
            Assert.That(a.Count, Is.EqualTo(changes.Count + 1));
            Assert.That(a[revised].Amount, Is.EqualTo(19)); Assert.That(a[added].Amount, Is.EqualTo(71));
            Assert.That(b.Keys.All(packed => Netplay.GetSectionX(packed >> 16) != 0 || Netplay.GetSectionY(packed & 65535) != 0), Is.True);
            Assert.That(b.ContainsKey(added), Is.True);
            Save("visibility-new-revision", new { firstCount = a.Count, secondCount = b.Count,
                firstFrames = first.Frames.Select(FrameInfo), secondFrames = second.Frames.Select(FrameInfo), latestRevision = a[revised] });
        });
    }

    [Test]
    public void HookCancellationRetainsUnsentCoordinatesAndTransportFailureOnlyClosesTheCapturedSocket()
    {
        Fixture((patch, first, second) =>
        {
            var changes = Seed(100);
            void Cancel(NetManager _, HookEvents.Terraria.Net.NetManager.SendDataEventArgs e) => e.ContinueExecution = false;
            HookEvents.Terraria.Net.NetManager.SendData += Cancel;
            try { NetLiquidModule.CreateAndBroadcastByChunk(changes); }
            finally { HookEvents.Terraria.Net.NetManager.SendData -= Cancel; }
            Assert.That(patch.Snapshot(0).Pending, Is.EqualTo(100)); Assert.That(first.Frames, Is.Empty);
            first.ThrowOnSubmit = true;
            patch.Pump();
            Assert.That(first.Closed, Is.True); Assert.That(second.Closed, Is.False);
            Assert.That(patch.TransportFailures, Is.EqualTo(1));
            Assert.That(patch.Snapshot(0).Pending, Is.EqualTo(100), "Failed submission is retained until this disconnected binding is retired.");
            var replacement = new DeferredSocket();
            Netplay.Clients[0].Socket = replacement;
            NetLiquidModule.CreateAndBroadcastByChunk(changes);
            Drain(patch, replacement, second);
            Assert.That(replacement.Closed, Is.False);
            Assert.That(Decode(replacement.Frames).Count, Is.EqualTo(100));
            Save("cancel-failure-reconnect", new { patch.TransportFailures, patch.CancelledAttempts,
                replacementDecoded = Decode(replacement.Frames).Count, accountVerdict = false,
                recovery = "new socket obtains native section synchronization; controlled path verifies replacement liquid dispatch" });
        });
    }

    [Test]
    public void LateWriteCompletionCannotReleaseOrOverwriteTheReplacementBindingsFrame()
    {
        Fixture((patch, first, second) =>
        {
            var changes = Seed(100);
            NetLiquidModule.CreateAndBroadcastByChunk(changes);
            var replacement = new DeferredSocket(); Netplay.Clients[0].Socket = replacement;
            int packed = changes.First(); Main.tile[packed >> 16, packed & 65535].liquid = 33;
            NetLiquidModule.CreateAndBroadcastByChunk([packed]);
            Assert.That(replacement.Pending.Count, Is.EqualTo(1));
            first.Complete(); patch.Pump();
            Assert.That(patch.Snapshot(0).InFlight, Is.EqualTo(1));
            Assert.That(replacement.Pending.Count, Is.EqualTo(1));
            Drain(patch, replacement, second);
            Assert.That(Decode(replacement.Frames)[packed].Amount, Is.EqualTo(33));
            Assert.That(replacement.Closed, Is.False);
        });
    }

    [Test]
    public void SerializationAndHookExceptionsReturnTheBatchBeforeRethrowing()
    {
        Fixture((patch, first, second) =>
        {
            int packed = (100 << 16) | 50;
            Main.tile[100, 50] = null!;
            Assert.Throws<NullReferenceException>(() => NetLiquidModule.CreateAndBroadcastByChunk([packed]));
            Assert.That(patch.Snapshot(0).Pending, Is.EqualTo(1)); Assert.That(patch.Snapshot(0).InFlight, Is.Zero);
            Main.tile[100, 50] = new Tile { liquid = 52 };
            void Throw(NetManager _, HookEvents.Terraria.Net.NetManager.SendDataEventArgs e) => throw new IOException("Owned test hook failure");
            HookEvents.Terraria.Net.NetManager.SendData += Throw;
            try { Assert.Throws<IOException>(() => patch.Pump()); }
            finally { HookEvents.Terraria.Net.NetManager.SendData -= Throw; }
            Assert.That(patch.Snapshot(0).Pending, Is.EqualTo(1)); Assert.That(patch.Snapshot(0).InFlight, Is.Zero);
            Drain(patch, first, second);
            Assert.That(Decode(first.Frames)[packed].Amount, Is.EqualTo(52));
        });
    }

    [Test]
    public void AcceptedSocketSendsTheWholeCheckerboardOverRealTcpToIndependentReceiver()
    {
        Fixture((patch, first, second) =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var receiver = new TcpClient(); receiver.Connect((IPEndPoint)listener.LocalEndpoint);
            using var accepted = listener.AcceptTcpClient();
            var socket = new TShockAPI.Sockets.LinuxTcpSocket(accepted);
            Netplay.Clients[0].Socket = socket;
            receiver.ReceiveTimeout = 3000;
            var frames = new List<byte[]>(); var changes = Seed(21600);
            NetLiquidModule.CreateAndBroadcastByChunk(changes);
            var read = Task.Run(() =>
            {
                var stream = receiver.GetStream();
                for (int i = 0; i < 2; i++)
                {
                    byte[] prefix = new byte[2]; stream.ReadExactly(prefix);
                    byte[] frame = new byte[BitConverter.ToUInt16(prefix)]; prefix.CopyTo(frame, 0);
                    stream.ReadExactly(frame.AsSpan(2)); frames.Add(frame);
                }
            });
            // Bounded controlled scheduler while the actual kernel write completes; no fixed
            // pacing, no synthetic write-completion callback and no service GUI are involved.
            Assert.That(SpinWait.SpinUntil(() => { patch.Pump(); second.Complete(); return read.IsCompleted; }, TimeSpan.FromSeconds(5)), Is.True);
            read.GetAwaiter().GetResult();
            var decoded = Decode(frames);
            SaveFrames("real-tcp-21600", frames);
            Assert.That(decoded.Keys, Is.EquivalentTo(changes));
            foreach (int cell in changes) Assert.That(decoded[cell], Is.EqualTo((Main.tile[cell >> 16, cell & 65535].liquid, Main.tile[cell >> 16, cell & 65535].liquidType())));
            Save("real-tcp-21600", new { layer = "actual-loopback-tcp-product-dispatch", frames = frames.Select(FrameInfo), finalCount = decoded.Count,
                receiveBytes = frames.Sum(f => f.Length), playerGui = false, socketImplementation = socket.GetType().FullName });
        });
    }

    [Test]
    public void ActualLiquidUpdateReachesProductOutboundPathAndKeepsChangesArrivingDuringTheSend()
    {
        Fixture((patch, first, second) =>
        {
            var saved = typeof(Liquid).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsInitOnly && !f.IsLiteral).Select(f => (Field: f, Value: f.GetValue(null))).ToArray();
            var oldLiquid = Main.liquid; var oldBuffer = Main.liquidBuffer; int oldBufferCount = LiquidBuffer.numLiquidBuffer;
            bool oldGenerating = WorldGen.isGeneratingOrLoadingWorld;
            try
            {
                Main.liquid = new Liquid[Liquid.maxLiquid]; Main.liquidBuffer = new LiquidBuffer[Liquid.maxLiquidBuffer];
                Liquid.ReInit(); LiquidBuffer.numLiquidBuffer = 0; WorldGen.isGeneratingOrLoadingWorld = false;
                Liquid.skipCount = 2; Liquid.wetCounter = 0; Liquid.cycles = 10; Liquid.curMaxLiquid = 24500;
                Liquid.quickSettle = Liquid.quickFall = Liquid.panicMode = Liquid.stuck = false;
                Liquid.panicCounter = Liquid.panicY = Liquid.stuckCount = Liquid.stuckAmount = 0;
                Liquid._netChangeSet = []; Liquid._swapNetChangeSet = [];
                int x = 220, y = 120;
                Main.tile[x, y].liquid = 255;
                Liquid.AddWater(x, y); // Actual native producer, not direct preparation of a wire fixture.
                first.OnSubmit = () => { Main.tile[350, 200].liquid = 17; Liquid.NetSendLiquid(350, 200); first.OnSubmit = null; };
                Liquid.UpdateLiquid();
                Assert.That(first.Pending.Count, Is.EqualTo(1));
                Assert.That(Liquid._netChangeSet.Contains((350 << 16) | 200), Is.True, "Native post-swap change remains pending.");
                Liquid.UpdateLiquid();
                Drain(patch, first, second);
                var decoded = Decode(first.Frames);
                SaveFrames("actual-liquid-update-outbound", first.Frames);
                Assert.That(decoded.ContainsKey((x << 16) | y), Is.True);
                Assert.That(decoded[(350 << 16) | 200].Amount, Is.EqualTo(17));
                Save("actual-liquid-update-outbound", new { nativeEntry = "Liquid.AddWater -> Liquid.UpdateLiquid -> CreateAndBroadcastByChunk -> NetManager.SendData -> ISocket.AsyncSend",
                    frames = first.Frames.Select(FrameInfo), coordinates = decoded.Keys, layer = "controlled-native-method-and-independent-wire-decoder",
                    experimentalScheduler = false, conservationQualified = false, naturalConvergenceQualified = false });
            }
            finally
            {
                foreach (var (field, value) in saved) field.SetValue(null, value);
                Main.liquid = oldLiquid; Main.liquidBuffer = oldBuffer; LiquidBuffer.numLiquidBuffer = oldBufferCount;
                WorldGen.isGeneratingOrLoadingWorld = oldGenerating;
            }
        });
    }

    private static HashSet<int> Seed(int count)
    {
        var result = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int x = 100 + i / 180, y = 50 + i % 180;
            Main.tile[x, y].liquid = (byte)(1 + i % 255); Main.tile[x, y].liquidType((byte)(i % 4));
            result.Add((x << 16) | y);
        }
        return result;
    }

    private static void NativeLiquidStorage(Action action)
    {
        var saved = typeof(Liquid).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.IsInitOnly && !f.IsLiteral).Select(f => (Field: f, Value: f.GetValue(null))).ToArray();
        var oldLiquid = Main.liquid; var oldBuffer = Main.liquidBuffer; int oldBufferCount = LiquidBuffer.numLiquidBuffer;
        bool oldGenerating = WorldGen.isGeneratingOrLoadingWorld;
        try
        {
            Main.liquid = new Liquid[Liquid.maxLiquid]; Main.liquidBuffer = new LiquidBuffer[Liquid.maxLiquidBuffer];
            Liquid.ReInit(); LiquidBuffer.numLiquidBuffer = 0; WorldGen.isGeneratingOrLoadingWorld = false;
            Liquid.skipCount = 2; Liquid.wetCounter = 0; Liquid.cycles = 10; Liquid.curMaxLiquid = 24500;
            Liquid.quickSettle = Liquid.quickFall = Liquid.panicMode = Liquid.stuck = false;
            Liquid.panicCounter = Liquid.panicY = Liquid.stuckCount = Liquid.stuckAmount = 0;
            Liquid._netChangeSet = []; Liquid._swapNetChangeSet = [];
            action();
        }
        finally
        {
            foreach (var (field, value) in saved) field.SetValue(null, value);
            Main.liquid = oldLiquid; Main.liquidBuffer = oldBuffer; LiquidBuffer.numLiquidBuffer = oldBufferCount;
            WorldGen.isGeneratingOrLoadingWorld = oldGenerating;
        }
    }

    private static void Drain(LiquidPatchFacade patch, params DeferredSocket[] sockets)
    {
        for (int turn = 0; turn < 16; turn++)
        {
            foreach (var socket in sockets) socket.Complete();
            patch.Pump();
            if (Enumerable.Range(0, 2).All(i => patch.Snapshot(i).Pending == 0 && patch.Snapshot(i).InFlight == 0)) return;
        }
        Assert.Fail("Liquid backlog did not drain within the bounded controlled completion window.");
    }

    private static object FrameInfo(byte[] bytes) => new { length = bytes.Length, count = BitConverter.ToUInt16(bytes, 5), sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    private static void SaveFrames(string name, IEnumerable<byte[]> frames)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m12-liquid-" + name + ".frames.bin");
        using (var stream = File.Create(path)) foreach (byte[] frame in frames) stream.Write(frame);
        TestContext.AddTestAttachment(path, "Actual serialized frames for independent offline decoding");
    }
    private static Dictionary<int, (byte Amount, byte Type)> Decode(IEnumerable<byte[]> frames)
    {
        // Independent receiver: no product serializer helpers or Terraria Deserialize calls.
        var state = new Dictionary<int, (byte, byte)>();
        foreach (byte[] frame in frames)
        {
            using var reader = new BinaryReader(new MemoryStream(frame));
            Assert.That(reader.ReadUInt16(), Is.EqualTo(frame.Length)); Assert.That(reader.ReadByte(), Is.EqualTo(82));
            Assert.That(reader.ReadUInt16(), Is.EqualTo(NetManager.Instance.GetId<NetLiquidModule>()));
            int count = reader.ReadUInt16(); Assert.That(frame.Length, Is.EqualTo(7 + count * 6));
            for (int i = 0; i < count; i++) state[reader.ReadInt32()] = (reader.ReadByte(), reader.ReadByte());
            Assert.That(reader.BaseStream.Position, Is.EqualTo(reader.BaseStream.Length));
        }
        return state;
    }

    private static void Save(string name, object data)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m12-liquid-" + name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { data,
            target = "Terraria 1.4.5.8 / protocol326", runtimeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Liquid).Assembly.Location))),
            patchSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(LiquidPatchFacade.Implementation.Assembly.Location))),
            patchAssembly = LiquidPatchFacade.Implementation.Assembly.Location,
            sourceLinkedDevelopmentPatch = LiquidPatchFacade.Implementation.Assembly == typeof(M12LiquidCapacityTests).Assembly,
            gui = false, transportCompletionIsClientAcknowledgement = false }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        TestContext.AddTestAttachment(path); TestContext.Out.WriteLine(path);
    }

    private static void Fixture(Action<LiquidPatchFacade, DeferredSocket, DeferredSocket> action) => M6WiringExecutionTests.RunScenario((_, _, _) =>
    {
        var oldClients = Netplay.Clients.ToArray(); var oldChunks = NetLiquidModule._changesByChunkCoords;
        var savedTiles = new List<(int X, int Y, ITile Tile)>();
        try
        {
            for (int x = 90; x < 500; x++) for (int y = 40; y <= 240; y++)
            { savedTiles.Add((x, y, Main.tile[x, y])); Main.tile[x, y] = new Tile(); }
            for (int i = 0; i < Netplay.Clients.Length; i++) Netplay.Clients[i] = new RemoteClient { Id = i };
            var first = new DeferredSocket(); var second = new DeferredSocket();
            Netplay.Clients[0].Socket = first; Netplay.Clients[1].Socket = second;
            foreach (int i in new[] { 0, 1 })
                for (int x = 0; x < Netplay.Clients[i].TileSections.GetLength(0); x++)
                    for (int y = 0; y < Netplay.Clients[i].TileSections.GetLength(1); y++) Netplay.Clients[i].TileSections[x, y] = true;
            NetLiquidModule._changesByChunkCoords = [];
            using var patch = LiquidPatchFacade.Install(); patch.FailureObserver = TestContext.Out.WriteLine;
            action(patch, first, second);
        }
        finally
        {
            oldClients.CopyTo(Netplay.Clients, 0); NetLiquidModule._changesByChunkCoords = oldChunks;
            foreach (var (x, y, tile) in savedTiles) Main.tile[x, y] = tile;
        }
    });

    private sealed class LiquidPatchFacade : IDisposable
    {
        private const string Name = "TerrariaApi.Server.Hooking.NativeLiquidSyncPatch";
        internal const int MaximumCellsPerPacket = (ushort.MaxValue - 7) / 6;
        internal static Type Implementation => Environment.GetEnvironmentVariable("M12_USE_SOURCE_LIQUID_PATCH") == "1"
            ? typeof(M12LiquidCapacityTests).Assembly.GetType(Name) ?? throw new TypeLoadException(Name)
            : typeof(TerrariaApi.Server.ServerApi).Assembly.GetType(Name)
            ?? (Environment.GetEnvironmentVariable("M12_REQUIRE_ACCEPTED_LIQUID_RUNTIME") == "1"
                ? throw new InvalidOperationException("Accepted TSAPI does not contain the M12 liquid implementation.")
                : typeof(M12LiquidCapacityTests).Assembly.GetType(Name) ?? throw new TypeLoadException(Name));
        private readonly object value;
        private LiquidPatchFacade(object value) => this.value = value;
        private object? Call(string method, params object[] args)
        {
            try { return Implementation.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(value, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private long Read(string field) => (long)Implementation.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(value)!;
        public long SubmittedPackets => Read(nameof(SubmittedPackets));
        public long CompletedWrites => Read(nameof(CompletedWrites));
        public long TransportFailures => Read(nameof(TransportFailures));
        public long CancelledAttempts => Read(nameof(CancelledAttempts));
        public Action<string> FailureObserver { set => Implementation.GetField(nameof(FailureObserver), BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.value, value); }
        public static LiquidPatchFacade Install() => new(Implementation.GetMethod("Install")!.Invoke(null, null)!);
        public static NetPacket Serialize(int[] coordinates) => (NetPacket)Implementation.GetMethod("Serialize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [coordinates])!;
        public void Pump() => Call("Pump");
        public (int Pending, int InFlight, int AllocatedSections) Snapshot(int index) => ((int, int, int))Call("Snapshot", index)!;
        public void Dispose() => Call("Dispose");
    }

    private sealed class DeferredSocket : ISocket
    {
        internal sealed record Write(byte[] Bytes, int Offset, int Count, SocketSendCallback Callback, object State);
        internal readonly Queue<Write> Pending = new();
        internal readonly List<byte[]> Frames = new();
        internal bool Closed, ThrowOnSubmit;
        internal Action? OnSubmit;
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state)
        {
            if (ThrowOnSubmit) throw new IOException("Owned test socket write failure");
            Pending.Enqueue(new(data, offset, size, callback, state)); OnSubmit?.Invoke();
        }
        internal void Complete()
        {
            while (Pending.TryDequeue(out var write))
            { Frames.Add(write.Bytes.AsSpan(write.Offset, write.Count).ToArray()); write.Callback(write.State); }
        }
        public bool IsConnected() => !Closed;
        public void Close() => Closed = true;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(IPAddress.Loopback, 17777);
        public bool IsDataAvailable() => false;
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state) => throw new NotSupportedException();
        public void Connect(RemoteAddress address) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() { }
    }
}
