using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M13NpcBuffContractTests
{
    private int oldMode, oldMyPlayer;
    private bool[] oldRemovalTable = null!;
    private readonly List<(int Id, int Target, int Buff, int Time)> sends = [];
    private readonly SessionKey session = new(Guid.NewGuid(), 1, 7, 1);

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer;
        oldRemovalTable = BuffID.Sets.CanBeRemovedByNetMessage;
        sends.Clear();
        HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer;
        BuffID.Sets.CanBeRemovedByNetMessage = oldRemovalTable;
    }

    private void Capture(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        sends.Add((args.msgType, args.number, (int)args.number2, (int)args.number3));
        args.ContinueExecution = false;
    }

    [TestCase(BuffID.OnFire)] [TestCase(BuffID.ScytheWhipEnemyDebuff)] [TestCase(BuffID.EelWhipNPCDebuff)]
    public void RealClientAddBuffPassesAndOriginalRemovalReturnsBeforeMutationOrSend(int type)
    {
        Main.netMode = 1; Main.myPlayer = session.Slot;
        var npc = new NPC { whoAmI = 3, active = true };
        npc.AddBuff(type, 600);
        Assert.That(sends, Is.EqualTo(new[] { (53, 3, type, 600) }));
        Assert.That(npc.FindBuffIndex(type), Is.GreaterThanOrEqualTo(0));
        Assert.That(M13NpcBuffPacketReader.Evaluate(new(M13NpcBuffOperation.Add, 3, type, 600), session,
            TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Pass));
        sends.Clear();
        var before = npc.buffType.ToArray(); var times = npc.buffTime.ToArray();
        npc.RequestBuffRemoval(type);
        Assert.That(sends, Is.Empty);
        Assert.That(npc.buffType, Is.EqualTo(before)); Assert.That(npc.buffTime, Is.EqualTo(times));
        Assert.That(M13NpcBuffPacketReader.Evaluate(new(M13NpcBuffOperation.Remove, 3, type), session,
            TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ExplicitNativeTableExtensionReallyEnablesRemovalAndSuspendsHardRule()
    {
        Main.netMode = 1; Main.myPlayer = session.Slot;
        var npc = new NPC { whoAmI = 3, active = true };
        npc.AddBuff(BuffID.OnFire, 600, quiet: true);
        BuffID.Sets.CanBeRemovedByNetMessage = (bool[])oldRemovalTable.Clone();
        BuffID.Sets.CanBeRemovedByNetMessage[BuffID.OnFire] = true;
        Assert.That(M13NpcBuffPacketReader.RemovalTableIntact(), Is.False);
        Assert.That(M13NpcBuffPacketReader.Evaluate(new(M13NpcBuffOperation.Remove, 3, BuffID.OnFire), session,
            TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Unknown));
        npc.RequestBuffRemoval(BuffID.OnFire);
        Assert.That(npc.FindBuffIndex(BuffID.OnFire), Is.EqualTo(-1));
        Assert.That(sends, Is.EqualTo(new[] { (137, 3, BuffID.OnFire, 0) }));
    }

    [Test]
    public void ExactFixedFramesBoundMalformedRequestsAndKeepAddSeparate()
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(body, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), BuffID.OnFire);
        var read = M13NpcBuffPacketReader.ReadPayload(137, body);
        Assert.That(read.Kind, Is.EqualTo(PacketReadKind.Parsed));
        Assert.That(read.Packet, Is.EqualTo(new M13NpcBuffObservation(M13NpcBuffOperation.Remove, 3, BuffID.OnFire)));
        foreach (int length in new[] { 0, 1, 2, 3, 5, 100 })
            Assert.That(M13NpcBuffPacketReader.ReadPayload(137, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        foreach (int length in new[] { 0, 4, 5, 7 })
            Assert.That(M13NpcBuffPacketReader.ReadPayload(53, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        Assert.That(M13NpcBuffPacketReader.ReadPayload(54, []).Kind, Is.EqualTo(PacketReadKind.Unrelated));
        var args = M2ContractsTests.Packet((PacketTypes)137, body);
        Assert.That(M13NpcBuffPacketReader.Read(args, true).Packet, Is.EqualTo(read.Packet));
        Assert.That(M13NpcBuffPacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        args.Index = args.Msg.readBuffer.Length;
        Assert.That(M13NpcBuffPacketReader.Read(args, true).Kind, Is.EqualTo(PacketReadKind.Malformed));
    }

    [Test]
    public void UnknownHostExtensionCannotTurnSyntheticRemovalIntoAccountProof()
    {
        var packet = new M13NpcBuffObservation(M13NpcBuffOperation.Remove, 3, BuffID.OnFire);
        Assert.That(M13NpcBuffPacketReader.Evaluate(packet, session, TargetRuntime.Fingerprint,
            nativeHostContractComplete: false).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(M13NpcBuffPacketReader.Evaluate(packet, session, TargetRuntime.Fingerprint,
            nativeHostContractComplete: true).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void LockedRuntimeHasAnEmptyRemovalTableAndOnlyItsGuardReadsTheTable()
    {
        Assert.That(BuffID.Count, Is.EqualTo(401));
        Assert.That(M13NpcBuffPacketReader.RemovalTableIntact(), Is.True);
        AuditImage(typeof(NPC).Assembly.Location, TargetRuntime.OtApiSha256, true);
    }

    [Test]
    public void LockedOriginalClientHasTheSameExclusiveRemovalProducerAndTableReferences()
    {
        const string client = "E:/SteamLibrary/steamapps/common/Terraria/Terraria.exe";
        Assert.That(File.Exists(client), Is.True, "Original 1.4.5.8 audit image is required; do not substitute a patched client.");
        AuditImage(client, "960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3", false);
    }

    private static void AuditImage(string path, string hash, bool wrapped)
    {
        using var image = File.OpenRead(path);
        Assert.That(Convert.ToHexString(SHA256.HashData(image)), Is.EqualTo(hash)); image.Position = 0;
        using var pe = new PEReader(image); var md = pe.GetMetadataReader();
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        string TypeName(EntityHandle h)
        {
            if (h.Kind == HandleKind.TypeDefinition)
            {
                var t = md.GetTypeDefinition((TypeDefinitionHandle)h);
                return t.GetDeclaringType().IsNil ? md.GetString(t.Namespace) + "." + md.GetString(t.Name)
                    : TypeName(t.GetDeclaringType()) + "+" + md.GetString(t.Name);
            }
            if (h.Kind == HandleKind.TypeReference)
            {
                var t = md.GetTypeReference((TypeReferenceHandle)h); return md.GetString(t.Namespace) + "." + md.GetString(t.Name);
            }
            return "unresolved";
        }
        (string Type, string Name) Member(EntityHandle h)
        {
            if (h.Kind == HandleKind.FieldDefinition) { var m = md.GetFieldDefinition((FieldDefinitionHandle)h); return (TypeName(m.GetDeclaringType()), md.GetString(m.Name)); }
            if (h.Kind == HandleKind.MethodDefinition) { var m = md.GetMethodDefinition((MethodDefinitionHandle)h); return (TypeName(m.GetDeclaringType()), md.GetString(m.Name)); }
            if (h.Kind == HandleKind.MemberReference) { var m = md.GetMemberReference((MemberReferenceHandle)h); return (TypeName(m.Parent), md.GetString(m.Name)); }
            return ("unresolved", "unresolved");
        }
        var tableRefs = new SortedSet<string>(); var sendCandidates = new SortedSet<string>(); var requestCallers = new SortedSet<string>();
        foreach (var h in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(h); if (method.RelativeVirtualAddress == 0) continue;
            string caller = TypeName(method.GetDeclaringType()) + "." + md.GetString(method.Name);
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            bool constant137 = false, sends = false;
            for (int i = 0; i < il.Length;)
            {
                ushort value = il[i++]; if (value == 0xfe) value = (ushort)(0xfe00 | il[i++]); var op = codes[value];
                if (op == OpCodes.Ldc_I4 && BitConverter.ToInt32(il, i) == 137) constant137 = true;
                if (op.OperandType is OperandType.InlineField or OperandType.InlineMethod)
                {
                    var target = Member(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i)));
                    if (target.Type == "Terraria.ID.BuffID+Sets" && target.Name == "CanBeRemovedByNetMessage") tableRefs.Add(caller);
                    if (target.Type == "Terraria.NetMessage" && target.Name is "SendData" or "TrySendData") sends = true;
                    if (target.Type == "Terraria.NPC" && target.Name == "RequestBuffRemoval") requestCallers.Add(caller);
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i), _ => 4
                };
            }
            if (constant137 && sends) sendCandidates.Add(caller);
        }
        string prefix = wrapped ? "mfwh_" : "";
        TestContext.Out.WriteLine("Table references: " + string.Join("; ", tableRefs));
        TestContext.Out.WriteLine("Methods containing 137 and SendData (includes unrelated constants): " + string.Join("; ", sendCandidates));
        TestContext.Out.WriteLine("Removal callers: " + string.Join("; ", requestCallers));
        Assert.That(tableRefs, Is.EquivalentTo(new[] { "Terraria.ID.BuffID+Sets..cctor", "Terraria.NPC." + prefix + "RequestBuffRemoval" }));
        Assert.That(sendCandidates, Does.Contain("Terraria.NPC." + prefix + "RequestBuffRemoval"));
        Assert.That(requestCallers, Does.Contain("Terraria.MessageBuffer.GetData"));
    }
}
