using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Progression;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Plugin.TShock;

/// <summary>Packet-time item evidence for bounded F08 diagnostics.</summary>
public readonly record struct M18GroundItemPacketTrace(
    SessionKey Session,
    long AccountId,
    byte PacketId,
    int TargetSlot,
    int Stack,
    int Prefix,
    int Type,
    float RequestX,
    float RequestY,
    float VelocityX,
    float VelocityY,
    byte Flags,
    int PayloadLength,
    bool TargetExists,
    bool TargetActive,
    int ServerTargetType,
    int ServerTargetStack,
    bool TargetBeingGrabbed,
    bool AlreadyCancelled,
    bool AttributionComplete)
{
    public bool RequestCoordinatesComplete { get; init; } = true;
    public float ActorX { get; init; }
    public float ActorY { get; init; }
    public bool ActorPositionSnapshotComplete { get; init; }
}

/// <summary>Constructs small immutable inputs from the locked runtime; no world or inventory writes.</summary>
public sealed class M2BusinessAdapter
{
    private enum Producer { ItemTable, BuffTable, ProjectileTable, ProjectileOwnership, Progression, Inventory, ProjectileAuthority, Healing, PlayerControls }
    private int failedProducers;
    private readonly string fingerprint;
    private readonly ImmutableArray<ProgressionRule> itemProgression;
    private readonly ImmutableArray<ProgressionEntityRule> entityProgression;
    private readonly ImmutableDictionary<(ProgressionSubjectKind Kind, int Id), ImmutableArray<ProgressionRule>> progressionIndex;
    private readonly ImmutableDictionary<string, ProgressionEntityRule> entityByRule;
    private readonly ImmutableDictionary<int, ItemTypeDefinition>.Builder definitions = ImmutableDictionary.CreateBuilder<int, ItemTypeDefinition>();
    private int nextItem = -48;
    private VersionedItemCatalog? items;
    private BuffCatalog? buffs;
    private Array? buffSource;
    private FieldInfo? buffField;
    private PropertyInfo? buffMaximum;
    private PropertyInfo? buffSelfOnly;
    private PropertyInfo? buffWithoutPvp;
    private ProjectileCatalog? projectileCatalog;
    private bool[]? projectileSource;
    private long tick;
    private readonly Dictionary<uint, (SessionKey Session, int Type, long Tick)> pendingProjectiles = [];
    private readonly Dictionary<uint, (SessionKey Session, int Type, long Tick)> ownedProjectiles = [];
    private ImmutableDictionary<string, JsonElement> worldFacts = ImmutableDictionary<string, JsonElement>.Empty;
    private DateTimeOffset worldCaptured;
    private M17WorldFactSnapshot? worldFactSnapshot;
    private long worldRevision;
    private int worldStableTicks;
    private long capturedWorldEpoch;
    private int capturedWorldId;
    private long observedWorldEpoch = -1;
    private int observedWorldId;
    private readonly M18WorldEditQueue worldEditQueue;
    private readonly M18ParticleQueue particleQueue;
    private readonly M18GroundItemClearQueue groundItemClearQueue;
    private readonly M18WorldItemGenerationTracker groundItemGenerations = new();

    public M2BusinessAdapter(string fingerprint, string dataDirectory,
        M18GroundItemClearQueueOptions? groundItemClearQueueOptions = null,
        M18WorldEditQueueOptions? worldEditQueueOptions = null,
        M18ParticleQueueOptions? particleQueueOptions = null)
    {
        this.fingerprint = fingerprint;
        worldEditQueue = new(worldEditQueueOptions ?? M18WorldEditQueueOptions.Disabled);
        particleQueue = new(particleQueueOptions ?? M18ParticleQueueOptions.Disabled);
        // The currently available packet151 and server snapshots cannot prove
        // a malicious source distinct from ordinary aligned pickups.
        groundItemClearQueue = new((groundItemClearQueueOptions ?? M18GroundItemClearQueueOptions.Disabled)
            with { EnablePermanentSanctions = false });
        var itemFile = Path.Combine(dataDirectory, "candidates.json");
        var entityFile = Path.Combine(dataDirectory, "entity-candidates.json");
        itemProgression = File.Exists(itemFile) ? ProgressionCatalog.Load(itemFile).Rules : [];
        entityProgression = File.Exists(entityFile) ? ProgressionEntityCatalog.Load(entityFile).Rules : [];
        // Optional data must fit beside the compiled rules before Core's registry is built.
        // Reserved/cross-catalog IDs and excess data are catalog faults, never a reason to
        // abort independent protocol rules or to widen production qualification.
        var mergedIds = M2RuleRegistry.Create(fingerprint, ExecutionScope.ObserveOnly)
            .Select(rule => rule.RuleId).ToHashSet(StringComparer.Ordinal);
        foreach (string id in itemProgression.Select(rule => rule.Id).Concat(entityProgression.Select(entry => entry.Rule.Id)))
            if (!mergedIds.Add(id) || mergedIds.Count > 128) // Existing AntiCheatEngine business registry capacity.
                throw new InvalidDataException("Optional progression IDs conflict with the compiled registry or exceed its capacity.");
        progressionIndex = itemProgression.SelectMany(rule => rule.Items.Select(id => (Kind: ProgressionSubjectKind.Item, Id: id, Rule: rule)))
            .Concat(entityProgression.SelectMany(entry => entry.Rule.Items.Select(id => (Kind: entry.SubjectKind, Id: id, Rule: entry.Rule))))
            .GroupBy(x => (x.Kind, x.Id)).ToImmutableDictionary(x => x.Key, x => x.Select(y => y.Rule).ToImmutableArray());
        entityByRule = entityProgression.ToImmutableDictionary(x => x.Rule.Id);
    }

    public IEnumerable<string> ProgressionRuleIds => itemProgression.Select(x => x.Id).Concat(entityProgression.Select(x => x.Rule.Id));
    public bool ItemTableReady => items is not null;
    public bool BuffTableReady => buffs is not null;
    public bool ProjectileTableReady => projectileCatalog is not null;
    public bool WorldEditQueueEnabled => worldEditQueue.Enabled;
    public bool ParticleQueueEnabled => particleQueue.Enabled;
    public bool GroundItemClearQueueEnabled => groundItemClearQueue.Enabled;
    public M3InventoryContexts? InventoryContexts { get; set; }
    public Action<string, Exception>? IntegrityFault { get; set; }
    public Action<M18GroundItemPacketTrace>? GroundItemPacketObserved { get; set; }
    public Action<M18GroundItemPacketTrace, BusinessRuleResult>? GroundItemDecisionObserved { get; set; }

    // One bit per fixed producer bounds both retries and health reports. A producer fault is never
    // a player observation; independently sourced rules continue in this same update or packet.
    private bool Run(Producer producer, Action action)
    {
        int bit = 1 << (int)producer;
        if ((Volatile.Read(ref failedProducers) & bit) != 0) return false;
        try { action(); return true; }
        catch (Exception exception)
        {
            bool first = (Interlocked.Or(ref failedProducers, bit) & bit) == 0;
            if (producer == Producer.ItemTable) items = null;
            if (producer == Producer.BuffTable) buffs = null;
            if (producer == Producer.ProjectileTable) projectileCatalog = null;
            if (producer == Producer.Inventory) InventoryContexts = null;
            if (first) IntegrityFault?.Invoke(producer.ToString(), exception);
            return false;
        }
    }

    public void ResetWorld()
    {
        worldEditQueue.Reset();
        particleQueue.Reset();
        groundItemClearQueue.Reset();
        groundItemGenerations.Reset();
        Run(Producer.Inventory, () => InventoryContexts?.ResetWorld());
        Run(Producer.ProjectileOwnership, () => { pendingProjectiles.Clear(); ownedProjectiles.Clear(); });
        worldFacts = ImmutableDictionary<string, JsonElement>.Empty;
        worldFactSnapshot = null;
        capturedWorldEpoch = 0;
        worldCaptured = default;
        worldStableTicks = 0;
        worldRevision++;
    }

    public void Forget(SessionKey session)
    {
        worldEditQueue.Forget(session);
        particleQueue.Forget(session);
        groundItemClearQueue.Forget(session);
    }

    public BusinessRuleResult? EvaluateParticle(M18ParticlePacket packet, SessionKey session,
        TSPlayer actor, bool alreadyCancelled)
    {
        if (alreadyCancelled || packet.ParticleType != M18ParticleQueue.StormLightningType)
            return null;

        // The transport binding owns this request. The invoking-player byte
        // remains a payload claim and cannot debit a different session.
        bool attributed = actor.IsLoggedIn && actor.Account is { ID: > 0 } &&
            actor.Index == session.Slot;
        var decision = particleQueue.Observe(tick, new(
            session,
            attributed ? actor.Account!.ID : 0,
            packet.ParticleType,
            packet.InvokingPlayer,
            float.IsFinite(packet.PositionX) && float.IsFinite(packet.PositionY),
            float.IsFinite(packet.MovementX) && float.IsFinite(packet.MovementY),
            ParseComplete: true,
            ClientOrigin: true,
            BeforeSideEffects: true,
            AttributionComplete: attributed)
        {
            PayloadIdentityMatchesSession = packet.InvokingPlayer == session.Slot,
        });
        return decision.IsResourceBlock
            ? M18ParticleQueueRules.ResourceBlock(Input(session, true, false),
                ImmutableDictionary<string, string>.Empty, decision)
            : null;
    }

    public void Update(Func<int, SessionSnapshot?> sessionAtSlot, long worldEpoch)
    {
        // World lifetime is independent of the progression facts producer. If fact capture fails,
        // it must not repeatedly clear healthy container grants or projectile ownership next tick.
        if (observedWorldEpoch != worldEpoch || observedWorldId != Main.worldID)
        {
            ResetWorld();
            observedWorldEpoch = worldEpoch;
            observedWorldId = Main.worldID;
        }
        worldEditQueue.AdvanceWorld(worldEpoch);
        particleQueue.AdvanceWorld(worldEpoch);
        groundItemClearQueue.AdvanceWorld(worldEpoch);
        tick++;
        Run(Producer.Inventory, () => InventoryContexts?.Update());
        // Static version tables are finite and immutable after construction; no per-packet item creation.
        Run(Producer.ItemTable, () =>
        {
            if (items is null && Main.netMode == 2 && Main.maxTilesX > 0 && ItemID.Count < 20000 && PrefixID.Count < 256)
            {
                for (int budget = 0; budget < 128 && nextItem < ItemID.Count; budget++, nextItem++)
                {
                    var item = new Item();
                    item.netDefaults(nextItem);
                    Run(Producer.Inventory, () => InventoryContexts?.AddItemDefinition(item));
                    var prefixes = ImmutableHashSet.CreateBuilder<int>();
                    prefixes.Add(0);
                    for (int prefix = 1; prefix < PrefixID.Count; prefix++) if (item.CanRollPrefix(prefix)) prefixes.Add(prefix);
                    definitions[nextItem] = new(item.type, item.maxStack, prefixes.ToImmutable(), true);
                }
                if (nextItem == ItemID.Count)
                {
                    items = new(fingerprint, "runtime326-netDefaults-CanRollPrefix", -48, ItemID.Count, PrefixID.Count, definitions.ToImmutable(), true);
                    Run(Producer.Inventory, () => InventoryContexts?.CompleteItemCatalog());
                }
            }
        });
        Run(Producer.BuffTable, () => { if (buffs is null) TryReadBuffTable(); });
        Run(Producer.ProjectileTable, () =>
        {
            if (projectileCatalog is null && Main.projHostile is { Length: > 0 } hostile && hostile.Length == ProjectileID.Count)
            {
                projectileSource = hostile;
                projectileCatalog = new(fingerprint, "runtime326-projHostile", Enumerable.Range(1, hostile.Length - 1)
                    .ToImmutableDictionary(id => id, id => new ProjectileCategory(hostile[id] ? ProjectileSourceConstraint.ServerOnly : ProjectileSourceConstraint.Allowed, [], [])), true);
            }
        });
        Run(Producer.ProjectileOwnership, () =>
        {
            foreach (var entry in pendingProjectiles.Take(32).ToArray())
            {
                if (tick - entry.Value.Tick >= 2 || tick <= entry.Value.Tick)
                {
                    pendingProjectiles.Remove(entry.Key);
                    continue;
                }
                var key = (ProjectileKey)entry.Key;
                if (M2ProjectileLookup.TryGet(key, out var projectile, out _) && projectile!.active &&
                    projectile.owner == entry.Value.Session.Slot && projectile.type == entry.Value.Type &&
                    sessionAtSlot(projectile.owner)?.Key == entry.Value.Session)
                {
                    if (ownedProjectiles.Count < 1024 || ownedProjectiles.ContainsKey(entry.Key))
                        ownedProjectiles[entry.Key] = entry.Value;
                    pendingProjectiles.Remove(entry.Key);
                }
            }
            // Bounded dictionary scan once per second, never a scan of the world or all entities per packet.
            if (tick % 60 == 0)
                foreach (var entry in ownedProjectiles.Where(x => tick - x.Value.Tick > 1800 || sessionAtSlot(x.Value.Session.Slot)?.Key != x.Value.Session).ToArray())
                    ownedProjectiles.Remove(entry.Key);
        });
        Run(Producer.Progression, () => CaptureWorldFacts(worldEpoch));
    }

    private void TryReadBuffTable()
    {
        // These exact internal symbols are audited for the hash-locked TShock build. Failure removes only C5's table.
        var bouncer = typeof(TSPlayer).Assembly.GetType("TShockAPI.Bouncer");
        buffField ??= bouncer?.GetField("PlayerAddBuffWhitelist", BindingFlags.Static | BindingFlags.NonPublic);
        if (buffField?.GetValue(null) is not Array source || source.Length != BuffID.Count) return;
        var builder = ImmutableDictionary.CreateBuilder<int, BuffDefinition>();
        for (int id = 0; id < source.Length; id++)
        {
            if (source.GetValue(id) is not { } value) continue;
            var type = value.GetType();
            buffMaximum ??= type.GetProperty("MaxTicks");
            buffSelfOnly ??= type.GetProperty("CanOnlyBeAppliedToSender");
            buffWithoutPvp ??= type.GetProperty("CanBeAddedWithoutHostile");
            if (buffMaximum?.GetValue(value) is not int maximum || maximum <= 0 ||
                buffSelfOnly?.GetValue(value) is not bool selfOnly || buffWithoutPvp?.GetValue(value) is not bool withoutPvp) return;
            builder[id] = new(maximum, selfOnly, !withoutPvp);
        }
        buffSource = source;
        buffs = new(fingerprint, "locked-core-PlayerAddBuffWhitelist", BuffID.Count, builder.ToImmutable(), true);
    }

    private RuleInputContext Input(SessionKey key, bool snapshotComplete, bool exceptionsExcluded) =>
        new(key, fingerprint, fingerprint, true, snapshotComplete, true, exceptionsExcluded);

    public IReadOnlyList<BusinessRuleResult> Evaluate(M2Packet packet, SessionKey session, TSPlayer actor,
        Func<int, (SessionSnapshot? Session, TSPlayer? Player)> targetAtSlot, bool alreadyCancelled)
    {
        var results = new List<BusinessRuleResult>(8);
        var body = packet.Payload;
        if (packet.Kind == M2PacketKind.PlayerSlot && body[0] == session.Slot)
        {
            int slot = M2PacketReader.Int16(body, 1), stack = M2PacketReader.Int16(body, 3), type = M2PacketReader.Int16(body, 6);
            Run(Producer.ItemTable, () =>
            {
                if (items is not null)
                    results.Add(InventoryRules.Evaluate(new(type, stack, body[5], slot, ItemLocation.Inventory),
                        new(Input(session, true, false), PlayerItemSlotID.Count, !actor.HasSentInventory || actor.IgnoreSSCPackets,
                            true, false), items));
            });
            if (slot >= PlayerItemSlotID.Armor0 && slot < PlayerItemSlotID.Armor0 + 20 && actor.TPlayer is { } player)
            {
                int armorSlot = slot - PlayerItemSlotID.Armor0;
                Run(Producer.Inventory, () => { if (InventoryContexts is not null) results.Add(InventoryContexts.EvaluateEquipment(session, actor, armorSlot, type)); });
                AddProgression(results, session, actor, ProgressionSubjectKind.Item, type, ProgressionActionKind.Equip,
                    null, !actor.HasSentInventory || actor.IgnoreSSCPackets);
            }
        }
        else if (packet.Kind == M2PacketKind.ChestItem)
        {
            int id = M2PacketReader.Int16(body, 0), slot = body[2];
            Run(Producer.Inventory, () => { if (InventoryContexts is not null) results.Add(InventoryContexts.EvaluateContainerWrite(session, actor, id, slot)); });
            Run(Producer.ItemTable, () =>
            {
                var chest = id >= 0 && id < Main.chest.Length ? Main.chest[id] : null;
                if (items is not null && chest is not null)
                    results.Add(InventoryRules.Evaluate(new(M2PacketReader.Int16(body, 6), M2PacketReader.Int16(body, 3), body[5], slot, ItemLocation.Container),
                        new(Input(session, true, false), chest.maxItems, false, true, false), items));
            });
        }
        else if (packet.Kind == M2PacketKind.WorldItemDrop)
        {
            int id = M2PacketReader.Int16(body, 0);
            int stack = M2PacketReader.Int16(body, 18);
            int prefix = body[20];
            int type = M2PacketReader.Int16(body, 22);
            float requestX = M2PacketReader.Single(body, 2);
            float requestY = M2PacketReader.Single(body, 6);
            bool attributed = IsAccountActor(session, actor);
            var actorPosition = GetActorPosition(session, actor);
            var item = GetWorldItem(id);
            var trace = new M18GroundItemPacketTrace(session, attributed ? actor.Account!.ID : 0,
                packet.MessageId, id, stack, prefix, type, requestX, requestY,
                M2PacketReader.Single(body, 10), M2PacketReader.Single(body, 14), body[21],
                body.Length, item is not null, item?.active == true, item?.type ?? 0,
                item?.stack ?? 0, item?.beingGrabbed == true, alreadyCancelled, attributed)
            {
                ActorX = actorPosition.X,
                ActorY = actorPosition.Y,
                ActorPositionSnapshotComplete = actorPosition.Complete,
            };
            EmitGroundItemPacket(trace);
            var result = ObserveGroundItemClear(packet, session, actor, alreadyCancelled,
                id, stack, type, requestX, requestY, item);
            if (result is not null)
            {
                results.Add(result);
                EmitGroundItemDecision(trace, result);
            }
        }
        else if (packet.Kind == M2PacketKind.WorldItemDespawn)
        {
            // Protocol-326 packet151 has only a two-byte item id. Coordinates
            // come from the server item and actor snapshots, never the wire.
            int id = M2PacketReader.Int16(body, 0);
            bool attributed = IsAccountActor(session, actor);
            var actorPosition = GetActorPosition(session, actor);
            var item = GetWorldItem(id);
            var trace = new M18GroundItemPacketTrace(session, attributed ? actor.Account!.ID : 0,
                packet.MessageId, id, 0, 0, 0, float.NaN, float.NaN, 0, 0, 0,
                body.Length, item is not null, item?.active == true, item?.type ?? 0,
                item?.stack ?? 0, item?.beingGrabbed == true, alreadyCancelled, attributed)
            {
                RequestCoordinatesComplete = false,
                ActorX = actorPosition.X,
                ActorY = actorPosition.Y,
                ActorPositionSnapshotComplete = actorPosition.Complete,
            };
            EmitGroundItemPacket(trace);
            var result = ObserveGroundItemDespawn(packet, session, actor, alreadyCancelled, id, item);
            if (result is not null)
            {
                results.Add(result);
                EmitGroundItemDecision(trace, result);
            }
        }
        else if (packet.Kind is M2PacketKind.ProjectileNew or M2PacketKind.ProjectileDestroy)
        {
            var key = (ProjectileKey)M2PacketReader.Int32(body, 0);
            bool creating = packet.Kind == M2PacketKind.ProjectileNew;
            int type = creating ? M2PacketReader.Int16(body, 20) : 0;
            Projectile? existing = null;
            bool lookupComplete = false, keyExists = false, found = false;
            bool authorityAvailable = Run(Producer.ProjectileAuthority, () =>
            {
                keyExists = M2ProjectileLookup.TryGet(key, out existing, out lookupComplete);
                found = keyExists && existing!.active;
            });
            SessionKey ownerSession = default, retainedOwnerSession = default;
            Run(Producer.ProjectileOwnership, () =>
            {
                if (ownedProjectiles.TryGetValue(key.bits, out var ownership) && tick - ownership.Tick <= 1800)
                {
                    // The full-key, bounded post-receive ownership record is only a negative
                    // constraint for stale cleanup. Never replace it with the session now
                    // occupying a player slot, or promote a pending27 into ownership.
                    retainedOwnerSession = ownership.Session;
                    if (keyExists && ownership.Type == existing!.type && ownership.Session.Slot == existing.owner)
                        ownerSession = ownership.Session;
                }
            });
            // Only29 needs the inactive object's real owner. For27, inactive still means
            // fresh creation, including exact-key reuse after the native generation wraps.
            var state = keyExists && (!creating || found) ? new ProjectileState(new(key.Spawner, key.Index, key.Generation), existing!.owner,
                ownerSession, existing.type, existing.active) : null;
            var operation = creating ? found ? ProjectileOperation.Update : ProjectileOperation.Create : ProjectileOperation.Destroy;
            var observation = new ProjectileObservation(new(key.Spawner, key.Index, key.Generation), operation, type);
            if (authorityAvailable) Run(Producer.ProjectileAuthority, () => results.Add(ProjectileRules.EvaluateAuthority(observation, new(Input(session, true, true), session,
                session.WorldEpoch, tick, tick, state, lookupComplete, creating && !found, false, false, false,
                M2ProjectileLookup.KeyIdentityCount, ProjectileID.Count, retainedOwnerSession))));
            bool finite = creating ? Enumerable.Range(0, 4).All(i => float.IsFinite(M2PacketReader.Single(body, 4 + i * 4)))
                : float.IsFinite(M2PacketReader.Single(body, 4)) && float.IsFinite(M2PacketReader.Single(body, 8));
            // Native Projectile.Update sends NaN/NaN for silent cleanup. MessageBuffer29
            // deactivates an owned object without assigning these values to its position or
            // executing Kill effects. Authority checks above still reject foreign objects.
            bool nativeSilentCleanup = !creating && float.IsNaN(M2PacketReader.Single(body, 4)) &&
                float.IsNaN(M2PacketReader.Single(body, 8));
            if (!finite && !nativeSilentCleanup) results.Add(Safety("C0.ProjectileNumeric", "nonfinite-projectile-vector"));
            if (creating)
            {
                Run(Producer.ProjectileTable, () =>
                {
                    if (authorityAvailable && projectileCatalog is not null)
                    {
                        bool unchanged = ReferenceEquals(Main.projHostile, projectileSource) && type > 0 && type < projectileSource!.Length &&
                            projectileCatalog.Types.TryGetValue(type, out var category) &&
                            projectileSource[type] == (category.Constraint == ProjectileSourceConstraint.ServerOnly);
                        // A core hostile flag supports the existing safety filter, but cannot prove every creation mechanism.
                        results.Add(!unchanged ? new("C2.ProjectileSource", "1.0.0", ControlAction.Unknown, Verdict.Unknown,
                            "projectile-category-changed-since-snapshot", false, false, ImmutableDictionary<string, string>.Empty) :
                            projectileSource![type] && !found ? new("C2.ProjectileSource", "1.0.0", ControlAction.Block, Verdict.UnsafeInput,
                                "core-hostile-projectile-filter-without-source-proof", false, false, ImmutableDictionary<string, string>.Empty) :
                            ProjectileRules.EvaluateSource(observation, new(Input(session, true, false), !found,
                                false, false, null, null, false), projectileCatalog));
                    }
                });
                if (authorityAvailable) AddProgression(results, session, actor, ProgressionSubjectKind.Projectile, type, ProgressionActionKind.CreateProjectile, null, found);
                // An absent entity with a fresh candidate can reuse a wrapped key; it must not inherit a former session's ownership.
                Run(Producer.ProjectileOwnership, () =>
                {
                    if (!found && lookupComplete && key.Spawner == session.Slot) ownedProjectiles.Remove(key.bits);
                    if (!found && lookupComplete && key.Spawner == session.Slot && finite && !alreadyCancelled
                        && results.All(x => x.Action != ControlAction.Block) && pendingProjectiles.Count < 1024)
                        pendingProjectiles[key.bits] = (session, type, tick);
                });
            }
        }
        else if (packet.Kind is M2PacketKind.Buff or M2PacketKind.Heal)
        {
            Run(packet.Kind == M2PacketKind.Buff ? Producer.BuffTable : Producer.Healing, () =>
            {
                int recipient = body[0];
                var target = targetAtSlot(recipient);
                var targetKey = target.Session?.Key ?? default;
                var context = new CombatContext(Input(session, target.Session is not null, true),
                    new(targetKey, target.Player?.Active == true, target.Player?.TPlayer.hostile == true), targetKey,
                    target.Session is not null, false, target.Player is null ? null : actor.IsInRange(target.Player.TileX, target.Player.TileY, 50));
                if (packet.Kind == M2PacketKind.Buff && buffs is not null)
                {
                    int buffId = (ushort)M2PacketReader.Int16(body, 1);
                    if (CurrentBuffEntryMatches(buffId))
                        results.Add(CombatRules.EvaluateBuff(new(recipient, buffId, M2PacketReader.Int32(body, 3)), context, buffs));
                    else results.Add(new("C5.BuffProtocol", "1.0.0", ControlAction.Unknown, Verdict.Unknown,
                        "core-buff-entry-changed-since-snapshot", false, false, ImmutableDictionary<string, string>.Empty));
                }
                else if (packet.Kind == M2PacketKind.Heal)
                    results.Add(CombatRules.EvaluateHeal(new(recipient, M2PacketReader.Int16(body, 1)), context,
                        new(fingerprint, "healing-cause-unverified", false, session, targetKey, 0, false)));
            });
        }
        else if (packet.Kind == M2PacketKind.PlayerUpdate)
        {
            Run(Producer.PlayerControls, () => ObserveGroundPlayerControls(packet, session, actor, alreadyCancelled));
            if (!float.IsFinite(M2PacketReader.Single(body, 6)) || !float.IsFinite(M2PacketReader.Single(body, 10)))
                results.Add(Safety("A05.PlayerNumeric", "nonfinite-player-position"));
            Run(Producer.PlayerControls, () =>
            {
                if (body[0] == session.Slot && (body[1] & 32) != 0 && body[5] < actor.TPlayer.inventory.Length)
                    AddProgression(results, session, actor, ProgressionSubjectKind.Item, actor.TPlayer.inventory[body[5]].type,
                        ProgressionActionKind.UseItem, null, actor.IgnoreSSCPackets);
            });
        }
        else if (packet.Kind is M2PacketKind.Tile or M2PacketKind.Liquid)
        {
            var queueDecision = ObserveWorldEdit(packet, session, actor, alreadyCancelled);
            if (queueDecision.IsResourceBlock)
                results.Add(M18WorldEditQueueRules.ResourceBlock(Input(session, true, false),
                    ImmutableDictionary<string, string>.Empty, queueDecision));

            if (packet.Kind == M2PacketKind.Tile && body[0] is 1 or 3 or 21 or 22)
            {
                bool wall = body[0] is 3 or 22;
                AddProgression(results, session, actor, wall ? ProgressionSubjectKind.Wall : ProgressionSubjectKind.Tile,
                    M2PacketReader.Int16(body, 5), wall ? ProgressionActionKind.PlaceWall : ProgressionActionKind.PlaceTile,
                    wall ? null : body[7], false);
            }
        }
        return results;
    }

    private M18WorldEditQueueDecision ObserveWorldEdit(M2Packet packet, SessionKey session,
        TSPlayer actor, bool alreadyCancelled)
    {
        if (alreadyCancelled)
            return M18WorldEditQueueDecision.Disabled;

        bool attributed = actor.IsLoggedIn && actor.Account is { ID: > 0 } &&
            actor.Index == session.Slot;
        long accountId = attributed ? actor.Account!.ID : 0;
        if (packet.Kind == M2PacketKind.Tile)
        {
            int operation = packet.Payload[0];
            return worldEditQueue.Observe(tick, new(session, accountId,
                M18WorldEditKind.Tile,
                M2PacketReader.Int16(packet.Payload, 1),
                M2PacketReader.Int16(packet.Payload, 3),
                operation,
                M2PacketReader.Int16(packet.Payload, 5),
                0,
                operation is 21 or 22 ? 2 : 1,
                ParseComplete: true,
                ClientOrigin: true,
                BeforeSideEffects: true,
                AttributionComplete: attributed));
        }

        return worldEditQueue.Observe(tick, new(session, accountId,
            M18WorldEditKind.Liquid,
            M2PacketReader.Int16(packet.Payload, 0),
            M2PacketReader.Int16(packet.Payload, 2),
            Operation: 0,
            Data: packet.Payload[5],
            Amount: packet.Payload[4],
            WorkUnits: 1,
            ParseComplete: true,
            ClientOrigin: true,
            BeforeSideEffects: true,
            AttributionComplete: attributed));
    }

    private static bool IsAccountActor(SessionKey session, TSPlayer actor) =>
        actor.IsLoggedIn && actor.Account is { ID: > 0 } && GetActorPosition(session, actor).Complete;

    private static (float X, float Y, bool Complete) GetActorPosition(SessionKey session, TSPlayer actor)
    {
        if (session.ServerRunId == Guid.Empty || session.WorldEpoch <= 0 || session.Generation <= 0 ||
            session.Slot < 0 || session.Slot >= Main.player.Length || actor.Index != session.Slot)
            return (float.NaN, float.NaN, false);
        var current = Main.player[session.Slot];
        if (current is null || !current.active || current.whoAmI != session.Slot ||
            !ReferenceEquals(actor.TPlayer, current))
            return (float.NaN, float.NaN, false);
        float x = current.position.X, y = current.position.Y;
        return (x, y, float.IsFinite(x) && float.IsFinite(y));
    }

    private static WorldItem? GetWorldItem(int id) =>
        id >= 0 && id < Main.maxItems && id < Main.item.Length ? Main.item[id] : null;

    private void EmitGroundItemPacket(M18GroundItemPacketTrace trace)
    {
        try { GroundItemPacketObserved?.Invoke(trace); }
        catch { /* Diagnostics cannot affect native packet admission. */ }
    }

    private void EmitGroundItemDecision(M18GroundItemPacketTrace trace, BusinessRuleResult result)
    {
        try { GroundItemDecisionObserved?.Invoke(trace, result); }
        catch { /* Diagnostics cannot affect native packet admission. */ }
    }

    private BusinessRuleResult? ObserveGroundItemClear(M2Packet packet, SessionKey session,
        TSPlayer actor, bool alreadyCancelled, int id, int stack, int type,
        float requestX, float requestY, WorldItem? item)
    {
        // Packet90 updates a live item; a nonzero packet21 type allocates or
        // updates one. Neither shape is a ground-item clear candidate.
        if (alreadyCancelled || packet.MessageId != (byte)PacketTypes.ItemDrop || stack != 0 || type != 0)
            return null;

        bool attributed = IsAccountActor(session, actor);
        var actorPosition = GetActorPosition(session, actor);
        bool snapshotComplete = item is not null && item.active && item.type > 0 &&
            item.stack > 0 && !item.beingGrabbed;
        // Observe incomplete snapshots too: an air transition or a replaced
        // inner Item is a lifecycle boundary, even though this request cannot
        // enter the F08 evidence queue.
        int generation = groundItemGenerations.Observe(id, item);
        bool finite = float.IsFinite(requestX) && float.IsFinite(requestY);
        bool normalPickupShape = snapshotComplete && finite &&
            MathF.Abs(requestX - item!.position.X) <= 64f &&
            MathF.Abs(requestY - item.position.Y) <= 64f;
        var decision = groundItemClearQueue.Observe(tick, new(
            session, attributed ? actor.Account!.ID : 0, id, generation,
            snapshotComplete ? item!.type : 0, snapshotComplete ? item!.stack : 0,
            snapshotComplete ? item!.position.X : 0f, snapshotComplete ? item!.position.Y : 0f,
            requestX, requestY, snapshotComplete, item?.active == true,
            item?.beingGrabbed == true, normalPickupShape,
            ParseComplete: finite, ClientOrigin: true, BeforeSideEffects: true,
            AttributionComplete: attributed)
        {
            PacketId = packet.MessageId,
            AlreadyCancelled = alreadyCancelled,
            ActorX = actorPosition.X,
            ActorY = actorPosition.Y,
            ActorPositionSnapshotComplete = actorPosition.Complete,
        });
        return M18GroundItemClearQueueRules.Observe(Input(session, true, false), decision);
    }

    private BusinessRuleResult? ObserveGroundItemDespawn(M2Packet packet, SessionKey session,
        TSPlayer actor, bool alreadyCancelled, int id, WorldItem? item)
    {
        if (alreadyCancelled || packet.MessageId != (byte)PacketTypes.SyncItemDespawn)
            return null;

        bool attributed = IsAccountActor(session, actor);
        var actorPosition = GetActorPosition(session, actor);
        bool snapshotComplete = item is not null && item.active && item.type > 0 &&
            item.stack > 0 && !item.beingGrabbed;
        int generation = groundItemGenerations.Observe(id, item);
        var decision = groundItemClearQueue.Observe(tick, new(
            session, attributed ? actor.Account!.ID : 0, id, generation,
            snapshotComplete ? item!.type : 0, snapshotComplete ? item!.stack : 0,
            snapshotComplete ? item!.position.X : 0f, snapshotComplete ? item!.position.Y : 0f,
            float.NaN, float.NaN, snapshotComplete, item?.active == true,
            item?.beingGrabbed == true, NormalPickupShape: false,
            ParseComplete: true, ClientOrigin: true, BeforeSideEffects: true,
            AttributionComplete: attributed)
        {
            PacketId = packet.MessageId,
            AlreadyCancelled = alreadyCancelled,
            RequestCoordinatesComplete = false,
            ActorX = actorPosition.X,
            ActorY = actorPosition.Y,
            ActorPositionSnapshotComplete = actorPosition.Complete,
        });
        return M18GroundItemClearQueueRules.Observe(Input(session, true, false), decision);
    }

    private void ObserveGroundPlayerControls(M2Packet packet, SessionKey session,
        TSPlayer actor, bool alreadyCancelled)
    {
        if (packet.Payload.Length < 14 || packet.Payload[0] != session.Slot)
            return;

        bool attributed = IsAccountActor(session, actor);
        var actorPosition = GetActorPosition(session, actor);
        float x = M2PacketReader.Single(packet.Payload, 6);
        float y = M2PacketReader.Single(packet.Payload, 10);
        groundItemClearQueue.ObservePlayerControls(tick, new(
            session, attributed ? actor.Account!.ID : 0, x, y,
            float.IsFinite(x) && float.IsFinite(y),
            ParseComplete: true, ClientOrigin: true, BeforeSideEffects: true,
            AttributionComplete: attributed, AlreadyCancelled: alreadyCancelled)
        {
            ServerActorX = actorPosition.X,
            ServerActorY = actorPosition.Y,
            ServerActorPositionSnapshotComplete = actorPosition.Complete,
        });
    }

    private sealed class M18WorldItemGenerationTracker
    {
        private const int Capacity = 4096;
        private readonly WorldItem?[] wrappers = new WorldItem?[Capacity];
        private readonly Item?[] inners = new Item?[Capacity];
        private readonly int[] generations = new int[Capacity];
        private readonly bool[] observedAir = new bool[Capacity];
        private readonly bool[] unverified = new bool[Capacity];

        public int Observe(int slot, WorldItem? item)
        {
            if (slot is < 0 or >= Capacity) return 0;
            if (item is null || !item.active || item.type <= 0 || item.stack <= 0)
            {
                // An in-place resurrection after observed air is not a native
                // allocation proof. Keep the old wrapper so a later replacement
                // is still recognizable, but do not mint a generation here.
                if (wrappers[slot] is not null) observedAir[slot] = true;
                return 0;
            }

            if (!ReferenceEquals(wrappers[slot], item))
            {
                // In the locked OTAPI runtime, Item.NewItem allocates a new
                // WorldItem and assigns Main.item[slot]. A changed wrapper is
                // an allocation identity; mutable stack, position, prefix and
                // grab state within a wrapper are not.
                wrappers[slot] = item;
                inners[slot] = item.inner;
                observedAir[slot] = false;
                if (generations[slot] == int.MaxValue)
                    unverified[slot] = true; // Never wrap into an old target id.
                else
                {
                    generations[slot]++;
                    unverified[slot] = false;
                }
            }
            else if (observedAir[slot] || !ReferenceEquals(inners[slot], item.inner))
            {
                // A wrapper revived or had its inner Item replaced in place.
                // No verified allocation identity distinguishes the new life.
                unverified[slot] = true;
            }
            return unverified[slot] ? 0 : generations[slot];
        }

        public void Reset()
        {
            Array.Clear(wrappers);
            Array.Clear(inners);
            Array.Clear(generations);
            Array.Clear(observedAir);
            Array.Clear(unverified);
        }
    }

    private static BusinessRuleResult Safety(string id, string reason) => new(id, "m2.1", ControlAction.Block,
        Verdict.UnsafeInput, reason, false, false, ImmutableDictionary<string, string>.Empty);

    private bool CurrentBuffEntryMatches(int id)
    {
        if (buffField?.GetValue(null) is not Array current || !ReferenceEquals(current, buffSource) || current.Length != BuffID.Count) return false;
        if (id < 0 || id >= current.Length) return true; // Domain checks do not index a source row.
        if (!buffs!.ClientAddBuffTypes.TryGetValue(id, out var definition)) return current.GetValue(id) is null;
        var value = current.GetValue(id);
        return value is not null && buffMaximum?.GetValue(value) is int maximum && maximum == definition.MaximumTicks &&
            buffSelfOnly?.GetValue(value) is bool self && self == definition.SelfOnly &&
            buffWithoutPvp?.GetValue(value) is bool withoutPvp && withoutPvp == !definition.RequiresTargetPvp;
    }

    private void AddProgression(List<BusinessRuleResult> output, SessionKey session, TSPlayer actor,
        ProgressionSubjectKind kind, int id, ProgressionActionKind action, int? style, bool synchronization)
        => Run(Producer.Progression, () => AddProgressionCore(output, session, actor, kind, id, action, style, synchronization));

    private void AddProgressionCore(List<BusinessRuleResult> output, SessionKey session, TSPlayer actor,
        ProgressionSubjectKind kind, int id, ProgressionActionKind action, int? style, bool synchronization)
    {
        if (!progressionIndex.TryGetValue((kind, id), out var rules)) return;
        bool currentFacts = worldFactSnapshot is { } snapshot && snapshot == M17WorldFactSnapshot.Read();
        if (!currentFacts) worldStableTicks = 0; // An observed transition cannot settle merely by changing back.
        var now = DateTimeOffset.UtcNow;
        var observation = new ProgressionActionObservation(session, id, action)
        {
            SubjectKind = kind,
            Style = style,
            CurrentSession = session,
            RuntimeFingerprint = fingerprint,
            TerrariaVersion = "1.4.5.8",
            Authenticated = actor.IsLoggedIn && actor.Account is not null,
            ParseComplete = true,
            ClientOrigin = true,
            ActiveActionAttributed = !synchronization,
            BeforeSideEffects = true,
            IsSynchronization = synchronization,
            NowUtc = now,
            World = new(capturedWorldEpoch, fingerprint, worldRevision.ToString(), "world:" + capturedWorldId,
                worldCaptured, worldCaptured.AddSeconds(2), currentFacts,
                worldStableTicks >= 2 && capturedWorldEpoch == session.WorldEpoch && capturedWorldId == Main.worldID, worldFacts)
            // Acquisition, imported-asset and scoped grant evidence are not inferred from accepted inventory.
        };
        foreach (var rule in rules)
            output.Add(entityByRule.TryGetValue(rule.Id, out var entity) ? ProgressionBusinessRules.Evaluate(entity, observation)
                : ProgressionBusinessRules.Evaluate(rule, observation));
    }

    private void CaptureWorldFacts(long worldEpoch)
    {
        var snapshot = M17WorldFactSnapshot.Read();
        var next = snapshot.ToFacts();
        bool changed = worldFactSnapshot != snapshot;
        if (changed) { worldRevision++; worldStableTicks = 0; } else worldStableTicks = Math.Min(3, worldStableTicks + 1);
        worldFacts = next;
        worldFactSnapshot = snapshot;
        worldCaptured = DateTimeOffset.UtcNow;
        capturedWorldEpoch = worldEpoch;
        capturedWorldId = snapshot.WorldId;
    }
}
