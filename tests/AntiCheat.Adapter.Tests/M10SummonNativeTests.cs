using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Creative;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7CombatNativeEvidenceTests
{
    [Test]
    public void M10_NativeSlimeReplacementSends29BeforeNew27()
    {
        var player = Main.player[Actor]; player.maxMinions = 1;
        var staff = player.inventory[0]; staff.SetDefaults(ItemID.SlimeStaff);
        player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), withAudioVisualFeedback: false);
        var first = Main.projectile.Single(x => x.active && x.type == 266);
        Assert.That(first.minionSlots, Is.EqualTo(1)); sent.Clear();
        // These are the two actual target methods in their audited ItemCheck order, not GUI input.
        player.FreeUpPetsAndMinions(staff);
        Assert.That(first.active, Is.False);
        player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), withAudioVisualFeedback: false);
        var packets = sent.Where(frame => frame[2] is 27 or 29).ToArray();
        Assert.That(packets.Select(frame => frame[2]), Is.EqualTo(new byte[] { 29, 27 }));
        Assert.That(Main.projectile.Count(x => x.active && x.owner == Actor && x.minion), Is.EqualTo(1));
        TestContext.Out.WriteLine("Native SlimeStaff FreeUpPetsAndMinions -> old Kill sends29 and deactivates -> ItemCheck_Shoot sends27. This is a native mechanism experiment; GUI is separate.");
    }

    [Test]
    public void M10_NativeSentryReplacementHasOppositeOrderAndIsNotInSlimeGuard()
    {
        var player = Main.player[Actor]; player.maxTurrets = 1;
        var staff = player.inventory[0]; staff.SetDefaults(ItemID.StaffoftheFrostHydra);
        player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), withAudioVisualFeedback: false);
        var old = Main.projectile.Single(x => x.active && x.sentry); old.timeLeft = 100;
        sent.Clear(); player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), withAudioVisualFeedback: false);
        var packets = sent.Where(frame => frame[2] is 27 or 29).ToArray();
        Assert.That(packets.Select(frame => frame[2]), Is.EqualTo(new byte[] { 27, 29 }));
        Assert.That(Main.projectile.Count(x => x.active && x.sentry), Is.EqualTo(1));
    }

    [Test]
    public void M10_NativeQuickBuffCanIncreaseCapacityAndCreateBeforeAny50()
    {
        var player = Main.player[Actor]; player.maxMinions = 1; player.statMana = player.statManaMax2 = 200;
        var staff = player.inventory[0]; staff.SetDefaults(ItemID.SlimeStaff);
        var potion = player.inventory[1]; potion.SetDefaults(ItemID.SummoningPotion); potion.stack = 1;
        player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), withAudioVisualFeedback: false);
        var first = Main.projectile.Single(entity => entity.active && entity.type == 266);
        sent.Clear(); player.QuickBuff();
        Assert.That(player.FindBuffIndex(BuffID.Summoning), Is.GreaterThanOrEqualTo(0));
        player.ResetEffects(); player.UpdateBuffs(Actor);
        Assert.That(player.maxMinions, Is.EqualTo(2));
        player.FreeUpPetsAndMinions(staff);
        player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), withAudioVisualFeedback: false);
        Assert.That(first.active, Is.True);
        Assert.That(Main.projectile.Count(entity => entity.active && entity.type == 266), Is.EqualTo(2));
        Assert.That(sent.Any(frame => frame[2] == 50 || frame[2] == 29), Is.False);
        Assert.That(sent.Count(frame => frame[2] == 27), Is.EqualTo(1));
        TestContext.Out.WriteLine("Native QuickBuff consumed SummoningPotion and UpdateBuffs raised capacity1->2; FreeUpPetsAndMinions retained old Slime and native ItemCheck_Shoot emitted second27 with no prior50 or29. Server old capacity1 is not an exclusive current client upper bound.");
    }

    [Test]
    public void M10_NativePacket27CommitRequiresActualAllocationAndCancelledRequestDoesNotCount()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        int oldWidth = Main.maxTilesX; Main.maxTilesX = 4200;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot => slot == Actor ? (session, actor, true) : (null, null, false));
        bool cancel = false;
        void Request(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor || args.Instance.readBuffer[0] != 27) return;
            var result = guard.Evaluate(new(M2PacketKind.ProjectileNew, args.Instance.readBuffer.AsSpan(1, 23).ToArray()), session, actor, cancel)!;
            Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown), "A native entity commit cannot invent a native capacity calculation.");
            if (cancel) args.Result = OTAPI.HookResult.Cancel;
        }
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        try
        {
            guard.Install(); guard.Tick(1); Assert.That(guard.Healthy, Is.True, guard.FailureType + ": " + guard.FailureReason);
            OTAPI.Hooks.MessageBuffer.GetData += Request; HookEvents.Terraria.NetMessage.SendData += Sink;
            Receive(M10SlimeFrame(1), Actor);
            Assert.That(guard.NativeCommits, Is.EqualTo(1));
            Receive(M10SlimeFrame(1), Actor); Assert.That(guard.NativeCommits, Is.EqualTo(1), "Existing update adds no slot.");
            cancel = true; Receive(M10SlimeFrame(2), Actor);
            Assert.That(guard.NativeCommits, Is.EqualTo(1));
            Assert.That(Main.projectile.Count(entity => entity.active && entity.type == 266), Is.EqualTo(1));
            Assert.That(guard.NativeCalculations, Is.Zero);
        }
        finally
        { OTAPI.Hooks.MessageBuffer.GetData -= Request; HookEvents.Terraria.NetMessage.SendData -= Sink; Main.maxTilesX = oldWidth; }
    }

    [Test]
    public void M10_RequestObservationFaultDisablesOnlyGuardAndStillReachesNativeGetData()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        bool failLookup = false, reachedNative = false;
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot =>
        {
            if (failLookup) throw new InvalidOperationException("isolated-summon-binding-fault");
            return slot == Actor ? (session, actor, true) : (null, null, false);
        }) { IntegrityFault = _ => throw new InvalidOperationException("isolated-reporter-fault") };
        void Cancel(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor) return;
            reachedNative = true; args.Result = OTAPI.HookResult.Cancel;
        }
        try
        {
            guard.Install(); guard.Tick(1); Assert.That(guard.Healthy, Is.True, guard.FailureReason);
            OTAPI.Hooks.MessageBuffer.GetData += Cancel; failLookup = true;
            Assert.DoesNotThrow(() => Receive([4], Actor));
            Assert.That(reachedNative, Is.True); Assert.That(guard.Healthy, Is.False);
            Assert.That(guard.FailureReason, Is.EqualTo("isolated-summon-binding-fault"));
        }
        finally { OTAPI.Hooks.MessageBuffer.GetData -= Cancel; }
    }

    [Test]
    public void M10_CancelledEquipmentPostObservationFaultDoesNotEscapeNativeGetData()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot => slot == Actor ? (session, actor, true) : (null, null, false));
        var armor = actor.TPlayer.armor;
        bool reachedNative = false;
        void Cancel(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor || args.Instance.readBuffer[0] != 5) return;
            reachedNative = true; args.Result = OTAPI.HookResult.Cancel;
        }
        try
        {
            guard.Install(); guard.Tick(1); Assert.That(guard.Healthy, Is.True, guard.FailureReason);
            OTAPI.Hooks.MessageBuffer.GetData += Cancel;
            var gear = new byte[10]; gear[0] = 5; gear[1] = Actor;
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(2), (short)PlayerItemSlotID.Armor0);
            actor.TPlayer.armor = null!; // A host-state fault after an earlier cancellation is not a player violation.
            Assert.DoesNotThrow(() => Receive(gear, Actor));
            Assert.That(reachedNative, Is.True); Assert.That(guard.Healthy, Is.False);
            Assert.That(guard.FailureType, Is.EqualTo(typeof(NullReferenceException).FullName));
            Assert.That(guard.NativeCommits, Is.Zero);
        }
        finally { actor.TPlayer.armor = armor; OTAPI.Hooks.MessageBuffer.GetData -= Cancel; }
    }

    [Test]
    public void M10_NativeGetDataFailurePreservesOriginalException()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot => slot == Actor ? (session, actor, true) : (null, null, false));
        var failure = new InvalidOperationException("native-GetData-failure");
        void Throw(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        { if (args.Instance.whoAmI == Actor) throw failure; }
        try
        {
            guard.Install(); guard.Tick(1); Assert.That(guard.Healthy, Is.True, guard.FailureReason);
            OTAPI.Hooks.MessageBuffer.GetData += Throw;
            var escaped = Assert.Throws<InvalidOperationException>(() => Receive([4], Actor));
            Assert.That(escaped, Is.SameAs(failure)); Assert.That(guard.Healthy, Is.True);
            Assert.That(guard.NativeCommits, Is.Zero);
        }
        finally { OTAPI.Hooks.MessageBuffer.GetData -= Throw; }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void M10_RealPlayerUpdateCapacityAndNativeCommitsBlockFirstExtraButPermitRetirement(bool cancelledEquipmentDuringMaintenance)
    {
        var oldTiles = Main.tile; var oldWorldItems = Main.item; int oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY;
        double oldSurface = Main.worldSurface, oldRock = Main.rockLayer;
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        bool canWrite = true;
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot => slot == Actor ? (session, actor, canWrite) : (null, null, false));
        BusinessRuleResult? last = null;
        void Request(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor || args.Instance.readBuffer[0] != 27) return;
            last = guard.Evaluate(new(M2PacketKind.ProjectileNew, args.Instance.readBuffer.AsSpan(1, 23).ToArray()), session, actor, false);
            if (last?.Action == ControlAction.Block) args.Result = OTAPI.HookResult.Cancel;
        }
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        try
        {
            Main.maxTilesX = Main.maxTilesY = 100; Main.worldSurface = 50; Main.rockLayer = 70;
            Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            Main.tile = new ModFramework.DefaultCollection<ITile>(100, 100);
            _ = Main.tile[0, 0]; // The real lazy collection initializes its backing store on the first getter.
            for (int x = 0; x < 100; x++) for (int y = 0; y < 100; y++) Main.tile[x, y] = new Tile();
            if (CreativePowerManager.Instance.GetPower<CreativePowers.GodmodePower>() is null)
                CreativePowerManager.Initialize(); // Same native registry initialization used by Main.Initialize.
            if (ArmorSetBonuses.All.Count == 0) ArmorSetBonuses.Initialize();
            if (ArmorSetBonuses.SetsContaining is null || ArmorSetBonuses.SetsContaining.Length == 0 || ArmorSetBonuses.SetsContaining[0] is null)
                ArmorSetBonuses.BuildLookup(); // Initialize registers sets; this separate native step allocates SetsContaining.
            guard.Install(); guard.Tick(1); Assert.That(guard.Healthy, Is.True, guard.FailureType + ": " + guard.FailureReason);
            HookEvents.Terraria.NetMessage.SendData += Sink; OTAPI.Hooks.MessageBuffer.GetData += Request;
            // Read-only IL trace locates missing isolated-runtime fixture services without skipping native work.
            var trace = new Queue<string>(12);
            using (var traceHook = new ILHook(typeof(Player).GetMethod(nameof(Player.Update), [typeof(int)])!, il =>
            {
                var cursor = new ILCursor(il);
                foreach (var instruction in il.Body.Instructions.Where(instruction => instruction.OpCode.Code is
                    Code.Call or Code.Callvirt or Code.Ldfld or Code.Ldelem_Ref).ToArray())
                {
                    string label = instruction.Offset + ": " + instruction;
                    cursor.Goto(instruction, MoveType.Before); cursor.MoveAfterLabels();
                    cursor.EmitDelegate<Action>(() => { if (trace.Count == 12) trace.Dequeue(); trace.Enqueue(label); });
                }
            }))
            {
                try { actor.TPlayer.Update(Actor); }
                catch { TestContext.Out.WriteLine("Native Update fixture trace:\n" + string.Join('\n', trace)); throw; }
            }
            Assert.That(guard.NativeCalculations, Is.GreaterThan(0), "Actual Player.Update and all four native calculation bodies must execute.");
            Receive(M10SlimeFrame(1), Actor); Assert.That(last!.Action, Is.EqualTo(ControlAction.Pass));
            Receive(M10SlimeFrame(2), Actor); Assert.That(last!.Action, Is.EqualTo(ControlAction.Pass), "Pending QuickBuff may already have raised the client's capacity.");
            Receive(M10SlimeFrame(3), Actor); Assert.That(last!.Action, Is.EqualTo(ControlAction.Pass), "Both native capacity buffs remain in the possible client domain.");
            Receive(M10SlimeFrame(4), Actor); Assert.That(last!.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(last.Verdict, Is.EqualTo(Verdict.ResourceAbuse)); Assert.That(guard.NativeCommits, Is.EqualTo(3));
            var old = Main.projectile.Single(entity => entity.active && entity.key.bits == new ProjectileKey(Actor, 1, 1).bits);
            byte[] retirement = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(retirement, old.key.bits);
            void EarlyCancel29(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
            { if (args.Instance.whoAmI == Actor && args.Instance.readBuffer[0] == 29) args.Result = OTAPI.HookResult.Cancel; }
            OTAPI.Hooks.MessageBuffer.GetData += EarlyCancel29;
            try { Receive([29, .. retirement], Actor); }
            finally { OTAPI.Hooks.MessageBuffer.GetData -= EarlyCancel29; }
            Receive(M10SlimeFrame(5), Actor);
            Assert.That(last!.Action, Is.EqualTo(ControlAction.Pass), "Even cancelled29 excludes uncertain old retirement cost without killing the entity.");
            Assert.That(old.active, Is.True);
            Assert.That(last.Facts["retirementUncertainEntities"], Is.EqualTo("1"));
            Assert.That(guard.NativeCommits, Is.EqualTo(4));
            var decision = guard.CaptureDecision(Actor)!;
            Assert.That(decision.ProjectileKey, Is.EqualTo(new ProjectileKey(Actor, 5, 1).bits));
            Assert.That(decision.Sequence, Is.EqualTo(5)); Assert.That(decision.Passes, Is.EqualTo(4)); Assert.That(decision.Blocks, Is.EqualTo(1));

            byte[] gear = new byte[10]; gear[0] = 5; gear[1] = Actor;
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(2), (short)PlayerItemSlotID.Armor0);
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(4), 1);
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(7), ItemID.StardustHelmet);
            void EarlyCancel5(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
            { if (args.Instance.whoAmI == Actor && args.Instance.readBuffer[0] == 5) args.Result = OTAPI.HookResult.Cancel; }
            OTAPI.Hooks.MessageBuffer.GetData += EarlyCancel5;
            canWrite = !cancelledEquipmentDuringMaintenance;
            try { Receive(gear, Actor); }
            finally { canWrite = true; OTAPI.Hooks.MessageBuffer.GetData -= EarlyCancel5; }
            actor.TPlayer.Update(Actor); Receive(M10SlimeFrame(6), Actor);
            Assert.That(last!.Action, Is.EqualTo(ControlAction.Unknown), "A declined gear request remains unsettled after another real native calculation, including after recoverable maintenance ends.");
            Assert.That(last.Facts["equipmentInputAcceptanceSettled"], Is.EqualTo("False"));
            Receive(gear, Actor); actor.TPlayer.Update(Actor); Receive(M10SlimeFrame(7), Actor);
            Assert.That(last!.Action, Is.EqualTo(ControlAction.Block), "Exact accepted gear state plus subsequent native calculation restores this bounded resource scope.");
            Assert.That(last.Facts["capacityUpperBound"], Is.EqualTo("4"));
            TestContext.Out.WriteLine("Real native Player.Update plus two possible pending native capacity buffs -> three accepted native27 commits -> first fourth27 resource cancellation -> cancelled29 uncertainty permits replacement, with no entity deletion by the guard.");
        }
        finally
        {
            OTAPI.Hooks.MessageBuffer.GetData -= Request; HookEvents.Terraria.NetMessage.SendData -= Sink;
            Main.tile = oldTiles; Main.item = oldWorldItems;
            Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight; Main.worldSurface = oldSurface; Main.rockLayer = oldRock;
        }
    }

    private static byte[] M10SlimeFrame(int identity)
    {
        var frame = new byte[24]; frame[0] = 27;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), new ProjectileKey(Actor, identity, 1).bits);
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(5), 400); BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(9), 400);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(21), 266); return frame;
    }
}
