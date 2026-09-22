using System.Diagnostics;
using System.Text.Json;
using Terraria;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private const int AutomationPlayerLimit = 8;
    private const int AutomationChestLimit = 8;
    private const int AutomationSnapshotByteLimit = 80 * 1024;
    private readonly string automationServerSessionId = Guid.NewGuid().ToString("N");
    private readonly TSPlayer?[] automationPlayerReferences = new TSPlayer?[256];
    private readonly long[] automationPlayerGenerations = new long[256];
    private readonly AutoResetEvent automationWriteReady = new(false);
    private static readonly JsonSerializerOptions AutomationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private Thread? automationWriter;
    private AutomationSnapshot? automationPending;
    private AutomationSnapshot? automationLastSnapshot;
    private string? automationOutputPath;
    private string? automationRoot;
    private string? automationWriteFailure;
    private volatile bool automationStopping;
    private long automationNextCapture;
    private long automationObservationSequence;

    // Called only from TSAPI.GameUpdate. The writer receives value-only snapshots,
    // never TSPlayer/Player/Item/Chest references and never a gameplay callback.
    private void TickClientAutomationState()
    {
        long now = Stopwatch.GetTimestamp();
        if (automationStopping || now < automationNextCapture) return;
        automationNextCapture = now + Stopwatch.Frequency / 10;
        if (automationOutputPath is null &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMPAT_QA_ROOT"))) return;

        AutomationSnapshot snapshot;
        try
        {
            // Reuse the existing marker, world, save-directory and SQLite checks.
            // This is independent of journal recording, queue capacity and limits.
            ValidateIsolation();
            if (automationOutputPath is null)
            {
                automationRoot = root;
                automationOutputPath = Path.Combine(output!, "client-automation-state.json");
                automationWriter = new Thread(WriteClientAutomationStates)
                {
                    IsBackground = true,
                    Name = "GameplayScaffold bounded state writer"
                };
                automationWriter.Start();
            }
            Require(root == automationRoot &&
                Path.Combine(output!, "client-automation-state.json") == automationOutputPath,
                "Automation isolation configuration changed.");
            snapshot = CaptureClientAutomationState();
        }
        catch (Exception ex)
        {
            // Before verified isolation there is no approved output location.
            if (automationOutputPath is null) return;
            snapshot = NewAutomationSnapshot(null, null, [], [],
                ["state-unavailable:" + ex.GetType().Name]);
        }
        automationLastSnapshot = snapshot;
        // Capacity one, replace-oldest: a slow local disk cannot queue snapshots
        // or block the game loop. Consumers must check utc and sequence freshness.
        Interlocked.Exchange(ref automationPending, snapshot);
        automationWriteReady.Set();
    }

    private AutomationSnapshot CaptureClientAutomationState()
    {
        var unknown = new List<string>();
        var players = new List<AutomationPlayer>(AutomationPlayerLimit);
        var chestIds = new List<int>(AutomationChestLimit);
        void AddChest(int id)
        {
            if (id < 0 || chestIds.Contains(id)) return;
            if (chestIds.Count == AutomationChestLimit)
            {
                if (!unknown.Contains("chest-capacity-exceeded")) unknown.Add("chest-capacity-exceeded");
                return;
            }
            chestIds.Add(id);
        }
        if (room is not null) AddChest(room.ChestId);
        AddChest(secondaryChestId);
        if (m10QuickChest is { } quickChest)
        {
            if (quickChest.index >= 0 && quickChest.index < Main.chest.Length &&
                ReferenceEquals(Main.chest[quickChest.index], quickChest)) AddChest(quickChest.index);
            else unknown.Add("quick-stack-chest-identity-changed");
        }

        if (TShock.Players.Length > automationPlayerReferences.Length) unknown.Add("player-slot-capacity-exceeded");
        for (int slot = 0; slot < Math.Min(TShock.Players.Length, automationPlayerReferences.Length); slot++)
        {
            var actor = TShock.Players[slot];
            if (!ReferenceEquals(actor, automationPlayerReferences[slot]))
            {
                automationPlayerReferences[slot] = actor;
                automationPlayerGenerations[slot] = checked(automationPlayerGenerations[slot] + 1);
            }
            if (actor is null || !actor.RealPlayer) continue;
            if (players.Count == AutomationPlayerLimit)
            {
                if (!unknown.Contains("player-capacity-exceeded")) unknown.Add("player-capacity-exceeded");
                continue;
            }
            var player = actor.TPlayer;
            if (player is null) { unknown.Add($"player-{slot}-unavailable"); continue; }
            AddChest(actor.ActiveChest);
            float? x = float.IsFinite(player.position.X) ? player.position.X : null;
            float? y = float.IsFinite(player.position.Y) ? player.position.Y : null;
            if (x is null || y is null) unknown.Add($"player-{slot}-position-unavailable");
            string? name = actor.Name;
            if (name?.Length > 128) { name = null; unknown.Add($"player-{slot}-name-over-capacity"); }
            players.Add(new(slot, name, actor.Account?.ID, actor.IsLoggedIn,
                automationPlayerGenerations[slot], actor.Active && player.active, player.dead,
                new(x, y), player.selectedItem, player.CurrentLoadoutIndex, actor.ActiveChest,
                CaptureAutomationItems(player.inventory, 59, $"player-{slot}-inventory", unknown)));
        }
        var chests = new List<AutomationChest>(chestIds.Count);
        foreach (int id in chestIds)
        {
            var chest = id < Main.chest.Length ? Main.chest[id] : null;
            if (chest is null) { unknown.Add($"chest-{id}-unavailable"); continue; }
            if (room?.ChestId == id && (chest.x != room.ChestX || chest.y != room.ChestY))
            { unknown.Add($"chest-{id}-room-identity-changed"); continue; }
            chests.Add(new(id, chest.x, chest.y,
                CaptureAutomationItems(chest.item, 40, $"chest-{id}-slots", unknown)));
        }
        string? worldPath = Main.worldPathName;
        if (worldPath?.Length > 1024) { worldPath = null; unknown.Add("world-path-over-capacity"); }
        return NewAutomationSnapshot(Main.worldID, worldPath, players.ToArray(), chests.ToArray(), unknown.ToArray());
    }

    private static AutomationItem[] CaptureAutomationItems(Item[]? inventory, int count,
        string label, List<string> unknown)
    {
        var slots = new AutomationItem[count];
        bool complete = inventory is not null && inventory.Length >= count;
        for (int slot = 0; slot < count; slot++)
        {
            var item = inventory is not null && slot < inventory.Length ? inventory[slot] : null;
            if (item is null) complete = false;
            slots[slot] = new(slot, item?.type, item?.stack, item?.prefix, item?.favorited);
        }
        if (!complete) unknown.Add(label + "-incomplete");
        return slots;
    }

    private AutomationSnapshot NewAutomationSnapshot(int? worldId, string? worldPath,
        AutomationPlayer[] players, AutomationChest[] chests, string[] unknown)
    {
        var writeFailure = Volatile.Read(ref automationWriteFailure);
        if (writeFailure is not null) unknown = [.. unknown, writeFailure];
        return new(1, DateTimeOffset.UtcNow, ++automationObservationSequence,
            automationServerSessionId, worldId, worldPath,
            new(unknown.Length == 0 ? "ok" : "unknown", unknown), players, chests,
            new("unknown", "server-observation-only"),
            "Read-only server state; not client UI or original-executable acceptance. Check freshness, session and health before and after each action.");
    }

    private void WriteClientAutomationStates()
    {
        // One writer owns a fixed temporary path beside the destination. Atomic
        // rename prevents readers observing half a JSON document; no journal use.
        string temporaryPath = automationOutputPath + "." + automationServerSessionId + ".tmp";
        while (true)
        {
            automationWriteReady.WaitOne();
            var snapshot = Interlocked.Exchange(ref automationPending, null);
            if (snapshot is not null)
            {
                try
                {
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AutomationJsonOptions);
                    if (bytes.Length > AutomationSnapshotByteLimit)
                    {
                        snapshot = snapshot with { Health = new("unknown", ["snapshot-byte-capacity-exceeded"]), Players = [], Chests = [] };
                        bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AutomationJsonOptions);
                    }
                    File.WriteAllBytes(temporaryPath, bytes);
                    File.Move(temporaryPath, automationOutputPath!, true);
                    Volatile.Write(ref automationWriteFailure, null);
                }
                catch (Exception ex)
                {
                    // The existing file becomes stale. Never rewrite its timestamp
                    // or report success when a new observation could not be saved.
                    Volatile.Write(ref automationWriteFailure, "state-write-failed:" + ex.GetType().Name);
                }
            }
            if (automationStopping && Volatile.Read(ref automationPending) is null) break;
        }
        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void DisposeClientAutomationState()
    {
        automationStopping = true;
        if (automationLastSnapshot is { } last)
            Interlocked.Exchange(ref automationPending, last with
            {
                Utc = DateTimeOffset.UtcNow,
                ObservationSequence = ++automationObservationSequence,
                Health = new("stopped", ["plugin-disposed"]), Players = [], Chests = []
            });
        automationWriteReady.Set();
        // Bounded cleanup: a stalled disk must not stall TShock disposal. The
        // single background writer can finish its pending atomic write later.
        if (automationWriter is null || automationWriter.Join(250)) automationWriteReady.Dispose();
    }

    private sealed record AutomationSnapshot(int SchemaVersion, DateTimeOffset Utc, long ObservationSequence,
        string ServerSessionId, int? WorldId, string? WorldPath, AutomationHealth Health,
        AutomationPlayer[] Players, AutomationChest[] Chests, AutomationClientUi ClientUi, string Note);
    private sealed record AutomationHealth(string Status, string[] Unknown);
    private sealed record AutomationClientUi(string Status, string Reason);
    private sealed record AutomationPosition(float? X, float? Y);
    private sealed record AutomationPlayer(int Slot, string? Name, int? AccountId, bool IsLoggedIn,
        long SessionGeneration, bool Active, bool Dead, AutomationPosition Position, int SelectedItem,
        int CurrentLoadoutIndex, int ActiveChest, AutomationItem[] Inventory);
    private sealed record AutomationChest(int Id, int X, int Y, AutomationItem[] Slots);
    private sealed record AutomationItem(int Slot, int? Type, int? Stack, byte? Prefix, bool? Favorited);
}
