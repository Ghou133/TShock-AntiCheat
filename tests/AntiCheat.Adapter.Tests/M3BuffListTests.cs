using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M3BuffListTests
{
    private const int Slot = 7;
    private static readonly SessionKey Session = new(Guid.NewGuid(), 1, Slot, 1);

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(44)]
    public void ActualTargetSerializerProducesAcceptedEmptyNormalAndMaximumLists(int count)
    {
        byte[] body = SerializeWithTarget(count, 600);
        Assert.That(body.Length, Is.EqualTo(3 + 2 * count));
        Assert.That(body[0], Is.EqualTo(Slot));
        Assert.That(body[^2..], Is.EqualTo(new byte[2]));
        var read = M3BuffListReader.Read(M2ContractsTests.Packet(PacketTypes.PlayerBuff, body, Slot), true);
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(read.Packet!.BuffTypes.Length, Is.EqualTo(count));
        var result = M3BuffListReader.Evaluate(read.Packet, Session, "locked-target-fixture");
        Assert.That(result.Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(result.Facts["durationField"], Is.EqualTo("absent"));
        Assert.That(result.PredicateSatisfied, Is.False);
    }

    [Test]
    public void ActualSerializerDoesNotPutBuffDurationOnTheWire()
    {
        Assert.That(SerializeWithTarget(1, 600), Is.EqualTo(SerializeWithTarget(1, 216000)));
    }

    [Test]
    public void ParserRejectsTruncationMissingTerminatorTrailingBytesAndTooManyEntries()
    {
        byte[] legal = SerializeWithTarget(1, 600);
        Assert.That(M3BuffListReader.ReadPayload(legal[..^1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3BuffListReader.ReadPayload(legal[..^2]).Reason, Is.EqualTo("buff-list-zero-terminator-missing"));
        Assert.That(M3BuffListReader.ReadPayload([Slot, 0, 0, 1, 0]).Reason, Is.EqualTo("buff-list-data-after-zero-terminator"));
        Assert.That(M3BuffListReader.ReadPayload([Slot, 0]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var tooMany = new byte[3 + 45 * 2]; tooMany[0] = Slot;
        for (int i = 0; i < 45; i++) BinaryPrimitives.WriteUInt16LittleEndian(tooMany.AsSpan(1 + i * 2), BuffID.Regeneration);
        Assert.That(M3BuffListReader.ReadPayload(tooMany).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var maxWithoutTerminator = new byte[M3BuffListReader.MaximumPayloadLength]; maxWithoutTerminator[0] = Slot;
        for (int i = 1; i < maxWithoutTerminator.Length; i += 2) maxWithoutTerminator[i] = BuffID.Regeneration;
        Assert.That(M3BuffListReader.ReadPayload(maxWithoutTerminator).Reason, Is.EqualTo("buff-list-count-exceeds-target-capacity"));
    }

    [TestCase(ushort.MaxValue)]
    [TestCase(0)]
    public void InvalidTypeCannotBecomeAccountProof(int id)
    {
        var result = M3BuffListReader.Evaluate(new(Slot, [id]), Session, "locked-target-fixture");
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(result.PrerequisitesComplete, Is.False);
    }

    [Test]
    public void ExactTargetTypeBoundaryBlocksWhileLargestValidTypeAndDuplicatesPass()
    {
        Assert.That(M3BuffListReader.Evaluate(new(Slot, [BuffID.Count]), Session, "target").Action, Is.EqualTo(ControlAction.Block));
        Assert.That(M3BuffListReader.Evaluate(new(Slot, [BuffID.Count - 1, BuffID.Count - 1]), Session, "target").Action,
            Is.EqualTo(ControlAction.Pass));
        var read = M3BuffListReader.ReadPayload([Slot, 255, 255, 0, 0]);
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(M3BuffListReader.Evaluate(read.Packet!, Session, "target").Action, Is.EqualTo(ControlAction.Block));
    }

    [Test]
    public void ServerBroadcastsOtherPlayersAreLegalAndClientSenderMismatchIsOnlySafetyBlock()
    {
        var packet = new M3BuffListPacket(Slot + 1, [BuffID.Regeneration]);
        var server = M3BuffListReader.Evaluate(packet, Session, "target", clientOrigin: false);
        Assert.That(server.Action, Is.EqualTo(ControlAction.Pass));
        var client = M3BuffListReader.Evaluate(packet, Session, "target");
        Assert.That(client.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(client.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(client.PredicateSatisfied, Is.False);
        Assert.That(M3BuffListReader.Evaluate(packet, Session, "target", alreadyCancelled: true).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void RuntimeAndSliceGuardsDoNotInferPacketsOutsideTheirDomain()
    {
        Assert.That(M3BuffListReader.Read(M2ContractsTests.Packet(PacketTypes.Emoji, [Slot, 0]), true).Kind,
            Is.EqualTo(PacketReadKind.Unrelated));
        var args = M2ContractsTests.Packet(PacketTypes.PlayerBuff, [Slot, 0, 0]);
        Assert.That(M3BuffListReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        args.Index = int.MaxValue;
        Assert.That(M3BuffListReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        args.Index = -1;
        Assert.That(M3BuffListReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3BuffListReader.Evaluate(new(Slot, ImmutableArray<int>.Empty), Session, "").Action,
            Is.EqualTo(ControlAction.Unknown));
    }

    private static byte[] SerializeWithTarget(int count, int duration)
    {
        var priorPlayer = Main.player[Slot]; int priorMode = Main.netMode;
        var priorConnection = Netplay.Connection;
        var priorBuffer = NetMessage.buffer[256];
        byte[]? captured = null;
        void Capture(object? _, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args)
        {
            if (args.msgType == (int)PacketTypes.PlayerBuff) captured = args.ms.ToArray();
        }
        void SuppressNetwork(object? _, HookEvents.Terraria.NetMessage.SendPacketToServerEventArgs args) => args.ContinueExecution = false;
        try
        {
            Main.netMode = 1;
            // The isolated assembly fixture has no socket factory. A terminated connection lets
            // the real serializer complete without constructing or contacting any network peer.
            Netplay.Connection = new RemoteServer { PendingTermination = true };
            NetMessage.buffer[256] = new MessageBuffer();
            Main.player[Slot] = new Player { whoAmI = Slot, active = true };
            for (int i = 0; i < count; i++) { Main.player[Slot].buffType[i] = BuffID.Regeneration; Main.player[Slot].buffTime[i] = duration; }
            HookEvents.Terraria.NetMessage.OnPacketWrite += Capture;
            HookEvents.Terraria.NetMessage.SendPacketToServer += SuppressNetwork;
            NetMessage.SendData((int)PacketTypes.PlayerBuff, number: Slot);
            Assert.That(captured, Is.Not.Null, "The actual target serializer must run.");
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(captured), Is.EqualTo(captured!.Length));
            Assert.That(captured[2], Is.EqualTo((byte)PacketTypes.PlayerBuff));
            return captured[3..];
        }
        finally
        {
            HookEvents.Terraria.NetMessage.OnPacketWrite -= Capture;
            HookEvents.Terraria.NetMessage.SendPacketToServer -= SuppressNetwork;
            Main.player[Slot] = priorPlayer; Main.netMode = priorMode; Netplay.Connection = priorConnection;
            NetMessage.buffer[256] = priorBuffer;
        }
    }
}
