using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture]
public sealed class M3CheatPacketTests
{
    [Test]
    public void TargetDebuffFrameReadsSignedAmountWithoutAcceptingNpcStrikeOrLegacyShapes()
    {
        // Independently specified wire bytes: target 9, Int16 -32768.
        var parsed = M3CheatPacketReader.ReadPayload(153, [9, 0, 128]);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(parsed.Packet!.NpcIndex, Is.EqualTo(9));
        Assert.That(parsed.Packet.DebuffAmount, Is.EqualTo(short.MinValue));
        Assert.That(M3CheatPacketReader.ReadPayload(153, [9, 0]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3CheatPacketReader.ReadPayload(153, [9, 0, 128, 0]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3CheatPacketReader.ReadPayload(28, [9, 0, 128]).Kind, Is.EqualTo(PacketReadKind.Unrelated));
    }

    [Test]
    public void TargetNpcPortalFrameIsDifferentFromLegitimatePlayerPortal96()
    {
        byte[] body = [9, 0, 2, 0, 0, 0, 200, 67, 0, 0, 150, 67, 0, 0, 0, 0, 0, 0, 64, 192];
        var parsed = M3CheatPacketReader.ReadPayload(100, body);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(parsed.Packet!.Message, Is.EqualTo(NpcServerAuthorityMessage.PortalTeleport));
        Assert.That(parsed.Packet.NpcIndex, Is.EqualTo(9));
        Assert.That(parsed.Packet.PortalColorIndex, Is.EqualTo(2));
        Assert.That(parsed.Packet.X, Is.EqualTo(400)); Assert.That(parsed.Packet.Y, Is.EqualTo(300));
        Assert.That(parsed.Packet.VelocityY, Is.EqualTo(-3));
        body[0] = 10;
        Assert.That(parsed.Packet.NpcIndex, Is.EqualTo(9), "Parsed observations do not retain mutable client buffers.");
        Assert.That(M3CheatPacketReader.ReadPayload(96, body).Kind, Is.EqualTo(PacketReadKind.Unrelated));
        Assert.That(M3CheatPacketReader.ReadPayload(100, body[..^1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3CheatPacketReader.Read(M2ContractsTests.Packet((PacketTypes)100, body), false).Kind,
            Is.EqualTo(PacketReadKind.UnknownRuntime));
    }

    [Test]
    public void TargetChestResizeParserDoesNotConfuseOrdinaryChestItemPacketsWithPrivilegedResize()
    {
        var args = M2ContractsTests.Packet((PacketTypes)155, [2, 0, 40, 0]);
        var read = M3ChestSizePacketReader.Read(args, true);
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(read.Packet, Is.EqualTo(new ChestResizeObservation(2, 40)));
        Assert.That(M3ChestSizePacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        args.Length--;
        Assert.That(M3ChestSizePacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3ChestSizePacketReader.Read(M2ContractsTests.Packet(PacketTypes.ChestItem, new byte[8]), true).Kind,
            Is.EqualTo(PacketReadKind.Unrelated));
    }
}
