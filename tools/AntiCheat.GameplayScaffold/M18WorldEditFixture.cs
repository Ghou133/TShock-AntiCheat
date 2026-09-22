using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // The base qa_prepare uses TShock's configured GiveItem behavior. In this
    // isolated server GiveItemsDirectly is false, so that setup intentionally
    // drops materials into the world. This one-time M18-only helper places a
    // DirtBlock in a previously empty SSC inventory slot through the same
    // authorized server-side PlayerSlot path used by existing fixtures. It
    // does not overwrite an existing slot and never edits the world.
    private void M18WorldMaterialsCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_materials <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_materials.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated SSC account without bypass required.");
        string marker = Path.Combine(root!, "qa-m18-materials-issued-" + player.Index + ".json");
        Require(!File.Exists(marker), "M18 material fixture was already issued; existing player changes are retained.");

        int slot = Enumerable.Range(10, Math.Max(0, Math.Min(49, player.TPlayer.inventory.Length - 10)))
            .FirstOrDefault(index => player.TPlayer.inventory[index].IsAir, -1);
        Require(slot >= 10, "No empty normal inventory slot is available for the M18 fixture.");
        var previous = player.TPlayer.inventory[slot].Clone();
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            actor = player.Name,
            accountId = player.Account!.ID,
            slot,
            item = ItemID.DirtBlock,
            stack = 100,
            previous = new { previous.type, previous.stack, previous.prefix, previous.favorited },
            source = "one-time isolated server fixture; authorized PlayerSlot S2C; not client acquisition evidence",
            worldMutation = false
        }, jsonOptions));

        player.TPlayer.inventory[slot].SetDefaults(ItemID.DirtBlock);
        player.TPlayer.inventory[slot].stack = 100;
        player.TPlayer.inventory[slot].favorited = false;
        player.PlayerData.CopyCharacter(player);
        player.SendData(PacketTypes.PlayerSlot, "", player.Index, slot, 1, 0, ItemID.DirtBlock);
        Record("m18-materials-issued", new
        {
            actor = player.Name,
            accountId = player.Account.ID,
            slot,
            item = ItemID.DirtBlock,
            stack = 100,
            previous = new { previous.type, previous.stack, previous.prefix, previous.favorited },
            marker,
            fixtureArtificial = true,
            worldMutation = false
        });
        player.SendInfoMessage($"M18 isolated fixture item issued in normal slot {slot}; no world cell was changed.");
    }

    // A separate one-time helper for the wall operation slice. It issues only
    // ordinary placement/hammer materials through the authorized SSC
    // PlayerSlot path; it never edits the world or overwrites a player slot.
    private void M18WorldWallMaterialsCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_wall_materials <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_wall_materials.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated SSC account without bypass required.");
        string marker = Path.Combine(root!, "qa-m18-wall-materials-issued-" + player.Index + ".json");
        Require(!File.Exists(marker), "M18 wall material fixture was already issued; existing player changes are retained.");

        var requests = new[]
        {
            (item: ItemID.WoodWall, stack: 100),
            (item: ItemID.DirtWall, stack: 100),
            (item: ItemID.IronHammer, stack: 1)
        };
        var assignments = new List<(int Slot, int Item, int Stack, Item Previous)>();
        foreach (var request in requests)
        {
            int slot = Enumerable.Range(10, Math.Max(0, Math.Min(49, player.TPlayer.inventory.Length - 10)))
                .FirstOrDefault(index => player.TPlayer.inventory[index].IsAir &&
                    assignments.All(assignment => assignment.Slot != index), -1);
            Require(slot >= 10, "No empty normal inventory slot is available for the M18 wall fixture.");
            assignments.Add((slot, request.item, request.stack, player.TPlayer.inventory[slot].Clone()));
        }

        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            actor = player.Name,
            accountId = player.Account!.ID,
            assignments = assignments.Select(assignment => new
            {
                assignment.Slot,
                item = assignment.Item,
                assignment.Stack,
                previous = new
                {
                    assignment.Previous.type,
                    assignment.Previous.stack,
                    assignment.Previous.prefix,
                    assignment.Previous.favorited
                }
            }).ToArray(),
            source = "one-time isolated server fixture; authorized PlayerSlot S2C; not client acquisition evidence",
            worldMutation = false
        }, jsonOptions));

        player.PlayerData.CopyCharacter(player);
        foreach (var assignment in assignments)
        {
            var item = player.TPlayer.inventory[assignment.Slot];
            item.SetDefaults(assignment.Item);
            item.stack = assignment.Stack;
            item.favorited = false;
            player.SendData(PacketTypes.PlayerSlot, "", player.Index, assignment.Slot, 1, 0, assignment.Item);
            Record("m18-wall-material-issued", new
            {
                actor = player.Name,
                accountId = player.Account.ID,
                slot = assignment.Slot,
                item = assignment.Item,
                stack = assignment.Stack,
                fixtureArtificial = true,
                worldMutation = false,
                marker
            });
        }
        player.PlayerData.CopyCharacter(player);
        player.SendInfoMessage("M18 isolated wall materials and hammer issued in empty normal slots; no world cell was changed.");
    }

    // A separate one-time helper for the Tile replacement slice. It issues
    // matching DirtBlock and StoneBlock items through the authorized SSC
    // PlayerSlot path; it never edits the world or overwrites a player slot.
    private void M18WorldTileMaterialsCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_tile_materials <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_tile_materials.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated SSC account without bypass required.");
        string marker = Path.Combine(root!, "qa-m18-tile-materials-issued-" + player.Index + ".json");
        Require(!File.Exists(marker), "M18 tile material fixture was already issued; existing player changes are retained.");

        var requests = new[]
        {
            (item: ItemID.DirtBlock, stack: 100),
            (item: ItemID.StoneBlock, stack: 100)
        };
        var assignments = new List<(int Slot, int Item, int Stack, Item Previous)>();
        foreach (var request in requests)
        {
            int slot = Enumerable.Range(10, Math.Max(0, Math.Min(49, player.TPlayer.inventory.Length - 10)))
                .FirstOrDefault(index => player.TPlayer.inventory[index].IsAir &&
                    assignments.All(assignment => assignment.Slot != index), -1);
            Require(slot >= 10, "No empty normal inventory slot is available for the M18 tile fixture.");
            assignments.Add((slot, request.item, request.stack, player.TPlayer.inventory[slot].Clone()));
        }

        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            actor = player.Name,
            accountId = player.Account!.ID,
            assignments = assignments.Select(assignment => new
            {
                assignment.Slot,
                item = assignment.Item,
                assignment.Stack,
                previous = new
                {
                    assignment.Previous.type,
                    assignment.Previous.stack,
                    assignment.Previous.prefix,
                    assignment.Previous.favorited
                }
            }).ToArray(),
            source = "one-time isolated server fixture; authorized PlayerSlot S2C; not client acquisition evidence",
            worldMutation = false
        }, jsonOptions));

        player.PlayerData.CopyCharacter(player);
        foreach (var assignment in assignments)
        {
            var item = player.TPlayer.inventory[assignment.Slot];
            item.SetDefaults(assignment.Item);
            item.stack = assignment.Stack;
            item.favorited = false;
            player.SendData(PacketTypes.PlayerSlot, "", player.Index, assignment.Slot, 1, 0, assignment.Item);
            Record("m18-tile-material-issued", new
            {
                actor = player.Name,
                accountId = player.Account.ID,
                slot = assignment.Slot,
                item = assignment.Item,
                stack = assignment.Stack,
                fixtureArtificial = true,
                worldMutation = false,
                marker
            });
        }
        player.PlayerData.CopyCharacter(player);
        player.SendInfoMessage("M18 isolated tile materials issued in empty normal slots; no world cell was changed.");
    }

    // Read-only cell witness for the isolated M18 direct-packet slice. It never
    // repairs, clears, places, or otherwise changes the world.
    private void M18WorldCellCommand(string[] args)
    {
        Require(args.Length == 2, "Use qa_m18_cell <x> <y>.");
        var xParsed = int.TryParse(args[0], out var x);
        var yParsed = int.TryParse(args[1], out var y);
        Require(xParsed && yParsed, "The M18 cell coordinates must be integers.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_cell.");
        Require(x >= room!.Left && x < room.Left + room.Width && y >= room.Top && y <= room.FloorY,
            "The M18 cell witness must stay inside the recorded disposable room.");
        var tile = Main.tile[x, y];
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            worldId = Main.worldID,
            x,
            y,
            active = tile.active(),
            type = (int)tile.type,
            wall = (int)tile.wall,
            liquid = (int)tile.liquid,
            liquidType = (int)tile.liquidType(),
            checkingLiquid = tile.checkingLiquid(),
            frameX = (int)tile.frameX,
            frameY = (int)tile.frameY
        };
        Record("m18-world-cell", payload);
        File.WriteAllText(Path.Combine(root!, "m18-world-cell-latest.json"),
            JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage($"QA M18 cell: ({x},{y}) active={payload.active} type={payload.type} wall={payload.wall} liquid={payload.liquid}/{payload.liquidType}.");
    }

    // Read-only witness for the client-supplied packet13 position slice. The
    // outer ring is deliberately bounded around the disposable room so a
    // range target can be inspected without opening a general world-reader.
    private void M18WorldPositionCellCommand(string[] args)
    {
        Require(args.Length == 2, "Use qa_m18_position_cell <x> <y>.");
        var xParsed = int.TryParse(args[0], out var x);
        var yParsed = int.TryParse(args[1], out var y);
        Require(xParsed && yParsed, "The M18 position cell coordinates must be integers.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_position_cell.");
        Require(x >= room!.Left - 2 && x <= room.Left + room.Width + 96 &&
            y >= room.Top - 2 && y <= room.FloorY + 32 &&
            x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY,
            "The M18 position cell witness must stay in the bounded outer ring around the disposable room.");
        var tile = Main.tile[x, y];
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            worldId = Main.worldID,
            x,
            y,
            active = tile.active(),
            type = (int)tile.type,
            wall = (int)tile.wall,
            liquid = (int)tile.liquid,
            liquidType = (int)tile.liquidType(),
            checkingLiquid = tile.checkingLiquid(),
            frameX = (int)tile.frameX,
            frameY = (int)tile.frameY,
            scope = "bounded read-only outer ring (extended vertical probe); no world mutation"
        };
        Record("m18-world-position-cell", payload);
        File.WriteAllText(Path.Combine(root!, "m18-world-position-cell-latest.json"),
            JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage($"QA M18 position cell: ({x},{y}) active={payload.active} type={payload.type} wall={payload.wall} liquid={payload.liquid}/{payload.liquidType}.");
    }

    // Read-only witness for the two server-side position values used by the
    // target's update/range path. It never accepts, corrects, or writes a
    // position; it only records the state after a real packet13 arrives.
    private void M18WorldPositionStateCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_position_state <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_position_state.");
        var player = ResolvePlayer(args[0]);
        var position = player.TPlayer.position;
        var lastNet = player.LastNetPosition;
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            actor = player.Name,
            accountId = player.Account!.ID,
            playerIndex = player.Index,
            tileX = player.TileX,
            tileY = player.TileY,
            position = new { x = position.X, y = position.Y },
            lastNetPosition = new { x = lastNet.X, y = lastNet.Y },
            distanceBetweenPositionAndLastNet = Vector2.Distance(position, lastNet),
            scope = "read-only packet13 position witness; accepted state is not legality proof"
        };
        Record("m18-world-position-state", payload);
        File.WriteAllText(Path.Combine(root!, "m18-world-position-state-latest.json"),
            JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage($"QA M18 position state: {player.Name} tile=({payload.tileX},{payload.tileY}) position=({position.X},{position.Y}) lastNet=({lastNet.X},{lastNet.Y}).");
    }
}
