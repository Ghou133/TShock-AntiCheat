using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Persistence;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(15)]
    [TestCase(16)]
    public void NativeRewardObserverHandlesEveryBoundedItemCount(int changedItemCount)
    {
        var observer = new M18ImportantItemRewardObserver(
            new M18ImportantItemQueueOptions { Enabled = true },
            M18ImportantItemCatalog.Items);
        observer.Tick(session.WorldEpoch);
        var oldWorldItems = Main.item;
        try
        {
            Main.item = Enumerable.Range(0, 401)
                .Select(index => new WorldItem { whoAmI = index }).ToArray();
            for (int index = 0; index < 16; index++)
                Main.item[index].inner.SetDefaults(0);
            var baseline = observer.CaptureBaseline();
            for (int index = 0; index < changedItemCount; index++)
            {
                Main.item[index].inner.SetDefaults(ItemID.MoonLordBossBag);
                Main.item[index].inner.stack = 1;
            }

            var observation = observer.CaptureReward(session, 6207, Target, Target.generation,
                nativeLootMethodObserved: true, baseline);
            Assert.That(observation?.Items.Length ?? 0, Is.EqualTo(Math.Min(changedItemCount, 16)));
            if (changedItemCount > 0)
            {
                Assert.That(observation, Is.Not.Null);
                Assert.That(observation!.Items.All(item => item.PreviousStack == 0 && item.Delta == 1), Is.True);
            }
        }
        finally
        {
            Main.item = oldWorldItems;
        }
    }

    [Test]
    public void OptionalRewardObserverFaultDoesNotDisableSharedStrikeQueue()
    {
        int sharedFaults = 0, optionalFaults = 0;
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint,
            M18NpcStrikeQueueOptions.TestLabCandidate,
            new M18ImportantItemQueueOptions { Enabled = true },
            M18ImportantItemCatalog.Items)
        {
            IntegrityFault = _ => sharedFaults++,
            OptionalRewardObservationFault = _ => optionalFaults++,
        };

        causes.ReportOptionalRewardObservationFault(new InvalidOperationException("injected optional observer fault"));

        Assert.Multiple(() =>
        {
            Assert.That(causes.StrikeQueueEnabled, Is.True);
            Assert.That(causes.ImportantItemRewardEnabled, Is.False);
            Assert.That(causes.ImportantItemRewardObservationHealthy, Is.False);
            Assert.That(causes.ImportantItemRewardObservationFaultType, Is.EqualTo(nameof(InvalidOperationException)));
            Assert.That(optionalFaults, Is.EqualTo(1));
            Assert.That(sharedFaults, Is.Zero);
        });
    }

    [Test]
    public void TestLabImportantBossBagGrowthIsObservedButNeverCancelsOrSanctions()
    {
        string journalRoot = Path.Combine(Path.GetTempPath(), "AntiCheat.M18ImportantItemTests", Guid.NewGuid().ToString("N"));
        string journalFile = Path.Combine(journalRoot, "m18-observations.json");
        var journal = new M18ObservationJournal(journalFile);
        typeof(AntiCheatPlugin).GetField("_m18ObservationJournal", Private)!.SetValue(plugin, journal);
        var business = new M2BusinessAdapter(TargetRuntime.Fingerprint,
            Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "progression"),
            importantItemQueueOptions: new M18ImportantItemQueueOptions
            {
                Enabled = true,
                WindowTicks = 12,
                EventCapacity = 8,
                InventorySlotCapacity = 16,
            },
            importantItemDefinitions: M18ImportantItemCatalog.Items);
        business.ImportantItemObservationRecorded =
            typeof(AntiCheatPlugin).GetMethod("RecordM18ImportantItemObservation", Private)!
                .CreateDelegate<Action<M18ImportantItemObservation, M18ImportantItemQueueDecision>>(plugin);
        typeof(AntiCheatPlugin).GetField("_business", Private)!.SetValue(plugin, business);
        var actor = TShockAPI.TShock.Players[Slot]!;
        bool oldSentInventory = actor.HasSentInventory;
        try
        {
            actor.HasSentInventory = false;
            Assert.That(RootImportantSlot(0, ItemID.MoonLordBossBag, 1).Handled, Is.False);
            actor.HasSentInventory = true;
            Assert.That(RootImportantSlot(0, ItemID.MoonLordBossBag, 9999).Handled, Is.False);
            Assert.That(RootImportantSlot(0, 0, 0).Handled, Is.False);
            Assert.That(RootImportantSlot(1, ItemID.MoonLordBossBag, 9999).Handled, Is.False,
                "A clear/refill sequence remains a record-only possible sort, not an input block.");

            var chestBody = new byte[8];
            BinaryPrimitives.WriteInt16LittleEndian(chestBody.AsSpan(0), 0);
            chestBody[2] = 2;
            BinaryPrimitives.WriteInt16LittleEndian(chestBody.AsSpan(3), 1);
            BinaryPrimitives.WriteInt16LittleEndian(chestBody.AsSpan(6), ItemID.MoonLordBossBag);
            var chest = M2PacketReader.Read(M2ContractsTests.Packet(PacketTypes.ChestItem, chestBody, Slot), true).Packet!;
            var chestResult = business.Evaluate(chest, session, actor, _ => (null, null), false)
                .Single(result => result.RuleId == M18ImportantItemQueueRules.RuleId);
            Assert.That(chestResult.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(chestResult.PredicateSatisfied, Is.False);

            var dropBody = new byte[24];
            BinaryPrimitives.WriteInt16LittleEndian(dropBody.AsSpan(0), 3);
            BinaryPrimitives.WriteInt16LittleEndian(dropBody.AsSpan(18), 12);
            BinaryPrimitives.WriteInt16LittleEndian(dropBody.AsSpan(22), ItemID.MoonLordBossBag);
            var drop = M2PacketReader.Read(M2ContractsTests.Packet(PacketTypes.ItemDrop, dropBody, Slot), true).Packet!;
            var dropResult = business.Evaluate(drop, session, actor, _ => (null, null), false)
                .Single(result => result.RuleId == M18ImportantItemQueueRules.RuleId);
            Assert.That(dropResult.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(dropResult.Facts["sourceAttributionComplete"], Is.EqualTo("False"));

            var observationJournal = (M18ObservationJournal)typeof(AntiCheatPlugin)
                .GetField("_m18ObservationJournal", Private)!.GetValue(plugin)!;
            var samples = observationJournal.Snapshot().ImportantItemBuckets
                .SelectMany(bucket => bucket.Samples).ToArray();
            var clear = samples.Single(sample => sample.Stack == 0 && sample.ItemId == ItemID.MoonLordBossBag);
            var refill = samples.Single(sample => sample.Stack == 9999 && sample.CurrentTotal == 9999 &&
                sample.PreviousTotal == 0);
            Assert.Multiple(() =>
            {
                Assert.That(clear.PreviousStack, Is.EqualTo(9999));
                Assert.That(clear.PreviousTotal, Is.EqualTo(9999));
                Assert.That(clear.CurrentTotal, Is.Zero);
                Assert.That(clear.PreviousSlotStack, Is.EqualTo(9999));
                Assert.That(clear.CurrentSlotStack, Is.Zero);
                Assert.That(refill.ItemId, Is.EqualTo(ItemID.MoonLordBossBag));
                Assert.That(refill.PreviousSlotStack, Is.Zero);
                Assert.That(refill.CurrentSlotStack, Is.EqualTo(9999));
            });

            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            TestContext.Out.WriteLine("TestLab important boss-bag queue: initial sync, stack growth, clear/refill, chest, and world-drop observations stayed record-only; no cancellation and no sanctions.");
        }
        finally
        {
            actor.HasSentInventory = oldSentInventory;
            typeof(AntiCheatPlugin).GetField("_business", Private)!.SetValue(plugin, null);
            journal.Dispose();
            try { if (Directory.Exists(journalRoot)) Directory.Delete(journalRoot, recursive: true); }
            catch { /* The test already owns this disposable temporary path. */ }
        }
    }

    private GetDataEventArgs RootImportantSlot(int inventorySlot, int itemId, int stack,
        bool cancelled = false)
    {
        byte[] body = new byte[9];
        body[0] = SlotByte();
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), (short)inventorySlot);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(3), (short)stack);
        body[5] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(6), (short)itemId);
        var args = M2ContractsTests.Packet(PacketTypes.PlayerSlot, body, Slot);
        args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args);
        return args;
    }

    private static byte SlotByte() => 7;
}
