using System.Buffers.Binary;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.Net;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private sealed record M15LeashedTarget(TELeashedEntityAnchorWithItem Entity, Region Region, int Item);
    private readonly Dictionary<int, M15LeashedTarget> m15LeashedTargets = [];
    private int m15LeashedSelected;
    private bool m15LeashedObservers;
    private long m15LeashedRaw;
    private object? m15LeashedLastRaw;

    private void M15LeashedItemCommand(string[] args)
    {
        Require(args.Length > 0 && m14ResumePlacementActor is not null && m14ResumePlacementPeer is not null && room is not null,
            "The owned entity fixture actors and room are required.");
        var actor = m14ResumePlacementActor!; var peer = m14ResumePlacementPeer!;
        if (args[0] == "setup")
        {
            Require(m15LeashedTargets.Count == 0, "Only one leashed fixture per run.");
            foreach (int family in new[] { 9, 10 })
            {
                // TShock regions include their right/bottom borders; leave a cell between regions.
                int x = room!.Left + (family == 9 ? 26 : 28), y = room.FloorY - 1;
                Require(!Main.tile[x, y].active() && !TileEntity.ByPosition.ContainsKey(new Point16(x, y)), "Only empty owned room cells.");
                var tile = Main.tile[x, y]; tile.active(true); tile.type = (ushort)(family == 9 ? 723 : 724); tile.frameX = tile.frameY = 0;
                int id = TileEntity.Place(x, y, family);
                var anchor = (TELeashedEntityAnchorWithItem)TileEntity.ByID[id];
                Require(anchor.GetType() == (family == 9 ? typeof(TEKiteAnchor) : typeof(TECritterAnchor)) && anchor.IsTileValidForEntity(x, y),
                    "Actual registered native anchor required.");
                int itemType = family == 9 ? ItemID.KiteBlue : ItemID.Bunny;
                Require(anchor.FitsItem(itemType), "Native corresponding item required.");
                string regionName = "qa_m15_leashed_" + family + "_" + Main.worldID;
                Require(TShock.Regions.AddRegion(x, y, 1, 1, regionName, "qa-scaffold-owner", Main.worldID.ToString(), 1000001), "Own region required.");
                var region = TShock.Regions.GetRegionByName(regionName); region.AllowedIDs.Add(actor.Account!.ID);
                m15LeashedTargets.Add(family, new(anchor, region, itemType));
                NetMessage.SendTileSquare(-1, x, y, 1, 1); NetMessage.SendData(86, -1, -1, null, id);
            }
            ServerApi.Hooks.NetGetData.Register(this, ObserveM15LeashedRaw, -1001); m15LeashedObservers = true;
            m15LeashedSelected = 9;
            Record("m15-leashed-setup", new { artificial = true, scope = "empty own anchors and permissions only; item insertion must come through TCP156" });
        }
        else switch (args[0])
        {
            case "select":
                Require(args.Length == 2 && int.TryParse(args[1], out _) && m15LeashedTargets.ContainsKey(int.Parse(args[1])), "Select9 or10.");
                m15LeashedSelected = int.Parse(args[1]);
                var target = m15LeashedTargets[m15LeashedSelected];
                foreach (var player in new[] { actor, peer }) player.Teleport((target.Entity.Position.X - 1) * 16, (room!.FloorY - 3) * 16);
                break;
            case "allow":
                var allow = m15LeashedTargets[m15LeashedSelected].Region.AllowedIDs;
                if (!allow.Contains(actor.Account!.ID)) allow.Add(actor.Account.ID);
                break;
            case "deny": m15LeashedTargets[m15LeashedSelected].Region.AllowedIDs.Remove(actor.Account!.ID); break;
            case "state": break;
            case "finish": DisposeM15LeashedItem(); break;
            default: throw new InvalidOperationException("Unknown leashed fixture operation.");
        }
        WriteM15LeashedState();
    }

    private void ObserveM15LeashedRaw(GetDataEventArgs args)
    {
        if ((int)args.MsgID != 156 || args.Msg is null || !(ReferenceEquals(TShock.Players[args.Msg.whoAmI], m14ResumePlacementActor) ||
            ReferenceEquals(TShock.Players[args.Msg.whoAmI], m14ResumePlacementPeer))) return;
        int length = args.Length - 1;
        if (length < 0 || args.Index < 0 || args.Index > args.Msg.readBuffer.Length - length) return;
        var body = args.Msg.readBuffer.AsSpan(args.Index, length); m15LeashedRaw++;
        m15LeashedLastRaw = new { sequence = m15LeashedRaw, receivingSlot = args.Msg.whoAmI, length, handled = args.Handled,
            x = length == 6 ? (int?)BinaryPrimitives.ReadInt16LittleEndian(body) : null,
            y = length == 6 ? (int?)BinaryPrimitives.ReadInt16LittleEndian(body[2..]) : null,
            item = length == 6 ? (int?)BinaryPrimitives.ReadInt16LittleEndian(body[4..]) : null, hex = Convert.ToHexString(body[..Math.Min(length, 6)]) };
    }

    private void WriteM15LeashedState()
    {
        var plugin = M5Plugin();
        var snapshot = new { utc = DateTimeOffset.UtcNow, selected = m15LeashedSelected, fixtureArtificial = true,
            moduleId = NetManager.Instance.GetId<LeashedEntity.NetModule>(),
            targets = m15LeashedTargets.Select(pair =>
            {
                var t = pair.Value; var anchor = t.Entity; var leash = anchor.leashedEntity;
                return new { family = pair.Key, id = anchor.ID, x = anchor.Position.X, y = anchor.Position.Y, requestedItem = t.Item,
                    item = anchor.itemType, tileValid = anchor.IsTileValidForEntity(anchor.Position.X, anchor.Position.Y),
                    leashedId = leash is null ? (int?)null : leash.whoAmI, leashedType = leash is null ? (int?)null : leash.Type,
                    leashedActive = leash?.active ?? false,
                    registeredLeashed = leash is not null && LeashedEntity.TryGet(leash.whoAmI, out var found) && ReferenceEquals(leash, found),
                    actorAllowed = m14ResumePlacementActor!.HasBuildPermission(anchor.Position.X, anchor.Position.Y, false),
                    peerAllowed = m14ResumePlacementPeer!.HasBuildPermission(anchor.Position.X, anchor.Position.Y, false),
                    regionPresent = TShock.Regions.GetRegionByName(t.Region.Name) is not null };
            }).ToArray(), raw = m15LeashedRaw, lastRaw = m15LeashedLastRaw,
            safetyBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin), observersInstalled = m15LeashedObservers };
        File.WriteAllText(Path.Combine(output!, "m15-leashed-state-latest.json"), JsonSerializer.Serialize(snapshot, jsonOptions));
    }

    private void DisposeM15LeashedItem()
    {
        if (m15LeashedObservers) { ServerApi.Hooks.NetGetData.Deregister(this, ObserveM15LeashedRaw); m15LeashedObservers = false; }
        foreach (var target in m15LeashedTargets.Values)
        {
            if (TShock.Regions.GetRegionByName(target.Region.Name) is not null) TShock.Regions.DeleteRegion(target.Region.Name);
        }
    }
}
