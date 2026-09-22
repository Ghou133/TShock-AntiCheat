using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TShockAPI;
using MonoMod.RuntimeDetour;

namespace AntiCheat.Plugin.TShock;

public sealed record M8NpcStrikeCompletion(SessionKey Session, long AccountId, long Tick, int TargetSlot, int TargetGeneration,
    int TargetType, int WireDamage, int ReceiverDamage, int LifeBefore, int LifeAfter, bool ActiveAfter,
    bool NativeStrikeEntryObserved, bool LootMethodEntryObserved, bool RelayAttemptObserved,
    bool Critical, int NativeDefense, float NativeTakenMultiplier,
    int TargetLifeMax, bool TargetFriendly, bool TargetDummy,
    bool AttributionComplete, bool LegalExceptionsExcluded,
    M18NpcStrikeQueueDecision InitialQueueDecision)
{
    public long ObservedLifeChange => (long)LifeBefore - LifeAfter;
    public bool ObservedDeath => LifeBefore > 0 && LifeAfter <= 0;
    public bool ClientAttackAuthorized => false;
    public bool LootItemsVerified => false;
}

public sealed partial class M7NpcStrikeCauseContexts
{
    private sealed class StrikeTransaction(SessionKey session, NpcStrikeObservation request, NPC npc, long tick,
        long accountId, M18ImportantItemState[] importantItemBaseline,
        M18NpcStrikeQueueDecision queueDecision, bool attributionComplete,
        bool legalExceptionsExcluded)
    {
        public SessionKey Session = session;
        public NpcStrikeObservation Request = request;
        public NPC Target = npc;
        public long Tick = tick;
        public long AccountId = accountId;
        public M18ImportantItemState[] ImportantItemBaseline = importantItemBaseline;
        public int LifeBefore = npc.life, TargetType = npc.type, Defense = npc.defense;
        public int LifeMax = npc.lifeMax;
        public bool TargetFriendly = npc.friendly;
        public bool TargetDummy = npc.type == Terraria.ID.NPCID.TargetDummy;
        public bool AttributionComplete = attributionComplete;
        public bool LegalExceptionsExcluded = legalExceptionsExcluded;
        public M18NpcStrikeQueueDecision QueueDecision = queueDecision;
        public float TakenMultiplier = npc.takenDamageMultiplier;
        public bool NativeSeen, LootSeen;
    }
    private readonly StrikeTransaction?[] strikeTransactions = new StrikeTransaction?[255];
    private readonly M8NpcStrikeCompletion?[] strikeCompletions = new M8NpcStrikeCompletion?[255];
    private Hook? strikeScopeHook;
    private delegate int NativeStrike(NPC target, int damage, float knockBack, int hitDirection, bool critical,
        bool fromNet, int owner, Entity? entity);
    private delegate int AroundNativeStrike(NativeStrike original, NPC target, int damage, float knockBack, int hitDirection,
        bool critical, bool fromNet, int owner, Entity? entity);
    private sealed record StrikeScope(M7NpcStrikeCauseContexts Observer, StrikeTransaction Transaction);
    [ThreadStatic] private static StrikeScope? currentStrike;
    public long ClientStrikesObserved { get; private set; }
    public long ClientStrikeRelaysObserved { get; private set; }

    private void InstallStrikeScope()
    {
        var method = typeof(NPC).GetMethod(nameof(NPC.StrikeNPC),
            [typeof(int), typeof(float), typeof(int), typeof(bool), typeof(bool), typeof(int), typeof(Entity)])
            ?? throw new MissingMethodException(nameof(NPC.StrikeNPC));
        strikeScopeHook = new Hook(method, (AroundNativeStrike)WithinNativeStrike);
    }

    private int WithinNativeStrike(NativeStrike original, NPC target, int damage, float knockBack, int hitDirection,
        bool critical, bool fromNet, int owner, Entity? entity)
    {
        var previous = currentStrike;
        currentStrike = null; // A nested server/plugin strike never inherits the client request's attribution.
        bool returned = false;
        try
        {
            if (installed && !failed && fromNet && Environment.CurrentManagedThreadId == Volatile.Read(ref updateThread) &&
                (uint)owner < strikeTransactions.Length && strikeTransactions[owner] is { } transaction && SameTransaction(transaction, target))
                currentStrike = new(this, transaction);
            int result = original(target, damage, knockBack, hitDirection, critical, fromNet, owner, entity);
            returned = true;
            return result;
        }
        finally
        {
            if (returned && currentStrike is { } scope && ReferenceEquals(scope.Observer, this) &&
                scope.Transaction is { NativeSeen: true, LootSeen: true } completed)
            {
                try
                {
                    importantItemRewards.CaptureReward(completed.Session, completed.AccountId,
                        completed.Target, completed.Request.TargetGeneration,
                        completed.LootSeen, completed.ImportantItemBaseline);
                }
                catch (Exception error)
                {
                    // Reward observation must never alter native combat/loot. The
                    // existing native strike context remains the authority if a
                    // bounded optional reward sample cannot be serialized.
                    importantItemRewards.ReportObservationFault(error);
                }
            }
            currentStrike = previous;
        }
    }

    private void BeginClientStrike(NpcStrikeObservation request, SessionKey session, TSPlayer actor, NPC npc,
        M18NpcStrikeQueueDecision queueDecision, bool legalExceptionsExcluded)
    {
        if ((uint)session.Slot >= strikeTransactions.Length) return;
        strikeTransactions[session.Slot] = null;
        if (fingerprint != TargetRuntime.Fingerprint || actor.Index != session.Slot || !actor.IsLoggedIn ||
            actor.Account is null || Main.netMode != 2 || epoch != session.WorldEpoch || !npc.active || npc.life <= 0 ||
            npc.generation != request.TargetGeneration || request.Damage is < short.MinValue or > short.MaxValue ||
            request.CriticalFlag is < 0 or > 1 || request.EncodedDirection is < 0 or > 2 || !float.IsFinite(request.Knockback)) return;
        // Start the bounded reward window only after every request invariant has
        // passed. A structural M6 rejection must not clear a prior observation.
        importantItemRewards.Begin(session);
        strikeTransactions[session.Slot] = new(session, request, npc, tick, actor.Account!.ID,
            importantItemRewards.CaptureBaseline(), queueDecision,
            attributionComplete: actor.IsLoggedIn && actor.Account is not null && actor.Index == session.Slot,
            legalExceptionsExcluded);
        ClientStrikesObserved = Saturate(ClientStrikesObserved);
    }

    private void ObserveClientStrike(NPC target, HookEvents.Terraria.NPC.StrikeNPCEventArgs args)
    {
        if (!installed || failed || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) ||
            !args.ContinueExecution || !args.fromNet || Main.netMode != 2 || (uint)args.owner >= strikeTransactions.Length ||
            strikeTransactions[args.owner] is not { } transaction) return;
        var request = transaction.Request;
        if (!SameTransaction(transaction, target) || args.Damage != M8DamageAllowedResults.NpcReceiverDamage((short)request.Damage) ||
            args.knockBack != request.Knockback || args.hitDirection != request.EncodedDirection - 1 || args.crit != (request.CriticalFlag == 1))
        { strikeTransactions[args.owner] = null; return; }
        // This is the actual receiver invocation, after native negative-wire clamping. Its Player
        // argument remains a sender placeholder; it cannot establish a melee or projectile cause.
        transaction.NativeSeen = true;
    }

    private void OnStrikeLoot(NPC target, HookEvents.Terraria.NPC.NPCLootEventArgs args)
    {
        if (!installed || failed || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) || !args.ContinueExecution) return;
        if (currentStrike is { } scope && ReferenceEquals(scope.Observer, this) && scope.Transaction is { NativeSeen: true } transaction &&
            SameTransaction(transaction, target)) transaction.LootSeen = true;
    }

    private void OnStrikeRelay(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!installed || failed || Main.netMode != 2 || !args.ContinueExecution || args.msgType != 28 ||
            Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) || args.remoteClient != -1 ||
            (uint)args.ignoreClient >= strikeTransactions.Length || strikeTransactions[args.ignoreClient] is not { NativeSeen: true } transaction) return;
        var request = transaction.Request;
        if (args.number != request.TargetSlot || args.number2 != M8DamageAllowedResults.NpcReceiverDamage((short)request.Damage) ||
            args.number3 != request.Knockback || args.number4 != request.EncodedDirection - 1 || args.number5 != request.CriticalFlag ||
            !SameTransaction(transaction, transaction.Target)) return;
        var npc = transaction.Target;
        strikeTransactions[args.ignoreClient] = null;
        var completion = new M8NpcStrikeCompletion(transaction.Session, transaction.AccountId, tick,
            request.TargetSlot, request.TargetGeneration,
            transaction.TargetType, request.Damage, M8DamageAllowedResults.NpcReceiverDamage((short)request.Damage),
            transaction.LifeBefore, npc.life, npc.active, true, transaction.LootSeen, true,
            request.CriticalFlag == 1, transaction.Defense, transaction.TakenMultiplier,
            transaction.LifeMax, transaction.TargetFriendly, transaction.TargetDummy,
            transaction.AttributionComplete, transaction.LegalExceptionsExcluded,
            transaction.QueueDecision);
        strikeCompletions[args.ignoreClient] = completion;
        var postNativeObservation = new M18NpcStrikePostNativeObservation(
            completion.Session,
            transaction.AccountId,
            completion.TargetSlot,
            completion.TargetGeneration,
            completion.TargetType,
            completion.WireDamage,
            completion.ReceiverDamage,
            completion.TargetLifeMax,
            completion.LifeBefore,
            completion.LifeAfter,
            completion.TargetFriendly,
            completion.TargetDummy,
            completion.InitialQueueDecision.Stage,
            completion.InitialQueueDecision.StageSnapshotComplete,
            completion.InitialQueueDecision.ButcherPositionContextComplete,
            completion.InitialQueueDecision.ButcherPreControlAtTarget,
            completion.InitialQueueDecision.ButcherControlJumpDetected,
            ClientOrigin: true,
            completion.AttributionComplete,
            completion.LegalExceptionsExcluded,
            completion.NativeStrikeEntryObserved,
            completion.LootMethodEntryObserved,
            completion.RelayAttemptObserved);
        var postNativeDecision = strikeQueue.ObservePostNative(tick, postNativeObservation,
            completion.InitialQueueDecision);
        try { StrikeCompletionRecorded?.Invoke(completion, postNativeDecision); }
        catch { /* Post-native evidence must never alter the native relay. */ }
        ClientStrikeRelaysObserved = Saturate(ClientStrikeRelaysObserved);
    }

    private void OnImportantItemRewardSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!installed || failed || !importantItemRewards.Enabled || Main.netMode != 2 ||
            !args.ContinueExecution || args.msgType != 90 ||
            Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) ||
            currentStrike is not { } scope || scope.Transaction is not { NativeSeen: true, LootSeen: true } transaction ||
            args.remoteClient != transaction.Session.Slot || !SameTransaction(transaction, transaction.Target))
            return;

        try
        {
            importantItemRewards.CaptureInstancedReward(
                transaction.Session,
                transaction.AccountId,
                transaction.Target,
                transaction.Request.TargetGeneration,
                transaction.LootSeen,
                args.number);
        }
        catch (Exception error)
        {
            // Packet90 observation is optional and has its own health latch.
            // A malformed reward sample must not dispose the shared strike queue.
            importantItemRewards.ReportObservationFault(error);
        }
    }

    private bool SameTransaction(StrikeTransaction transaction, NPC target) => transaction.Tick == tick &&
        transaction.Session.WorldEpoch == epoch && transaction.Request.TargetGeneration == target.generation &&
        transaction.Request.TargetSlot == target.whoAmI && ReferenceEquals(transaction.Target, target) &&
        (uint)target.whoAmI < Main.npc.Length && ReferenceEquals(Main.npc[target.whoAmI], target);

    public M8NpcStrikeCompletion? CaptureClientStrike(SessionKey session)
    {
        if (!installed || failed || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) ||
            Volatile.Read(ref invalidationPending) != 0 || (uint)session.Slot >= strikeCompletions.Length) return null;
        var completed = strikeCompletions[session.Slot];
        return completed?.Session == session && completed.Session.WorldEpoch == epoch && tick - completed.Tick <= TtlTicks ? completed : null;
    }

    /// <summary>Cancel a pending or completed sample for this exact session before a
    /// candidate resource stop-loss prevents the current packet from reaching the receiver.</summary>
    private void CancelPendingClientStrike(SessionKey session)
    {
        if ((uint)session.Slot >= strikeTransactions.Length)
            return;
        if (strikeTransactions[session.Slot]?.Session == session)
            strikeTransactions[session.Slot] = null;
        if (strikeCompletions[session.Slot]?.Session == session)
            strikeCompletions[session.Slot] = null;
    }

    /// <summary>Packet28 is the union of melee-without27, projectile, indirect and host-export
    /// branches. A server-local strike is evidence of an input, never authorization for a client28.</summary>
    public M8DamageAllowedResults BuildClientStrikeResults(SessionKey session)
    {
        // Consumption samples belong exclusively to CaptureClientStrike. Previously accepted
        // client damage must never enter a legitimate-source set, even if NPC life really changed.
        return M8DamageAllowedResults.Build([],
            ["ordinary-melee-results-without-projectile-key-not-complete", "ordinary-projectile-and-indirect-results-not-complete",
                "account-item-and-effect-import-baseline-not-established"]);
    }

    private static long Saturate(long value) => value == long.MaxValue ? value : value + 1;
}
