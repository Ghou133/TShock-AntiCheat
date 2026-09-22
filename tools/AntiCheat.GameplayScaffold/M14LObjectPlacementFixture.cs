using System.Buffers.Binary;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private readonly Dictionary<int, TileEntity> m14lPlacementObjects = [];
    private TSPlayer? m14lPlacementActor, m14lPlacementPeer;
    private TShockAPI.DB.Region? m14lPlacementRegion;
    private int[] m14lPlacementPriorAllowed = [];
    private bool m14lPlacementObservers;
    private long m14lPlacementRaw, m14lPlacementSends;
    private object? m14lPlacementLastRaw;

    private void M14LObjectPlacementCommand(string[] args)
    {
        Require(args.Length > 0, "Use qa_m14l_placement setup <actor> <peer> | select <89|123|133> | allow | deny | state | finish.");
        if (args[0] == "setup")
        {
            Require(args.Length == 3 && m14lPlacementObjects.Count == 0, "One owned fixture per run.");
            var actor = ResolvePlayer(args[1]); var peer = ResolvePlayer(args[2]);
            Require(actor.Index != peer.Index && actor.Account?.ID != peer.Account?.ID, "Independent actor and peer required.");
            foreach (var player in new[] { actor, peer })
                Require(player.IsLoggedIn && player.Account is not null && player.HasSentInventory &&
                    !player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
                    !player.HasPermission(Permissions.editregion), "Ordinary authenticated SSC actors required.");
            if (room is null) Prepare(actor);
            m14lPlacementRegion = TShock.Regions.GetRegionByName(room!.RegionName);
            Require(m14lPlacementRegion is not null, "Owned test room region required.");
            m14lPlacementPriorAllowed = m14lPlacementRegion!.AllowedIDs.ToArray();
            if (!m14lPlacementRegion.AllowedIDs.Contains(actor.Account!.ID)) m14lPlacementRegion.AllowedIDs.Add(actor.Account.ID);
            int offset = 0;
            foreach (int packet in new[] { 89, 123, 133 })
            {
                int size = packet switch { 89 => 2, 123 => 3, _ => 1 };
                int x = room.Left + 30 + offset, y = room.FloorY - size; offset += packet == 89 ? 3 : 4;
                Require(x >= room.RegionX && x + size <= room.RegionX + room.RegionWidth &&
                    y >= room.RegionY && y + size <= room.FloorY,
                    "Every object footprint must stay inside the owned prepared region and above its floor.");
                Require(!TileEntity.ByPosition.ContainsKey(new Point16(x, y)), "Existing entity cannot be replaced.");
                for (int dx = 0; dx < size; dx++) for (int dy = 0; dy < size; dy++)
                    Require(!Main.tile[x + dx, y + dy].active(), "Only empty owned room cells may be used.");
                ushort type = packet switch { 89 => TileID.ItemFrame, 123 => TileID.WeaponsRack2, _ => TileID.FoodPlatter };
                for (int dx = 0; dx < size; dx++) for (int dy = 0; dy < size; dy++)
                {
                    var tile = Main.tile[x + dx, y + dy]; tile.active(true); tile.type = type;
                    tile.frameX = (short)(dx * 18); tile.frameY = (short)(dy * 18); tile.wall = WallID.Wood;
                }
                int id = packet switch { 89 => TileEntityType<TEItemFrame>.Place(x, y),
                    123 => TileEntityType<TEWeaponsRack>.Place(x, y), _ => TileEntityType<TEFoodPlatter>.Place(x, y) };
                var entity = TileEntity.ByID[id];
                Require(entity.IsTileValidForEntity(x, y), "Actual native valid object required.");
                m14lPlacementObjects.Add(packet, entity);
                NetMessage.SendTileSquare(-1, x, y, size, size); NetMessage.SendData(86, -1, -1, null, id);
            }
            m14lPlacementActor = actor; m14lPlacementPeer = peer;
            ServerApi.Hooks.NetGetData.Register(this, ObserveM14LPlacementRaw, -1001);
            HookEvents.Terraria.NetMessage.SendData += ObserveM14LPlacementSend;
            m14lPlacementObservers = true;
            Record("m14l-placement-setup", new { actor = actor.Name, peer = peer.Name,
                artificial = true, objects = m14lPlacementObjects.Keys, scope = "owned room empty display cells and ordinary region grant" });
        }
        else
        {
            Require(m14lPlacementActor is not null, "Prepare owned fixture first.");
            switch (args[0])
            {
                case "select":
                    Require(args.Length == 2 && int.TryParse(args[1], out _) && m14lPlacementObjects.ContainsKey(int.Parse(args[1])), "Select89,123,133.");
                    int packet = int.Parse(args[1]); var entity = m14lPlacementObjects[packet];
                    int type = packet == 133 ? ItemID.Apple : ItemID.WoodenSword;
                    foreach (var player in new[] { m14lPlacementActor!, m14lPlacementPeer! })
                    {
                        player.TPlayer.selectedItemState.Select(0); player.TPlayer.inventory[0].SetDefaults(type);
                        player.TPlayer.inventory[0].stack = 1; player.TPlayer.itemTime = 0;
                        player.Teleport((entity.Position.X - 2) * 16, (room!.FloorY - 3) * 16);
                        NetMessage.SendData(5, -1, -1, null, player.Index, 0);
                    }
                    Record("m14l-placement-select", new { packet, type, source = "explicit fixture item and location, not original acquisition" });
                    break;
                case "allow":
                    if (!m14lPlacementRegion!.AllowedIDs.Contains(m14lPlacementActor!.Account.ID))
                        m14lPlacementRegion.AllowedIDs.Add(m14lPlacementActor.Account.ID);
                    break;
                case "deny": m14lPlacementRegion!.AllowedIDs.Remove(m14lPlacementActor!.Account.ID); break;
                case "state": break;
                case "finish": DisposeM14LObjectPlacement(); break;
                default: throw new InvalidOperationException("Unknown placement fixture operation.");
            }
        }
        WriteM14LPlacementState();
    }

    private void ObserveM14LPlacementRaw(GetDataEventArgs args)
    {
        if ((int)args.MsgID is not (89 or 123 or 133) || args.Msg is null ||
            !(ReferenceEquals(TShock.Players[args.Msg.whoAmI], m14lPlacementActor) ||
              ReferenceEquals(TShock.Players[args.Msg.whoAmI], m14lPlacementPeer))) return;
        int length = args.Length - 1;
        if (length < 0 || args.Index < 0 || args.Index > args.Msg.readBuffer.Length - length) return;
        var body = args.Msg.readBuffer.AsSpan(args.Index, length); m14lPlacementRaw++;
        m14lPlacementLastRaw = new { sequence = m14lPlacementRaw, packet = (int)args.MsgID,
            receivingSlot = args.Msg.whoAmI, length, handled = args.Handled,
            stack = length == 9 ? (int?)BinaryPrimitives.ReadInt16LittleEndian(body[7..]) : null,
            hex = Convert.ToHexString(body[..Math.Min(length, 12)]) };
    }

    private void ObserveM14LPlacementSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.ContinueExecution && args.msgType == 86 && m14lPlacementObjects.Values.Any(entity => entity.ID == args.number))
            m14lPlacementSends++;
    }

    private static Item M14LPlacementItem(TileEntity entity) => entity switch
        { TEItemFrame frame => frame.item, TEWeaponsRack rack => rack.item, TEFoodPlatter platter => platter.item, _ => throw new InvalidOperationException() };

    private void WriteM14LPlacementState()
    {
        Require(m14lPlacementActor is not null && m14lPlacementPeer is not null, "Prepare fixture first.");
        var plugin = M5Plugin();
        object Actor(TSPlayer actor) => new { slot = actor.Index, account = actor.Account.ID, actor.Name,
            actor.IsLoggedIn, actor.HasSentInventory, bypass = actor.HasPermission("anticheat.bypass"),
            sscBypass = actor.HasPermission(Permissions.bypassssc),
            sameActor = ReferenceEquals(TShock.Players[actor.Index], actor),
            rackAllowed = actor.HasBuildPermissionForTileObject(m14lPlacementObjects[123].Position.X, m14lPlacementObjects[123].Position.Y, 3, 3, false) };
        var snapshot = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true,
            objects = m14lPlacementObjects.Select(entry => new { packet = entry.Key, id = entry.Value.ID,
                x = entry.Value.Position.X, y = entry.Value.Position.Y,
                type = M14LPlacementItem(entry.Value).type, stack = M14LPlacementItem(entry.Value).stack,
                prefix = M14LPlacementItem(entry.Value).prefix,
                tileValid = entry.Value.IsTileValidForEntity(entry.Value.Position.X, entry.Value.Position.Y),
                registered = TileEntity.ByID.TryGetValue(entry.Value.ID, out var current) && ReferenceEquals(current, entry.Value) }).ToArray(),
            actor = Actor(m14lPlacementActor!), peer = Actor(m14lPlacementPeer!),
            worldItems = Main.item.Select((item, index) => new { item, index }).Where(entry => entry.item.active)
                .Select(entry => new { entry.index, entry.item.type, entry.item.stack, entry.item.prefix }).ToArray(),
            raw = m14lPlacementRaw, sends = m14lPlacementSends, lastRaw = m14lPlacementLastRaw,
            safetyBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin),
            guardPresent = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M14LObjectPlacementSafety") is not null,
            currentAllowed = m14lPlacementRegion!.AllowedIDs.ToArray(), originalAllowed = m14lPlacementPriorAllowed,
            observersInstalled = m14lPlacementObservers };
        File.WriteAllText(Path.Combine(output!, "m14l-placement-state-latest.json"), JsonSerializer.Serialize(snapshot, jsonOptions));
    }

    private void DisposeM14LObjectPlacement()
    {
        if (m14lPlacementObservers)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, ObserveM14LPlacementRaw);
            HookEvents.Terraria.NetMessage.SendData -= ObserveM14LPlacementSend;
            m14lPlacementObservers = false;
        }
        if (m14lPlacementRegion is not null)
        {
            m14lPlacementRegion.AllowedIDs.Clear();
            foreach (int id in m14lPlacementPriorAllowed) m14lPlacementRegion.AllowedIDs.Add(id);
        }
    }
}
