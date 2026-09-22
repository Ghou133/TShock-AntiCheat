using System.Buffers.Binary;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [Test]
    public void TestLabWorldEditQueueCancelsDirectBrushWorkBeforeNativeReceiverWithoutSanction()
    {
        var queueOptions = new M18WorldEditQueueOptions
        {
            Enabled = true,
            WindowTicks = 10,
            PerSessionWorkUnits = 3,
            EventCapacity = 8,
        };
        var dataDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "progression");
        var business = new M2BusinessAdapter(TargetRuntime.Fingerprint, dataDirectory, queueOptions);
        typeof(AntiCheatPlugin).GetField("_business", Private)!.SetValue(plugin, business);
        try
        {
            Assert.That(RootWorld(PacketTypes.Tile, TileBody(1, data: 1)).Handled, Is.False,
                "A legal tile request inside the candidate work budget remains available to the native receiver.");
            Assert.That(RootWorld(PacketTypes.LiquidSet, LiquidBody()).Handled, Is.False,
                "A legal liquid request consumes one determinable work unit and remains available.");

            var replacement = TileBody(21, data: 1);
            Assert.That(RootWorld(PacketTypes.Tile, replacement).Handled, Is.True,
                "A replacement is two work units; exceeding the finite session budget must stop this raw request.");
            Assert.That(engine.CanWrite(session), Is.True, "A resource stop-loss cannot revoke the authenticated session.");
            Assert.That(engine.SanctionCount, Is.Zero, "A density budget is not closed cheat evidence.");

            // Feed the same blocked candidate through the native MessageBuffer boundary. The
            // priority-1000 plugin hook must preserve Handled before TShock's receiver executes.
            ReceiveWorld(PacketTypes.Tile, replacement);

            business.Forget(session);
            Assert.That(RootWorld(PacketTypes.Tile, TileBody(21, data: 1)).Handled, Is.False,
                "Exact-session cleanup starts a fresh candidate window.");
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            TestContext.Out.WriteLine("TestLab world-edit queue: tile=1 + liquid=1 + replacement=2 exceeded budget=3; raw hook cancelled only the excess; exact-session forget reopened; sanction=0.");
        }
        finally
        {
            typeof(AntiCheatPlugin).GetField("_business", Private)!.SetValue(plugin, null);
        }
    }

    private static byte[] TileBody(byte operation, short x = 20, short y = 20, short data = 1, byte style = 0)
    {
        byte[] body = new byte[8];
        body[0] = operation;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), x);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(3), y);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(5), data);
        body[7] = style;
        return body;
    }

    private static byte[] LiquidBody(short x = 20, short y = 20, byte amount = 255, byte type = 0)
    {
        byte[] body = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(0), x);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), y);
        body[4] = amount;
        body[5] = type;
        return body;
    }

    private void ReceiveWorld(PacketTypes type, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot };
        buffer.readBuffer[0] = (byte)type;
        body.CopyTo(buffer.readBuffer, 1);
        buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out int parsedType);
        Assert.That(parsedType, Is.EqualTo((int)type));
    }

    private GetDataEventArgs RootWorld(PacketTypes type, byte[] body, bool cancelled = false)
    {
        var args = M2ContractsTests.Packet(type, body, Slot);
        args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args);
        return args;
    }
}
