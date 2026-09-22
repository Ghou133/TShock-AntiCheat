using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>Records actual server-local strike origins independently from forwarded client28 requests.
/// It does not turn an observed nearby/native hit into authorization for another client hit.</summary>
public sealed partial class M7NpcStrikeCauseContexts : IDisposable
{
    private sealed record NativeCause(long Epoch, long Tick, NPC Target, int Generation, int Owner,
        string SourceKind, int SourceType, uint? ProjectileKey, int Damage, bool Critical);
    private readonly NativeCause?[] causes = new NativeCause?[200];
    public const int TtlTicks = 120;
    private readonly string fingerprint;
    private long epoch = -1, tick;
    private int updateThread, invalidationPending;
    private volatile bool installed, failed;
    private readonly M18NpcStrikeQueue strikeQueue;
    private readonly M18ImportantItemRewardObserver importantItemRewards;
    public Action<Exception>? IntegrityFault { get; set; }
    public Action<Exception>? OptionalRewardObservationFault
    {
        get => importantItemRewards.ObservationFault;
        set => importantItemRewards.ObservationFault = value;
    }
    public Action<M18ImportantItemRewardObservation>? ImportantItemRewardObserved
    {
        get => importantItemRewards.ObservationRecorded;
        set => importantItemRewards.ObservationRecorded = value;
    }
    public bool ImportantItemRewardObservationHealthy => importantItemRewards.ObservationHealthy;
    public string? ImportantItemRewardObservationFaultType => importantItemRewards.LastObservationFaultType;
    public string? ImportantItemRewardObservationFaultMessage => importantItemRewards.LastObservationFaultMessage;
    public Action<M18NpcStrikeObservation, M18NpcStrikeQueueDecision>? StrikeObservationRecorded
    {
        get => strikeQueue.ObservationRecorded;
        set => strikeQueue.ObservationRecorded = value;
    }
    /// <summary>Raised after the native receiver and its packet28 relay have
    /// closed a client strike transaction. The plugin uses only a complete
    /// post-native candidate for account enforcement; normal completions are
    /// retained as comparison evidence.</summary>
    public Action<M8NpcStrikeCompletion, M18NpcStrikeQueueDecision>? StrikeCompletionRecorded { get; set; }
    /// <summary>Optional native summon context. It is deliberately auxiliary:
    /// the Butcher sequence remains effective when a legal matching summon is
    /// present.</summary>
    public Func<SessionKey, M18NpcSummonAuxiliaryContext?>? SummonAuxiliaryContext { get; set; }
    public M7NpcStrikeCauseContexts(string fingerprint, M18NpcStrikeQueueOptions? queueOptions = null,
        M18ImportantItemQueueOptions? importantItemQueueOptions = null,
        IReadOnlyDictionary<int, string>? importantItemDefinitions = null)
    {
        this.fingerprint = fingerprint;
        strikeQueue = new(queueOptions ?? M18NpcStrikeQueueOptions.Disabled);
        importantItemRewards = new(importantItemQueueOptions ?? M18ImportantItemQueueOptions.Disabled,
            importantItemDefinitions ?? new Dictionary<int, string>());
    }
    public bool StrikeQueueEnabled => strikeQueue.Enabled;
    public bool ImportantItemRewardEnabled => importantItemRewards.Enabled;

    public void ReportOptionalRewardObservationFault(Exception error)
        => importantItemRewards.ReportObservationFault(error);

    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.NPC.StrikeNPC += OnStrike;
        HookEvents.Terraria.NetMessage.SendData += OnStrikeRelay;
        HookEvents.Terraria.NetMessage.SendData += OnImportantItemRewardSend;
        HookEvents.Terraria.NPC.NPCLoot += OnStrikeLoot;
        installed = true;
        try { InstallStrikeScope(); }
        catch { failed = true; Dispose(); throw; }
    }
    public void Tick(long worldEpoch)
    {
        if (!installed || failed) return;
        Volatile.Write(ref updateThread, Environment.CurrentManagedThreadId);
        ApplyPendingInvalidation();
        if (epoch != worldEpoch) { epoch = worldEpoch; Array.Clear(causes); Array.Clear(strikeCompletions); }
        strikeQueue.AdvanceWorld(worldEpoch);
        importantItemRewards.Tick(worldEpoch);
        tick++;
        Array.Clear(strikeTransactions);
    }
    public void Dispose()
    {
        if (installed) HookEvents.Terraria.NPC.StrikeNPC -= OnStrike;
        if (installed) HookEvents.Terraria.NetMessage.SendData -= OnStrikeRelay;
        if (installed) HookEvents.Terraria.NetMessage.SendData -= OnImportantItemRewardSend;
        if (installed) HookEvents.Terraria.NPC.NPCLoot -= OnStrikeLoot;
        strikeScopeHook?.Dispose(); strikeScopeHook = null;
        installed = false; Array.Clear(causes);
        strikeQueue.Reset();
        importantItemRewards.Reset();
        Array.Clear(strikeTransactions); Array.Clear(strikeCompletions);
    }

    public void Forget(SessionKey session)
    {
        strikeQueue.Forget(session);
        importantItemRewards.Forget(session);
        CancelPendingClientStrike(session);
    }

    public M18ImportantItemRewardObservation? CaptureClientImportantItemReward(SessionKey session)
        => importantItemRewards.CaptureLatest(session);

    /// <summary>Feeds a parsed packet13 control declaration to the finite
    /// position tracker before the packet reaches the ordinary M2 producers.</summary>
    public void ObservePlayerControls(M2Packet packet, SessionKey session, TSPlayer actor,
        bool alreadyCancelled)
    {
        if (!installed || failed || packet.Kind != M2PacketKind.PlayerUpdate ||
            packet.Payload.Length < 14 || packet.Payload[0] != session.Slot)
            return;

        bool attributed = actor.IsLoggedIn && actor.Account is not null &&
            actor.Account.ID > 0 && actor.Index == session.Slot;
        strikeQueue.ObservePlayerControls(tick, new(
            session,
            attributed ? actor.Account!.ID : 0,
            M2PacketReader.Single(packet.Payload, 6),
            M2PacketReader.Single(packet.Payload, 10),
            float.IsFinite(M2PacketReader.Single(packet.Payload, 6)) &&
                float.IsFinite(M2PacketReader.Single(packet.Payload, 10)),
            ParseComplete: true,
            ClientOrigin: true,
            BeforeSideEffects: true,
            AttributionComplete: attributed,
            AlreadyCancelled: alreadyCancelled));
    }

    private void OnStrike(NPC target, HookEvents.Terraria.NPC.StrikeNPCEventArgs args)
    {
        ObserveClientStrike(target, args);
        if (!installed || failed || args.fromNet || !args.ContinueExecution) return;
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread))
        {
            // Locked TShock console Butcher calls StrikeNPC on Server Input Thread.
            // Do not inspect its mutable world/source objects or permanently disable later observations.
            // One coalescing signal invalidates old causes on the next verified context; no off-thread queue/writeback.
            Interlocked.Exchange(ref invalidationPending, 1);
            return;
        }
        if (Main.netMode != 2) return;
        try
        {
            if (epoch <= 0 || fingerprint != TargetRuntime.Fingerprint)
                throw new InvalidOperationException("Native NPC cause observed outside the verified target update context.");
            ApplyPendingInvalidation();
            if ((uint)target.whoAmI >= causes.Length || !ReferenceEquals(Main.npc[target.whoAmI], target)) return;
            string source = args.entity switch { Projectile => "server-native-projectile", Player => "server-native-player", null => "server-native-unspecified", _ => "server-native-other-entity" };
            int sourceType = args.entity is Projectile projectile ? projectile.type : -1;
            uint? key = args.entity is Projectile keyed ? keyed.key.bits : null;
            causes[target.whoAmI] = new(epoch, tick, target, target.generation, args.owner, source, sourceType, key, args.Damage, args.crit);
        }
        catch (Exception error) { failed = true; Dispose(); IntegrityFault?.Invoke(error); }
    }

    public BusinessRuleResult Enrich(BusinessRuleResult result, NpcStrikeObservation request, SessionKey session, TSPlayer actor)
    {
        // A structural rejection (including a stale-generation no-op) cannot
        // be allowed to leave an earlier same-session transaction available to
        // a later native call. This is deliberately done before the context
        // checks below because an out-of-range rejected request has no target
        // context to enrich.
        bool structuralStop = result.RuleId == M6CombatRules.StrikeRuleId && result.Action != ControlAction.Unknown;
        if (structuralStop)
            CancelPendingClientStrike(session);

        if (!installed || failed || epoch != session.WorldEpoch || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) ||
            actor.Index != session.Slot ||
            (uint)request.TargetSlot >= causes.Length) return result;
        ApplyPendingInvalidation();
        if (Main.npc[request.TargetSlot] is not { } target) return result;
        var accountId = actor.Account?.ID ?? 0;
        var priorCompletion = CaptureClientStrike(session);
        bool targetIsKnownLegalException = target.type == NPCID.TargetDummy;
        bool actorIdentityComplete = actor.IsLoggedIn && actor.Account is not null && actor.Index == session.Slot;
        // TShockAPI's permission lookup assumes an initialized logged-in
        // account. Keep unauthenticated/partial actors in the Unknown path and
        // never let a diagnostic permission query become a parser crash.
        bool actorHasStrikeBypass = actorIdentityComplete &&
            (actor.HasPermission("anticheat.bypass") || actor.HasPermission(Permissions.bypassssc));
        // This is the complete producer for the queue's
        // LegalExceptionsExcluded bit. It excludes only the currently known
        // target-dummy and actor-bypass cases; it is not a claim that weapon,
        // projectile, item, buff, or plugin-effect provenance is complete.
        bool legalExceptionsExcluded = actorIdentityComplete &&
            !targetIsKnownLegalException && !actorHasStrikeBypass;
        var strikeStage = !Main.hardMode ? M18NpcStrikeStage.PreHardmode :
            NPC.downedMoonlord ? M18NpcStrikeStage.PostMoonlord :
            NPC.downedPlantBoss ? M18NpcStrikeStage.PostPlantera : M18NpcStrikeStage.Hardmode;
        bool stageSnapshotComplete = Main.netMode == 2 && Main.worldID > 0;
        M18NpcSummonAuxiliaryContext summonAuxiliary = default;
        try { summonAuxiliary = SummonAuxiliaryContext?.Invoke(session) ?? default; }
        catch { /* Auxiliary source context cannot affect packet admission. */ }

        // A post-native/dead target is no longer an admissible packet-time
        // queue target. Keep a bounded diagnostic projection for the closed
        // transaction, but never create a fresh queue state from defaulted
        // target fields. This prevents an after-death callback from becoming
        // business evidence with an unrelated target/generation.
        if (target.whoAmI != request.TargetSlot || !target.active || target.life <= 0 ||
            target.generation != request.TargetGeneration)
        {
            var closedAllowed = BuildClientStrikeResults(session);
            var closedFacts = result.Facts
                .SetItem("causeProducer", nameof(M7NpcStrikeCauseContexts))
                .SetItem("targetAtRequest", $"type={target.type};life={target.life};generation={target.generation};requestedGeneration={request.TargetGeneration}")
                .SetItem("targetFactProvenance", "current-server-NPC-slot-and-generation-closed-before-new-admission")
                .SetItem("clientAllowedDamageResults", $"wire={closedAllowed.AllowedResults};complete={closedAllowed.Complete};nativeCauseIsAuthorization=False")
                .SetItem("exclusiveAttackCauseAvailable", "False")
                .SetItem("recentServerNativeCause", "none-for-current-target-generation")
                .SetItem("nativeCauseIsClientAuthorization", "False")
                .SetItem("strikeLegalExceptionGate", legalExceptionsExcluded ?
                    "closed;source=M7NpcStrikeCauseContexts:TargetDummy-or-anticheat.bypass-or-bypassssc" :
                    "open;source=M7NpcStrikeCauseContexts:TargetDummy-or-anticheat.bypass-or-bypassssc")
                .SetItem("strikeExtremeLineProof", "closed-target-no-new-admission;weapon-and-effect-provenance-missing")
                .SetItem("strikeQueue", "reason=npc-strike-post-native-target-closed")
                .SetItem("strikeQueueClassification", "post-native-closed-target-no-new-admission")
                .SetItem("strikeQueueActionContract", "no-new-admission-no-sanction");
            return result with { Facts = closedFacts };
        }

        var queueDecision = strikeQueue.Observe(tick, new M18NpcStrikeObservation(
            session,
            accountId,
            request.TargetSlot,
            request.TargetGeneration,
            target.type,
            request.Damage,
            M8DamageAllowedResults.NpcReceiverDamage((short)request.Damage),
            TargetSnapshotComplete: target.whoAmI == request.TargetSlot && target.active && target.life > 0,
            TargetActive: target.active,
            TargetGenerationMatchesCurrent: target.generation == request.TargetGeneration,
            ClientOrigin: true,
            AttributionComplete: actorIdentityComplete,
            LegalExceptionsExcluded: legalExceptionsExcluded)
        {
            WorldId = Main.worldID,
            Stage = strikeStage,
            StageSnapshotComplete = stageSnapshotComplete,
            TargetLife = target.life,
            TargetLifeMax = target.lifeMax,
            TargetFriendly = target.friendly,
            TargetDummy = targetIsKnownLegalException,
            TargetX = target.position.X,
            TargetY = target.position.Y,
            TargetPositionSnapshotComplete = target.whoAmI == request.TargetSlot &&
                float.IsFinite(target.position.X) && float.IsFinite(target.position.Y),
            WireKnockback = request.Knockback,
            WireDirection = request.EncodedDirection,
            WireCriticalFlag = request.CriticalFlag,
            AlreadyCancelled = result.Action != ControlAction.Unknown,
            SummonContextComplete = summonAuxiliary.SnapshotComplete,
            SummonMaintenanceBuffObserved = summonAuxiliary.MaintenanceBuffObserved,
            MatchingSummonEntityObserved = summonAuxiliary.MatchingObservedEntity,
            MatchingSummonEntityCount = summonAuxiliary.MatchingObservedEntityCount,
        });
        if (structuralStop || queueDecision.Action == ControlAction.Block)
        {
            CancelPendingClientStrike(session);
            if (queueDecision.IsButcherPatternBlock)
                strikeQueue.SuppressPendingBehavior(session);
            else
                strikeQueue.CancelPendingBehavior(session);
        }
        else
            BeginClientStrike(request, session, actor, target, queueDecision,
                legalExceptionsExcluded);
        var native = causes[request.TargetSlot];
        var allowed = BuildClientStrikeResults(session);
        bool liveCause = Volatile.Read(ref invalidationPending) == 0 && native is not null && native.Epoch == epoch && tick - native.Tick <= TtlTicks &&
            native.Generation == target.generation && ReferenceEquals(native.Target, target);
        var facts = result.Facts.Remove("nativeCauseCoordinates").Remove("nativeCauseInputDamage").Remove("nativeCauseCritical")
            .SetItem("causeProducer", nameof(M7NpcStrikeCauseContexts))
            .SetItem("targetAtRequest", $"type={target.type};life={target.life};hitbox={target.position.X},{target.position.Y},{target.width},{target.height}")
            .SetItem("targetFactProvenance", "current-server-NPC-slot-and-generation")
            .SetItem("actorPositionProvenance", queueDecision.ButcherPositionContextComplete
                ? "packet13-target-control-and-saved-return-finite-sequence-not-attack-authorization"
                : "accepted-client-movement-not-exclusive-attack-range")
            .Remove("actorPositionAtRequest")
            .SetItem("clientAllowedDamageResults", $"wire={allowed.AllowedResults};complete={allowed.Complete};nativeCauseIsAuthorization=False")
            .SetItem("exclusiveAttackCauseAvailable", "False")
            .SetItem("recentServerNativeCause", liveCause ? native!.SourceKind : "none-for-current-target-generation")
            .SetItem("nativeCauseIsClientAuthorization", "False")
            .SetItem("strikeLegalExceptionGate", legalExceptionsExcluded ?
                "closed;source=M7NpcStrikeCauseContexts:TargetDummy-or-anticheat.bypass-or-bypassssc" :
                "open;source=M7NpcStrikeCauseContexts:TargetDummy-or-anticheat.bypass-or-bypassssc")
            .SetItem("strikeExtremeLineProof", "wire-target-stage-state;weapon-and-effect-provenance-missing");
        if (liveCause)
            facts = facts.SetItem("nativeCauseCoordinates", $"tick={native!.Tick};owner={native.Owner};sourceType={native.SourceType};projectileKey={native.ProjectileKey?.ToString() ?? "none"}")
                .SetItem("nativeCauseInputDamage", native.Damage.ToString()).SetItem("nativeCauseCritical", native.Critical.ToString());
        if (priorCompletion is { } completion)
            facts = facts.SetItem("priorClientStrikeCompletion",
                $"target={completion.TargetSlot};generation={completion.TargetGeneration};life={completion.LifeBefore}->{completion.LifeAfter};" +
                    $"native={completion.NativeStrikeEntryObserved};loot={completion.LootMethodEntryObserved};relay={completion.RelayAttemptObserved}");
        if (strikeQueue.Enabled)
            facts = M18NpcStrikeQueueRules.AddFacts(facts, queueDecision);
        var enriched = result with { Facts = facts };
        if (queueDecision.IsResourceBlock || queueDecision.IsExtremeDamageBlock)
        {
            var queueInput = new RuleInputContext(
                session,
                fingerprint,
                TargetRuntime.Fingerprint,
                ParserComplete: true,
                SnapshotComplete: true,
                AttributionComplete: queueDecision.Enabled,
                ExceptionsExcluded: legalExceptionsExcluded,
                ClientOrigin: true);
            // A resource/extreme queue stop is still only an additional
            // bounded observation. Preserve an earlier structural M6 result
            // and its reason even when the queue is the result that requests
            // cancellation.
            var queueResult = M18NpcStrikeQueueRules.Observe(queueInput, facts, queueDecision);
            return M18NpcStrikeQueueRules.MergeWithPrior(enriched, queueResult);
        }

        if (strikeQueue.Enabled)
        {
            var queueInput = new RuleInputContext(
                session,
                fingerprint,
                TargetRuntime.Fingerprint,
                ParserComplete: true,
                SnapshotComplete: true,
                AttributionComplete: queueDecision.Enabled,
                ExceptionsExcluded: legalExceptionsExcluded,
                ClientOrigin: true);
            // The queue result is a separate bounded business observation. It
            // may stop an otherwise Unknown request, but it cannot overwrite
            // the older structural Pass/Block decision.
            var queueResult = M18NpcStrikeQueueRules.Observe(queueInput, facts, queueDecision);
            return M18NpcStrikeQueueRules.MergeWithPrior(enriched, queueResult);
        }

        return enriched;
    }

    private void ApplyPendingInvalidation()
    {
        if (Interlocked.Exchange(ref invalidationPending, 0) != 0)
        { Array.Clear(causes); Array.Clear(strikeTransactions); Array.Clear(strikeCompletions); }
    }
}
