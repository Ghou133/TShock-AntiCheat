using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M18GroundItemIdentityTests
{
    [Test]
    public void SameLiveItemMovementStackGrabAndRepeatedRequestsDoNotMintTargets()
    {
        WithAdapter((adapter, actor, session) =>
        {
            const short slot = 4;
            var item = NewItem(slot, ItemID.Wood, 5);
            Main.item[slot] = item;
            var first = Despawn(adapter, actor, session, slot);
            int generation = Generation(first);
            Assert.That(generation, Is.GreaterThan(0));

            item.position = new Vector2(112, 104);
            item.stack = 4;
            for (int repeat = 0; repeat < 4; repeat++)
            {
                var observed = Despawn(adapter, actor, session, slot);
                Assert.Multiple(() =>
                {
                    Assert.That(Generation(observed), Is.EqualTo(generation));
                    Assert.That(observed.Facts["packet151DistinctTargetCount"], Is.EqualTo("1"));
                    Assert.That(observed.PredicateSatisfied, Is.False);
                });
            }

            item.beingGrabbed = true;
            var grabbed = Despawn(adapter, actor, session, slot);
            Assert.Multiple(() =>
            {
                Assert.That(Generation(grabbed), Is.EqualTo(generation));
                Assert.That(grabbed.Action, Is.EqualTo(ControlAction.Unknown));
                Assert.That(grabbed.Reason, Is.EqualTo("ground-item-clear-target-context-incomplete"));
                Assert.That(grabbed.Facts["sessionClearCount"], Is.EqualTo("0"));
            });
            item.beingGrabbed = false;
            var ungrabbed = Despawn(adapter, actor, session, slot);
            Assert.Multiple(() =>
            {
                Assert.That(Generation(ungrabbed), Is.EqualTo(generation));
                Assert.That(ungrabbed.Facts["packet151DistinctTargetCount"], Is.EqualTo("1"));
                Assert.That(ungrabbed.PredicateSatisfied, Is.False);
            });
        });
    }

    [Test]
    public void NewWorldItemWrapperInReusedSlotMintsOneGenerationEvenWithSameTypeAndState()
    {
        WithAdapter((adapter, actor, session) =>
        {
            const short slot = 5;
            var firstItem = NewItem(slot, ItemID.Wood, 5);
            Main.item[slot] = firstItem;
            int first = Generation(Despawn(adapter, actor, session, slot));

            firstItem.TurnToAir();
            var air = Despawn(adapter, actor, session, slot);
            Assert.That(air.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(Generation(air), Is.Zero);

            // Item.NewItem in the locked OTAPI runtime assigns a new
            // WorldItem wrapper to Main.item[slot] on allocation.
            Main.item[slot] = NewItem(slot, ItemID.Wood, 5);
            var second = Despawn(adapter, actor, session, slot);
            int secondGeneration = Generation(second);
            Assert.Multiple(() =>
            {
                Assert.That(secondGeneration, Is.GreaterThan(first));
                Assert.That(second.Facts["packet151DistinctTargetCount"], Is.EqualTo("2"));
            });
            var repeat = Despawn(adapter, actor, session, slot);
            Assert.Multiple(() =>
            {
                Assert.That(Generation(repeat), Is.EqualTo(secondGeneration));
                Assert.That(repeat.Facts["packet151DistinctTargetCount"], Is.EqualTo("2"));
            });
        });
    }

    [Test]
    public void InPlaceResurrectionOrInnerReplacementCannotClaimASecondIdentity()
    {
        WithAdapter((adapter, actor, session) =>
        {
            const short slot = 6;
            var item = NewItem(slot, ItemID.Wood, 5);
            Main.item[slot] = item;
            Assert.That(Generation(Despawn(adapter, actor, session, slot)), Is.GreaterThan(0));

            item.TurnToAir();
            Assert.That(Generation(Despawn(adapter, actor, session, slot)), Is.Zero);
            item.inner.SetDefaults(ItemID.Wood);
            item.stack = 5;
            var revived = Despawn(adapter, actor, session, slot);
            Assert.Multiple(() =>
            {
                Assert.That(Generation(revived), Is.Zero);
                Assert.That(revived.Action, Is.EqualTo(ControlAction.Unknown));
                Assert.That(revived.Reason, Is.EqualTo("ground-item-clear-target-context-incomplete"));
                Assert.That(revived.Facts["sessionClearCount"], Is.EqualTo("0"));
            });

            item.inner = new Item();
            item.inner.SetDefaults(ItemID.Wood);
            item.stack = 5;
            var replacedInner = Despawn(adapter, actor, session, slot);
            Assert.Multiple(() =>
            {
                Assert.That(Generation(replacedInner), Is.Zero);
                Assert.That(replacedInner.Action, Is.EqualTo(ControlAction.Unknown));
            });

            Main.item[slot] = NewItem(slot, ItemID.Wood, 5);
            Assert.That(Generation(Despawn(adapter, actor, session, slot)), Is.GreaterThan(1),
                "A later wrapper replacement restores a verifiable allocation identity.");
        });
    }

    [Test]
    public void InnerItemReplacementWhileWrapperRemainsActiveDegradesIdentity()
    {
        WithAdapter((adapter, actor, session) =>
        {
            const short slot = 8;
            var item = NewItem(slot, ItemID.Wood, 5);
            Main.item[slot] = item;
            Assert.That(Generation(Despawn(adapter, actor, session, slot)), Is.GreaterThan(0));

            item.inner = new Item();
            item.inner.SetDefaults(ItemID.Wood);
            item.stack = 5;
            var ambiguous = Despawn(adapter, actor, session, slot);
            Assert.Multiple(() =>
            {
                Assert.That(Generation(ambiguous), Is.Zero);
                Assert.That(ambiguous.Action, Is.EqualTo(ControlAction.Unknown));
                Assert.That(ambiguous.Reason, Is.EqualTo("ground-item-clear-target-context-incomplete"));
            });

            Main.item[slot] = NewItem(slot, ItemID.Wood, 5);
            Assert.That(Generation(Despawn(adapter, actor, session, slot)), Is.GreaterThan(1));
        });
    }

    private static int Generation(BusinessRuleResult result) => int.Parse(result.Facts["targetGeneration"]);

    private static WorldItem NewItem(int slot, int type, int stack)
    {
        var item = new WorldItem { whoAmI = slot, position = new Vector2(100, 100) };
        item.inner.SetDefaults(type);
        item.stack = stack;
        return item;
    }

    private static BusinessRuleResult Despawn(M2BusinessAdapter adapter, TSPlayer actor,
        SessionKey session, short slot)
    {
        byte[] body = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(body, slot);
        var args = new GetDataEventArgs { MsgID = PacketTypes.SyncItemDespawn, Index = 0, Length = 3 };
        typeof(GetDataEventArgs).GetProperty(nameof(GetDataEventArgs.Msg))!.SetValue(args,
            new MessageBuffer { whoAmI = session.Slot, readBuffer = body });
        var parsed = M2PacketReader.Read(args, true);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        return adapter.Evaluate(parsed.Packet!, session, actor, _ => (null, null), false)
            .Single(result => result.RuleId == M18GroundItemClearQueueRules.RuleId);
    }

    private static void WithAdapter(Action<M2BusinessAdapter, TSPlayer, SessionKey> run)
    {
        const int actorSlot = 7;
        var oldItems = Main.item;
        var oldPlayer = Main.player[actorSlot];
        int oldMode = Main.netMode, oldWidth = Main.maxTilesX;
        try
        {
            Main.netMode = 0;
            Main.maxTilesX = 0;
            Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            Main.player[actorSlot] = new Player { whoAmI = actorSlot, active = true, position = new Vector2(100, 100) };
            var actor = new TSPlayer(actorSlot)
            {
                IsLoggedIn = true,
                Account = new UserAccount { ID = 707, Name = "ground-item-identity" },
                Group = new Group("ground-item-identity")
            };
            var session = new SessionKey(Guid.NewGuid(), 1, actorSlot, 1);
            var adapter = new M2BusinessAdapter(TargetRuntime.Fingerprint,
                Path.Combine(Path.GetTempPath(), "absent-f08-identity-" + Guid.NewGuid().ToString("N")),
                M18GroundItemClearQueueOptions.TestLabCandidate);
            adapter.Update(_ => null, session.WorldEpoch);
            run(adapter, actor, session);
        }
        finally
        {
            Main.item = oldItems;
            Main.player[actorSlot] = oldPlayer;
            Main.netMode = oldMode;
            Main.maxTilesX = oldWidth;
        }
    }
}
