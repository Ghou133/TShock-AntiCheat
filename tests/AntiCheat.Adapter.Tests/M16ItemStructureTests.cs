using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M16ItemStructureTests
{
    private const int Actor = 17, ChestId = 0;
    private M2BusinessAdapter business = null!;
    private TSPlayer actor = null!;
    private SessionKey session;
    private readonly List<byte[]> frames = [];
    private readonly List<string> faults = [];
    private int oldMode, oldLocal, oldWidth, oldHeight;
    private bool oldDedicated, oldSsc;
    private Player oldPlayer = null!;
    private Player oldServerPlayer = null!;
    private RemoteClient oldClient = null!;
    private MessageBuffer oldWriter = null!;
    private Chest oldChest = null!;
    private TShockConfig oldConfig = null!;
    private ServerSideConfig oldSscConfig = null!;
    private ILog oldLog = null!;
    private TextLog log = null!;
    private Terraria.Utilities.UnifiedRandom? oldRandom;
    private Terraria.Localization.LanguageManager oldLanguage = null!;
    private readonly List<Action> restoreLanguage = [];

    [OneTimeSetUp]
    public void InitializeActualItemNamesWithoutLeakingLanguageState()
    {
        oldLanguage = Terraria.Localization.LanguageManager.Instance;
        foreach (var field in typeof(Lang).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (field.IsLiteral) continue;
            object? original = field.GetValue(null);
            if (original is Array array)
            {
                var copy = (Array)array.Clone();
                restoreLanguage.Add(() => { Array.Copy(copy, array, array.Length); if (!field.IsInitOnly) field.SetValue(null, array); });
            }
            else if (original is IDictionary dictionary)
            {
                var copy = dictionary.Keys.Cast<object>().Select(key => new DictionaryEntry(key, dictionary[key])).ToArray();
                restoreLanguage.Add(() => { dictionary.Clear(); foreach (var entry in copy) dictionary.Add(entry.Key, entry.Value); if (!field.IsInitOnly) field.SetValue(null, dictionary); });
            }
            else if (!field.IsInitOnly) restoreLanguage.Add(() => field.SetValue(null, original));
        }
        Terraria.Localization.LanguageManager.Instance = new();
        Terraria.Localization.LanguageManager.Instance.LoadLanguage(Terraria.Localization.GameCulture.FromName("en-US"));
        Lang.InitializeLegacyLocalization();
    }

    [OneTimeTearDown]
    public void RestoreLanguage()
    {
        Terraria.Localization.LanguageManager.Instance = oldLanguage;
        for (int index = restoreLanguage.Count - 1; index >= 0; index--) restoreLanguage[index]();
        restoreLanguage.Clear();
    }

    [SetUp]
    public void Setup()
    {
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldDedicated = Main.dedServ; oldSsc = Main.ServerSideCharacter;
        oldRandom = Main.rand;
        oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY; oldPlayer = Main.player[Actor]; oldServerPlayer = Main.player[255];
        oldClient = Netplay.Clients[Actor]; oldWriter = NetMessage.buffer[256]; oldChest = Main.chest[ChestId];
        oldConfig = ServerTShock.Config; oldSscConfig = ServerTShock.ServerSideCharacterConfig; oldLog = ServerTShock.Log;
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true; Main.ServerSideCharacter = true;
        // Even Prefix(0) creates Main.rand when null. Own its state rather than leaking
        // initialization or consuming another fixture's random sequence.
        Main.rand = new Terraria.Utilities.UnifiedRandom(0x4D313743);
        Main.maxTilesX = 500; Main.maxTilesY = 500;
        Main.player[Actor] = new Player { whoAmI = Actor, active = true };
        // Native SetDefaults1(269..271) reads the dedicated server's local player colors.
        Main.player[255] = new Player { whoAmI = 255 };
        Netplay.Clients[Actor] = new RemoteClient { State = 10 };
        NetMessage.buffer[256] = new MessageBuffer();
        Main.chest[ChestId] = new Chest();
        ServerTShock.Config = new TShockConfig(); ServerTShock.ServerSideCharacterConfig = new ServerSideConfig();
        log = new TextLog(Path.Combine(Path.GetTempPath(), "m16-c01-" + Guid.NewGuid().ToString("N") + ".log"), false);
        ServerTShock.Log = log;
        actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, PlayerData = new PlayerData(false),
            Account = new UserAccount { ID = 1717, Name = "m16-c01" }, Group = new Group("m16-c01") };
        actor.PlayerData.CopyCharacter(actor); session = new(Guid.NewGuid(), 1, Actor, 1);
        business = new(TargetRuntime.Fingerprint, Path.Combine(Path.GetTempPath(), "absent-m16-c01-data"));
        faults.Clear(); business.IntegrityFault = (producer, error) => faults.Add(producer + ":" + error);
        for (int index = 0; index < (ItemID.Count + 48) / 128 + 2; index++) business.Update(_ => null, 1);
        Assert.That(GetCatalog(), Is.Not.Null, "nextItem=" + typeof(M2BusinessAdapter).GetField("nextItem", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(business) + " " + string.Join(",", faults));
        HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
        frames.Clear();
    }

    [TearDown]
    public void Cleanup()
    {
        HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.dedServ = oldDedicated; Main.ServerSideCharacter = oldSsc;
        Main.rand = oldRandom!;
        Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.player[Actor] = oldPlayer; Main.player[255] = oldServerPlayer;
        Netplay.Clients[Actor] = oldClient; NetMessage.buffer[256] = oldWriter; Main.chest[ChestId] = oldChest;
        ServerTShock.Config = oldConfig; ServerTShock.ServerSideCharacterConfig = oldSscConfig; ServerTShock.Log = oldLog; log.Dispose();
    }

    private void Capture(object? sender, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args) => frames.Add(args.ms.ToArray());
    private VersionedItemCatalog? GetCatalog() => (VersionedItemCatalog?)typeof(M2BusinessAdapter)
        .GetField("items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(business);

    [Test]
    public void ActualRuntimeCatalogIsBoundToTheLockedNativeTypeDomain()
    {
        Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Item).Assembly.Location))),
            Is.EqualTo("C7FF9B092B296C86E1EA91B9BA8426E9F4955B4FA62DB7EA7FCFAE2EE3D1D511"));
        var catalog = GetCatalog()!;
        Assert.That(catalog.Definitions.Count, Is.EqualTo(ItemID.Count + 48));
        Assert.That(catalog.MinimumNetId, Is.EqualTo(-48)); Assert.That(catalog.ItemIdExclusiveMax, Is.EqualTo(ItemID.Count));
        for (int id = -48; id < ItemID.Count; id++)
        {
            var native = new Item(); native.SetDefaults(id);
            Assert.That(catalog.Definitions[id].CanonicalId, Is.EqualTo(native.type), $"wire id {id}");
            Assert.That(catalog.Definitions[id].MaxStack, Is.EqualTo(native.maxStack), $"wire id {id}");
        }
    }

    [TestCase(5)] [TestCase(32)]
    public void EveryNativeNegativeAliasUsesTheSameCanonicalItemThroughActualCoreReceiverAndWriter(int packet)
    {
        for (short type = -48; type < 0; type++)
        {
            var expected = new Item(); expected.netDefaults(type);
            Assert.That(expected.type, Is.GreaterThan(0));
            var observed = Dispatch(packet, type, 1, 0, packet == 5 ? 58 : 0);
            Assert.That(observed.Result.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(observed.Native.type, Is.EqualTo(expected.type));
            Assert.That(observed.Native.stack, Is.EqualTo(1));
            if (packet == 5)
            {
                Assert.That(actor.ItemInHand.type, Is.EqualTo(expected.type));
                Assert.That(actor.PlayerData.inventory[58].NetId, Is.EqualTo(type), "SSC retains its real wire representation.");
            }
            Assert.That(observed.FrameType, Is.EqualTo(expected.type), "The actual serializer emits canonical type, never the incoming negative alias.");
        }
    }

    [TestCase(5)] [TestCase(32)]
    public void PositiveTypeBoundariesAndAirUnusedFieldsPassWithoutSourceHistory(int packet)
    {
        foreach (short type in new[] { (short)1, (short)ItemID.Wood, (short)(ItemID.Count - 1) })
        {
            var result = Dispatch(packet, type, 1, 0);
            Assert.That(result.Result.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(result.Native.type, Is.EqualTo(type));
            Assert.That(result.FrameType, Is.EqualTo(type));
        }
        var clear = Dispatch(packet, 0, -1, byte.MaxValue);
        Assert.That(clear.Result.Reason, Is.EqualTo("normal-air-clear"));
        Assert.That(clear.Native.IsAir, Is.True);
        Assert.That(clear.FrameType, Is.Zero);
    }

    [TestCase(5)] [TestCase(32)]
    public void InvalidGlobalDomainsAreBlockedBeforeCoreAndNativeAndAllowSameAccountRecovery(int packet)
    {
        Dispatch(packet, ItemID.Wood, 2, 0);
        foreach (var value in new[] { ((short)-49, (byte)0), ((short)ItemID.Count, (byte)0),
            ((short)short.MaxValue, (byte)0), ((short)ItemID.WoodenSword, (byte)PrefixID.Count), ((short)ItemID.WoodenSword, byte.MaxValue) })
        {
            var before = Current(packet, 10).Clone(); int sent = frames.Count;
            var blocked = Dispatch(packet, value.Item1, 1, value.Item2);
            Assert.That(blocked.Result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(blocked.Result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(blocked.CoreEntered, Is.False); Assert.That(blocked.NativeEntered, Is.False);
            Assert.That(Current(packet, 10).type, Is.EqualTo(before.type));
            Assert.That(Current(packet, 10).stack, Is.EqualTo(before.stack)); Assert.That(frames.Count, Is.EqualTo(sent));
        }
        Assert.That(Dispatch(packet, ItemID.Wood, 1, 0).Native.type, Is.EqualTo(ItemID.Wood));
    }

    [Test]
    public void InitialSscAndSpecialSlotsRetainNativeZeroStackFavoriteAndIgnoredFlagSemantics()
    {
        foreach (int slot in new[] { 10, 58, PlayerItemSlotID.TrashItem, PlayerItemSlotID.Bank4_0, PlayerItemSlotID.Count - 1 })
        {
            actor.HasSentInventory = false;
            var normal = Dispatch(5, ItemID.Wood, 1, 0, slot, 255);
            Assert.That(normal.Result.Action, Is.EqualTo(ControlAction.Pass)); Assert.That(normal.Native.favorited, Is.True);
            if (slot == PlayerItemSlotID.Count - 1) Assert.That(actor.HasSentInventory, Is.True);
            var clear = Dispatch(5, 0, 0, byte.MaxValue, slot, 255);
            Assert.That(clear.Result.Action, Is.EqualTo(ControlAction.Pass)); Assert.That(clear.Native.IsAir, Is.True);
        }
        actor.HasSentInventory = true;
        var zero = Dispatch(5, ItemID.Wood, 0, 0);
        Assert.That(zero.Result.Action, Is.Not.EqualTo(ControlAction.Block), "An unattributed zero-stack value cannot become a ban proof.");
        Assert.That(zero.FrameType, Is.Zero, "Actual packet5 writer canonicalizes an empty item.");
        Assert.That(zero.Native.IsAir, Is.True);
    }

    private Item Current(int packet, int slot) => packet == 5 ? new PlayerItemSlotID.SlotReference(actor.TPlayer, slot).Item : Main.chest[ChestId].item[slot];
    private sealed record Outcome(BusinessRuleResult Result, Item Native, int? FrameType, bool CoreEntered, bool NativeEntered);
    private Outcome Dispatch(int packet, short type, short stack, byte prefix, int slot = 10, byte flags = 0)
    {
        using var bodyStream = new MemoryStream(); using (var writer = new BinaryWriter(bodyStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (packet == 5) { writer.Write((byte)Actor); writer.Write((short)slot); }
            else { writer.Write((short)ChestId); writer.Write((byte)slot); }
            writer.Write(stack); writer.Write(prefix); writer.Write(type); if (packet == 5) writer.Write(flags);
        }
        byte[] body = bodyStream.ToArray();
        var args = new GetDataEventArgs { MsgID = (PacketTypes)packet, Index = 0, Length = body.Length + 1 };
        typeof(GetDataEventArgs).GetProperty(nameof(GetDataEventArgs.Msg))!.SetValue(args, new MessageBuffer { whoAmI = Actor, readBuffer = body });
        var parsed = M2PacketReader.Read(args, true); Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        var result = business.Evaluate(parsed.Packet!, session, actor, _ => (null, null), false).Single(x => x.RuleId == InventoryRules.RuleId);
        if (result.Action == ControlAction.Block) return new(result, Current(packet, slot), null, false, false);
        using var input = new MemoryStream(body, false);
        var core = typeof(GetDataHandlers).GetMethod(packet == 5 ? "HandlePlayerSlot" : "HandleChestItem", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool canceled = (bool)core.Invoke(null, [new GetDataHandlerArgs(actor, input)])!;
        if (canceled) return new(result, Current(packet, slot), null, true, false);
        var native = new MessageBuffer { whoAmI = Actor }; native.readBuffer[0] = (byte)packet; body.CopyTo(native.readBuffer, 1); native.ResetReader();
        int before = frames.Count; native.GetData(0, body.Length + 1, out int received); Assert.That(received, Is.EqualTo(packet));
        byte[]? frame = frames.Skip(before).LastOrDefault(x => x.Length > 10 && x[2] == packet);
        // Packet5 and32 share the type offset: three-byte frame header + six-byte body prefix.
        int? serialized = frame is null ? null : BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(9, 2));
        return new(result, Current(packet, slot), serialized, true, true);
    }
}
