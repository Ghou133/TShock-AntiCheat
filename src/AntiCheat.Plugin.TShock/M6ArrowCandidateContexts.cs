using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

[Flags]
public enum ArrowCandidateGap
{
    None = 0, ConnectionStartNotObserved = 1, SnapshotHistoryOverflow = 2,
    InventoryUnavailable = 4, ServerItemCustomization = 8, ServerArrowSource = 16,
    ArrowExportToOwner = 32, UnverifiedPluginComposition = 64, NativeCreationBranchesNotClosed = 128,
    SnapshotHistoryExpired = 256
}

public sealed record ArrowItemProjection(int Slot, int Type, int Prefix, int Stack, int DeclaredDamage);
public sealed record ArrowGameplaySnapshot(long Tick, int HeldSlot,
    ImmutableArray<ArrowItemProjection> Inventory, ImmutableArray<ArrowItemProjection> EffectiveArmor,
    ImmutableArray<int> ActiveBuffs, int MountType, float DeclaredStealth)
{
    public float ObservedBowMultiplier { get; init; }
    public bool ObservedSharpBarb { get; init; }
}
public sealed record ArrowCanonicalCandidate(int Type, int DefaultProjectile, int AmmoType, int MaximumPrefixedDamage, int ExemplarPrefix);

/// <summary>
/// Component envelope produced from the canonical item table and actual gameplay observations.
/// This is deliberately not a complete projectile damage bound: observed/accepted state is not a legality witness.
/// </summary>
public sealed record ArrowCandidateEnvelope(SessionKey Session, long Tick, int DeclaredDamage,
    ImmutableArray<ArrowCanonicalCandidate> CanonicalCandidates, int MaximumCanonicalAmmoDamage,
    ImmutableArray<ArrowGameplaySnapshot> RecentSnapshots, ArrowCandidateGap Gaps)
{
    public const string Provenance = "accepted-client-state+canonical-item-union+server-source-and-export-hooks";
    public int MaximumCanonicalWeaponDamage => CanonicalCandidates.IsEmpty ? 0 : CanonicalCandidates.Max(x => x.MaximumPrefixedDamage);
    // Sharp Barb adds one before the weapon/ammo multiplier; this is only the unscaled item component.
    public long UnscaledItemComponentUpperBound => (long)MaximumCanonicalWeaponDamage + MaximumCanonicalAmmoDamage + 1;
    public int? CompleteFirstDamageUpperBound => null;
    public M8DamageAllowedResults? DamageResults { get; init; }
    public ImmutableArray<M8CombatExport> CombatExports { get; init; } = [];
    public M18ResetIntervalSnapshot? SourceResetInterval { get; init; }

    public BusinessRuleResult AddEvidence(BusinessRuleResult result)
    {
        var latest = RecentSnapshots.LastOrDefault();
        string held = latest is null ? "unavailable" : latest.Inventory.FirstOrDefault(x => x.Slot == latest.HeldSlot) is { } item
            ? $"slot{item.Slot}:type{item.Type}:prefix{item.Prefix}:declaredDamage{item.DeclaredDamage}" : "unavailable";
        string effects = latest is null ? "unavailable" : "armor=" + string.Join(',', latest.EffectiveArmor.Select(x => $"{x.Type}/{x.Prefix}")) +
            ";buffs=" + string.Join(',', latest.ActiveBuffs.Take(10)) + $";buffCount={latest.ActiveBuffs.Length};mount={latest.MountType}";
        if (effects.Length > 256) effects = effects[..242] + "[truncated]";
        return result with { Facts = result.Facts
            .Add("candidateProvenance", Provenance + (SourceResetInterval is not { } reset ? ";reset=unavailable" :
                $";resetHistory={reset.HistoricalResetSequenceObserved};resetAvailable={reset.InitialResetInputsAvailable};restoring={reset.SscRestoreInProgress};nativeTransport={reset.NativeTransportIdentityVerified}")).Add("canonicalSourceCount", CanonicalCandidates.Length.ToString())
            .Add("sourceSnapshotTicks", $"count={RecentSnapshots.Length};ticks=" + string.Join(',', RecentSnapshots.Select(x => x.Tick)))
            .Add("sourceObservedHeld", held).Add("sourceObservedEffects", effects)
            .Add("sourceCandidateGaps", Gaps.ToString())
            .Add("allowedDamageUnion", DamageResults is null ? "not-computed" :
                $"supportedContains={DamageResults.SupportedResults.Contains(DeclaredDamage)};allowed={DamageResults.AllowedResults};complete={DamageResults.Complete}") };
    }
}

/// <summary>
/// Bounded, transparent collection for ordinary type1 first declarations. Keeps every canonical arrow-use
/// candidate even when it is absent from the current inventory or recent snapshots. No sanction is issued here.
/// </summary>
public sealed partial class M6ArrowCandidateContexts : IDisposable
{
    private sealed class State(SessionKey session, bool connected)
    {
        public SessionKey Session = session;
        public ArrowCandidateGap Gaps = connected ? ArrowCandidateGap.None : ArrowCandidateGap.ConnectionStartNotObserved;
        public readonly Queue<ArrowGameplaySnapshot> Snapshots = new(SnapshotCapacity);
        public ulong LastDigest;
        public bool HasDigest;
        public readonly Queue<M8CombatExport> Exports = new(ExportCapacity);
        public bool ExportHistoryLost;
        public long DamageRevision;
        public long CachedDamageRevision = -1;
        public M8DamageAllowedResults? CachedDamageResults;
    }
    public const int SnapshotCapacity = 8;
    public const int SnapshotTtlTicks = 1800;
    private readonly State?[] states = new State?[255];
    private readonly string fingerprint;
    private M18ArrowSourceResetIntervals? resetIntervals;
    public void AttachResetIntervals(M18ArrowSourceResetIntervals observer)
    {
        if (resetIntervals is not null && !ReferenceEquals(resetIntervals, observer)) throw new InvalidOperationException("Reset observer already attached.");
        resetIntervals = observer;
    }
    public void Authenticated(SessionKey session, long account) => resetIntervals?.Authenticated(session, account);
    private ImmutableArray<ArrowCanonicalCandidate> candidates = [];
    private ImmutableHashSet<int> canonicalArrowAmmunition = ImmutableHashSet<int>.Empty;
    private int maximumAmmo;
    private long tick, worldEpoch = -1;
    private int updateThread;
    private bool installed, failed, catalogReady;
    public Action<Exception>? IntegrityFault { get; set; }
    public bool Healthy => installed && !failed && catalogReady;
    public int RetainedSnapshotCount => states.Sum(x => x?.Snapshots.Count ?? 0);

    public M6ArrowCandidateContexts(string fingerprint) => this.fingerprint = fingerprint;
    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.Projectile.ApplyStatsFromSource += OnSource;
        HookEvents.Terraria.NetMessage.SendData += OnExport;
        HookEvents.Terraria.NetMessage.OnPacketWrite += OnCombatPacketWrite;
        installed = true;
    }
    public void Dispose()
    {
        if (installed)
        {
            HookEvents.Terraria.Projectile.ApplyStatsFromSource -= OnSource;
            HookEvents.Terraria.NetMessage.SendData -= OnExport;
            HookEvents.Terraria.NetMessage.OnPacketWrite -= OnCombatPacketWrite;
        }
        installed = false; Array.Clear(states); resetIntervals?.Dispose(); resetIntervals = null;
    }
    public void Connected(SessionKey session)
    {
        if ((uint)session.Slot >= states.Length || !installed || failed) return;
        Interlocked.Exchange(ref states[session.Slot], new(session, connected: true));
        resetIntervals?.Connected(session);
    }
    public void Left(SessionKey session)
    {
        resetIntervals?.Forget(session);
        if ((uint)session.Slot < states.Length && states[session.Slot] is { } state && state.Session == session)
            Interlocked.CompareExchange(ref states[session.Slot], null, state);
    }
    public void ResetWorld() { Array.Clear(states); resetIntervals?.ResetWorld(); }

    public void Tick(long epoch, Func<int, (SessionSnapshot? Session, TSPlayer? Player)> targets, bool pluginCompositionVerified)
    {
        if (!installed || failed) return;
        try
        {
            updateThread = Environment.CurrentManagedThreadId;
            resetIntervals?.Tick(epoch, pluginCompositionVerified);
            if (worldEpoch != epoch)
            {
                worldEpoch = epoch;
                for (int slot = 0; slot < states.Length; slot++)
                    if (states[slot]?.Session.WorldEpoch != epoch) states[slot] = null;
            }
            tick++;
            if (!catalogReady && epoch > 0 && Main.netMode == 2 && Main.myPlayer == 255) BuildCanonicalCandidates();
            for (int slot = 0; slot < states.Length; slot++)
            {
                var target = targets(slot);
                if (target.Session is not { } session || session.Key.WorldEpoch != epoch)
                { states[slot] = null; continue; }
                var state = states[slot];
                if (state?.Session != session.Key) states[slot] = state = new(session.Key, connected: false);
                if (!pluginCompositionVerified) AddGap(state, ArrowCandidateGap.UnverifiedPluginComposition);
                while (state.Snapshots.TryPeek(out var old) && tick - old.Tick > SnapshotTtlTicks)
                {
                    state.Snapshots.Dequeue(); AddGap(state, ArrowCandidateGap.SnapshotHistoryExpired);
                    InvalidateDamageResults(state);
                }
                while (state.Exports.TryPeek(out var export) && tick - export.Tick > SnapshotTtlTicks)
                { state.Exports.Dequeue(); state.ExportHistoryLost = true; InvalidateDamageResults(state); }
                if (target.Player is { } player && !session.Revoked) Capture(state, player);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void BuildCanonicalCandidates()
    {
        if (fingerprint != TargetRuntime.Fingerprint || Main.versionNumber.TrimStart('v') != TargetRuntime.TargetVersion)
            throw new InvalidOperationException("Arrow canonical catalog target mismatch.");
        var result = ImmutableArray.CreateBuilder<ArrowCanonicalCandidate>();
        var ammunition = ImmutableHashSet.CreateBuilder<int>();
        for (int type = 1; type < ItemID.Count; type++)
        {
            var item = new Item(); item.SetDefaults(type);
            if (item.ammo == AmmoID.Arrow && item.shoot == ProjectileID.WoodenArrowFriendly)
            {
                maximumAmmo = Math.Max(maximumAmmo, item.damage);
                ammunition.Add(type);
            }
            if (item.useAmmo != AmmoID.Arrow && item.shoot != ProjectileID.WoodenArrowFriendly) continue;
            int maximum = item.damage, exemplar = 0;
            for (int prefix = 1; prefix < PrefixID.Count; prefix++)
            {
                var prefixed = new Item(); prefixed.SetDefaults(type);
                if (prefixed.Prefix(prefix) && prefixed.damage > maximum) { maximum = prefixed.damage; exemplar = prefix; }
            }
            result.Add(new(type, item.shoot, item.useAmmo, maximum, exemplar));
        }
        candidates = result.ToImmutable();
        canonicalArrowAmmunition = ammunition.ToImmutable();
        if (candidates.IsEmpty || maximumAmmo <= 0) throw new InvalidOperationException("Arrow canonical catalog unavailable.");
        catalogReady = true;
    }

    private void Capture(State state, TSPlayer actor)
    {
        var player = actor.TPlayer;
        if (!actor.HasSentInventory || actor.IgnoreSSCPackets || player.inventory.Length < 59 || player.armor.Length < 10)
        { AddGap(state, ArrowCandidateGap.InventoryUnavailable); return; }
        // Accepted declarations only. No ItemCheck/UpdateEquips/UpdateBuffs is invoked on a fabricated player in the live world.
        ulong digest = 14695981039346656037UL;
        void Hash(int value) { digest = (digest ^ unchecked((uint)value)) * 1099511628211UL; }
        Hash(player.selectedItem); Hash(player.mount.Active ? player.mount.Type : -1); Hash(BitConverter.SingleToInt32Bits(player.stealth));
        Hash(BitConverter.SingleToInt32Bits(player.bowEffectiveDamage)); Hash(player.accSharpBarb ? 1 : 0);
        for (int slot = 0; slot < 59; slot++)
        {
            var item = player.inventory[slot]; Hash(item.type); Hash(item.prefix); Hash(item.stack); Hash(item.damage);
        }
        for (int slot = 0; slot < 10; slot++)
        {
            var item = player.GetEffectiveArmor(slot); Hash(item.type); Hash(item.prefix); Hash(item.stack); Hash(item.damage);
        }
        for (int slot = 0; slot < player.buffType.Length; slot++) Hash(player.buffTime[slot] > 0 ? player.buffType[slot] : 0);
        if (state.HasDigest && state.LastDigest == digest && state.Snapshots.Count > 0) return;
        var inventory = ImmutableArray.CreateBuilder<ArrowItemProjection>(59);
        var armor = ImmutableArray.CreateBuilder<ArrowItemProjection>(10);
        for (int slot = 0; slot < 59; slot++)
        {
            var item = player.inventory[slot]; inventory.Add(new(slot, item.type, item.prefix, item.stack, item.damage));
        }
        for (int slot = 0; slot < 10; slot++)
        {
            var item = player.GetEffectiveArmor(slot); armor.Add(new(slot, item.type, item.prefix, item.stack, item.damage));
        }
        var buffs = ImmutableArray.CreateBuilder<int>();
        for (int slot = 0; slot < player.buffType.Length; slot++)
            if (player.buffTime[slot] > 0 && player.buffType[slot] > 0) buffs.Add(player.buffType[slot]);
        if (state.Snapshots.Count == SnapshotCapacity)
        { state.Snapshots.Dequeue(); AddGap(state, ArrowCandidateGap.SnapshotHistoryOverflow); }
        state.Snapshots.Enqueue(new(tick, player.selectedItem, inventory.MoveToImmutable(), armor.MoveToImmutable(),
            buffs.ToImmutable(), player.mount.Active ? player.mount.Type : -1, player.stealth)
            { ObservedBowMultiplier = player.bowEffectiveDamage, ObservedSharpBarb = player.accSharpBarb });
        state.LastDigest = digest; state.HasDigest = true;
        InvalidateDamageResults(state);
    }

    public ArrowCandidateEnvelope? Observe(M2Packet packet, SessionKey session, TSPlayer actor)
    {
        if (!Healthy || Environment.CurrentManagedThreadId != updateThread || session.WorldEpoch != worldEpoch ||
            actor.Index != session.Slot || Main.netMode != 2 || Main.myPlayer != 255 ||
            (uint)session.Slot >= states.Length || states[session.Slot] is not { } state || state.Session != session ||
            !M4CombatProjectileReader.TryRead(packet, out var parsed) || parsed!.Type != ProjectileID.WoodenArrowFriendly ||
            ((ProjectileKey)parsed.Key).Spawner != session.Slot) return null;
        try
        {
            Capture(state, actor);
            var envelope = new ArrowCandidateEnvelope(session, tick, parsed.Damage, candidates, maximumAmmo, state.Snapshots.ToImmutableArray(),
                state.Gaps | ArrowCandidateGap.NativeCreationBranchesNotClosed) { CombatExports = state.Exports.ToImmutableArray(), SourceResetInterval = resetIntervals?.Capture(session) };
            if (state.CachedDamageResults is null || state.CachedDamageRevision != state.DamageRevision)
            {
                state.CachedDamageResults = BuildDamageResults(envelope, state.ExportHistoryLost);
                state.CachedDamageRevision = state.DamageRevision;
            }
            // Declared damage is deliberately outside the cache key: the union describes sources,
            // but supported matches must still count each newly observed declaration.
            if (state.CachedDamageResults.SupportedResults.Contains(parsed.Damage))
                DamageUnionSupportedMatches = Increment(DamageUnionSupportedMatches);
            return envelope with { DamageResults = state.CachedDamageResults };
        }
        catch (Exception error) { Fail(error); return null; }
    }

    private void OnSource(Projectile entity, HookEvents.Terraria.Projectile.ApplyStatsFromSourceEventArgs args)
    {
        if (!installed || failed || entity.type != ProjectileID.WoodenArrowFriendly) return;
        try
        {
            VerifyHookContext();
            if ((uint)entity.key.Spawner < states.Length && states[entity.key.Spawner] is { } state)
                AddGap(state, ArrowCandidateGap.ServerArrowSource);
        }
        catch (Exception error) { Fail(error); }
    }
    private void OnExport(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        PrepareCombatExport(args);
        if (!installed || failed || !args.ContinueExecution || Main.netMode != 2 || args.msgType is not (27 or 88)) return;
        try
        {
            if (args.msgType == 27)
            {
                if ((uint)args.number >= Main.projectile.Length || Main.projectile[args.number] is not { active: true, type: 1 } entity) return;
                VerifyHookContext();
                int owner = entity.key.Spawner;
                if ((uint)owner < states.Length && states[owner] is { } state &&
                    (args.remoteClient == owner || args.remoteClient == -1 && args.ignoreClient != owner))
                    AddGap(state, ArrowCandidateGap.ArrowExportToOwner);
                return;
            }
            // 88 can change damage, shoot, ammo, useAmmo and notAmmo. Color/size-only exports do not erase this context.
            byte flags = (byte)args.number2, secondary = (byte)args.number3;
            if ((flags & 0x22) == 0 && ((flags & 0x80) == 0 || (secondary & 0x38) == 0)) return;
            VerifyHookContext();
            for (int slot = 0; slot < states.Length; slot++)
                if (states[slot] is { } state && (args.remoteClient == slot || args.remoteClient == -1 && args.ignoreClient != slot))
                    AddGap(state, ArrowCandidateGap.ServerItemCustomization);
        }
        catch (Exception error) { Fail(error); }
    }
    private static void AddGap(State state, ArrowCandidateGap gap)
    {
        var next = state.Gaps | gap;
        if (next == state.Gaps) return;
        state.Gaps = next;
        InvalidateDamageResults(state);
    }
    private static void InvalidateDamageResults(State state)
    {
        // One immutable union per bounded session state. Clear even if the diagnostic revision
        // counter saturates; wraparound or saturation must never make changed input look current.
        state.CachedDamageResults = null;
        state.DamageRevision = Increment(state.DamageRevision);
    }
    private void VerifyHookContext()
    {
        if (Environment.CurrentManagedThreadId != updateThread || worldEpoch <= 0)
            throw new InvalidOperationException("Arrow candidate hook ran outside the verified update context.");
    }
    private void Fail(Exception error)
    {
        failed = true;
        try { Dispose(); } catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
        IntegrityFault?.Invoke(error);
    }
}
