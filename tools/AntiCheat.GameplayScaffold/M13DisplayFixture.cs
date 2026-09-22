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
    private TEDisplayDoll? m13Display;
    private TSPlayer? m13DisplayActor;
    private TShockAPI.DB.Region? m13DisplayRegion;
    private int[] m13DisplayPriorAllowed = [];
    private long m13DisplayRaw, m13DisplaySends, m13DisplayEvents;
    private object? m13DisplayLastRaw;
    private bool m13DisplayObservers;

    private void M13DisplayCommand(string[] args)
    {
        Require(args.Length > 0, "Use qa_m13_display setup <actor> | state | allow | deny | finish.");
        if (args[0] == "setup")
        {
            Require(args.Length == 2 && m13Display is null, "One owned mannequin fixture per isolated run.");
            var actor = ResolvePlayer(args[1]);
            Require(actor.IsLoggedIn && actor.Account is not null && actor.HasSentInventory &&
                !actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc) &&
                !actor.HasPermission(Permissions.editregion), "Ordinary authenticated SSC account required.");
            if (room is null) Prepare(actor);
            int x = room!.Left + 33, y = room.FloorY - 3;
            Require(!TileEntity.ByPosition.ContainsKey(new Point16(x, y)), "Existing tile entity cannot be replaced.");
            for (int dx = 0; dx < 2; dx++)
            for (int dy = 0; dy < 3; dy++) Require(!Main.tile[x + dx, y + dy].active(), "Owned mannequin cells must be empty.");
            m13DisplayRegion = TShock.Regions.GetRegionByName(room.RegionName);
            Require(m13DisplayRegion is not null, "Existing owned room region required.");
            m13DisplayPriorAllowed = m13DisplayRegion!.AllowedIDs.ToArray();
            m13DisplayRegion.AllowedIDs.Add(actor.Account!.ID);
            Require(actor.HasBuildPermission(x, y, false), "Normal region authorization must actually allow the setup location.");
            // Only these six prechecked empty cells are fixture setup; runtime packet121 remains the writer under test.
            for (int dx = 0; dx < 2; dx++)
            for (int dy = 0; dy < 3; dy++)
            {
                var tile = Main.tile[x + dx, y + dy]; tile.active(true); tile.type = TileID.DisplayDoll;
                tile.frameX = (short)(dx * 18); tile.frameY = (short)(dy * 18);
            }
            int id = TileEntityType<TEDisplayDoll>.Place(x, y);
            Require(TileEntity.TryGet<TEDisplayDoll>(id, out m13Display) && m13Display!.IsTileValidForEntity(x, y),
                "Actual registered native mannequin and valid tiles required.");
            m13DisplayActor = actor;
            ServerApi.Hooks.NetGetData.Register(this, ObserveM13DisplayRaw, -1001);
            HookEvents.Terraria.NetMessage.SendData += ObserveM13DisplaySend;
            GetDataHandlers.DisplayDollItemSync.Register(ObserveM13DisplayItem, HandlerPriority.Lowest, true);
            m13DisplayObservers = true;
            NetMessage.SendTileSquare(-1, x, y, 3);
            NetMessage.SendData(86, -1, -1, null, id);
            Record("m13-display-setup", new { id, x, y, actor = actor.Name, actor.Account.ID,
                fixtureArtificial = true, area = "six previously empty owned test-room cells", ordinaryRegionGrant = true });
        }
        else
        {
            Require(args.Length == 1 && m13Display is not null && m13DisplayActor is not null, "Prepare display fixture first.");
            switch (args[0])
            {
                case "allow":
                    if (!m13DisplayRegion!.AllowedIDs.Contains(m13DisplayActor!.Account.ID)) m13DisplayRegion.AllowedIDs.Add(m13DisplayActor.Account.ID);
                    break;
                case "deny": m13DisplayRegion!.AllowedIDs.Remove(m13DisplayActor!.Account.ID); break;
                case "state": break;
                case "finish": DisposeM13Display(); break;
                default: throw new InvalidOperationException("Unknown display fixture operation.");
            }
        }
        WriteM13DisplayState();
    }

    private void ObserveM13DisplayRaw(GetDataEventArgs args)
    {
        if ((int)args.MsgID != 121 || args.Msg is null || m13DisplayActor is null || args.Msg.whoAmI != m13DisplayActor.Index ||
            !ReferenceEquals(TShock.Players[m13DisplayActor.Index], m13DisplayActor)) return;
        int length = args.Length - 1;
        if (length < 7 || args.Index < 0 || args.Index > args.Msg.readBuffer.Length - length) return;
        var body = args.Msg.readBuffer.AsSpan(args.Index, length);
        if (BinaryPrimitives.ReadInt32LittleEndian(body[1..]) != m13Display!.ID) return;
        m13DisplayRaw++;
        m13DisplayLastRaw = new { sequence = m13DisplayRaw, packet = 121, slot = body[5], command = body[6],
            payloadLength = length, handled = args.Handled, payloadHex = Convert.ToHexString(body[..Math.Min(body.Length, 16)]),
            source = "same-current-account after product and TShock raw hooks, before native write" };
    }

    private void ObserveM13DisplaySend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.ContinueExecution && args.msgType == 121 && m13Display is not null && (int)args.number2 == m13Display.ID)
            m13DisplaySends++;
    }

    private void ObserveM13DisplayItem(object? _, GetDataHandlers.DisplayDollItemSyncEventArgs args)
    { if (ReferenceEquals(args.DisplayDollEntity, m13Display) && ReferenceEquals(args.Player, m13DisplayActor)) m13DisplayEvents++; }

    private void WriteM13DisplayState()
    {
        Require(m13Display is not null && m13DisplayActor is not null, "Prepare display fixture first.");
        var actor = m13DisplayActor!; var doll = m13Display!; var plugin = M5Plugin();
        using var poseStream = new MemoryStream(); using var writer = new BinaryWriter(poseStream); doll.WriteData(0, 2, writer);
        object[] Items(Item[] collection) => collection.Select((item, slot) => (object)new { slot, item.type, item.stack, item.prefix }).ToArray();
        var snapshot = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, id = doll.ID,
            x = doll.Position.X, y = doll.Position.Y, tileValid = doll.IsTileValidForEntity(doll.Position.X, doll.Position.Y),
            currentRegisteredObject = TileEntity.TryGet<TEDisplayDoll>(doll.ID, out var current) && ReferenceEquals(current, doll),
            actor = new { slot = actor.Index, actor.Name, account = actor.Account.ID, actor.IsLoggedIn, actor.HasSentInventory,
                bypass = actor.HasPermission("anticheat.bypass"), sscBypass = actor.HasPermission(Permissions.bypassssc),
                regionAllowed = actor.HasBuildPermission(doll.Position.X, doll.Position.Y, false),
                sameActor = ReferenceEquals(TShock.Players[actor.Index], actor) },
            equip = Items(doll._equip), dyes = Items(doll._dyes), misc = Items(doll._misc), pose = poseStream.ToArray()[0],
            raw = m13DisplayRaw, sends = m13DisplaySends, itemEvents = m13DisplayEvents, lastRaw = m13DisplayLastRaw,
            safetyBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin),
            guardPresent = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M13DisplayEntityPacketSafety") is not null,
            note = "bounded live native object, raw and send snapshots; safety rejection is not an account proof" };
        File.WriteAllText(Path.Combine(output!, "m13-display-state-latest.json"), JsonSerializer.Serialize(snapshot, jsonOptions));
    }

    private void DisposeM13Display()
    {
        if (m13DisplayObservers)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, ObserveM13DisplayRaw);
            HookEvents.Terraria.NetMessage.SendData -= ObserveM13DisplaySend;
            GetDataHandlers.DisplayDollItemSync.UnRegister(ObserveM13DisplayItem);
            m13DisplayObservers = false;
        }
        if (m13DisplayRegion is not null)
        {
            m13DisplayRegion.AllowedIDs.Clear();
            foreach (int id in m13DisplayPriorAllowed) m13DisplayRegion.AllowedIDs.Add(id);
        }
    }
}
