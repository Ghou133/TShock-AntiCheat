using System.Reflection;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
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
            Assert.That(M2PacketReader.TryReadProjectilePayload(backing.AsSpan(7, body.Length - 1), out _), Is.False,
                "A short frame must not borrow a byte from the backing buffer.");
            Assert.That(M2PacketReader.TryReadProjectilePayload(backing.AsSpan(7, body.Length + 1), out _), Is.False,
                "A trailing byte must not be interpreted as part of the native frame.");
        });
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
            var missingMarker = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.That(missingMarker.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(missingMarker.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            Assert.That(missingMarker.M18EnableBlocks, Is.False);
            AssertNoTestLabAdmission(missingMarker);
            File.WriteAllText(Path.Combine(root, ".anticheat-lab"), "test");
            var nonLoopback = M2Configuration.Load(save, IPAddress.Any).Configuration;
            Assert.That(nonLoopback.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(nonLoopback.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            AssertNoTestLabAdmission(nonLoopback);
            Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.TestLab));
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root[..^1]);
            var mismatchedSave = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.That(mismatchedSave.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(mismatchedSave.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            AssertNoTestLabAdmission(mismatchedSave);
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root);
            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"TestLab\",\"M18CandidateMode\":\"ProductionCandidate\"}");
            var candidateScopeMismatch = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.That(candidateScopeMismatch.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(candidateScopeMismatch.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            AssertNoTestLabAdmission(candidateScopeMismatch);
            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"TestLab\",\"M18CandidateMode\":\"TestLabCandidate\"}");
            var validExplicitCandidate = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.That(validExplicitCandidate.ExecutionScope, Is.EqualTo(ExecutionScope.TestLab));
            Assert.That(validExplicitCandidate.M18CandidateMode, Is.EqualTo(M18CandidateMode.TestLabCandidate));
            Assert.That(M2RuleRegistry.Create("test", validExplicitCandidate.ExecutionScope)
                .Any(x => x.Qualification == RuleQualification.TestLab), Is.True);
            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"TestLabCandidate\"}");
            var productionScopeMismatch = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.That(productionScopeMismatch.ExecutionScope, Is.EqualTo(ExecutionScope.Production));
            Assert.That(productionScopeMismatch.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            Assert.That(M2RuleRegistry.Create("test", productionScopeMismatch.ExecutionScope)
                .All(x => x.Qualification != RuleQualification.TestLab), Is.True);
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

    private static void AssertNoTestLabAdmission(M2Configuration configuration)
    {
        Assert.That(configuration.ExecutionScope, Is.Not.EqualTo(ExecutionScope.TestLab));
        var policies = M2RuleRegistry.Create("test", configuration.ExecutionScope);
        Assert.That(policies, Is.Not.Empty);
        Assert.That(policies.All(x => x.Qualification != RuleQualification.TestLab), Is.True);
    }

    [Test]
    public void ExplicitM18ProductionCandidateRequiresOwnedBoundaryAndMapsIndependentControls()
    {
        string root = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N"));
        string save = Path.Combine(root, "server-save");
        Directory.CreateDirectory(save);
        string? prior = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root);
            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"ProductionCandidate\"," +
                "\"M18RecordObservations\":true,\"M18EnableBlocks\":false," +
                "\"M18EnablePermanentSanctions\":true}");

            var beforeMarker = M2Configuration.Load(save, IPAddress.Loopback);
            Assert.Multiple(() =>
            {
                Assert.That(beforeMarker.Configuration.ExecutionScope,
                    Is.EqualTo(ExecutionScope.Production), "Boundary failure preserves the existing production scope.");
                Assert.That(beforeMarker.Configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.Auto));
                Assert.That(beforeMarker.Configuration.M18EnableBlocks, Is.False);
                Assert.That(beforeMarker.Reason, Does.Contain("candidate-disabled-production-preserved"));
            });
            File.WriteAllText(Path.Combine(root, ".anticheat-lab"), "test");

            var loaded = M2Configuration.Load(save, IPAddress.Loopback);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.Reason, Is.EqualTo("isolated-loopback-production-candidate"));
                Assert.That(loaded.Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.Production));
                Assert.That(loaded.Configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.ProductionCandidate));
            });

            var npc = M18NpcStrikeQueueOptions.ForExecutionScope(loaded.Configuration.ExecutionScope,
                loaded.Configuration.M18CandidateMode, loaded.Configuration.M18RecordObservations,
                loaded.Configuration.M18EnableBlocks, loaded.Configuration.M18EnablePermanentSanctions);
            var world = M18WorldEditQueueOptions.ForExecutionScope(loaded.Configuration.ExecutionScope,
                loaded.Configuration.M18CandidateMode, loaded.Configuration.M18RecordObservations,
                loaded.Configuration.M18EnableBlocks);
            var particle = M18ParticleQueueOptions.ForExecutionScope(loaded.Configuration.ExecutionScope,
                loaded.Configuration.M18CandidateMode, loaded.Configuration.M18RecordObservations,
                loaded.Configuration.M18EnableBlocks);
            var important = M18ImportantItemQueueOptions.ForExecutionScope(loaded.Configuration.ExecutionScope,
                loaded.Configuration.M18CandidateMode, loaded.Configuration.M18RecordObservations);
            var lockHealth = M18LockHealthOptions.ForExecutionScope(loaded.Configuration.ExecutionScope,
                loaded.Configuration.M18CandidateMode, loaded.Configuration.M18RecordObservations,
                loaded.Configuration.M18EnableBlocks, loaded.Configuration.M18EnableServiceKick);
            Assert.Multiple(() =>
            {
                Assert.That(npc.Enabled, Is.True);
                Assert.That(npc.EnablePreForwardBlocks, Is.False);
                Assert.That(npc.EnablePermanentSanctions, Is.False,
                    "Production candidate config cannot grant an unqualified permanent sanction path.");
                Assert.That(world.Enabled && !world.EnablePreForwardBlocks, Is.True);
                Assert.That(particle.Enabled && !particle.EnablePreForwardBlocks, Is.True);
                Assert.That(important.Enabled, Is.True);
                Assert.That(lockHealth.Enabled, Is.True);
                Assert.That(lockHealth.EnableServiceBlock, Is.False);
                Assert.That(lockHealth.EnableServiceKick, Is.False,
                    "Production candidate defaults service kicks off unless explicitly configured.");
            });

            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"ProductionCandidate\"," +
                "\"M18EnableServiceKick\":true}");
            var kickConfig = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            var kickOptions = M18LockHealthOptions.ForExecutionScope(kickConfig.ExecutionScope,
                kickConfig.M18CandidateMode, kickConfig.M18RecordObservations,
                kickConfig.M18EnableBlocks, kickConfig.M18EnableServiceKick);
            Assert.That(kickOptions.EnableServiceKick, Is.True);

            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"Auto\"}");
            var autoProduction = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            var autoProductionNpc = M18NpcStrikeQueueOptions.ForExecutionScope(autoProduction.ExecutionScope,
                autoProduction.M18CandidateMode);
            var autoProductionWorld = M18WorldEditQueueOptions.ForExecutionScope(autoProduction.ExecutionScope,
                autoProduction.M18CandidateMode);
            var autoProductionParticle = M18ParticleQueueOptions.ForExecutionScope(autoProduction.ExecutionScope,
                autoProduction.M18CandidateMode);
            var autoProductionImportant = M18ImportantItemQueueOptions.ForExecutionScope(autoProduction.ExecutionScope,
                autoProduction.M18CandidateMode);
            var autoProductionLock = M18LockHealthOptions.ForExecutionScope(autoProduction.ExecutionScope,
                autoProduction.M18CandidateMode);
            Assert.Multiple(() =>
            {
                Assert.That(autoProductionNpc.Enabled && !autoProductionNpc.EnablePreForwardBlocks, Is.True);
                Assert.That(autoProductionWorld.Enabled && !autoProductionWorld.EnablePreForwardBlocks, Is.True);
                Assert.That(autoProductionParticle.Enabled && !autoProductionParticle.EnablePreForwardBlocks, Is.True);
                Assert.That(autoProductionImportant.Enabled, Is.True);
                Assert.That(autoProductionLock.Enabled && !autoProductionLock.EnableServiceBlock &&
                    !autoProductionLock.EnableServiceKick, Is.True);
            });

            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"ProductionCandidate\"}");
            var invalidBind = M2Configuration.Load(save, IPAddress.Any);
            Assert.Multiple(() =>
            {
                Assert.That(invalidBind.Configuration.ExecutionScope,
                    Is.EqualTo(ExecutionScope.Production), "A non-loopback bind cannot replace the existing production scope.");
                Assert.That(invalidBind.Configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.Auto));
                Assert.That(invalidBind.Configuration.M18EnableBlocks, Is.False);
            });
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
