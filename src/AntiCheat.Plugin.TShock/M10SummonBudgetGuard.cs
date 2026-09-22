using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Rules;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record M10SummonDecision(SessionKey Session, long Sequence, uint ProjectileKey,
    ControlAction Action, Verdict Verdict, string Reason, long Passes, long Blocks, long Unknowns);

/// <summary>Transparent resource guard for the audited ordinary Slime Staff replacement order.
/// No inventory writes, entity deletion, other-owner cleanup or account sanction. Unknown remains playable.
/// Native commits are bounded positive witnesses, never a complete projectile-origin ledger.</summary>
public sealed class M10SummonBudgetGuard : IDisposable
{
    public const int EntitiesPerSession = 64;
    public const int WitnessTtlTicks = 1800;
    private const int Slime = 266;
    private sealed record ItemWitness(Item Reference, int Type, int Stack, int Prefix, bool Accessory,
        bool Expert, bool Favorite, int Head, int Body, int Legs);
    private sealed record Capacity(SessionKey Session, Player Player, int Maximum, int UpperBound,
        int PendingBuffAllowance, int TurretMaximum, int TurretUpperBound, int PendingTurretBuffAllowance,
        int Loadout, bool Expert, bool Master, bool Anniversary, bool ExtraAccessory,
        ItemWitness[] Items, int[] Buffs);
    private sealed class EntityWitness(SessionKey session, Projectile entity, long tick)
    {
        public readonly SessionKey Session = session;
        public readonly Projectile Entity = entity;
        public readonly uint Key = entity.key.bits;
        public readonly int Type = entity.type;
        public readonly float Slots = entity.minionSlots;
        public long LastSeenTick = tick;
        // This sticky mark is uncertainty, not a successful native Kill. TTL can only discard cost.
        public bool RetirementUncertain;
    }
    private sealed class PlayerScope(M10SummonBudgetGuard owner, Player player, int index)
    {
        public readonly M10SummonBudgetGuard Owner = owner;
        public readonly Player Player = player;
        public readonly int Index = index;
        public int Next, Entered = -1;
        public bool NativeEntry, Invalid;
    }
    private sealed class RequestScope(M10SummonBudgetGuard owner, MessageBuffer buffer,
        int start, int length, byte[] payload)
    {
        public readonly M10SummonBudgetGuard Owner = owner;
        public readonly MessageBuffer Buffer = buffer;
        public readonly int Start = start;
        public readonly int Length = length;
        public readonly byte[] Payload = payload;
        public SessionKey? Session;
        public uint Key;
        public int Type;
        public bool Admitted;
        public bool AlreadyCancelled;
        public Projectile? Allocation;
        public Projectile? ExistingSentryCandidate;
    }
    private sealed record NativePreflight(M10SummonBudgetGuard Owner, SessionKey Session, uint Key,
        int Type, BusinessRuleResult Result);
    private sealed record EquipmentRequest(SessionKey Session, TSPlayer Actor, Player Player, int Slot,
        int Type, int Stack, int Prefix, bool Favorite, int Loadout);
    [ThreadStatic] private static PlayerScope? playerScope;
    [ThreadStatic] private static RequestScope? requestScope;
    [ThreadStatic] private static NativePreflight? nativePreflight;
    private delegate void GetDataOriginal(MessageBuffer buffer, int start, int length, out int messageType);
    private delegate void GetDataHook(GetDataOriginal original, MessageBuffer buffer, int start, int length, out int messageType);
    private readonly Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current;
    private readonly string fingerprint;
    private readonly List<IDisposable> hooks = [];
    private readonly Capacity?[] capacities = new Capacity?[256];
    private readonly int[] highWater = new int[256];
    private readonly int[] turretHighWater = new int[256];
    public M11SentryBudgetGuard SentryBudget { get; } = new();
    private readonly SessionKey?[] sessions = new SessionKey?[256];
    private readonly List<EntityWitness>?[] entities = new List<EntityWitness>?[256];
    private readonly M10SummonDecision?[] decisions = new M10SummonDecision?[256];
    private readonly HashSet<int>?[] equipmentGaps = new HashSet<int>?[256];
    private int thread, cursor;
    private long epoch = -1, tick;
    private bool failed, installed;
    private bool nativeReceiveHookInstalled;
    private readonly bool isolatedLab;
    public bool Healthy => installed && !failed;
    public string? FailureType { get; private set; }
    public string? FailureReason { get; private set; }
    public long NativeCalculations { get; private set; }
    public long NativeCommits { get; private set; }
    public long NativePreflightEvaluations { get; private set; }
    public long NativePreflightCancellations { get; private set; }
    public Action<Exception>? IntegrityFault { get; set; }

    public M10SummonBudgetGuard(string fingerprint,
        Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current,
        bool allowIsolatedScaffold = false)
    { this.fingerprint = fingerprint; this.current = current; isolatedLab = allowIsolatedScaffold; }

    public void Install()
    {
        if (installed || failed) return;
        try
        {
            if (fingerprint != TargetRuntime.Fingerprint || typeof(Player).Assembly.GetName().Version != new Version(1, 4, 5, 8))
                throw new NotSupportedException("Native summon budget requires the audited 1.4.5.8 target.");
            var slime = new Projectile(); slime.SetDefaults(Slime);
            var staff = new Item(); staff.SetDefaults(ItemID.SlimeStaff);
            if (!slime.minion || slime.sentry || slime.minionSlots != 1 || staff.shoot != Slime ||
                !ProjectileID.Sets.MinionSacrificable[Slime] || ItemID.Sets.StaffMinionSlotsRequired[staff.type] != 1)
                throw new InvalidOperationException("Slime Staff resource definition differs from the audited target.");
            SentryBudget.ValidateTarget();
            var update = typeof(Player).GetMethod(nameof(Player.Update), [typeof(int)])!;
            hooks.Add(new ILHook(update, InstrumentUpdate));
            hooks.Add(new Hook(update, (Action<Action<Player, int>, Player, int>)WithinPlayerUpdate));
            string[] methods = [nameof(Player.ResetEffects), nameof(Player.UpdateBuffs), nameof(Player.UpdateEquips), nameof(Player.UpdateArmorSets)];
            for (int index = 0; index < methods.Length; index++)
            {
                int stage = index;
                MethodInfo method = typeof(Player).GetMethod(methods[index], index == 0 ? Type.EmptyTypes : [typeof(int)])!;
                hooks.Add(new ILHook(method, il => InstrumentCalculation(il, stage)));
            }
            var receive = typeof(MessageBuffer).GetMethod(nameof(MessageBuffer.GetData))!;
            hooks.Add(new ILHook(receive, InstrumentAllocation));
            hooks.Add(new Hook(receive, (GetDataHook)WithinRequest));
            OTAPI.Hooks.MessageBuffer.GetData += OnNativeGetData;
            nativeReceiveHookInstalled = true;
            installed = true;
        }
        catch (Exception error) { Fail(error); }
    }

    public void Tick(long worldEpoch)
    {
        if (thread != 0 && thread != Environment.CurrentManagedThreadId)
        { Fail(new InvalidOperationException("Summon execution thread changed.")); return; }
        thread = Environment.CurrentManagedThreadId;
        if (epoch != worldEpoch) { ResetWorld(); epoch = worldEpoch; }
        tick++;
        // At most 64 retained references per update; no Main.projectile scan per packet or per tick.
        int slot = cursor++ % entities.Length;
        if (cursor == entities.Length) cursor = 0;
        var binding = current(slot);
        SentryBudget.ValidateSlot(slot, binding.Session);
        if (sessions[slot] is { } previous && binding.Session != previous) ClearSlot(slot);
        else if (entities[slot] is { } retained)
        {
            retained.RemoveAll(entry => !SameActive(entry) || tick - entry.LastSeenTick > WitnessTtlTicks);
            foreach (var entry in retained) entry.LastSeenTick = tick;
        }
    }

    public void ResetWorld()
    { Array.Clear(capacities); Array.Clear(highWater); Array.Clear(turretHighWater); Array.Clear(sessions); Array.Clear(entities); Array.Clear(decisions); Array.Clear(equipmentGaps); SentryBudget.ResetWorld(); }

    public void Forget(SessionKey session)
    { if ((uint)session.Slot < sessions.Length && sessions[session.Slot] == session) ClearSlot(session.Slot); }

    private void ClearSlot(int slot)
    { capacities[slot] = null; highWater[slot] = 0; turretHighWater[slot] = 0; sessions[slot] = null; entities[slot] = null; decisions[slot] = null; equipmentGaps[slot] = null; SentryBudget.ClearSlot(slot); }

    public M10SummonDecision? CaptureDecision(int slot) => (uint)slot < decisions.Length &&
        decisions[slot] is { } decision && current(slot).Session == decision.Session ? decision : null;

    public M11SentryState? CaptureSentry(int slot) => (uint)slot < sessions.Length ? SentryBudget.Capture(slot, current(slot).Session) : null;

    /// <summary>Returns only the small native summon context needed by the
    /// ordinary Butcher observer. The locked Slime Staff pair is the sole
    /// mapping in this slice: projectile 266 is maintained by BuffID.BabySlime
    /// (64). A buff without an observed body, or a body without that buff, is
    /// never promoted to a match and never exempts the player from another
    /// rule.</summary>
    public M18NpcSummonAuxiliaryContext? CaptureNpcStrikeAuxiliary(SessionKey session)
    {
        if (!OnThread(session) || (uint)session.Slot >= capacities.Length)
            return null;

        var binding = current(session.Slot);
        var capacity = capacities[session.Slot];
        if (binding.Session != session || binding.Player is not { } actor ||
            !binding.CanWrite || !actor.IsLoggedIn || actor.Account is null ||
            !actor.HasSentInventory || actor.IgnoreSSCPackets ||
            actor.TPlayer is not { } player || !player.active || player.dead ||
            !ReferenceEquals(Main.player[session.Slot], player) ||
            capacity?.Session != session || !SameInputs(capacity, player))
            return null;

        bool maintenanceBuff = HasActiveBuff(player, BuffID.BabySlime);
        int observed = 0;
        if (maintenanceBuff && entities[session.Slot] is { } list)
        {
            foreach (var entity in list)
                if (entity.Session == session && entity.Type == Slime && SameActive(entity))
                    observed = observed == int.MaxValue ? observed : observed + 1;
        }

        return new M18NpcSummonAuxiliaryContext(
            SnapshotComplete: true,
            MaintenanceBuffObserved: maintenanceBuff,
            MatchingObservedEntity: observed > 0,
            MatchingObservedEntityCount: observed);
    }

    private void Bind(SessionKey session)
    {
        if (sessions[session.Slot] == session) return;
        ClearSlot(session.Slot); sessions[session.Slot] = session;
    }

    /// <summary>Call at the authenticated pre-core hook even when a preceding hook cancelled the packet.</summary>
    public void ObserveIncoming(SessionKey session, GetDataEventArgs args)
    {
        if (!OnThread(session)) return;
        Bind(session);
        if ((byte)args.MsgID is 5 or 50 or 147) capacities[session.Slot] = null;
        if ((byte)args.MsgID == 29 && args.Length >= 4 && args.Index >= 0 &&
            args.Index <= args.Msg.readBuffer.Length - 4)
            ObserveRetirement(session, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(args.Msg.readBuffer.AsSpan(args.Index, 4)));
    }

    private void ObserveRetirement(SessionKey session, uint key)
    {
        if (entities[session.Slot] is not { } list) return;
        foreach (var entry in list)
            if (entry.Session == session && entry.Key == key) entry.RetirementUncertain = true;
    }

    public BusinessRuleResult? Evaluate(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled)
    {
        if (!OnThread(session)) return null;
        Bind(session);
        if (packet.Kind == M2PacketKind.ProjectileDestroy)
        {
            ObserveRetirement(session, (uint)M2PacketReader.Int32(packet.Payload, 0)); return null;
        }
        if (packet.Kind != M2PacketKind.ProjectileNew) return null;
        uint bits = (uint)M2PacketReader.Int32(packet.Payload, 0);
        var key = (ProjectileKey)bits;
        int type = M2PacketReader.Int16(packet.Payload, 20);
        if (nativePreflight is { } cached && cached.Owner == this && cached.Session == session &&
            cached.Key == bits && cached.Type == type)
        {
            nativePreflight = null;
            if (requestScope is { } cachedRequest && cachedRequest.Owner == this &&
                ReferenceEquals(cachedRequest.Buffer, requestScope.Buffer) &&
                cachedRequest.Buffer.whoAmI == session.Slot)
            {
                cachedRequest.Session = session; cachedRequest.Key = bits; cachedRequest.Type = type;
                cachedRequest.AlreadyCancelled = alreadyCancelled;
                cachedRequest.Admitted = !alreadyCancelled && cached.Result.Action != ControlAction.Block;
            }
            return cached.Result;
        }
        bool keyFound = M2ProjectileLookup.TryGet(key, out var old, out bool complete);
        bool found = keyFound && old is { active: true };
        // Native27 can SetDefaults on an existing key whose type changes, creating a sentry body
        // without NewProjectileSetup. Ordinary same-type308 updates add no body and remain outside it.
        bool addsSentry = type == M11SentryBudgetGuard.Hydra && (!found || old!.type != M11SentryBudgetGuard.Hydra);
        if (requestScope is { } request && request.Owner == this && request.Buffer.whoAmI == session.Slot)
        {
            request.Session = session; request.Key = bits; request.Type = type;
            request.Admitted = !alreadyCancelled && complete && (!found || addsSentry) && key.Spawner == session.Slot;
            request.ExistingSentryCandidate = addsSentry && keyFound ? old : null;
        }
        if (type is not (Slime or M11SentryBudgetGuard.Hydra)) return null;
        var binding = current(session.Slot);
        var capacity = capacities[session.Slot];
        bool bound = binding.Session == session && binding.CanWrite && ReferenceEquals(binding.Player, actor) &&
            actor.IsLoggedIn && actor.Account is not null && actor.HasSentInventory && !actor.IgnoreSSCPackets &&
            actor.TPlayer is { active: true, dead: false } && ReferenceEquals(Main.player[session.Slot], actor.TPlayer);
        bool equipmentSettled = equipmentGaps[session.Slot] is not { Count: > 0 };
        bool knownHost = ServerApi.Plugins.All(plugin => plugin.Plugin.GetType() == typeof(TShockAPI.TShock) ||
            plugin.Plugin.GetType() == typeof(AntiCheatPlugin) ||
            plugin.Plugin.GetType().FullName == "CompatibilityAudit.GameplayScaffold" && isolatedLab);
        bool stable = bound && equipmentSettled && knownHost && capacity?.Session == session && SameInputs(capacity, actor.TPlayer);
        if (type == M11SentryBudgetGuard.Hydra)
        {
            var sentryResult = SentryBudget.Evaluate(session, bits, capacity?.Session ?? default,
                fingerprint == TargetRuntime.Fingerprint && Healthy, capacity is not null,
                stable && capacity!.TurretMaximum == actor.TPlayer.maxTurrets,
                complete && addsSentry && key.Spawner == session.Slot, alreadyCancelled,
                capacity?.TurretMaximum ?? 0, capacity?.TurretUpperBound ?? 0,
                capacity?.PendingTurretBuffAllowance ?? 0, equipmentSettled, knownHost, bound);
            if (sentryResult.Action == ControlAction.Block && requestScope?.Owner == this) requestScope.Admitted = false;
            return sentryResult;
        }
        double known = 0;
        int count = 0, retirement = 0;
        if (entities[session.Slot] is { } list)
        {
            list.RemoveAll(entry => entry.Session != session || !SameActive(entry) || tick - entry.LastSeenTick > WitnessTtlTicks);
            foreach (var entry in list)
            {
                entry.LastSeenTick = tick;
                if (entry.RetirementUncertain) { retirement++; continue; }
                known += entry.Slots; count++;
            }
        }
        var result = M10SummonBudgetRules.Evaluate(new(session, capacity?.Session ?? default,
            fingerprint == TargetRuntime.Fingerprint && Healthy, capacity is not null, stable,
            complete && !found && key.Spawner == session.Slot, alreadyCancelled, known, 1,
            capacity?.UpperBound ?? 0, count, retirement));
        if (result.Action == ControlAction.Block && requestScope?.Owner == this) requestScope.Admitted = false;
        result = result with { Facts = result.Facts.Add("producer", nameof(M10SummonBudgetGuard))
            .Add("capacitySource", "native-Player.Update-ResetEffects-UpdateBuffs-UpdateEquips-UpdateArmorSets")
            .Add("capacityCurrent", capacity?.Maximum.ToString() ?? "unavailable")
            .Add("pendingNativeBuffAllowance", capacity?.PendingBuffAllowance.ToString() ?? "unavailable")
            .Add("equipmentInputAcceptanceSettled", equipmentSettled.ToString()).Add("nativeHostKnown", knownHost.ToString())
            .Add("nativeCommits", NativeCommits.ToString()).Add("nativeCalculations", NativeCalculations.ToString())
            .Add("activeWitnessLimit", EntitiesPerSession.ToString()).Add("witnessTtlTicks", WitnessTtlTicks.ToString()) };
        var previous = decisions[session.Slot];
        static long Increment(long value) => value < long.MaxValue ? value + 1 : value;
        decisions[session.Slot] = new(session, Increment(previous?.Sequence ?? 0), bits, result.Action, result.Verdict, result.Reason,
            result.Action == ControlAction.Pass ? Increment(previous?.Passes ?? 0) : previous?.Passes ?? 0,
            result.Action == ControlAction.Block ? Increment(previous?.Blocks ?? 0) : previous?.Blocks ?? 0,
            result.Action == ControlAction.Unknown ? Increment(previous?.Unknowns ?? 0) : previous?.Unknowns ?? 0);
        return result;
    }

    private bool OnThread(SessionKey session) => Healthy && Main.netMode == 2 && session.WorldEpoch == epoch &&
        (uint)session.Slot < sessions.Length && thread == Environment.CurrentManagedThreadId;

    private static bool SameActive(EntityWitness entry) => entry.Entity is { active: true, minion: true, sentry: false } entity &&
        entity.owner == entry.Session.Slot && entity.key.bits == entry.Key && entity.type == entry.Type &&
        float.IsFinite(entry.Slots) && entry.Slots > 0 && entity.minionSlots == entry.Slots &&
        M2ProjectileLookup.TryGet((ProjectileKey)entry.Key, out var currentEntity, out bool complete) && complete &&
        ReferenceEquals(currentEntity, entity);

    private static bool HasActiveBuff(Player player, int buffType)
    {
        for (int index = 0; index < player.buffType.Length; index++)
            if (player.buffType[index] == buffType && player.buffTime[index] > 0)
                return true;
        return false;
    }

    private void WithinRequest(GetDataOriginal original, MessageBuffer buffer, int start, int length, out int messageType)
    {
        EquipmentRequest? equipment = null;
        try { equipment = ObserveRequest(buffer, start, length); }
        catch (Exception error) { Fail(error); }
        var previous = requestScope;
        byte[]? requestPayload = null;
        bool validProjectile = start >= 0 && length > 1 && start <= buffer.readBuffer.Length - length &&
            buffer.readBuffer[start] == 27 &&
            M2PacketReader.TryReadProjectilePayload(buffer.readBuffer.AsSpan(start + 1, length - 1), out requestPayload);
        var scope = Healthy && thread == Environment.CurrentManagedThreadId && Main.netMode == 2 && validProjectile
            ? new RequestScope(this, buffer, start, length, requestPayload!) : null;
        requestScope = scope;
        try
        {
            // Native failures retain their original propagation. Only this observer's work is isolated.
            original(buffer, start, length, out messageType);
            try { CompleteEquipmentRequest(equipment); CompleteRequest(scope); }
            catch (Exception error) { Fail(error); }
        }
        finally
        {
            // A native failure after an actual allocation must not forget the still-live resource.
            // Observation errors remain isolated; the original native exception still propagates.
            try { CompleteSentryRequest(scope); }
            catch (Exception error) { Fail(error); }
            if (nativePreflight is { Owner: var owner } && owner == this) nativePreflight = null;
            requestScope = previous;
        }
    }

    private void OnNativeGetData(object? sender, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
    {
        if (!Healthy || Main.netMode != 2 || thread != Environment.CurrentManagedThreadId ||
            args.Instance is not { } instance || instance.readBuffer is not { } buffer) return;
        var request = requestScope;
        if (request is null || request.Owner != this || !ReferenceEquals(request.Buffer, instance)) return;
        if (args.Result == OTAPI.HookResult.Cancel)
        {
            request.AlreadyCancelled = true;
            request.Admitted = false;
            return;
        }
        if (request.Start < 0 || request.Length <= 1 || request.Start > buffer.Length - request.Length ||
            buffer[request.Start] != 27 ||
            !M2PacketReader.TryReadProjectilePayload(buffer.AsSpan(request.Start + 1, request.Length - 1), out var payload) ||
            !payload.AsSpan().SequenceEqual(request.Payload)) return;
        int slot = args.Instance.whoAmI;
        if ((uint)slot >= sessions.Length) return;
        var binding = current(slot);
        if (binding.Session is not { } session || binding.Player is not { } actor || !OnThread(session)) return;
        int type = M2PacketReader.Int16(payload, 20);
        if (type != M11SentryBudgetGuard.Hydra) return;
        uint bits = (uint)M2PacketReader.Int32(payload, 0);
        try
        {
            var result = Evaluate(new(M2PacketKind.ProjectileNew, payload),
                session, actor, alreadyCancelled: request.AlreadyCancelled);
            if (result is null) return;
            nativePreflight = new(this, session, bits, type, result);
            if (NativePreflightEvaluations < long.MaxValue) NativePreflightEvaluations++;
            if (result.Action == ControlAction.Block)
            {
                if (NativePreflightCancellations < long.MaxValue) NativePreflightCancellations++;
                args.Result = OTAPI.HookResult.Cancel;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private EquipmentRequest? ObserveRequest(MessageBuffer buffer, int start, int length)
    {
        EquipmentRequest? equipment = null;
        // This outer native request boundary runs before OTAPI's GetData event. A prior OTAPI
        // cancellation may prevent TSAPI from seeing29 altogether; preserve retirement uncertainty anyway.
        if (Healthy && thread == Environment.CurrentManagedThreadId && Main.netMode == 2 &&
            (uint)buffer.whoAmI < sessions.Length && start >= 0 && length > 0 && start <= buffer.readBuffer.Length - length)
        {
            var binding = current(buffer.whoAmI);
            if (binding.Session is { } incoming && OnThread(incoming))
            {
                Bind(incoming);
                int kind = buffer.readBuffer[start];
                if (kind is 5 or 50 or 147) capacities[incoming.Slot] = null;
                // Cancelled input can still have changed the client during a recoverable maintenance state.
                // Recording uncertainty is independent of permission to accept or commit that input.
                if (binding.Player is { } actor &&
                    ReferenceEquals(actor.TPlayer, Main.player[incoming.Slot]))
                {
                    if (kind == 5 && length == 10)
                    {
                        var bytes = buffer.readBuffer.AsSpan(start + 1, 9);
                        int itemSlot = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes[1..]);
                        if (bytes[0] == incoming.Slot && IsCapacityEquipmentSlot(itemSlot))
                        {
                            (equipmentGaps[incoming.Slot] ??= []).Add(itemSlot);
                            equipment = new(incoming, actor, actor.TPlayer, itemSlot,
                                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes[6..]),
                                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes[3..]), bytes[5], (bytes[8] & 1) != 0, -1);
                        }
                    }
                    else if (kind == 147 && length == 5 && buffer.readBuffer[start + 1] == incoming.Slot)
                    {
                        (equipmentGaps[incoming.Slot] ??= []).Add(-1);
                        equipment = new(incoming, actor, actor.TPlayer, -1, 0, 0, 0, false, buffer.readBuffer[start + 2]);
                    }
                    // Character-info includes accessory-unlock capabilities; this slice does not claim
                    // to parse/acknowledge its full variable-size transaction after authentication.
                    else if (kind == 4 && actor.IsLoggedIn && actor.Account is not null &&
                        actor.HasSentInventory && !actor.IgnoreSSCPackets)
                        (equipmentGaps[incoming.Slot] ??= []).Add(-2);
                }
                if (kind == 29 && length >= 5)
                    ObserveRetirement(incoming, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.readBuffer.AsSpan(start + 1, 4)));
            }
        }
        return equipment;
    }

    private void CompleteRequest(RequestScope? scope)
    {
        if (scope is { Admitted: true, Session: { } session, Allocation: { } entity } && OnThread(session))
        {
            var binding = current(session.Slot);
            if (binding.Session != session || !binding.CanWrite || entity.key.bits != scope.Key || entity.type != scope.Type ||
                entity.owner != session.Slot || !entity.active || !entity.minion || entity.sentry ||
                entity.type != Slime || entity.minionSlots != 1) return;
            // Only the exact ordinary Slime definition contributes to this slice's lower bound.
            // Fractional/twin/dragon/sentry/custom costs are not rounded into a uniform entity count.
            Bind(session);
            var list = entities[session.Slot] ??= [];
            list.RemoveAll(entry => !SameActive(entry) || tick - entry.LastSeenTick > WitnessTtlTicks);
            if (list.Count < EntitiesPerSession && !list.Any(entry => entry.Key == scope.Key))
            {
                list.Add(new(session, entity, tick)); if (NativeCommits < long.MaxValue) NativeCommits++;
            }
        }
    }

    private void CompleteSentryRequest(RequestScope? scope)
    {
        if (scope is not { Admitted: true, Session: { } session, Type: M11SentryBudgetGuard.Hydra } || !OnThread(session)) return;
        var entity = scope.Allocation ?? scope.ExistingSentryCandidate;
        if (entity is null) return;
        var binding = current(session.Slot);
        // Record an actual committed resource even if maintenance began after its native allocation.
        // This does not grant permission to write or turn a failed/cancelled request into success.
        if (binding.Session == session) SentryBudget.RecordCommit(session, entity, scope.Key);
    }

    private static bool IsCapacityEquipmentSlot(int slot) =>
        slot >= PlayerItemSlotID.Armor0 && slot < PlayerItemSlotID.Armor0 + 10 ||
        slot >= PlayerItemSlotID.Loadout1_Armor_0 && slot < PlayerItemSlotID.Loadout1_Armor_0 + 10 ||
        slot >= PlayerItemSlotID.Loadout2_Armor_0 && slot < PlayerItemSlotID.Loadout2_Armor_0 + 10 ||
        slot >= PlayerItemSlotID.Loadout3_Armor_0 && slot < PlayerItemSlotID.Loadout3_Armor_0 + 10;

    private void CompleteEquipmentRequest(EquipmentRequest? request)
    {
        if (request is null || !OnThread(request.Session)) return;
        var binding = current(request.Session.Slot);
        if (binding.Session != request.Session || !binding.CanWrite || !ReferenceEquals(binding.Player, request.Actor) ||
            !ReferenceEquals(request.Actor.TPlayer, request.Player)) return;
        bool matches;
        if (request.Slot == -1) matches = request.Loadout is >= 0 and < 3 && request.Player.CurrentLoadoutIndex == request.Loadout;
        else
        {
            var reference = new PlayerItemSlotID.SlotReference(request.Player, request.Slot);
            if (!reference.TryGetArraySlot(out var items, out int index) || (uint)index >= items.Length) return;
            var item = items[index];
            matches = item is not null && item.type == request.Type && item.stack == request.Stack &&
                item.prefix == request.Prefix && item.favorited == request.Favorite;
        }
        if (matches) equipmentGaps[request.Session.Slot]?.Remove(request.Slot);
        // A declined request stays Unknown through later server calculations; a tick is no client acknowledgment.
    }

    private void InstrumentAllocation(ILContext il)
    {
        var cursor = new ILCursor(il);
        int matches = 0;
        while (cursor.TryGotoNext(MoveType.After, instruction => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == typeof(Projectile).FullName && method.Name == nameof(Projectile.NewProjectileSetup)))
        {
            cursor.Emit(OpCodes.Dup);
            cursor.EmitDelegate<Action<Projectile>>(entity =>
            {
                if (requestScope is { Admitted: true } scope && scope.Owner == this) scope.Allocation = entity;
            });
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException("Native packet27 allocation boundary changed.");
    }

    private void WithinPlayerUpdate(Action<Player, int> original, Player player, int index)
    {
        var previous = playerScope;
        playerScope = Healthy && Main.netMode == 2 && thread == Environment.CurrentManagedThreadId &&
            (uint)index < sessions.Length && player.whoAmI == index ? new(this, player, index) : null;
        try { original(player, index); }
        catch
        {
            if (playerScope?.Owner == this && (uint)index < capacities.Length) capacities[index] = null;
            throw;
        }
        finally { playerScope = previous; }
    }

    private void InstrumentUpdate(ILContext il)
    {
        var cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Action<Player>>(player =>
        {
            if (playerScope is { } scope && scope.Owner == this && scope.Player == player) scope.NativeEntry = true;
        });
        int matches = 0;
        while (cursor.TryGotoNext(MoveType.After, instruction => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == typeof(Player).FullName && method.Name == nameof(Player.UpdateArmorSets)))
        {
            cursor.Emit(OpCodes.Ldarg_0); cursor.EmitDelegate<Action<Player>>(CaptureCalculation); matches++;
        }
        if (matches != 1) throw new InvalidOperationException("Native player capacity boundary changed.");
    }

    private void InstrumentCalculation(ILContext il, int stage)
    {
        var cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0); cursor.EmitDelegate<Action<Player>>(player => CalculationWitness(player, stage, false));
        int returns = 0;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.MoveAfterLabels(); cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<Player>>(player => CalculationWitness(player, stage, true));
            cursor.Index++; returns++;
        }
        if (returns == 0) throw new InvalidOperationException("Native capacity calculation has no return.");
    }

    private void CalculationWitness(Player player, int stage, bool returned)
    {
        if (playerScope is not { } scope || scope.Owner != this || scope.Player != player || !scope.NativeEntry || scope.Invalid) return;
        if (returned)
        {
            if (scope.Entered != stage || scope.Next != stage) { scope.Invalid = true; return; }
            scope.Entered = -1; scope.Next++;
        }
        else
        {
            if (scope.Next != stage || scope.Entered != -1) { scope.Invalid = true; return; }
            scope.Entered = stage;
        }
    }

    private void CaptureCalculation(Player player)
    {
        if (playerScope is not { NativeEntry: true, Invalid: false, Next: 4, Entered: -1 } scope ||
            scope.Owner != this || scope.Player != player || !Healthy) return;
        try
        {
            var binding = current(scope.Index);
            if (binding.Session is not { } session || !OnThread(session) || !binding.CanWrite ||
                binding.Player is not { IsLoggedIn: true, HasSentInventory: true, IgnoreSSCPackets: false, Account: not null } actor ||
                !ReferenceEquals(actor.TPlayer, player) || !ReferenceEquals(Main.player[scope.Index], player) ||
                !player.active || player.dead || player.maxMinions < 1 || player.CurrentLoadoutIndex is < 0 or >= 3 ||
                player.armor is not { Length: >= 20 } || player.Loadouts is not { Length: 3 }) return;
            Bind(session);
            // A decline never immediately lowers the ceiling while the client's retirements can be in flight.
            // It stays conservative for this session; no tick delay invents completion or lawful acquisition.
            // Native QuickBuff can add110 earlier in this Player.Update and emit27 before the next50.
            // The two native capacity buffs each have one possible pending positive contribution.
            // Server receipt/calculation is never used as an acknowledgment of client completion.
            bool ActiveBuff(int type) => Enumerable.Range(0, player.buffType.Length)
                .Any(index => player.buffType[index] == type && player.buffTime[index] > 0);
            int pendingBuffAllowance = (ActiveBuff(BuffID.Summoning) ? 0 : 1) + (ActiveBuff(BuffID.Bewitched) ? 0 : 1);
            long possible = (long)player.maxMinions + pendingBuffAllowance;
            if (possible > int.MaxValue) return;
            highWater[scope.Index] = Math.Max(highWater[scope.Index], (int)possible);
            // War Table is the sole positive native UpdateBuffs maxTurrets contribution (348).
            // It may already be active on the client before the next50 reaches this server.
            int pendingTurretBuffAllowance = ActiveBuff(348) ? 0 : 1;
            long possibleTurrets = (long)player.maxTurrets + pendingTurretBuffAllowance;
            int turretUpper = possibleTurrets is >= 1 and <= int.MaxValue
                ? Math.Max(turretHighWater[scope.Index], (int)possibleTurrets) : int.MaxValue;
            turretHighWater[scope.Index] = turretUpper;
            capacities[scope.Index] = new(session, player, player.maxMinions, highWater[scope.Index],
                pendingBuffAllowance, player.maxTurrets, turretUpper, pendingTurretBuffAllowance,
                player.CurrentLoadoutIndex, Main.expertMode, Main.masterMode, Main.tenthAnniversaryWorld, player.extraAccessory,
                Enumerable.Range(0, 10).Select(slot => Witness(player.GetEffectiveArmor(slot))).ToArray(), player.buffType.ToArray());
            if (NativeCalculations < long.MaxValue) NativeCalculations++;
        }
        catch (Exception error) { Fail(error); }
    }

    private static ItemWitness Witness(Item item) => new(item, item.type, item.stack, item.prefix,
        item.accessory, item.expertOnly, item.favorited, item.headSlot, item.bodySlot, item.legSlot);

    private static bool SameInputs(Capacity capacity, Player player) => ReferenceEquals(capacity.Player, player) &&
        capacity.Maximum == player.maxMinions && capacity.Loadout == player.CurrentLoadoutIndex &&
        capacity.Expert == Main.expertMode && capacity.Master == Main.masterMode && capacity.Anniversary == Main.tenthAnniversaryWorld &&
        capacity.ExtraAccessory == player.extraAccessory &&
        capacity.Buffs.AsSpan().SequenceEqual(player.buffType) && capacity.Items.Select((item, index) =>
            item == Witness(player.GetEffectiveArmor(index))).All(equal => equal);

    private void Fail(Exception error)
    {
        if (failed) return;
        failed = true; FailureType = error.GetType().FullName;
        FailureReason = error.Message.Length <= 256 ? error.Message : error.Message[..256];
        ResetWorld();
        // Reporting is optional and must not turn an isolated rule fault into a native gameplay failure.
        try { IntegrityFault?.Invoke(error); } catch { }
    }

    public void Dispose()
    {
        installed = false;
        if (nativeReceiveHookInstalled)
        {
            OTAPI.Hooks.MessageBuffer.GetData -= OnNativeGetData;
            nativeReceiveHookInstalled = false;
        }
        for (int index = hooks.Count - 1; index >= 0; index--) hooks[index].Dispose();
        hooks.Clear(); ResetWorld();
    }
}
