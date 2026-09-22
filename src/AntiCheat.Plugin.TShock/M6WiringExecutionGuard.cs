using AntiCheat.Core;
using Terraria;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>Budgets native statue execution before cooldown writes, entity scans, creation or teleport.
/// This limits statue effects, not the wire graph traversal, and never supplies an account verdict.</summary>
public sealed partial class M6WiringExecutionGuard : IDisposable
{
    private readonly Func<int, (SessionSnapshot? Session, TSPlayer? Player)> resolve;
    private readonly Func<bool> writesAvailable;
    private readonly BoundedRateLimiter actors, areas, global;
    private SessionKey? triggeringSession;
    private int triggeringThread;
    private bool installed, failed;
    public Action<Exception>? IntegrityFault { get; set; }
    public bool ContractHealthy => installed && !failed;
    public bool FaultObserverFailed { get; private set; }
    public long Allowed { get; private set; }
    public long PermissionBlocked { get; private set; }
    public long BudgetBlocked { get; private set; }
    public long UnknownActor { get; private set; }

    public M6WiringExecutionGuard(TimeProvider clock, Func<int, (SessionSnapshot?, TSPlayer?)> resolve,
        RateLimitOptions? actorBudget = null, RateLimitOptions? areaBudget = null, RateLimitOptions? globalBudget = null,
        Func<bool>? writesAvailable = null, RateLimitOptions? pumpActorBudget = null,
        RateLimitOptions? pumpAreaBudget = null, RateLimitOptions? pumpGlobalBudget = null)
    {
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        this.writesAvailable = writesAvailable ?? (() => true);
        actors = new(clock, actorBudget ?? new(256, 1024, 256, TimeSpan.FromMinutes(2)));
        areas = new(clock, areaBudget ?? new(2048, 2048, 512, TimeSpan.FromMinutes(2)));
        global = new(clock, globalBudget ?? new(1, 8192, 2048, TimeSpan.FromMinutes(2)));
        pumpActors = new(clock, pumpActorBudget ?? new(256, 16384, 4096, TimeSpan.FromMinutes(2)));
        pumpAreas = new(clock, pumpAreaBudget ?? new(2048, 32768, 8192, TimeSpan.FromMinutes(2)));
        pumpGlobal = new(clock, pumpGlobalBudget ?? new(1, 131072, 32768, TimeSpan.FromMinutes(2)));
    }

    // The caller installs only after accepting the exact audited target runtime.
    public void Install()
    {
        if (installed || failed) return;
        HookEvents.Terraria.Wiring.SetCurrentUser += OnSetCurrentUser;
        HookEvents.Terraria.Wiring.HitWireSingle += OnHitWireSingle;
        if (!pumpFailed)
        {
            HookEvents.Terraria.Wiring.XferWater += OnXferWater;
            pumpInstalled = true;
        }
        installed = true;
    }

    private void OnSetCurrentUser(object? sender, HookEvents.Terraria.Wiring.SetCurrentUserEventArgs args)
    {
        if (!installed || failed) return;
        try { ObserveCurrentUser(args); }
        catch (Exception error) { Fail(error); }
    }

    private void ObserveCurrentUser(HookEvents.Terraria.Wiring.SetCurrentUserEventArgs args)
    {
        triggeringSession = null; triggeringThread = 0;
        if (!args.ContinueExecution || Main.netMode != 2 || args.plr is < 0 or >= 255) return;
        // Snapshot at the actual native source assignment, then compare again at execution. A reused slot
        // must not inherit the old source permission or per-session budget, even before the next timer reset.
        var (session, player) = resolve(args.plr);
        if (session is not null && player is not null && session.Key.Slot == args.plr &&
            player.Index == args.plr && player.IsLoggedIn && player.Account is not null && session.AccountId == player.Account.ID)
        { triggeringSession = session.Key; triggeringThread = Environment.CurrentManagedThreadId; }
    }

    private void OnHitWireSingle(object? sender, HookEvents.Terraria.Wiring.HitWireSingleEventArgs args)
    {
        if (!installed || failed) return;
        try { GuardStatue(args); }
        catch (Exception error) { Fail(error); }
    }

    private void GuardStatue(HookEvents.Terraria.Wiring.HitWireSingleEventArgs args)
    {
        if (!args.ContinueExecution || Main.netMode != 2 || (uint)args.i >= Main.maxTilesX || (uint)args.j >= Main.maxTilesY)
            return;
        var tile = Main.tile[args.i, args.j];
        if (tile is null || !tile.active() || tile.type != 105) return;
        // Same 2x3 origin used by the audited native statue branch. No world scan or guessed delay.
        int x = args.i - tile.frameX % 36 / 18, y = args.j - tile.frameY % 54 / 18;
        if (x < 0 || y < 0 || x + 1 >= Main.maxTilesX || y + 2 >= Main.maxTilesY) return;
        var (session, player) = Wiring.CurrentUser is >= 0 and < 255 ? resolve(Wiring.CurrentUser) : (null, null);
        bool attributed = session is not null && player is not null && triggeringSession == session.Key &&
            triggeringThread == Environment.CurrentManagedThreadId && session.Key.Slot == Wiring.CurrentUser &&
            player.Index == Wiring.CurrentUser && player.IsLoggedIn && player.Account is not null &&
            session.AccountId == player.Account.ID;
        // Tick-driven wiring has CurrentUser=255. Missing attribution is not a nearest-player accusation.
        if (attributed)
        {
            if (session!.Revoked || !writesAvailable())
            { args.ContinueExecution = false; PermissionBlocked = Increment(PermissionBlocked); return; }
            for (int dx = 0; dx < 2; dx++)
                for (int dy = 0; dy < 3; dy++)
                    if (!player!.HasBuildPermission(x + dx, y + dy, false))
                    { args.ContinueExecution = false; PermissionBlocked = Increment(PermissionBlocked); return; }
        }
        else if (Wiring.CurrentUser != 255) UnknownActor = Increment(UnknownActor);

        // Every applicable budget is charged even if another rejects. Session identity includes generations.
        bool actorAllowed = !attributed || actors.TryConsume($"{session!.Key.ServerRunId:N}:{session.Key.WorldEpoch}:{session.Key.Slot}:{session.Key.Generation}").Behavior == ControlAction.Pass;
        bool areaAllowed = areas.TryConsume($"{Main.worldID}:{x / 64}:{y / 64}").Behavior == ControlAction.Pass;
        bool globallyAllowed = global.TryConsume("statue-native-effects").Behavior == ControlAction.Pass;
        if (!actorAllowed || !areaAllowed || !globallyAllowed)
        { args.ContinueExecution = false; BudgetBlocked = Increment(BudgetBlocked); return; }
        Allowed = Increment(Allowed);
    }

    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
    private void Fail(Exception error)
    {
        if (failed) return;
        failed = true;
        Dispose(); // This object's source and execution subscriptions only. Never restores cancelled actions.
        try { IntegrityFault?.Invoke(error); }
        catch (Exception) { FaultObserverFailed = true; } // A failed reporter cannot manufacture a second core packet exception.
    }
    public void Dispose()
    {
        if (installed)
        {
            HookEvents.Terraria.Wiring.SetCurrentUser -= OnSetCurrentUser;
            HookEvents.Terraria.Wiring.HitWireSingle -= OnHitWireSingle;
        }
        if (pumpInstalled) HookEvents.Terraria.Wiring.XferWater -= OnXferWater;
        pumpInstalled = false;
        triggeringSession = null; triggeringThread = 0;
        installed = false;
    }
}
