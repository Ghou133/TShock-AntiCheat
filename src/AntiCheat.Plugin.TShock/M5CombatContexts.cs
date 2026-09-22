using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Confirms a fresh ordinary arrow's initial raw declaration at the real post-write vanilla relay.
/// No inventory/equipment data establishes a damage limit. This observes a single entity lifecycle.
/// </summary>
public sealed partial class M5CombatContexts : IDisposable
{
    private sealed record Pending(SessionKey Session, uint Key, int Damage, long Tick);
    private sealed record Witness(SessionKey Session, uint Key, Projectile Entity, int InitialDamage, long Tick);
    private readonly string fingerprint;
    private readonly Pending?[] pending = new Pending?[256];
    private readonly SessionKey?[] sourceExceptions = new SessionKey?[256];
    private readonly SessionKey?[] pluginExceptions = new SessionKey?[256];
    private readonly Dictionary<uint, Witness> witnesses = [];
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? targets;
    private long worldEpoch = -1, tick;
    private int updateThread;
    private bool installed, failed, healthy;
    public const int Capacity = 1024;
    public const int WitnessTtlTicks = 1800;
    public Action<Exception>? IntegrityFault { get; set; }
    public int WitnessCount => witnesses.Count;
    public bool ContractHealthy => installed && healthy && !failed;

    public M5CombatContexts(string fingerprint) => this.fingerprint = fingerprint;

    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += OnSource;
        HookEvents.Terraria.Projectile.SetDefaults += OnProjectionDefaults;
        HookEvents.Terraria.NetMessage.SendData += OnSendData;
        installed = true;
    }

    public void Dispose()
    {
        if (installed)
        {
            HookEvents.Terraria.Projectile.ApplyStatsFromSource -= OnSource;
            HookEvents.Terraria.Projectile.SetDefaults -= OnProjectionDefaults;
            HookEvents.Terraria.NetMessage.SendData -= OnSendData;
        }
        installed = false; healthy = false; targets = null;
        witnesses.Clear(); Array.Clear(pending); Array.Clear(sourceExceptions); Array.Clear(pluginExceptions);
    }

    public void Tick(long currentWorldEpoch, Func<int, (SessionSnapshot? Session, TSPlayer? Player)> currentTargets)
    {
        if (failed || !installed) return;
        updateThread = Environment.CurrentManagedThreadId; targets = currentTargets; tick++;
        if (worldEpoch != currentWorldEpoch)
        {
            worldEpoch = currentWorldEpoch; witnesses.Clear(); Array.Clear(pending); Array.Clear(sourceExceptions); Array.Clear(pluginExceptions);
        }
        ObservePluginComposition();
        Array.Clear(pending); // Confirmation occurs in the same raw GetData call; never guess across updates.
        var probe = new Projectile(); probe.SetDefaults(M5CombatRules.ArrowType);
        healthy = Main.netMode == 2 && Main.myPlayer == 255 && worldEpoch > 0 &&
            Main.projectile is { Length: >= 1000 } && Main.projHostile is { Length: > M5CombatRules.ArrowType } &&
            !Main.projHostile[M5CombatRules.ArrowType] && probe.aiStyle == 1 && probe.friendly && !probe.hostile &&
            probe.ranged && !probe.minion && !probe.sentry && !probe.bobber;
        for (int slot = 0; slot < sourceExceptions.Length; slot++)
            if (sourceExceptions[slot] is { } exception && currentTargets(slot).Session?.Key != exception)
                sourceExceptions[slot] = null;
        for (int slot = 0; slot < pluginExceptions.Length; slot++)
            if (pluginExceptions[slot] is { } exception && currentTargets(slot).Session?.Key != exception)
                pluginExceptions[slot] = null;
        foreach (var pair in witnesses.ToArray())
        {
            var witness = pair.Value;
            if (tick - witness.Tick > WitnessTtlTicks || currentTargets(witness.Session.Slot).Session?.Key != witness.Session ||
                !SameEntity(witness)) witnesses.Remove(pair.Key);
            else if (!M7ArrowProjectionRules.IsReachable(witness.InitialDamage, witness.Entity.damage))
            {
                // A value outside the native projection family is a server-side integrity/source exception.
                // A legal Int16 wrap on reflection must not disable C7 for the remainder of the session.
                ExcludeSession(witness.Session.Slot);
                witnesses.Remove(pair.Key);
            }
        }
    }

    private static bool SameEntity(Witness witness) =>
        M2ProjectileLookup.TryGet((ProjectileKey)witness.Key, out var entity, out bool complete) && complete &&
        ReferenceEquals(entity, witness.Entity) && entity is { active: true, type: M5CombatRules.ArrowType, aiStyle: 1, minion: false, sentry: false } &&
        entity.key.bits == witness.Key && entity.owner == witness.Session.Slot;

    private void ExcludeSession(int slot)
    {
        if ((uint)slot >= 255) return;
        if (targets?.Invoke(slot).Session is { } session && session.Key.WorldEpoch == worldEpoch)
            sourceExceptions[slot] = session.Key;
        pending[slot] = null;
    }

    private void Fail(Exception error)
    {
        failed = true;
        try { Dispose(); } catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
        IntegrityFault?.Invoke(error);
    }

    private bool VerifiedThread => Environment.CurrentManagedThreadId == updateThread && worldEpoch > 0;

    private bool ObservePluginComposition()
    {
        bool known = ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(TShockAPI.TShock) ||
            x.Plugin.GetType() == typeof(AntiCheatPlugin));
        if (!known && targets is not null)
            for (int slot = 0; slot < pluginExceptions.Length; slot++)
                if (targets(slot).Session is { } current && current.Key.WorldEpoch == worldEpoch)
                {
                    pluginExceptions[slot] = current.Key;
                    pending[slot] = null;
                }
        return known;
    }

    private void OnSource(Projectile entity, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
    {
        if (failed || entity.type != M5CombatRules.ArrowType) return;
        try
        {
            if (!VerifiedThread) { Fail(new InvalidOperationException("Arrow source executed outside the verified update context.")); return; }
            ObservePluginComposition();
            ExcludeSession(entity.key.Spawner);
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnSendData(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (failed || !installed || !args.ContinueExecution || args.msgType != 27 || Main.netMode != 2) return;
        try
        {
            if (!VerifiedThread) { Fail(new InvalidOperationException("Arrow export executed outside the verified update context.")); return; }
            if (!ObservePluginComposition()) return;
            if ((uint)args.number >= Main.projectile.Length || Main.projectile[args.number] is not { active: true } entity ||
                entity.type != M5CombatRules.ArrowType) return;
            int slot = entity.key.Spawner;
            if ((uint)slot >= 255) return;
            if (args.remoteClient == slot || args.remoteClient == -1 && args.ignoreClient != slot)
            {
                // Session-lifetime exception also covers delayed echoes after this exported entity is removed.
                ExcludeSession(slot); return;
            }
            if (args.remoteClient != -1 || args.ignoreClient != slot || pending[slot] is not { } first) return;
            pending[slot] = null;
            if (first.Tick != tick || first.Key != entity.key.bits || first.Damage != entity.damage || entity.owner != slot ||
                first.Session != targets?.Invoke(slot).Session?.Key || sourceExceptions[slot] == first.Session || pluginExceptions[slot] == first.Session ||
                witnesses.Count >= Capacity && !witnesses.ContainsKey(first.Key)) return;
            witnesses[first.Key] = new(first.Session, first.Key, entity, first.Damage, tick);
        }
        catch (Exception error) { Fail(error); }
    }

    public IReadOnlyList<BusinessRuleResult> Evaluate(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled)
    {
        if (!installed || failed || !VerifiedThread) return [];
        bool knownPlugins = ObservePluginComposition();
        if (packet.Kind == M2PacketKind.ProjectileDestroy && packet.Payload.Length >= 4)
        {
            uint destroyed = unchecked((uint)M2PacketReader.Int32(packet.Payload, 0));
            if (!alreadyCancelled && witnesses.TryGetValue(destroyed, out var destroyedWitness) && destroyedWitness.Session == session)
                witnesses.Remove(destroyed);
            return [];
        }
        if (!M4CombatProjectileReader.TryRead(packet, out var parsed)) return [];
        var shot = parsed!;
        var key = (ProjectileKey)shot.Key;
        if ((uint)session.Slot >= 255) return [];
        pending[session.Slot] = null;
        if (shot.Type != M5CombatRules.ArrowType)
        {
            if (witnesses.TryGetValue(shot.Key, out var changed) && changed.Session == session)
                witnesses.Remove(shot.Key); // An actor cannot invalidate another session's evidence.
            return [];
        }
        bool found = M2ProjectileLookup.TryGet(key, out var entity, out bool complete) && entity!.active;
        bool authenticated = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is not null &&
            targets?.Invoke(session.Slot).Session is { } current && current.Key == session &&
            !current.Revoked && current.AccountId == actor.Account.ID;
        bool available = ContractHealthy && knownPlugins && VerifiedThread && worldEpoch == session.WorldEpoch &&
            Main.netMode == 2 && Main.myPlayer == 255 && Main.projHostile is { Length: > 1 } && !Main.projHostile[1] &&
            shot.AllNumbersFinite && actor.HasSentInventory && !actor.IgnoreSSCPackets;
        var input = new RuleInputContext(session, fingerprint, fingerprint, true, available, authenticated, available);
        bool confirmed = witnesses.TryGetValue(shot.Key, out var witness) && witness.Session == session &&
            tick - witness.Tick <= WitnessTtlTicks && SameEntity(witness!);
        bool sourceException = sourceExceptions[session.Slot] == session || pluginExceptions[session.Slot] == session ||
            confirmed && entity!.damage > witness!.InitialDamage;
        var observation = new ProjectileObservation(new(key.Spawner, key.Index, key.Generation),
            found ? ProjectileOperation.Update : ProjectileOperation.Create, shot.Type);
        var result = M5CombatRules.EvaluateArrowEvolution(observation, shot.Damage,
            new(input, available, found && complete && confirmed, confirmed, witness?.InitialDamage ?? 0, sourceException));
        if (!alreadyCancelled && available && authenticated && complete && !found && key.Spawner == session.Slot &&
            shot.Damage >= 0 && !sourceException)
            pending[session.Slot] = new(session, shot.Key, shot.Damage, tick);
        return [result with { Facts = result.Facts.Add("producer", "M5CombatContexts")
            .Add("initialWitness", "raw-fresh-key+post-write-vanilla-relay")
            .Add("pluginCompositionException", (pluginExceptions[session.Slot] == session).ToString())
            .Add("upstreamCancelled", alreadyCancelled.ToString()).Add("snapshotTick", tick.ToString()) }];
    }
}
