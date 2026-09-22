using System.Reflection;
using System.Text.Json;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Passive, bounded witnesses for the owned packet5 mapped-array acceptance.
/// No test inventory is injected and no packet or native setter is invoked by this fixture.</summary>
public sealed partial class GameplayScaffold
{
    private TSPlayer? m16SlotActor;
    private ILHook? m16SlotSetterWitness;
    private bool m16SlotSubscribed;
    private long m16SlotRawRequests, m16SlotCoreEntries, m16SlotNativeWrites, m16SlotSends, m16SlotFaults;
    private M16SlotPending? m16SlotPending;
    private M16SlotRaw? m16SlotLastRaw;
    private sealed record M16SlotItem(int Slot, int Type, int Stack, int Prefix, bool Favorite);
    private sealed record M16SlotSnapshot(M16SlotItem[] Native, string[] Ssc);
    private sealed record M16SlotPending(GetDataEventArgs Args, Player Player, long Sequence, int Thread, bool Handled, M16SlotSnapshot Before);
    private sealed record M16SlotRaw(long Sequence, int Slot, bool SamePlayer, bool SameThread, bool HandledBefore,
        bool HandledAfter, M16SlotSnapshot Before, M16SlotSnapshot After);

    private void PrepareM16InventorySlot(string[] names)
    {
        Require(names.Length == 1 && m16SlotActor is null, "Use qa_m16_inventory_slots <one exact authenticated actor> once.");
        var actor = ResolvePlayer(names[0]);
        Require(actor.IsLoggedIn && actor.HasSentInventory && !actor.HasPermission("anticheat.bypass") &&
            !actor.HasPermission(Permissions.bypassssc), "Ordinary current SSC actor required.");
        m16SlotActor = actor;
        try
        {
            var setter = typeof(PlayerItemSlotID.SlotReference).GetProperty(nameof(PlayerItemSlotID.SlotReference.Item))!.SetMethod!;
            m16SlotSetterWitness = new(setter, il =>
            {
                var cursor = new ILCursor(il);
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldfld, typeof(PlayerItemSlotID.SlotReference).GetField(nameof(PlayerItemSlotID.SlotReference.Player))!);
                cursor.EmitDelegate<Action<Player>>(player => { if (IsM16SlotActor(player)) m16SlotNativeWrites = M16SlotIncrement(m16SlotNativeWrites); });
            });
            ServerApi.Hooks.NetGetData.Register(this, ObserveM16SlotBefore, 2001);
            ServerApi.Hooks.NetGetData.Register(this, ObserveM16SlotAfter, -1001);
            GetDataHandlers.PlayerSlot.Register(ObserveM16SlotCore, HandlerPriority.Lowest, true);
            HookEvents.Terraria.NetMessage.SendData += ObserveM16SlotSend;
            m16SlotSubscribed = true;
        }
        catch { DisposeM16InventorySlot(); throw; }
        Record("m16-inventory-slot-passive-fixture", new { actor = actor.Name, account = actor.Account.ID,
            inventoryEdited = false, packetSubmitted = false, nativeSetterInvoked = false });
        WriteM16InventorySlotState();
    }
    private bool IsM16SlotActor(Player player) => m16SlotActor is { } actor &&
        ReferenceEquals(TShock.Players[actor.Index], actor) && ReferenceEquals(actor.TPlayer, player) && ReferenceEquals(Main.player[actor.Index], player);
    private static long M16SlotIncrement(long value) => value == long.MaxValue ? value : value + 1;
    private M16SlotSnapshot CaptureM16Slots()
    {
        var player = m16SlotActor!.TPlayer;
        Require(PlayerItemSlotID.Count <= 1024, "Locked slot domain changed.");
        var native = new List<M16SlotItem>(350);
        for (int id = 0; id < PlayerItemSlotID.Count; id++)
        {
            var slot = new PlayerItemSlotID.SlotReference(player, id);
            if (id != PlayerItemSlotID.TrashItem && (!slot.TryGetArraySlot(out var array, out int index) || array is null || (uint)index >= (uint)array.Length)) continue;
            var item = slot.Item;
            native.Add(new(id, item.type, item.stack, item.prefix, item.favorited));
        }
        return new(native.ToArray(), m16SlotActor.PlayerData.inventory.Select(item => item.ToString()).ToArray());
    }
    private void ObserveM16SlotBefore(GetDataEventArgs args)
    {
        if (args.MsgID != PacketTypes.PlayerSlot || args.Msg?.whoAmI != m16SlotActor?.Index) return;
        try
        {
            if (m16SlotPending is not null) m16SlotFaults = M16SlotIncrement(m16SlotFaults);
            m16SlotRawRequests = M16SlotIncrement(m16SlotRawRequests);
            m16SlotPending = new(args, m16SlotActor!.TPlayer, m16SlotRawRequests, Environment.CurrentManagedThreadId, args.Handled, CaptureM16Slots());
        }
        catch { m16SlotFaults = M16SlotIncrement(m16SlotFaults); }
    }
    private void ObserveM16SlotAfter(GetDataEventArgs args)
    {
        if (args.MsgID != PacketTypes.PlayerSlot || args.Msg?.whoAmI != m16SlotActor?.Index) return;
        var before = m16SlotPending; m16SlotPending = null;
        try
        {
            if (before is null || !ReferenceEquals(before.Args, args) || args.Length != 10)
            { m16SlotFaults = M16SlotIncrement(m16SlotFaults); return; }
            int slot = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(args.Msg!.readBuffer.AsSpan(args.Index + 1, 2));
            m16SlotLastRaw = new(before.Sequence, slot, IsM16SlotActor(before.Player), before.Thread == Environment.CurrentManagedThreadId,
                before.Handled, args.Handled, before.Before, CaptureM16Slots());
        }
        catch { m16SlotFaults = M16SlotIncrement(m16SlotFaults); }
    }
    private void ObserveM16SlotCore(object? _, GetDataHandlers.PlayerSlotEventArgs args)
    { if (ReferenceEquals(args.Player, m16SlotActor)) m16SlotCoreEntries = M16SlotIncrement(m16SlotCoreEntries); }
    private void ObserveM16SlotSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    { if (args.ContinueExecution && args.msgType == 5 && args.number == m16SlotActor?.Index) m16SlotSends = M16SlotIncrement(m16SlotSends); }

    private void WriteM16InventorySlotState()
    {
        Require(m16SlotActor is not null, "Prepare the owned passive slot fixture first.");
        var actor = m16SlotActor!; var plugin = M5Plugin();
        var guard = plugin.GetType().Assembly.GetType("AntiCheat.Plugin.TShock.M16InventorySlotSafety");
        long? Counter(string name) => guard?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as long?;
        var payload = new { utc = DateTimeOffset.UtcNow, guardPresent = guard is not null,
            witnessInstalled = m16SlotSubscribed && m16SlotSetterWitness is not null,
            actor = new { actor.Name, slot = actor.Index, account = actor.Account.ID, actor.IsLoggedIn, actor.HasSentInventory,
                samePlayer = IsM16SlotActor(actor.TPlayer), bypass = actor.HasPermission("anticheat.bypass"), sscBypass = actor.HasPermission(Permissions.bypassssc) },
            checks = Counter("Checks"), rejected = Counter("Rejected"), rawRequests = m16SlotRawRequests,
            coreEntries = m16SlotCoreEntries, nativeWrites = m16SlotNativeWrites, inventorySends = m16SlotSends,
            witnessFaults = m16SlotFaults, lastRaw = m16SlotLastRaw, assets = CaptureM16Slots(),
            bankStarts = new[] { PlayerItemSlotID.Bank1_0, PlayerItemSlotID.Bank2_0, PlayerItemSlotID.Bank3_0, PlayerItemSlotID.Bank4_0 },
            bankLengths = new[] { actor.TPlayer.bank.item.Length, actor.TPlayer.bank2.item.Length, actor.TPlayer.bank3.item.Length, actor.TPlayer.bank4.item.Length },
            lastSlot = PlayerItemSlotID.Count - 1, trash = PlayerItemSlotID.TrashItem,
            source = "passive real raw/core/native witnesses only; no preparation mutation or unsafe native call" };
        File.WriteAllText(Path.Combine(output!, "m16-inventory-slot-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
    private void DisposeM16InventorySlot()
    {
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM16SlotBefore);
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM16SlotAfter);
        GetDataHandlers.PlayerSlot.UnRegister(ObserveM16SlotCore);
        HookEvents.Terraria.NetMessage.SendData -= ObserveM16SlotSend;
        try { m16SlotSetterWitness?.Dispose(); }
        finally { m16SlotSetterWitness = null; m16SlotSubscribed = false; m16SlotPending = null; }
    }
}
