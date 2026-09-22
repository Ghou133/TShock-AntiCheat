using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Read-only bridge from the existing QA scaffold to the candidate's
/// bounded native reward witness. It never creates, changes, or removes an item.
/// </summary>
public sealed partial class GameplayScaffold
{
    private static readonly BindingFlags PrivateM18Reward = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int M18RewardTraceCapacity = 256;
    private List<object>? m18RewardTrace;
    private int m18RewardTraceNpcSlot = -1;
    private int m18RewardTraceNpcGeneration;
    private int m18RewardTraceNpcType;
    private Hook? m18RewardItemHook;
    private Hook? m18RewardNpcLootHook;
    private delegate int NativeM18RewardNewItem(IEntitySource source, int x, int y, int width, int height,
        int type, int stack, bool noBroadcast, int prefix, NewItemOwnership ownership,
        Vector2? velocity, Item.NewItemModifier modifier);
    private delegate int AroundM18RewardNewItem(NativeM18RewardNewItem original, IEntitySource source,
        int x, int y, int width, int height, int type, int stack, bool noBroadcast, int prefix,
        NewItemOwnership ownership, Vector2? velocity, Item.NewItemModifier modifier);
    private delegate void NativeM18RewardNpcLoot(NPC target);
    private delegate void AroundM18RewardNpcLoot(NativeM18RewardNpcLoot original, NPC target);

    private void M18RewardStateCommand(string[] arguments)
    {
        Require(arguments.Length == 1, "Use qa_m18_reward_state <exact authenticated player>.");
        var player = ResolvePlayer(arguments[0]);
        var plugin = ServerApi.Plugins.Select(x => x.Plugin)
            .SingleOrDefault(x => x.GetType().FullName == "AntiCheat.Plugin.TShock.AntiCheatPlugin");
        Require(plugin is not null, "The AntiCheat candidate plugin is required for the reward witness.");
        var antiCheat = plugin!;

        var bindings = antiCheat.GetType().GetField("_bindings", PrivateM18Reward)?.GetValue(antiCheat) as Array;
        var binding = bindings?.GetValue(player.Index);
        var session = binding?.GetType().GetProperty("Key")?.GetValue(binding);
        var observer = antiCheat.GetType().GetField("_npcStrikeCauses", PrivateM18Reward)?.GetValue(antiCheat);
        var capture = observer?.GetType().GetMethod("CaptureClientImportantItemReward", BindingFlags.Public | BindingFlags.Instance);
        object? reward = session is null || capture is null ? null : capture.Invoke(observer, [session]);
        var trace = StopM18RewardTrace();
        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            fixtureArtificial = false,
            readOnly = true,
            actor = player.Name,
            accountId = player.Account!.ID,
            playerIndex = player.Index,
            expertMode = Main.expertMode,
            masterMode = Main.masterMode,
            reward,
            nativeTrace = trace,
            activeWorldItems = Main.item.Select((item, index) => new
            {
                index,
                item.active,
                item.type,
                item.stack,
                item.maxStack,
                reservedFor = item.playerIndexTheItemIsReservedFor,
                x = item.position.X,
                y = item.position.Y
            }).Where(item => item.active && item.type > 0).Take(256).ToArray(),
            note = "Read-only TestLab state bridge. A native loot observation is not creator proof, GUI evidence, or formal qualification."
        };
        string marker = Path.Combine(root!, "m18-reward-state-" + player.Index + ".json");
        File.WriteAllText(marker, JsonSerializer.Serialize(payload, jsonOptions));
        Record("m18-important-reward-state", payload);
        TSPlayer.Server.SendInfoMessage("M18 reward witness state written: " + marker + "; reward=" + (reward is null ? "none" : "present"));
    }

    private void M18RewardTraceCommand(string[] arguments)
    {
        int npcSlot = -1;
        Require(arguments.Length == 1 && int.TryParse(arguments[0], out npcSlot),
            "Use qa_m18_reward_trace <native NPC slot>." );
        Require((uint)npcSlot < (uint)Main.npc.Length, "The native reward trace NPC slot is outside Main.npc.");
        var npc = Main.npc[npcSlot];
        Require(npc.active && npc.life > 0 && npc.generation > 0 && npc.type == 50,
            "The native reward trace requires an active native King Slime target.");
        StopM18RewardTrace();
        m18RewardTrace = new List<object>(M18RewardTraceCapacity);
        m18RewardTraceNpcSlot = npc.whoAmI;
        m18RewardTraceNpcGeneration = npc.generation;
        m18RewardTraceNpcType = npc.type;
        var method = typeof(Item).GetMethod(nameof(Item.NewItem), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(IEntitySource), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                typeof(bool), typeof(int), typeof(NewItemOwnership), typeof(Vector2?), typeof(Item.NewItemModifier)],
            modifiers: null) ?? throw new MissingMethodException("Terraria.Item.NewItem reward overload");
        m18RewardItemHook = new Hook(method, (AroundM18RewardNewItem)AroundRewardNewItem);
        var lootMethod = typeof(NPC).GetMethod(nameof(NPC.NPCLoot), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new MissingMethodException("Terraria.NPC.NPCLoot");
        m18RewardNpcLootHook = new Hook(lootMethod, (AroundM18RewardNpcLoot)AroundRewardNpcLoot);
        HookEvents.Terraria.NPC.NPCLoot += OnM18RewardTraceLoot;
        HookEvents.Terraria.NPC.NPCLoot_DropItems += OnM18RewardTraceDropItems;
        HookEvents.Terraria.NetMessage.SendData += OnM18RewardTraceSend;
        OTAPI.Hooks.NPC.BossBag += OnM18RewardTraceBossBag;
        OTAPI.Hooks.NPC.DropLoot += OnM18RewardTraceDropLoot;
        Record("m18-reward-native-trace-armed", new
        {
            npcSlot = m18RewardTraceNpcSlot,
            npcGeneration = m18RewardTraceNpcGeneration,
            npcType = m18RewardTraceNpcType,
            capacity = M18RewardTraceCapacity,
            readOnly = true,
            fixtureArtificial = false,
            note = "Passive native reward trace; it does not create, cancel, or rewrite loot."
        });
        TSPlayer.Server.SendInfoMessage("M18 native reward trace armed for NPC slot " + npcSlot + ".");
    }

    private object[] StopM18RewardTrace()
    {
        var trace = m18RewardTrace?.ToArray() ?? Array.Empty<object>();
        DisposeM18RewardTrace();
        return trace;
    }

    private void DisposeM18RewardTrace()
    {
        if (m18RewardTrace is not null)
        {
            HookEvents.Terraria.NPC.NPCLoot -= OnM18RewardTraceLoot;
            HookEvents.Terraria.NPC.NPCLoot_DropItems -= OnM18RewardTraceDropItems;
            HookEvents.Terraria.NetMessage.SendData -= OnM18RewardTraceSend;
            OTAPI.Hooks.NPC.BossBag -= OnM18RewardTraceBossBag;
            OTAPI.Hooks.NPC.DropLoot -= OnM18RewardTraceDropLoot;
        }
        m18RewardItemHook?.Dispose();
        m18RewardItemHook = null;
        m18RewardNpcLootHook?.Dispose();
        m18RewardNpcLootHook = null;
        m18RewardTrace = null;
        m18RewardTraceNpcSlot = -1;
        m18RewardTraceNpcGeneration = 0;
        m18RewardTraceNpcType = 0;
    }

    private int AroundRewardNewItem(NativeM18RewardNewItem original, IEntitySource source,
        int x, int y, int width, int height, int type, int stack, bool noBroadcast, int prefix,
        NewItemOwnership ownership, Vector2? velocity, Item.NewItemModifier modifier)
    {
        int index = original(source, x, y, width, height, type, stack, noBroadcast, prefix, ownership, velocity, modifier);
        if (type == 3318 && m18RewardTrace is not null)
        {
            bool validIndex = (uint)index < (uint)Main.item.Length;
            var item = validIndex ? Main.item[index] : null;
            AddM18RewardTrace(new
            {
                kind = "Item.NewItem",
                utc = DateTimeOffset.UtcNow,
                requestedType = type,
                requestedStack = stack,
                noBroadcast,
                ownership = ownership.ToString(),
                returnIndex = index,
                validIndex,
                createdActive = item?.active ?? false,
                createdType = item?.type ?? 0,
                createdStack = item?.stack ?? 0,
                createdMaxStack = item?.maxStack ?? 0,
                createdReservedFor = item?.playerIndexTheItemIsReservedFor ?? -1,
                createdX = item?.position.X ?? 0,
                createdY = item?.position.Y ?? 0,
                thread = Environment.CurrentManagedThreadId
            });
        }
        return index;
    }

    private void AroundRewardNpcLoot(NativeM18RewardNpcLoot original, NPC target)
    {
        string? exceptionType = null;
        try
        {
            original(target);
        }
        catch (Exception error)
        {
            exceptionType = error.GetType().FullName;
            throw;
        }
        finally
        {
            if (MatchesM18RewardTrace(target))
            {
                AddM18RewardTrace(new
                {
                    kind = "NPC.NPCLoot.return",
                    utc = DateTimeOffset.UtcNow,
                    npcSlot = target.whoAmI,
                    npcGeneration = target.generation,
                    npcType = target.type,
                    active = target.active,
                    life = target.life,
                    exceptionType,
                    importantItems = Main.item.Select((item, index) => new
                    {
                        index,
                        item.active,
                        item.type,
                        item.stack,
                        item.maxStack,
                        reservedFor = item.playerIndexTheItemIsReservedFor,
                        x = item.position.X,
                        y = item.position.Y
                    }).Where(item => item.active && item.type == 3318).Take(16).ToArray(),
                    thread = Environment.CurrentManagedThreadId
                });
            }
        }
    }

    private bool MatchesM18RewardTrace(NPC npc) => m18RewardTrace is not null &&
        npc.whoAmI == m18RewardTraceNpcSlot && npc.generation == m18RewardTraceNpcGeneration &&
        npc.type == m18RewardTraceNpcType;

    private void AddM18RewardTrace(object payload)
    {
        try
        {
            if (m18RewardTrace is null || m18RewardTrace.Count >= M18RewardTraceCapacity) return;
            m18RewardTrace.Add(payload);
            Record("m18-reward-native-trace", payload);
        }
        catch
        {
            // A passive trace must never change the native loot outcome.
        }
    }

    private void OnM18RewardTraceLoot(NPC npc, HookEvents.Terraria.NPC.NPCLootEventArgs args)
    {
        if (!MatchesM18RewardTrace(npc)) return;
        AddM18RewardTrace(new
        {
            kind = "NPCLoot",
            utc = DateTimeOffset.UtcNow,
            npcSlot = npc.whoAmI,
            npcGeneration = npc.generation,
            npcType = npc.type,
            continueExecution = args.ContinueExecution,
            thread = Environment.CurrentManagedThreadId
        });
    }

    private void OnM18RewardTraceDropItems(NPC npc, HookEvents.Terraria.NPC.NPCLoot_DropItemsEventArgs args)
    {
        if (!MatchesM18RewardTrace(npc)) return;
        AddM18RewardTrace(new
        {
            kind = "NPCLoot_DropItems",
            utc = DateTimeOffset.UtcNow,
            npcSlot = npc.whoAmI,
            npcGeneration = npc.generation,
            npcType = npc.type,
            closestPlayer = args.closestPlayer,
            continueExecution = args.ContinueExecution,
            thread = Environment.CurrentManagedThreadId
        });
    }

    private void OnM18RewardTraceSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (m18RewardTrace is null || args.msgType != 90 || (uint)args.number >= (uint)Main.item.Length)
            return;
        var item = Main.item[args.number];
        if (!item.active || item.type != 3318 || item.stack <= 0) return;
        AddM18RewardTrace(new
        {
            kind = "NetMessage.SendData",
            utc = DateTimeOffset.UtcNow,
            packet = args.msgType,
            remoteClient = args.remoteClient,
            ignoreClient = args.ignoreClient,
            itemIndex = args.number,
            itemType = item.type,
            stack = item.stack,
            maxStack = item.maxStack,
            reservedFor = item.playerIndexTheItemIsReservedFor,
            x = item.position.X,
            y = item.position.Y,
            continueExecution = args.ContinueExecution,
            thread = Environment.CurrentManagedThreadId
        });
    }

    private void OnM18RewardTraceBossBag(object? sender, OTAPI.Hooks.NPC.BossBagEventArgs args)
    {
        if (args.Npc is not { } npc || !MatchesM18RewardTrace(npc)) return;
        AddM18RewardTrace(new
        {
            kind = "BossBag",
            utc = DateTimeOffset.UtcNow,
            npcSlot = npc.whoAmI,
            npcGeneration = npc.generation,
            npcType = npc.type,
            itemType = args.Type,
            stack = args.Stack,
            result = args.Result.ToString(),
            noBroadcast = args.NoBroadcast,
            ownership = args.Ownership.ToString(),
            hasSource = args.Source is not null,
            thread = Environment.CurrentManagedThreadId
        });
    }

    private void OnM18RewardTraceDropLoot(object? sender, OTAPI.Hooks.NPC.DropLootEventArgs args)
    {
        if (args.Npc is not { } npc || !MatchesM18RewardTrace(npc)) return;
        AddM18RewardTrace(new
        {
            kind = "DropLoot",
            utc = DateTimeOffset.UtcNow,
            npcSlot = npc.whoAmI,
            npcGeneration = npc.generation,
            npcType = npc.type,
            eventStage = args.Event.ToString(),
            itemType = args.Type,
            stack = args.Stack,
            itemIndex = args.ItemIndex,
            result = args.Result.ToString(),
            noBroadcast = args.NoBroadcast,
            ownership = args.Ownership.ToString(),
            hasSource = args.Source is not null,
            thread = Environment.CurrentManagedThreadId
        });
    }
}
