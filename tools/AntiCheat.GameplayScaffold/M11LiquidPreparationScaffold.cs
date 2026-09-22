using System.Diagnostics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private ILHook? m11PreparationBody, m11PreparationUpdates;
    private long m11PreparationBodyEntries, m11PreparationBodyReturns, m11PreparationUpdateCalls;
    private long m11PreparationStarted;
    private readonly object?[] m11PreparationRecentUpdates = new object?[128];
    private delegate void M11ObserveLiquidUpdate(ref Liquid entry);

    private void EnsureM11LiquidPreparationTrace()
    {
        if (m10LiquidAttempted || m11PreparationBody is not null) return;
        if (m11PreparationStarted == 0) m11PreparationStarted = Stopwatch.GetTimestamp();
        try
        {
            m11PreparationBody = new(typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid), [])!, il =>
            {
                var cursor = new ILCursor(il);
                cursor.EmitDelegate<Action>(() => m11PreparationBodyEntries++);
                int returns = 0;
                while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret))
                {
                    cursor.MoveAfterLabels(); cursor.EmitDelegate<Action>(() => m11PreparationBodyReturns++);
                    cursor.Index++; returns++;
                }
                if (returns != 2) throw new NotSupportedException("Native preparation liquid body return sites changed.");
            });
            m11PreparationUpdates = new(typeof(Liquid).GetMethod(nameof(Liquid.Update), [])!, il =>
            {
                var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
                cursor.EmitDelegate<M11ObserveLiquidUpdate>((ref Liquid entry) =>
                {
                    long sequence = ++m11PreparationUpdateCalls;
                    m11PreparationRecentUpdates[(sequence - 1) % m11PreparationRecentUpdates.Length] = new
                    {
                        sequence, body = m11PreparationBodyEntries, entry.x, entry.y, entry.kill, entry.delay,
                        Liquid.skipCount, Main.time
                    };
                });
            });
        }
        catch { DisposeM11LiquidPreparationTrace(); throw; }
    }

    private object M11LiquidPreparationTrace() => new
    {
        bodyEntries = m11PreparationBodyEntries, bodyReturns = m11PreparationBodyReturns,
        actualCellUpdateCalls = m11PreparationUpdateCalls, capacity = m11PreparationRecentUpdates.Length,
        overwritten = Math.Max(0, m11PreparationUpdateCalls - m11PreparationRecentUpdates.Length),
        recentActualCellUpdates = Enumerable.Range(0, (int)Math.Min(m11PreparationUpdateCalls, m11PreparationRecentUpdates.Length))
            .Select(i => m11PreparationRecentUpdates[(Math.Max(0, m11PreparationUpdateCalls - m11PreparationRecentUpdates.Length) + i) % m11PreparationRecentUpdates.Length]).ToArray(),
        counterStart = "first preparation sample; earlier load/settle calls not claimed",
        observationsOnly = true
    };

    private bool M11LiquidPreparationReady() => M10LiquidQuiescent() && m11PreparationStarted != 0 &&
        Stopwatch.GetElapsedTime(m11PreparationStarted) < TimeSpan.FromSeconds(60);

    private static object[] M11LiquidPreparationNeighborhood(int x, int y)
    {
        var result = new List<object>(9);
        for (int nx = x - 1; nx <= x + 1; nx++) for (int ny = y - 1; ny <= y + 1; ny++)
        {
            if (nx < 0 || nx >= Main.maxTilesX || ny < 0 || ny >= Main.maxTilesY) continue;
            var tile = Main.tile[nx, ny];
            result.Add(new { x = nx, y = ny, tile.type, tile.wall, tile.liquid, tile.sTileHeader,
                tile.bTileHeader, tile.bTileHeader2, tile.bTileHeader3, tile.frameX, tile.frameY,
                checking = tile.checkingLiquid(), skip = tile.skipLiquid(), liquidType = tile.liquidType(), active = tile.active() });
        }
        return result.ToArray();
    }

    private void DisposeM11LiquidPreparationTrace()
    {
        try { m11PreparationBody?.Dispose(); }
        finally { m11PreparationBody = null; m11PreparationUpdates?.Dispose(); m11PreparationUpdates = null; }
    }
}
