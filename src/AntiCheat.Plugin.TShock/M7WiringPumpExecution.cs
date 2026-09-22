using AntiCheat.Core;
using Terraria;

namespace AntiCheat.Plugin.TShock;

/// <summary>Guards one native per-wire-color pump transfer before any liquid or framing write.
/// The wire traversal and unrelated circuit effects continue through their normal native cleanup.</summary>
public sealed partial class M6WiringExecutionGuard
{
    // Native pump collection stops at 19, with arrays allocated to 20. Accept the full array
    // capacity for trusted native callers; no graph traversal or unbounded endpoint collection.
    public const int MaximumPumpEndpoints = 20;
    private readonly BoundedRateLimiter pumpActors, pumpAreas, pumpGlobal;
    private bool pumpInstalled, pumpFailed;
    public Action<Exception>? PumpIntegrityFault { get; set; }
    public bool PumpContractHealthy => installed && !failed && pumpInstalled && !pumpFailed;
    public bool PumpFaultObserverFailed { get; private set; }
    public long PumpAllowed { get; private set; }
    public long PumpPermissionBlocked { get; private set; }
    public long PumpBudgetBlocked { get; private set; }
    public long PumpMalformedBlocked { get; private set; }
    public long PumpUnknownActor { get; private set; }
    public long PumpAdmittedWorkUnits { get; private set; }

    private void OnXferWater(object? sender, HookEvents.Terraria.Wiring.XferWaterEventArgs args)
    {
        if (!PumpContractHealthy || !args.ContinueExecution || Main.netMode != 2) return;
        try { GuardPumpTransfer(args); }
        catch (Exception error)
        {
            // A local pump observer fault does not remove the independent statue guard or
            // the shared exact-source observer. Do not turn unknown observer state into guilt.
            pumpFailed = true;
            HookEvents.Terraria.Wiring.XferWater -= OnXferWater;
            pumpInstalled = false;
            try { PumpIntegrityFault?.Invoke(error); }
            catch (Exception) { PumpFaultObserverFailed = true; }
        }
    }

    private void GuardPumpTransfer(HookEvents.Terraria.Wiring.XferWaterEventArgs args)
    {
        int inputs = Wiring._numInPump, outputs = Wiring._numOutPump;
        if (!ValidPumpArray(Wiring._inPumpX, Wiring._inPumpY, inputs) ||
            !ValidPumpArray(Wiring._outPumpX, Wiring._outPumpY, outputs))
        { args.ContinueExecution = false; PumpMalformedBlocked = Increment(PumpMalformedBlocked); return; }
        if (inputs == 0 || outputs == 0) return;

        var (session, player) = Wiring.CurrentUser is >= 0 and < 255 ? resolve(Wiring.CurrentUser) : (null, null);
        bool attributed = session is not null && player is not null && triggeringSession == session.Key &&
            triggeringThread == Environment.CurrentManagedThreadId && session.Key.Slot == Wiring.CurrentUser &&
            player.Index == Wiring.CurrentUser && player.IsLoggedIn && player.Account is not null &&
            session.AccountId == player.Account.ID;
        if (attributed && (session!.Revoked || !writesAvailable()))
        { args.ContinueExecution = false; PumpPermissionBlocked = Increment(PumpPermissionBlocked); return; }
        if (!attributed && Wiring.CurrentUser != 255) PumpUnknownActor = Increment(PumpUnknownActor);

        // Authorize every registered input/output before allowing the first native write. The
        // native SquareTileFrame call examines each endpoint's 3x3 neighborhood as well.
        var affectedAreas = new HashSet<(int X, int Y)>();
        bool endpointsAllowed = CheckEndpoints(Wiring._inPumpX, Wiring._inPumpY, inputs) &
            CheckEndpoints(Wiring._outPumpX, Wiring._outPumpY, outputs);
        if (!endpointsAllowed)
        { args.ContinueExecution = false; PumpPermissionBlocked = Increment(PumpPermissionBlocked); return; }

        // Upper bound on native endpoint-pair checks plus bounded preflight/framing cells.
        // Exhaustion skips this transfer in its entirety: no partial debit, rollback or refund.
        int pairs = inputs * outputs;
        int workUnits = pairs + 9 * (pairs + 2 * inputs) + 9 * (inputs + outputs);
        bool actorAllowed = !attributed || pumpActors.TryConsume(
            $"{session!.Key.ServerRunId:N}:{session.Key.WorldEpoch}:{session.Key.Slot}:{session.Key.Generation}", workUnits).Behavior == ControlAction.Pass;
        bool areaAllowed = true;
        foreach (var (x, y) in affectedAreas)
            areaAllowed &= pumpAreas.TryConsume($"{Main.worldID}:{x}:{y}", workUnits).Behavior == ControlAction.Pass;
        bool globalAllowed = pumpGlobal.TryConsume("pump-native-transfer", workUnits).Behavior == ControlAction.Pass;
        if (!actorAllowed || !areaAllowed || !globalAllowed)
        { args.ContinueExecution = false; PumpBudgetBlocked = Increment(PumpBudgetBlocked); return; }
        PumpAllowed = Increment(PumpAllowed);
        PumpAdmittedWorkUnits = PumpAdmittedWorkUnits > long.MaxValue - workUnits ? long.MaxValue : PumpAdmittedWorkUnits + workUnits;

        bool CheckEndpoints(int[] xs, int[] ys, int count)
        {
            bool allowed = true;
            for (int index = 0; index < count; index++)
                for (int x = Math.Max(0, xs[index] - 1); x <= Math.Min(Main.maxTilesX - 1, xs[index] + 1); x++)
                    for (int y = Math.Max(0, ys[index] - 1); y <= Math.Min(Main.maxTilesY - 1, ys[index] + 1); y++)
                    {
                        affectedAreas.Add((x / 64, y / 64));
                        if (attributed && !player!.HasBuildPermission(x, y, false)) allowed = false;
                    }
            return allowed;
        }
    }

    private static bool ValidPumpArray(int[]? xs, int[]? ys, int count)
    {
        if (count is < 0 or > MaximumPumpEndpoints || xs is null || ys is null || xs.Length < count || ys.Length < count)
            return false;
        for (int index = 0; index < count; index++)
            if ((uint)xs[index] >= Main.maxTilesX || (uint)ys[index] >= Main.maxTilesY || Main.tile[xs[index], ys[index]] is null)
                return false;
        return true;
    }
}
