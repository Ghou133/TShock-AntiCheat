using System.Reflection;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Owned grants and passive actual bank/native/SSC snapshots. Reuses existing
/// packet5 witnesses. Rebinding follows a real closed transport, never swaps a product session.</summary>
public sealed partial class GameplayScaffold
{
    private int m18StorageBindings;
    private bool m18StorageSubscribed;
    private long m18StorageTail138;
    private bool m18StorageLastTailHandled;
    private readonly HashSet<int> m18StorageGrantedAccounts = new(4);
    private sealed record M18StorageValue(int Slot, int Type, int Stack, int Prefix, bool Favorite);
    private bool CurrentM18StorageActor() => m16SlotActor is { Active: true } actor &&
        ReferenceEquals(TShock.Players[actor.Index], actor) && ReferenceEquals(Main.player[actor.Index], actor.TPlayer);
    private static int[] M18BankOffsets() => [PlayerItemSlotID.Bank1_0, PlayerItemSlotID.Bank2_0, PlayerItemSlotID.Bank3_0, PlayerItemSlotID.Bank4_0];
    private static Chest[] M18Banks(Player player) => [player.bank, player.bank2, player.bank3, player.bank4];

    private void M18StorageCommand(string[] args)
    {
        Require(args.Length >= 1, "Use qa_m18_storage bind <actor>, or state.");
        if (args[0] == "bind")
        {
            Require(args.Length == 2 && !CurrentM18StorageActor() && m18StorageBindings < 8, "At most eight real bindings, after the previous transport leaves.");
            var actor = ResolvePlayer(args[1]);
            Require(actor.IsLoggedIn && actor.HasSentInventory && !actor.IgnoreSSCPackets &&
                !actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc), "Ordinary authenticated SSC actor required.");
            if (!m16SlotSubscribed) PrepareM16InventorySlot([args[1]]);
            else m16SlotActor = actor; // Only the passive fixture target follows the new real login.
            if (!m18StorageSubscribed)
            { ServerApi.Hooks.NetGetData.Register(this, ObserveM18StorageTail, -1001); m18StorageSubscribed = true; }
            m18StorageBindings++;
            if (!m18StorageGrantedAccounts.Contains(actor.Account.ID))
            {
                Require(m18StorageGrantedAccounts.Count < 4, "Four fixed storage accounts per run.");
                var player = actor.TPlayer; var banks = M18Banks(player); int[] offsets = M18BankOffsets();
                Require(banks.All(bank => bank.maxItems == 40 && bank.item.Length == 40 && bank.item.All(item => item.IsAir)) &&
                    new[] { 10, 11, 12 }.All(slot => player.inventory[slot].IsAir), "Fresh empty bank and source slots required; no accepted asset is erased.");
                void Grant(Item item, int type, int stack, bool favorite = false, byte prefix = 0)
                { item.SetDefaults(type); item.Prefix(prefix); item.stack = stack; item.favorited = favorite; }
                Grant(player.inventory[10], ItemID.Wood, 23); Grant(player.inventory[11], ItemID.Gel, 15); Grant(player.inventory[12], ItemID.Wood, 99, true);
                foreach (var bank in banks)
                {
                    Grant(bank.item[2], ItemID.Wood, 7000); Grant(bank.item[39], ItemID.Wood, 6000); Grant(bank.item[7], ItemID.Wood, 100, true);
                    Grant(bank.item[30], ItemID.WoodenSword, 1, prefix: PrefixID.Legendary); Grant(bank.item[33], ItemID.WoodenSword, 1); Grant(bank.item[23], ItemID.Gel, 300);
                }
                actor.PlayerData.CopyCharacter(actor);
                // Export every supported source row to this actual client. The record is
                // preparation provenance, not a client-completion or source-legality premise.
                for (int slot = 0; slot < 59; slot++) Terraria.NetMessage.SendData(5, actor.Index, number: actor.Index, number2: PlayerItemSlotID.Inventory0 + slot);
                for (int bank = 0; bank < banks.Length; bank++)
                    for (int slot = 0; slot < banks[bank].item.Length; slot++) Terraria.NetMessage.SendData(5, actor.Index, number: actor.Index, number2: offsets[bank] + slot);
                m18StorageGrantedAccounts.Add(actor.Account.ID);
                Record("m18-storage-prepare", new { actor.Name, actor.Account.ID, inventorySlots = new[] { 10, 11, 12 }, bankSlots = new[] { 2, 7, 23, 30, 33, 39 },
                    preparationOnly = true, productStateChanged = false, nativeSortingCalled = false });
            }
        }
        else Require(args.Length == 1 && args[0] == "state", "Unknown bounded storage command.");
        WriteM18StorageState();
    }
    private void ObserveM18StorageTail(GetDataEventArgs args)
    {
        if ((byte)args.MsgID != 138 || args.Msg?.whoAmI != m16SlotActor?.Index || !CurrentM18StorageActor()) return;
        if (m18StorageTail138 < long.MaxValue) m18StorageTail138++;
        m18StorageLastTailHandled = args.Handled;
    }
    private void DisposeM18Storage()
    { if (m18StorageSubscribed) ServerApi.Hooks.NetGetData.Deregister(this, ObserveM18StorageTail); m18StorageSubscribed = false; }
    private void WriteM18StorageState()
    {
        Require(m16SlotActor is not null && m18StorageBindings > 0, "Bind a current storage actor first.");
        var actor = m16SlotActor!; var player = actor.TPlayer; var banks = M18Banks(player); int[] offsets = M18BankOffsets();
        Require(banks.All(bank => bank.item.Length == 40) && player.inventory.Length >= 59, "Actual allocated storage domain changed.");
        M18StorageValue Value(Item item, int slot) => new(slot, item.type, item.stack, item.prefix, item.favorited);
        var inventory = player.inventory.Take(59).Select(Value).ToArray(); var bankValues = banks.Select(bank => bank.item.Select(Value).ToArray()).ToArray();
        var native = inventory.Select(value => value with { Slot = PlayerItemSlotID.Inventory0 + value.Slot })
            .Concat(bankValues.SelectMany((bank, index) => bank.Select(value => value with { Slot = offsets[index] + value.Slot }))).ToArray();
        var map = typeof(GetDataHandlers).GetMethod("NetworkSlotToInternalSlot", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ssc = native.Select(value =>
        {
            int index = (int)map.Invoke(null, [value.Slot])!;
            Require((uint)index < actor.PlayerData.inventory.Length, "Actual TShock storage mapping is outside SSC.");
            var item = actor.PlayerData.inventory[index]; return new M18StorageValue(value.Slot, item.NetId, item.Stack, item.PrefixId, item.Favorited);
        }).ToArray();
        var plugin = M5Plugin();
        var binding = ((Array)plugin.GetType().GetField("_bindings", PrivateM5)!.GetValue(plugin)!).GetValue(actor.Index);
        var business = plugin.GetType().GetField("_business", PrivateM5)!.GetValue(plugin);
        var payload = new { utc = DateTimeOffset.UtcNow, actor = actor.Name, slot = actor.Index, account = actor.Account.ID,
            currentActor = CurrentM18StorageActor(), session = binding?.GetType().GetProperty("Key")!.GetValue(binding),
            witnessInstalled = m16SlotSubscribed && m16SlotSetterWitness is not null, itemTableReady = business?.GetType().GetProperty("ItemTableReady")!.GetValue(business),
            offsets, bankLengths = banks.Select(bank => bank.item.Length), inventory, banks = bankValues, native, ssc,
            raw = m16SlotRawRequests, core = m16SlotCoreEntries, setters = m16SlotNativeWrites, sends = m16SlotSends, faults = m16SlotFaults,
            tail138 = m18StorageTail138, lastTailHandled = m18StorageLastTailHandled,
            last = m16SlotLastRaw, bindingCount = m18StorageBindings, grantAccountCount = m18StorageGrantedAccounts.Count,
            source = "passive actual mapped native and SSC state; explicit isolated grants are not player completion or ownership proofs" };
        WriteM16ItemSnapshotFile(Path.Combine(output!, "m18-storage-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
}
