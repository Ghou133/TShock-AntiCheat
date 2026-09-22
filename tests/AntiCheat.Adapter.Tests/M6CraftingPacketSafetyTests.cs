using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.Net;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M6CraftingPacketSafetyTests
{
    private const ushort Module = 23;

    [Test]
    public void ActualNativeWriterPreservesGroupsDuplicateChestsAndNegativeLocalReferences()
    {
        var packet = CraftingRequests.NetCraftingRequestsModule.WriteRequest(
            [new(9, 10), new(-1, 2)], [new Chest(-1), new Chest(7999), new Chest(7999)]);
        try
        {
            packet.ShrinkToFit(); // NetManager performs this before putting the native frame on the wire.
            packet.Reader.BaseStream.Position = 3;
            var body = packet.Reader.ReadBytes(packet.Length - 3);
            Assert.That(M6CraftingPacketSafety.RejectPayload(body,
                NetManager.Instance.GetId<CraftingRequests.NetCraftingRequestsModule>(), 8000), Is.False);
        }
        finally { packet.Recycle(); }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ImpossibleHugeCountIsRejectedWithoutNativeListAllocation(int location)
    {
        var body = Body(w =>
        {
            if (location == 0) w.Write7BitEncodedInt(int.MaxValue);
            else
            {
                w.Write7BitEncodedInt(1); w.Write(9);
                if (location == 1) w.Write(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x7f });
                else { w.Write7BitEncodedInt(1); w.Write7BitEncodedInt(int.MaxValue); }
            }
        });
        Assert.That(M6CraftingPacketSafety.RejectPayload(body, Module, 8000), Is.True);
    }

    [TestCase(-1, false)]
    [TestCase(-5, false)]
    [TestCase(7999, false)]
    [TestCase(8000, true)]
    public void NativeBankNullSentinelRemainsLegalButWorldArrayBoundsAreProtected(int target, bool reject)
    {
        var body = Body(w => { w.Write7BitEncodedInt(1); w.Write(9); w.Write7BitEncodedInt(1); w.Write7BitEncodedInt(1); w.Write7BitEncodedInt(target); });
        Assert.That(M6CraftingPacketSafety.RejectPayload(body, Module, 8000), Is.EqualTo(reject));
    }

    [Test]
    public void EveryTruncationAndExtraTailOfValidFrameIsRejected()
    {
        var body = Body(w => { w.Write7BitEncodedInt(1); w.Write(9); w.Write7BitEncodedInt(130); w.Write7BitEncodedInt(1); w.Write7BitEncodedInt(7999); });
        Assert.That(M6CraftingPacketSafety.RejectPayload(body, Module, 8000), Is.False);
        for (int length = 0; length < body.Length; length++)
            Assert.That(M6CraftingPacketSafety.RejectPayload(body.AsSpan(0, length), Module, 8000), Is.True, "prefix " + length);
        Assert.That(M6CraftingPacketSafety.RejectPayload([.. body, 0], Module, 8000), Is.True);
    }

    [Test]
    public void OtherRegisteredModulePayloadIsNeverParsedAsCrafting()
    {
        var body = Body(w => w.Write7BitEncodedInt(int.MaxValue));
        Assert.That(M6CraftingPacketSafety.RejectPayload(body, Module + 1, 8000), Is.False);
    }

    [Test]
    public void AuthorizationTargetCostIsAvailableBeforeAnyNativePermissionHook()
    {
        var body = Body(w => { w.Write7BitEncodedInt(1); w.Write(9); w.Write7BitEncodedInt(1);
            w.Write7BitEncodedInt(3); w.Write7BitEncodedInt(-1); w.Write7BitEncodedInt(2); w.Write7BitEncodedInt(2); });
        Assert.That(M6CraftingPacketSafety.RejectPayload(body, Module, 8000, out int cost), Is.False);
        Assert.That(cost, Is.EqualTo(48), "Supplied targets reserve admission even if native filtering later removes them.");
    }

    private static byte[] Body(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Module); write(writer); return stream.ToArray();
    }
}
