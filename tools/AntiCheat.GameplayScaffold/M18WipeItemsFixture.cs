using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // One-time server-owned batch for the actual WipeGroundItems sequence.
    // Item.NewItem and the packet21/22 broadcasts are native control setup;
    // the later client packet13/21 sequence is still the operation under test.
    private void M18WipeItemsCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_wipe_items <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_wipe_items.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated account without bypass required.");

        string marker = Path.Combine(root!, "qa-m18-wipe-items-" + player.Index + ".json");
        Require(!File.Exists(marker), "The one-time M18 WipeGroundItems batch already ran; existing world changes are retained.");

        const int stack = 3;
        var definitions = new[]
        {
            (itemType: ItemID.StoneBlock, tileOffset: 8),
            (itemType: ItemID.DirtBlock, tileOffset: 10),
            (itemType: ItemID.Wood, tileOffset: 12),
            (itemType: ItemID.StoneBlock, tileOffset: 14),
            (itemType: ItemID.DirtBlock, tileOffset: 16),
        };
        var created = new List<object>(definitions.Length);
        var createdIndices = new List<int>(definitions.Length);
        foreach (var definition in definitions)
        {
            int tileX = room!.Left + definition.tileOffset;
            int tileY = room.FloorY - 2;
            Require(tileX >= room.Left && tileX < room.Left + room.Width &&
                tileY > room.Top && tileY < room.FloorY,
                "The WipeGroundItems batch must remain inside the recorded disposable room.");

            int index = Item.NewItem(null, tileX * 16 + 8, tileY * 16 + 8, player.TPlayer.width,
                player.TPlayer.height, definition.itemType, stack, noBroadcast: true, 0,
                Terraria.NewItemOwnership.None);
            Require(index >= 0 && index < Math.Min(400, Main.maxItems) && Main.item[index].active,
                "Native WipeGroundItems batch item creation failed; existing items remain recorded.");
            var item = Main.item[index];
            Require(item.type == definition.itemType && item.stack == stack && !item.beingGrabbed,
                "The WipeGroundItems batch item did not retain its active non-grabbed state.");
            item.playerIndexTheItemIsReservedFor = player.Index;
            createdIndices.Add(index);
            created.Add(new
            {
                index,
                type = item.type,
                stack = item.stack,
                prefix = item.prefix,
                active = item.active,
                beingGrabbed = item.beingGrabbed,
                reservation = item.playerIndexTheItemIsReservedFor,
                position = new { x = item.position.X, y = item.position.Y },
                velocity = new { x = item.velocity.X, y = item.velocity.Y },
                tileX,
                tileY
            });
        }

        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            fixtureArtificial = true,
            command = "qa_m18_wipe_items",
            source = "native Item.NewItem(noBroadcast) + reservation + packet21/22 broadcast",
            actor = player.Name,
            accountId = player.Account!.ID,
            playerIndex = player.Index,
            scanLimit = 400,
            created,
            worldMutation = true,
            beingGrabbedExcluded = true,
            attributionBoundary = "server-owned live-item batch is a control; it does not establish a client creator or authorize a later clear",
            sanction = "none"
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(payload, jsonOptions));
        Record("m18-wipe-items-control", payload);

        foreach (int index in createdIndices)
        {
            NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, index, 1f);
            NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, index);
        }
        TSPlayer.Server.SendInfoMessage("M18 WipeGroundItems batch broadcast: " + created.Count +
            " active items; scanLimit=400; reservation=" + player.Index + ".");
    }
}
