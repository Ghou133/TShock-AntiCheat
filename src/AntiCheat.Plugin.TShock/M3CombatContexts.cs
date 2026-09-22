using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Bounded runtime observations. Accepted controls establish positive compatibility, not an exclusive
/// weapon cause. Only the real ApplyStatsFromSource hook supplies an independent creation source.
/// No missing record, excess slot count or negative compatibility observation can become a ban here.
/// </summary>
public sealed class M3CombatContexts : IDisposable
{
    private sealed record Definition(bool Minion, float Slots, bool Bobber, bool Sentry);
    private sealed record Control(SessionKey Session, int Selected, int Type, int Shoot, bool Fishing, long Tick);
    private sealed record Capacity(SessionKey Session, double Slots, int Maximum, int Bobbers, int Sentries, int MaximumSentries, long Tick);
    private sealed record Source(long WorldEpoch, uint Key, int Type, int Owner, string Kind, int? Weapon,
        int? Parent, SessionKey? Actor, long Tick);
    private readonly string fingerprint;
    private readonly Dictionary<int, Definition> definitions = [];
    private readonly Dictionary<uint, Source> sources = [];
    private readonly Control?[] pendingControls = new Control?[256];
    private readonly Control?[] controls = new Control?[256];
    private readonly Capacity?[] capacities = new Capacity?[256];
    private readonly Dictionary<uint, (SessionKey Session, int Type, long Tick)> pending = [];
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? targetAtSlot;
    private int nextDefinition = 1;
    private int updateThread;
    private long tick;
    private long worldEpoch = -1;
    private bool installed;
    private bool failed;
    public const int EntityCapacity = 1024;
    public const int SourceTtlTicks = 1800;
    public const int UseTtlTicks = 120;
    public const string SentryBudgetRuleId = "C3.SentryBudget";

    public M3CombatContexts(string fingerprint) => this.fingerprint = fingerprint;
    public int SourceCount => sources.Count;
    public int PendingCount => pending.Count;
    public bool ProjectileTableReady => nextDefinition == ProjectileID.Count;
    public Action<Exception>? IntegrityFault { get; set; }

    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += OnSource;
        installed = true;
    }

    public void Dispose()
    {
        if (installed) HookEvents.Terraria.Projectile.ApplyStatsFromSource -= OnSource;
        installed = false;
        Clear();
    }

    private void Clear()
    {
        sources.Clear(); pending.Clear();
        Array.Clear(pendingControls); Array.Clear(controls); Array.Clear(capacities);
    }

    /// <summary>Call from the verified GameUpdate context. One fixed 1000-entity scan per update, never per packet.</summary>
    public void Tick(long currentWorldEpoch, Func<int, (SessionSnapshot? Session, TSPlayer? Player)> currentTargetAtSlot)
    {
        updateThread = Environment.CurrentManagedThreadId;
        targetAtSlot = currentTargetAtSlot;
        tick++;
        if (worldEpoch != currentWorldEpoch) { Clear(); worldEpoch = currentWorldEpoch; }
        if (Main.netMode != 2) return;
        if (ProjectileID.Count is > 0 and < 20000)
            for (int budget = 0; budget < 64 && nextDefinition < ProjectileID.Count; budget++, nextDefinition++)
            {
                var projectile = new Projectile();
                projectile.SetDefaults(nextDefinition);
                definitions[nextDefinition] = new(projectile.minion, projectile.minionSlots, projectile.bobber, projectile.sentry);
            }

        var slots = new double[256];
        var bobbers = new int[256];
        var sentries = new int[256];
        bool entitiesComplete = Main.projectile is { Length: >= 1000 };
        if (entitiesComplete)
            for (int index = 0; index < 1000; index++)
            {
                var entity = Main.projectile[index];
                if (entity is not { active: true } || entity.owner is < 0 or >= 256) continue;
                if (entity.minion)
                    slots[entity.owner] += float.IsFinite(entity.minionSlots) && entity.minionSlots >= 0
                        ? entity.minionSlots : double.NaN;
                if (entity.bobber) bobbers[entity.owner]++;
                // WipableTurret has local-player/DD2 semantics; count sentries positively here and
                // do not pretend this server count proves which client-owned turret gets replaced.
                if (entity.sentry) sentries[entity.owner]++;
            }
        for (int slot = 0; slot < 256; slot++)
        {
            var target = currentTargetAtSlot(slot);
            if (target.Session is not { } session || session.Key.WorldEpoch != worldEpoch ||
                target.Player is not { HasSentInventory: true, IgnoreSSCPackets: false } actor ||
                !actor.IsLoggedIn || actor.TPlayer is not { active: true } player)
            {
                capacities[slot] = null; controls[slot] = null; pendingControls[slot] = null;
                continue;
            }
            capacities[slot] = entitiesComplete ? new(session.Key, slots[slot], player.maxMinions, bobbers[slot],
                sentries[slot], player.maxTurrets, tick) : null;
            if (pendingControls[slot] is { } candidate)
            {
                if (candidate.Session == session.Key && tick - candidate.Tick is > 0 and <= 2 &&
                    player.controlUseItem && player.selectedItem == candidate.Selected &&
                    (uint)candidate.Selected < player.inventory.Length &&
                    player.inventory[candidate.Selected].type == candidate.Type)
                    controls[slot] = candidate;
                pendingControls[slot] = null;
            }
            if (controls[slot] is { } accepted && (accepted.Session != session.Key ||
                tick - accepted.Tick > UseTtlTicks || !player.controlUseItem ||
                player.selectedItem != accepted.Selected || player.inventory[accepted.Selected].type != accepted.Type))
                controls[slot] = null;
        }
        foreach (var entry in pending.ToArray())
            if (tick > entry.Value.Tick || currentTargetAtSlot(entry.Value.Session.Slot).Session?.Key != entry.Value.Session)
                pending.Remove(entry.Key);
        foreach (var entry in sources.ToArray())
            if (tick - entry.Value.Tick > SourceTtlTicks || entry.Value.WorldEpoch != worldEpoch ||
                entry.Value.Actor is { } actorKey && currentTargetAtSlot(actorKey.Slot).Session?.Key != actorKey ||
                !TrySameEntity(entry.Value))
                sources.Remove(entry.Key);
    }

    // Read-only hook: never calls OriginalMethod or changes ContinueExecution. Core keeps its own semantics.
    private void OnSource(Projectile entity, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
    {
        if (failed) return;
        try { ObserveSource(entity, args); }
        catch (Exception exception)
        {
            failed = true;
            try { Dispose(); } catch (Exception cleanup) { exception = new AggregateException(exception, cleanup); }
            IntegrityFault?.Invoke(exception);
        }
    }

    private void ObserveSource(Projectile entity, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
    {
        if (!args.ContinueExecution || Environment.CurrentManagedThreadId != updateThread || worldEpoch < 0 ||
            !entity.active || entity.type <= 0 || entity.type >= ProjectileID.Count) return;
        string kind;
        int? weapon = null, parent = null;
        SessionKey? actor = null;
        if (args.spawnSource is EntitySource_ItemUse itemSource)
        {
            kind = "server-ItemUse";
            weapon = itemSource.Item.type;
            if (itemSource.Entity is Player player) actor = targetAtSlot?.Invoke(player.whoAmI).Session?.Key;
        }
        else if (args.spawnSource is EntitySource_Parent { Entity: Projectile parentEntity })
        {
            kind = "server-parent-projectile";
            parent = parentEntity.type;
            if (sources.TryGetValue(parentEntity.key.bits, out var parentSource) && TrySameEntity(parentSource))
                actor = parentSource.Actor;
        }
        else kind = "server-" + (args.spawnSource?.GetType().Name ?? "unspecified-source");
        if (sources.Count >= EntityCapacity && !sources.ContainsKey(entity.key.bits)) return;
        sources[entity.key.bits] = new(worldEpoch, entity.key.bits, entity.type, entity.owner, kind,
            weapon, parent, actor, tick);
    }

    private static bool TrySameEntity(Source source) =>
        M2ProjectileLookup.TryGet((ProjectileKey)source.Key, out var entity, out _) &&
        entity is { active: true } && entity.type == source.Type && entity.owner == source.Owner;

    private RuleInputContext Input(SessionKey session) => new(session, fingerprint, fingerprint, true, true, true, false);

    /// <summary>Called at the existing pre-core raw hook, with a parsed immutable packet and actual authenticated session.</summary>
    public IReadOnlyList<BusinessRuleResult> Evaluate(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled)
    {
        var output = new List<BusinessRuleResult>(3);
        if (Environment.CurrentManagedThreadId != updateThread || session.WorldEpoch != worldEpoch) return output;
        if (packet.Kind == M2PacketKind.PlayerUpdate && packet.Payload[0] == session.Slot)
        {
            int selected = packet.Payload[5];
            if (alreadyCancelled || (packet.Payload[1] & 32) == 0 || actor.IgnoreSSCPackets || !actor.HasSentInventory ||
                actor.TPlayer is not { } player || (uint)selected >= player.inventory.Length)
            {
                pendingControls[session.Slot] = null; controls[session.Slot] = null;
                return output;
            }
            var item = player.inventory[selected];
            pendingControls[session.Slot] = new(session, selected, item.type, item.shoot, item.fishingPole > 0, tick);
            // A different selection invalidates the old cause immediately, before the next update.
            if (controls[session.Slot]?.Selected != selected || controls[session.Slot]?.Type != item.type)
                controls[session.Slot] = null;
            return output;
        }
        if (packet.Kind == M2PacketKind.ProjectileDestroy)
        {
            if (!alreadyCancelled) pending.Remove((uint)M2PacketReader.Int32(packet.Payload, 0));
            return output;
        }
        if (packet.Kind != M2PacketKind.ProjectileNew) return output;
        uint keyBits = (uint)M2PacketReader.Int32(packet.Payload, 0);
        var key = (ProjectileKey)keyBits;
        int type = M2PacketReader.Int16(packet.Payload, 20);
        bool exists = M2ProjectileLookup.TryGet(key, out var existing, out var lookupComplete) && existing!.active;
        var common = ImmutableDictionary<string, string>.Empty.Add("producer", "M3CombatContexts")
            .Add("projectileType", type.ToString()).Add("snapshotTick", tick.ToString());
        if (alreadyCancelled || !lookupComplete || key.Spawner != session.Slot && !exists)
            return [Result(CombatRules.WeaponRuleId, false, "combat-input-cancelled-or-key-unavailable", common)];

        if (sources.TryGetValue(keyBits, out var source) && source.WorldEpoch == worldEpoch &&
            tick - source.Tick <= SourceTtlTicks && TrySameEntity(source) && source.Type == type)
        {
            var facts = common.Add("sourceHook", "Projectile.ApplyStatsFromSource").Add("sourceKind", source.Kind)
                .Add("sourceWeapon", source.Weapon?.ToString() ?? "none").Add("sourceParent", source.Parent?.ToString() ?? "none");
            output.Add(Result(CombatRules.WeaponRuleId, true, "runtime-created-projectile-source-observed", facts));
        }
        else
        {
            var use = controls[session.Slot];
            bool match = use is not null && use.Session == session && tick - use.Tick <= UseTtlTicks &&
                use.Shoot == type && use.Shoot > 0;
            output.Add(Result(CombatRules.WeaponRuleId, match,
                match ? "accepted-use-projectile-compatible-not-exclusive-cause" : "client-projectile-exclusive-cause-not-on-wire",
                common.Add("acceptedUseType", use?.Type.ToString() ?? "missing")
                    .Add("cooldownProof", "unavailable").Add("exclusiveCause", "false")));
        }
        if (!definitions.TryGetValue(type, out var definition)) return output;
        var capacity = capacities[session.Slot];
        if (definition.Minion)
        {
            double incoming = exists ? 0 : definition.Slots;
            double inFlight = pending.Where(x => x.Value.Session == session && x.Key != keyBits)
                .Sum(x => definitions.TryGetValue(x.Value.Type, out var d) && d.Minion ? d.Slots : 0d);
            bool stable = capacity is not null && capacity.Session == session && capacity.Tick == tick &&
                actor.TPlayer.maxMinions == capacity.Maximum;
            bool noReplacementNeeded = stable && capacity!.Slots + inFlight + incoming <= capacity.Maximum;
            output.Add(ProjectileRules.EvaluateSummonBudget(new(Input(session), session,
                capacity?.Slots + inFlight ?? 0, incoming, 0, capacity?.Maximum ?? 0, stable,
                noReplacementNeeded, !stable)) with
            {
                Facts = common.Add("slotsSource", "GameUpdate-active-projectiles+same-tick-candidates")
                    .Add("maximumSource", "server-computed-maxMinions-resource-only")
                    .Add("existingSlots", (capacity?.Slots + inFlight)?.ToString() ?? "missing")
                    .Add("incomingSlots", incoming.ToString()).Add("maximumSlots", capacity?.Maximum.ToString() ?? "missing"),
                Reason = noReplacementNeeded ? "observed-summon-fits-without-replacement" : "summon-replacement-or-capacity-transition-unobserved"
            });
        }
        if (definition.Bobber)
        {
            var use = controls[session.Slot];
            bool compatible = exists && existing!.type == type || use is { Fishing: true } && use.Session == session &&
                tick - use.Tick <= UseTtlTicks && (use.Shoot == type || actor.TPlayer.overrideFishingBobber == type);
            output.Add(Result(ProjectileRules.FishingRuleId, compatible,
                compatible ? "observed-bobber-compatible-with-existing-entity-or-accepted-cast" : "fishing-cast-token-not-on-wire",
                common.Add("activeBobbers", capacity?.Bobbers.ToString() ?? "missing")
                    .Add("castBudgetEnforced", "false").Add("replacementProof", "unavailable")));
        }
        if (definition.Sentry)
        {
            int incoming = exists ? 0 : 1;
            int inFlight = pending.Count(x => x.Value.Session == session && x.Key != keyBits &&
                definitions.TryGetValue(x.Value.Type, out var d) && d.Sentry);
            bool stable = capacity is not null && capacity.Session == session && capacity.Tick == tick &&
                actor.TPlayer.maxTurrets == capacity.MaximumSentries && capacity.MaximumSentries >= 0;
            bool fits = stable && (long)capacity!.Sentries + inFlight + incoming <= capacity.MaximumSentries;
            output.Add(Result(SentryBudgetRuleId, fits,
                fits ? "observed-sentry-fits-without-replacement" : "sentry-dd2-replacement-or-capacity-transition-unobserved",
                common.Add("existingSentries", (capacity?.Sentries + inFlight)?.ToString() ?? "missing")
                    .Add("incomingSentries", incoming.ToString()).Add("maximumSentries", capacity?.MaximumSentries.ToString() ?? "missing")
                    .Add("maximumSource", "server-computed-maxTurrets-resource-only")
                    .Add("replacementAuthority", "owner-only-UpdateMaxTurrets+DD2-exception")
                    .Add("hardBudgetEnforced", "false")));
        }
        if (!exists && key.Spawner == session.Slot && pending.Count < EntityCapacity)
            pending[keyBits] = (session, type, tick);
        return output;
    }

    private static BusinessRuleResult Result(string rule, bool pass, string reason, ImmutableDictionary<string, string> facts) =>
        new(rule, "1.0.0", pass ? ControlAction.Pass : ControlAction.Unknown,
            pass ? Verdict.Pass : Verdict.Unknown, reason, false, false, facts);
}
