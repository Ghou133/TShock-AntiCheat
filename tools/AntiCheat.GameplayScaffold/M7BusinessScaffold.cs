using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

// Console preparation and passive measurement only; never included in the product package.
public sealed partial class GameplayScaffold
{
    private sealed record M7PumpFixture(string Label, int InX, int OutX, int Y, int SwitchX, int SwitchY);
    private M7PumpFixture[] m7Pumps = [];
    private int m7PumpActor = -1;
    private readonly List<object> m7PumpEffects = new(128);
    private int m7PumpEffectsDropped;
    private int m7LoadoutActor = -1;

    private void InstallM7Business()
    {
        HookEvents.Terraria.Wiring.XferWater += ObserveM7Transfer;
        HookEvents.Terraria.WorldGen.SquareTileFrame += ObserveM7PumpFrame;
    }
    private void DisposeM7Business()
    {
        HookEvents.Terraria.Wiring.XferWater -= ObserveM7Transfer;
        HookEvents.Terraria.WorldGen.SquareTileFrame -= ObserveM7PumpFrame;
    }

    private void PrepareM7Pumps(string[] arguments)
    {
        Require(arguments.Length == 1, "Use qa_m7_pumps <exact authenticated player>.");
        Require(room is null && m7Pumps.Length == 0, "Pump fixture requires a fresh owned room; existing test changes are retained.");
        var actor = ResolvePlayer(arguments[0]);
        Require(!actor.HasPermission(Permissions.editregion) && !actor.HasPermission(Permissions.bypassssc) &&
            !actor.HasPermission("anticheat.bypass"), "Ordinary account without bypass is required.");
        Prepare(actor);
        var owned = room!;
        var planned = new[] {
            new M7PumpFixture("allowed", owned.Left + 6, owned.Left + 12, owned.Top + 3, owned.Left + 3, owned.Top + 4),
            new M7PumpFixture("protected", owned.Left + 6, owned.Left + 12, owned.Top + 7, owned.Left + 3, owned.Top + 8) };
        var cells = planned.SelectMany(f => Enumerable.Range(f.SwitchX, f.OutX + 3 - f.SwitchX)
            .SelectMany(x => Enumerable.Range(f.Y - 1, 4).Select(y => (X: x, Y: y)))).Distinct().ToArray();
        Require(cells.All(p => p.X > owned.Left && p.X < owned.Left + owned.Width - 1 && p.Y > owned.Top && p.Y < owned.FloorY &&
            Main.tile[p.X, p.Y] is { } t && !t.active() && !t.wire() && !t.wire2() && !t.wire3() && !t.wire4() &&
            !t.actuator() && t.liquid == 0 && actor.HasBuildPermission(p.X, p.Y, false)), "Owned pump cells must be empty and allowed.");
        string regionName = "qa_m7_pump_" + Main.worldID;
        Require(TShock.Regions.GetRegionByName(regionName) is null, "Existing pump region is retained.");
        var denied = planned[1];
        Require(TShock.Regions.AddRegion(denied.OutX + 2, denied.Y + 2, 1, 1, regionName,
            "qa-scaffold-owner", Main.worldID.ToString(), 1000000), "Could not create the owned frame-neighbor region.");
        foreach (var fixture in planned)
        {
            foreach (var (origin, type) in new[] { (fixture.InX, TileID.InletPump), (fixture.OutX, TileID.OutletPump) })
            {
                // Native liquid is kept inside a 2x2 pump chamber by solid glass; later
                // snapshots can measure the real transfer without a fixture tick hold.
                for (int x = origin - 1; x <= origin + 2; x++) for (int y = fixture.Y - 1; y <= fixture.Y + 2; y++)
                {
                    var tile = Main.tile[x, y]; tile.active(true);
                    bool pump = x >= origin && x <= origin + 1 && y >= fixture.Y && y <= fixture.Y + 1;
                    tile.type = (ushort)(pump ? type : TileID.Glass);
                    tile.frameX = pump ? (short)((x - origin) * 18) : (short)0;
                    tile.frameY = pump ? (short)((y - fixture.Y) * 18) : (short)0;
                }
            }
            Main.tile[fixture.InX, fixture.Y + 1].liquid = 200;
            Main.tile[fixture.InX, fixture.Y + 1].liquidType(0);
            var trigger = Main.tile[fixture.SwitchX, fixture.SwitchY]; trigger.active(true); trigger.type = TileID.PressurePlates;
            for (int x = fixture.SwitchX; x <= fixture.OutX; x++) Main.tile[x, fixture.SwitchY].wire(true);
        }
        m7Pumps = planned; m7PumpActor = actor.Index;
        Record("m7-pump-fixtures-prepared", new { fixtureArtificial = true, regionName, planned,
            note = "One-time authorized room setup. Later switches arrive over TCP59; no direct XferWater call or liquid tick hold is used by the fixture." });
        TSPlayer.All.SendTileRect((short)(owned.Left + 2), (short)(owned.Top + 1), 14, 10);
        WriteM7PumpState();
    }

    private void PumpEffect(object effect)
    {
        if (m7PumpEffects.Count < 128) m7PumpEffects.Add(effect);
        else if (m7PumpEffectsDropped < int.MaxValue) m7PumpEffectsDropped++;
    }

    private void ObserveM7Transfer(object? sender, HookEvents.Terraria.Wiring.XferWaterEventArgs args)
    {
        if (!recording || Wiring._numInPump is < 1 or > 20) return;
        var fixture = m7Pumps.FirstOrDefault(f => Wiring._inPumpX.Take(Wiring._numInPump)
            .Zip(Wiring._inPumpY.Take(Wiring._numInPump)).Any(p => p.First >= f.InX && p.First <= f.InX + 1 && p.Second >= f.Y && p.Second <= f.Y + 1));
        if (fixture is not null) PumpEffect(new { utc = DateTimeOffset.UtcNow, kind = "native-pump-transfer-entry", fixture.Label,
            args.ContinueExecution, source = Wiring.CurrentUser, inputs = Wiring._numInPump, outputs = Wiring._numOutPump,
            inputBefore = PumpLiquid(fixture.InX, fixture.Y), outputBefore = PumpLiquid(fixture.OutX, fixture.Y) });
    }

    private void ObserveM7PumpFrame(object? sender, HookEvents.Terraria.WorldGen.SquareTileFrameEventArgs args)
    {
        if (!recording) return;
        var fixture = m7Pumps.FirstOrDefault(f => args.j >= f.Y && args.j <= f.Y + 1 &&
            (args.i >= f.InX && args.i <= f.InX + 1 || args.i >= f.OutX && args.i <= f.OutX + 1));
        if (fixture is not null) PumpEffect(new { utc = DateTimeOffset.UtcNow, kind = "native-pump-frame-entry", fixture.Label,
            args.i, args.j, args.ContinueExecution, source = Wiring.CurrentUser });
    }

    private static int PumpLiquid(int x, int y) => Enumerable.Range(0, 2).SelectMany(dx => Enumerable.Range(0, 2)
        .Select(dy => (int)Main.tile[x + dx, y + dy].liquid)).Sum();

    private void WriteM7PumpState()
    {
        Require(m7Pumps.Length >= 2 && (uint)m7PumpActor < Main.maxPlayers, "Prepare M7 pumps first.");
        var actor = TShock.Players[m7PumpActor];
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_wiringExecution", PrivateM5)!.GetValue(plugin);
        object? Counter(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, guardPresent = guard is not null,
            contractHealthy = Counter("PumpContractHealthy"), allowed = Counter("PumpAllowed"),
            permissionBlocked = Counter("PumpPermissionBlocked"), budgetBlocked = Counter("PumpBudgetBlocked"),
            unknownActor = Counter("PumpUnknownActor"), workUnits = Counter("PumpAdmittedWorkUnits"),
            actor = new { actor?.Index, account = actor?.Account?.ID, actor?.IsLoggedIn,
                bypass = actor?.HasPermission("anticheat.bypass"), sscBypass = actor?.HasPermission(Permissions.bypassssc) },
            fixtures = m7Pumps.Select(f => new { f.Label, f.InX, f.OutX, f.Y, f.SwitchX, f.SwitchY,
                inputLiquid = PumpLiquid(f.InX, f.Y), outputLiquid = PumpLiquid(f.OutX, f.Y),
                switchAllowed = actor?.HasBuildPermission(f.SwitchX, f.SwitchY, false),
                frameNeighborAllowed = actor?.HasBuildPermission(f.OutX + 2, f.Y + 2, false) }).ToArray(),
            effects = m7PumpEffects.ToArray(), effectsDropped = m7PumpEffectsDropped,
            nativeCurrentUser = Wiring.CurrentUser, nativeRunning = Wiring.running };
        File.WriteAllText(Path.Combine(output!, "m7-pump-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage("QA_M7_PUMP_STATE " + JsonSerializer.Serialize(payload));
    }

    private void PrepareM7Loadouts(string[] arguments)
    {
        Require(arguments.Length == 1 && m7LoadoutActor == -1, "Use qa_m7_loadouts <exact authenticated player> once in this isolated run.");
        var actor = ResolvePlayer(arguments[0]); var player = actor.TPlayer;
        Require(room is not null && actor.Index == m7PumpActor && actor.IsLoggedIn && actor.PlayerData is not null &&
            !actor.HasPermission(Permissions.bypassssc) && !actor.HasPermission("anticheat.bypass"), "Prepared ordinary pump actor is required.");
        Require(player.CurrentLoadoutIndex == 0 && player.armor.All(i => i.IsAir) && player.dye.All(i => i.IsAir) &&
            player.Loadouts.All(l => l.Armor.All(i => i.IsAir) && l.Dye.All(i => i.IsAir)), "Existing equipment is retained; fixture requires empty equipment pages.");
        player.armor[0].SetDefaults(ItemID.WoodHelmet);
        player.armor[13].SetDefaults(ItemID.AvengerEmblem); // Vanity never supplies its functional damage.
        player.armor[8].SetDefaults(ItemID.AvengerEmblem); // Disabled slot in the owned normal world.
        player.Loadouts[1].Armor[0].SetDefaults(ItemID.CopperHelmet);
        player.Loadouts[1].Armor[3].SetDefaults(ItemID.AvengerEmblem);
        player.Loadouts[2].Armor[0].SetDefaults(ItemID.IronHelmet);
        actor.PlayerData!.CopyCharacter(actor);
        m7LoadoutActor = actor.Index;
        Record("m7-loadout-fixture-prepared", new { fixtureArtificial = true, actor.Index, account = actor.Account.ID,
            note = "One-time empty-page setup only. All following swaps and SSC writes originate from incoming TCP147; no loadout switch or tick stat mutation in the fixture." });
        WriteM7LoadoutState();
    }

    private void WriteM7LoadoutState()
    {
        Require((uint)m7LoadoutActor < Main.maxPlayers, "Prepare M7 loadouts first.");
        var actor = TShock.Players[m7LoadoutActor]; Require(actor?.PlayerData is not null && actor.IsLoggedIn, "Original authenticated actor must remain present.");
        var player = actor!.TPlayer; var plugin = M5Plugin();
        var inventory = plugin.GetType().GetField("_inventory", PrivateM5)!.GetValue(plugin);
        var observer = inventory?.GetType().GetProperty("LoadoutTransactions")?.GetValue(inventory);
        var binding = ((Array)plugin.GetType().GetField("_bindings", PrivateM5)!.GetValue(plugin)!).GetValue(actor.Index);
        var session = binding?.GetType().GetProperty("Key")?.GetValue(binding);
        object? Counter(string name) => observer?.GetType().GetProperty(name)?.GetValue(observer);
        var completion = session is not null ? observer?.GetType().GetMethod("Capture")?.Invoke(observer, [session]) : null;
        var executions = inventory?.GetType().GetProperty("EquipmentExecutions")?.GetValue(inventory);
        var calculation = session is not null ? executions?.GetType().GetMethod("Capture")?.Invoke(executions, [session]) : null;
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, actualGui = false,
            actor = new { actor.Index, account = actor.Account.ID, actor.IsLoggedIn, actor.HasSentInventory, actor.ReceivedInfo,
                bypass = actor.HasPermission("anticheat.bypass"), sscBypass = actor.HasPermission(Permissions.bypassssc) },
            observerPresent = observer is not null, observerHealthy = Counter("Healthy"), observed = Counter("Observed"),
            completed = Counter("Completed"), mismatched = Counter("Mismatched"), completion,
            equipmentExecutionHealthy = executions?.GetType().GetProperty("Healthy")?.GetValue(executions), calculation,
            malformedBlocked = plugin.GetType().GetField("_blockedMalformed", PrivateM5)!.GetValue(plugin),
            player.CurrentLoadoutIndex, player.statDefense, player.meleeDamage, helmetDefense = player.armor[0].defense,
            armor = player.armor.Select(i => new { i.type, i.stack, i.prefix }).ToArray(),
            dye = player.dye.Select(i => new { i.type, i.stack, i.prefix }).ToArray(),
            loadouts = player.Loadouts.Select(l => new { armor = l.Armor.Select(i => new { i.type, i.stack, i.prefix }).ToArray(),
                dye = l.Dye.Select(i => new { i.type, i.stack, i.prefix }).ToArray() }).ToArray(),
            visibility = player.hideVisibleAccessory.ToArray(),
            sscArmor = new[] { NetItem.ArmorIndex.Item1, NetItem.Loadout1Armor.Item1, NetItem.Loadout2Armor.Item1, NetItem.Loadout3Armor.Item1 }
                .Select(start => actor.PlayerData.inventory.Skip(start).Take(20).Select(i => new { i.NetId, i.Stack, i.PrefixId }).ToArray()).ToArray() };
        File.WriteAllText(Path.Combine(output!, "m7-loadout-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
        TSPlayer.Server.SendInfoMessage("QA_M7_LOADOUT_STATE " + JsonSerializer.Serialize(payload));
    }
}
