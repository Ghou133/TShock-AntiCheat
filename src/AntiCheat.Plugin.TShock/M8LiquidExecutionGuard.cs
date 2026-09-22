using System.Reflection;
using AntiCheat.Core;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Terraria;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Settles the previous ordinary native liquid scheduler step before admitting another. There is no
/// player attribution or account verdict: pumps, rain and old world liquid share this scheduler.
/// A refused step stays in Terraria's existing bounded arrays, with its cursor and flags intact.
/// </summary>
public sealed class M8LiquidExecutionGuard : IDisposable
{
    // Audited 1.4.5.8: at most maxLiquid Update calls, two maxLiquid removal loops and
    // one maxLiquid buffer-drain loop. These are scheduler work units, not a CPU-time bound
    // on all transitive framing, interactions, plugins or network serialization.
    public const int MaximumSchedulerWorkUnits = 100001;
    // A deployment policy measured in actual native scheduler visits, NOT CPU milliseconds.
    // Ordinary sustained full queues can exceed this quota and are paused between whole steps.
    public static RateLimitOptions DefaultBudget => new(1, MaximumSchedulerWorkUnits,
        100_000, TimeSpan.FromMinutes(2));

    private readonly BoundedRateLimiter budget;
    private readonly Func<bool> writesAvailable;
    private readonly double burst;
    private Hook? hook;
    private ILHook? bodyHook;
    private int executionThread;
    private bool failed;
    private int unpaidVisits;
    private sealed class NativeStep(M8LiquidExecutionGuard owner)
    {
        public readonly M8LiquidExecutionGuard Owner = owner;
        public int Visits;
    }
    [ThreadStatic] private static NativeStep? activeStep;

    public M8LiquidExecutionGuard(TimeProvider clock, Func<bool>? writesAvailable = null,
        RateLimitOptions? schedulerBudget = null)
    {
        var options = schedulerBudget ?? DefaultBudget;
        if (options.MaxKeys != 1) throw new ArgumentOutOfRangeException(nameof(schedulerBudget));
        budget = new(clock, options);
        burst = options.Burst;
        this.writesAvailable = writesAvailable ?? (() => true);
    }

    public Action<Exception>? IntegrityFault { get; set; }
    public bool ContractHealthy => hook is not null && bodyHook is not null && !failed && executionThread != 0;
    public bool FaultObserverFailed { get; private set; }
    public long Allowed { get; private set; }
    public long BudgetPaused { get; private set; }
    public long MaintenancePaused { get; private set; }
    public long RecoveryPassed { get; private set; }
    public long ContextUnavailablePassed { get; private set; }
    public long AdmittedSchedulerWorkUnits { get; private set; }
    public long ObservedSchedulerWorkUnits { get; private set; }
    public int LastPaidSchedulerWorkUnits { get; private set; }
    public int LastObservedSchedulerWorkUnits { get; private set; }
    public int UnpaidSchedulerWorkUnits => unpaidVisits;

    // Only the verified plugin lifecycle installs this hook. Liquid.UpdateLiquid is not one
    // of this target's OTAPI wrapper methods, so wrap its complete existing detour chain.
    public void Install()
    {
        if (hook is not null || failed) return;
        if (typeof(Liquid).Assembly.GetName().Version != new Version(1, 4, 5, 8) ||
            Liquid.maxLiquid != 25000 || Liquid.maxLiquidBuffer != 50000)
            throw new NotSupportedException("Liquid scheduler requires the audited 1.4.5.8 capacities.");
        var method = typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid), BindingFlags.Public | BindingFlags.Static, [])
            ?? throw new MissingMethodException("Audited liquid scheduler is absent.");
        try
        {
            bodyHook = new(method, InstrumentScheduler);
            hook = new(method, (Action<Action>)AroundUpdateLiquid);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Called from the real GameUpdate hook, never a client packet or elapsed timer.</summary>
    public void BindExecutionThread()
    {
        if (hook is null || failed) return;
        int current = Environment.CurrentManagedThreadId;
        int previous = Interlocked.CompareExchange(ref executionThread, current, 0);
        if (previous != 0 && previous != current)
            Fail(new InvalidOperationException("Native liquid execution thread changed."));
    }

    private void AroundUpdateLiquid(Action original)
    {
        bool run = true, meter = false;
        // Do not catch, retry or turn an exception from native code into a successful step.
        try { run = Admit(out meter); }
        catch (Exception error) { Fail(error); }
        if (!run) return;
        if (!meter || failed) { original(); return; }
        if (activeStep is not null)
        {
            Fail(new InvalidOperationException("Native liquid scheduler was re-entered."));
            original(); return;
        }
        var step = new NativeStep(this);
        activeStep = step;
        try { original(); }
        finally
        {
            activeStep = null;
            // Only this already completed (or throwing) invocation's visits are carried forward.
            // No liquid operation is replayed, and no visit can interrupt an in-progress step.
            unpaidVisits = step.Visits;
            LastObservedSchedulerWorkUnits = 1 + step.Visits;
            ObservedSchedulerWorkUnits = Add(ObservedSchedulerWorkUnits, LastObservedSchedulerWorkUnits);
        }
    }

    private bool Admit(out bool meter)
    {
        meter = false;
        if (failed || Main.netMode != 2 || WorldGen.isGeneratingOrLoadingWorld) return true;
        if (executionThread == 0)
        { ContextUnavailablePassed = Increment(ContextUnavailablePassed); return true; }
        if (executionThread != Environment.CurrentManagedThreadId)
        {
            Fail(new InvalidOperationException("Liquid scheduler was called outside the verified update thread."));
            return true;
        }
        // A maintenance pause is also at the complete native boundary, including panic recovery.
        // It does not clear checkingLiquid, change world solidity, advance panicY or send a batch.
        if (!writesAvailable())
        { MaintenancePaused = Increment(MaintenancePaused); return false; }

        ValidateNativeQueue();
        // Native panic walks world rows through QuickWater/SettleWaterAt and may invalidate all
        // sections. It is a separate unsupported cost domain. Never starve Terraria's recovery
        // by repeatedly refusing a batch that cannot fit the ordinary scheduler's contract.
        if (Liquid.panicMode || (LiquidBuffer.numLiquidBuffer >= Liquid.maxLiquidBuffer * 0.9 &&
            Liquid.panicCounter >= 3600))
        { RecoveryPassed = Increment(RecoveryPassed); return true; }

        int cost = 1 + unpaidVisits;
        LastPaidSchedulerWorkUnits = cost;
        // A bad operator budget must not leave a legal native step permanently queued. Withdraw
        // only this local guard and report the capacity fault; there is no clipping or hidden debt.
        if (cost > burst)
            throw new InvalidOperationException("Liquid scheduler burst cannot admit this complete native step.");
        if (budget.TryConsume("ordinary-liquid-scheduler", cost).Behavior != ControlAction.Pass)
        { BudgetPaused = Increment(BudgetPaused); return false; }
        unpaidVisits = 0;
        Allowed = Increment(Allowed);
        AdmittedSchedulerWorkUnits = Add(AdmittedSchedulerWorkUnits, cost);
        meter = true;
        return true;
    }

    private static void ValidateNativeQueue()
    {
        if (Liquid.numLiquid < 0 || Liquid.numLiquid > Liquid.maxLiquid ||
            LiquidBuffer.numLiquidBuffer < 0 || LiquidBuffer.numLiquidBuffer > Liquid.maxLiquidBuffer ||
            Main.liquid is null || Main.liquid.Length < Liquid.maxLiquid ||
            Main.liquidBuffer is null || Main.liquidBuffer.Length < Liquid.maxLiquidBuffer ||
            Main.player is null || Main.player.Length < 15 || Liquid.wetCounter < 0)
            throw new InvalidOperationException("Liquid scheduler state is outside its audited capacity contract.");
    }

    private static int NativeUpdateVisitCount()
    {
        int active = 0;
        // This unusual first-15-player range is exactly what the locked native method uses.
        for (int index = 0; index < 15; index++) if (Main.player[index].active) active++;
        int cycles = 10 + active / 3;
        int maximum = Main.Setting_UseReducedMaxLiquids ? 5000 : Liquid.maxLiquid - active * 250;
        int chunk = maximum / cycles;
        long counter = (long)Liquid.wetCounter + 1;
        long first = (long)chunk * Liquid.wetCounter;
        long end = counter == cycles ? Liquid.numLiquid : (long)chunk * counter;
        if (end > Liquid.numLiquid) { end = Liquid.numLiquid; counter = cycles; }
        int updates = (int)Math.Clamp(end - first, 0, Liquid.maxLiquid);
        return updates;
    }

    private void InstrumentScheduler(ILContext il)
    {
        // The entry span counts BOTH Update calls and native skipLiquid visits. Cleanup is
        // counted where its actual starting length is known, after Update may have enqueued.
        var cursor = new ILCursor(il);
        cursor.EmitDelegate<Action>(() => ObserveVisits(NativeUpdateVisitCount));
        int cursors = 0, drains = 0, stuckWrites = 0;
        while (cursor.TryGotoNext(MoveType.After, instruction =>
            instruction.OpCode == OpCodes.Stsfld && instruction.Operand is FieldReference field &&
            field.DeclaringType.FullName == typeof(Liquid).FullName && field.Name == nameof(Liquid.wetCounter)))
        {
            cursor.EmitDelegate<Action>(() => ObserveVisits(() => Liquid.wetCounter == 0 ? Liquid.numLiquid : 0));
            cursors++;
        }
        cursor.Index = 0;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == typeof(LiquidBuffer).FullName && method.Name == nameof(LiquidBuffer.DelBuffer)))
        {
            cursor.MoveAfterLabels();
            cursor.EmitDelegate<Action>(() => ObserveVisits(() => 1));
            cursor.Index++; drains++;
        }
        cursor.Index = 0;
        while (cursor.TryGotoNext(MoveType.After, instruction =>
            instruction.OpCode == OpCodes.Stsfld && instruction.Operand is FieldReference field &&
            field.DeclaringType.FullName == typeof(Liquid).FullName && field.Name == nameof(Liquid.stuck)))
        {
            cursor.EmitDelegate<Action>(() => ObserveVisits(() => Liquid.stuck ? Liquid.numLiquid : 0));
            stuckWrites++;
        }
        if (cursors != 3 || drains != 1 || stuckWrites != 2)
            throw new NotSupportedException("Liquid scheduler loop markers differ from the audited target.");
    }

    private void ObserveVisits(Func<int> count)
    {
        if (activeStep is not { } step || step.Owner != this || failed) return;
        try
        {
            int visits = count();
            if (visits < 0 || visits > MaximumSchedulerWorkUnits - 1 - step.Visits)
            { Fail(new InvalidOperationException("Liquid scheduler visit count exceeded the audited step bound.")); return; }
            step.Visits += visits;
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        if (failed) return;
        failed = true;
        try { IntegrityFault?.Invoke(error); }
        catch (Exception) { FaultObserverFailed = true; }
    }

    private static long Increment(long value) => Add(value, 1);
    private static long Add(long value, int amount) => value > long.MaxValue - amount ? long.MaxValue : value + amount;

    public void Dispose()
    {
        try { hook?.Dispose(); }
        finally { hook = null; bodyHook?.Dispose(); bodyHook = null; executionThread = 0; unpaidVisits = 0; }
    }
}
