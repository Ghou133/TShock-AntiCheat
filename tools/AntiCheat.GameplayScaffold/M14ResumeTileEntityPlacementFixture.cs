using System.Buffers.Binary;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private sealed record M14ResumePlacementTarget(byte Type, int X, int Y, int Width, int Height, Region Region);
    private readonly Dictionary<int, M14ResumePlacementTarget> m14ResumePlacementTargets = [];
    private TSPlayer? m14ResumePlacementActor, m14ResumePlacementPeer;
    private int m14ResumePlacementSelected;
    private bool m14ResumePlacementObservers;
    private long m14ResumePlacementRaw, m14ResumePlacementSends;
    private object? m14ResumePlacementLastRaw;

    private void M14ResumeTileEntityPlacementCommand(string[] args)
    {
        if (args.Length > 0 && args[0] == "leashed") { M15LeashedItemCommand(args[1..]); return; }
        Require(args.Length > 0, "Use qa_m14r_entity setup <actor> <peer> | select <1|3|4|5> | corner <dx> <dy> | allow | deny | state | finish.");
        if (args[0] == "setup")
        {
            Require(args.Length == 3 && m14ResumePlacementTargets.Count == 0, "One owned fixture per run.");
            var actor = ResolvePlayer(args[1]); var peer = ResolvePlayer(args[2]);
            Require(actor.Index != peer.Index && actor.Account?.ID != peer.Account?.ID, "Independent actors required.");
            foreach (var player in new[] { actor, peer })
                Require(player.IsLoggedIn && player.Account is not null && player.HasSentInventory &&
                    !player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
                    !player.HasPermission(Permissions.editregion), "Ordinary authenticated SSC actors required.");
            if (room is null) Prepare(actor);
            m14ResumePlacementActor = actor; m14ResumePlacementPeer = peer;
            foreach (int family in new[] { 1, 3, 4, 5 })
            {
                int width = family is 1 or 3 ? 2 : 3, height = family == 1 ? 2 : family == 5 ? 4 : 3;
                int x = room!.Left + (family == 1 ? 22 : family == 3 ? 3 : family == 4 ? 7 : 16), y = room.FloorY - height;
                Require(x > room.Left && x + width < room.Left + room.Width && y > room.Top,
                    "Only the owned prepared room is used.");
                Require(!TileEntity.ByPosition.ContainsKey(new Point16(x, y)), "Existing entity cannot be replaced.");
                for (int dx = 0; dx < width; dx++) for (int dy = 0; dy < height; dy++)
                    Require(!Main.tile[x + dx, y + dy].active(), "Only empty owned room cells may be used.");
                string name = "qa_m14r_entity_" + family + "_" + Main.worldID;
                Require(TShock.Regions.AddRegion(x + width - 1, y + height - 1, 1, 1, name, "qa-scaffold-owner",
                    Main.worldID.ToString(), 1000001), "Owned one-cell region creation required.");
                var region = TShock.Regions.GetRegionByName(name); region.AllowedIDs.Add(actor.Account!.ID);
                ushort tileType = family switch { 1 => TileID.ItemFrame, 3 => TileID.DisplayDoll, 4 => TileID.WeaponsRack2, _ => TileID.HatRack };
                for (int dx = 0; dx < width; dx++) for (int dy = 0; dy < height; dy++)
                {
                    var tile = Main.tile[x + dx, y + dy]; tile.active(true); tile.type = tileType;
                    tile.frameX = (short)(dx * 18); tile.frameY = (short)(dy * 18); tile.wall = WallID.Wood;
                }
                byte type = family switch { 1 => TileEntityType<TEItemFrame>.EntityTypeID, 3 => TileEntityType<TEDisplayDoll>.EntityTypeID,
                    4 => TileEntityType<TEWeaponsRack>.EntityTypeID, _ => TileEntityType<TEHatRack>.EntityTypeID };
                Require(type == family && TileEntity.manager.CheckValidTile(type, x, y), "Actual target registry and valid native anchor required.");
                m14ResumePlacementTargets.Add(type, new(type, x, y, width, height, region));
                NetMessage.SendTileSquare(-1, x, y, width, height);
            }
            ServerApi.Hooks.NetGetData.Register(this, ObserveM14ResumePlacementRaw, -1001);
            HookEvents.Terraria.NetMessage.SendData += ObserveM14ResumePlacementSend;
            m14ResumePlacementObservers = true; m14ResumePlacementSelected = 3;
            Record("m14r-entity-setup", new { fixtureArtificial = true, actor = actor.Name, peer = peer.Name,
                scope = "empty room cells and corner permissions; entity creation must come from actual TCP87" });
        }
        else
        {
            Require(m14ResumePlacementActor is not null, "Prepare owned fixture first.");
            switch (args[0])
            {
                case "select":
                    Require(args.Length == 2 && int.TryParse(args[1], out _) && m14ResumePlacementTargets.ContainsKey(int.Parse(args[1])), "Select1,3,4,5.");
                    m14ResumePlacementSelected = int.Parse(args[1]); var target = m14ResumePlacementTargets[m14ResumePlacementSelected];
                    if (TileEntity.TryGetAt<TileEntity>(target.X, target.Y, out var existing))
                    {
                        Require(existing.type == target.Type, "Do not remove an unrelated replacement.");
                        bool empty = existing switch { TEItemFrame frame => frame.item.IsAir,
                            TEDisplayDoll doll => doll._equip.Concat(doll._dyes).Concat(doll._misc).All(i => i.IsAir),
                            TEWeaponsRack rack => rack.item.IsAir, TEHatRack hat => hat._items.Concat(hat._dyes).All(i => i.IsAir), _ => false };
                        Require(empty, "Only this fixture's empty entity may be reset.");
                        TileEntity.Remove(existing);
                    }
                    foreach (var player in new[] { m14ResumePlacementActor!, m14ResumePlacementPeer! })
                        player.Teleport((target.X - 1) * 16, (room!.FloorY - 3) * 16);
                    Record("m14r-entity-select-reset", new { target.Type, target.X, target.Y, source = "explicit state setup, no creation result claimed" });
                    break;
                case "corner":
                    Require(args.Length == 3 && int.TryParse(args[1], out _) && int.TryParse(args[2], out _), "A fixture cell is required.");
                    var corner = m14ResumePlacementTargets[m14ResumePlacementSelected];
                    int cellX = int.Parse(args[1]), cellY = int.Parse(args[2]);
                    Require(cellX >= 0 && cellX < corner.Width && cellY >= 0 && cellY < corner.Height && (cellX != 0 || cellY != 0),
                        "The selected object's non-anchor cell is required.");
                    corner.Region.Area = new Microsoft.Xna.Framework.Rectangle(corner.X + cellX, corner.Y + cellY, 1, 1);
                    break;
                case "allow":
                    var allow = m14ResumePlacementTargets[m14ResumePlacementSelected].Region.AllowedIDs;
                    if (!allow.Contains(m14ResumePlacementActor!.Account.ID)) allow.Add(m14ResumePlacementActor.Account.ID);
                    break;
                case "deny": m14ResumePlacementTargets[m14ResumePlacementSelected].Region.AllowedIDs.Remove(m14ResumePlacementActor!.Account.ID); break;
                case "state": break;
                case "finish": DisposeM14ResumeTileEntityPlacement(); break;
                default: throw new InvalidOperationException("Unknown entity fixture operation.");
            }
        }
        WriteM14ResumePlacementState();
    }

    private void ObserveM14ResumePlacementRaw(GetDataEventArgs args)
    {
        if ((int)args.MsgID != 87 || args.Msg is null || !(ReferenceEquals(TShock.Players[args.Msg.whoAmI], m14ResumePlacementActor) ||
            ReferenceEquals(TShock.Players[args.Msg.whoAmI], m14ResumePlacementPeer))) return;
        int length = args.Length - 1;
        if (length < 0 || args.Index < 0 || args.Index > args.Msg.readBuffer.Length - length) return;
        var body = args.Msg.readBuffer.AsSpan(args.Index, length); m14ResumePlacementRaw++;
        m14ResumePlacementLastRaw = new { sequence = m14ResumePlacementRaw, receivingSlot = args.Msg.whoAmI, length, handled = args.Handled,
            x = length == 5 ? (int?)BinaryPrimitives.ReadInt16LittleEndian(body) : null,
            y = length == 5 ? (int?)BinaryPrimitives.ReadInt16LittleEndian(body[2..]) : null,
            type = length == 5 ? (int?)body[4] : null, hex = Convert.ToHexString(body[..Math.Min(length, 8)]) };
    }

    private void ObserveM14ResumePlacementSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.ContinueExecution && args.msgType == 86 && TileEntity.ByID.TryGetValue(args.number, out var entity) &&
            m14ResumePlacementTargets.Values.Any(t => t.X == entity.Position.X && t.Y == entity.Position.Y)) m14ResumePlacementSends++;
    }

    private void WriteM14ResumePlacementState()
    {
        var actor = m14ResumePlacementActor!; var peer = m14ResumePlacementPeer!; var plugin = M5Plugin();
        var snapshot = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, selected = m14ResumePlacementSelected,
            objects = m14ResumePlacementTargets.Values.Select(target =>
            {
                bool exists = TileEntity.TryGetAt<TileEntity>(target.X, target.Y, out var entity);
                return new { type = target.Type, x = target.X, y = target.Y, width = target.Width, height = target.Height,
                    registered = exists, id = exists ? entity.ID : (int?)null,
                    tileValid = TileEntity.manager.CheckValidTile(target.Type, target.X, target.Y),
                    anchorAllowed = actor.HasBuildPermission(target.X, target.Y, false),
                    actorAllowed = actor.HasBuildPermissionForTileObject(target.X, target.Y, target.Width, target.Height, false),
                    peerAllowed = peer.HasBuildPermissionForTileObject(target.X, target.Y, target.Width, target.Height, false),
                    regionPresent = TShock.Regions.GetRegionByName(target.Region.Name) is not null };
            }).ToArray(), nextEntityId = TileEntity.TileEntitiesNextID,
            raw = m14ResumePlacementRaw, sends = m14ResumePlacementSends, lastRaw = m14ResumePlacementLastRaw,
            safetyBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin),
            guardPresent = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M14ResumeTileEntityPlacementSafety") is not null,
            observersInstalled = m14ResumePlacementObservers,
            worldItems = Main.item.Select((item, index) => new { item, index }).Where(entry => entry.item.active)
                .Select(entry => new { entry.index, entry.item.type, entry.item.stack, entry.item.prefix }).ToArray() };
        File.WriteAllText(Path.Combine(output!, "m14r-entity-state-latest.json"), JsonSerializer.Serialize(snapshot, jsonOptions));
    }

    private void DisposeM14ResumeTileEntityPlacement()
    {
        DisposeM15LeashedItem();
        if (m14ResumePlacementObservers)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, ObserveM14ResumePlacementRaw);
            HookEvents.Terraria.NetMessage.SendData -= ObserveM14ResumePlacementSend; m14ResumePlacementObservers = false;
        }
        foreach (var target in m14ResumePlacementTargets.Values)
            if (TShock.Regions.GetRegionByName(target.Region.Name) is not null) TShock.Regions.DeleteRegion(target.Region.Name);
    }
}
