using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

// Uses the existing isolated target-runtime/root-hook setup, account and bounded test store.
public sealed partial class M6NpcStrikeTests
{
    private static byte[] TeleportBody(byte flags = 0, short claimed = Slot, float x = 400, float y = 450)
    {
        byte[] body = new byte[(flags & 8) != 0 ? 16 : 12]; body[0] = flags;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), claimed);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(3), x);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(7), y);
        return body;
    }
    private static byte[] PortalBody(float x = 400, float vx = 3)
    {
        byte[] body = new byte[19]; body[0] = Slot;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), 3);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(3), x);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(7), 450);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(11), vx);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(15), -2);
        return body;
    }
    private GetDataEventArgs RootTeleport(int id, byte[] body, bool cancelled = false)
    {
        var args = M2ContractsTests.Packet((PacketTypes)id, body, Slot); args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args); return args;
    }
    private static void NativeTeleport(int id, byte[] body)
    {
        // The existing strike fixture has no projectile/dust world. Teleport legitimately
        // scans grapples before its position write, so supply real inactive native arrays.
        var projectiles = Main.projectile; var dust = Main.dust;
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Main.dust = Enumerable.Range(0, 6001).Select(_ => new Dust { active = true }).ToArray();
        try
        {
            var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = (byte)id;
            body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out _);
        }
        finally { Main.projectile = projectiles; Main.dust = dust; }
    }

    [Test]
    public void M9_LegalTeleportAndPortalEnterGuardPassAndNativePositionVelocityWrites()
    {
        var player = Main.player[Slot]; player.position = new(320, 320);
        foreach (var request in new[] { (65, TeleportBody()), (96, PortalBody()) })
        {
            var parsed = M9PlayerTeleportGuard.ReadPayload(request.Item1, request.Item2).Packet!;
            Assert.That(M9PlayerTeleportGuard.Evaluate(parsed, session, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(RootTeleport(request.Item1, request.Item2).Handled, Is.False);
            NativeTeleport(request.Item1, request.Item2);
            Assert.That(player.position, Is.EqualTo(new Vector2(400, 450)), "Native body must reach the position write.");
            Assert.That(sent.Any(x => x.Id == request.Item1), Is.True);
        }
        Assert.That(player.velocity, Is.EqualTo(new Vector2(3, -2)));
        Assert.That(player.lastPortalColorIndex, Is.EqualTo(3));
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void M9_AckAndTargetPositionIgnoreRawCoordinatesWithoutInventingAuthorization()
    {
        var player = Main.player[Slot]; player.position = new(320, 320); player.unacknowledgedTeleports = 1;
        var ack = TeleportBody(3, short.MinValue, float.NaN, float.PositiveInfinity);
        Assert.That(RootTeleport(65, ack).Handled, Is.False); NativeTeleport(65, ack);
        Assert.That(player.unacknowledgedTeleports, Is.Zero); Assert.That(player.position, Is.EqualTo(new Vector2(320, 320)));
        var target = TeleportBody(4, Slot, float.NaN, float.NegativeInfinity);
        Assert.That(RootTeleport(65, target).Handled, Is.False); NativeTeleport(65, target);
        Assert.That(player.position, Is.EqualTo(new Vector2(320, 320)));
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
    public void M9_ConsumedNonfiniteTeleportInputStopsBeforeNativeStateAndRelay(float value)
    {
        var player = Main.player[Slot]; player.position = new(320, 320); player.velocity = Vector2.One;
        foreach (var request in new[] { (65, TeleportBody(x: value)), (65, TeleportBody(2, x: value)),
            (96, PortalBody(x: value)), (96, PortalBody(vx: value)) })
        {
            var result = M9PlayerTeleportGuard.Evaluate(M9PlayerTeleportGuard.ReadPayload(request.Item1, request.Item2).Packet!, session, TargetRuntime.Fingerprint);
            Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(RootTeleport(request.Item1, request.Item2).Handled, Is.True);
            Assert.That(player.position, Is.EqualTo(new Vector2(320, 320))); Assert.That(player.velocity, Is.EqualTo(Vector2.One));
        }
        Assert.That(sent, Is.Empty); Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        Assert.That(RootTeleport(65, TeleportBody(), true).Handled, Is.True);
        Assert.That(RootTeleport(65, TeleportBody()).Handled, Is.False, "A safe follow-up is not isolated or revoked.");
    }

    [TestCase((short)-1)] [TestCase((short)256)] [TestCase(short.MaxValue)]
    public void M9_TargetIndexIsBlockedBeforeTShockPreNormalizationDereference(short target)
    {
        foreach (byte mode in new byte[] { 0, 1, 2, 3 })
        {
            var body = TeleportBody((byte)(mode | 4), target);
            Assert.That(RootTeleport(65, body).Handled, Is.True);
        }
        Assert.That(engine.SanctionCount, Is.Zero); Assert.That(sent, Is.Empty);
        Assert.That(RootTeleport(65, TeleportBody(3, target)).Handled, Is.False,
            "Without bit2 native ignores this id and uses the actual sender, so mismatch is not wrongdoing.");
    }

    [Test]
    public void M9_TeleportReaderExactOptionalExtraAndVersionGates()
    {
        foreach (byte flags in new byte[] { 0, 3, 4, 8, 15, 255 })
        {
            var body = TeleportBody(flags);
            Assert.That(M9PlayerTeleportGuard.ReadPayload(65, body).Kind, Is.EqualTo(PacketReadKind.Parsed));
            Assert.That(M9PlayerTeleportGuard.ReadPayload(65, body[..^1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
            Assert.That(M9PlayerTeleportGuard.ReadPayload(65, body.Concat(new byte[1]).ToArray()).Kind, Is.EqualTo(PacketReadKind.Malformed));
        }
        Assert.That(M9PlayerTeleportGuard.Read(M2ContractsTests.Packet((PacketTypes)96, PortalBody()), false).Kind,
            Is.EqualTo(PacketReadKind.UnknownRuntime));
        Assert.That(RootTeleport(96, new byte[18]).Handled, Is.True);
        Assert.That(RootTeleport(65, new byte[4096]).Handled, Is.True);
    }
}
