using System.Reflection;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M2ContractsTests
{
    [TestCase(PacketTypes.Emoji, 2)]
    [TestCase(PacketTypes.PlayerSlot, 9)]
    [TestCase(PacketTypes.Tile, 8)]
    [TestCase(PacketTypes.LiquidSet, 6)]
    [TestCase(PacketTypes.ChestGetContents, 4)]
    [TestCase(PacketTypes.ChestItem, 8)]
    [TestCase(PacketTypes.ProjectileDestroy, 12)]
    [TestCase(PacketTypes.PlayerAddBuff, 7)]
    [TestCase(PacketTypes.PlayerHealOther, 3)]
    public void ActualTargetFixedFramesAcceptCompleteAndRejectTruncatedOrTrailingData(PacketTypes type, int size)
    {
        var packet = Packet(type, new byte[size]);
        Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Parsed));
        packet.Length--;
        Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        packet = Packet(type, new byte[size + 1]);
        Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M2PacketReader.Read(packet, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
    }

    [TestCase(PacketTypes.ItemDrop)]
    [TestCase(PacketTypes.UpdateItemDrop)]
    public void WorldItemDropFramesFollowTargetConditionalFlags(PacketTypes type)
    {
        foreach (byte flags in new byte[] { 0, 4, 8, 12, 0xF0 })
        {
            int size = 24 + ((flags & 4) != 0 ? 5 : 0) + ((flags & 8) != 0 ? 1 : 0);
            var body = new byte[size]; body[21] = flags;
            var packet = Packet(type, body);
            Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Parsed), $"flags={flags}");
            packet.Length--;
            Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed), $"truncated flags={flags}");
            packet = Packet(type, [.. body, 0]);
            Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed), $"trailing flags={flags}");
        }
    }

    [Test]
    public void SyncItemDespawnFrameIsExactlyTheTwoByteItemIndex()
    {
        var packet = Packet(PacketTypes.SyncItemDespawn, new byte[2]);
        Assert.Multiple(() =>
        {
            Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Parsed));
            Assert.That(M2PacketReader.Read(packet, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        });
        packet.Length--;
        Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M2PacketReader.Read(Packet(PacketTypes.SyncItemDespawn, new byte[3]), true).Kind,
            Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void ThreeOrdinaryPacket151PickupsStayNativePassWithoutAccountSanction()
    {
        WithGroundItemRuntime((adapter, actor, session) =>
        {
            for (short slot = 1; slot <= 3; slot++)
            {
                Main.item[slot] = GroundItem(slot, 5, new Vector2(100, 100));
                var result = Despawn(adapter, actor, session, slot);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Action, Is.EqualTo(ControlAction.Pass));
                    Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass));
                    Assert.That(result.PredicateSatisfied, Is.False);
                    Assert.That(result.Facts["requestCoordinates"], Is.EqualTo("unavailable(packet151-body-id-only)"));
                });
            }
            actor.TPlayer.position = new Vector2(5000, 5000);
            var remote = Despawn(adapter, actor, session, 3);
            Assert.That(remote.Action, Is.EqualTo(ControlAction.Unknown),
                "Server-side item and actor positions may be stale; a remote snapshot alone cannot block pickup.");
            Assert.That(remote.PredicateSatisfied, Is.False);

            Main.player[session.Slot] = null!;
            var missingActor = Despawn(adapter, actor, session, 3);
            Assert.That(missingActor.Action, Is.EqualTo(ControlAction.Unknown),
                "A missing current player cannot supply accepted position or account proof.");
            Assert.That(missingActor.PredicateSatisfied, Is.False);
        });
    }

    [Test]
    public void GroundItemGenerationDoesNotChangeForStackOrMovementOfSameSlotObject()
    {
        WithGroundItemRuntime((adapter, actor, session) =>
        {
            const short slot = 4;
            var item = GroundItem(slot, 5, new Vector2(100, 100));
            Main.item[slot] = item;
            string first = Despawn(adapter, actor, session, slot).Facts["targetGeneration"];
            item.stack = 4;
            item.position = new Vector2(104, 100);
            string updated = Despawn(adapter, actor, session, slot).Facts["targetGeneration"];
            Assert.That(updated, Is.EqualTo(first), "Mutable stack and position are the same target.");

            Main.item[slot] = GroundItem(slot, 5, new Vector2(100, 100));
            string replacement = Despawn(adapter, actor, session, slot).Facts["targetGeneration"];
            Assert.That(int.Parse(replacement), Is.GreaterThan(int.Parse(first)),
                "A new server object in the reused slot starts a new target generation.");
        });
    }

    private static WorldItem GroundItem(int slot, int stack, Vector2 position)
    {
        var item = new WorldItem { whoAmI = slot };
        item.inner.SetDefaults(ItemID.Wood);
        item.stack = stack;
        item.position = position;
        return item;
    }

    private static BusinessRuleResult Despawn(M2BusinessAdapter adapter, TSPlayer actor,
        SessionKey session, short slot)
    {
        byte[] body = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(body, slot);
        var parsed = M2PacketReader.Read(Packet(PacketTypes.SyncItemDespawn, body), true);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        return adapter.Evaluate(parsed.Packet!, session, actor, _ => (null, null), false)
            .Single(result => result.RuleId == M18GroundItemClearQueueRules.RuleId);
    }

    private static void WithGroundItemRuntime(Action<M2BusinessAdapter, TSPlayer, SessionKey> action)
    {
        const int slot = 7;
        var oldItems = Main.item;
        var oldPlayer = Main.player[slot];
        int oldMode = Main.netMode, oldWidth = Main.maxTilesX;
        try
        {
            Main.netMode = 0; Main.maxTilesX = 0;
            Main.item = Enumerable.Range(0, 401).Select(index => new WorldItem { whoAmI = index }).ToArray();
            Main.player[slot] = new Player { whoAmI = slot, active = true, position = new Vector2(100, 100) };
            var actor = new TSPlayer(slot) { IsLoggedIn = true,
                Account = new UserAccount { ID = 707, Name = "ground-item-fixture" },
                Group = new Group("ground-item-fixture") };
            var session = new SessionKey(Guid.NewGuid(), 1, slot, 1);
            var adapter = new M2BusinessAdapter(TargetRuntime.Fingerprint,
                Path.Combine(Path.GetTempPath(), "absent-ground-item-data"),
                M18GroundItemClearQueueOptions.TestLabCandidate with
                { EnablePreForwardBlocks = false, EnablePermanentSanctions = true });
            adapter.Update(_ => null, session.WorldEpoch);
            action(adapter, actor, session);
        }
        finally
        {
            Main.item = oldItems;
            Main.player[slot] = oldPlayer;
            Main.netMode = oldMode; Main.maxTilesX = oldWidth;
        }
    }

    [Test]
    public void SentryNativeFrameReaderUsesTheSelectedOffsetAndRejectsShortOrTrailingBodies()
    {
        var body = new byte[23];
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 308);
        byte[] backing = new byte[64];
        body.CopyTo(backing, 7);
        Assert.Multiple(() =>
        {
            Assert.That(M2PacketReader.TryReadProjectilePayload(backing.AsSpan(7, body.Length), out var selected), Is.True);
            Assert.That(selected, Is.EqualTo(body));
            Assert.That(M2PacketReader.TryReadProjectilePayload(backing.AsSpan(7, body.Length - 1), out _), Is.False);
            Assert.That(M2PacketReader.TryReadProjectilePayload(backing.AsSpan(7, body.Length + 1), out _), Is.False);
        });
    }

    [Test]
    public void PlayerUpdateAcceptsEveryOptionalFieldCombinationWithoutOverread()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            var body = new byte[14 + ((mask & 1) != 0 ? 8 : 0) + ((mask & 2) != 0 ? 2 : 0) + ((mask & 4) != 0 ? 16 : 0) + ((mask & 8) != 0 ? 8 : 0)];
            body[2] = (byte)(((mask & 1) != 0 ? 4 : 0) | ((mask & 2) != 0 ? 128 : 0));
            body[3] = (byte)((mask & 4) != 0 ? 64 : 0);
            body[4] = (byte)((mask & 8) != 0 ? 32 : 0);
            Assert.That(M2PacketReader.Read(Packet(PacketTypes.PlayerUpdate, body), true).Kind, Is.EqualTo(PacketReadKind.Parsed));
            Assert.That(M2PacketReader.Read(Packet(PacketTypes.PlayerUpdate, body[..^1]), true).Kind, Is.EqualTo(PacketReadKind.Malformed));
        }
    }

    [Test]
    public void ProjectileOptionalFieldsUseTargetTwoFlagBytesAndImmutablePayloadCopy()
    {
        byte[] body = new byte[46]; // 23 + second byte + AI1,AI2,damage,originalDamage,knockback,uuid,AI3
        body[22] = 127; body[23] = 1;
        var packet = Packet(PacketTypes.ProjectileNew, body);
        var parsed = M2PacketReader.Read(packet, true);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed));
        body[0] = 99;
        Assert.That(parsed.Packet!.Payload[0], Is.Zero);
        packet.Length--;
        Assert.That(M2PacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
    }

    [TestCase("9.0.7", true, Architecture.X64, true)]
    [TestCase("9.0.8", true, Architecture.X64, false)]
    [TestCase("9.0.7", false, Architecture.X64, false)]
    [TestCase("9.0.7", true, Architecture.X86, false)]
    [TestCase("9.0.7", true, Architecture.Arm64, false)]
    public void HostAdmissionRequiresTheAuditedRuntime(string version, bool windows, Architecture architecture, bool expected) =>
        Assert.That(TargetRuntime.AcceptsHostRuntime(Version.Parse(version), windows, architecture), Is.EqualTo(expected));

    [Test]
    public void TestLabRequiresIsolationAndProductionConfigurationCannotAddRuleAdmission()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N"));
        string save = Path.Combine(root, "save");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "anticheat.json"), "{\"ExecutionScope\":\"TestLab\"}");
        string? prior = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        try
        {
            Assert.That(new M2Configuration().ExperimentalLiquidScheduler, Is.False);
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root);
            Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            File.WriteAllText(Path.Combine(root, ".anticheat-lab"), "test");
            Assert.That(M2Configuration.Load(save, IPAddress.Any).Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.TestLab));
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root[..^1]);
            Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            File.WriteAllText(Path.Combine(save, "anticheat.json"), "{\"ExecutionScope\":\"Production\"}");
            Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExperimentalLiquidScheduler, Is.False);
            File.WriteAllText(Path.Combine(save, "anticheat.json"), "{\"ExecutionScope\":\"Production\",\"ExperimentalLiquidScheduler\":true}");
            Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExperimentalLiquidScheduler, Is.True);
            var scope = M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExecutionScope;
            Assert.That(M2RuleRegistry.Create("test", scope).All(x => x.Qualification == RuleQualification.Unqualified), Is.True);
            var locked = new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "verified-fixture");
            var admitted = M2RuleRegistry.Create(locked, scope, ["M3.UnreviewedCandidate"]);
            Assert.That(admitted.Where(x => x.Qualification == RuleQualification.ProductionQualified).Select(x => (x.RuleId, x.Version)),
                Is.EquivalentTo(new[] {
                    ("A01.EmojiSenderMismatch", "m2.1"), ("A02.InventorySenderMismatch", "m2.1"),
                    ("NPC01.ServerDebuffDamage", "1.0.0"), ("NPC02.ServerPortalTeleport", "1.0.0"),
                    ("C2.CultistRitualRole", "1.0.0"), ("C6.PortalPlacementDamage", "1.0.0"),
                    ("VITAL01.RawLifeMaximum", "1.0.0"), ("VITAL02.RawManaMaximum", "1.0.0"),
                    ("CONTAINER01.ChestResizeAuthority", "1.0.0"), ("B4.ContainerAuthorization", "1.1.0"),
                    ("VITAL04.NonPvpHurtTarget", "1.0.0"), ("PG-NAT-012.MechdusaSummonWorld", "1.0.0"),
                ("C7.WoodenArrowDamageProjection", "1.0.0"), ("PG-NAT-121.CelestialSigilWorld", "1.0.0"),
                ("NPC03.BossPartSummonRequest", "1.0.0"), ("PG-NAT-108.GolemSummonWorld", "1.0.0"),
                ("G03.NpcBuffRemovalContract", "1.0.0"), ("NPC04.BuffStateSyncAuthority", "1.0.0"),
                ("G05.NaturalRodWorldBorder", "1.0.0"),
                ("NPC05.EventStateAuthority", "1.0.0"), ("G03.PlayerBuffAddContract", "1.0.0"),
                ("G03.NpcShadowFlameAddContract", "1.0.0"),
                ("G03.NpcShimmerAddContract", "1.0.0"), ("WORLD01.WorldAlignmentStateAuthority", "1.0.0"),
                ("NPC06.CavernMonsterStateAuthority", "1.0.0"),
                ("WORLD02.CreditsRollStateAuthority", "1.0.0"), ("PROJ01.CannonFiringAuthority", "1.0.0"),
                ("G03.NpcBuffTypeContract", "1.0.0") }));
            Assert.That(admitted.Single(x => x.RuleId == "M3.UnreviewedCandidate").Qualification, Is.EqualTo(RuleQualification.Unqualified));
        }
        finally { Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", prior); }
    }

    public static GetDataEventArgs Packet(PacketTypes type, byte[] body, int sender = 7)
    {
        var args = new GetDataEventArgs { MsgID = type, Index = 0, Length = body.Length + 1 };
        typeof(GetDataEventArgs).GetProperty(nameof(GetDataEventArgs.Msg))!.SetValue(args,
            new Terraria.MessageBuffer { whoAmI = sender, readBuffer = body });
        return args;
    }
}
