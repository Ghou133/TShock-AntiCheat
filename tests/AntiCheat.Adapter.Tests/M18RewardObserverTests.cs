using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(15)]
    [TestCase(16)]
    public void M18RewardObserverKeepsNativeLootAvailableForEveryBoundedItemCount(int changedItemCount)
    {
        var observer = new M18ImportantItemRewardObserver(new M18RewardObservationOptions(true),
            M18ImportantItemCatalog.Items);
        observer.Tick(session.WorldEpoch);
        var previous = Main.item;
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

            var observed = observer.CaptureReward(session, 6207, Target, Target.generation,
                nativeLootMethodObserved: true, baseline);
            Assert.That(observed?.Items.Length ?? 0, Is.EqualTo(changedItemCount));
            if (changedItemCount > 0)
                Assert.That(observed!.Items.All(item => item.PreviousStack == 0 && item.Delta == 1), Is.True);
        }
        finally { Main.item = previous; }
    }

    [Test]
    public void M18RewardObservationFailureDoesNotDisableNpcStrikeQueue()
    {
        int sharedFaults = 0, optionalFaults = 0;
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint,
            M18NpcStrikeQueueOptions.TestLabCandidate,
            new M18RewardObservationOptions(true), M18ImportantItemCatalog.Items)
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
}
