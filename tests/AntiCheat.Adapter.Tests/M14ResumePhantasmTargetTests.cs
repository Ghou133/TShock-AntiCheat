using System.Buffers.Binary;
using AntiCheat.Plugin.TShock;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    private static byte[] M14ResumePhantasmBody(float target, short type = ProjectileID.PhantasmArrow)
    {
        byte[] body = new byte[31];
        BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Slot, 37, 1).bits);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4), 400f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), 400f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(12), 6f);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), type);
        body[22] = 3; BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(23), target);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(27), 15f);
        return body;
    }

    private void M14ResumeWithPhantasm(Action action, int capacity = 201)
    {
        var projectiles = Main.projectile; var map = Projectile.keyToIndex; var hostile = Main.projHostile; var npcs = Main.npc;
        try
        {
            Main.projectile = Enumerable.Range(0, Main.maxProjectiles).Select(_ => new Projectile()).ToArray();
            Projectile.keyToIndex = new int[256, 1001];
            Main.projHostile = new bool[ProjectileID.Count];
            Main.npc = Enumerable.Range(0, capacity).Select(i => new NPC { whoAmI = i, active = false }).ToArray();
            action();
        }
        finally { Main.projectile = projectiles; Projectile.keyToIndex = map; Main.projHostile = hostile; Main.npc = npcs; }
    }

    [TestCase(-1f)] [TestCase(-1.99f)] [TestCase(-0.99f)] [TestCase(0f)] [TestCase(199.9f)] [TestCase(200.9f)]
    public void M14ResumePhantasm_LegalNativeProjectionAndInactiveTargetKeepOriginalBehavior(float value)
    {
        M14ResumeWithPhantasm(() =>
        {
            var body = M14ResumePhantasmBody(value);
            var admission = M14ResumePhantasmTargetSafety.Read(M2ContractsTests.Packet((PacketTypes)27, body, Slot), true);
            Assert.That(admission!.Value.RejectMalformed, Is.False);
            Assert.That(RootDisplay(27, body).Handled, Is.False); NativeDisplay(27, body);
            Assert.That(new ProjectileKey(Slot, 37, 1).TryGet(out var shot), Is.True);
            Assert.That(shot.ai[0], Is.EqualTo(value)); Assert.That(shot.ai[1], Is.EqualTo(15f));
            Assert.That(shot.aiStyle, Is.EqualTo(122)); Assert.That(shot.friendly, Is.True);
            Assert.That(sent.Single().Id, Is.EqualTo(27)); sent.Clear();
            Assert.DoesNotThrow(() => shot.AI());
            Assert.That(shot.ai[0], Is.EqualTo(-1f), "Inactive targets retain native detach-to-minus-one behavior.");
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [TestCase(-2f)] [TestCase(201f)] [TestCase(30000f)]
    public void M14ResumePhantasm_InvalidIndexActuallyThrowsInNativeAiButBlocksBeforeCreationAndSameAccountRecovers(float value)
    {
        M14ResumeWithPhantasm(() =>
        {
            var body = M14ResumePhantasmBody(value);
            NativeDisplay(27, body);
            Assert.That(new ProjectileKey(Slot, 37, 1).TryGet(out var shot), Is.True);
            Assert.That(shot.ai[0], Is.EqualTo(value)); Assert.That(sent.Single().Id, Is.EqualTo(27));
            Assert.Throws<IndexOutOfRangeException>(() => shot.AI());
            shot.active = false; sent.Clear();
            Assert.That(RootDisplay(27, body).Handled, Is.True);
            Assert.That(Main.projectile.Any(p => p.active), Is.False); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
            var legal = M14ResumePhantasmBody(-1f);
            Assert.That(RootDisplay(27, legal).Handled, Is.False); NativeDisplay(27, legal);
            Assert.That(new ProjectileKey(Slot, 37, 1).TryGet(out var recovered), Is.True);
            Assert.That(recovered.ai[0], Is.EqualTo(-1f)); Assert.That(sent.Single().Id, Is.EqualTo(27));
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public void M14ResumePhantasm_ExistingOwnedEntityIsNotOverwrittenByBadUpdate()
    {
        M14ResumeWithPhantasm(() =>
        {
            var legal = M14ResumePhantasmBody(-1f); NativeDisplay(27, legal);
            Assert.That(new ProjectileKey(Slot, 37, 1).TryGet(out var shot), Is.True); sent.Clear();
            Assert.That(RootDisplay(27, M14ResumePhantasmBody(201f)).Handled, Is.True);
            Assert.That(shot.active, Is.True); Assert.That(shot.ai[0], Is.EqualTo(-1f)); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        });
    }

    [Test]
    public void M14ResumePhantasm_ActualArrayCapacityRuntimeUnmodeledTypesAndMalformedFramesRemainScoped()
    {
        M14ResumeWithPhantasm(() =>
        {
            var body = M14ResumePhantasmBody(200f);
            Assert.That(M14ResumePhantasmTargetSafety.Read(M2ContractsTests.Packet((PacketTypes)27, body, Slot), true)!.Value.RejectMalformed, Is.True);
            Assert.That(M14ResumePhantasmTargetSafety.Read(M2ContractsTests.Packet((PacketTypes)27, body, Slot), false), Is.Null);
            Assert.That(M14ResumePhantasmTargetSafety.Read(M2ContractsTests.Packet((PacketTypes)27,
                M14ResumePhantasmBody(999f, ProjectileID.WoodenArrowFriendly), Slot), true), Is.Null);
            Assert.That(RootDisplay(27, M14ResumePhantasmBody(-1f)[..^1]).Handled, Is.True);
            Assert.That(RootDisplay(27, M14ResumePhantasmBody(-1f).Concat(new byte[1]).ToArray()).Handled, Is.True);
            Assert.That(RootDisplay(27, M14ResumePhantasmBody(-1f), true).Handled, Is.True);
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue })
                Assert.That(RootDisplay(27, M14ResumePhantasmBody(value)).Handled, Is.True);
            Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        }, 200);
        M14ResumeWithPhantasm(() => Assert.That(M14ResumePhantasmTargetSafety.Read(M2ContractsTests.Packet(
            (PacketTypes)27, M14ResumePhantasmBody(300f), Slot), true), Is.Null), 202);
    }
}
