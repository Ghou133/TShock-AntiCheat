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
    private TEHatRack? m14ObjectRack;
    private TSPlayer? m14ObjectActor, m14ObjectPeer;
    private TShockAPI.DB.Region? m14ObjectRegion;
    private int[] m14ObjectPriorAllowed = [];
    private long m14ObjectRaw, m14ObjectSends, m14ObjectOpenRaw, m14ObjectOpenEvents;
    private object? m14ObjectLastRaw, m14ObjectLastOpen;
    private bool m14ObjectObservers;

    private void M14ObjectCommand(string[] args)
    {
        Require(args.Length > 0, "Use qa_m14_object setup <actor> <peer> | state | allow | deny | finish.");
        if (args[0] == "setup")
        {
            Require(args.Length == 3 && m14ObjectRack is null, "One owned hat-rack fixture per isolated run.");
            var actor = ResolvePlayer(args[1]); var peer = ResolvePlayer(args[2]);
            Require(actor.Index != peer.Index && actor.Account?.ID != peer.Account?.ID, "Independent actor and peer required.");
            foreach (var player in new[] { actor, peer })
                Require(player.IsLoggedIn && player.Account is not null && player.HasSentInventory &&
                    !player.HasPermission("anticheat.bypass") && !player.HasPermission(Permissions.bypassssc) &&
                    !player.HasPermission(Permissions.editregion), "Ordinary authenticated SSC accounts required.");
            if (room is null) Prepare(actor);
            int x = room!.Left + 36, y = room.FloorY - 4;
            Require(!TileEntity.ByPosition.ContainsKey(new Point16(x, y)), "Existing tile entity cannot be replaced.");
            for (int dx = 0; dx < 3; dx++)
            for (int dy = 0; dy < 4; dy++) Require(!Main.tile[x + dx, y + dy].active(), "Owned hat-rack cells must be empty.");
            m14ObjectRegion = TShock.Regions.GetRegionByName(room.RegionName);
            Require(m14ObjectRegion is not null, "Existing owned room region required.");
            m14ObjectPriorAllowed = m14ObjectRegion!.AllowedIDs.ToArray();
            if (!m14ObjectRegion.AllowedIDs.Contains(actor.Account!.ID)) m14ObjectRegion.AllowedIDs.Add(actor.Account.ID);
            Require(actor.HasBuildPermissionForTileObject(x, y, 3, 4, false) &&
                !peer.HasBuildPermissionForTileObject(x, y, 3, 4, false), "A real scoped region grant must allow only the actor.");
            // The setup owns only these twelve prechecked empty cells. Tested124 is the item writer.
            for (int dx = 0; dx < 3; dx++)
            for (int dy = 0; dy < 4; dy++)
            {
                var tile = Main.tile[x + dx, y + dy]; tile.active(true); tile.type = TileID.HatRack;
                tile.frameX = (short)(dx * 18); tile.frameY = (short)(dy * 18);
            }
            int id = TileEntityType<TEHatRack>.Place(x, y);
            Require(TileEntity.TryGet<TEHatRack>(id, out m14ObjectRack) && m14ObjectRack!.IsTileValidForEntity(x, y),
                "Actual registered native hat rack and valid tiles required.");
            m14ObjectActor = actor; m14ObjectPeer = peer;
            ServerApi.Hooks.NetGetData.Register(this, ObserveM14ObjectRaw, -1001);
            HookEvents.Terraria.NetMessage.SendData += ObserveM14ObjectSend;
            GetDataHandlers.RequestTileEntityInteraction.Register(ObserveM14ObjectOpening, HandlerPriority.Lowest, true);
            m14ObjectObservers = true;
            NetMessage.SendTileSquare(-1, x, y, 3, 4);
            NetMessage.SendData(86, -1, -1, null, id);
            Record("m14-object-setup", new { id, x, y, actor = actor.Name, peer = peer.Name,
                fixtureArtificial = true, area = "twelve previously empty owned test-room cells", ordinaryRegionGrant = true });
        }
        else
        {
            Require(args.Length == 1 && m14ObjectRack is not null && m14ObjectActor is not null, "Prepare object fixture first.");
            switch (args[0])
            {
                case "allow":
                    if (!m14ObjectRegion!.AllowedIDs.Contains(m14ObjectActor!.Account.ID)) m14ObjectRegion.AllowedIDs.Add(m14ObjectActor.Account.ID);
                    break;
                case "deny": m14ObjectRegion!.AllowedIDs.Remove(m14ObjectActor!.Account.ID); break;
                case "state": break;
                case "finish": DisposeM14Object(); break;
                default: throw new InvalidOperationException("Unknown object fixture operation.");
            }
        }
        WriteM14ObjectState();
    }

    private bool M14CurrentSubject(int slot) => (m14ObjectActor is not null && slot == m14ObjectActor.Index &&
        ReferenceEquals(TShock.Players[slot], m14ObjectActor)) || (m14ObjectPeer is not null && slot == m14ObjectPeer.Index &&
        ReferenceEquals(TShock.Players[slot], m14ObjectPeer));

    private void ObserveM14ObjectRaw(GetDataEventArgs args)
    {
        int packet = (int)args.MsgID;
        if (packet is not (122 or 124) || args.Msg is null || !M14CurrentSubject(args.Msg.whoAmI)) return;
        int length = args.Length - 1;
        if (length < 0 || args.Index < 0 || args.Index > args.Msg.readBuffer.Length - length) return;
        var body = args.Msg.readBuffer.AsSpan(args.Index, length);
        if (packet == 124)
        {
            m14ObjectRaw++;
            m14ObjectLastRaw = new { sequence = m14ObjectRaw, packet, receivingSlot = args.Msg.whoAmI,
                wireSender = length >= 1 ? (int?)body[0] : null,
                entity = length >= 5 ? (int?)BinaryPrimitives.ReadInt32LittleEndian(body[1..]) : null,
                wireSlot = length >= 6 ? (int?)body[5] : null, payloadLength = length, handled = args.Handled,
                payloadHex = Convert.ToHexString(body[..Math.Min(body.Length, 16)]),
                source = "current actor or peer after product and TShock raw hooks, before native item write" };
        }
        else
        {
            m14ObjectOpenRaw++;
            m14ObjectLastOpen = new { sequence = m14ObjectOpenRaw, packet, receivingSlot = args.Msg.whoAmI,
                entity = length >= 4 ? (int?)BinaryPrimitives.ReadInt32LittleEndian(body) : null,
                handled = args.Handled, payloadLength = length, payloadHex = Convert.ToHexString(body[..Math.Min(body.Length, 8)]) };
        }
    }

    private void ObserveM14ObjectSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.ContinueExecution && args.msgType == 124 && m14ObjectRack is not null && (int)args.number2 == m14ObjectRack.ID)
            m14ObjectSends++;
    }

    private void ObserveM14ObjectOpening(object? _, GetDataHandlers.RequestTileEntityInteractionEventArgs args)
    {
        if (ReferenceEquals(args.TileEntity, m14ObjectRack) && M14CurrentSubject(args.Player.Index)) m14ObjectOpenEvents++;
    }

    private void WriteM14ObjectState()
    {
        Require(m14ObjectRack is not null && m14ObjectActor is not null && m14ObjectPeer is not null, "Prepare object fixture first.");
        var rack = m14ObjectRack!; var plugin = M5Plugin();
        object Actor(TSPlayer player) => new { slot = player.Index, player.Name, account = player.Account.ID,
            player.IsLoggedIn, player.HasSentInventory, bypass = player.HasPermission("anticheat.bypass"),
            sscBypass = player.HasPermission(Permissions.bypassssc),
            regionAllowed = player.HasBuildPermissionForTileObject(rack.Position.X, rack.Position.Y, 3, 4, false),
            sameActor = ReferenceEquals(TShock.Players[player.Index], player),
            anchor = player.TPlayer.tileEntityAnchor.interactEntityID };
        object[] Items(Item[] items) => items.Select((item, slot) => (object)new { slot, item.type, item.stack, item.prefix }).ToArray();
        var snapshot = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, id = rack.ID,
            x = rack.Position.X, y = rack.Position.Y, tileValid = rack.IsTileValidForEntity(rack.Position.X, rack.Position.Y),
            currentRegisteredObject = TileEntity.TryGet<TEHatRack>(rack.ID, out var current) && ReferenceEquals(current, rack),
            actor = Actor(m14ObjectActor!), peer = Actor(m14ObjectPeer!), items = Items(rack._items), dyes = Items(rack._dyes),
            raw = m14ObjectRaw, sends = m14ObjectSends, openRaw = m14ObjectOpenRaw, openEvents = m14ObjectOpenEvents,
            lastRaw = m14ObjectLastRaw, lastOpen = m14ObjectLastOpen,
            safetyBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin),
            guardPresent = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M14ObjectPacketSafety") is not null,
            currentAllowed = m14ObjectRegion!.AllowedIDs.ToArray(), originalAllowed = m14ObjectPriorAllowed,
            observersInstalled = m14ObjectObservers,
            note = "current native hat rack and same receiving accounts; setup is not original-client placement evidence" };
        File.WriteAllText(Path.Combine(output!, "m14-object-state-latest.json"), JsonSerializer.Serialize(snapshot, jsonOptions));
    }

    private void DisposeM14Object()
    {
        if (m14ObjectObservers)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, ObserveM14ObjectRaw);
            HookEvents.Terraria.NetMessage.SendData -= ObserveM14ObjectSend;
            GetDataHandlers.RequestTileEntityInteraction.UnRegister(ObserveM14ObjectOpening);
            m14ObjectObservers = false;
        }
        if (m14ObjectRegion is not null)
        {
            m14ObjectRegion.AllowedIDs.Clear();
            foreach (int id in m14ObjectPriorAllowed) m14ObjectRegion.AllowedIDs.Add(id);
        }
    }
}
