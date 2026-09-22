using System.Reflection;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture]
public sealed class PacketAndDispatchTests
{
    [Test]
    public void RuntimeUsesActualLockedAssembliesForItsReportedVersion()
    {
        var game = Terraria.Main.versionNumber;
        var target = TargetRuntime.Inspect();
        var temporary = BaselineRuntime.Inspect();
        Assert.Multiple(() =>
        {
            if (game == "v1.4.5.8")
            {
                Assert.That(target.Verified, Is.True, target.Reason);
                Assert.That(temporary.ExactTemporaryBaseline, Is.False);
            }
            else
            {
                Assert.That(game, Is.EqualTo("v1.4.5.6"));
                Assert.That(temporary.ExactTemporaryBaseline, Is.True, temporary.Reason);
                Assert.That(target.Verified, Is.False);
            }
            Assert.That(typeof(TerrariaPlugin).Assembly.GetName().Version, Is.EqualTo(new Version(6, 1, 0, 0)));
        });
    }

    [Test]
    public void EmbeddedRealAssemblyWithoutLocationIsUnknownInsteadOfStartupFailure()
    {
        var embedded = Assembly.Load(File.ReadAllBytes(typeof(TerrariaPlugin).Assembly.Location));
        Assert.That(embedded.Location, Is.Empty);
        var matches = typeof(BaselineRuntime).GetMethod("Matches", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.That(matches.Invoke(null, [embedded, BaselineRuntime.TsApiSha256]), Is.False);
    }

    [TestCase(0, 0)]
    [TestCase(8, 58)]
    [TestCase(254, 350)]
    public void LegalInventoryPayloadPreservesOwnerAndSlot(int owner, int inventorySlot)
    {
        byte[] body = [(byte)owner, (byte)inventorySlot, (byte)(inventorySlot >> 8), 1, 0, 0, 1, 0, 3];
        var result = PlayerSlotPacketReader.Read(PacketTypes.PlayerSlot, body, 0, 10, true);
        Assert.That(result.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(result.Packet, Is.EqualTo(new PlayerSlotPacket((byte)owner, (short)inventorySlot, 1, 0, 1, 3)));
    }

    [TestCase(-1, 10)]
    [TestCase(0, 0)]
    [TestCase(0, 9)]
    [TestCase(0, 11)]
    [TestCase(int.MaxValue, 10)]
    [TestCase(0, int.MaxValue)]
    public void MalformedOffsetsAndLengthsCannotReadOutsidePacket(int index, int length)
    {
        var result = PlayerSlotPacketReader.Read(PacketTypes.PlayerSlot, new byte[9], index, length, true);
        Assert.That(result.Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(result.Packet, Is.Null);
    }

    [Test]
    public void UnknownRuntimeDoesNotApplyTemporaryLayoutOrBlockUnrelatedTargets()
    {
        Assert.That(PlayerSlotPacketReader.Read(PacketTypes.PlayerSlot, null, -1, 0, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        Assert.That(PlayerSlotPacketReader.Read(PacketTypes.PlayerUpdate, null, -1, 0, true).Kind, Is.EqualTo(PacketReadKind.Unrelated));
    }

    [Test]
    public void ExistingCancellationNeverBecomesFalse()
    {
        var args = new GetDataEventArgs { Handled = true };
        PlayerSlotPacketReader.PreserveOrBlock(args, false);
        Assert.That(args.Handled, Is.True);
        args.Handled = false;
        PlayerSlotPacketReader.PreserveOrBlock(args, true);
        Assert.That(args.Handled, Is.True);
    }

    [Test]
    public void RealTsApiHandlerCollectionRunsHigherPriorityBeforeCoreAndRetainsHandled()
    {
        var handlers = (HandlerCollection<GetDataEventArgs>)Activator.CreateInstance(typeof(HandlerCollection<GetDataEventArgs>),
            BindingFlags.Instance | BindingFlags.NonPublic, null, ["AntiCheatIsolatedTest"], null)!;
        using var plugin = new EmptyRealPlugin();
        var calls = new List<string>();
        handlers.Register(plugin, args => { if (!args.Handled) calls.Add("core-write"); }, 0);
        handlers.Register(plugin, args => { calls.Add("front"); args.Handled = true; }, 1000);
        handlers.Register(plugin, args => { calls.Add("observer"); PlayerSlotPacketReader.PreserveOrBlock(args, false); }, -1);
        var packet = new GetDataEventArgs();
        handlers.Invoke(packet);
        Assert.That(calls, Is.EqualTo(new[] { "front", "observer" }));
        Assert.That(packet.Handled, Is.True);
    }

    [Test]
    public void DispatcherBoundsCapacityExecutesOnceAndPropagatesDatabaseFailure()
    {
        using var dispatcher = new ServerThreadDispatcher(1);
        int calls = 0;
        var first = dispatcher.InvokeAsync(() => calls++).AsTask();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.InvokeAsync(() => calls++));
        Assert.That(dispatcher.Drain(1), Is.EqualTo(1));
        first.GetAwaiter().GetResult();
        Assert.That(calls, Is.EqualTo(1));
        var failure = dispatcher.InvokeAsync(() => throw new IOException("injected-db-failure")).AsTask();
        dispatcher.Drain(1);
        Assert.Throws<IOException>(() => failure.GetAwaiter().GetResult());
    }

    [Test]
    public void DispatcherRejectsDifferentDrainThreadAndCancelsDisposedQueue()
    {
        using var dispatcher = new ServerThreadDispatcher();
        dispatcher.Drain();
        Exception? wrongThread = null;
        var thread = new Thread(() => { try { dispatcher.Drain(); } catch (Exception ex) { wrongThread = ex; } });
        thread.Start(); thread.Join();
        Assert.That(wrongThread, Is.TypeOf<InvalidOperationException>());
        var pending = dispatcher.InvokeAsync(() => Assert.Fail("disposed work ran")).AsTask();
        dispatcher.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pending.GetAwaiter().GetResult());
        Assert.That(dispatcher.PendingCount, Is.Zero);
    }

    private sealed class EmptyRealPlugin() : TerrariaPlugin(null!)
    {
        public override void Initialize() { }
    }
}
