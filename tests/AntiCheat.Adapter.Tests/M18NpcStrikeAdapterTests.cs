using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using System.Collections.Immutable;
using NUnit.Framework;
using Terraria;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [Test]
    public void M18SuccessfulLoginClearsEarlierUnauthenticatedControlHistory()
    {
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint,
            M18NpcStrikeQueueOptions.TestLabCandidate);
        causes.Install();
        causes.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, causes);
        try
        {
            var queue = typeof(M7NpcStrikeCauseContexts).GetField("strikeQueue", Private)!.GetValue(causes)!;
            queue.GetType().GetMethod("ObservePlayerControls")!.Invoke(queue,
            [1L, new M18NpcPlayerControlObservation(session, 0, 640f, 480f,
                PositionSnapshotComplete: true, ParseComplete: true, ClientOrigin: true,
                BeforeSideEffects: true, AttributionComplete: false, AlreadyCancelled: false)]);
            var tracker = queue.GetType().GetField("_butcherBehavior", Private)!.GetValue(queue)!;
            var history = (Array)tracker.GetType().GetField("sessions", Private)!.GetValue(tracker)!;
            Assert.That(history.GetValue(Slot), Is.Not.Null);

            var actor = TShockAPI.TShock.Players[Slot]!;
            typeof(AntiCheatPlugin).GetMethod("OnLogin", Private)!.Invoke(plugin,
                [new TShockAPI.Hooks.PlayerPostLoginEventArgs(actor)]);
            Assert.That(history.GetValue(Slot), Is.Null);
            Assert.That(engine.GetSession(session)?.AccountId, Is.EqualTo(6207));
            Assert.That(engine.SanctionCount, Is.Zero);
        }
        finally { typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, null); }
    }

    [Test]
    public async Task M18ProvenCandidateCannotSanctionReusedAccountBinding()
    {
        var actor = TShockAPI.TShock.Players[Slot]!;
        var original = actor.Account;
        var current = (Array)typeof(AntiCheatPlugin).GetField("_bindings", Private)!.GetValue(plugin)!;
        var bound = current.GetValue(Slot)!;
        actor.Account = new TShockAPI.DB.UserAccount { ID = 6208, Name = "new-slot-account" };
        try
        {
            var result = new BusinessRuleResult(M18NpcStrikeQueueRules.RuleId,
                M18NpcStrikeQueueRules.Version, ControlAction.Block, Verdict.ProvenCheat,
                "closed-sequence", true, true, ImmutableDictionary<string, string>.Empty);
            var apply = typeof(AntiCheatPlugin).GetMethod("ApplyBusinessResult", Private)!;
            Assert.That(apply.Invoke(plugin, [ (byte)28, false, bound, result, null ]), Is.False);
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            Assert.That((await engine.PumpAsync()).Applied, Is.Zero);
            Assert.That(store.Bans, Is.Zero);
        }
        finally { actor.Account = original; }
    }

    [Test]
    public void M18BoundedStrikeBudgetCancelsBeforeReceiverWithoutAccountSanction()
    {
        var options = new M18NpcStrikeQueueOptions
        {
            Enabled = true, WindowTicks = 3, LowDamageMaximum = 1,
            PerTargetLowDamageLimit = 2, PerSessionLowDamageLimit = 4,
            LowDamageRingCapacity = 4, HighSampleCapacity = 2,
        };
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint, options);
        causes.Install();
        causes.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, causes);
        try
        {
            Assert.That(Root(Body(1000)).Handled, Is.False);
            Assert.That(Root(Body(1)).Handled, Is.True);
            Assert.That(Root(Body(1)).Handled, Is.True);
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            Assert.That(causes.CaptureClientStrike(session), Is.Null);
            causes.Forget(session);
            Assert.That(Root(Body(1)).Handled, Is.False);
        }
        finally { typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, null); }
    }

    [Test]
    public void M18RejectedOutOfRangeStrikeClearsPriorNativeTransaction()
    {
        using var causes = new M7NpcStrikeCauseContexts(TargetRuntime.Fingerprint,
            M18NpcStrikeQueueOptions.TestLabCandidate);
        causes.Install();
        causes.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, causes);
        try
        {
            Assert.That(Root(Body()).Handled, Is.False);
            var transactions = (Array)typeof(M7NpcStrikeCauseContexts)
                .GetField("strikeTransactions", Private)!.GetValue(causes)!;
            Assert.That(transactions.GetValue(Slot), Is.Not.Null);
            var invalid = Body();
            invalid[0] = (byte)Main.maxNPCs;
            Assert.That(Root(invalid).Handled, Is.True);
            Assert.That(transactions.GetValue(Slot), Is.Null);
            Target.StrikeNPC(9, 0, 1, false, true, Slot, Main.player[Slot]);
            Assert.That(causes.CaptureClientStrike(session), Is.Null);
        }
        finally { typeof(AntiCheatPlugin).GetField("_npcStrikeCauses", Private)!.SetValue(plugin, null); }
    }
}
