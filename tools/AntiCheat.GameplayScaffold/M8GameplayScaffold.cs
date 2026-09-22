using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.Net;
using Terraria.GameContent.NetModules;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private M7PumpFixture? m8Flow;

    private void PrepareM8LiquidPropagation()
    {
        Require(room is not null && m7Pumps.Length == 2 && m8Flow is null, "Prepare the ordinary M7 pump actor first; one flow fixture per owned run.");
        var actor = TShock.Players[m7PumpActor];
        Require(actor is not null && actor.IsLoggedIn && !actor.HasPermission("anticheat.bypass"), "Original ordinary actor required.");
        var f = new M7PumpFixture("propagation", room!.Left + 19, room.Left + 25, room.Top + 3, room.Left + 17, room.Top + 4);
        var cells = Enumerable.Range(f.SwitchX, 11).SelectMany(x => Enumerable.Range(f.Y - 1, 8).Select(y => (X: x, Y: y))).ToArray();
        Require(cells.All(p => Main.tile[p.X, p.Y] is { } t && !t.active() && t.liquid == 0 && !t.wire() &&
            !t.wire2() && !t.wire3() && !t.wire4() && !t.actuator() && actor!.HasBuildPermission(p.X, p.Y, false)),
            "Flow cells must be owned, empty, unwired and outside all protected regions.");
        foreach (var (origin, type) in new[] { (f.InX, TileID.InletPump), (f.OutX, TileID.OutletPump) })
            for (int x = origin - 1; x <= origin + 2; x++) for (int y = f.Y - 1; y <= f.Y + 2; y++)
            {
                var t = Main.tile[x, y]; bool pump = x >= origin && x <= origin + 1 && y >= f.Y && y <= f.Y + 1;
                t.active(true); t.type = (ushort)(pump ? type : TileID.Glass);
                t.frameX = pump ? (short)((x - origin) * 18) : (short)0;
                t.frameY = pump ? (short)((y - f.Y) * 18) : (short)0;
            }
        // Open outlet floor into a two-cell-wide sealed channel. XferWater's actual
        // SquareTileFrame automatically queues this water; the fixture never calls AddWater.
        for (int y = f.Y + 2; y <= f.Y + 6; y++) for (int x = f.OutX - 1; x <= f.OutX + 2; x++)
        {
            var t = Main.tile[x, y]; t.ClearEverything();
            if (x == f.OutX - 1 || x == f.OutX + 2 || y == f.Y + 6)
            { t.active(true); t.type = TileID.Glass; }
        }
        Main.tile[f.InX, f.Y + 1].liquid = 200; Main.tile[f.InX, f.Y + 1].liquidType(0);
        var trigger = Main.tile[f.SwitchX, f.SwitchY]; trigger.active(true); trigger.type = TileID.PressurePlates;
        for (int x = f.SwitchX; x <= f.OutX; x++) Main.tile[x, f.SwitchY].wire(true);
        m8Flow = f; m7Pumps = [..m7Pumps, f];
        Record("m8-flow-prepared", new { fixtureArtificial = true, f, singleWaterSeed = 200,
            note = "One-time preparation. TCP59 invokes the real pump. GameUpdate alone advances natural liquid; no update calls, tick resets, holds or refill." });
        TSPlayer.All.SendTileRect((short)f.SwitchX, (short)(f.Y - 1), 11, 8);
        WriteM8LiquidPropagationState();
    }

    private void WriteM8LiquidPropagationState()
    {
        Require(m8Flow is not null, "Prepare M8 flow first."); var f = m8Flow!;
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_liquidExecution", PrivateM5)!.GetValue(plugin);
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var binding = ((Array)plugin.GetType().GetField("_bindings", PrivateM5)!.GetValue(plugin)!).GetValue(m7PumpActor);
        var session = binding?.GetType().GetProperty("Key")?.GetValue(binding);
        var movement = plugin.GetType().GetField("_movementObservations", PrivateM5)!.GetValue(plugin);
        var movementSnapshot = session is null ? null : movement?.GetType().GetMethod("Capture")?.Invoke(movement, [session]);
        var cells = Enumerable.Range(f.OutX, 2).SelectMany(x => Enumerable.Range(f.Y, 6)
            .Select(y => new { x, y, liquid = (int)Main.tile[x, y].liquid, type = Main.tile[x, y].liquidType() })).ToArray();
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, f.SwitchX, f.SwitchY, f.OutX, f.Y,
            moduleId = NetManager.Instance.GetId<NetLiquidModule>(), inputLiquid = PumpLiquid(f.InX, f.Y),
            outputLiquid = PumpLiquid(f.OutX, f.Y), channelLiquid = cells.Where(c => c.y >= f.Y + 2).Sum(c => c.liquid),
            totalLiquid = PumpLiquid(f.InX, f.Y) + cells.Sum(c => c.liquid), cells,
            guardPresent = guard is not null, healthy = Value("ContractHealthy"), allowed = Value("Allowed"),
            observedWork = Value("ObservedSchedulerWorkUnits"), budgetPaused = Value("BudgetPaused"),
            maintenancePaused = Value("MaintenancePaused"), recoveryPassed = Value("RecoveryPassed"),
            Liquid.numLiquid, LiquidBuffer.numLiquidBuffer, Liquid.wetCounter, movementSnapshot };
        File.WriteAllText(Path.Combine(output!, "m8-flow-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
}
