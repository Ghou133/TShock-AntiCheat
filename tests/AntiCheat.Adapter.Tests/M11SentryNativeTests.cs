using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
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
    public void M11_NativeHydraCreatesRealMaxOneTransientThenRetiresOnlyItsOwnOldEntity()
    {
        var player = Main.player[Actor]; player.maxTurrets = 1;
        var staff = player.inventory[0]; staff.SetDefaults(ItemID.StaffoftheFrostHydra);
        player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), false);
        var old = Main.projectile.Single(entity => entity.active && entity.type == 308); old.timeLeft = 100;
        var foreign = Main.projectile.First(entity => !entity.active);
        foreign.SetDefaults(308); foreign.active = true; foreign.owner = TargetSlot; foreign.timeLeft = 1;
        int transient = 0;
        void Observe(object? _, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args)
        {
            var bytes = args.ms.ToArray();
            if (bytes.Length > 2 && bytes[2] == 27)
                transient = Main.projectile.Count(entity => entity.active && entity.owner == Actor && entity.type == 308);
        }
        HookEvents.Terraria.NetMessage.OnPacketWrite += Observe;
        try
        {
            sent.Clear(); player.ItemCheck_Shoot(Actor, staff, player.GetWeaponDamage(staff), false);
            Assert.That(sent.Where(frame => frame[2] is 27 or 29).Select(frame => frame[2]), Is.EqualTo(new byte[] { 27, 29 }));
            Assert.That(transient, Is.EqualTo(2)); Assert.That(old.active, Is.False); Assert.That(foreign.active, Is.True);
            Assert.That(Main.projectile.Count(entity => entity.active && entity.owner == Actor && entity.type == 308), Is.EqualTo(1));
            TestContext.Out.WriteLine("Real Staff1572/Projectile308: at outgoing27 maxTurrets=1 and own active bodies=2; subsequent UpdateMaxTurrets emits29 and kills only own oldest. Foreign oldest remains active.");
        }
        finally { HookEvents.Terraria.NetMessage.OnPacketWrite -= Observe; }
    }

    [Test]
    public void M11_ActualNativeCapacityAndReceiveBoundCreationButPreserveReplacementAndFailureOwnership()
    {
        var oldTiles = Main.tile; var oldItems = Main.item; int oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY;
        double oldSurface = Main.worldSurface, oldRock = Main.rockLayer;
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        bool canWrite = true, cancelNew = false, cancelRetire = false, alterAdapterCopy = false;
        bool retargetBeforePreflight = false, retargetReachedAdapter = false;
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot => slot == Actor ? (session, actor, canWrite) : (null, null, false));
        BusinessRuleResult? last = null;
        void Prior(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (!retargetBeforePreflight || args.Instance.whoAmI != Actor || args.PacketId != 27) return;
            // Model a preceding OTAPI hook selecting another frame. The later
            // adapter hook restores this request before native dispatch.
            args.Start += 32;
            args.Length += 1;
        }
        void Request(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI != Actor) return;
            if (args.Instance.readBuffer[0] == 27)
            {
                if (retargetBeforePreflight)
                {
                    retargetReachedAdapter = args.Start == 32 && args.Length == 25;
                    args.Start = 0;
                    args.Length = 24;
                }
                var adapterCopy = args.Instance.readBuffer.AsSpan(1, 23).ToArray();
                if (alterAdapterCopy) adapterCopy[4] ^= 1; // Same key/type, different immutable frame.
                last = guard.Evaluate(new(M2PacketKind.ProjectileNew, adapterCopy), session, actor, cancelNew);
                if (cancelNew || !canWrite || last?.Action == ControlAction.Block) args.Result = OTAPI.HookResult.Cancel;
            }
            if (args.Instance.readBuffer[0] == 29 && cancelRetire || args.Instance.readBuffer[0] == 5 && !canWrite)
                args.Result = OTAPI.HookResult.Cancel;
        }
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        try
        {
            Main.maxTilesX = Main.maxTilesY = 100; Main.worldSurface = 50; Main.rockLayer = 70;
            Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            Main.tile = new ModFramework.DefaultCollection<ITile>(100, 100); _ = Main.tile[0, 0];
            for (int x = 0; x < 100; x++) for (int y = 0; y < 100; y++) Main.tile[x, y] = new Tile();
            if (CreativePowerManager.Instance.GetPower<CreativePowers.GodmodePower>() is null) CreativePowerManager.Initialize();
            if (ArmorSetBonuses.All.Count == 0) ArmorSetBonuses.Initialize();
            if (ArmorSetBonuses.SetsContaining is null || ArmorSetBonuses.SetsContaining.Length == 0 || ArmorSetBonuses.SetsContaining[0] is null)
                ArmorSetBonuses.BuildLookup();
            OTAPI.Hooks.MessageBuffer.GetData += Prior;
            guard.Install(); guard.Tick(1); Assert.That(guard.Healthy, Is.True, guard.FailureReason);
            Assert.That(guard.SentryBudget.TargetValidated, Is.True, guard.SentryBudget.FailureReason);
            HookEvents.Terraria.NetMessage.SendData += Sink; OTAPI.Hooks.MessageBuffer.GetData += Request;
            actor.TPlayer.Update(Actor);
            long beforeDecision = guard.CaptureSentry(Actor)?.Decision?.Sequence ?? 0;
            long beforePreflight = guard.NativePreflightEvaluations;
            alterAdapterCopy = true;
            Create(1, ControlAction.Pass);
            alterAdapterCopy = false;
            Assert.Multiple(() =>
            {
                Assert.That(guard.NativePreflightEvaluations, Is.EqualTo(beforePreflight + 1));
                Assert.That(guard.CaptureSentry(Actor)!.Decision!.Sequence, Is.EqualTo(beforeDecision + 2),
                    "A same-key/type but different adapter payload must not reuse the native preflight decision.");
                Assert.That(guard.SentryBudget.NativeCommits, Is.EqualTo(1),
                    "The actual native allocation is still charged once.");
            });
            Create(2, ControlAction.Pass);
            Assert.That(last!.Facts["capacityCurrent"], Is.EqualTo("1"));
            Assert.That(last.Facts["capacityUpperBound"], Is.EqualTo("2"), "Potential unsynchronized WarTable contribution must be included.");
            Retire(1); Assert.That(guard.CaptureSentry(Actor)!.KnownActiveEntities, Is.EqualTo(1));
            Create(3, ControlAction.Pass); Retire(2); Create(4, ControlAction.Pass); Retire(3);
            Create(5, ControlAction.Pass); Create(6, ControlAction.Pass); // upper2 + one true transient
            var held = Main.projectile.Where(entity => entity.active && entity.type == 308).ToArray();
            long commits = guard.SentryBudget.NativeCommits;
            var ordinary = M11HydraFrame(200); BinaryPrimitives.WriteInt16LittleEndian(ordinary.AsSpan(21), ProjectileID.WoodenArrowFriendly);
            Receive(ordinary, Actor);
            Assert.That(M2ProjectileLookup.TryGet(new ProjectileKey(Actor, 200, 1), out var changing, out bool complete) && complete, Is.True);
            Assert.That(changing!.type, Is.EqualTo(ProjectileID.WoodenArrowFriendly));
            Create(200, ControlAction.Block);
            Assert.That(changing.type, Is.EqualTo(ProjectileID.WoodenArrowFriendly), "Same-key non308→308 must be blocked before native SetDefaults when at resource capacity.");
            Create(7, ControlAction.Block);
            Assert.That(held.All(entity => entity.active), Is.True, "Resource BLOCK cannot kill any legitimate old entity.");
            Assert.That(guard.SentryBudget.NativeCommits, Is.EqualTo(commits));
            cancelRetire = true; Retire(4); cancelRetire = false;
            Create(8, ControlAction.Block); // no29, cancelled29 and a late29 have the same actual occupancy
            for (int tick = 0; tick < M10SummonBudgetGuard.WitnessTtlTicks + 256; tick++) guard.Tick(1);
            Create(9, ControlAction.Block);
            Assert.That(guard.CaptureSentry(Actor)!.KnownActiveEntities, Is.EqualTo(3), "TTL never releases a still-live sentry resource.");
            Retire(4); Create(200, ControlAction.Pass);
            Assert.That(changing.type, Is.EqualTo(308));
            Assert.That(M2ProjectileLookup.TryGet(new ProjectileKey(Actor, 200, 1), out var changed, out _), Is.True);
            Assert.That(changed, Is.SameAs(changing), "Actual same-object type conversion is a resource addition without a new allocation.");
            cancelNew = true; Create(11, ControlAction.Unknown); cancelNew = false;
            Assert.That(guard.SentryBudget.NativeCommits, Is.EqualTo(commits + 1));
            // Existing key updates do not reserve or commit another body.
            Create(200, ControlAction.Unknown); Assert.That(guard.SentryBudget.NativeCommits, Is.EqualTo(commits + 1));

            var gear = new byte[10]; gear[0] = 5; gear[1] = Actor;
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(2), (short)(PlayerItemSlotID.Armor0 + 3));
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(4), 1);
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(7), ItemID.SquireShield);
            canWrite = false; Receive(gear, Actor); canWrite = true; actor.TPlayer.Update(Actor);
            Create(12, ControlAction.Unknown);
            Assert.That(last!.Facts["equipmentInputAcceptanceSettled"], Is.EqualTo("False"));
            Receive(gear, Actor); actor.TPlayer.Update(Actor); Create(13, ControlAction.Block);
            Assert.That(last!.Facts["capacityCurrent"], Is.EqualTo("2"));
            Assert.That(last.Facts["capacityUpperBound"], Is.EqualTo("3"));
            // The Unknown commit remains accounted after maintenance resolves; no free reservation reset.
            BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(4), 0); BinaryPrimitives.WriteInt16LittleEndian(gear.AsSpan(7), 0);
            Receive(gear, Actor); actor.TPlayer.Update(Actor); Create(14, ControlAction.Block);
            Assert.That(last!.Facts["capacityCurrent"], Is.EqualTo("1"));
            Assert.That(last.Facts["capacityUpperBound"], Is.EqualTo("3"), "Lower capability retains the same-session high-water.");
            // Repeated input gaps must still hit a physical execution bound before cache capacity.
            canWrite = false; Receive(gear, Actor); canWrite = true; actor.TPlayer.Update(Actor);
            for (int identity = 20; identity < 84; identity++) Create(identity, ControlAction.Unknown);
            Assert.That(guard.CaptureSentry(Actor)!.KnownActiveEntities, Is.EqualTo(M11SentryBudgetRules.AbsoluteNativeCapacity + 1));
            Create(90, ControlAction.Block);
            Assert.That(last!.Reason, Is.EqualTo("native-frost-hydra-absolute-resource-bound-exceeded"));
            guard.Forget(session); session = session with { Generation = 2 }; actor.TPlayer.Update(Actor);
            Create(15, ControlAction.Pass);
            Assert.That(guard.CaptureSentry(Actor)!.KnownActiveEntities, Is.EqualTo(1), "Old authenticated generation cannot belong to replacement account.");
            long beforeRetargetPreflight = guard.NativePreflightEvaluations;
            long beforeRetargetCommits = guard.SentryBudget.NativeCommits;
            retargetBeforePreflight = true; cancelNew = true;
            Create(16, ControlAction.Unknown);
            retargetBeforePreflight = false; cancelNew = false;
            Assert.Multiple(() =>
            {
                Assert.That(retargetReachedAdapter, Is.True, "The prior hook actually changed the selected native frame before preflight.");
                Assert.That(guard.NativePreflightEvaluations, Is.EqualTo(beforeRetargetPreflight),
                    "Preflight must not inspect the old immutable frame after an earlier hook changes start/length.");
                Assert.That(guard.SentryBudget.NativeCommits, Is.EqualTo(beforeRetargetCommits),
                    "The later cancelled request must not charge a native sentry body.");
            });
            TestContext.Out.WriteLine("Native server Update+GetData: repeated27→29 replacements; pending348 ceiling; bound+1 first BLOCK; early-cancelled29 and >1800ticks retain occupied resources; actual29 reopens one slot; cancelled/new-update create no commit; maintenance5 gap preserves Unknown commits; exact5 accept and native equipment raise/decline; slot generation isolates evidence.");
        }
        finally
        {
            OTAPI.Hooks.MessageBuffer.GetData -= Request;
            OTAPI.Hooks.MessageBuffer.GetData -= Prior;
            HookEvents.Terraria.NetMessage.SendData -= Sink;
            Main.tile = oldTiles; Main.item = oldItems; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
            Main.worldSurface = oldSurface; Main.rockLayer = oldRock;
        }

        void Create(int identity, ControlAction expected)
        { Receive(M11HydraFrame(identity), Actor); Assert.That(last?.Action, Is.EqualTo(expected), "identity=" + identity + " " + last?.Reason); }
        void Retire(int identity)
        {
            var bytes = new byte[13]; bytes[0] = 29;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), new ProjectileKey(Actor, identity, 1).bits);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5), 400); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(9), 400);
            Receive(bytes, Actor);
        }
    }

    private static byte[] M11HydraFrame(int identity)
    {
        var frame = M10SlimeFrame(identity); BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(21), 308); return frame;
    }

    [Test]
    public void M11_NativeReceiveFailureAfterAllocationRetainsLiveResourceAndOriginalException()
    {
        Main.netMode = 2; Main.myPlayer = 255; int oldWidth = Main.maxTilesX; Main.maxTilesX = 4200;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        using var guard = new M10SummonBudgetGuard(TargetRuntime.Fingerprint, slot => slot == Actor ? (session, actor, true) : (null, null, false));
        var failure = new InvalidOperationException("injected-failure-after-real-FinalizeProjectile");
        bool finalizationReturned = false;
        void Request(object? _, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            if (args.Instance.whoAmI == Actor && args.Instance.readBuffer[0] == 27)
                guard.Evaluate(new(M2PacketKind.ProjectileNew, args.Instance.readBuffer.AsSpan(1, 23).ToArray()), session, actor, false);
        }
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        try
        {
            guard.Install(); guard.Tick(1); OTAPI.Hooks.MessageBuffer.GetData += Request; HookEvents.Terraria.NetMessage.SendData += Sink;
            // SendData failures are swallowed by native TrySendData, so that earlier attempted
            // injection did not exercise GetData failure. This exact finalization boundary is outside it.
            using var fault = new Hook(typeof(Projectile).GetMethod(nameof(Projectile.FinalizeProjectile))!,
                (Action<Action<Projectile>, Projectile>)((original, entity) =>
                {
                    original(entity);
                    if (entity.type == 308 && entity.owner == Actor)
                    { finalizationReturned = true; throw failure; }
                }));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Receive(M11HydraFrame(1), Actor)), Is.SameAs(failure));
            Assert.That(finalizationReturned, Is.True);
            Assert.That(guard.Healthy, Is.True); Assert.That(guard.CaptureSentry(Actor)!.KnownActiveEntities, Is.EqualTo(1));
            Assert.That(guard.SentryBudget.NativeCommits, Is.EqualTo(1));
        }
        finally { OTAPI.Hooks.MessageBuffer.GetData -= Request; HookEvents.Terraria.NetMessage.SendData -= Sink; Main.maxTilesX = oldWidth; }
    }
}
