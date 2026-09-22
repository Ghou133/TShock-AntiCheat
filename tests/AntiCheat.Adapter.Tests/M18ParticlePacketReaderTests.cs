using System.Buffers.Binary;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.Drawing;
using Terraria.GameContent.NetModules;
using Terraria.Net;
using TerrariaApi.Server;
using Microsoft.Xna.Framework;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M18ParticlePacketReaderTests
{
    private const ushort Module = 22;

    [Test]
    public void ExactNetParticlesFrameMatchesNativeSettingsOrder()
    {
        var body = Body(Module, M18ParticleQueue.StormLightningType, invokingPlayer: 7);
        var result = M18ParticlePacketReader.ReadPayload(body, Module);

        Assert.That(result.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(result.Packet, Is.Not.Null);
        Assert.That(result.Packet!.ParticleType, Is.EqualTo(M18ParticleQueue.StormLightningType));
        Assert.That(result.Packet.PositionX, Is.EqualTo(320.5f));
        Assert.That(result.Packet.PositionY, Is.EqualTo(640.25f));
        Assert.That(result.Packet.MovementX, Is.EqualTo(14.5f));
        Assert.That(result.Packet.MovementY, Is.EqualTo(-3.25f));
        Assert.That(result.Packet.UniqueInfoPiece, Is.EqualTo(0x10203040));
        Assert.That(result.Packet.InvokingPlayer, Is.EqualTo(7));
    }

    [Test]
    public void NativeParticleSerializerProducesTheSameBoundedFrame()
    {
        var settings = new ParticleOrchestraSettings
        {
            PositionInWorld = new Vector2(320.5f, 640.25f),
            MovementVector = new Vector2(14.5f, -3.25f),
            UniqueInfoPiece = 0x10203040,
            IndexOfPlayerWhoInvokedThis = 7,
        };
        var packet = NetParticlesModule.Serialize(ParticleOrchestraType.StormLightning, settings);
        try
        {
            packet.ShrinkToFit();
            packet.Reader.BaseStream.Position = 3; // length(2), MessageID.NetModules(82)
            var body = packet.Reader.ReadBytes(packet.Length - 3);
            var result = M18ParticlePacketReader.ReadPayload(body, NetManager.Instance.GetId<NetParticlesModule>());

            Assert.That(body.Length, Is.EqualTo(M18ParticlePacketReader.BodyBytes));
            Assert.That(result.Kind, Is.EqualTo(PacketReadKind.Parsed));
            Assert.That(result.Packet!.ParticleType, Is.EqualTo(M18ParticleQueue.StormLightningType));
            Assert.That(result.Packet.PositionX, Is.EqualTo(settings.PositionInWorld.X));
            Assert.That(result.Packet.MovementY, Is.EqualTo(settings.MovementVector.Y));
            Assert.That(result.Packet.InvokingPlayer, Is.EqualTo(settings.IndexOfPlayerWhoInvokedThis));
        }
        finally { packet.Recycle(); }
    }

    [Test]
    public void TruncationAndTrailingBytesCannotEnterNativeDeserializer()
    {
        var body = Body(Module, M18ParticleQueue.StormLightningType, invokingPlayer: 7);
        for (int length = 0; length < body.Length; length++)
            Assert.That(M18ParticlePacketReader.ReadPayload(body.AsSpan(0, length), Module).Kind,
                Is.EqualTo(PacketReadKind.Malformed), "prefix " + length);
        Assert.That(M18ParticlePacketReader.ReadPayload([.. body, 0], Module).Kind,
            Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M18ParticlePacketReader.ReadPayload(body, Module + 1).Kind,
            Is.EqualTo(PacketReadKind.Unrelated));
    }

    [Test]
    public void NonLightningParticleTypesRemainOutsideCandidateQueue()
    {
        var body = Body(Module, 60, invokingPlayer: 7);
        var result = M18ParticlePacketReader.ReadPayload(body, Module);
        Assert.That(result.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(result.Packet!.ParticleType, Is.Not.EqualTo(M18ParticleQueue.StormLightningType));
    }

    private static byte[] Body(ushort module, int type, byte invokingPlayer)
    {
        byte[] body = new byte[M18ParticlePacketReader.BodyBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(body, module);
        body[2] = (byte)type;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(3), 320.5f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(7), 640.25f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(11), 14.5f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(15), -3.25f);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(19), 0x10203040);
        body[23] = invokingPlayer;
        return body;
    }
}
