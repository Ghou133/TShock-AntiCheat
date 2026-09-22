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

/// <summary>Owned room preparation and passive packet5/32 witnesses. These observations
/// are acceptance evidence only; they never change product health or supply a rule premise.</summary>
public sealed partial class GameplayScaffold
{
    private TSPlayer? m16ItemActor;
    private Chest? m16ItemChest;
    private ILHook? m16ItemSlotWrite, m16ItemChestWrite;
    private bool m16ItemSubscribed;
    private long m16ItemRawCount, m16ItemCoreCount, m16ItemNativeCount, m16ItemSends, m16ItemFaults;
    private M16ItemPending? m16ItemPending;
    private M16ItemRaw? m16ItemLast;
    private readonly string m16ItemSnapshotTempSuffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
    private sealed record M16ItemValue(int Slot, int Type, int Stack, byte Prefix, bool Favorite);
    private sealed record M16ItemAssets(M16ItemValue[] Inventory, M16ItemValue[] Chest, string[] Ssc);
    private sealed record M16ItemPending(GetDataEventArgs Args, Player Player, int Thread, long Sequence, bool Handled, M16ItemAssets Before);
    private sealed record M16ItemRaw(int Packet, long Sequence, bool SamePlayer, bool SameThread, bool HandledBefore, bool HandledAfter, M16ItemAssets Before, M16ItemAssets After);

    private void PrepareM16ItemStructure(string[] names)
    {
        Require(names.Length == 1 && m16ItemActor is null && room is null, "Use qa_m16_item_structure <one authenticated actor> in a fresh owned room.");
        var actor = ResolvePlayer(names[0]);
        Require(actor.IsLoggedIn && actor.HasSentInventory && !actor.HasPermission("anticheat.bypass") &&
            !actor.HasPermission(Permissions.bypassssc) && !actor.HasPermission(Permissions.editregion), "Ordinary current SSC account required.");
        Prepare(actor); // Existing bounded isolated room; setup is not a player-action claim.
        var chest = Main.chest[room!.ChestId];
        Require(chest is not null && actor.HasBuildPermission(chest.x, chest.y, false), "Owned allowed chest required.");
        m16ItemActor = actor; m16ItemChest = chest;
        try
        {
            var setter = typeof(PlayerItemSlotID.SlotReference).GetProperty(nameof(PlayerItemSlotID.SlotReference.Item))!.SetMethod!;
            m16ItemSlotWrite = new(setter, il =>
            {
                var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldfld, typeof(PlayerItemSlotID.SlotReference).GetField(nameof(PlayerItemSlotID.SlotReference.Player))!);
                cursor.EmitDelegate<Action<Player>>(player => { if (IsM16ItemActor(player)) m16ItemNativeCount = M16ItemIncrement(m16ItemNativeCount); });
            });
            var defaults = typeof(Item).GetMethods().Single(m => m.Name == nameof(Item.SetDefaults) && m.GetParameters().Length == 2);
            m16ItemChestWrite = new(defaults, il =>
            {
                var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
                cursor.EmitDelegate<Action<Item>>(item =>
                { if (m16ItemChest?.item.Take(m16ItemChest.maxItems).Any(current => ReferenceEquals(current, item)) == true)
                    m16ItemNativeCount = M16ItemIncrement(m16ItemNativeCount); });
            });
            ServerApi.Hooks.NetGetData.Register(this, ObserveM16ItemBefore, 2001);
            ServerApi.Hooks.NetGetData.Register(this, ObserveM16ItemAfter, -1001);
            GetDataHandlers.PlayerSlot.Register(ObserveM16ItemInventoryCore, HandlerPriority.Lowest, true);
            GetDataHandlers.ChestItemChange.Register(ObserveM16ItemChestCore, HandlerPriority.Lowest, true);
            HookEvents.Terraria.NetMessage.SendData += ObserveM16ItemSend;
            m16ItemSubscribed = true;
        }
        catch { DisposeM16ItemStructure(); throw; }
        Record("m16-item-structure-fixture", new { actor = actor.Name, account = actor.Account.ID, chest = chest!.index,
            setup = "existing bounded owned room only", productHealthChanged = false, packetSubmitted = false });
        WriteM16ItemStructureState();
    }
    private static long M16ItemIncrement(long value) => value == long.MaxValue ? value : value + 1;
    private bool IsM16ItemActor(Player player) => m16ItemActor is { } actor && ReferenceEquals(TShock.Players[actor.Index], actor) &&
        ReferenceEquals(Main.player[actor.Index], player) && ReferenceEquals(actor.TPlayer, player);
    private M16ItemAssets CaptureM16Items()
    {
        var actor = m16ItemActor!; var chest = m16ItemChest!;
        Require(IsM16ItemActor(actor.TPlayer) && ReferenceEquals(Main.chest[chest.index], chest) && PlayerItemSlotID.Count <= 1024 && chest.maxItems <= 200,
            "Current actor/chest identity and bounded layouts required.");
        var inventory = new List<M16ItemValue>(350);
        for (int id = 0; id < PlayerItemSlotID.Count; id++)
        {
            var slot = new PlayerItemSlotID.SlotReference(actor.TPlayer, id);
            if (id != PlayerItemSlotID.TrashItem && (!slot.TryGetArraySlot(out var array, out int index) || array is null || (uint)index >= (uint)array.Length)) continue;
            var item = slot.Item; inventory.Add(new(id, item.type, item.stack, item.prefix, item.favorited));
        }
        return new(inventory.ToArray(), chest.item.Take(chest.maxItems).Select((item, slot) => new M16ItemValue(slot, item.type, item.stack, item.prefix, item.favorited)).ToArray(),
            actor.PlayerData.inventory.Select(item => item.ToString()).ToArray());
    }
    private bool IsM16ItemPacket(GetDataEventArgs args) => (byte)args.MsgID is 5 or 32 && args.Msg?.whoAmI == m16ItemActor?.Index;
    private void ObserveM16ItemBefore(GetDataEventArgs args)
    {
        if (!IsM16ItemPacket(args)) return;
        try
        {
            if (m16ItemPending is not null) m16ItemFaults = M16ItemIncrement(m16ItemFaults);
            m16ItemRawCount = M16ItemIncrement(m16ItemRawCount);
            m16ItemPending = new(args, m16ItemActor!.TPlayer, Environment.CurrentManagedThreadId, m16ItemRawCount, args.Handled, CaptureM16Items());
        }
        catch { m16ItemFaults = M16ItemIncrement(m16ItemFaults); }
    }
    private void ObserveM16ItemAfter(GetDataEventArgs args)
    {
        if (!IsM16ItemPacket(args)) return;
        var before = m16ItemPending; m16ItemPending = null;
        try
        {
            if (before is null || !ReferenceEquals(before.Args, args)) { m16ItemFaults = M16ItemIncrement(m16ItemFaults); return; }
            m16ItemLast = new((byte)args.MsgID, before.Sequence, IsM16ItemActor(before.Player), before.Thread == Environment.CurrentManagedThreadId,
                before.Handled, args.Handled, before.Before, CaptureM16Items());
        }
        catch { m16ItemFaults = M16ItemIncrement(m16ItemFaults); }
    }
    private void ObserveM16ItemInventoryCore(object? _, GetDataHandlers.PlayerSlotEventArgs args)
    { if (ReferenceEquals(args.Player, m16ItemActor)) m16ItemCoreCount = M16ItemIncrement(m16ItemCoreCount); }
    private void ObserveM16ItemChestCore(object? _, GetDataHandlers.ChestItemEventArgs args)
    { if (ReferenceEquals(args.Player, m16ItemActor)) m16ItemCoreCount = M16ItemIncrement(m16ItemCoreCount); }
    private void ObserveM16ItemSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.ContinueExecution && (args.msgType == 5 && args.number == m16ItemActor?.Index || args.msgType == 32 && args.number == m16ItemChest?.index))
            m16ItemSends = M16ItemIncrement(m16ItemSends);
    }
    private void WriteM16ItemStructureState()
    {
        Require(m16ItemActor is not null && m16ItemChest is not null, "Prepare owned item structure fixture first.");
        var actor = m16ItemActor!; var chest = m16ItemChest!; var plugin = M5Plugin();
        var business = plugin.GetType().GetField("_business", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin);
        var aliases = Enumerable.Range(-48, 48).Select(id => { var item = new Item(); item.netDefaults(id); return new { wire = id, canonical = item.type }; }).ToArray();
        var payload = new { utc = DateTimeOffset.UtcNow, witnessInstalled = m16ItemSubscribed && m16ItemSlotWrite is not null && m16ItemChestWrite is not null,
            itemTableReady = business?.GetType().GetProperty("ItemTableReady")?.GetValue(business), itemCount = ItemID.Count, prefixCount = PrefixID.Count, aliases,
            actor = new { actor.Name, actor.Index, actor.IsLoggedIn, actor.HasSentInventory, samePlayer = IsM16ItemActor(actor.TPlayer),
                bypass = actor.HasPermission("anticheat.bypass"), sscBypass = actor.HasPermission(Permissions.bypassssc) },
            chest = new { id = chest.index, chest.x, chest.y, chest.maxItems }, trash = PlayerItemSlotID.TrashItem, lastSlot = PlayerItemSlotID.Count - 1,
            rawRequests = m16ItemRawCount, coreEntries = m16ItemCoreCount, nativeWrites = m16ItemNativeCount, sends = m16ItemSends, witnessFaults = m16ItemFaults,
            lastRaw = m16ItemLast, assets = CaptureM16Items(), source = "actual passive raw/core/native observations; no injected product premise" };
        WriteM16ItemSnapshotFile(Path.Combine(output!, "m16-item-structure-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
    private void WriteM16ItemSnapshotFile(string path, string contents)
    {
        // These snapshots have one main-thread writer. An independent backup name keeps
        // open Windows readers on the old file from occupying the next destination name.
        string temporaryPath = path + m16ItemSnapshotTempSuffix;
        string backupPath = path + "." + Guid.NewGuid().ToString("N") + ".bak";
        bool published = false;
        try
        {
            File.WriteAllText(temporaryPath, contents);
            if (File.Exists(path)) File.Replace(temporaryPath, path, backupPath);
            else File.Move(temporaryPath, path);
            published = true;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // A failed replacement may have moved the only old snapshot into backup.
            try { if (published && File.Exists(backupPath)) File.Delete(backupPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void DisposeM16ItemStructure()
    {
        ServerApi.Hooks.NetGetData.Deregister(this, ObserveM16ItemBefore); ServerApi.Hooks.NetGetData.Deregister(this, ObserveM16ItemAfter);
        GetDataHandlers.PlayerSlot.UnRegister(ObserveM16ItemInventoryCore); GetDataHandlers.ChestItemChange.UnRegister(ObserveM16ItemChestCore);
        HookEvents.Terraria.NetMessage.SendData -= ObserveM16ItemSend;
        try { m16ItemSlotWrite?.Dispose(); m16ItemChestWrite?.Dispose(); }
        finally { m16ItemSlotWrite = m16ItemChestWrite = null; m16ItemSubscribed = false; m16ItemPending = null; }
    }
}
