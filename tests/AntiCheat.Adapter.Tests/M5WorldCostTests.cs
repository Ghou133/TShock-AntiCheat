using System.Buffers.Binary;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;

namespace AntiCheat.Adapter.Tests;

[TestFixture]
public sealed class M5WorldCostTests
{
    public static byte[] Wire(short x, short y, short ex, short ey)
    {
        byte[] body = new byte[9];
        BinaryPrimitives.WriteInt16LittleEndian(body, x); BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), y);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(4), ex); BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(6), ey);
        body[8] = 1; return body;
    }
    public static byte[] Tiles(byte width, byte height)
    {
        byte[] body = new byte[7 + width * height * 3];
        BinaryPrimitives.WriteInt16LittleEndian(body, 20); BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), 20);
        body[4] = width; body[5] = height; return body;
    }

    [TestCase(20, 20, 20, 20)] [TestCase(20, 20, 35, 20)] [TestCase(20, 20, 20, 35)]
    [TestCase(20, 20, 30, 40)] [TestCase(30, 40, 20, 20)]
    public void WireCostMatchesActualCoreLPathForBothFacings(short x, short y, short ex, short ey)
    {
        var cost = M5WorldCost.ReadPayload(109, Wire(x, y, ex, ey), 500, 500);
        Assert.That(cost.RejectMalformed, Is.False);
        foreach (bool facing in new[] { false, true })
        {
            var actual = TShockAPI.Utils.Instance.GetMassWireOperationRange(new Point(x, y), new Point(ex, ey), facing);
            Assert.That(cost.WorkUnits, Is.EqualTo(actual.Count));
            Assert.That(actual.Distinct().Count(), Is.EqualTo(cost.WorkUnits));
        }
    }
    [Test]
    public void LargeWireLUsesLinearCostAndNoRectangleAllocation()
    {
        var cost = M5WorldCost.ReadPayload(109, Wire(1, 1, 8000, 2000), 8400, 2400);
        Assert.That(cost.WorkUnits, Is.EqualTo(9999)); Assert.That(cost.RejectMalformed, Is.False);
        Assert.That(cost.WorkUnits, Is.LessThan(8000 * 2000));
    }
    [TestCase(1, 1)] [TestCase(4, 4)] [TestCase(100, 100)] [TestCase(0, 0)]
    public void TileCostUsesActualWidthTimesHeightAndDoesNotImposeACoreBypassPolicy(byte width, byte height)
    {
        var cost = M5WorldCost.ReadPayload(20, Tiles(width, height), 500, 500);
        Assert.That(cost.RejectMalformed, Is.False); Assert.That(cost.WorkUnits, Is.EqualTo(width * height));
    }
    [Test]
    public void ShortWorldPayloadCannotBorrowAnotherFrameOrAllocateItsClaimedGrid()
    {
        var body = Tiles(100, 100);
        Assert.That(M5WorldCost.ReadPayload(20, body.AsSpan(0, 7), 500, 500).RejectMalformed, Is.True);
        Assert.That(M5WorldCost.ReadPayload(109, Wire(20, 20, 30, 30).AsSpan(0, 8), 500, 500).RejectMalformed, Is.True);
        Assert.That(M5WorldCost.ReadPayload(109, Wire(short.MinValue, 0, short.MaxValue, 1), 500, 500).RejectMalformed, Is.True);
        Assert.That(M5WorldCost.ReadPayload(20, body, 50, 50).RejectMalformed, Is.True);
    }
    [TestCase(48, 6, NetworkRequestKind.WorldMutation)] [TestCase(59, 4, NetworkRequestKind.BroadcastAmplification)]
    [TestCase(87, 5, NetworkRequestKind.WorldMutation)] [TestCase(89, 9, NetworkRequestKind.WorldMutation)]
    [TestCase(121, 7, NetworkRequestKind.BroadcastAmplification)] [TestCase(123, 9, NetworkRequestKind.WorldMutation)]
    [TestCase(124, 11, NetworkRequestKind.BroadcastAmplification)] [TestCase(156, 6, NetworkRequestKind.WorldMutation)]
    public void PreviouslyOrdinaryEntityAndSwitchRequestsHaveSpecificAdmissionCosts(int id, int size, NetworkRequestKind expected)
    {
        var cost = M5WorldCost.ReadPayload(id, new byte[size], 500, 500);
        Assert.That(cost.RejectMalformed, Is.False); Assert.That(cost.Kind, Is.EqualTo(expected));
        Assert.That(M5WorldCost.ReadPayload(id, new byte[size - 1], 500, 500).RejectMalformed, Is.True);
    }
}
