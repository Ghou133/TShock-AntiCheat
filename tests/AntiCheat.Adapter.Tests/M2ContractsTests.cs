using System.Reflection;
using System.Net;
using System.Runtime.InteropServices;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using TerrariaApi.Server;

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
