using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;

namespace AntiCheat.Adapter.Tests;

/// <summary>Read-only IL audit of the hash-locked assembly; does not run third-party assembly code.</summary>
[TestFixture]
public sealed class M4CombatSourceAuditTests
{
    [TestCase(490)]
    [TestCase(602)]
    public void TargetLiteralCreationCandidateInventoryMatchesReviewedMethods(int projectileType)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        var candidates = new List<string>();
        var literalMethods = new List<string>();
        using var image = File.OpenRead(typeof(Projectile).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(image)), Is.EqualTo(TargetRuntime.OtApiSha256));
        image.Position = 0;
        using var pe = new PEReader(image);
        var metadata = pe.GetMetadataReader();
        string TypeName(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace) + "." +
                metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace) + "." +
                metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
            _ => "unresolved"
        };
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            bool hasRitualConstant = false, callsCreation = false;
            for (int i = 0; i < bytes.Length;)
            {
                ushort value = bytes[i++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[i++]);
                var opcode = opcodes[value];
                int size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                    _ => 4
                };
                if (opcode == OpCodes.Ldc_I4 && BitConverter.ToInt32(bytes, i) == projectileType) hasRitualConstant = true;
                if (opcode.OperandType == OperandType.InlineMethod)
                {
                    var targetHandle = MetadataTokens.EntityHandle(BitConverter.ToInt32(bytes, i));
                    if (targetHandle.Kind == HandleKind.MethodSpecification)
                        targetHandle = metadata.GetMethodSpecification((MethodSpecificationHandle)targetHandle).Method;
                    string targetName = "", targetType = "";
                    if (targetHandle.Kind == HandleKind.MethodDefinition)
                    {
                        var target = metadata.GetMethodDefinition((MethodDefinitionHandle)targetHandle);
                        targetName = metadata.GetString(target.Name); targetType = TypeName(target.GetDeclaringType());
                    }
                    else if (targetHandle.Kind == HandleKind.MemberReference)
                    {
                        var target = metadata.GetMemberReference((MemberReferenceHandle)targetHandle);
                        targetName = metadata.GetString(target.Name); targetType = TypeName(target.Parent);
                    }
                    if (targetType == "Terraria.Projectile" && targetName is "NewProjectile" or "mfwh_NewProjectile") callsCreation = true;
                }
                i += size;
            }
            string name = TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name);
            if (hasRitualConstant) literalMethods.Add(name);
            if (hasRitualConstant && callsCreation) candidates.Add(name);
        }
        TestContext.Out.WriteLine("Target OTAPI: " + TargetRuntime.OtApiSha256);
        TestContext.Out.WriteLine("Methods mentioning integer " + projectileType + " (not all references mean projectile type):\n" + string.Join("\n", literalMethods));
        TestContext.Out.WriteLine("Literal " + projectileType + " + NewProjectile candidates:\n" + string.Join("\n", candidates));
        string[] reviewed = projectileType == 490
            ? ["Terraria.NPC.mfwh_AI", "Terraria.NPC.mfwh_AI_084_LunaticCultist", "Terraria.NPC.mfwh_HitEffect",
                "Terraria.Wiring.mfwh_HitWireSingle", "Terraria.WorldGen.mfwh_Check2x2"]
            : ["Terraria.NPC.mfwh_AI_007_TownEntities", "Terraria.NPC.mfwh_HitEffect",
                "Terraria.Wiring.mfwh_HitWireSingle", "Terraria.GameContent.PortalHelper.AddPortal"];
        Assert.That(candidates, Is.EquivalentTo(reviewed),
            "A new candidate requires source review; this assertion alone does not prove arbitrary dynamic creation routes.");
    }

    [Test]
    public void ActualPortalHelperConstructorOverwritesParentDamageWithLiteralZero()
    {
        // This is the target binary's producer, not the AntiCheat rule's own implementation.
        var method = typeof(Terraria.GameContent.PortalHelper).GetMethod("AddPortal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        byte[] expectedArgumentSequence = [(byte)OpCodes.Ldc_I4.Value, 0x5a, 0x02, 0, 0,
            (byte)OpCodes.Ldc_I4_0.Value, (byte)OpCodes.Ldc_R4.Value, 0, 0, 0, 0];
        int count = 0;
        for (int i = 0; i <= il.Length - expectedArgumentSequence.Length; i++)
            if (il.AsSpan(i, expectedArgumentSequence.Length).SequenceEqual(expectedArgumentSequence)) count++;
        Assert.That(count, Is.EqualTo(1), "602 must be followed by explicit damage=0 and knockback=0 in the reviewed AddPortal call.");
    }
}
