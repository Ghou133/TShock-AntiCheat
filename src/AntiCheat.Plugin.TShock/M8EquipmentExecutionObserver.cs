using AntiCheat.Core;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record M8EquipmentCalculation(SessionKey Session, long LoadoutRevision, int ActiveLoadout,
    string RuntimeFingerprint, M5EquipmentEffectSnapshot EffectiveInputs,
    bool ProjectionMatchesServerCommit, bool LoadoutSelectionAttributed, bool NativeCalculationReturned,
    int DefenseDelta, float MeleeDamageDelta, float RangedDamageDelta, float MagicDamageDelta,
    float MinionDamageDelta)
{
    // A native server calculation has no information about receipt of a server packet by the client.
    public bool ClientMultiSlotCompletionObserved => false;
    public bool ItemAcquisitionProven => false;
    public bool NativeCallChainReturned => true;
}

/// <summary>Observes the real UpdateEquips call/return following a bounded 147 transaction.
/// Neither an accepted slot nor a calculated benefit proves lawful acquisition or client edit completion.
/// M11 also scopes a narrow server effect guard to each actual native calculation: repeated type935
/// functional damage additions are skipped while every stored slot and other native step remains.
/// The component never certifies client edit completion, mutates items, or creates an account verdict.</summary>
public sealed partial class M8EquipmentExecutionObserver : IDisposable
{
    private readonly string fingerprint;
    private readonly Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current;
    private readonly M7LoadoutTransactionObserver transactions;
    private readonly M8EquipmentCalculation?[] samples = new M8EquipmentCalculation?[256];
    private sealed record ItemWitness(Item Reference, int Type, int Stack, int Prefix, int Defense, bool Accessory,
        bool ExpertOnly, bool Favorite, int Head, int Body, int Legs, int Wings, int Voice);
    private readonly ItemWitness[]?[] itemWitnesses = new ItemWitness[]?[256];
    private sealed class ExecutionScope(M8EquipmentExecutionObserver owner, Player player, int index)
    {
        public readonly M8EquipmentExecutionObserver Owner = owner;
        public readonly Player Player = player;
        public readonly int Index = index;
        public int Entries, Returns;
        public SessionKey? EffectSession;
        public bool AvengerBonusApplied;
        public int AvengerBonusesBlocked;
        public byte ClassEmblemMask;
        public int ClassEmblemBonusesBlocked;
        public byte ManaCapacityMask, ManaRegenerationMask;
        public int ManaCapacityBonusesBlocked, ManaRegenerationBonusesBlocked;
    }
    [ThreadStatic] private static ExecutionScope? scope;
    private Hook? hook;
    private ILHook? bodyHook;
    private int updateThread, invalidationPending, expiryCursor;
    private bool failed;
    public bool Healthy => hook is not null && bodyHook is not null && !failed;
    public string? FailureType { get; private set; }
    public long Observed { get; private set; }

    public M8EquipmentExecutionObserver(string fingerprint,
        Func<int, (SessionKey? Session, TSPlayer? Player, bool CanWrite)> current,
        M7LoadoutTransactionObserver transactions)
    { this.fingerprint = fingerprint; this.current = current; this.transactions = transactions; }

    public void Install()
    {
        if (hook is not null || failed) return;
        try
        {
            if (fingerprint != TargetRuntime.Fingerprint || typeof(Player).Assembly.GetName().Version != new Version(1, 4, 5, 8))
                throw new NotSupportedException("Equipment execution observation requires the audited target.");
            var method = typeof(Player).GetMethod(nameof(Player.UpdateEquips), [typeof(int)])
                ?? throw new MissingMethodException(nameof(Player.UpdateEquips));
            bodyHook = new(method, InstrumentBody);
            hook = new(method, (Action<Action<Player, int>, Player, int>)AroundUpdateEquips);
            InstallAvengerEffectGuard();
            InstallClassEmblemEffectGuard();
            InstallManaAccessoryEffectGuard();
        }
        catch (Exception error) { Fail(error); }
    }

    // Called by the existing verified world update dispatcher; no fixed tick wait grants completion.
    public void Update()
    {
        int thread = Environment.CurrentManagedThreadId;
        int prior = Interlocked.CompareExchange(ref updateThread, thread, 0);
        if (prior != 0 && prior != thread)
        { Fail(new InvalidOperationException("Equipment update execution thread changed.")); return; }
        if (Interlocked.Exchange(ref invalidationPending, 0) != 0) { Array.Clear(samples); Array.Clear(itemWitnesses); }
        for (int count = 0; count < 16; count++)
        {
            int slot = expiryCursor++ % samples.Length;
            if (expiryCursor == samples.Length) expiryCursor = 0;
            if (samples[slot] is { } sample && transactions.Capture(sample.Session)?.Revision != sample.LoadoutRevision)
            { samples[slot] = null; itemWitnesses[slot] = null; }
        }
    }
    public void ResetWorld() { Array.Clear(samples); Array.Clear(itemWitnesses); Array.Clear(effectReportStates); Volatile.Write(ref updateThread, 0); }
    public void Forget(SessionKey session, bool endSession = true)
    {
        InvalidateSample(session);
        if (endSession && (uint)session.Slot < effectReportStates.Length && effectReportStates[session.Slot]?.Session == session)
            effectReportStates[session.Slot] = null;
    }
    private void InvalidateSample(SessionKey session)
    { if ((uint)session.Slot < samples.Length && samples[session.Slot]?.Session == session) { samples[session.Slot] = null; itemWitnesses[session.Slot] = null; } }
    public void ObserveIncoming(SessionKey session, GetDataEventArgs args)
    {
        // Any item sync can change a shared effective slot. Invalidation is not a punishment.
        if ((byte)args.MsgID is 5 or 147) InvalidateSample(session);
    }

    public M8EquipmentCalculation? Capture(SessionKey session)
    {
        if (!Healthy || (uint)session.Slot >= samples.Length || Volatile.Read(ref invalidationPending) != 0 ||
            Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread)) return null;
        var result = samples[session.Slot]; var commit = transactions.Capture(session);
        var binding = current(session.Slot);
        if (result?.Session != session || commit?.Revision != result.LoadoutRevision || binding.Session != session ||
            binding.Player is not { } actor || !binding.CanWrite || !ReferenceEquals(Main.player[session.Slot], actor.TPlayer)) return null;
        // Bind the recorded projection, effective object references and selected native item fields.
        // This is not an all-item-field/custom-plugin certificate or an acquisition baseline.
        var now = M5EquipmentContexts.Capture(session, actor.TPlayer);
        return SameInputs(result.EffectiveInputs, now) && SameItems(itemWitnesses[session.Slot], actor.TPlayer) ? result : null;
    }

    private void AroundUpdateEquips(Action<Player, int> original, Player player, int index)
    {
        if (!Healthy || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread))
        {
            if (Healthy) Interlocked.Exchange(ref invalidationPending, 1);
            original(player, index); return;
        }
        M7LoadoutCompletion? commit = null; M5EquipmentEffectSnapshot? before = null; TSPlayer? actor = null;
        SessionKey? effectSession = null;
        ItemWitness[]? items = null;
        int defense = 0; float melee = 0, ranged = 0, magic = 0, minion = 0;
        try
        {
            if (Main.netMode == 2 && (uint)index < samples.Length && player.whoAmI == index && ReferenceEquals(Main.player[index], player))
            {
                var binding = current(index); actor = binding.Player;
                if (binding.Session is { } session && actor is not null && binding.CanWrite &&
                    ReferenceEquals(actor.TPlayer, player) && actor.IsLoggedIn && actor.Account is not null)
                {
                    effectSession = session;
                    commit = transactions.Capture(session);
                    if (commit is not null && samples[index]?.LoadoutRevision != commit.Revision)
                    {
                        before = M5EquipmentContexts.Capture(session, player);
                        items = CaptureItems(player);
                        defense = player.statDefense; melee = player.meleeDamage; ranged = player.rangedDamage;
                        magic = player.magicDamage; minion = player.minionDamage;
                    }
                }
            }
        }
        catch (Exception error) { Fail(error); }
        // The IL witness executes only inside the original method body. A preceding detour may
        // return without running that body; outer-call success must not manufacture a completion.
        var previous = scope;
        // The effect contract is scoped to every actual native calculation, independent of 147
        // observation, client completion or item acquisition. Scope never survives call/throw.
        var execution = effectSession is null ? null : new ExecutionScope(this, player, index) { EffectSession = effectSession };
        var effectObservation = effectSession is { } effectSubject ? BeginEffectObservation(effectSubject, player) : null;
        scope = execution;
        try { original(player, index); }
        finally { scope = previous; } // Native exceptions propagate unchanged; no return witness on throw.
        if (execution is not null && effectObservation is not null) CompleteEffectObservation(execution, effectObservation);
        if (before is null || commit is null || !Healthy) return;
        try
        {
            var binding = current(index);
            if (binding.Session != commit.Session || !binding.CanWrite || !ReferenceEquals(binding.Player, actor) ||
                !ReferenceEquals(Main.player[index], player) || transactions.Capture(commit.Session)?.Revision != commit.Revision) return;
            var after = M5EquipmentContexts.Capture(commit.Session, player);
            if (!SameInputs(before, after) || !SameItems(items, player)) { samples[index] = null; itemWitnesses[index] = null; return; }
            samples[index] = new(commit.Session, commit.Revision, before.ActiveLoadout, fingerprint, before,
                SameInputs(commit.EffectiveEquipment, before), commit.AttributionComplete, execution is { Entries: 1, Returns: 1 },
                player.statDefense - defense, player.meleeDamage - melee, player.rangedDamage - ranged,
                player.magicDamage - magic, player.minionDamage - minion);
            itemWitnesses[index] = items;
            if (Observed < long.MaxValue) Observed++;
        }
        catch (Exception error) { Fail(error); }
    }

    private static bool SameInputs(M5EquipmentEffectSnapshot left, M5EquipmentEffectSnapshot right) =>
        left.Complete && right.Complete && left.Session == right.Session && left.ActiveLoadout == right.ActiveLoadout &&
        left.Slots.SequenceEqual(right.Slots);
    private static ItemWitness[] CaptureItems(Player player) => Enumerable.Range(0, 10).Select(slot => Witness(player.GetEffectiveArmor(slot))).ToArray();
    private static ItemWitness Witness(Item item) => new(item, item.type, item.stack, item.prefix, item.defense,
        item.accessory, item.expertOnly, item.favorited, item.headSlot, item.bodySlot, item.legSlot, item.wingSlot, item.voiceSlot);
    private static bool SameItems(ItemWitness[]? witnesses, Player player) => witnesses is { Length: 10 } &&
        witnesses.Select((before, slot) =>
        {
            var item = player.GetEffectiveArmor(slot);
            return ReferenceEquals(before.Reference, item) && before == Witness(item);
        }).All(equal => equal);

    private void InstrumentBody(ILContext il)
    {
        // Audited void method; instrument every real return, including branches to a return.
        var cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1);
        cursor.EmitDelegate<Action<Player, int>>((player, index) => BodyWitness(player, index, false));
        int returns = 0;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.MoveAfterLabels();
            cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1);
            cursor.EmitDelegate<Action<Player, int>>((player, index) => BodyWitness(player, index, true));
            cursor.Index++; returns++;
        }
        if (returns == 0) throw new InvalidOperationException("Equipment method has no audited return boundary.");
    }
    private void BodyWitness(Player player, int index, bool returned)
    {
        if (scope is not { } active || active.Owner != this || active.Player != player || active.Index != index) return;
        if (returned) { if (active.Returns < int.MaxValue) active.Returns++; }
        else if (active.Entries < int.MaxValue) active.Entries++;
    }
    private void Fail(Exception error) { failed = true; FailureType = error.GetType().FullName; Array.Clear(samples); Array.Clear(itemWitnesses); }
    public void Dispose()
    {
        try { hook?.Dispose(); }
        finally { hook = null; bodyHook?.Dispose(); bodyHook = null; avengerEffectHook?.Dispose(); avengerEffectHook = null;
            classEmblemEffectHook?.Dispose(); classEmblemEffectHook = null;
            manaAccessoryEffectHook?.Dispose(); manaAccessoryEffectHook = null; ResetWorld(); }
    }
}
