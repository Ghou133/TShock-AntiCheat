using System.Diagnostics;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private int m9PaintActor = -1, m9PaintX, m9PaintY;
    private string m9PaintMode = "allowed";
    private TShockAPI.DB.Region? m9PaintRegion;
    private long m9PaintArmedUntil;
    private int m9PaintFaults;
    private Hook? m9LiquidTiming;
    private (int X, int Y, int Width, int Height)? m9Facility;
    private readonly List<double> m9LiquidCalls = new(4096);
    private int m9LiquidCallsDropped, m9LiquidPeak, m9BufferPeak;
    private long m9FacilityTotal;
    private TimeSpan m9FacilityCpu;
    private long m9FacilityStart;

    private void InstallM9World()
    {
        HookEvents.Terraria.WorldGen.paintTile += M9BeforePaint;
        HookEvents.Terraria.WorldGen.paintEffect += M9PaintEffect;
    }
    private void DisposeM9World()
    {
        HookEvents.Terraria.WorldGen.paintEffect -= M9PaintEffect;
        HookEvents.Terraria.WorldGen.paintTile -= M9BeforePaint;
        m9LiquidTiming?.Dispose(); m9LiquidTiming = null;
    }

    private void PrepareM9Paint(string[] args)
    {
        Require(args.Length == 1 && m9PaintActor < 0, "Use qa_m9_paint <ordinary actor>, once per isolated run.");
        var actor = ResolvePlayer(args[0]);
        Require(!actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.editregion) &&
            !actor.HasPermission(Permissions.bypassssc) && actor.HasPermission(Permissions.canpaint), "Ordinary SSC/paint permissions required.");
        if (room is null) Prepare(actor);
        m9PaintX = room!.Left + 19; m9PaintY = room.FloorY - 2;
        // The existing craft fixture also places a statue at offset19. A combined GUI
        // session uses another empty owned cell; no existing object is cleared or moved.
        if (Main.tile[m9PaintX, m9PaintY].active()) m9PaintX = room.Left + 21;
        Require(!Main.tile[m9PaintX, m9PaintY].active() && actor.HasBuildPermission(m9PaintX, m9PaintY, false), "Owned empty paint cell required.");
        string regionName = "qa_m9_paint_" + Main.worldID;
        Require(TShock.Regions.AddRegion(m9PaintX, m9PaintY, 1, 1, regionName, "qa-scaffold-owner", Main.worldID.ToString(), 1000001), "New owned paint region required.");
        m9PaintRegion = TShock.Regions.GetRegionByName(regionName); m9PaintRegion.AllowedIDs.Add(actor.Account.ID);
        var tile = Main.tile[m9PaintX, m9PaintY]; tile.active(true); tile.type = TileID.Stone; tile.color(2);
        // Console setup, no natural item acquisition claim. Existing selected content is retained
        // in the evidence before replacing one owned synthetic actor slot with a normal tool.
        var previous = actor.SelectedItem.Clone();
        actor.SelectedItem.SetDefaults(ItemID.Paintbrush);
        actor.PlayerData.CopyCharacter(actor);
        actor.SendData(PacketTypes.PlayerSlot, "", actor.Index, actor.TPlayer.selectedItem, 1, 0, ItemID.Paintbrush);
        m9PaintActor = actor.Index;
        NetMessage.SendTileSquare(-1, m9PaintX, m9PaintY, 1);
        Record("m9-paint-prepared", new { fixtureArtificial = true, actor.Index, actor.Account.ID, m9PaintX, m9PaintY,
            previousSelected = new { previous.type, previous.stack, previous.prefix }, tool = ItemID.Paintbrush,
            note = "Only isolated setup. TCP63 must pass ordinary TShock/Bouncer and actual product request/native commit guards." });
        WriteM9PaintState();
    }
    private void ArmM9Paint(string[] args)
    {
        Require(args.Length == 1 && m9PaintActor >= 0 && args[0] is "allowed" or "deny-before" or "revoke-during" or "aba-during" or "replace-during", "Known one-shot paint mode required.");
        var actor = TShock.Players[m9PaintActor];
        Require(actor is { IsLoggedIn: true, Account: not null }, "Original logged-in actor required.");
        m9PaintRegion!.AllowedIDs.Clear();
        // deny-before intentionally enters native guard after TShock's prior check; see event
        // below. Core denial itself is separately verified without manufacturing cheat proof.
        m9PaintRegion.AllowedIDs.Add(actor!.Account!.ID);
        m9PaintMode = args[0]; m9PaintArmedUntil = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        WriteM9PaintState();
    }
    private void M9PaintEffect(object? _, HookEvents.Terraria.WorldGen.paintEffectEventArgs e)
    {
        if (m9PaintActor < 0 || e.x != m9PaintX || e.y != m9PaintY || m9PaintMode == "allowed" || Stopwatch.GetTimestamp() > m9PaintArmedUntil) return;
        string mode = m9PaintMode; m9PaintMode = "allowed"; m9PaintArmedUntil = 0;
        if (mode == "aba-during")
        { var tile = Main.tile[e.x, e.y]; byte prior = tile.color(); tile.color(15); tile.color(prior); }
        if (mode == "replace-during")
        {
            // Constileation returns an alias, so retaining its TileReference would also observe
            // the replacement's zeros. Copy the value first to exercise real replacement A→B→A.
            var prior = new Tile(); prior.CopyFrom(Main.tile[e.x, e.y]);
            Main.tile[e.x, e.y] = new Tile(); Main.tile[e.x, e.y] = prior;
        }
        m9PaintRegion!.AllowedIDs.Clear(); m9PaintFaults++;
        Record("m9-paint-authorization-change-inside-native-effect", new { mode, e.x, e.y,
            note = "Controlled trusted-host TOCTOU fault; not a claim that an ordinary client can change permissions. Native paint and recovery still execute for the real TCP request." });
    }
    private void M9BeforePaint(object? _, HookEvents.Terraria.WorldGen.paintTileEventArgs e)
    {
        if (m9PaintMode != "deny-before" || e.x != m9PaintX || e.y != m9PaintY || Stopwatch.GetTimestamp() > m9PaintArmedUntil) return;
        m9PaintMode = "allowed"; m9PaintArmedUntil = 0; m9PaintRegion!.AllowedIDs.Clear(); m9PaintFaults++;
        Record("m9-paint-authorization-change-before-native-body", new { e.x, e.y,
            note = "Controlled trusted-host change after TShock checks. Product native-body guard must reject before effects/write and suppress the later MessageBuffer relay." });
    }
    private void WriteM9PaintState()
    {
        Require(m9PaintActor >= 0, "Prepare M9 paint first.");
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_paintRecovery", PrivateM5)?.GetValue(plugin);
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var actor = TShock.Players[m9PaintActor];
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, x = m9PaintX, y = m9PaintY,
            color = (int)Main.tile[m9PaintX, m9PaintY].color(), guardPresent = guard is not null,
            healthy = Value("Healthy"), invalidReason = Value("InvalidReason"), lastUnknown = Value("LastUnknown"),
            tileProvider = Main.tile.GetType().AssemblyQualifiedName, tileType = Main.tile[m9PaintX, m9PaintY].GetType().AssemblyQualifiedName,
            allowed = Value("Allowed"), blocked = Value("BeforeWriteBlocked"), restored = Value("Restored"),
            refused = Value("RecoveryRefused"), relaySuppressed = Value("RelaySuppressed"), unknown = Value("Unknown"),
            journal = guard?.GetType().GetMethod("Snapshot")?.Invoke(guard, null),
            m9PaintMode, m9PaintFaults, actor = new { actor?.Index, account = actor?.Account?.ID,
                bypass = actor?.HasPermission("anticheat.bypass"), allowed = actor?.HasPaintPermission(m9PaintX, m9PaintY), selected = actor?.SelectedItem.type } };
        File.WriteAllText(Path.Combine(output!, "m9-paint-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void PrepareM9LiquidFacility(string[] args)
    {
        Require(args.Length == 1 && m9Facility is null && room is not null, "Use qa_m9_liquid <ordinary actor>, once after owned room preparation.");
        var actor = ResolvePlayer(args[0]);
        const int width = 242, height = 202;
        int left = room!.Left + 60, top = Math.Max(25, room.Top - height - 20);
        Require(left + width < Main.maxTilesX - 20 && top + height < Main.maxTilesY - 20, "Facility bounds unavailable.");
        var area = new Microsoft.Xna.Framework.Rectangle(left - 1, top - 1, width + 2, height + 2);
        Require(!Main.chest.Any(c => c is not null && area.Contains(c.x, c.y)) && !Main.sign.Any(s => s is not null && area.Contains(s.x, s.y)) &&
            !TileEntity.ByPosition.Keys.Any(p => area.Contains(p.X, p.Y)) && !TShock.Regions.Regions.Any(r => r.Area.Intersects(area)), "Facility cannot overlap stored objects or protected regions.");
        for (int x = left; x < left + width; x++) for (int y = top; y < top + height; y++)
            Require(Main.tile[x, y] is { liquid: 0 } tile && !tile.checkingLiquid(), "Facility must start dry with no existing queued liquid; no stale queue entries may be overwritten.");
        m9Facility = (left, top, width, height); // Own and record bounds before the first setup write.
        Record("m9-liquid-facility-begin", new { fixtureArtificial = true, left, top, width, height, actor = actor.Account.ID });
        for (int x = left; x < left + width; x++) for (int y = top; y < top + height; y++)
        {
            var tile = Main.tile[x, y]; tile.ClearEverything();
            if (x == left || x == left + width - 1 || y == top || y == top + height - 1 || ((x - left) & 1) == 0)
            { tile.active(true); tile.type = TileID.Glass; }
        }
        int seeds = 0;
        // 120 independently enclosed one-cell-wide wells, each with180 full water cells and20
        // empty lower cells. This second legal facility avoids horizontal averaging; the earlier
        // checkerboard reservoir's one-unit conservation failure remains in its original report.
        for (int x = left + 1; x < left + width - 1; x++) for (int y = top + 1; y <= top + 180; y++)
            if (((x - left) & 1) == 1) { Main.tile[x, y].liquid = 255; Liquid.AddWater(x, y); seeds++; }
        m9FacilityTotal = seeds * 255L;
        m9LiquidCalls.Clear(); m9LiquidCallsDropped = m9LiquidPeak = m9BufferPeak = 0;
        using (var process = Process.GetCurrentProcess()) m9FacilityCpu = process.TotalProcessorTime;
        m9FacilityStart = Stopwatch.GetTimestamp();
        m9LiquidTiming = new Hook(typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid))!, (Action<Action>)(original =>
        {
            long start = Stopwatch.GetTimestamp();
            try { original(); }
            finally
            {
                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (m9LiquidCalls.Count < 4096) m9LiquidCalls.Add(milliseconds); else m9LiquidCallsDropped++;
                m9LiquidPeak = Math.Max(m9LiquidPeak, Liquid.numLiquid); m9BufferPeak = Math.Max(m9BufferPeak, LiquidBuffer.numLiquidBuffer);
            }
        }));
        Record("m9-liquid-facility-seeded", new { fixtureArtificial = true, layout = "120-independent-one-cell-wide-wells", seeds, water = m9FacilityTotal,
            directNativeUpdateCalls = 0, oneTimeNativeAddWater = seeds, refill = false,
            note = "Natural server GameUpdate alone advances every later complete step; no fixture hold, debt reset, artificial tick advance or queue clipping." });
        WriteM9LiquidFacilityState();
    }
    private void WriteM9LiquidFacilityState()
    {
        Require(m9Facility is not null, "Prepare M9 facility first."); var f = m9Facility!.Value;
        long total = 0; int checking = 0, occupied = 0;
        for (int x = f.X; x < f.X + f.Width; x++) for (int y = f.Y; y < f.Y + f.Height; y++)
        { var tile = Main.tile[x, y]; total += tile.liquid; if (tile.checkingLiquid()) checking++; if (tile.liquid > 0) occupied++; }
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_liquidExecution", PrivateM5)?.GetValue(plugin);
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var samples = m9LiquidCalls.Order().ToArray();
        double Quantile(double fraction) => samples.Length == 0 ? 0 : samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * fraction) - 1)];
        using var process = Process.GetCurrentProcess();
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, layout = "120-independent-one-cell-wide-wells", elapsedMs = Stopwatch.GetElapsedTime(m9FacilityStart).TotalMilliseconds,
            total, expectedTotal = m9FacilityTotal, checking, occupied, Liquid.numLiquid, LiquidBuffer.numLiquidBuffer,
            Liquid.panicMode, Liquid.panicCounter, m9LiquidPeak, m9BufferPeak, healthy = Value("ContractHealthy"),
            allowed = Value("Allowed"), budgetPaused = Value("BudgetPaused"), observedWork = Value("ObservedSchedulerWorkUnits"),
            admittedWork = Value("AdmittedSchedulerWorkUnits"), unpaid = Value("UnpaidSchedulerWorkUnits"), recoveryPassed = Value("RecoveryPassed"),
            schedulerCalls = samples.Length, m9LiquidCallsDropped,
            callWallMilliseconds = new { p50 = Quantile(.5), p95 = Quantile(.95), p99 = Quantile(.99), max = samples.LastOrDefault(), sum = samples.Sum() },
            processCpuMilliseconds = (process.TotalProcessorTime - m9FacilityCpu).TotalMilliseconds, process.WorkingSet64,
            costBoundary = "Wall time wraps the real complete scheduler/guard call including transitive native work; process CPU covers the whole server. Neither equals scheduler work units. Fixture snapshot scan is outside measured scheduler calls." };
        File.WriteAllText(Path.Combine(output!, "m9-liquid-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
}
