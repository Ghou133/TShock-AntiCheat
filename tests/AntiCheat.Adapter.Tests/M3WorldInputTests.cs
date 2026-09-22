using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Progression;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M3WorldInputTests
{
    private static readonly SessionKey Session = new(Guid.NewGuid(), 1, 7, 1);
    private static string SourceDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "data", "progression", "entity-candidates.json")))
                    return Path.Combine(dir.FullName, "data", "progression");
            throw new DirectoryNotFoundException("Preserved entity source catalog not found.");
        }
    }
    private static ProgressionEntityRule Source() => ProgressionEntityCatalog.Load(Path.Combine(SourceDirectory,
        "entity-candidates.json")).Rules.Single(x => x.Rule.Id == "PG-PRJ-POL-001");
    private static M3ProgressionPolicy Policy(bool enabled = true, params int[] whitelist) => new("target-test",
        ExecutionScope.TestLab, new() { Enabled = enabled, PolicyContract = M3ProgressionPolicy.PolicyContract,
            WhitelistedProjectileTypes = whitelist }, Source());

    [TestCase("")]
    [TestCase("原版告示牌 🌳")]
    [TestCase("line 1\nline 2")]
    public void NativeSignStringsRequireBothAuditedTrailingFields(string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        { writer.Write((short)3); writer.Write((short)10); writer.Write((short)20); writer.Write(text); writer.Write((byte)7); writer.Write((byte)0); }
        var bytes = stream.ToArray();
        var actual = M3WorldPacketReader.Read(M2ContractsTests.Packet(PacketTypes.SignNew, bytes), true);
        Assert.That(actual.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(actual.Packet, Is.EqualTo(new M3WorldPacket(WorldActionKind.SignWrite, 10, 20, 3, Sender: 7)));
        Assert.That(M3WorldPacketReader.ReadPayload(PacketTypes.SignNew, bytes[..^1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3WorldPacketReader.ReadPayload(PacketTypes.SignNew, bytes.Concat(new byte[1]).ToArray()).Kind, Is.EqualTo(PacketReadKind.Malformed));
    }

    [TestCase(PacketTypes.PlaceObject, 11)]
    [TestCase(PacketTypes.RequestTileEntityInteraction, 5)]
    public void TargetObjectFramesAreExactAndVersionGated(PacketTypes type, int length)
    {
        var packet = M2ContractsTests.Packet(type, new byte[length]);
        Assert.That(M3WorldPacketReader.Read(packet, true).Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(M3WorldPacketReader.Read(packet, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        Assert.That(M3WorldPacketReader.ReadPayload(type, new byte[length - 1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3WorldPacketReader.ReadPayload(type, new byte[length + 1]).Kind, Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void InvalidSignLengthCannotAllocateOrBorrowFollowingPacket()
    {
        foreach (var prefix in new[] { new byte[] { 255,255,255,255,127 }, new byte[] { 128,128,128,128,128 }, new byte[] { 255,127 } })
            Assert.That(M3WorldPacketReader.ReadPayload(PacketTypes.SignNew, new byte[6].Concat(prefix).Concat(new byte[2]).ToArray()).Kind,
                Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void TerraAngelSignExploitLengthHasNoStringBodyAndIsRejectedBeforeNativeRead()
    {
        // The locked TerraAngel source writes the 7-bit value 60000 followed by
        // one byte, not 60000 UTF-8 bytes. This is a complete 10-byte payload,
        // but it is not a complete MessageBuffer sign frame.
        byte[] bytes = new byte[10];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, 3);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), 10);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), 20);
        bytes[6] = 0xE0; bytes[7] = 0xD4; bytes[8] = 0x03; bytes[9] = 7;

        Assert.That(M3WorldPacketReader.ReadPayload(PacketTypes.SignNew, bytes).Kind,
            Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M3WorldPacketReader.ReadPayload(PacketTypes.SignNew, bytes.Concat(new byte[] { 1, 2 }).ToArray()).Kind,
            Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void DisplayReleasePassesWithoutInventingIdentityViolationOrWorldLookup()
    {
        var release = new M3WorldPacket(WorldActionKind.DisplayEntityInteraction, 0, 0, -1, Sender: 99);
        Assert.That(M3WorldContexts.Evaluate(release, Session, null!, "target-test", false).Verdict, Is.EqualTo(Verdict.Pass));
        var cancelled = M3WorldContexts.Evaluate(release, Session, null!, "target-test", true);
        Assert.That(cancelled.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(cancelled.Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [Test]
    public void DisabledPolicyDoesNotRequireDataAndCannotEnableInProduction()
    {
        string temp = Path.Combine(Path.GetTempPath(), "AntiCheat.M3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.That(M3ProgressionPolicy.Load(temp, Path.Combine(temp, "missing"), "target-test", ExecutionScope.TestLab), Is.Null);
            WriteConfig(temp);
            Assert.That(M3ProgressionPolicy.Load(temp, Path.Combine(temp, "missing"), "target-test", ExecutionScope.Production), Is.Null);
            var policy = Policy(false);
            policy.Update(3);
            Assert.That(typeof(M3ProgressionPolicy).GetField("epoch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(policy), Is.EqualTo(0L));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Test]
    public void ChangedSourcePredicateOrCatalogDisablesOnlyThisPolicy()
    {
        var source = Source();
        Assert.Throws<InvalidDataException>(() => new M3ProgressionPolicy("target-test", ExecutionScope.TestLab,
            new(), source with { Rule = source.Rule with { Condition = new() { Op = "constant", Value = true } } }));
        string temp = Path.Combine(Path.GetTempPath(), "AntiCheat.M3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            WriteConfig(temp);
            File.WriteAllText(Path.Combine(temp, "entity-candidates.json"), File.ReadAllText(Path.Combine(SourceDirectory, "entity-candidates.json")) + " ");
            Assert.Throws<InvalidDataException>(() => M3ProgressionPolicy.Load(temp, temp, "target-test", ExecutionScope.TestLab));
            Assert.That(M3ProgressionPolicy.Load(temp, SourceDirectory, "target-test", ExecutionScope.TestLab)!.Enabled, Is.True);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Test]
    public void ActualRuntimeWorldAndKeyProducerDistinguishesLegalSyncWhitelistAndFirstPolicyViolation()
    {
        WithRuntime(() =>
        {
            var actor = Actor();
            var policy = Policy();
            var packet = Candidate();
            Assert.That(policy.Evaluate(packet, Session, actor, false)!.Verdict, Is.EqualTo(Verdict.Unknown));
            Settle(policy);
            Assert.That(policy.Evaluate(packet, Session, actor, false)!.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(policy.Evaluate(packet, Session, actor, true)!.Verdict, Is.EqualTo(Verdict.Unknown));
            var exempt = Policy(true, 406); Settle(exempt);
            Assert.That(exempt.Evaluate(packet, Session, actor, false)!.Verdict, Is.EqualTo(Verdict.Pass));
            Main.projectile[0].key = (ProjectileKey)BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
            Main.projectile[0].active = true;
            Assert.That(policy.Evaluate(packet, Session, actor, false)!.Reason, Is.EqualTo("existing-projectile-synchronization-exempt"));
        });
    }

    [Test]
    public void SameTickBossSpawnFlagChangeAndWorldChangeCannotUseEarlierSettledSnapshot()
    {
        WithRuntime(() =>
        {
            var policy = Policy(); var packet = Candidate(); var actor = Actor(); Settle(policy);
            Main.npc[0] = new NPC { active = true, type = NPCID.KingSlime };
            Assert.That(policy.Evaluate(packet, Session, actor, false)!.Verdict, Is.EqualTo(Verdict.Unknown));
            Main.npc[0].active = false;
            NPC.downedSlimeKing = true;
            Assert.That(policy.Evaluate(packet, Session, actor, false)!.Verdict, Is.EqualTo(Verdict.Unknown));
            Settle(policy);
            Assert.That(policy.Evaluate(packet, Session, actor, false)!.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(policy.Evaluate(packet, Session with { WorldEpoch = 2 }, actor, false)!.Verdict, Is.EqualTo(Verdict.Unknown));
        });
    }

    private static TSPlayer Actor() => new(7) { IsLoggedIn = true, HasSentInventory = true,
        Account = new UserAccount { ID = 731, Name = "ordinary-policy-fixture" }, Group = new Group("ordinary") };
    private static M2Packet Candidate()
    {
        byte[] body = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)(7 | (10 << 8) | (1 << 18)));
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 406);
        return new(M2PacketKind.ProjectileNew, body);
    }
    private static void Settle(M3ProgressionPolicy policy) { policy.Update(1); policy.Update(1); policy.Update(1); }
    private static void WriteConfig(string path) => File.WriteAllText(Path.Combine(path, "anticheat.json"), JsonSerializer.Serialize(new
    { ProgressionActiveCreationPolicy = new M3ProgressionPolicyOptions { Enabled = true, PolicyContract = M3ProgressionPolicy.PolicyContract } }));
    private static void WithRuntime(Action action)
    {
        int netMode = Main.netMode, width = Main.maxTilesX;
        bool downed = NPC.downedSlimeKing;
        var npcs = Main.npc; var projectiles = Main.projectile; var keys = Projectile.keyToIndex;
        try
        {
            Main.netMode = 2; Main.maxTilesX = 100; NPC.downedSlimeKing = false;
            Main.npc = new NPC[200]; Main.projectile = [new Projectile { active = false }]; Projectile.keyToIndex = new int[256, 1001];
            action();
        }
        finally { Main.netMode = netMode; Main.maxTilesX = width; NPC.downedSlimeKing = downed;
            Main.npc = npcs; Main.projectile = projectiles; Projectile.keyToIndex = keys; }
    }
}
