using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
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
    public Action<Exception>? IntegrityFault { get; set; }
    public M7NpcStrikeCauseContexts(string fingerprint) => this.fingerprint = fingerprint;

    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.NPC.StrikeNPC += OnStrike;
        HookEvents.Terraria.NetMessage.SendData += OnStrikeRelay;
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
        tick++;
        Array.Clear(strikeTransactions);
    }
    public void Dispose()
    {
        if (installed) HookEvents.Terraria.NPC.StrikeNPC -= OnStrike;
        if (installed) HookEvents.Terraria.NetMessage.SendData -= OnStrikeRelay;
        if (installed) HookEvents.Terraria.NPC.NPCLoot -= OnStrikeLoot;
        strikeScopeHook?.Dispose(); strikeScopeHook = null;
        installed = false; Array.Clear(causes);
        Array.Clear(strikeTransactions); Array.Clear(strikeCompletions);
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
        if (!installed || failed || epoch != session.WorldEpoch || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) ||
            actor.Index != session.Slot ||
            (uint)request.TargetSlot >= causes.Length) return result;
        ApplyPendingInvalidation();
        if (Main.npc[request.TargetSlot] is not { } target) return result;
        BeginClientStrike(request, session, actor, target);
        var native = causes[request.TargetSlot];
        var allowed = BuildClientStrikeResults(session);
        bool liveCause = Volatile.Read(ref invalidationPending) == 0 && native is not null && native.Epoch == epoch && tick - native.Tick <= TtlTicks &&
            native.Generation == target.generation && ReferenceEquals(native.Target, target);
        var facts = result.Facts.Remove("nativeCauseCoordinates").Remove("nativeCauseInputDamage").Remove("nativeCauseCritical")
            .SetItem("causeProducer", nameof(M7NpcStrikeCauseContexts))
            .SetItem("targetAtRequest", $"type={target.type};life={target.life};hitbox={target.position.X},{target.position.Y},{target.width},{target.height}")
            .SetItem("targetFactProvenance", "current-server-NPC-slot-and-generation")
            .SetItem("actorPositionProvenance", "accepted-client-movement-not-exclusive-attack-range")
            .Remove("actorPositionAtRequest")
            .SetItem("clientAllowedDamageResults", $"wire={allowed.AllowedResults};complete={allowed.Complete};nativeCauseIsAuthorization=False")
            .SetItem("exclusiveAttackCauseAvailable", "False")
            .SetItem("recentServerNativeCause", liveCause ? native!.SourceKind : "none-for-current-target-generation")
            .SetItem("nativeCauseIsClientAuthorization", "False");
        if (liveCause)
            facts = facts.SetItem("nativeCauseCoordinates", $"tick={native!.Tick};owner={native.Owner};sourceType={native.SourceType};projectileKey={native.ProjectileKey?.ToString() ?? "none"}")
                .SetItem("nativeCauseInputDamage", native.Damage.ToString()).SetItem("nativeCauseCritical", native.Critical.ToString());
        return result with { Facts = facts };
    }

    private void ApplyPendingInvalidation()
    {
        if (Interlocked.Exchange(ref invalidationPending, 0) != 0)
        { Array.Clear(causes); Array.Clear(strikeTransactions); Array.Clear(strikeCompletions); }
    }
}
