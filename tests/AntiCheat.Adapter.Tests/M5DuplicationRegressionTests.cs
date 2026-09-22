using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;
using TShockAPI.Configuration;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

/// <summary>Current core's documented distant-pickup/re-export duplication regression, without disabling Bouncer.</summary>
[TestFixture, NonParallelizable]
public sealed class M5DuplicationRegressionTests
{
    [Test]
    public void RejectedDistantPickupNeverReexportsTheItemAndNearPickupRemainsAllowed()
    {
        var previousConfig = ServerTShock.Config; var previousPlayer = Main.player[14]; var previousItem = Main.item[7];
        var previousLog = ServerTShock.Log;
        string logPath = Path.Combine(Path.GetTempPath(), "anticheat-m5-dupe-" + Guid.NewGuid().ToString("N") + ".log");
        using var log = new TextLog(logPath, false);
        try
        {
            ServerTShock.Log = log;
            ServerTShock.Config = new TShockConfig(); ServerTShock.Config.Settings.RangeChecks = true;
            Main.player[14] = new Player { whoAmI = 14, active = true, position = new(160, 160) };
            Main.item[7] = new WorldItem(); Main.item[7].inner.SetDefaults(ItemID.Wood); Main.item[7].stack = 17;
            Main.item[7].position = new(16000, 16000);
            var actor = new SinkPlayer(14) { Group = new Group("m5-dupe-default") };
            var type = typeof(TSPlayer).Assembly.GetType("TShockAPI.Bouncer")!;
            // This handler's audited branch uses no instance fields. Do not run the constructor,
            // which would install a second set of global core hooks in the test process.
            var bouncer = RuntimeHelpers.GetUninitializedObject(type);
            var method = type.GetMethod("OnItemDrop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            GetDataHandlers.ItemDropEventArgs Request() => new() { Player = actor, ID = 7, Type = 0,
                Position = Main.item[7].position, Velocity = default, Stacks = 0 };
            var first = Request(); method.Invoke(bouncer, [null, first]);
            Assert.That(first.Handled, Is.True); Assert.That(actor.Sends, Is.Empty,
                "The historical re-export after rejecting a pickup must stay absent; it could duplicate the locally acquired item.");
            var repeat = Request(); method.Invoke(bouncer, [null, repeat]);
            Assert.That(repeat.Handled, Is.True); Assert.That(actor.Sends, Is.Empty);
            Assert.That(Main.item[7].active, Is.True); Assert.That(Main.item[7].stack, Is.EqualTo(17));
            Main.player[14].position = Main.item[7].position;
            var near = Request(); method.Invoke(bouncer, [null, near]);
            Assert.That(near.Handled, Is.False, "The original near pickup is still eligible for the downstream native receive path.");
            Assert.That(actor.Sends, Is.Empty);
        }
        finally
        {
            ServerTShock.Log = previousLog; ServerTShock.Config = previousConfig;
            Main.player[14] = previousPlayer; Main.item[7] = previousItem;
            log.Dispose(); File.Delete(logPath);
        }
    }
    private sealed class SinkPlayer(int slot) : TSPlayer(slot)
    {
        public List<PacketTypes> Sends { get; } = [];
        public override void SendData(PacketTypes msgType, string text = "", int number = 0, float number2 = 0,
            float number3 = 0, float number4 = 0, int number5 = 0) => Sends.Add(msgType);
    }
}
