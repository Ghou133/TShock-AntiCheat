using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;

if (args.Length == 3 && args[0] == "storage") { NativeStorageCorpus.Run(args[1], args[2]); return; }
if (args.Length != 2) throw new ArgumentException("Supply a bounded complete native chest snapshot JSON and output JSON file.");
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var source = JsonSerializer.Deserialize<Source>(File.ReadAllText(args[0]), json) ?? throw new InvalidDataException("Missing snapshot.");
if (source.Chest < 0 || source.Chest >= 8000 || source.Items.Length != 40 || source.Items.Select(item => item.Slot).Distinct().Count() != 40 ||
    !source.Items.Select(item => item.Slot).Order().SequenceEqual(Enumerable.Range(0, 40))) throw new InvalidDataException("Only one complete default 40-slot chest is accepted.");
string runtimeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Item).Assembly.Location)));
if (runtimeHash != "C7FF9B092B296C86E1EA91B9BA8426E9F4955B4FA62DB7EA7FCFAE2EE3D1D511") throw new InvalidDataException("Locked target runtime changed.");
Terraria.Program.SavePath = Path.Combine(Path.GetTempPath(), "M17NativeSortCorpus", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Terraria.Program.SavePath);
Main.netMode = 1; Main.myPlayer = 17; Main.dedServ = false;
Main.rand = new Terraria.Utilities.UnifiedRandom(0x4D313753);
Main.player[17] = new Player { whoAmI = 17, active = true, chest = source.Chest };
LanguageManager.Instance.LoadLanguage(GameCulture.FromName("en-US")); Lang.InitializeLegacyLocalization();
NetMessage.buffer[256] = new MessageBuffer();
var chest = Main.chest[source.Chest] = new Chest();
if (chest.maxItems != source.Items.Length) throw new InvalidDataException("Native chest size mismatch.");
foreach (var value in source.Items)
{
    if (value.Type < 0 || value.Type >= ItemID.Count || value.Prefix < 0 || value.Prefix >= PrefixID.Count || value.Stack < 0)
        throw new InvalidDataException("Input must be the accepted canonical server snapshot, not invalid stimulus.");
    var item = chest.item[value.Slot] = new Item(); item.SetDefaults(value.Type); item.Prefix(value.Prefix); item.stack = value.Stack; item.favorited = value.Favorite;
    if (value.Type != 0 && (value.Stack == 0 || value.Stack > item.maxStack)) throw new InvalidDataException("Only legal positive stacks are accepted.");
}
// The real sorting layers read OriginalRarity from native item samples. Populate
// their required item domain with actual defaults; no sorting key is fabricated.
for (int type = -48; type < ItemID.Count; type++)
{
    var sample = new Item(); sample.netDefaults(type); ContentSamples.ItemsByType[type] = sample;
}
ItemSorting.SetupWhiteLists();
var before = Snapshot(); var frames = new List<byte[]>(40);
HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
try { ItemSorting.SortChest(); }
finally { HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture; }
var after = Snapshot();
var expected = before.Zip(after).Where(pair => (pair.First.Type, pair.First.Stack, pair.First.Prefix) != (pair.Second.Type, pair.Second.Stack, pair.Second.Prefix)).Select(pair => pair.First.Slot).ToArray();
if (!frames.Select(frame => (int)frame[5]).SequenceEqual(expected)) throw new InvalidDataException("Actual changed-slot serialization order differs from native MemoryStamp contract.");
foreach (var frame in frames)
{
    int slot = frame[5]; var item = after[slot];
    if (BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(3, 2)) != source.Chest || BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(6, 2)) != item.Stack ||
        frame[8] != item.Prefix || BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(9, 2)) != item.Type) throw new InvalidDataException("Native serialized content differs from post-sort item.");
}
string Totals(Value[] items) => JsonSerializer.Serialize(items.Where(item => item.Type != 0).GroupBy(item => (item.Type, item.Prefix))
    .OrderBy(group => group.Key.Type).ThenBy(group => group.Key.Prefix).Select(group => new { group.Key.Type, group.Key.Prefix, total = group.Sum(item => item.Stack) }));
if (Totals(before) != Totals(after)) throw new InvalidDataException("Native sorting did not conserve type/prefix quantities.");
foreach (var favorite in before.Where(item => item.Favorite)) if (after[favorite.Slot] != favorite) throw new InvalidDataException("Native favorite slot moved.");
File.WriteAllText(args[1], JsonSerializer.Serialize(new { source.Chest, runtimeHash, before, after, changedSlots = expected,
    frames = frames.Select(Convert.ToHexString).ToArray(), nativeEntry = "Terraria.UI.ItemSorting.SortChest", mainNetMode = Main.netMode,
    withSync = true, withFeedback = true, actualSerialization = true, liveTcpConnection = false, stockExecutableOrGui = false,
    inputProvenance = source.Provenance, receivedServerSnapshotUsedAsPreparation = true, protectionRulePremisesChanged = false }, json));
Console.WriteLine($"Native SortChest serialized {frames.Count} changed slots; quantity and favorite invariants passed.");
return;
Value[] Snapshot() => chest.item.Take(chest.maxItems).Select((item, slot) => new Value(slot, item.type, item.stack, item.prefix, item.favorited)).ToArray();
void Capture(object? _, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs packet)
{
    var frame = packet.ms.ToArray();
    if (frame.Length != 11 || frame[2] != 32 || frames.Count >= 40) throw new InvalidDataException("Unexpected or unbounded sorting serialization.");
    frames.Add(frame);
}
internal sealed record Source(int Chest, Value[] Items, string Provenance);
internal sealed record Value(int Slot, int Type, int Stack, int Prefix, bool Favorite);
