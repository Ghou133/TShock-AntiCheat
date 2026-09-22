using System.Diagnostics;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private int m16WallActor = -1, m16WallX, m16WallY;
    private string m16WallMode = "allowed";
    private TShockAPI.DB.Region? m16WallRegion;
    private long m16WallArmedUntil;
    private int m16WallFaults;

    private void InstallM16WorldPaint()
    {
        HookEvents.Terraria.WorldGen.paintWall += M16BeforeWallPaint;
        HookEvents.Terraria.WorldGen.paintEffect += M16WallPaintEffect;
    }
    private void DisposeM16WorldPaint()
    {
        HookEvents.Terraria.WorldGen.paintWall -= M16BeforeWallPaint;
        HookEvents.Terraria.WorldGen.paintEffect -= M16WallPaintEffect;
    }
    private void PrepareM16WallPaint(string[] args)
    {
        Require(args.Length == 1 && m16WallActor < 0, "Use qa_m16_wall <ordinary actor>, once per isolated run.");
        var actor = ResolvePlayer(args[0]);
        Require(!actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.editregion) &&
            !actor.HasPermission(Permissions.bypassssc) && actor.HasPermission(Permissions.canpaint), "Ordinary SSC/paint permissions required.");
        if (room is null) Prepare(actor);
        m16WallX = room!.Left + 23; m16WallY = room.FloorY - 2;
        Require(actor.HasBuildPermission(m16WallX, m16WallY, false) && !Main.tile[m16WallX, m16WallY].active(), "Owned empty wall fixture cell required.");
        string name = "qa_m16_wall_" + Main.worldID;
        Require(TShock.Regions.AddRegion(m16WallX, m16WallY, 1, 1, name, "qa-scaffold-owner", Main.worldID.ToString(), 1000001), "New owned wall paint region required.");
        m16WallRegion = TShock.Regions.GetRegionByName(name); m16WallRegion.AllowedIDs.Add(actor.Account.ID);
        var tile = Main.tile[m16WallX, m16WallY]; tile.wall = WallID.Stone; tile.wallColor(2); tile.color(3);
        var previous = actor.SelectedItem.Clone(); actor.SelectedItem.SetDefaults(ItemID.PaintRoller);
        actor.PlayerData.CopyCharacter(actor);
        actor.SendData(PacketTypes.PlayerSlot, "", actor.Index, actor.TPlayer.selectedItem, 1, 0, ItemID.PaintRoller);
        m16WallActor = actor.Index;
        NetMessage.SendTileSquare(-1, m16WallX, m16WallY, 1);
        Record("m16-wall-prepared", new { fixtureArtificial = true, actor.Index, actor.Account.ID, m16WallX, m16WallY,
            previousSelected = new { previous.type, previous.stack, previous.prefix }, tool = ItemID.PaintRoller,
            note = "Isolated setup only; subsequent TCP64 uses ordinary TShock/Bouncer and actual product/native commit path." });
        WriteM16WallPaintState();
    }
    private void ArmM16WallPaint(string[] args)
    {
        Require(args.Length == 1 && m16WallActor >= 0 && args[0] is "allowed" or "deny-before" or "revoke-during" or
            "aba-during" or "replace-during" or "later-paint-during" or "core-denied", "Known wall paint mode required.");
        var actor = TShock.Players[m16WallActor];
        Require(actor is { IsLoggedIn: true, Account: not null }, "Original logged-in actor required.");
        m16WallRegion!.AllowedIDs.Clear();
        if (args[0] != "core-denied") m16WallRegion.AllowedIDs.Add(actor!.Account!.ID);
        m16WallMode = args[0]; m16WallArmedUntil = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        WriteM16WallPaintState();
    }
    private void M16BeforeWallPaint(object? _, HookEvents.Terraria.WorldGen.paintWallEventArgs e)
    {
        if (m16WallMode != "deny-before" || e.x != m16WallX || e.y != m16WallY || Stopwatch.GetTimestamp() > m16WallArmedUntil) return;
        m16WallMode = "allowed"; m16WallArmedUntil = 0; m16WallRegion!.AllowedIDs.Clear(); m16WallFaults++;
        Record("m16-wall-permission-lost-before-native-body", new { e.x, e.y, trustedHostFault = true });
    }
    private void M16WallPaintEffect(object? _, HookEvents.Terraria.WorldGen.paintEffectEventArgs e)
    {
        if (m16WallActor < 0 || e.x != m16WallX || e.y != m16WallY ||
            m16WallMode is "allowed" or "core-denied" || Stopwatch.GetTimestamp() > m16WallArmedUntil) return;
        string mode = m16WallMode; m16WallMode = "allowed"; m16WallArmedUntil = 0;
        var tile = Main.tile[e.x, e.y]; byte committed = tile.wallColor();
        if (mode == "aba-during") { tile.wallColor(15); tile.wallColor(committed); }
        if (mode == "later-paint-during") tile.wallColor(18);
        if (mode == "replace-during")
        {
            var prior = new Tile(); prior.CopyFrom(tile);
            Main.tile[e.x, e.y] = new Tile(); Main.tile[e.x, e.y] = prior;
        }
        m16WallRegion!.AllowedIDs.Clear(); m16WallFaults++;
        Record("m16-wall-permission-lost-after-native-color-write", new { mode, e.x, e.y, committed, trustedHostFault = true,
            note = "paintWall writes before paintEffect; controlled host callback is diagnostic, never production cheat proof." });
    }
    private void WriteM16WallPaintState()
    {
        Require(m16WallActor >= 0, "Prepare M16 wall paint first.");
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_paintRecovery", PrivateM5)?.GetValue(plugin);
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var actor = TShock.Players[m16WallActor]; var tile = Main.tile[m16WallX, m16WallY];
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, x = m16WallX, y = m16WallY,
            color = (int)tile.wallColor(), blockColor = (int)tile.color(), wall = (int)tile.wall, blockActive = tile.active(),
            guardPresent = guard is not null, healthy = Value("Healthy"), invalidReason = Value("InvalidReason"),
            allowed = Value("Allowed"), blocked = Value("BeforeWriteBlocked"), restored = Value("Restored"),
            refused = Value("RecoveryRefused"), relaySuppressed = Value("RelaySuppressed"), unknown = Value("Unknown"),
            journal = guard?.GetType().GetMethod("Snapshot")?.Invoke(guard, null), m16WallMode, m16WallFaults,
            actor = new { actor?.Index, account = actor?.Account?.ID, bypass = actor?.HasPermission("anticheat.bypass"),
                allowed = actor?.HasPaintPermission(m16WallX, m16WallY), selected = actor?.SelectedItem.type } };
        File.WriteAllText(Path.Combine(output!, "m16-wall-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
}
