using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M11NaturalSolarTabletTests
{
    [Test]
    public void M12RealInputAndSscOutputPathsRetainDifferentPossibleStarterAndArrivalItemsWithoutProvingReceipt()
    {
        bool oldSsc = Main.ServerSideCharacter;
        try
        {
            Main.ServerSideCharacter = true;
            actor.TPlayer.inventory[0].SetDefaults(ItemID.ClockworkAssaultRifle);
            byte[] use = new byte[14]; use[0] = Slot; use[1] = 32;
            Assert.That(contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerUpdate, use, Slot), session, actor, true), Is.Null);
            actor.TPlayer.inventory[0].SetDefaults(ItemID.SolarTablet);
            NetMessage.SendData(5, Slot, -1, number: Slot, number2: 0);
            var result = Evaluate(); var sources = contexts.CaptureUsePossibilities(session, actor);
            Assert.That(sources.Observed, Does.Contain(new M12PossibleUseSource(ItemID.ClockworkAssaultRifle, 0, M12PossibleUseOrigin.AcceptedServerItemAtClientUseIntent)));
            Assert.That(sources.Observed, Does.Contain(new M12PossibleUseSource(ItemID.SolarTablet, 0, M12PossibleUseOrigin.SscInventoryExportAttempt)));
            Assert.That(sources.Observed, Does.Contain(new M12PossibleUseSource(ItemID.SolarTablet, 0, M12PossibleUseOrigin.MessageArrivalCurrentItem)));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(result.Facts["possibleUseSourceSetComplete"], Is.EqualTo("False"));
            Assert.That(result.Facts["unobservedAnimationStartStillPossible"], Is.EqualTo("True"));
            Assert.That(result.Facts["sscExportProvesClientReceipt"], Is.EqualTo("False"));
            Assert.That(result.Facts["observedPossibleUseItemIds"], Does.Contain(ItemID.ClockworkAssaultRifle.ToString()));
            TestContext.Out.WriteLine("Actual raw13 observation + actual outgoing SSC5 hook + actual raw61 arrival retain rifle and Tablet as distinct possibilities. The SSC sink may cancel later: observation explicitly says export attempt, never client receipt. Verdict remains Unknown.");
        }
        finally { Main.ServerSideCharacter = oldSsc; }
    }

    [Test]
    public void M12MissingCancelledWrongSenderAndNewGenerationCannotTurnPossibleSourceRecordsIntoCheatProof()
    {
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        actor.TPlayer.inventory[0].SetDefaults(ItemID.ClockworkAssaultRifle);
        byte[] use = new byte[14]; use[0] = Slot; use[1] = 32;
        var canceled = M2ContractsTests.Packet(PacketTypes.PlayerUpdate, use, Slot); canceled.Handled = true;
        contexts.Evaluate(canceled, session, actor, true);
        use[0] = Slot + 1; contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerUpdate, use, Slot), session, actor, true);
        Assert.That(contexts.CaptureUsePossibilities(session, actor).Observed.Any(x => x.ItemType == ItemID.ClockworkAssaultRifle), Is.False);
        use[0] = Slot; contexts.Evaluate(M2ContractsTests.Packet(PacketTypes.PlayerUpdate, use, Slot), session, actor, true);
        var prior = session; Reconnect();
        Assert.That(contexts.CaptureUsePossibilities(prior, actor).LostEntries, Is.True);
        Assert.That(contexts.CaptureUsePossibilities(session, actor).Observed, Is.Empty);
        Assert.That(Evaluate().Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(Evaluate(-16).Verdict, Is.EqualTo(Verdict.ProvenCheat), "Unproven animation sources do not disable the independently proved native Mechdusa world gate.");
    }

    [Test]
    public void M12SourceRingAndExpiryStayBoundedAndRetainUnobservedPossibilities()
    {
        var clock = new M12UseClock(); var history = new M12ItemUsePossibilities(clock);
        for (int item = 1; item <= 24; item++) history.Observe(item, item, M12PossibleUseOrigin.SscInventoryExportAttempt);
        var full = history.Capture(); Assert.That(full.Observed.Length, Is.EqualTo(16)); Assert.That(full.LostEntries, Is.True);
        Assert.That(full.Complete, Is.False); Assert.That(full.UnobservedStartPossible, Is.True);
        clock.Advance(M12ItemUsePossibilities.Retention);
        var expired = history.Capture(); Assert.That(expired.Observed, Is.Empty); Assert.That(expired.LostEntries, Is.True);
        Assert.That(expired.Complete, Is.False); Assert.That(expired.UnobservedStartPossible, Is.True);
        history.Observe(ItemID.SolarTablet, 0, M12PossibleUseOrigin.MessageArrivalCurrentItem);
        Assert.That(history.Capture().LostEntries, Is.True, "New data cannot reconstruct dropped old starts.");
    }

    [TestCase(125, 544)] [TestCase(126, 544)] [TestCase(127, 557)] [TestCase(134, 556)]
    public void M12MechanicalGroupHasActualPayloadEntryAndIndependentVariantHistoryGap(short summon, int item)
    {
        var enabled = Evaluate(summon);
        Assert.That(enabled.RuleId, Is.EqualTo(M12NaturalMechanicalRules.RuleId)); Assert.That(enabled.Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(enabled.Facts["itemId"], Is.EqualTo(item.ToString()));
        bool oldWorthy = Main.getGoodWorld;
        try
        {
            Main.remixWorld = Main.getGoodWorld = true; Reconnect();
            actor.TPlayer.inventory[0].SetDefaults(item);
            var unknown = Evaluate(summon);
            Assert.That(unknown.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(unknown.Reason, Is.EqualTo("mechanical-client-variant-refresh-and-use-source-history-unproved"));
            Assert.That(unknown.Facts["clientVariantHistoryComplete"], Is.EqualTo("False"));
            Assert.That(unknown.Facts["possibleClientVariants"], Is.EqualTo("default,DisabledBossSummonVariant"));
            Assert.That(Evaluate(-16).Verdict, Is.EqualTo(Verdict.Pass));
        }
        finally { Main.getGoodWorld = oldWorthy; }
    }

    private sealed class M12UseClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }
}
