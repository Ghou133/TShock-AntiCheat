using System.Buffers.Binary;
using AntiCheat.Rules;
using Terraria.GameContent.NetModules;
using Terraria.Net;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

/// <summary>Exact protocol-326 NetModules/NetParticles frame before native Deserialize.</summary>
public sealed record M18ParticlePacket(
    int ParticleType,
    float PositionX,
    float PositionY,
    float MovementX,
    float MovementY,
    int UniqueInfoPiece,
    byte InvokingPlayer);

public readonly record struct M18ParticleReadResult(PacketReadKind Kind, M18ParticlePacket? Packet);

public static class M18ParticlePacketReader
{
    public const int MessageId = 82;
    public const int BodyBytes = 24; // UInt16 module id + byte type + 21-byte settings.

    /// <summary>
    /// Returns a bounded, read-only description of the exact runtime module lookup used by
    /// <see cref="Read"/>. This is TestLab diagnostics only: it never registers a module and
    /// never treats a missing receiver as client evidence.
    /// </summary>
    public static string DescribeRuntime(bool verifiedRuntime)
    {
        if (!verifiedRuntime) return "verifiedRuntime=False";
        try
        {
            var manager = NetManager.Instance;
            ushort id = manager.GetId<NetParticlesModule>();
            var module = manager.GetModule<NetParticlesModule>();
            bool registered = module is not null && manager._modules.TryGetValue(id, out var current) &&
                ReferenceEquals(module, current);
            return $"verifiedRuntime=True moduleId={id} modulePresent={module is not null} moduleRegistered={registered}";
        }
        catch (Exception error)
        {
            return $"verifiedRuntime=True probeException={error.GetType().Name}";
        }
    }

    public static M18ParticleReadResult Read(GetDataEventArgs args, bool verifiedRuntime)
    {
        if ((byte)args.MsgID != MessageId)
            return new(PacketReadKind.Unrelated, null);
        if (!verifiedRuntime)
            return new(PacketReadKind.UnknownRuntime, null);

        int length = args.Length - 1;
        var buffer = args.Msg?.readBuffer;
        if (buffer is null || length < 2 || length > ushort.MaxValue - 3 ||
            args.Index < 0 || args.Index > buffer.Length - length)
            return new(PacketReadKind.Malformed, null);

        try
        {
            var manager = NetManager.Instance;
            ushort id = manager.GetId<NetParticlesModule>();
            var module = manager.GetModule<NetParticlesModule>();
            if (module is null || !manager._modules.TryGetValue(id, out var registered) ||
                !ReferenceEquals(module, registered))
                return new(PacketReadKind.UnknownRuntime, null);

            var body = buffer.AsSpan(args.Index, length);
            if (BinaryPrimitives.ReadUInt16LittleEndian(body) != id)
                return new(PacketReadKind.Unrelated, null);
            return ReadPayload(body, id);
        }
        catch
        {
            // A module-registration/runtime identity fault is not player evidence.
            return new(PacketReadKind.UnknownRuntime, null);
        }
    }

    public static M18ParticleReadResult ReadPayload(ReadOnlySpan<byte> body, ushort moduleId)
    {
        if (body.Length < 2)
            return new(PacketReadKind.Malformed, null);
        if (BinaryPrimitives.ReadUInt16LittleEndian(body) != moduleId)
            return new(PacketReadKind.Unrelated, null);
        if (body.Length != BodyBytes)
            return new(PacketReadKind.Malformed, null);

        return new(PacketReadKind.Parsed, new(
            ParticleType: body[2],
            PositionX: BinaryPrimitives.ReadSingleLittleEndian(body[3..]),
            PositionY: BinaryPrimitives.ReadSingleLittleEndian(body[7..]),
            MovementX: BinaryPrimitives.ReadSingleLittleEndian(body[11..]),
            MovementY: BinaryPrimitives.ReadSingleLittleEndian(body[15..]),
            UniqueInfoPiece: BinaryPrimitives.ReadInt32LittleEndian(body[19..]),
            InvokingPlayer: body[23]));
    }
}
