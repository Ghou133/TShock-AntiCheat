using System.Collections.Concurrent;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace CompatibilityAudit;

[ApiVersion(2, 1)]
public sealed partial class GameplayScaffold : TerrariaPlugin
{
    private readonly List<Command> commands = [];
    private readonly ConcurrentQueue<(string Command, string[] Arguments)> pending = new();
    private readonly ConcurrentQueue<object> observations = new();
    private const int MaximumObservationQueue = 8192;
    private const int MaximumPendingCommands = 32;
    private const long MaximumJournalBytes = 64L * 1024 * 1024;
    private int observationCount;
    private long observationDropped;
    private long journalBytes;
    private bool journalLimitReported;
    private int statusFiles;
    private PendingCancellation? forcedCancellation;
    private int[] rawPacketIds = [16, 27, 28, 29, 42, 53, 55, 65, 96, 100, 117, 118, 153, 155];
    private readonly string session = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private string? root;
    private string? output;
    private string? journal;
    private Room? room;
    private int secondaryChestId = -1;
    private bool recording;
    private DateTimeOffset lastSnapshot;
    private long sequence;

    public override string Name => "GameplayScaffold (isolated QA only)";
    public override string Author => "Local compatibility audit";
    public override string Description => "Console-created disposable test room and passive real-client observations.";
    public override Version Version => new(1, 0, 2);

    public GameplayScaffold(Main game) : base(game) { Order = 1000; }

    public override void Initialize()
    {
        foreach (var name in new[]
        {
            "qa_status", "qa_prepare", "qa_mark", "qa_npcs", "qa_chests", "qa_cancel_next", "qa_capture",
            "qa_m5_target", "qa_m5_state", "qa_m5_maintenance", "qa_m5_recoverable", "qa_m5_recover",
            "qa_m6_safety", "qa_m6_state", "qa_m6_remote_arrow", "qa_m7_materials", "qa_m7_pumps", "qa_m7_pump_state",
            "qa_m7_loadouts", "qa_m7_loadout_state", "qa_m7_npc_station", "qa_m8_liquid", "qa_m8_liquid_state",
            "qa_m9_application", "qa_m9_application_state", "qa_m9_craft", "qa_m9_craft_arm", "qa_m9_craft_state",
            "qa_m9_paint", "qa_m9_paint_mode", "qa_m9_paint_state", "qa_m9_liquid", "qa_m9_liquid_state",
            "qa_m16_wall", "qa_m16_wall_mode", "qa_m16_wall_state",
            "qa_m16_egress", "qa_m16_egress_state", "qa_m16_timeouts", "qa_m17_work",
            "qa_m16_inventory_slots", "qa_m16_inventory_slots_state", "qa_m16_item_structure", "qa_m16_item_structure_state",
            "qa_m17_sort", "qa_m17_sort_state", "qa_m17_sort_add_pending", "qa_m17_sort_policy", "qa_m17_e01", "qa_m18_storage",
            "qa_m9_teleport", "qa_m9_teleport_state", "qa_m10_rod", "qa_m10_quickstack", "qa_m10_quickstack_state",
            "qa_m10_summon", "qa_m10_summon_state", "qa_m10_summon_materials",
            "qa_m11_sentry_state", "qa_m11_sentry_materials",
            "qa_m10_liquid_control", "qa_m10_liquid_control_state", "qa_m10_liquid_control_finish", "qa_m12_receive", "qa_m13_display", "qa_m14_object", "qa_m14l_placement", "qa_m14r_entity"
        })
        {
            var commandName = name;
            var command = new Command("compatibility.qa.console", args => Enqueue(commandName, args), name)
            {
                AllowServer = true,
                HelpText = name == "qa_prepare" ? "qa_prepare <exact player name or tsi:index>; authenticated real player required." : name == "qa_mark" ? "qa_mark <observation label>" : "Write current isolated server/player state and enable passive observations."
            };
            commands.Add(command);
            Commands.ChatCommands.Add(command);
        }
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
        ServerApi.Hooks.GamePostUpdate.Register(this, OnPostUpdate);
        ServerApi.Hooks.NetGetData.Register(this, OnRawData, 2000);
        // Official idle-server callback runs in the same main loop when Game.Update is paused.
        Main.OnTickForThirdPartySoftwareOnly += OnIdleTick;
        GetDataHandlers.PlayerSlot.Register(OnPlayerSlot, HandlerPriority.Lowest, true);
        GetDataHandlers.TileEdit.Register(OnTileEdit, HandlerPriority.Lowest, true);
        GetDataHandlers.ChestOpen.Register(OnChestOpen, HandlerPriority.Lowest, true);
        GetDataHandlers.ChestItemChange.Register(OnChestItem, HandlerPriority.Lowest, true);
        PlayerHooks.PlayerPermission += OnPermission;
        InstallM5();
        InstallM7Business();
        InstallM7NpcInteraction();
        InstallM9World();
        InstallM16WorldPaint();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeClientAutomationState();
            DisposeApplicationWitness();
            DisposeM9CraftWitness();
            DisposeM9World();
            DisposeM16WorldPaint();
            DisposeM16Egress();
            DisposeM16TimeoutFixture();
            DisposeM17CommandWork();
            DisposeM18Storage();
            DisposeM16InventorySlot();
            DisposeM16ItemStructure();
            DisposeM17ProgressionWorld();
            DisposeTeleportWitness();
            DisposeM10QuickStack();
            DisposeM10Summon();
            DisposeM10LiquidControl();
            DisposeM12Receive();
            DisposeM13Display();
            DisposeM14Object();
            DisposeM14LObjectPlacement();
            DisposeM14ResumeTileEntityPlacement();
            DisposeM7NpcInteraction();
            DisposeM7Business();
            foreach (var command in commands) Commands.ChatCommands.Remove(command);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
            ServerApi.Hooks.GamePostUpdate.Deregister(this, OnPostUpdate);
            ServerApi.Hooks.NetGetData.Deregister(this, OnRawData);
            Main.OnTickForThirdPartySoftwareOnly -= OnIdleTick;
            GetDataHandlers.PlayerSlot.UnRegister(OnPlayerSlot);
            GetDataHandlers.TileEdit.UnRegister(OnTileEdit);
            GetDataHandlers.ChestOpen.UnRegister(OnChestOpen);
            GetDataHandlers.ChestItemChange.UnRegister(OnChestItem);
            PlayerHooks.PlayerPermission -= OnPermission;
            DisposeM5();
        }
        base.Dispose(disposing);
    }

    private void Enqueue(string name, CommandArgs args)
    {
        if (!ReferenceEquals(args.Player, TSPlayer.Server))
        {
            args.Player.SendErrorMessage("QA scaffold commands require the local server console.");
            return;
        }
        if (pending.Count >= MaximumPendingCommands)
        {
            args.Player.SendErrorMessage("QA command queue is full; no command was queued.");
            return;
        }
        pending.Enqueue((name, args.Parameters.ToArray()));
        args.Player.SendInfoMessage($"{name} queued for the server main thread.");
    }

    private void OnUpdate(EventArgs args)
    {
        ProcessCommands("TSAPI.GameUpdate");
        TickM7NpcStation();
        TickM12Receive();
        TickClientAutomationState();
    }
    private void OnPostUpdate(EventArgs args) => ObserveState();
    private void OnIdleTick()
    {
        ProcessCommands("Terraria.Main.OnTickForThirdPartySoftwareOnly");
        TickM7NpcStation();
        TickM12Receive();
        ObserveState();
    }

    private void ProcessCommands(string hook)
    {
        int processed = 0;
        while (processed++ < 4 && pending.TryDequeue(out var request))
        {
            try
            {
                ValidateIsolation();
                recording = true;
                Record("console-command", new { request.Command, request.Arguments, hook, mainThreadId = Environment.CurrentManagedThreadId });
                switch (request.Command)
                {
                    case "qa_m13_display": M13DisplayCommand(request.Arguments); break;
                    case "qa_m14_object": M14ObjectCommand(request.Arguments); break;
                    case "qa_m14l_placement": M14LObjectPlacementCommand(request.Arguments); break;
                    case "qa_m14r_entity": M14ResumeTileEntityPlacementCommand(request.Arguments); break;
                    case "qa_m12_receive": M12ReceiveCommand(request.Arguments); break;
                    case "qa_m9_application": PrepareApplicationWitness(request.Arguments); break;
                    case "qa_m9_application_state": WriteApplicationState(); break;
                    case "qa_m9_craft": PrepareM9Craft(request.Arguments); break;
                    case "qa_m9_craft_arm": ArmM9LateCraftMutation(); break;
                    case "qa_m9_craft_state": WriteM9CraftState(); break;
                    case "qa_m9_paint": PrepareM9Paint(request.Arguments); break;
                    case "qa_m9_paint_mode": ArmM9Paint(request.Arguments); break;
                    case "qa_m9_paint_state": WriteM9PaintState(); break;
                    case "qa_m16_wall": PrepareM16WallPaint(request.Arguments); break;
                    case "qa_m16_wall_mode": ArmM16WallPaint(request.Arguments); break;
                    case "qa_m16_wall_state": WriteM16WallPaintState(); break;
                    case "qa_m16_egress": PrepareM16Egress(request.Arguments); break;
                    case "qa_m16_egress_state": WriteM16EgressState(); break;
                    case "qa_m16_timeouts": M16TimeoutCommand(request.Arguments); break;
                    case "qa_m17_work": M17CommandWorkCommand(request.Arguments); break;
                    case "qa_m16_inventory_slots": PrepareM16InventorySlot(request.Arguments); break;
                    case "qa_m16_inventory_slots_state": WriteM16InventorySlotState(); break;
                    case "qa_m16_item_structure": PrepareM16ItemStructure(request.Arguments); break;
                    case "qa_m16_item_structure_state": WriteM16ItemStructureState(); break;
                    case "qa_m18_storage": M18StorageCommand(request.Arguments); break;
                    case "qa_m17_e01": M17ProgressionWorldCommand(request.Arguments); break;
                    case "qa_m17_sort": PrepareM17ContainerSort(request.Arguments); break;
                    case "qa_m17_sort_state": WriteM17ContainerSortState(); break;
                    case "qa_m17_sort_add_pending": PrepareM17PendingSort(); break;
                    case "qa_m17_sort_policy": SetM17ContainerSortPolicy(request.Arguments); break;
                    case "qa_m9_liquid": PrepareM9LiquidFacility(request.Arguments); break;
                    case "qa_m9_liquid_state": WriteM9LiquidFacilityState(); break;
                    case "qa_m9_teleport": PrepareTeleportWitness(request.Arguments); break;
                    case "qa_m10_rod": SetTeleportRodPermission(request.Arguments); break;
                    case "qa_m10_quickstack": PrepareM10QuickStack(request.Arguments); break;
                    case "qa_m10_quickstack_state": WriteM10QuickStackState(); break;
                    case "qa_m10_summon": PrepareM10Summon(request.Arguments); break;
                    case "qa_m10_summon_state": WriteM10SummonState(); break;
                    case "qa_m10_summon_materials": SupplyM10SummonGuiMaterials(request.Arguments); break;
                    case "qa_m11_sentry_state": WriteM11SentryState(); break;
                    case "qa_m11_sentry_materials": SupplyM11SentryGuiMaterials(request.Arguments); break;
                    case "qa_m10_liquid_control": PrepareM10LiquidControl(request.Arguments); break;
                    case "qa_m10_liquid_control_state": WriteM10LiquidControlState(); break;
                    case "qa_m10_liquid_control_finish": FinishM10LiquidControl(); break;
                    case "qa_m9_teleport_state": WriteTeleportState(); break;
                    case "qa_m7_materials": PrepareM7Materials(request.Arguments); break;
                    case "qa_m7_npc_station": PrepareM7NpcStation(request.Arguments); break;
                    case "qa_m7_pumps": PrepareM7Pumps(request.Arguments); break;
                    case "qa_m7_pump_state": WriteM7PumpState(); break;
                    case "qa_m7_loadouts": PrepareM7Loadouts(request.Arguments); break;
                    case "qa_m7_loadout_state": WriteM7LoadoutState(); break;
                    case "qa_m8_liquid": PrepareM8LiquidPropagation(); break;
                    case "qa_m8_liquid_state": WriteM8LiquidPropagationState(); break;
                    case "qa_m6_safety": PrepareM6Safety(request.Arguments); break;
                    case "qa_m6_state": WriteM6SafetyState(); break;
                    case "qa_m6_remote_arrow": PrepareM6RemoteArrow(request.Arguments); break;
                    case "qa_m5_target": PrepareM5Target(request.Arguments); break;
                    case "qa_m5_state": WriteM5State(); break;
                    case "qa_m5_maintenance": InjectM5Maintenance(); break;
                    case "qa_m5_recoverable": InjectM5Recoverable(); break;
                    case "qa_m5_recover": RecoverM5(); break;
                    case "qa_status":
                        Require(request.Arguments.Length == 0, "Use qa_status without arguments.");
                        Snapshot("qa_status", true);
                        break;
                    case "qa_mark":
                        Require(request.Arguments.Length > 0, "Use qa_mark <observation label>.");
                        Snapshot("qa_mark: " + string.Join(" ", request.Arguments), true);
                        break;
                    case "qa_prepare":
                        Require(request.Arguments.Length == 1, "Use qa_prepare <exact player name or tsi:index>; quote names containing spaces.");
                        Prepare(ResolvePlayer(request.Arguments[0]));
                        Snapshot("qa_prepare-complete", true);
                        break;
                    case "qa_npcs":
                        Require(request.Arguments.Length == 1, "Use qa_npcs <exact player name or tsi:index>; quote names containing spaces.");
                        PrepareNpcs(ResolvePlayer(request.Arguments[0]));
                        Snapshot("qa_npcs-complete", true);
                        break;
                    case "qa_chests":
                        Require(request.Arguments.Length == 1, "Use qa_chests <exact player name or tsi:index>.");
                        PrepareChests(ResolvePlayer(request.Arguments[0]));
                        Snapshot("qa_chests-complete", true);
                        break;
                    case "qa_cancel_next":
                        Require(request.Arguments.Length == 2 && int.TryParse(request.Arguments[1], out _), "Use qa_cancel_next <player> <100|153|50>.");
                        ArmCancellation(ResolvePlayer(request.Arguments[0]), int.Parse(request.Arguments[1]));
                        break;
                    case "qa_capture":
                        ConfigureRawCapture(request.Arguments);
                        break;
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"GameplayScaffold {request.Command}: {ex}");
                TSPlayer.Server.SendErrorMessage($"{request.Command} failed: {ex.Message}");
                if (journal != null)
                {
                    try { Record("command-failed", new { request.Command, error = ex.ToString() }); }
                    catch (Exception logError) { TShock.Log.Error($"GameplayScaffold could not persist failure evidence: {logError}"); }
                }
            }
        }
    }

    private void ValidateIsolation()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("COMPAT_QA_ROOT");
        var configuredOutput = Environment.GetEnvironmentVariable("COMPAT_QA_OUTPUT");
        Require(!string.IsNullOrWhiteSpace(configuredRoot) && Path.IsPathFullyQualified(configuredRoot), "COMPAT_QA_ROOT must be the absolute disposable run directory.");
        Require(!string.IsNullOrWhiteSpace(configuredOutput) && Path.IsPathFullyQualified(configuredOutput), "COMPAT_QA_OUTPUT must be the absolute audit evidence directory.");
        root = Path.GetFullPath(configuredRoot!);
        output = Path.GetFullPath(configuredOutput!);
        Require(File.Exists(Path.Combine(root, ".compatibility-isolated-test")), "Isolation marker is missing.");
        Require(IsUnder(Environment.CurrentDirectory, root) && IsUnder(TShock.SavePath, root), "Working directory and TShock save path must be inside the isolated run root.");
        Require(!string.IsNullOrEmpty(Main.worldPathName) && IsUnder(Main.worldPathName, root) && File.Exists(Main.worldPathName), "Loaded world must be an existing file inside the isolated run root.");
        Require(TShock.DB.GetType().FullName == "Microsoft.Data.Sqlite.SqliteConnection", "Only isolated SQLite is supported.");
        var databasePath = Convert.ToString(TShock.DB.GetType().GetProperty("DataSource")!.GetValue(TShock.DB));
        Require(!string.IsNullOrEmpty(databasePath) && IsUnder(databasePath, root), "SQLite DataSource must be inside the isolated run root.");
        Directory.CreateDirectory(output);
        journal ??= Path.Combine(output, $"gameplay-events-{session}.jsonl");
        var roomFile = Path.Combine(root, "qa-scaffold-room.json");
        if (room == null && File.Exists(roomFile))
            room = JsonSerializer.Deserialize<Room>(File.ReadAllText(roomFile));
        string chestFile = Path.Combine(root, "qa-chests.json");
        if (secondaryChestId < 0 && File.Exists(chestFile))
        {
            using var prior = JsonDocument.Parse(File.ReadAllText(chestFile));
            secondaryChestId = prior.RootElement.GetProperty("secondary").GetProperty("id").GetInt32();
        }
    }

    private static TSPlayer ResolvePlayer(string selector)
    {
        var matches = selector.StartsWith("tsi:", StringComparison.Ordinal)
            ? TSPlayer.FindByNameOrID(selector)
            : TShock.Players.Where(p => p != null && p.Name == selector).ToList();
        Require(matches.Count == 1, "Provide one exact connected player name or tsi:index.");
        var player = matches[0];
        Require(player.RealPlayer && player.Active && player.TPlayer.active && player.IsLoggedIn && player.Account != null, "The target must be a connected, authenticated real player; scaffold never logs a player in.");
        return player;
    }

    private void Prepare(TSPlayer player)
    {
        Require(room == null, "A scaffold room is already recorded for this world. Use qa_status; do not clear/recreate a room containing client test changes.");
        Require(Math.Abs(player.TileX - Main.spawnTileX) <= 120 && Math.Abs(player.TileY - Main.spawnTileY) <= 80, "Return near world spawn before preparing the room.");
        int left = player.TileX - 20;
        int floor = (int)Math.Ceiling(player.TPlayer.Bottom.Y / 16f) + 1;
        int top = floor - 12;
        Require(left >= 20 && left + 40 < Main.maxTilesX - 20 && top >= 20 && floor + 2 < Main.maxTilesY - 20, "Room would exceed safe world bounds.");
        bool InBounds(int x, int y) => x >= left - 2 && x <= left + 41 && y >= top - 2 && y <= floor + 2;
        Require(!Main.chest.Any(c => c != null && InBounds(c.x, c.y)), "Room intersects an existing chest; no terrain was changed.");
        Require(!Main.sign.Any(s => s != null && InBounds(s.x, s.y)), "Room intersects an existing sign; no terrain was changed.");
        Require(!TileEntity.ByPosition.Keys.Any(p => InBounds(p.X, p.Y)), "Room intersects an existing tile entity; no terrain was changed.");
        Require(!TShock.Regions.Regions.Any(r => r.Area.Intersects(new Microsoft.Xna.Framework.Rectangle(left - 2, top - 2, 44, 17))), "Room intersects an existing region; no terrain was changed.");

        room = new Room(left, top, 40, 12, floor, left + 12, floor - 1, -1, -1, -1,
            "qa_protected_" + Main.worldID, left + 30, floor - 4, 8, 4, player.Name, "preparing");
        SaveRoom(); // Records the intended bounds even if a later placement fails.
        Record("prepare-begin", room);
        for (int x = left; x < left + 40; x++)
            for (int y = top; y <= floor; y++)
            {
                var tile = Main.tile[x, y];
                tile.ClearEverything();
                // A flat floor, roof and side walls; three-tile high entrance on the left.
                bool boundary = y == floor || y == top || x == left + 39 || (x == left && y < floor - 3);
                if (boundary)
                {
                    tile.active(true);
                    tile.type = (ushort)(y == floor && x >= left + 30 && x <= left + 38 ? TileID.RedBrick : TileID.Stone);
                }
            }
        for (int x = left; x < left + 40; x++)
            for (int y = top; y <= floor; y++) WorldGen.SquareTileFrame(x, y, true);

        Require(WorldGen.PlaceTile(left + 12, floor - 1, TileID.WorkBenches, true, false, player.Index, 0), "Workbench placement failed; partial scaffold bounds remain recorded.");
        int chestId = WorldGen.PlaceChest(left + 24, floor - 1, TileID.Containers, false, 0);
        Require(chestId >= 0 && chestId < Main.chest.Length && Main.chest[chestId] != null, "Chest placement failed; partial scaffold bounds remain recorded.");
        var chest = Main.chest[chestId];
        room = room with { ChestId = chestId, ChestX = chest.x, ChestY = chest.y };
        Require(TShock.Regions.AddRegion(room.RegionX, room.RegionY, room.RegionWidth, room.RegionHeight, room.RegionName, "qa-scaffold-owner", Main.worldID.ToString(), 1000000), "Protected region creation failed.");
        var region = TShock.Regions.GetRegionByName(room.RegionName);
        Require(region != null && region.AllowedIDs.Count == 0 && region.AllowedGroups.Count == 0, "Protected region did not retain an empty allow-list.");
        room = room with { State = "prepared" };
        SaveRoom();
        TSPlayer.All.SendTileRect((short)(left - 1), (short)(top - 1), 42, 15);
        // Match the normal server placement response so connected clients create the chest object.
        // Packet 34 uses the placement origin; PlaceChestDirect subtracts one from its Y.
        TSPlayer.All.SendData(PacketTypes.PlaceChest, number: 0, number2: left + 24,
            number3: floor - 1, number4: 0, number5: chestId);
        player.GiveItem(ItemID.Wood, 100);
        player.GiveItem(ItemID.Gel, 30);
        player.GiveItem(ItemID.DirtBlock, 100);
        Record("prepare-complete", new { room, materials = new[] { new { type = ItemID.Wood, stack = 100 }, new { type = ItemID.Gel, stack = 30 }, new { type = ItemID.DirtBlock, stack = 100 } }, giveItemsDirectly = TShock.Config.Settings.GiveItemsDirectly, canBuildUnprotected = TShock.Regions.CanBuild(left + 18, floor - 1, player), canBuildProtected = TShock.Regions.CanBuild(left + 34, floor, player), note = "Setup uses authorized server APIs; it does not establish that a client action passed." });
        string description = $"QA room x={left}..{left + 39}, y={top}..{floor}; workbench=({left + 12},{floor - 1}); chest id={chestId} at ({chest.x},{chest.y}); protected RED BRICK floor x={left + 30}..{left + 38}, y={floor}; region={room.RegionName}.";
        TSPlayer.Server.SendInfoMessage(description);
        player.SendInfoMessage(description);
        if (player.HasPermission(Permissions.editregion))
            TSPlayer.Server.SendWarningMessage("Target currently has tshock.world.editregion; protected-area rejection cannot be validated with this player's present group. No permissions were changed.");
    }

    private void SaveRoom() => File.WriteAllText(Path.Combine(root!, "qa-scaffold-room.json"), JsonSerializer.Serialize(room, jsonOptions));

    private void PrepareNpcs(TSPlayer player)
    {
        Require(room is { State: "prepared" }, "Run qa_prepare first; qa_npcs uses the recorded disposable room.");
        Require(!player.HasPermission(Permissions.editregion) && !player.HasPermission(Permissions.bypassssc)
            && !player.HasPermission("anticheat.bypass"), "The target must have ordinary permissions without region, SSC or AntiCheat bypass.");
        Require(Math.Abs(player.TileX - room!.WorkbenchX) <= 60 && Math.Abs(player.TileY - room.FloorY) <= 40,
            "Target must be near the recorded test room.");
        var created = new List<object>();
        foreach (var target in new[] { (Type: NPCID.Merchant, X: room.Left + 7), (Type: NPCID.GoblinTinkerer, X: room.Left + 18) })
        {
            // Idempotent setup does not duplicate or move an existing NPC, even within the disposable map.
            var existing = Main.npc.FirstOrDefault(n => n is { active: true } && n.type == target.Type);
            if (existing is not null)
            {
                created.Add(new { type = target.Type, index = existing.whoAmI, created = false,
                    tileX = (int)(existing.position.X / 16), tileY = (int)(existing.position.Y / 16) });
                continue;
            }
            int index = NPC.NewNPC(new EntitySource_DebugCommand(), target.X * 16,
                (room.FloorY - 1) * 16, target.Type);
            Require(index >= 0 && index < Main.npc.Length && Main.npc[index].active && Main.npc[index].type == target.Type,
                "NPC creation failed; already-created fixture NPCs remain recorded.");
            var npc = Main.npc[index];
            TrackM7NpcFixture(npc);
            npc.homeTileX = target.X; npc.homeTileY = room.FloorY - 1; npc.homeless = false; npc.netUpdate = true;
            TSPlayer.All.SendData(PacketTypes.NpcUpdate, number: index);
            created.Add(new { type = target.Type, index, created = true, tileX = target.X, tileY = room.FloorY - 1 });
        }
        string materialMarker = Path.Combine(root!, "qa-npc-materials-issued.json");
        bool supplied = !File.Exists(materialMarker);
        if (supplied)
        {
            // Mark first: repeated setup cannot silently mint a second batch after a partial failure.
            File.WriteAllText(materialMarker, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow,
                accountId = player.Account.ID, player = player.Name, note = "One-time setup materials; not client acceptance." }, jsonOptions));
            player.GiveItem(ItemID.GoldCoin, 50);
            player.GiveItem(ItemID.WoodenBow, 1);
            player.GiveItem(ItemID.WoodenArrow, 100);
            player.GiveItem(ItemID.Campfire, 1);
        }
        Record("qa-npcs-prepared", new { npcFixtures = created, supplied, accountId = player.Account.ID,
            group = player.Group.Name, note = "Console-only isolated setup. Buying, selling, buyback, reforge and campfire placement still require actual ordinary-client UI actions." });
        TSPlayer.Server.SendInfoMessage($"QA NPC fixtures ready; merchant/goblin recorded; one-time materials issued={supplied}. No permissions or world progress flags changed.");
    }

    private void PrepareChests(TSPlayer player)
    {
        Require(room is { State: "prepared" }, "Run qa_prepare first.");
        Require(!player.HasPermission(Permissions.editregion) && !player.HasPermission(Permissions.bypassssc)
            && !player.HasPermission("anticheat.bypass"), "The target must have ordinary permissions without protection bypass.");
        var primary = room!.ChestId >= 0 && room.ChestId < Main.chest.Length ? Main.chest[room.ChestId] : null;
        Require(primary != null && primary.x == room.ChestX && primary.y == room.ChestY, "Recorded primary chest no longer matches; no placement attempted.");
        // The primary's left neighbour is occupied by the fixture's platform/sign.
        // Use the empty right-hand footprint inside the disposable room.
        int left = primary!.x + 3, top = primary.y;
        Require(left > room.Left && left + 1 < room.Left + room.Width && top >= room.Top && top + 1 < room.FloorY,
            "Second chest would be outside the recorded room.");
        Require(player.IsInRange(left, top), "Target must be near the recorded chest pair.");
        if (secondaryChestId < 0)
        {
            Require(!Main.chest.Any(c => c != null && Math.Abs(c.x - left) < 2 && Math.Abs(c.y - top) < 2),
                "Secondary placement intersects an existing chest.");
            for (int x = left; x < left + 2; x++)
                for (int y = top; y < top + 2; y++)
                    Require(!Main.tile[x, y].active(), "Second chest footprint is occupied; no terrain was cleared.");
            int created = WorldGen.PlaceChest(left, top + 1, TileID.Containers, false, 0);
            Require(created >= 0 && created < Main.chest.Length && Main.chest[created] != null, "Second chest creation failed.");
            secondaryChestId = created;
            TSPlayer.All.SendTileRect((short)(left - 1), (short)(top - 1), 4, 4);
            TSPlayer.All.SendData(PacketTypes.PlaceChest, number: 0, number2: left, number3: top + 1, number4: 0, number5: created);
        }
        var secondary = secondaryChestId < Main.chest.Length ? Main.chest[secondaryChestId] : null;
        Require(secondary != null && secondary.x == left && secondary.y == top, "Recorded secondary chest no longer matches; no replacement attempted.");
        var payload = new { utc = DateTimeOffset.UtcNow, worldId = Main.worldID, primary = DescribeChest(room.ChestId),
            secondary = DescribeChest(secondaryChestId), note = "Disposable fixture only; operations must enter through real client/network hooks. No chest items changed." };
        string path = Path.Combine(root!, "qa-chests.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, jsonOptions));
        Record("qa-chests-prepared", payload);
        TSPlayer.Server.SendInfoMessage($"QA chest pair primary={room.ChestId}, secondary={secondaryChestId}; manifest={path}");
    }

    private static object? DescribeChest(int id)
    {
        var chest = id >= 0 && id < Main.chest.Length ? Main.chest[id] : null;
        if (chest == null) return null;
        var items = chest.item.Select((item, slot) => new { slot, item.type, item.stack, item.prefix })
            .Where(x => x.type != 0 && x.stack != 0).ToArray();
        var state = new { id, chest.x, chest.y, chest.maxItems, chest.name, items };
        string currentStateSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))));
        return new { id, chest.x, chest.y, chest.maxItems, chest.name, items, currentStateSha256,
            versionKind = "current-content-sha256-not-monotonic-event-version" };
    }

    private void ArmCancellation(TSPlayer player, int packetId)
    {
        Require(packetId is 100 or 153 or 50, "Only packets 100, 153 and 50 are accepted by this one-shot fixture.");
        forcedCancellation = new(player, player.Account.ID, packetId,
            System.Diagnostics.Stopwatch.GetTimestamp() + 5 * System.Diagnostics.Stopwatch.Frequency);
        Record("cancellation-armed", new { playerIndex = player.Index, accountId = player.Account.ID,
            packetId, ttlSeconds = 5, hookPriority = 2000, note = "Fixture cancellation only; AntiCheat must prove its own independent predicate." });
        TSPlayer.Server.SendInfoMessage($"QA_CANCEL_ARMED accountId={player.Account.ID} slot={player.Index} packetId={packetId} ttlSeconds=5");
    }

    private void ConfigureRawCapture(string[] arguments)
    {
        Require(arguments.Length is > 0 and <= 20, "Use qa_capture off or at most 20 allowed game packet IDs.");
        int[] next = [];
        if (!(arguments.Length == 1 && arguments[0] == "off"))
        {
            next = new int[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                Require(int.TryParse(arguments[i], out int packet) && packet is
                    5 or 13 or 16 or 21 or 22 or 23 or 27 or 28 or 29 or 31 or 32 or 42 or 50 or 53 or 55 or 65 or 85 or 96 or 100 or 117 or 118 or 147 or 153 or 155,
                    "Only fixed experiment game packets may be captured; authentication/chat/text are excluded.");
                next[i] = packet;
            }
            next = next.Distinct().Order().ToArray();
        }
        var previous = Volatile.Read(ref rawPacketIds);
        Record("raw-capture-selection", new { previous, selected = next,
            note = "Passive incoming body capture only; immutable selector, original queue/disk limits retained." });
        Volatile.Write(ref rawPacketIds, next);
        TSPlayer.Server.SendInfoMessage("QA raw packet capture: " + string.Join(',', next));
    }

    private void OnRawData(GetDataEventArgs args)
    {
        int packetId = (int)args.MsgID;
        // Bounded passive capture of experiment messages only. Authentication, chat and
        // arbitrary player text are deliberately absent; this does not change Handled.
        if (recording && Array.IndexOf(Volatile.Read(ref rawPacketIds), packetId) >= 0)
        {
            int size = args.Length - 1;
            byte[]? buffer = args.Msg?.readBuffer;
            if (buffer != null && size >= 0 && size <= 512 && args.Index >= 0 && args.Index <= buffer.Length - size)
                Observe("raw-client-experiment-frame", new { packetId, playerIndex = args.Msg!.whoAmI,
                    payloadBytes = size, payloadHex = Convert.ToHexString(buffer.AsSpan(args.Index, size)),
                    handledObserved = args.Handled, hookPriority = 2000 });
        }
        if (args.Msg is null) return;
        var token = forcedCancellation;
        if (token is null) return;
        if (System.Diagnostics.Stopwatch.GetTimestamp() > token.ExpiresAt)
        { forcedCancellation = null; return; }
        if ((int)args.MsgID != token.PacketId || args.Msg.whoAmI != token.Player.Index) return;
        forcedCancellation = null; // consume before any logging, even if a stale session is detected
        if (!ReferenceEquals(TShock.Players[token.Player.Index], token.Player) || !token.Player.Active
            || !token.Player.IsLoggedIn || token.Player.Account?.ID != token.AccountId) return;
        args.Handled = true;
        Observe("fixture-pre-core-cancellation", new { packetId = token.PacketId, playerIndex = token.Player.Index,
            accountId = token.AccountId, handled = true, hookPriority = 2000 });
        TSPlayer.Server.SendInfoMessage($"QA_CANCEL_APPLIED accountId={token.AccountId} slot={token.Player.Index} packetId={token.PacketId} handled=True");
    }

    private void ObserveState()
    {
        if (!recording || journal == null) return;
        try
        {
            int count = 0;
            while (count++ < 1000 && observations.TryDequeue(out var observation))
            { Interlocked.Decrement(ref observationCount); Append(observation); }
            if (DateTimeOffset.UtcNow - lastSnapshot >= TimeSpan.FromSeconds(2)) Snapshot("periodic-after-server-update", false);
        }
        catch (Exception ex)
        {
            recording = false;
            TShock.Log.Error($"GameplayScaffold observation failed; recording stopped: {ex}");
            TSPlayer.Server.SendErrorMessage($"GameplayScaffold recording stopped: {ex.Message}");
        }
    }

    private void Snapshot(string label, bool writeFile)
    {
        lastSnapshot = DateTimeOffset.UtcNow;
        var players = TShock.Players.Where(p => p != null && p.Active && p.RealPlayer).Select(p => new
        {
            p.Index, p.Name, p.IsLoggedIn, accountName = p.Account?.Name, group = p.Group?.Name,
            tileX = p.TileX, tileY = p.TileY, health = p.TPlayer.statLife, dead = p.TPlayer.dead,
            rawLifeMaximum = p.TPlayer.statLifeMax, effectiveLifeMaximum = p.TPlayer.statLifeMax2,
            mana = p.TPlayer.statMana, rawManaMaximum = p.TPlayer.statManaMax, effectiveManaMaximum = p.TPlayer.statManaMax2,
            p.ActiveChest, p.IsDisabledForSSC, p.IsDisabledPendingTrashRemoval,
            inventory = p.TPlayer.inventory.Select((item, slot) => new { slot, item.type, item.stack, item.prefix, item.favorited }).Where(i => i.type != 0 && i.stack != 0).ToArray(),
            equipmentAndPersonalStorage = DescribeM7EquipmentAndStorage(p.TPlayer),
            regionCanBuild = room == null || !p.IsLoggedIn || p.Account == null ? (bool?)null : TShock.Regions.CanBuild(room.RegionX + 2, room.FloorY, p)
        }).ToArray();
        var chest = room != null && room.ChestId >= 0 && room.ChestId < Main.chest.Length ? Main.chest[room.ChestId] : null;
        var payload = new
        {
            label, Main.worldName, Main.worldID, worldPath = Main.worldPathName, ssc = Main.ServerSideCharacter,
            players, room,
            npcs = Main.npc.Take(200).Select((npc, index) => new { index, type = npc?.type ?? 0,
                active = npc?.active ?? false, life = npc?.life ?? 0, generation = npc?.generation ?? 0,
                position = new { x = npc?.position.X ?? 0, y = npc?.position.Y ?? 0 },
                velocity = new { x = npc?.velocity.X ?? 0, y = npc?.velocity.Y ?? 0 } }).ToArray(),
            recordingHealth = new { queued = observationCount, dropped = observationDropped, journalBytes,
                maximumJournalBytes = MaximumJournalBytes, maximumQueue = MaximumObservationQueue, journalLimitReported },
            chestPair = new { primary = room == null ? null : DescribeChest(room.ChestId), secondary = DescribeChest(secondaryChestId) },
            toolTargetChests = Enumerable.Range(0, 100).Select(DescribeChest).ToArray(),
            chest = chest == null ? null : new { chest.x, chest.y, chest.name, items = chest.item.Select((item, slot) => new { slot, item.type, item.stack, item.prefix }).Where(i => i.type != 0 && i.stack != 0).ToArray() },
            protectedFloor = room == null ? null : Enumerable.Range(room.RegionX, room.RegionWidth + 1).Select(x => new { x, y = room.FloorY, active = Main.tile[x, room.FloorY].active(), type = Main.tile[x, room.FloorY].type }).ToArray(),
            note = "Server state observation only. Correlate timestamps and event payloads with actual client UI actions; receipt/Handled flags alone do not prove success or rejection."
        };
        Record("server-state", payload);
        if (writeFile)
        {
            var path = Path.Combine(output!, statusFiles++ < 128
                ? $"gameplay-status-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.json" : "gameplay-status-latest.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, payload }, jsonOptions));
            File.WriteAllText(Path.Combine(output!, "gameplay-status-latest.json"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, payload }, jsonOptions));
            TSPlayer.Server.SendInfoMessage($"QA status: {path}; players={players.Length}; journal={journal}");
            foreach (var player in players) TSPlayer.Server.SendInfoMessage($"[{player.Index}] {player.Name}: loggedIn={player.IsLoggedIn}, group={player.group}, tile=({player.tileX},{player.tileY}), health={player.health}, chest={player.ActiveChest}, protectedCanBuild={player.regionCanBuild}");
        }
    }

    private void OnPlayerSlot(object? sender, GetDataHandlers.PlayerSlotEventArgs e) => Observe("player-slot-packet", new { playerIndex = e.Player.Index, playerName = e.Player.Name, e.Slot, e.Type, e.Stack, e.Prefix, e.Favorited, handledObserved = e.Handled });
    private void OnTileEdit(object? sender, GetDataHandlers.TileEditEventArgs e) => Observe("tile-edit-packet", new { playerIndex = e.Player.Index, playerName = e.Player.Name, e.X, e.Y, action = e.Action.ToString(), e.EditData, e.Style, handledObserved = e.Handled });
    private void OnChestOpen(object? sender, GetDataHandlers.ChestOpenEventArgs e) => Observe("chest-open-packet", new { playerIndex = e.Player.Index, playerName = e.Player.Name, e.X, e.Y, handledObserved = e.Handled });
    private void OnChestItem(object? sender, GetDataHandlers.ChestItemEventArgs e) => Observe("chest-item-packet", new { playerIndex = e.Player.Index, playerName = e.Player.Name, e.ID, e.Slot, e.Type, e.Stacks, e.Prefix, handledObserved = e.Handled });
    private void OnPermission(PlayerPermissionEventArgs e)
    {
        if (e.Permission == Permissions.canbuild || e.Permission == Permissions.editregion || e.Permission == Permissions.bypassssc)
            Observe("permission-hook-observed", new { playerIndex = e.Player.Index, playerName = e.Player.Name, e.Permission, hookResultBeforeGroupFallback = e.Result.ToString() });
    }

    private void Observe(string kind, object payload)
    {
        if (!recording) return;
        if (Interlocked.Increment(ref observationCount) > MaximumObservationQueue)
        {
            Interlocked.Decrement(ref observationCount);
            Interlocked.Increment(ref observationDropped);
            return; // bounded drop policy; never alter a player's action because QA evidence overflowed
        }
        observations.Enqueue(new { utc = DateTimeOffset.UtcNow, kind, payload });
    }
    private void Record(string kind, object payload) => Append(new { utc = DateTimeOffset.UtcNow, sequence = ++sequence, kind, mainThreadId = Environment.CurrentManagedThreadId, payload });
    private void Append(object entry)
    {
        string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        int bytes = System.Text.Encoding.UTF8.GetByteCount(line);
        if (journalBytes > MaximumJournalBytes - bytes)
        {
            recording = false;
            Interlocked.Increment(ref observationDropped);
            if (!journalLimitReported)
            {
                journalLimitReported = true;
                TSPlayer.Server.SendWarningMessage("QA evidence journal reached its 64 MiB session limit. Recording stopped; qa_status still writes a bounded latest snapshot. Gameplay is unaffected.");
            }
            return;
        }
        File.AppendAllText(journal!, line);
        journalBytes += bytes;
    }

    private static bool IsUnder(string path, string parent)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return full.Equals(prefix, StringComparison.OrdinalIgnoreCase) || full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed record Room(int Left, int Top, int Width, int Height, int FloorY, int WorkbenchX, int WorkbenchY,
        int ChestId, int ChestX, int ChestY, string RegionName, int RegionX, int RegionY, int RegionWidth, int RegionHeight, string PreparedFor, string State);
    private sealed record PendingCancellation(TSPlayer Player, int AccountId, int PacketId, long ExpiresAt);
}
