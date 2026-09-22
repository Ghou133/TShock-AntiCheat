using System.Reflection;
using System.Text.Json;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>One owned fixture and passive witnesses for three loopback acceptance requests.
/// Never invokes QuickStacking, edits a request, refunds, or submits a client packet.</summary>
public sealed partial class GameplayScaffold
{
    private TSPlayer? m10QuickActor;
    private Chest? m10QuickChest;
    private ILHook? m10QuickReadWitness, m10QuickTransferWitness;
    private bool m10QuickSubscribed;
    private long m10QuickRawCount, m10QuickReadCount, m10QuickTransferCount, m10QuickChestSends,
        m10QuickInventorySends, m10QuickWitnessFaults;
    private M10QuickPending? m10QuickPending;
    private M10QuickRaw? m10QuickLastRaw;
    private sealed record M10QuickItem(int Slot, int Type, int Stack, byte Prefix, bool Favorited);
    private sealed record M10QuickAssets(M10QuickItem[] Inventory, M10QuickItem[] VoidBag,
        M10QuickItem[] Chest, M10QuickItem[] WorldItems);
    private sealed record M10QuickPending(GetDataEventArgs Arguments, Player Player, long Sequence,
        int Thread, bool Handled, M10QuickAssets Assets);
    private sealed record M10QuickRaw(long Sequence, bool SamePlayer, bool SameThread, bool HandledBefore,
        bool HandledAfter, M10QuickAssets Before, M10QuickAssets After);

    private void PrepareM10QuickStack(string[] names)
    {
        Require(names.Length == 1, "Use qa_m10_quickstack <one exact ordinary authenticated actor>.");
        Require(m10QuickActor is null && room is null, "Quick Stack acceptance requires a fresh owned room.");
        var actor = ResolvePlayer(names[0]);
        Require(!actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc) &&
            !actor.HasPermission(Permissions.editregion), "Ordinary authenticated SSC account without bypass required.");
        Require(actor.HasSentInventory && actor.TPlayer.inventory[10].IsAir && actor.TPlayer.inventory[11].IsAir,
            "SSC must be ready and both fixture source slots empty; existing player items are retained.");
        Prepare(actor); // Existing bounded disposable-room setup and isolation checks.
        var chest = Main.chest[room!.ChestId];
        Require(chest is not null && chest.item.Take(chest.maxItems).All(item => item.IsAir), "Owned new chest must be empty.");
        Require(actor.HasBuildPermission(chest!.x, chest.y, false), "Owned chest must be in the normal allowed area.");
        m10QuickActor = actor; m10QuickChest = chest;
        // One-time, explicitly labeled server fixture grants; these are not player-action evidence.
        chest.item[0].SetDefaults(ItemID.Wood); chest.item[0].stack = chest.item[0].maxStack;
        actor.TPlayer.inventory[10].SetDefaults(ItemID.Wood); actor.TPlayer.inventory[10].stack = 7;
        actor.TPlayer.inventory[11].SetDefaults(ItemID.Wood); actor.TPlayer.inventory[11].stack = 11;
        NetMessage.SendData(32, number: chest.index, number2: 0);
        NetMessage.SendData(5, actor.Index, number: actor.Index, number2: 10);
        NetMessage.SendData(5, actor.Index, number: actor.Index, number2: 11);
        try
        {
            var read = typeof(QuickStacking).GetMethod(nameof(QuickStacking.ReadNetInventory),
                BindingFlags.Public | BindingFlags.Static, [typeof(Player), typeof(BinaryReader)])!;
            var transfer = typeof(QuickStacking).GetMethod(nameof(QuickStacking.QuickStackToNearbyChests),
                BindingFlags.Public | BindingFlags.Static, [typeof(Player), typeof(QuickStacking.SourceInventory), typeof(bool)])!;
            Require(read is not null && transfer is not null, "Locked native Quick Stack methods are required.");
            m10QuickReadWitness = new(read!, il =>
            {
                var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
                cursor.EmitDelegate<Action<Player>>(player => { if (IsM10QuickActor(player)) m10QuickReadCount = M10QuickIncrement(m10QuickReadCount); });
            });
            m10QuickTransferWitness = new(transfer!, il =>
            {
                var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
                cursor.EmitDelegate<Action<Player>>(player => { if (IsM10QuickActor(player)) m10QuickTransferCount = M10QuickIncrement(m10QuickTransferCount); });
            });
            HookEvents.Terraria.NetMessage.SendData += ObserveM10QuickSend;
            ServerApi.Hooks.NetGetData.Register(this, ObserveM10QuickBefore, 2001);
            ServerApi.Hooks.NetGetData.Register(this, ObserveM10QuickAfter, -1001);
            m10QuickSubscribed = true;
        }
        catch { DisposeM10QuickStack(); throw; }
        Record("m10-quick-stack-fixture", new { actor = actor.Name, account = actor.Account.ID,
            chest = chest.index, sourceSlots = new[] { 10, 11 }, setupAmounts = new[] { 7, 11 },
            fixtureArtificial = true, clientInputSubmitted = false, operationInvoked = false });
        WriteM10QuickStackState();
    }

    private static long M10QuickIncrement(long value) => value == long.MaxValue ? value : value + 1;
    private bool IsM10QuickActor(Player player) => m10QuickActor is { } actor &&
        ReferenceEquals(TShock.Players[actor.Index], actor) && ReferenceEquals(actor.TPlayer, player);

    private M10QuickAssets CaptureM10QuickAssets()
    {
        var player = m10QuickActor!.TPlayer; var chest = m10QuickChest!;
        Require(ReferenceEquals(Main.chest[chest.index], chest) && player.inventory.Length <= 400 &&
            player.bank4.item.Length <= 400 && chest.maxItems <= 200 && Main.item.Length <= 401,
            "Owned native fixture identity/capacity changed.");
        static M10QuickItem ItemState(Item item, int slot) => new(slot, item.type, item.stack, item.prefix, item.favorited);
        return new(player.inventory.Select(ItemState).ToArray(), player.bank4.item.Select(ItemState).ToArray(),
            chest.item.Take(chest.maxItems).Select(ItemState).ToArray(),
            Main.item.Select((item, slot) => (item, slot)).Where(x => x.item is { active: true })
                .Select(x => ItemState(x.item.inner, x.slot)).ToArray());
    }

    private void ObserveM10QuickBefore(GetDataEventArgs args)
    {
        if ((byte)args.MsgID != 85 || args.Msg is null || m10QuickActor is not { } actor || args.Msg.whoAmI != actor.Index) return;
        try
        {
            if (m10QuickPending is not null) m10QuickWitnessFaults = M10QuickIncrement(m10QuickWitnessFaults);
            m10QuickRawCount = M10QuickIncrement(m10QuickRawCount);
            m10QuickPending = new(args, actor.TPlayer, m10QuickRawCount, Environment.CurrentManagedThreadId,
                args.Handled, CaptureM10QuickAssets());
        }
        catch { m10QuickWitnessFaults = M10QuickIncrement(m10QuickWitnessFaults); }
    }

    private void ObserveM10QuickAfter(GetDataEventArgs args)
    {
        if ((byte)args.MsgID != 85 || args.Msg is null || m10QuickActor is not { } actor || args.Msg.whoAmI != actor.Index) return;
        var before = m10QuickPending; m10QuickPending = null;
        try
        {
            if (before is null || !ReferenceEquals(before.Arguments, args))
            { m10QuickWitnessFaults = M10QuickIncrement(m10QuickWitnessFaults); return; }
            m10QuickLastRaw = new(before.Sequence, IsM10QuickActor(before.Player),
                before.Thread == Environment.CurrentManagedThreadId, before.Handled, args.Handled,
                before.Assets, CaptureM10QuickAssets());
        }
        catch { m10QuickWitnessFaults = M10QuickIncrement(m10QuickWitnessFaults); }
    }

    private void ObserveM10QuickSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!args.ContinueExecution) return;
        if (args.msgType == 32 && m10QuickChest is { } chest && args.number == chest.index)
            m10QuickChestSends = M10QuickIncrement(m10QuickChestSends);
        if (args.msgType == 5 && m10QuickActor is { } actor && args.number == actor.Index)
            m10QuickInventorySends = M10QuickIncrement(m10QuickInventorySends);
    }

    private void WriteM10QuickStackState()
    {
        Require(m10QuickActor is not null && m10QuickChest is not null, "Prepare the owned Quick Stack fixture first.");
        var actor = m10QuickActor!; var chest = m10QuickChest!; var plugin = M5Plugin();
        var guard = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M10NaturalQuickStackSafety");
        long? Counter(string name) => guard?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as long?;
        var payload = new { utc = DateTimeOffset.UtcNow, guardPresent = guard is not null,
            scope = plugin.GetType().GetField("_scope", PrivateM5)?.GetValue(plugin)?.ToString(),
            witnessInstalled = m10QuickSubscribed && m10QuickReadWitness is not null && m10QuickTransferWitness is not null,
            actor = new { actor.Name, slot = actor.Index, account = actor.Account.ID, actor.IsLoggedIn, actor.HasSentInventory,
                group = actor.Group.Name, bypass = actor.HasPermission("anticheat.bypass"),
                sscBypass = actor.HasPermission(Permissions.bypassssc), samePlayer = IsM10QuickActor(actor.TPlayer) },
            chest = new { id = chest.index, chest.x, chest.y, chest.maxItems }, assets = CaptureM10QuickAssets(),
            checks = Counter("Checks"), rejected = Counter("Rejected"), duplicateRejected = Counter("DuplicateRejected"),
            rawRequests = m10QuickRawCount, nativeReads = m10QuickReadCount, nativeTransfers = m10QuickTransferCount,
            chestSends = m10QuickChestSends, inventorySends = m10QuickInventorySends, witnessFaults = m10QuickWitnessFaults,
            lastRaw = m10QuickLastRaw, source = "passive raw dispatch and actual native read/transfer entries; console setup only; no GUI claim" };
        File.WriteAllText(Path.Combine(output!, "m10-quick-stack-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void DisposeM10QuickStack()
    {
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM10QuickBefore);
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM10QuickAfter);
        HookEvents.Terraria.NetMessage.SendData -= ObserveM10QuickSend;
        try { m10QuickReadWitness?.Dispose(); }
        finally { m10QuickReadWitness = null; try { m10QuickTransferWitness?.Dispose(); }
            finally { m10QuickTransferWitness = null; m10QuickSubscribed = false; m10QuickPending = null; } }
    }
}
