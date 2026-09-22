using System.Buffers.Binary;
using TShockAPI;
using TShockAPI.DB;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

/// <summary>Native method experiments only. No GUI, synthetic frame is labelled a natural player action,
/// or client-side attack-source observation is claimed to exist on the server.</summary>
[TestFixture, NonParallelizable]
public sealed partial class M18ArrowSourceResetIntervalTests
{
    private const int Actor = 7, TargetSlot = 11;
    private Player[] oldPlayers = null!;
    private NPC[] oldNpcs = null!;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldProjectileMap = null!;
    private int[] oldProjectileGenerations = null!;
    private CombatText[] oldText = null!;
    private Dust[] oldDust = null!;
    private WorldItem oldItem = null!;
    private RemoteClient oldClient = null!;
    private RemoteServer oldConnection = null!;
    private MessageBuffer oldBuffer = null!;
    private MessageBuffer oldActorBuffer = null!;
    private int oldMode, oldLocal;
    private bool oldDedicated;
    private readonly List<byte[]> sent = [];
    private Terraria.Utilities.UnifiedRandom oldRandom = null!;
    private Gore[] oldGore = null!;
    private int oldMouseX, oldMouseY;
    private Vector2 oldScreen;


    [SetUp]
    public void SetUp()
    {
        oldRandom = Main.rand; oldGore = Main.gore;
        oldMouseX = Main.mouseX; oldMouseY = Main.mouseY; oldScreen = Main.screenPosition;
        Main.rand = new Terraria.Utilities.UnifiedRandom(1704);
        Main.gore = Enumerable.Range(0, 601).Select(_ => new Gore { active = true }).ToArray();
        Main.mouseX = 800; Main.mouseY = 400; Main.screenPosition = Vector2.Zero;
        oldPlayers = Main.player; oldNpcs = Main.npc; oldProjectiles = Main.projectile;
        oldProjectileMap = Projectile.keyToIndex; oldProjectileGenerations = Projectile.slotGenerations;
        oldText = Main.combatText; oldDust = Main.dust; oldItem = Main.item[7];
        oldMode = Main.netMode; oldLocal = Main.myPlayer; oldDedicated = Main.dedServ;
        oldClient = Netplay.Clients[Actor]; oldConnection = Netplay.Connection; oldBuffer = NetMessage.buffer[256];
        oldActorBuffer = NetMessage.buffer[Actor];
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
        Main.player[Actor].active = true; Main.player[Actor].position = new(400, 400); Main.player[Actor].direction = 1;
        Main.npc = Enumerable.Range(0, Main.maxNPCs).Select(i => new NPC { whoAmI = i }).ToArray();
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001]; Projectile.slotGenerations = new int[1001];
        Main.combatText = Enumerable.Range(0, 100).Select(_ => new CombatText { active = true }).ToArray();
        Main.dust = Enumerable.Range(0, 6001).Select(_ => new Dust { active = true }).ToArray();
        Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
        Main.netMode = 1; Main.myPlayer = Actor; Main.dedServ = true;
        Netplay.Clients[Actor] = new RemoteClient { State = 10, Socket = new Terraria.Net.Sockets.TcpSocket() };
        NetMessage.buffer[Actor] = new MessageBuffer();
        Netplay.Connection = new RemoteServer { PendingTermination = true }; NetMessage.buffer[256] = new MessageBuffer();
        HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
        HookEvents.Terraria.NetMessage.SendPacketToServer += Suppress;
        sent.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture;
        HookEvents.Terraria.NetMessage.SendPacketToServer -= Suppress;
        Main.rand = oldRandom; Main.gore = oldGore;
        Main.mouseX = oldMouseX; Main.mouseY = oldMouseY; Main.screenPosition = oldScreen;
        Main.player = oldPlayers; Main.npc = oldNpcs; Main.projectile = oldProjectiles;
        Projectile.keyToIndex = oldProjectileMap; Projectile.slotGenerations = oldProjectileGenerations;
        Main.combatText = oldText; Main.dust = oldDust; Main.item[7] = oldItem;
        Main.netMode = oldMode; Main.myPlayer = oldLocal; Main.dedServ = oldDedicated;
        Netplay.Clients[Actor].Socket.Close();
        Netplay.Clients[Actor] = oldClient; Netplay.Connection = oldConnection; NetMessage.buffer[256] = oldBuffer;
        NetMessage.buffer[Actor] = oldActorBuffer;
    }

    private void Capture(object? sender, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args) => sent.Add(args.ms.ToArray());
    private static void Suppress(object? sender, HookEvents.Terraria.NetMessage.SendPacketToServerEventArgs args) => args.ContinueExecution = false;
    private static void Save(string name, object witnesses)
    {
        string directory = Environment.GetEnvironmentVariable("ANTICHEAT_M18_RESET_WITNESS_OUTPUT") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, target = "Terraria 1.4.5.8", evidenceTier = "native-dispatch-and-receiver-with-explicit-fixture-phases",
            pluginHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(M18ArrowSourceResetIntervals).Assembly.Location))),
            tshockApiHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(TSPlayer).Assembly.Location))),
            runtimeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(Main).Assembly.Location))),
            witnesses
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(path); TestContext.Out.WriteLine(path);
    }

    private sealed class M18NativeLanguageScope : IDisposable
    {
        private readonly Terraria.Localization.LanguageManager original = Terraria.Localization.LanguageManager.Instance;
        private readonly List<Action> restore = [];
        public M18NativeLanguageScope()
        {
            // Same native language initialization and restoration as existing C01 source tests.
            foreach (var field in typeof(Lang).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
            {
                if (field.IsLiteral) continue;
                object? value = field.GetValue(null);
                if (value is Array array)
                {
                    var copy = (Array)array.Clone();
                    restore.Add(() => { Array.Copy(copy, array, array.Length); if (!field.IsInitOnly) field.SetValue(null, array); });
                }
                else if (value is System.Collections.IDictionary dictionary)
                {
                    var copy = dictionary.Keys.Cast<object>().Select(key => new System.Collections.DictionaryEntry(key, dictionary[key])).ToArray();
                    restore.Add(() => { dictionary.Clear(); foreach (var entry in copy) dictionary.Add(entry.Key, entry.Value); if (!field.IsInitOnly) field.SetValue(null, dictionary); });
                }
                else if (!field.IsInitOnly) restore.Add(() => field.SetValue(null, value));
            }
            Terraria.Localization.LanguageManager.Instance = new();
            Terraria.Localization.LanguageManager.Instance.LoadLanguage(Terraria.Localization.GameCulture.FromName("en-US"));
            Lang.InitializeLegacyLocalization();
        }
        public void Dispose()
        {
            Terraria.Localization.LanguageManager.Instance = original;
            for (int index = restore.Count - 1; index >= 0; index--) restore[index]();
        }
    }
    private NPC ResetTarget()
    {
        var npc = new NPC { whoAmI = TargetSlot }; npc.SetDefaults(NPCID.BlueSlime);
        npc.active = true; npc.generation = 3; npc.life = npc.lifeMax = 5000; npc.defense = 0;
        npc.position = new(400, 400); npc.noTileCollide = true; npc.takenDamageMultiplier = 1; npc.knockBackResist = 0;
        Main.npc[TargetSlot] = npc; return npc;
    }
    private static void Receive(byte[] frame, int sender)
    {
        var buffer = new MessageBuffer { whoAmI = sender }; frame.CopyTo(buffer.readBuffer, 0); buffer.ResetReader();
        buffer.GetData(0, frame.Length, out _);
    }

}
