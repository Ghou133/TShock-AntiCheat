using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;

/// <summary>Finite native private-storage operation and real native player-difference writer.
/// Independent process, no socket, no product reference and no claim of a running stock client.</summary>
internal static class NativeStorageCorpus
{
    public static void Run(string input, string output)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        var source = JsonSerializer.Deserialize<StorageSource>(File.ReadAllText(input), json) ?? throw new InvalidDataException("Missing storage snapshot.");
        if (source.Bank is < 0 or > 3 || source.Player is < 0 or >= 255 || source.Operation is not ("sort" or "quickstack") ||
            source.Inventory.Length != 59 || source.Banks.Length != 4 || source.Banks.Any(bank => bank.Length != 40))
            throw new InvalidDataException("Only the locked four-bank / 59-inventory finite input is supported.");
        string runtimeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Item).Assembly.Location)));
        if (runtimeHash != "C7FF9B092B296C86E1EA91B9BA8426E9F4955B4FA62DB7EA7FCFAE2EE3D1D511") throw new InvalidDataException("Locked native runtime changed.");
        Terraria.Program.SavePath = Path.Combine(Path.GetTempPath(), "M18NativeStorageCorpus", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Terraria.Program.SavePath);
        Main.netMode = 1; Main.myPlayer = source.Player; Main.dedServ = false; Main.rand = new Terraria.Utilities.UnifiedRandom(0x4D313853);
        var player = Main.player[source.Player] = new Player { whoAmI = source.Player, active = true, chest = -2 - source.Bank };
        int[] offsets = [PlayerItemSlotID.Bank1_0, PlayerItemSlotID.Bank2_0, PlayerItemSlotID.Bank3_0, PlayerItemSlotID.Bank4_0];
        Chest[] banks = [player.bank, player.bank2, player.bank3, player.bank4];
        if (banks.Any(bank => bank.maxItems != 40 || bank.item.Length != 40)) throw new InvalidDataException("Native allocated bank lengths changed.");
        Load(source.Inventory, player.inventory, 59);
        for (int bank = 0; bank < banks.Length; bank++) Load(source.Banks[bank], banks[bank].item, 40);
        LanguageManager.Instance.LoadLanguage(GameCulture.FromName("en-US")); Lang.InitializeLegacyLocalization();
        for (int type = -48; type < ItemID.Count; type++) { var sample = new Item(); sample.netDefaults(type); ContentSamples.ItemsByType[type] = sample; }
        ItemSorting.SetupWhiteLists(); NetMessage.buffer[256] = new MessageBuffer();
        var client = new Player { whoAmI = source.Player };
        player.clientClone(client);
        typeof(Main).GetField("clientPlayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(null, client);
        var before = Snapshot(); var frames = new List<byte[]>(220); var stages = new List<object>(3);
        HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
        StorageState after;
        try
        {
            int operationStart = frames.Count;
            if (source.Operation == "sort") ItemSorting.SortChest(); else ChestUI.QuickStack(false);
            after = Snapshot();
            stages.Add(new { stage = "native-operation", frames = frames.Skip(operationStart).Select(Convert.ToHexString).ToArray() });
            if (frames.Count != 0) throw new InvalidDataException("Private bank operation unexpectedly serialized directly; audit changed.");
            Main.TrySyncingMyPlayer();
            stages.Add(new { stage = "native-player-difference-sync", frames = frames.Select(Convert.ToHexString).ToArray() });
            int firstSyncCount = frames.Count;
            Main.TrySyncingMyPlayer();
            stages.Add(new { stage = "native-clientClone-restabilized", frames = frames.Skip(firstSyncCount).Select(Convert.ToHexString).ToArray() });
            if (frames.Count != firstSyncCount) throw new InvalidDataException("Native cloned baseline retransmitted unchanged inventory.");
        }
        finally { HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture; }
        var expected = Flatten(before).Zip(Flatten(after)).Where(pair => pair.First != pair.Second).Select(pair => pair.First.Slot).ToArray();
        var writes = frames.Where(frame => frame[2] == 5).ToArray();
        if (!writes.Select(frame => (int)BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(4, 2))).SequenceEqual(expected))
            throw new InvalidDataException("Actual native changed-slot serialization order differs from full item state delta.");
        if (frames.Count != writes.Length + (expected.Length > 0 ? 1 : 0) || expected.Length > 0 && frames[^1][2] != 138)
            throw new InvalidDataException("Native inventory tail138 is not aligned with actual writes.");
        var bySlot = Flatten(after).ToDictionary(value => value.Slot);
        foreach (var frame in writes)
        {
            int slot = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(4, 2)); var value = bySlot[slot];
            if (frame[3] != source.Player || BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(6, 2)) != value.Stack || frame[8] != value.Prefix ||
                BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(9, 2)) != value.Type || ((frame[11] & 1) != 0) != value.Favorite)
                throw new InvalidDataException("Actual native5 content does not match its stored item.");
        }
        if (Totals(before) != Totals(after)) throw new InvalidDataException("Native operation failed total type/prefix conservation.");
        foreach (var favorite in Flatten(before).Where(value => value.Favorite))
            if (bySlot[favorite.Slot] != favorite) throw new InvalidDataException("Native operation moved or changed a favorited slot.");
        File.WriteAllText(output, JsonSerializer.Serialize(new { source.Bank, source.Player, source.Operation, source.Provenance, runtimeHash, offsets,
            allocatedLengths = banks.Select(bank => bank.item.Length), reservedAddressSpanPerBank = 200, before, after, changedSlots = expected,
            frames = frames.Select(Convert.ToHexString), stages, nativeOperation = source.Operation == "sort" ? "Terraria.UI.ItemSorting.SortChest" : "Terraria.UI.ChestUI.QuickStack(false)",
            nativeSync = "Terraria.Main.TrySyncingMyPlayer -> TrySyncingItemArray / player.clientClone", nativeDiffAndSerialization = true,
            actualTcpConnection = false, stockExecutableOrGui = false, nativePreparationOnly = true, productPremisesChanged = false }, json));
        Console.WriteLine($"Native {source.Operation}, bank {source.Bank}: {writes.Length} actual5 + {(expected.Length > 0 ? 1 : 0)} actual138; conserved and stable.");

        void Load(StorageValue[] values, Item[] items, int length)
        {
            if (!values.Select(value => value.Slot).Order().SequenceEqual(Enumerable.Range(0, length))) throw new InvalidDataException("A complete unique source domain is required.");
            foreach (var value in values)
            {
                if (value.Type < 0 || value.Type >= ItemID.Count || value.Prefix < 0 || value.Prefix >= PrefixID.Count || value.Stack < 0)
                    throw new InvalidDataException("Only accepted canonical item states are inputs.");
                var item = items[value.Slot] = new Item(); item.SetDefaults(value.Type); item.Prefix(value.Prefix); item.stack = value.Stack; item.favorited = value.Favorite;
                if (value.Type != 0 && (value.Stack == 0 || value.Stack > item.maxStack)) throw new InvalidDataException("Only legal positive stacks are inputs.");
            }
        }
        StorageState Snapshot() => new(player.inventory.Take(59).Select(Value).ToArray(), banks.Select(bank => bank.item.Select(Value).ToArray()).ToArray());
        StorageValue Value(Item item, int slot) => new(slot, item.type, item.stack, item.prefix, item.favorited);
        IEnumerable<StorageValue> Flatten(StorageState state) => state.Inventory.Select(value => value with { Slot = PlayerItemSlotID.Inventory0 + value.Slot })
            .Concat(state.Banks.SelectMany((bank, index) => bank.Select(value => value with { Slot = offsets[index] + value.Slot })));
        string Totals(StorageState state) => JsonSerializer.Serialize(Flatten(state).Where(value => value.Type != 0).GroupBy(value => (value.Type, value.Prefix))
            .OrderBy(group => group.Key.Type).ThenBy(group => group.Key.Prefix).Select(group => new { group.Key.Type, group.Key.Prefix, total = group.Sum(value => value.Stack) }));
        void Capture(object? _, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs packet)
        {
            byte[] frame = packet.ms.ToArray();
            if (frames.Count >= 220 || !(frame.Length == 12 && frame[2] == 5 || frame.Length == 3 && frame[2] == 138))
                throw new InvalidDataException("Unexpected or unbounded native private-storage serialization.");
            frames.Add(frame);
        }
    }
    private sealed record StorageSource(int Bank, int Player, string Operation, StorageValue[] Inventory, StorageValue[][] Banks, string Provenance);
    private sealed record StorageState(StorageValue[] Inventory, StorageValue[][] Banks);
    private sealed record StorageValue(int Slot, int Type, int Stack, int Prefix, bool Favorite);
}
