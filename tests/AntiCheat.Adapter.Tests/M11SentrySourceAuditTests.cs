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

public sealed class M11SentrySourceAuditTests
{
    [Test]
    public void M11_HashLockedEntireAssemblyTurretCapacityWritersMatchReviewedBound()
    {
        using var stream = File.OpenRead(typeof(Player).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(stream)), Is.EqualTo(TargetRuntime.OtApiSha256)); stream.Position = 0;
        using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => unchecked((ushort)opcode.Value));
        string Name(TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            return parent.IsNil ? metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)
                : Name(parent) + "+" + metadata.GetString(type.Name);
        }
        var writers = new HashSet<string>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle); if (method.RelativeVirtualAddress == 0) continue;
            var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            for (int index = 0; index < bytes.Length;)
            {
                ushort value = bytes[index++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[index++]);
                var opcode = opcodes[value];
                int size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, index), _ => 4
                };
                if (opcode == OpCodes.Stfld || opcode == OpCodes.Ldflda)
                {
                    var token = MetadataTokens.EntityHandle(BitConverter.ToInt32(bytes, index)); bool matches = false;
                    if (token.Kind == HandleKind.FieldDefinition)
                    {
                        var field = metadata.GetFieldDefinition((FieldDefinitionHandle)token);
                        matches = Name(field.GetDeclaringType()) == "Terraria.Player" && metadata.GetString(field.Name) == "maxTurrets";
                    }
                    else if (token.Kind == HandleKind.MemberReference)
                    {
                        var field = metadata.GetMemberReference((MemberReferenceHandle)token);
                        string parent = field.Parent.Kind switch
                        {
                            HandleKind.TypeDefinition => Name((TypeDefinitionHandle)field.Parent),
                            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)field.Parent).Namespace) + "." +
                                metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)field.Parent).Name), _ => ""
                        };
                        matches = parent == "Terraria.Player" && metadata.GetString(field.Name) == "maxTurrets";
                    }
                    if (matches) writers.Add(Name(method.GetDeclaringType()) + "." + metadata.GetString(method.Name));
                }
                index += size;
            }
        }
        TestContext.Out.WriteLine("Hash-locked direct/byref maxTurrets writers:\n" + string.Join('\n', writers.Order()));
        var expected = new[] { "Terraria.Player..ctor", "Terraria.Player.ResetEffects", "Terraria.Player.UpdateBuffs",
            "Terraria.Player.UpdateEquips", "Terraria.Player.GrantArmorBenefits" }
            .Concat(new[] { "SquireTier2", "ApprenticeTier2", "HuntressTier2", "MonkTier2", "SquireTier3", "ApprenticeTier3", "HuntressTier3", "MonkTier3" }
                .Select(name => "Terraria.DataStructures.ArmorSetBonuses+Benefits." + name));
        Assert.That(writers, Is.EquivalentTo(expected), "A new write or byref path invalidates the whole-native fallback source inventory.");
        Assert.That(Player.maxBuffs, Is.EqualTo(44));
    }
}
