using System.Buffers.Binary;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private sealed class M10SummonActor(TSPlayer actor)
    {
        public readonly TSPlayer Actor = actor;
        public long RawRequests, Send27, Send29;
        public object? LastRaw;
    }
    private readonly Dictionary<int, M10SummonActor> m10SummonActors = new(2);

    private void PrepareM10Summon(string[] names)
    {
        Require(names.Length is > 0 and <= 2 && m10SummonActors.Count == 0,
            "Use qa_m10_summon <one or two ordinary authenticated players>, once per owned run.");
        foreach (string name in names)
        {
            var actor = ResolvePlayer(name);
            Require(actor.IsLoggedIn && actor.Account is not null && actor.HasSentInventory && actor.tempGroup is null &&
                !actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc) &&
                !actor.HasPermission("compatibility.qa.console"), "Ordinary SSC account without bypass required.");
            m10SummonActors.Add(actor.Index, new(actor));
        }
        ServerApi.Hooks.NetGetData.Register(this, ObserveM10SummonRaw, -1001);
        HookEvents.Terraria.NetMessage.SendData += ObserveM10SummonSend;
        Record("m10-summon-witness-prepared", new { actors = names, fixtureArtificial = false,
            source = "passive current-account raw27/29 and native SendData witness; no gameplay mutation" });
        WriteM10SummonState();
    }

    private void SupplyM10SummonGuiMaterials(string[] names)
    {
        Require(names.Length == 1, "Use qa_m10_summon_materials <prepared ordinary actor>.");
        var actor = ResolvePlayer(names[0]);
        Require(m10SummonActors.TryGetValue(actor.Index, out var witness) && ReferenceEquals(witness.Actor, actor) &&
            actor.IsLoggedIn && !actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc),
            "Current prepared ordinary actor required.");
        string marker = Path.Combine(root!, "qa-m10-summon-materials-" + actor.Index + ".json");
        Require(!File.Exists(marker), "The one-time summon material batch was already issued.");
        var materials = new[] { (Type: ItemID.SlimeStaff, Stack: 1), (Type: ItemID.SummoningPotion, Stack: 2),
            (Type: ItemID.WoodHelmet, Stack: 1), (Type: ItemID.WoodBreastplate, Stack: 1), (Type: ItemID.WoodGreaves, Stack: 1) };
        File.WriteAllText(marker, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, actor = actor.Name,
            account = actor.Account.ID, source = "one-time isolated console fixture supply; natural pickup/use/loadout actions remain with client",
            materials = materials.Select(item => new { item.Type, item.Stack }) }, jsonOptions));
        foreach (var item in materials) actor.GiveItem(item.Type, item.Stack);
        Record("m10-summon-gui-materials", new { marker, actor = actor.Name, noBypass = true });
        WriteM10SummonState();
    }

    private void ObserveM10SummonRaw(GetDataEventArgs args)
    {
        if ((byte)args.MsgID is not (27 or 29) || args.Msg is null || args.Length < 4 ||
            !m10SummonActors.TryGetValue(args.Msg.whoAmI, out var witness) ||
            !ReferenceEquals(TShock.Players[args.Msg.whoAmI], witness.Actor)) return;
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(args.Msg.readBuffer.AsSpan(args.Index, 4));
        witness.RawRequests++;
        witness.LastRaw = new { packet = (int)args.MsgID, key = bits, handled = args.Handled, witness.RawRequests,
            type = (byte)args.MsgID == 27 && args.Length >= 22
                ? (int?)BinaryPrimitives.ReadInt16LittleEndian(args.Msg.readBuffer.AsSpan(args.Index + 20, 2)) : null,
            source = "same synchronous raw dispatch after product and TShock hooks, before native consumer" };
    }

    private void ObserveM10SummonSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!args.ContinueExecution || args.msgType is not (27 or 29)) return;
        int slot = -1;
        if (args.msgType == 27 && (uint)args.number < Main.projectile.Length) slot = Main.projectile[args.number].owner;
        else if (args.msgType == 29) slot = ((ProjectileKey)(uint)args.number).Spawner;
        if (!m10SummonActors.TryGetValue(slot, out var witness) || !ReferenceEquals(TShock.Players[slot], witness.Actor)) return;
        if (args.msgType == 27) witness.Send27++; else witness.Send29++;
    }

    private void WriteM10SummonState()
    {
        var plugin = M5Plugin();
        var guard = plugin.GetType().GetField("_summonBudget", PrivateM5)?.GetValue(plugin);
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var capacities = guard?.GetType().GetField("capacities", PrivateM5)?.GetValue(guard) as Array;
        var pending = guard?.GetType().GetField("equipmentGaps", PrivateM5)?.GetValue(guard) as Array;
        object? CapacityValue(object? sample, string name) => sample?.GetType().GetProperty(name)?.GetValue(sample);
        var payload = new { utc = DateTimeOffset.UtcNow, guardPresent = guard is not null,
            healthy = Value("Healthy"), failureType = Value("FailureType"), failureReason = Value("FailureReason"),
            isolatedScaffoldAllowed = guard?.GetType().GetField("isolatedLab", PrivateM5)?.GetValue(guard),
            actualPluginTypes = ServerApi.Plugins.Select(plugin => plugin.Plugin.GetType().FullName).ToArray(),
            nativeCalculations = Value("NativeCalculations"), nativeCommits = Value("NativeCommits"),
            actors = m10SummonActors.Values.Select(witness =>
            {
                var actor = witness.Actor; var player = actor.TPlayer; var capacity = capacities?.GetValue(actor.Index);
                var decision = guard?.GetType().GetMethod("CaptureDecision")?.Invoke(guard, [actor.Index]);
                return new { slot = actor.Index, actor.Name, account = actor.Account.ID,
                    samePlayer = ReferenceEquals(TShock.Players[actor.Index], actor), actor.IsLoggedIn, actor.HasSentInventory,
                    bypass = actor.HasPermission("anticheat.bypass"), sscBypass = actor.HasPermission(Permissions.bypassssc),
                    x = player.position.X, y = player.position.Y, player.dead, player.maxMinions, player.maxTurrets,
                    player.CurrentLoadoutIndex, buffs = player.buffType.Where(type => type > 0).ToArray(),
                    nativeCapacityReady = capacity is not null,
                    nativeCapacity = CapacityValue(capacity, "Maximum"), upperBound = CapacityValue(capacity, "UpperBound"),
                    pendingNativeBuffAllowance = CapacityValue(capacity, "PendingBuffAllowance"),
                    pendingEquipmentSlots = (pending?.GetValue(actor.Index) as IEnumerable<int>)?.Order().ToArray() ?? [],
                    nativeCapacitySession = CapacityValue(capacity, "Session"), witness.RawRequests, witness.Send27, witness.Send29, witness.LastRaw,
                    latestDecision = decision is null ? null : new { session = CapacityValue(decision, "Session"),
                        sequence = CapacityValue(decision, "Sequence"), key = CapacityValue(decision, "ProjectileKey"),
                        action = CapacityValue(decision, "Action")?.ToString(), verdict = CapacityValue(decision, "Verdict")?.ToString(),
                        reason = CapacityValue(decision, "Reason"), passes = CapacityValue(decision, "Passes"),
                        blocks = CapacityValue(decision, "Blocks"), unknowns = CapacityValue(decision, "Unknowns") },
                    activeMinions = Main.projectile.Where(entity => entity.active && entity.owner == actor.Index && entity.minion)
                        .Take(1000).Select(entity => new { key = entity.key.bits, entity.type, entity.minionSlots, entity.timeLeft }).ToArray(),
                    source = "console-requested bounded snapshot; no entity deletion, capacity assignment, completion flag or player action" };
            }).ToArray() };
        File.WriteAllText(Path.Combine(output!, "m10-summon-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void DisposeM10Summon()
    {
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM10SummonRaw);
        HookEvents.Terraria.NetMessage.SendData -= ObserveM10SummonSend;
        m10SummonActors.Clear();
    }
}
