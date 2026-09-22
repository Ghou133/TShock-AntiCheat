using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.ObjectData;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>Synchronous, exact-event snapshots; no world writes and no cached permission decisions.</summary>
public static class M3WorldContexts
{
    public const int MaximumFootprintTiles = 256;

    public static BusinessRuleResult Evaluate(M3WorldPacket packet, SessionKey session, TSPlayer actor,
        string fingerprint, bool alreadyCancelled)
    {
        // Target MessageBuffer treats -1 as release; its client player field is overwritten with whoAmI.
        if (packet.Kind == WorldActionKind.DisplayEntityInteraction && packet.TargetId == -1)
            return new("WORLD-PASS", WorldRules.Version, alreadyCancelled ? ControlAction.Block : ControlAction.Pass,
                alreadyCancelled ? Verdict.Unknown : Verdict.Pass, "display-anchor-release", false, false,
                ImmutableDictionary<string, string>.Empty);
        return WorldRules.Evaluate(Capture(packet, session, actor, fingerprint, alreadyCancelled));
    }

    public static WorldActionObservation Capture(M3WorldPacket packet, SessionKey session, TSPlayer actor,
        string fingerprint, bool alreadyCancelled)
    {
        int x = packet.X, y = packet.Y;
        TileEntity? entity = null;
        if (packet.Kind == WorldActionKind.DisplayEntityInteraction)
        {
            TileEntity.ByID.TryGetValue(packet.TargetId, out entity);
            if (entity is not null) { x = entity.Position.X; y = entity.Position.Y; }
        }
        var observation = new WorldActionObservation(session, packet.Kind, x, y)
        {
            CurrentSession = session, EventId = Guid.NewGuid(), RuntimeFingerprint = fingerprint,
            ParseComplete = true, ClientOrigin = true, BeforeSideEffects = true, AlreadyCancelled = alreadyCancelled,
            TargetId = packet.TargetId, EditData = packet.Type,
            Geometry = new(session.WorldEpoch, fingerprint, "runtime326-exact-world-event", Main.maxTilesX,
                Main.maxTilesY, TileID.Count, WallID.Count, Main.sign?.Length ?? 0, Main.netMode == 2)
        };
        if (alreadyCancelled || x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
            return observation;

        bool? binding = null, allowed = null, inRange = null;
        bool complete = false, footprint = false;
        if (packet.Kind == WorldActionKind.SignWrite)
        {
            var sign = packet.TargetId >= 0 && packet.TargetId < (Main.sign?.Length ?? 0) ? Main.sign![packet.TargetId] : null;
            binding = sign is null ? null : sign.x == x && sign.y == y;
            allowed = actor.HasBuildPermission(x, y, false);
            inRange = actor.IsInRange(x, y);
            complete = footprint = true;
        }
        else if (packet.Kind == WorldActionKind.ObjectPlacement && packet.Type >= 0 && packet.Type < TileID.Count && packet.Style >= 0)
        {
            var data = TileObjectData.GetTileData(packet.Type, packet.Style, packet.Alternate);
            if (data is not null && ValidFootprint(x - data.Origin.X, y - data.Origin.Y, data.Width, data.Height))
            {
                int left = x - data.Origin.X, top = y - data.Origin.Y;
                bool allAllowed = actor.HasBuildPermissionForTileObject(left, top, data.Width, data.Height, false);
                // Bouncer also permits specific modified-ice operations. A denied ordinary permission is
                // not enough to reject without reproducing that stateful exception; leave it with core.
                allowed = allAllowed ? true : null;
                inRange = actor.IsInRange(left, top);
                complete = footprint = true;
            }
        }
        else if (packet.Kind == WorldActionKind.DisplayEntityInteraction && entity is not null)
        {
            binding = entity.ID == packet.TargetId && entity.Position.X == x && entity.Position.Y == y;
            // Match the core's typed display-object footprint, without guessing a full footprint for
            // arbitrary tile entities. Core imposes no additional distance policy on this request.
            int width = entity is TEHatRack ? TEHatRack.entityTileWidth : entity is TEDisplayDoll ? TEDisplayDoll.entityTileWidth : 0;
            int height = entity is TEHatRack ? TEHatRack.entityTileHeight : entity is TEDisplayDoll ? TEDisplayDoll.entityTileHeight : 0;
            if (ValidFootprint(x, y, width, height))
            {
                allowed = actor.HasBuildPermissionForTileObject(x, y, width, height, false);
                inRange = true;
                complete = footprint = true;
            }
        }
        return observation with
        {
            TargetBindingValid = binding,
            Authorization = new(session, observation.EventId, packet.Kind, x, y, packet.TargetId, 0,
                complete, allowed, inRange, footprint)
        };
    }

    private static bool ValidFootprint(int left, int top, int width, int height) => left >= 0 && top >= 0
        && width > 0 && height > 0 && (long)width * height <= MaximumFootprintTiles
        && left <= Main.maxTilesX - width && top <= Main.maxTilesY - height;
}
