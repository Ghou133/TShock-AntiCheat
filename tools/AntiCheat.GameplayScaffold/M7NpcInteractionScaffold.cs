using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace CompatibilityAudit;

// Isolated GUI fixture only; never shipped with AntiCheat. This is preparation for
// native shop/reforge UI, not ordinary NPC movement or transaction acceptance.
// Target audit: artifacts/m3-combat-audit/Terraria.NPC.decompiled.cs:7792 (AI hook),
// :58406 (town idle; walk timer is server-only), :95721 (normal gravity/collision).
// NPC source SHA256 6402DCF654E68ADAD88FC294F0B70329554D1ED9962EF68AC5F88E2BD19F795F.
// Terraria.NetMessage.decompiled.cs:2244 writes native 23 generation/position/velocity/ai;
// source SHA256 8F07FAF23D4FD93FF571C1909B02871DCA1122262AB0CF08C46F0C53B66744D6.
// GUI observation is still required to verify client interpolation and UI interaction.
public sealed partial class GameplayScaffold
{
    private const long M7NpcStationLifetimeMs = 20 * 60 * 1000;
    private const long M7NpcOwnershipLifetimeMs = 2 * 60 * 60 * 1000;
    private sealed record M7OwnedNpc(NPC Entity, int Index, byte Generation, int Type,
        int WorldId, string WorldPath, long ExpiresAt);
    private sealed record M7StationNpc(M7OwnedNpc Owned, Vector2 Anchor, float[] PreviousAi,
        Vector2 PreviousPosition, int PreviousDirection, int PreviousSpriteDirection, int Facing, int InitialLife);
    private readonly M7OwnedNpc?[] m7OwnedNpcs = new M7OwnedNpc?[2];
    private M7StationNpc[] m7NpcStation = [];
    private TSPlayer? m7NpcStationActor;
    private Player? m7NpcStationPlayer;
    private int m7NpcStationAccountId;
    private int m7NpcStationThread;
    private long m7NpcStationDeadline, m7NpcStationNextSync, m7NpcStationNextRecord;
    private long m7NpcStationSessionEpoch, m7NpcStationStartedEpoch;
    private long m7NpcStationAiHolds, m7NpcStationSyncs;
    private string? m7NpcStationInvalidated;
    private bool m7NpcStationHooksInstalled, m7NpcStationAiInstalled;

    private void InstallM7NpcInteraction()
    {
        if (m7NpcStationHooksInstalled) return;
        PlayerHooks.PlayerPostLogin += OnM7StationLogin;
        PlayerHooks.PlayerLogout += OnM7StationLogout;
        ServerApi.Hooks.ServerLeave.Register(this, OnM7StationLeave);
        m7NpcStationHooksInstalled = true;
    }

    private void DisposeM7NpcInteraction()
    {
        StopM7NpcStation("scaffold-dispose");
        if (m7NpcStationHooksInstalled)
        {
            PlayerHooks.PlayerPostLogin -= OnM7StationLogin;
            PlayerHooks.PlayerLogout -= OnM7StationLogout;
            ServerApi.Hooks.ServerLeave.Deregister(this, OnM7StationLeave);
            m7NpcStationHooksInstalled = false;
        }
        Array.Clear(m7OwnedNpcs);
    }

    // Call only in qa_npcs' successful NPC.NewNPC/created=true branch. An existing
    // natural NPC must never be adopted merely because its type or location fits.
    private void TrackM7NpcFixture(NPC npc)
    {
        int entry = npc.type == NPCID.Merchant ? 0 : npc.type == NPCID.GoblinTinkerer ? 1 : -1;
        Require(entry >= 0 && room is { State: "prepared" } && root is not null &&
            recording && Main.netMode == 2 && npc.active && (uint)npc.whoAmI < Main.npc.Length &&
            ReferenceEquals(Main.npc[npc.whoAmI], npc), "Only newly created owned merchant/goblin fixtures can be recorded.");
        var previous = m7OwnedNpcs[entry];
        Require(previous is null || !M7NpcMatches(previous) || ReferenceEquals(previous.Entity, npc) &&
            previous.Generation == npc.generation, "A different live owned NPC is already registered for this fixture type.");
        m7OwnedNpcs[entry] = new(npc, npc.whoAmI, npc.generation, npc.type, Main.worldID,
            Main.worldPathName, Environment.TickCount64 + M7NpcOwnershipLifetimeMs);
        Record("m7-npc-fixture-owned", new { index = npc.whoAmI, npc.generation, npc.type,
            worldId = Main.worldID, created = true, source = "qa_npcs-native-NewNPC-created-branch",
            ownershipTtlMinutes = 120 });
    }

    private void PrepareM7NpcStation(string[] arguments)
    {
        Require(arguments.Length == 1, "Use qa_m7_npc_station <exact authenticated player> or qa_m7_npc_station off.");
        if (arguments[0] == "off") { StopM7NpcStation("console-off"); return; }
        Require(m7NpcStationHooksInstalled, "NPC fixture lifecycle hooks are not installed.");
        Require(m7NpcStation.Length == 0, "A station is already active; use qa_m7_npc_station off before repositioning.");
        Require(room is { State: "prepared" } && recording && root is not null && Main.netMode == 2,
            "A validated isolated room with active evidence recording is required.");
        var actor = ResolvePlayer(arguments[0]);
        Require(!actor.HasPermission(Permissions.editregion) && !actor.HasPermission(Permissions.bypassssc) &&
            !actor.HasPermission("anticheat.bypass"), "Ordinary authenticated account without region, SSC or AntiCheat bypass required.");
        Require(actor.TileX > room!.Left && actor.TileX < room.Left + room.Width - 1 &&
            Math.Abs(actor.TileY - room.FloorY) <= 5, "Stand inside the owned room near its floor.");
        Require(m7OwnedNpcs.All(n => n is not null && M7NpcMatches(n) && Environment.TickCount64 < n.ExpiresAt),
            "Run qa_npcs in this scaffold instance first; both existing NPCs must have current created-fixture records.");
        var planned = m7OwnedNpcs.Select((owned, i) =>
        {
            var npc = owned!.Entity;
            Require(npc.aiStyle == 7 && npc.ai.Length == 4 && !npc.noGravity && !npc.noTileCollide && npc.life > 0,
                "Expected live native town-NPC AI, gravity and collision are required.");
            var anchor = new Vector2((room.Left + (i == 0 ? 15 : 21)) * 16 + 8 - npc.width / 2f,
                room.FloorY * 16 - npc.height);
            Require(M7StationFootprintClear(anchor, npc.width, npc.height),
                "A fixed station footprint is occupied or lacks the original flat floor; no tiles or assets were changed.");
            var hitbox = new Rectangle((int)anchor.X, (int)anchor.Y, npc.width, npc.height);
            Require(!Main.player.Any(p => p is { active: true } && p.Hitbox.Intersects(hitbox)),
                "A player occupies a station point; step between the two points before retrying.");
            return new M7StationNpc(owned!, anchor, (float[])npc.ai.Clone(), npc.position, npc.direction,
                npc.spriteDirection, i == 0 ? 1 : -1, npc.life);
        }).ToArray();
        // Both targets were validated before moving either; no NPC is created here.
        m7NpcStation = planned; m7NpcStationActor = actor; m7NpcStationPlayer = actor.TPlayer;
        m7NpcStationAccountId = actor.Account.ID; m7NpcStationThread = Environment.CurrentManagedThreadId;
        m7NpcStationStartedEpoch = Interlocked.Read(ref m7NpcStationSessionEpoch);
        Volatile.Write(ref m7NpcStationInvalidated, null);
        m7NpcStationDeadline = Environment.TickCount64 + M7NpcStationLifetimeMs;
        m7NpcStationNextSync = m7NpcStationNextRecord = 0;
        m7NpcStationAiHolds = m7NpcStationSyncs = 0;
        HookEvents.Terraria.NPC.AI += HoldM7StationAi;
        m7NpcStationAiInstalled = true;
        try
        {
            Record("m7-npc-station-started", new { fixtureArtificial = true, actor = actor.Index,
                accountId = actor.Account.ID, sessionEpoch = m7NpcStationStartedEpoch, ttlMinutes = 20,
                targets = planned.Select(p => new { p.Owned.Index, p.Owned.Generation, p.Owned.Type,
                    previousPosition = p.PreviousPosition, anchor = p.Anchor, previousAi = p.PreviousAi }),
                note = "Only two registered fixture AI calls are canceled. Native gravity/collision, health, dialogue, shop, trade and reforge remain native. This is not NPC movement acceptance." });
            TickM7NpcStation();
            Require(m7NpcStation.Length == 2, "Station prerequisites changed during setup; the hold was released.");
        }
        catch
        {
            StopM7NpcStation("station-setup-failed");
            throw;
        }
        TSPlayer.Server.SendInfoMessage($"NPC station active for {actor.Name}: Merchant tileX={room.Left + 15}, Goblin tileX={room.Left + 21}; native UI required; qa_m7_npc_station off releases it.");
    }

    private void OnM7StationLogin(PlayerPostLoginEventArgs args) => InvalidateM7StationSession(args.Player, "new-login");
    private void OnM7StationLogout(PlayerLogoutEventArgs args) => InvalidateM7StationSession(args.Player, "logout");
    private void OnM7StationLeave(LeaveEventArgs args)
    {
        if (m7NpcStationActor is { } actor && args.Who == actor.Index)
        {
            Interlocked.Increment(ref m7NpcStationSessionEpoch);
            Volatile.Write(ref m7NpcStationInvalidated, "server-leave");
        }
    }
    private void InvalidateM7StationSession(TSPlayer player, string reason)
    {
        if (m7NpcStationActor is { } actor && (ReferenceEquals(player, actor) || player.Index == actor.Index))
        {
            Interlocked.Increment(ref m7NpcStationSessionEpoch);
            Volatile.Write(ref m7NpcStationInvalidated, reason);
        }
    }

    // Called by the existing main-thread OnUpdate and idle callback. No timers or Tasks.
    private void TickM7NpcStation()
    {
        if (m7NpcStation.Length == 0) return;
        var failure = M7StationFailure();
        if (failure is not null) { StopM7NpcStation(failure); return; }
        long now = Environment.TickCount64;
        if (now < m7NpcStationNextSync) return;
        var actor = m7NpcStationActor!;
        if (actor.HasPermission(Permissions.editregion) || actor.HasPermission(Permissions.bypassssc) ||
            actor.HasPermission("anticheat.bypass")) { StopM7NpcStation("permissions-changed"); return; }
        foreach (var target in m7NpcStation)
        {
            if (!M7StationFootprintClear(target.Anchor, target.Owned.Entity.width, target.Owned.Entity.height))
            { StopM7NpcStation("station-world-footprint-changed"); return; }
        }
        m7NpcStationNextSync = now + 1000;
        foreach (var target in m7NpcStation)
        {
            HoldM7NpcPosition(target);
            // Actual packet23 contains generation, position, velocity and ai[0..3].
            // Town idle's walk timer is server-only; periodic native sync corrects
            // client interpolation without spoofing chat/shop/client input.
            TSPlayer.All.SendData(PacketTypes.NpcUpdate, number: target.Owned.Index);
            m7NpcStationSyncs++;
        }
        if (now < m7NpcStationNextRecord) return;
        m7NpcStationNextRecord = now + 5000;
        Observe("m7-npc-station-held", new { fixtureArtificial = true, actor = actor.Index,
            accountId = m7NpcStationAccountId, sessionEpoch = m7NpcStationStartedEpoch,
            aiHolds = m7NpcStationAiHolds, native23Syncs = m7NpcStationSyncs,
            targets = m7NpcStation.Select(t => new { t.Owned.Index, t.Owned.Generation,
                t.Owned.Type, t.Owned.Entity.position, t.Owned.Entity.velocity, t.Owned.Entity.ai,
                t.Owned.Entity.life, anchor = t.Anchor }) });
    }

    private void HoldM7StationAi(NPC npc, HookEvents.Terraria.NPC.AIEventArgs args)
    {
        if (!args.ContinueExecution) return; // Never revive a previously canceled hook.
        var target = m7NpcStation.FirstOrDefault(t => ReferenceEquals(t.Owned.Entity, npc) &&
            t.Owned.Index == npc.whoAmI && t.Owned.Generation == npc.generation && t.Owned.Type == npc.type);
        if (target is null) return;
        var failure = M7StationFailure();
        if (failure is not null) { StopM7NpcStation(failure); return; }
        HoldM7NpcPosition(target);
        args.ContinueExecution = false;
        if (m7NpcStationAiHolds < long.MaxValue) m7NpcStationAiHolds++;
    }

    private static void HoldM7NpcPosition(M7StationNpc target)
    {
        var npc = target.Owned.Entity;
        npc.position = target.Anchor; npc.velocity = Vector2.Zero;
        npc.ai[0] = 0f; npc.ai[1] = 300f; npc.ai[2] = npc.ai[3] = 0f;
        npc.direction = npc.spriteDirection = target.Facing;
    }

    private string? M7StationFailure()
    {
        if (Environment.CurrentManagedThreadId != m7NpcStationThread) return "execution-thread-changed";
        if (Volatile.Read(ref m7NpcStationInvalidated) is { } invalidated) return invalidated;
        if (Interlocked.Read(ref m7NpcStationSessionEpoch) != m7NpcStationStartedEpoch) return "session-epoch-changed";
        if (!recording || journalLimitReported) return "evidence-recording-stopped";
        if (Environment.TickCount64 >= m7NpcStationDeadline) return "twenty-minute-expiry";
        var actor = m7NpcStationActor;
        if (actor is null || !actor.Active || !actor.RealPlayer || !actor.IsLoggedIn || actor.Account?.ID != m7NpcStationAccountId ||
            !ReferenceEquals(TShock.Players[actor.Index], actor) || !ReferenceEquals(actor.TPlayer, m7NpcStationPlayer) ||
            !actor.TPlayer.active) return "authenticated-session-changed";
        if (room is null || actor.TileX <= room.Left || actor.TileX >= room.Left + room.Width - 1 ||
            actor.TileY < room.Top || actor.TileY > room.FloorY + 1) return "actor-left-station-room";
        if (Main.netMode != 2 || m7NpcStation.Any(t => !M7NpcMatches(t.Owned))) return "world-or-npc-identity-changed";
        if (m7NpcStation.Any(t => t.Owned.Entity.aiStyle != 7 || t.Owned.Entity.ai.Length != 4 ||
            t.Owned.Entity.noGravity || t.Owned.Entity.noTileCollide)) return "native-npc-shape-changed";
        if (m7NpcStation.Any(t => t.Owned.Entity.life != t.InitialLife || t.Owned.Entity.justHit)) return "fixture-health-changed";
        return null;
    }

    private static bool M7NpcMatches(M7OwnedNpc owned) => Main.worldID == owned.WorldId &&
        string.Equals(Main.worldPathName, owned.WorldPath, StringComparison.OrdinalIgnoreCase) &&
        (uint)owned.Index < Main.npc.Length && ReferenceEquals(Main.npc[owned.Index], owned.Entity) &&
        owned.Entity.active && owned.Entity.generation == owned.Generation && owned.Entity.type == owned.Type;

    private bool M7StationFootprintClear(Vector2 anchor, int width, int height)
    {
        if (room is null || width is <= 0 or > 64 || height is <= 0 or > 96) return false;
        int left = (int)MathF.Floor(anchor.X / 16), right = (int)MathF.Floor((anchor.X + width - 0.01f) / 16);
        int top = (int)MathF.Floor(anchor.Y / 16), bottom = (int)MathF.Floor((anchor.Y + height - 0.01f) / 16);
        if (left < 0 || right >= Main.maxTilesX || top < 0 || room.FloorY >= Main.maxTilesY) return false;
        if (left <= room.Left || right >= room.Left + room.Width - 1 || top < room.Top || bottom != room.FloorY - 1) return false;
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
                if (Main.tile[x, y] is not { } body || body.active() || body.liquid != 0) return false;
            var floor = Main.tile[x, room.FloorY];
            if (floor is null || !floor.nactive() || (uint)floor.type >= Main.tileSolid.Length ||
                !Main.tileSolid[floor.type] || floor.halfBrick() || floor.slope() != 0) return false;
        }
        return true;
    }

    private void StopM7NpcStation(string reason)
    {
        if (m7NpcStationAiInstalled)
        {
            HookEvents.Terraria.NPC.AI -= HoldM7StationAi;
            m7NpcStationAiInstalled = false;
        }
        var finished = m7NpcStation;
        m7NpcStation = []; m7NpcStationActor = null; m7NpcStationPlayer = null;
        if (finished.Length == 0) return;
        int restored = 0;
        if (Environment.CurrentManagedThreadId == m7NpcStationThread)
        {
            foreach (var target in finished)
            {
                var npc = target.Owned.Entity;
                if (!M7NpcMatches(target.Owned) || npc.ai.Length != 4 || npc.ai[0] != 0f ||
                    npc.ai[1] != 300f || npc.ai[2] != 0f || npc.ai[3] != 0f) continue;
                // Restore only temporary AI fields still exactly owned by this hold.
                // Do not rewind positions, life, inventory, homes or world objects.
                target.PreviousAi.CopyTo(npc.ai, 0);
                if (npc.direction == target.Facing) npc.direction = target.PreviousDirection;
                if (npc.spriteDirection == target.Facing) npc.spriteDirection = target.PreviousSpriteDirection;
                npc.netUpdate = true;
                restored++;
            }
        }
        Observe("m7-npc-station-ended", new { reason, fixtureArtificial = true, restoredTemporaryAi = restored,
            aiHolds = m7NpcStationAiHolds, native23Syncs = m7NpcStationSyncs,
            targets = finished.Select(t => new { t.Owned.Index, t.Owned.Generation, t.Owned.Type }),
            note = "AI hold removed; NPCs resume native behavior at their current position. No entity created/removed, no life/home/assets restored." });
        TSPlayer.Server.SendInfoMessage($"NPC station released: {reason}; temporary AI restored on {restored} matching fixtures.");
    }
}
