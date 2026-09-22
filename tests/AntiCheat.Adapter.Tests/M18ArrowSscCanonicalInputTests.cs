using System.Buffers.Binary;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M18ArrowSourceResetIntervalTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void M18_ActualSscRestoreReplacesSameTypeWeaponButDoesNotEraseAlreadyProducedAttack(bool clientHasSscWorldInfo, bool executeNativeProjectileClear)
    {
        using var language = new M18NativeLanguageScope();
        var oldConfig = TShockAPI.TShock.Config; var oldSscConfig = TShockAPI.TShock.ServerSideCharacterConfig;
        var oldLog = TShockAPI.TShock.Log; bool oldSsc = Main.ServerSideCharacter; int oldGameMode = Main.GameMode;
        var oldClientPlayer = Main.clientPlayer;
        var oldBuffers = (MessageBuffer[])NetMessage.buffer.Clone();
        var oldClients = (RemoteClient[])Netplay.Clients.Clone();
        string evidenceDirectory = Environment.GetEnvironmentVariable("ANTICHEAT_M18_SSC_DRAFT_OUTPUT") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(evidenceDirectory);
        using var log = new TextLog(Path.Combine(evidenceDirectory, "ssc-native-restore.log"), false);
        try
        {
            TShockAPI.TShock.Config = new TShockConfig(); TShockAPI.TShock.ServerSideCharacterConfig = new ServerSideConfig(); TShockAPI.TShock.Log = log;
            Main.GameMode = 0;
            // RestoreCharacter broadcasts loadout before its targeted slot stream. The native
            // broadcaster examines every fixed buffer's broadcast flag, even unused slots.
            for (int index = 0; index < NetMessage.buffer.Length; index++) NetMessage.buffer[index] ??= new MessageBuffer();
            for (int index = 0; index < Netplay.Clients.Length; index++) Netplay.Clients[index] ??= new RemoteClient();
            // Explicit isolated authorized-host customization is the retained M7 legal source,
            // not a stock natural drop and not a production rule premise.
            Main.netMode = 2; Main.myPlayer = 255; Main.item[7].inner.damage = 1000;
            NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
            byte[] export = sent.Single(x => x.Length > 3 && x[2] == 88);
            Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.WoodenBow);
            Main.netMode = 1; Main.myPlayer = Actor; Receive(export[2..], 256);
            var clientPlayer = Main.player[Actor]; Main.clientPlayer = new Player { whoAmI = Actor };
            Assert.That(clientPlayer.GetItem(Main.item[7].inner, new GetItemSettings(NoText: true, NoSound: true)).IsAir, Is.True);
            int bowSlot = Array.FindIndex(clientPlayer.inventory, item => item.type == ItemID.WoodenBow);
            clientPlayer.selectedItemState.Select(bowSlot); var oldBow = clientPlayer.inventory[bowSlot];
            clientPlayer.inventory[54].SetDefaults(ItemID.WoodenArrow); clientPlayer.inventory[54].stack = 100;
            Assert.That(oldBow.damage, Is.EqualTo(1000)); sent.Clear();
            clientPlayer.ItemCheck_Shoot(Actor, oldBow, clientPlayer.GetWeaponDamage(oldBow), withAudioVisualFeedback: false);
            var oldArrow = Main.projectile.Single(p => p.active && p.type == ProjectileID.WoodenArrowFriendly);
            byte[] oldDeclaration = sent.Single(x => x.Length > 3 && x[2] == 27);
            Assert.That(oldArrow.damage, Is.EqualTo(1005)); uint oldKey = oldArrow.key.bits;
            // Run the real existing TShock SSC restoration and real network serializer on the
            // independent server player, then feed its actual5 stream to the same client object.
            Main.netMode = 2; Main.myPlayer = 255; Main.ServerSideCharacter = true;
            var serverPlayer = new Player { whoAmI = Actor, active = true, name = "M18SscInput" };
            serverPlayer.inventory[bowSlot].SetDefaults(ItemID.WoodenBow);
            serverPlayer.inventory[54].SetDefaults(ItemID.WoodenArrow); serverPlayer.inventory[54].stack = 100;
            Main.player[Actor] = serverPlayer;
            var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true,
                Account = new UserAccount { ID = 1807, Name = "M18SscInput" }, Group = new Group("m18-ssc"), PlayerData = new PlayerData(false) };
            actor.PlayerData.CopyCharacter(actor); sent.Clear();
            var restoreExceptions = new List<string>();
            var restoreSendCalls = new List<string>();
            void CaptureRestoreSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
            { if (restoreSendCalls.Count < 16) restoreSendCalls.Add($"{args.msgType}:remote{args.remoteClient}:ignore{args.ignoreClient}:number{args.number}"); }
            void CaptureRestoreException(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
            { if (restoreExceptions.Count < 8) restoreExceptions.Add(args.Exception.ToString()); }
            AppDomain.CurrentDomain.FirstChanceException += CaptureRestoreException;
            HookEvents.Terraria.NetMessage.SendData += CaptureRestoreSend;
            try { actor.PlayerData.RestoreCharacter(actor); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= CaptureRestoreException; HookEvents.Terraria.NetMessage.SendData -= CaptureRestoreSend; }
            foreach (string error in restoreExceptions) TestContext.Out.WriteLine(error);
            TestContext.Out.WriteLine(string.Join(",", restoreSendCalls));
            byte[][] restoration = sent.Where(x => x.Length > 3 && x[2] is 5 or 50 && x[3] == Actor).ToArray();
            Assert.That(restoration.Count(x => x[2] == 5), Is.GreaterThanOrEqualTo(350));
            int[] slots = restoration.Where(x => x[2] == 5).Select(x => (int)BinaryPrimitives.ReadInt16LittleEndian(x.AsSpan(4))).Distinct().Order().ToArray();
            Assert.That(slots.Length, Is.EqualTo(NetItem.MaxInventory));
            Assert.That(actor.IgnoreSSCPackets, Is.False);
            Main.player[Actor] = clientPlayer; Main.netMode = 1; Main.myPlayer = Actor;
            Main.ServerSideCharacter = clientHasSscWorldInfo;
            foreach (byte[] frame in restoration) Receive(frame[2..], 256);
            Assert.That(Main.player[Actor], Is.SameAs(clientPlayer), "Actual SSC receive on same live client object, without reload.");
            Assert.That(ReferenceEquals(clientPlayer.inventory[bowSlot], oldBow), Is.EqualTo(!clientHasSscWorldInfo));
            Assert.That(clientPlayer.inventory[bowSlot].type, Is.EqualTo(ItemID.WoodenBow));
            Assert.That(clientPlayer.inventory[bowSlot].damage, Is.EqualTo(clientHasSscWorldInfo ? 4 : 1000));
            if (executeNativeProjectileClear)
            {
                // Exact native sub-entry called by WorldGen.clearWorld during normal client
                // join state4. This is a separate sub-entry experiment, not a full reconnect.
                Projectile.ClearAll();
                Assert.That(Main.projectile.All(p => !p.active), Is.True);
                Assert.That(Main.projectile.Any(p => ReferenceEquals(p, oldArrow)), Is.False);
            }
            clientPlayer.ResetEffects(); clientPlayer.UpdateBuffs(Actor); sent.Clear();
            clientPlayer.ItemCheck_Shoot(Actor, clientPlayer.inventory[bowSlot], clientPlayer.GetWeaponDamage(clientPlayer.inventory[bowSlot]), withAudioVisualFeedback: false);
            var freshArrow = Main.projectile.Single(p => p.active && p.type == ProjectileID.WoodenArrowFriendly && !ReferenceEquals(p, oldArrow));
            byte[] newDeclaration = sent.Single(x => x.Length > 3 && x[2] == 27);
            Assert.That(freshArrow.damage, Is.EqualTo(clientHasSscWorldInfo ? 9 : 1005));
            Assert.That(Main.projectile.Any(p => ReferenceEquals(p, oldArrow) && p.active), Is.EqualTo(!executeNativeProjectileClear));
            var target = ResetTarget(); var actualAttack = executeNativeProjectileClear ? freshArrow : oldArrow;
            actualAttack.Center = target.Center; sent.Clear(); int before = target.life;
            actualAttack.Damage();
            byte[] delayedHit = sent.Single(x => x.Length > 3 && x[2] == 28);
            Assert.That(target.life, Is.LessThan(before));
            Assert.That(BinaryPrimitives.ReadInt16LittleEndian(delayedHit.AsSpan(5)) > 900, Is.EqualTo(!executeNativeProjectileClear));
            Save(!clientHasSscWorldInfo ? "m18-ssc-input-without-client-worldinfo.json" : executeNativeProjectileClear ? "m18-ssc-input-with-native-clear.json" : "m18-ssc-input-distinction.json", new
            {
                source = "real TShock PlayerData.RestoreCharacter -> real SendData5/50 -> actual native client GetData -> actual ItemCheck_Shoot/Damage",
                restoreFrames = restoration.Length, inventoryFrames = restoration.Count(x => x[2] == 5), distinctCanonicalSlotIds = slots,
                clientHasSscWorldInfo,
                sameClientObject = true, oldWeaponDamage = oldBow.damage, freshWeaponDamage = clientPlayer.inventory[bowSlot].damage,
                oldArrowDamage = 1005, newArrowDamage = freshArrow.damage, oldArrowKey = oldKey,
                executeNativeProjectileClear, freshKey = freshArrow.key.bits,
                oldDeclaration = Convert.ToHexString(oldDeclaration), newDeclaration = Convert.ToHexString(newDeclaration), delayedHit = Convert.ToHexString(delayedHit),
                targetBefore = before, targetAfter = target.life,
                conclusion = !clientHasSscWorldInfo ? "Without client SSC world-info state, the native client ignores own-slot5 resets and a fresh attack still uses1005. Server SSC enabled alone is not a receipt premise."
                    : executeNativeProjectileClear ? "Actual Projectile.ClearAll removes the old attack from the active engine array; fresh native attack uses canonical9. This is the audited world-clear sub-entry only."
                    : "Real SSC restore reconstructs same-type held item for NEW attacks; it does not erase an already-produced attack in the same live client world.",
                limits = "Native same-process SSC restore and optional exact Projectile.ClearAll sub-entry only, not full transport disconnect/world-clear/reconnect. No production health flag, source-complete flag, narrowed allowed set or new sanction. Full reconnect cleanup/order remains a distinct next experiment."
            });
        }
        finally
        {
            TShockAPI.TShock.Config = oldConfig; TShockAPI.TShock.ServerSideCharacterConfig = oldSscConfig; TShockAPI.TShock.Log = oldLog;
            Main.ServerSideCharacter = oldSsc; Main.GameMode = oldGameMode; Main.clientPlayer = oldClientPlayer;
            Array.Copy(oldBuffers, NetMessage.buffer, oldBuffers.Length);
            for (int index = 0; index < oldClients.Length; index++)
                if (oldClients[index] is null) Netplay.Clients[index].Socket?.Close();
            Array.Copy(oldClients, Netplay.Clients, oldClients.Length);
        }
    }

}
