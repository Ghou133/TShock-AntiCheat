using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M5ContainerRaceTests
{
    [Test]
    public void NativeSwitchLeavesOldChestUsableBeforeReplyAndServerFieldsAreNotClientAcknowledgement()
    {
        const int slot = 16;
        var oldPlayer = Main.player[slot]; var oldChests = Main.chest; var oldIndex = Chest._chestsByCoords;
        var oldConfig = ServerTShock.Config; var oldMode = Main.netMode; var oldLocal = Main.myPlayer;
        var oldWidth = Main.maxTilesX; var oldHeight = Main.maxTilesY; var oldSplit = Main.stackSplit;
        var oldTile = Main.tile[22, 20]; var oldEdit = Main.editChest; var oldRight = Main.mouseRightRelease;
        var oldChat = Main.npcChatText; var oldChatCorner = Main.npcChatCornerItem;
        var sends = new List<(int Type, int Target, int Slot)>();
        void Sink(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
        { sends.Add((args.msgType, args.number, (int)args.number2)); args.ContinueExecution = false; }
        HookEvents.Terraria.NetMessage.SendData += Sink;
        try
        {
            Main.maxTilesX = 500; Main.maxTilesY = 500; Main.myPlayer = slot;
            Main.chest = new Chest[8000]; Chest._chestsByCoords = [];
            var first = Chest.CreateWorldChest(0, 20, 20); var second = Chest.CreateWorldChest(1, 22, 20);
            first.item[0].SetDefaults(ItemID.Wood); first.item[0].stack = 100;
            second.item[0].SetDefaults(ItemID.Wood); second.item[0].stack = 100;
            Main.tile[22, 20] = new Tile { type = TileID.Containers, frameX = 0, frameY = 0 };
            Main.editChest = false;
            ServerTShock.Config = new TShockConfig(); ServerTShock.Config.Settings.RegionProtectChests = false;
            ServerTShock.Config.Settings.RangeChecks = true;
            var serverPlayer = new Player { whoAmI = slot, active = true, position = new(320, 320), chest = 0 };
            var clientPlayer = new Player { whoAmI = slot, active = true, position = new(320, 320), chest = 0,
                chestX = 20, chestY = 20, tileInteractAttempted = true, releaseUseTile = true };
            Main.player[slot] = serverPlayer;
            var actor = new TSPlayer(slot) { IsLoggedIn = true, ActiveChest = 0,
                Account = new UserAccount { ID = 160, Name = "m5-switch" } };
            var session = new SessionKey(Guid.NewGuid(), 1, slot, 1);
            using var contexts = new M3InventoryContexts("target326-container-race",
                i => i == slot ? (session, actor, true) : (null, null, false), (_, _, _) => false);
            contexts.ObserveOpen(session, actor, first.x, first.y, false);
            Assert.That(contexts.EvaluateContainerWrite(session, actor, 0, 0).Action, Is.EqualTo(ControlAction.Pass));
            Main.netMode = 1; Main.player[slot] = clientPlayer;
            clientPlayer.TileInteractionsUse(second.x, second.y);
            Assert.That(sends.Any(s => s.Type == 31 && s.Target == second.x && s.Slot == second.y), Is.True);
            Assert.That(clientPlayer.chest, Is.EqualTo(0), "The native switch sends31 but does not close the previous chest.");
            Assert.That(Main.stackSplit, Is.EqualTo(600));
            Main.netMode = 2; Main.player[slot] = serverPlayer;
            contexts.ObserveOpen(session, actor, second.x, second.y, false);
            serverPlayer.chest = 1; actor.ActiveChest = 1; // Audited native31 + coreBouncer accepted-open effects.
            // Deliver no33 reply yet. The client still has the legitimately open first chest.
            Main.netMode = 1; Main.player[slot] = clientPlayer;
            Assert.That(Main.LocalPlayerHasPendingInventoryActions(), Is.False);
            Assert.That(CraftingRequests.CanCraftLocally(new(ItemID.Wood, 10), [first]), Is.True);
            int remaining = CraftingRequests.Consume(new(ItemID.Wood, 10), [first], null, fromChests: true);
            Assert.That(remaining, Is.Zero); Assert.That(first.item[0].stack, Is.EqualTo(90));
            Assert.That(sends.Last(s => s.Type == 32), Is.EqualTo((32, 0, 0)));
            Main.netMode = 2; Main.player[slot] = serverPlayer;
            var decision = contexts.EvaluateContainerWrite(session, actor, 0, 0);
            TestContext.Out.WriteLine($"Actual native31B→native open-chest consumption32A; receiver decision={decision.Verdict}/{decision.Reason}");
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.Unknown),
                "Server acceptance is not proof that the client has received33B; keep this native old-chest write nonpunitive.");
            Assert.That(decision.Facts["serverChestAligned"], Is.EqualTo("True"));
            Assert.That(decision.Facts["leaseConfirmed"], Is.EqualTo("False"));
            Assert.That(contexts.ConfirmedContainerGrants, Is.EqualTo(1), "Only the earlier client-confirmedA lease exists so far.");
            for (int i = 0; i < 3; i++)
                Assert.That(contexts.EvaluateContainerWrite(session, actor, 0, 0).Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(contexts.EvaluateContainerWrite(session, actor, 1, second.maxItems).Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(contexts.ConfirmedContainerGrants, Is.EqualTo(1), "An invalid slot cannot acknowledge the target.");
            Main.netMode = 1; Main.player[slot] = clientPlayer;
            var reply = new MessageBuffer { whoAmI = 256 };
            using (var body = new BinaryWriter(new MemoryStream(reply.readBuffer)))
            {
                body.Write((byte)33); body.Write((short)1); body.Write((short)22); body.Write((short)20); body.Write((byte)0);
            }
            reply.GetData(0, 8, out int replyType);
            Assert.That(replyType, Is.EqualTo(33)); Assert.That(clientPlayer.chest, Is.EqualTo(1));
            Assert.That(CraftingRequests.Consume(new(ItemID.Wood, 10), [second], null, fromChests: true), Is.Zero);
            Assert.That(second.item[0].stack, Is.EqualTo(90)); Assert.That(sends.Last(s => s.Type == 32), Is.EqualTo((32, 1, 0)));
            Main.netMode = 2; Main.player[slot] = serverPlayer;
            var confirmation = contexts.EvaluateContainerWrite(session, actor, 1, 0);
            Assert.That(confirmation.Action, Is.EqualTo(ControlAction.Pass));
            Assert.That(contexts.ConfirmedContainerGrants, Is.EqualTo(2));
            var firstIllegal = contexts.EvaluateContainerWrite(session, actor, 0, 0);
            Assert.That(firstIllegal.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(firstIllegal.Version, Is.EqualTo("1.1.0"));
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendData -= Sink; Main.player[slot] = oldPlayer; Main.chest = oldChests;
            Chest._chestsByCoords = oldIndex; ServerTShock.Config = oldConfig; Main.netMode = oldMode; Main.myPlayer = oldLocal;
            Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.stackSplit = oldSplit; Main.tile[22, 20] = oldTile;
            Main.editChest = oldEdit; Main.mouseRightRelease = oldRight; Main.npcChatText = oldChat; Main.npcChatCornerItem = oldChatCorner;
        }
    }
}
