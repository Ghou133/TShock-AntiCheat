using System.Reflection;
using AntiCheat.Plugin.TShock;
using AntiCheat.Progression;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M8ShimmerItemTransactionTests
{
    private int oldMode, oldLocal, oldWidth, oldHeight, oldWorld;
    private bool oldMoonLord;
    private ITile? oldTile;
    private WorldItem oldWorldItem = null!;
    private TestClock clock = null!;
    private M8ShimmerItemTransactions context = null!;
    private readonly List<int> sends = [];

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldWidth = Main.maxTilesX;
        oldHeight = Main.maxTilesY; oldWorld = Main.worldID; oldMoonLord = NPC.downedMoonlord;
        oldWorldItem = Main.item[15];
        Main.netMode = 2; Main.myPlayer = 255; Main.maxTilesX = 500; Main.maxTilesY = 300;
        NPC.downedMoonlord = false;
        oldTile = Main.tile[20, 19];
        Main.tile[20, 19] = new Tile { liquid = 255 }; Main.tile[20, 19].liquidType(3);
        clock = new(); context = new(clock, TargetRuntime.Fingerprint);
        context.Install(); context.Tick(41);
        sends.Clear(); HookEvents.Terraria.NetMessage.SendData += Sink;
    }

    [TearDown]
    public void Cleanup()
    {
        context.Dispose(); HookEvents.Terraria.NetMessage.SendData -= Sink;
        Main.tile[20, 19] = oldTile!;
        Main.item[15] = oldWorldItem;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.maxTilesX = oldWidth;
        Main.maxTilesY = oldHeight; Main.ActiveWorldFileData.WorldId = oldWorld; NPC.downedMoonlord = oldMoonLord;
    }

    private void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { sends.Add(args.msgType); args.ContinueExecution = false; }

    private static WorldItem Item(int type)
    {
        var item = new Item(); item.SetDefaults(type);
        return new(item) { whoAmI = 15, position = new Vector2(20 * 16, 20 * 16),
            velocity = new Vector2(4, 8), shimmerWet = true, shimmerTime = 0.89f };
    }

    [TestCase(1326, 5335)] [TestCase(779, 5134)] [TestCase(3031, 5364)] [TestCase(5364, 3031)]
    public void ActualNativeOrdinaryTransformCommitsWithAllPrerequisitesAndRecordsCurrentWorld(int input, int output)
    {
        NPC.downedMoonlord = true;
        var entity = Item(input);
        Main.item[15] = entity;
        Assert.That(entity.inner.CanShimmer(), Is.True);
        Assert.That(entity.CanShimmerAtPosition(), Is.True);
        float gravity = 0.1f;
        entity.UpdateShimmer(ref gravity); entity.UpdateShimmer(ref gravity);
        Assert.That(entity.type, Is.EqualTo(output)); Assert.That(entity.stack, Is.EqualTo(1));
        Assert.That(context.Allowed, Is.EqualTo(1)); Assert.That(context.Completed, Is.EqualTo(1));
        Assert.That(context.Unknown, Is.Zero); Assert.That(context.Blocked, Is.Zero);
        Assert.That(sends, Does.Contain(21)); Assert.That(sends, Does.Contain(146));
        var receipt = context.CaptureRecent().Single();
        Assert.That(receipt.InputType, Is.EqualTo(input)); Assert.That(receipt.OutputType, Is.EqualTo(output));
        Assert.That(receipt.WorldEpoch, Is.EqualTo(41)); Assert.That(receipt.CurrentMoonLordDefeated, Is.True);
        Assert.That(receipt.Outcome, Is.EqualTo("native-transform-committed"));
        Assert.That(receipt.BoundToWorldSlot, Is.True);
    }

    [TestCase(1326)] [TestCase(779)] [TestCase(3031)] [TestCase(5364)]
    public void NativeOrdinaryGatePreservesImportedOrPassiveItemsWithoutAccusingAHolder(int input)
    {
        var entity = Item(input); entity.playerIndexTheItemIsReservedFor = 25;
        Assert.That(entity.inner.CanShimmer(), Is.False);
        float gravity = 0.1f;
        for (int i = 0; i < 4; i++) entity.UpdateShimmer(ref gravity);
        Assert.That(entity.type, Is.EqualTo(input)); Assert.That(entity.stack, Is.EqualTo(1));
        Assert.That(sends, Is.Empty); Assert.That(context.CaptureRecent(), Is.Empty);
        Assert.That(context.Blocked, Is.Zero, "The unmodified native producer rejects before calling the new guard.");
    }

    [TestCase(1326)] [TestCase(779)] [TestCase(3031)] [TestCase(5364)]
    public void DirectNativeCallCannotBypassTheSameLockAndDoesNotWriteOrBroadcast(int input)
    {
        var entity = Item(input);
        var before = (entity.type, entity.stack, entity.prefix, entity.position, entity.velocity,
            entity.wet, entity.shimmerWet, entity.shimmered, entity.shimmerTime);
        entity.GetShimmered();
        Assert.That((entity.type, entity.stack, entity.prefix, entity.position, entity.velocity,
            entity.wet, entity.shimmerWet, entity.shimmered, entity.shimmerTime), Is.EqualTo(before));
        Assert.That(sends, Is.Empty); Assert.That(context.Blocked, Is.EqualTo(1));
        Assert.That(context.CaptureRecent().Single().Outcome, Is.EqualTo("blocked-native-transform-lock"));
    }

    [Test]
    public void SameVersionUnprotectedDirectCallActuallyBypassesTheGate()
    {
        context.Dispose();
        var entity = Item(1326);
        Assert.That(entity.inner.CanShimmer(), Is.False);
        entity.GetShimmered();
        Assert.That(entity.type, Is.EqualTo(5335)); Assert.That(sends, Does.Contain(21));
        Assert.That(sends, Does.Contain(146));
    }

    [Test]
    public void ObservedPositiveHistoryDoesNotBecomeACleanNegativeHistoryOrAuthorizeANewLockedConversion()
    {
        NPC.downedMoonlord = true; context.Tick(41); Item(1326).GetShimmered();
        NPC.downedMoonlord = false; context.Tick(41); Item(1326).GetShimmered();
        var entries = context.CaptureRecent();
        Assert.That(entries.Last().MoonLordObservedInEpoch, Is.True);
        Assert.That(entries.Last().CurrentMoonLordDefeated, Is.False);
        Assert.That(entries.Last().Outcome, Is.EqualTo("blocked-native-transform-lock"));
        context.Tick(42); Item(1326).GetShimmered();
        Assert.That(context.CaptureRecent().Single().WorldEpoch, Is.EqualTo(42));
        Assert.That(context.CaptureRecent().Single().MoonLordObservedInEpoch, Is.False,
            "False means not observed in this epoch, never proof of no historical defeat.");
    }

    [Test]
    public void UnknownWorldAndMutatedNativeTablesWithdrawOnlyThisTransformationContract()
    {
        Main.ActiveWorldFileData.WorldId++;
        var entity = Item(1326); entity.GetShimmered();
        Assert.That(entity.type, Is.EqualTo(5335)); Assert.That(context.Unknown, Is.EqualTo(1));
        context.Tick(42);
        int old = ItemID.Sets.ShimmerTransformToItem[1326];
        try
        {
            ItemID.Sets.ShimmerTransformToItem[1326] = 5364;
            entity = Item(1326); entity.GetShimmered();
            Assert.That(entity.type, Is.EqualTo(5364)); Assert.That(context.Unknown, Is.EqualTo(2));
            Assert.That(context.LastUnknownReason, Is.EqualTo("selected-transform-table-changed"));
        }
        finally { ItemID.Sets.ShimmerTransformToItem[1326] = old; }
        Assert.That(context.Blocked, Is.Zero);
    }

    [Test]
    public void AnUnsupportedHostCanKeepItsLegitimateCustomTransformationWithoutAttribution()
    {
        var plugins = (List<PluginContainer>)typeof(ServerApi).GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var plugin = new CustomTransformPlugin();
        var container = new PluginContainer(plugin); plugins.Add(container);
        try
        {
            var entity = Item(1326); entity.GetShimmered();
            Assert.That(entity.type, Is.EqualTo(5335)); Assert.That(context.Unknown, Is.EqualTo(1));
            Assert.That(context.Blocked, Is.Zero); Assert.That(context.CaptureRecent(), Is.Empty);
        }
        finally { plugins.Remove(container); }
        context.Tick(42);
        var retainedCallback = Item(1326); retainedCallback.GetShimmered();
        Assert.That(retainedCallback.type, Is.EqualTo(5335));
        Assert.That(context.Unknown, Is.EqualTo(2), "Unloading an actually observed extension does not erase its callbacks.");
    }

    [Test]
    public void UnrelatedOrdinaryShimmerContinuesBeforeMoonLord()
    {
        var entity = Item(9); entity.GetShimmered();
        Assert.That(entity.type, Is.EqualTo(2)); Assert.That(sends, Does.Contain(21));
        Assert.That(context.Blocked, Is.Zero); Assert.That(context.Unknown, Is.Zero);
    }

    [Test]
    public void TransactionBufferHasCapacityExpiryAndNoReplayOrRefund()
    {
        for (int i = 0; i < M8ShimmerItemTransactions.Capacity + 3; i++) Item(1326).GetShimmered();
        Assert.That(context.CaptureRecent(), Has.Length.EqualTo(M8ShimmerItemTransactions.Capacity));
        Assert.That(context.Dropped, Is.EqualTo(3)); Assert.That(sends, Is.Empty);
        clock.Now -= TimeSpan.FromDays(1);
        Assert.That(context.CaptureRecent(), Has.Length.EqualTo(M8ShimmerItemTransactions.Capacity));
        clock.Timestamp += M8ShimmerItemTransactions.Retention.Ticks + 1;
        Assert.That(context.CaptureRecent(), Is.Empty); Assert.That(sends, Is.Empty);
    }

    [Test]
    public void FailedDiagnosticsWithdrawOnlyTheGuardWithoutUndoingOrRepeatingTheNativeTransformation()
    {
        NPC.downedMoonlord = true;
        clock.ThrowUtc = true;
        int faults = 0;
        context.IntegrityFault = _ => { faults++; throw new InvalidOperationException("reporter failed"); };
        var entity = Item(1326); entity.GetShimmered();
        Assert.That(entity.type, Is.EqualTo(5335)); Assert.That(context.Completed, Is.EqualTo(1));
        Assert.That(sends.Count(x => x == 21), Is.EqualTo(1)); Assert.That(faults, Is.EqualTo(1));
        Assert.That(context.Healthy, Is.False); Assert.That(context.FaultObserverFailed, Is.True);
        NPC.downedMoonlord = false;
        entity = Item(1326); entity.GetShimmered();
        Assert.That(entity.type, Is.EqualTo(5335)); Assert.That(context.Unknown, Is.EqualTo(1));
        Assert.That(faults, Is.EqualTo(1));
    }

    [Test]
    public void DiagnosticFailureCannotReplaceAnOriginalNativeException()
    {
        NPC.downedMoonlord = true; clock.ThrowUtc = true;
        var expected = new IOException("native network sink failure");
        void NativeFailure(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => throw expected;
        HookEvents.Terraria.NetMessage.SendData += NativeFailure;
        try
        {
            Assert.That(Assert.Throws<IOException>(() => Item(1326).GetShimmered()), Is.SameAs(expected));
            Assert.That(context.NativeFailures, Is.EqualTo(1)); Assert.That(context.Healthy, Is.False);
        }
        finally { HookEvents.Terraria.NetMessage.SendData -= NativeFailure; }
    }

    [Test]
    public void MismatchedRuntimeIsALocalInstallationFault()
    {
        context.Dispose(); context = new(clock, "unverified");
        int faults = 0; context.IntegrityFault = _ => faults++;
        Assert.DoesNotThrow(() => context.Install()); Assert.That(faults, Is.EqualTo(1));
        Assert.That(context.Healthy, Is.False);
    }

    [Test]
    public void SelectedOutputsStayInsideThePreservedMklpGroupWithoutPromotingThePolicy()
    {
        var source = ProgressionCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "progression", "candidates.json"))
            .Rules.Single(x => x.Id == "PG-POL-012");
        Assert.That(source.Items, Is.SupersetOf(new[] { 5335, 5134, 5364 }));
        Assert.That(source.Classification, Is.EqualTo("server_policy"));
        Assert.That(source.Qualification.ProductionEligible, Is.False);
        foreach (var pair in M8ShimmerItemTransactions.Transformations)
        {
            Assert.That(ItemID.Sets.ShimmerPostMoonlord[pair.Key], Is.True);
            Assert.That(ShimmerTransforms.GetTransformToItem(pair.Key), Is.EqualTo(pair.Value));
        }
    }

    private sealed class CustomTransformPlugin() : TerrariaPlugin(null!) { public override void Initialize() { } }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
        public long Timestamp { get; set; }
        public bool ThrowUtc { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => ThrowUtc ? throw new IOException("diagnostic clock failure") : Now;
    }
}
