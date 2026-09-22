using System.Buffers.Binary;
using System.Text.Json;
using Terraria;
using Terraria.GameContent;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Net;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

// Owned disposable fixtures and passive observations. This file is never shipped with AntiCheat.
public sealed partial class GameplayScaffold
{
    private Chest? m6CraftChest;
    private int m6Actor = -1;
    private readonly List<object> m6SafetyEffects = new(128);
    private int m6SafetyEffectsDropped;
    private readonly List<object> m6PacketObservations = new(128);
    private int m6PacketObservationsDropped;
    private Projectile? m6RemoteArrow;
    private uint? m6RemoteArrowKey;
    private long m6RemoteArrowDeadline;
    private int m6RemoteArrowHeldUpdates, m6RemoteArrowExports, m6RemoteArrowReplies;
    private bool m6RemoteArrowHoldInstalled;
    private sealed record M6Statue(string Label, int X, int Y, int SwitchX, int SwitchY);
    private M6Statue[] m6Statues = [];
    private readonly List<object> m6StatueEffects = new(64);
    private int m6StatueEffectsDropped;

    private void PrepareM6RemoteArrow(string[] arguments)
    {
        Require(arguments.Length == 1, "Use qa_m6_remote_arrow <exact authenticated player>.");
        ExpireM6RemoteArrowHold();
        Require(!m6RemoteArrowHoldInstalled, "Wait for the prior two-second owned fixture hold to finish.");
        var actor = ResolvePlayer(arguments[0]);
        Require(Main.netMode == 2 && Main.myPlayer == 255, "The fixture requires the dedicated server owner 255.");
        Require(!actor.HasPermission(Permissions.bypassssc) && !actor.HasPermission("anticheat.bypass"),
            "An ordinary authenticated player without SSC or AntiCheat bypass is required.");
        var plugin = M5Plugin();
        Require(plugin.GetType().GetField("_scope", PrivateM5)!.GetValue(plugin)?.ToString() == "TestLab",
            "Remote-arrow GUI fixture requires AntiCheat TestLab scope.");
        Require(Main.projectile.Take(1000).Any(p => p is { active: false }),
            "A free native projectile slot is required; no existing entity will be replaced.");
        // Creation is a labeled server fixture. The later packet29 must come from the GUI's
        // real Projectile.Update path; this command never constructs or injects client input.
        var previousCapture = Volatile.Read(ref rawPacketIds);
        Volatile.Write(ref rawPacketIds, previousCapture.Append(29).Distinct().Order().ToArray());
        int index = Projectile.NewProjectile(new EntitySource_DebugCommand(), -64f, -64f, 0f, 0f,
            ProjectileID.WoodenArrowFriendly, 0, 0f, 255);
        Require((uint)index < Main.projectile.Length, "Native projectile creation did not return a live slot.");
        var shot = Main.projectile[index];
        Require(shot is { active: true, type: ProjectileID.WoodenArrowFriendly, owner: 255 } &&
            float.IsFinite(shot.position.X) && float.IsFinite(shot.position.Y) &&
            shot.position.X < Main.leftWorld && shot.position.Y < Main.topWorld,
            "Native remote-arrow identity or finite out-of-world fixture position changed.");
        m6RemoteArrow = shot;
        m6RemoteArrowKey = shot.key.bits;
        m6RemoteArrowHeldUpdates = m6RemoteArrowExports = m6RemoteArrowReplies = 0;
        m6RemoteArrowDeadline = System.Diagnostics.Stopwatch.GetTimestamp() + 2 * System.Diagnostics.Stopwatch.Frequency;
        HookEvents.Terraria.Projectile.Update += HoldM6RemoteArrowUpdate;
        HookEvents.Terraria.NetMessage.SendData += ObserveM6RemoteArrowExport;
        Main.OnTickForThirdPartySoftwareOnly += ExpireM6RemoteArrowHold;
        m6RemoteArrowHoldInstalled = true;
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true,
            actor = actor.Name, actorSlot = actor.Index, accountId = actor.Account.ID,
            projectileIndex = index, key = shot.key.bits, spawner = shot.key.Spawner,
            generation = shot.key.Generation, shot.owner, shot.type, shot.damage,
            x = shot.position.X, y = shot.position.Y,
            artificialServerHold = new { maximumSeconds = 2, exactObjectAndKey = true,
                purpose = "Delay only this artificial server entity's native Update so the GUI can run its own native foreign-owner cleanup before receiving server cleanup." },
            explicitNativeExport = new { packet = 27, remoteClient = actor.Index, ignoreClient = -1 },
            note = "Console-only isolated artificial server projectile and two-second server hold. Native NewProjectile also broadcasts27; explicit SendData27 targets this GUI. Only a subsequently captured real client29 demonstrates native foreign-owner cleanup. This is not a spontaneous normal-server timing scenario." };
        Record("m6-remote-arrow-fixture", payload);
        NetMessage.SendData(27, actor.Index, -1, null, index);
        File.WriteAllText(Path.Combine(output!, "m6-remote-arrow-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage("QA_M6_REMOTE_ARROW " + JsonSerializer.Serialize(payload));
    }

    private void HoldM6RemoteArrowUpdate(object? sender, HookEvents.Terraria.Projectile.UpdateEventArgs args)
    {
        ExpireM6RemoteArrowHold();
        if (!m6RemoteArrowHoldInstalled || !args.ContinueExecution ||
            !ReferenceEquals(sender, m6RemoteArrow) || sender is not Projectile shot ||
            shot.key.bits != m6RemoteArrowKey || !shot.active || shot.owner != 255 ||
            shot.type != ProjectileID.WoodenArrowFriendly || args.i != shot.whoAmI ||
            (uint)args.i >= Main.projectile.Length || !ReferenceEquals(Main.projectile[args.i], shot)) return;
        args.ContinueExecution = false;
        if (m6RemoteArrowHeldUpdates < int.MaxValue) m6RemoteArrowHeldUpdates++;
    }

    private void ObserveM6RemoteArrowExport(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!m6RemoteArrowHoldInstalled || args.msgType != 27 || !args.ContinueExecution ||
            (uint)args.number >= Main.projectile.Length || Main.projectile[args.number] is not { } shot ||
            !ReferenceEquals(shot, m6RemoteArrow) || shot.key.bits != m6RemoteArrowKey || m6RemoteArrowExports >= 8) return;
        m6RemoteArrowExports++;
        Observe("m6-remote-arrow-native-export-entry", new { fixtureArtificial = true, packet = 27,
            key = shot.key.bits, generation = shot.key.Generation, shot.owner, shot.type, shot.damage,
            args.remoteClient, args.ignoreClient, args.ContinueExecution,
            note = "Passive native SendData entry; GUI receipt and its packet29 response are separate evidence." });
    }

    private void ExpireM6RemoteArrowHold()
    {
        if (m6RemoteArrowHoldInstalled && System.Diagnostics.Stopwatch.GetTimestamp() >= m6RemoteArrowDeadline)
            StopM6RemoteArrowHold("two-second-deadline");
    }

    private void StopM6RemoteArrowHold(string reason)
    {
        if (!m6RemoteArrowHoldInstalled) return;
        HookEvents.Terraria.Projectile.Update -= HoldM6RemoteArrowUpdate;
        HookEvents.Terraria.NetMessage.SendData -= ObserveM6RemoteArrowExport;
        Main.OnTickForThirdPartySoftwareOnly -= ExpireM6RemoteArrowHold;
        m6RemoteArrowHoldInstalled = false;
        Observe("m6-remote-arrow-server-hold-ended", new { fixtureArtificial = true, reason,
            key = m6RemoteArrowKey, heldUpdates = m6RemoteArrowHeldUpdates,
            note = "Only the scoped fixture subscriptions were removed; native server execution may resume. No Kill, ForceDeath or client packet was invoked." });
        m6RemoteArrow = null;
    }

    private void InstallM6Safety()
    {
        HookEvents.Terraria.GameContent.CraftingRequests.Consume += ObserveM6Consume;
        HookEvents.Terraria.Wiring.HitWireSingle += ObserveM6StatueWire;
        HookEvents.Terraria.Wiring.CheckMech += ObserveM6StatueCooldown;
        HookEvents.Terraria.NPC.NewNPC += ObserveM6StatueSpawn;
        HookEvents.Terraria.NetMessage.SendData += ObserveM6StatueExport;
        ServerApi.Hooks.NetGetData.Register(this, ObserveM6PostRaw, -1000);
    }
    private void DisposeM6Safety()
    {
        StopM6RemoteArrowHold("scaffold-dispose");
        HookEvents.Terraria.GameContent.CraftingRequests.Consume -= ObserveM6Consume;
        HookEvents.Terraria.Wiring.HitWireSingle -= ObserveM6StatueWire;
        HookEvents.Terraria.Wiring.CheckMech -= ObserveM6StatueCooldown;
        HookEvents.Terraria.NPC.NewNPC -= ObserveM6StatueSpawn;
        HookEvents.Terraria.NetMessage.SendData -= ObserveM6StatueExport;
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM6PostRaw);
    }

    private void ObserveM6PostRaw(GetDataEventArgs args)
    {
        if (recording && (byte)args.MsgID == 29 && args.Length - 1 == 12 &&
            args.Msg?.readBuffer is { } remoteBuffer && args.Index >= 0 && args.Index <= remoteBuffer.Length - 12 &&
            m6RemoteArrowKey is { } expectedKey && BinaryPrimitives.ReadUInt32LittleEndian(remoteBuffer.AsSpan(args.Index)) == expectedKey &&
            m6RemoteArrowReplies < 8)
        {
            m6RemoteArrowReplies++;
            Observe("m6-remote-arrow-client-cleanup-post-guard", new { fixtureArtificial = true,
                packet = 29, sender = args.Msg.whoAmI, key = expectedKey, args.Handled,
                rawBody = Convert.ToHexString(remoteBuffer.AsSpan(args.Index, 12)),
                note = "Real incoming TCP body observed after AntiCheat1000/TShock0 at priority-1000; observer does not alter Handled." });
        }
        if (!recording || (byte)args.MsgID != 28 || args.Length - 1 != 10 ||
            args.Msg?.readBuffer is not { } buffer || args.Index < 0 || args.Index > buffer.Length - 10 ||
            buffer[args.Index] != m5Target || buffer[args.Index + 1] != m5Generation) return;
        if (m6PacketObservations.Count >= 128)
        { if (m6PacketObservationsDropped < int.MaxValue) m6PacketObservationsDropped++; return; }
        m6PacketObservations.Add(new { utc = DateTimeOffset.UtcNow, packet = 28, sender = args.Msg.whoAmI,
            target = buffer[args.Index], generation = buffer[args.Index + 1],
            damage = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(args.Index + 2)),
            encodedDirection = buffer[args.Index + 8], args.Handled,
            note = "Passive NetGetData priority -1000, after AntiCheat1000 and TShock0; no argument changes." });
    }

    private void ObserveM6Consume(object? sender, HookEvents.Terraria.GameContent.CraftingRequests.ConsumeEventArgs args)
    {
        if (!recording || m6CraftChest is null || args.chests is null || !args.chests.Contains(m6CraftChest)) return;
        if (m6SafetyEffects.Count < 128)
            m6SafetyEffects.Add(new { utc = DateTimeOffset.UtcNow, kind = "native-consume-entry",
                args.req.itemIdOrRecipeGroup, args.req.stack, args.fromChests, args.ContinueExecution,
                chest = m6CraftChest.index, woodBefore = m6CraftChest.item.Take(m6CraftChest.maxItems)
                    .Where(x => x.type == ItemID.Wood).Sum(x => x.stack) });
        else if (m6SafetyEffectsDropped < int.MaxValue) m6SafetyEffectsDropped++;
    }

    private void PrepareM6Safety(string[] arguments)
    {
        Require(arguments.Length == 1, "Use qa_m6_safety <exact authenticated player>.");
        Require(m6CraftChest is null && room is null, "M6 needs a fresh disposable room; existing fixtures are retained.");
        var player = ResolvePlayer(arguments[0]);
        Require(!player.HasPermission(Permissions.editregion) && !player.HasPermission(Permissions.bypassssc)
            && !player.HasPermission("anticheat.bypass"), "Ordinary permissions are required.");
        Require(NetManager.Instance.GetModule<CraftingRequests.NetCraftingRequestsModule>() is not null,
            "Native crafting module is not registered.");
        Prepare(player); // Existing bounded room creation and all isolation checks stay in force.
        var chest = Main.chest[room!.ChestId];
        Require(chest is not null && chest.item.Take(chest.maxItems).All(x => x.IsAir),
            "New fixture chest must be empty; existing contents are never replaced.");
        m6Actor = player.Index;
        m6CraftChest = chest;
        PrepareM6Statues(player);
        chest!.item[0].SetDefaults(ItemID.Wood); chest.item[0].stack = 30;
        NetMessage.SendData(32, number: chest.index, number2: 0);
        Record("m6-safety-fixture-prepared", new { player = player.Name, account = player.Account.ID,
            chest = chest.index, chest.x, chest.y, setupWood = 30,
            note = "One-time console fixture initialization; later craft inputs arrive over ordinary TCP." });
        WriteM6SafetyState();
    }

    private void PrepareM6Statues(TSPlayer actor)
    {
        Require(room is { State: "prepared" } && m6Statues.Length == 0, "Statues require the freshly owned room.");
        var ownedRoom = room!;
        Require(!Main.npc.Any(n => n is { active: true, SpawnedFromStatue: true }),
            "Existing statue NPCs are retained; this exclusive fixture cannot be established.");
        var planned = new[] { new M6Statue("allowed", ownedRoom.Left + 7, ownedRoom.FloorY - 3, ownedRoom.Left + 5, ownedRoom.FloorY - 1),
            new M6Statue("protected", ownedRoom.Left + 19, ownedRoom.FloorY - 3, ownedRoom.Left + 17, ownedRoom.FloorY - 1) };
        var cells = planned.SelectMany(s => Enumerable.Range(s.SwitchX, s.X + 2 - s.SwitchX)
            .SelectMany(x => Enumerable.Range(s.Y, 3).Select(y => (X: x, Y: y)))).Distinct().ToArray();
        Require(cells.All(p => p.X > ownedRoom.Left && p.X < ownedRoom.Left + ownedRoom.Width - 1 && p.Y > ownedRoom.Top && p.Y < ownedRoom.FloorY),
            "Statue circuits must stay inside the recorded disposable room.");
        Require(cells.All(p => Main.tile[p.X, p.Y] is { } tile && !tile.active() && !tile.wire() &&
            !tile.wire2() && !tile.wire3() && !tile.wire4() && !tile.actuator()),
            "Statue circuit intersects existing tile/wire state; no existing interaction is replaced.");
        Require(cells.All(p => actor.HasBuildPermission(p.X, p.Y, false)), "Statue fixture starts in an allowed owned-room area.");
        string regionName = "qa_m6_statue_" + Main.worldID;
        Require(TShock.Regions.GetRegionByName(regionName) is null, "Existing named statue region is retained.");
        var denied = planned[1];
        Require(TShock.Regions.AddRegion(denied.X + 1, denied.Y + 2, 1, 1, regionName,
            "qa-scaffold-owner", Main.worldID.ToString(), 1000000), "Could not create isolated last-footprint-cell region.");
        foreach (var statue in planned)
        {
            for (int dx = 0; dx < 2; dx++) for (int dy = 0; dy < 3; dy++)
            {
                var tile = Main.tile[statue.X + dx, statue.Y + dy];
                tile.active(true); tile.type = TileID.Statues;
                tile.frameX = (short)(4 * 36 + dx * 18); tile.frameY = (short)(dy * 18);
            }
            var trigger = Main.tile[statue.SwitchX, statue.SwitchY];
            trigger.active(true); trigger.type = TileID.PressurePlates; trigger.frameX = trigger.frameY = 0;
            for (int x = statue.SwitchX; x <= statue.X; x++) Main.tile[x, statue.SwitchY].wire(true);
        }
        m6Statues = planned;
        Record("m6-statue-fixtures-prepared", new { fixtureArtificial = true, regionName, planned, cells,
            note = "One-time setup inside the already cleared owned room. Separate red circuits, slime-statue style4; second region denies only its last footprint cell. Actual triggers must arrive as TCP59." });
        TSPlayer.All.SendTileRect((short)(ownedRoom.Left + 4), (short)(ownedRoom.FloorY - 4), 18, 5);
    }

    private bool M6CooldownPresent(M6Statue statue) => Enumerable.Range(0,
        Math.Clamp(Wiring._numMechs, 0, Math.Min(Wiring._mechX.Length, Wiring._mechY.Length)))
        .Any(i => Wiring._mechX[i] == statue.X && Wiring._mechY[i] == statue.Y);

    private void M6StatueEffect(object value)
    {
        if (m6StatueEffects.Count < 64) m6StatueEffects.Add(value);
        else if (m6StatueEffectsDropped < int.MaxValue) m6StatueEffectsDropped++;
    }

    private void ObserveM6StatueWire(object? sender, HookEvents.Terraria.Wiring.HitWireSingleEventArgs args)
    {
        if (!recording) return;
        var fixture = m6Statues.FirstOrDefault(s => args.i >= s.X && args.i < s.X + 2 && args.j >= s.Y && args.j < s.Y + 3);
        if (fixture is null) return;
        M6StatueEffect(new { kind = "statue-execution-hook", fixture.Label, args.i, args.j,
            currentUser = Wiring.CurrentUser, args.ContinueExecution,
            cooldownPresent = M6CooldownPresent(fixture), note = "Passive callback after the installed AntiCheat statue guard, before original HitWireSingle." });
    }

    private void ObserveM6StatueCooldown(object? sender, HookEvents.Terraria.Wiring.CheckMechEventArgs args)
    {
        if (!recording) return;
        var fixture = m6Statues.FirstOrDefault(s => s.X == args.i && s.Y == args.j);
        if (fixture is not null) M6StatueEffect(new { kind = "native-cooldown-entry", fixture.Label,
            args.i, args.j, args.time, args.ContinueExecution, priorPresent = M6CooldownPresent(fixture) });
    }

    private void ObserveM6StatueSpawn(object? sender, HookEvents.Terraria.NPC.NewNPCEventArgs args)
    {
        if (!recording || args.source is not EntitySource_Wiring || args.Type != NPCID.BlueSlime) return;
        var fixture = m6Statues.FirstOrDefault(s => args.X == s.X * 16 + 16 && args.Y == (s.Y + 3) * 16 - 12);
        if (fixture is not null) M6StatueEffect(new { kind = "native-statue-npc-create-entry", fixture.Label,
            args.X, args.Y, args.Type, args.ContinueExecution, cooldownPresent = M6CooldownPresent(fixture),
            source = args.source.GetType().FullName });
    }

    private void ObserveM6StatueExport(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!recording || m6Statues.Length == 0 || !args.ContinueExecution || args.msgType != 23 ||
            (uint)args.number >= Main.npc.Length || Main.npc[args.number] is not { active: true, SpawnedFromStatue: true } npc) return;
        // Keep one export per native NPC generation; normal later movement broadcasts are not new spawn effects.
        string key = npc.whoAmI + ":" + npc.generation;
        if (m6StatueExported.Contains(key)) return;
        if (m6StatueExported.Count >= 64)
        { if (m6StatueEffectsDropped < int.MaxValue) m6StatueEffectsDropped++; return; }
        m6StatueExported.Add(key);
        M6StatueEffect(new { kind = "native-statue-npc-export", index = npc.whoAmI, npc.generation,
            npc.type, npc.SpawnedFromStatue, args.remoteClient, args.ignoreClient });
    }

    private readonly HashSet<string> m6StatueExported = new();

    private object CaptureM6Statues(TSPlayer? actor, object plugin)
    {
        var guard = plugin.GetType().GetField("_wiringExecution", PrivateM5)!.GetValue(plugin);
        object? Counter(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        return new { guardPresent = guard is not null, contractHealthy = Counter("ContractHealthy"),
            allowed = Counter("Allowed"), permissionBlocked = Counter("PermissionBlocked"),
            budgetBlocked = Counter("BudgetBlocked"), unknownActor = Counter("UnknownActor"),
            fixtures = m6Statues.Select(s => new { s.Label, s.X, s.Y, s.SwitchX, s.SwitchY,
                switchAllowed = actor?.HasBuildPermission(s.SwitchX, s.SwitchY, false) ?? false,
                footprint = Enumerable.Range(0, 2).SelectMany(dx => Enumerable.Range(0, 3).Select(dy => new {
                    x = s.X + dx, y = s.Y + dy, allowed = actor?.HasBuildPermission(s.X + dx, s.Y + dy, false) ?? false })).ToArray(),
                cooldownPresent = M6CooldownPresent(s) }).ToArray(),
            spawnedNpcs = Main.npc.Where(n => n is { active: true, SpawnedFromStatue: true })
                .Select(n => new { index = n.whoAmI, n.generation, n.type, n.SpawnedFromStatue }).ToArray(),
            effects = m6StatueEffects.ToArray(), effectsDropped = m6StatueEffectsDropped,
            nativeCurrentUser = Wiring.CurrentUser, nativeRunning = Wiring.running };
    }

    private void WriteM6SafetyState()
    {
        Require(m6CraftChest is not null && (uint)m6Actor < Main.maxPlayers, "Prepare the M6 fixture first.");
        var chest = m6CraftChest!;
        Require((uint)chest.index < Main.chest.Length && ReferenceEquals(Main.chest[chest.index], chest),
            "Owned chest identity changed; no replacement snapshot is accepted.");
        var actor = TShock.Players[m6Actor];
        var plugin = M5Plugin();
        var inventory = plugin.GetType().GetField("_inventory", PrivateM5)!.GetValue(plugin);
        object? Counter(string name) => inventory?.GetType().GetProperty(name)?.GetValue(inventory);
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            module = NetManager.Instance.GetId<CraftingRequests.NetCraftingRequestsModule>(),
            chest = new { id = chest.index, chest.x, chest.y, chest.maxItems,
                locked = Chest.IsLocked(chest.x, chest.y), usingPlayer = Chest.UsingChest(chest.index),
                items = chest.item.Take(chest.maxItems).Select((item, slot) => new { slot, item.type, item.stack, item.prefix }).ToArray() },
            actor = actor is null ? null : new { slot = actor.Index, actor.Name, account = actor.Account?.ID,
                actor.IsLoggedIn, actor.HasSentInventory, actor.IgnoreSSCPackets, actor.ActiveChest,
                nativeChest = actor.TPlayer.chest, x = actor.TPlayer.position.X, y = actor.TPlayer.position.Y,
                inRange = actor.IsInRange(chest.x, chest.y),
                regionAllowed = TShock.Regions.CanBuild(chest.x, chest.y, actor) },
            inventoryContextPresent = inventory is not null,
            malformedBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin),
            duplicateTargetsRemoved = Counter("DuplicateCraftTargetsRemoved"),
            infeasibleRequestsRejected = Counter("InfeasibleCraftRequestsRejected"),
            simulationBudgetRejections = Counter("CraftSimulationBudgetRejections"),
            lastSimulationSteps = Counter("LastCraftSimulationSteps"),
            consumeEntries = m6SafetyEffects.ToArray(), effectsDropped = m6SafetyEffectsDropped,
            statues = CaptureM6Statues(actor, plugin),
            worldItems = Main.item.Select((item, index) => new { index, item.active, item.type, item.stack,
                x = item.position.X, y = item.position.Y }).Where(x => x.active).ToArray(),
            note = "Passive snapshot plus native Consume entries. Client approval and chest broadcasts are separately captured from real TCP."
        };
        File.WriteAllText(Path.Combine(output!, "m6-safety-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage("QA_M6_STATE " + JsonSerializer.Serialize(new { payload.utc, chest = chest.index,
            payload.duplicateTargetsRemoved, payload.infeasibleRequestsRejected }));
    }
}
