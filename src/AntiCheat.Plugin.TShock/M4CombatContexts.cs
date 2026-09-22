using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>A bounded producer for one NPC-only creation contract and complete projectile numeric filtering.</summary>
public sealed class M4CombatContexts : IDisposable
{
    private readonly string fingerprint;
    private readonly SessionKey?[] sourceExceptions = new SessionKey?[256];
    private readonly SessionKey?[] portalExceptions = new SessionKey?[256];
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? targetAtSlot;
    private bool[]? hostileTable;
    private long worldEpoch = -1;
    private int updateThread;
    private bool installed, healthy, portalHealthy, failed;
    public const string NumericRuleId = "C0.ProjectileNumericArguments";
    public Action<Exception>? IntegrityFault { get; set; }
    public bool ContractHealthy => healthy && !failed && installed;
    public bool PortalContractHealthy => portalHealthy && !failed && installed;

    public M4CombatContexts(string fingerprint) => this.fingerprint = fingerprint;

    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += OnSource;
        HookEvents.Terraria.NetMessage.SendData += OnSendData;
        installed = true;
    }

    public void Dispose()
    {
        if (installed) HookEvents.Terraria.Projectile.ApplyStatsFromSource -= OnSource;
        if (installed) HookEvents.Terraria.NetMessage.SendData -= OnSendData;
        installed = false; healthy = false; portalHealthy = false; targetAtSlot = null;
        Array.Clear(sourceExceptions); Array.Clear(portalExceptions);
    }

    public void Tick(long currentWorldEpoch, Func<int, (SessionSnapshot? Session, TSPlayer? Player)> targets)
    {
        if (failed || !installed) return;
        updateThread = Environment.CurrentManagedThreadId;
        targetAtSlot = targets;
        if (worldEpoch != currentWorldEpoch) { worldEpoch = currentWorldEpoch; Array.Clear(sourceExceptions); Array.Clear(portalExceptions); }
        healthy = false; portalHealthy = false;
        if (Main.netMode != 2 || Main.myPlayer != 255 || currentWorldEpoch <= 0 ||
            Main.projHostile is not { } current || current.Length != ProjectileID.Count ||
            current.Length <= M4CombatRules.PortalType ||
            Main.projectile is not { Length: >= 1000 }) return;
        var probe = new Projectile(); probe.SetDefaults(M4CombatRules.RitualType);
        healthy = current[M4CombatRules.RitualType] && probe.hostile && !probe.friendly && probe.aiStyle == 89 && probe.netImportant;
        var portal = new Projectile(); portal.SetDefaults(M4CombatRules.PortalType);
        portalHealthy = !current[M4CombatRules.PortalType] && !portal.hostile && portal.friendly && portal.aiStyle == 114 && portal.netImportant;
        hostileTable = current;
        for (int slot = 0; slot < sourceExceptions.Length; slot++)
        {
            if (sourceExceptions[slot] is { } exception && targets(slot).Session?.Key != exception)
                sourceExceptions[slot] = null;
            if (portalExceptions[slot] is { } portalException && targets(slot).Session?.Key != portalException)
                portalExceptions[slot] = null;
        }
        // A pre-existing/plugin-mutated player key is an exception, not evidence against its owner.
        // One fixed entity scan per update; no packet-triggered scan, dictionary, unbounded TTL or queue.
        for (int index = 0; index < 1000; index++)
            if (Main.projectile[index] is { active: true } entity &&
                (entity.type == M4CombatRules.RitualType || entity.type == M4CombatRules.PortalType && entity.damage != 0))
                RecordSourceException(entity.key.Spawner, entity.type);
    }

    private void RecordSourceException(int spawner, int type)
    {
        if (spawner is < 0 or >= 255) return;
        if (targetAtSlot?.Invoke(spawner).Session is { } session && session.Key.WorldEpoch == worldEpoch)
            (type == M4CombatRules.PortalType ? portalExceptions : sourceExceptions)[spawner] = session.Key;
    }

    private void OnSource(Projectile entity, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
    {
        if (failed || entity.type is not (M4CombatRules.RitualType or M4CombatRules.PortalType)) return;
        try
        {
            // An off-context creation removes this hard rule. It is never ignored as if collection were complete.
            if (Environment.CurrentManagedThreadId != updateThread || worldEpoch <= 0)
            {
                healthy = false; failed = true;
                IntegrityFault?.Invoke(new InvalidOperationException("Cultist ritual source hook executed outside the verified update context."));
                return;
            }
            // Conservatively preserve an exception even if another hook cancels source application.
            RecordSourceException(entity.key.Spawner, entity.type);
        }
        catch (Exception exception)
        {
            failed = true; healthy = false;
            try { Dispose(); } catch (Exception cleanup) { exception = new AggregateException(exception, cleanup); }
            IntegrityFault?.Invoke(exception);
        }
    }

    private void OnSendData(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (failed || !installed || !args.ContinueExecution || args.msgType != 27 || Main.netMode != 2) return;
        try
        {
            if (Environment.CurrentManagedThreadId != updateThread || worldEpoch <= 0)
            {
                failed = true; healthy = false; portalHealthy = false;
                IntegrityFault?.Invoke(new InvalidOperationException("Projectile export executed outside the verified update context."));
                return;
            }
            if ((uint)args.number >= Main.projectile.Length || Main.projectile[args.number] is not { active: true } entity ||
                entity.type is not (M4CombatRules.RitualType or M4CombatRules.PortalType)) return;
            int spawner = entity.key.Spawner;
            // A relay to other players is not a server export to the owner. Preserve the actual recipient boundary.
            if (!(args.remoteClient == spawner || args.remoteClient == -1 && args.ignoreClient != spawner)) return;
            if (entity.type == M4CombatRules.RitualType || entity.damage != 0)
                RecordSourceException(spawner, entity.type);
        }
        catch (Exception exception)
        {
            failed = true; healthy = false; portalHealthy = false;
            try { Dispose(); } catch (Exception cleanup) { exception = new AggregateException(exception, cleanup); }
            IntegrityFault?.Invoke(exception);
        }
    }

    public IReadOnlyList<BusinessRuleResult> Evaluate(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled)
    {
        if (!M4CombatProjectileReader.TryRead(packet, out var parsed)) return [];
        var shot = parsed!;
        var results = new List<BusinessRuleResult>(2);
        var facts = ImmutableDictionary<string, string>.Empty.Add("producer", "M4CombatContexts")
            .Add("damage", shot.Damage.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("originalDamage", shot.OriginalDamage.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("upstreamCancelled", alreadyCancelled.ToString());
        if (!shot.AllNumbersFinite)
            results.Add(new(NumericRuleId, "1.0.0", ControlAction.Block, Verdict.UnsafeInput,
                "nonfinite-projectile-optional-or-vector-number", false, false, facts));
        if (shot.Type is not (M4CombatRules.RitualType or M4CombatRules.PortalType)) return results;
        var key = (ProjectileKey)shot.Key;
        bool found = M2ProjectileLookup.TryGet(key, out var existing, out bool lookupComplete) && existing!.active;
        bool sharedContext = Environment.CurrentManagedThreadId == updateThread &&
            session.WorldEpoch == worldEpoch && ReferenceEquals(hostileTable, Main.projHostile) &&
            Main.netMode == 2 && Main.myPlayer == 255;
        bool correctContext = sharedContext && (shot.Type == M4CombatRules.RitualType
            ? ContractHealthy && hostileTable![M4CombatRules.RitualType]
            : PortalContractHealthy && !hostileTable![M4CombatRules.PortalType]);
        bool authenticated = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is not null &&
            targetAtSlot?.Invoke(session.Slot).Session is { } current && current.Key == session &&
            !current.Revoked && current.AccountId == actor.Account.ID;
        var input = new RuleInputContext(session, fingerprint, fingerprint, true, correctContext,
            authenticated, correctContext && actor.HasSentInventory && !actor.IgnoreSSCPackets);
        var observation = new ProjectileObservation(new(key.Spawner, key.Index, key.Generation),
            found ? ProjectileOperation.Update : ProjectileOperation.Create, shot.Type);
        var result = shot.Type == M4CombatRules.RitualType
            ? M4CombatRules.EvaluateRitual(observation,
                new(input, correctContext, lookupComplete, found, (uint)session.Slot >= 256 || sourceExceptions[session.Slot] == session))
            : M4CombatRules.EvaluatePortalDamage(observation, shot.Damage,
                new(input, correctContext, lookupComplete, found, (uint)session.Slot >= 256 || portalExceptions[session.Slot] == session, shot.AllNumbersFinite));
        results.Add(result with { Facts = result.Facts.SetItems(facts) });
        return results;
    }
}
