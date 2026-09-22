using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // One-time, console-owned P0-D control. This follows the target TShock
    // Bouncer's existing server-side item-return shape: native Item.NewItem,
    // explicit reservation, then packet21/packet22 broadcast. It is setup
    // evidence only and never feeds a player sanction.
    private void M18ServerItemCommand(string[] args)
    {
        Require(args.Length == 1, "Use qa_m18_server_item <exact authenticated player>.");
        Require(room is { State: "prepared" }, "Run qa_prepare before qa_m18_server_item.");
        var player = ResolvePlayer(args[0]);
        Require(!player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
            !player.HasPermission(Permissions.editregion), "Ordinary authenticated account without bypass required.");

        string marker = Path.Combine(root!, "qa-m18-server-item-" + player.Index + ".json");
        Require(!File.Exists(marker), "The one-time M18 server-item control already ran; existing world changes are retained.");

        const int itemType = ItemID.StoneBlock;
        const int stack = 9999;
        int tileX = room!.Left + 6, tileY = room.FloorY - 2;
        Require(tileX >= room.Left && tileX < room.Left + room.Width && tileY > room.Top && tileY < room.FloorY,
            "The server-item control must remain inside the recorded disposable room.");

        int index = Item.NewItem(null, tileX * 16 + 8, tileY * 16 + 8, player.TPlayer.width,
            player.TPlayer.height, itemType, stack, noBroadcast: true, 0, Terraria.NewItemOwnership.None);
        Require(index >= 0 && index < Main.maxItems && Main.item[index].active,
            "Native server-owned world-item creation failed; no broadcast was attempted.");
        var item = Main.item[index];
        Require(item.type == itemType && item.stack == stack && item.maxStack >= stack,
            "The server-owned control item did not retain the legal 9999 stack definition.");

        item.playerIndexTheItemIsReservedFor = player.Index;
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            fixtureArtificial = true,
            source = "existing target TShock Bouncer server-side Item.NewItem + reservation + packet21/22 control",
            actor = player.Name,
            accountId = player.Account!.ID,
            playerIndex = player.Index,
            itemIndex = index,
            itemType,
            stack,
            maxStack = item.maxStack,
            tileX,
            tileY,
            reservation = item.playerIndexTheItemIsReservedFor,
            noBroadcastCreation = true,
            worldMutation = true,
            attributionBoundary = "server fixture reservation is a control, not creator proof for a client packet21 request",
            sanction = "none"
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(payload, jsonOptions));
        Record("m18-server-item-control", payload);

        NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, index, 1f);
        NetMessage.SendData((int)PacketTypes.ItemOwner, -1, -1, null, index);
        File.WriteAllText(Path.Combine(root!, "m18-server-item-latest.json"),
            JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage("M18 server-owned item control broadcast: index=" + index + ", type=" + itemType + ", stack=" + stack + ", reservedFor=" + player.Index + ".");
    }
}
