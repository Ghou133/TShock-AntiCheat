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

[TestFixture]
public sealed class M5CombatSourceAuditTests
{
    [Test]
    public void HashLockedDamageWriteInventoryIncludesAllNativeDirectAndByrefWriters()
    {
        using var stream = File.OpenRead(typeof(Projectile).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(stream)), Is.EqualTo(TargetRuntime.OtApiSha256));
        stream.Position = 0;
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        string TypeName(TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        }
        var writers = new List<string>();
        foreach (var methodHandle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            bool writes = false;
            for (int position = 0; position < bytes.Length;)
            {
                ushort value = bytes[position++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[position++]);
                var opcode = opcodes[value];
                int size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, position),
                    _ => 4
                };
                if (opcode == OpCodes.Stfld || opcode == OpCodes.Ldflda)
                {
                    var fieldHandle = MetadataTokens.EntityHandle(BitConverter.ToInt32(bytes, position));
                    if (fieldHandle.Kind == HandleKind.FieldDefinition)
                    {
                        var field = metadata.GetFieldDefinition((FieldDefinitionHandle)fieldHandle);
                        if (TypeName(field.GetDeclaringType()) == "Terraria.Projectile" && metadata.GetString(field.Name) == "damage")
                            writes = true;
                    }
                    else if (fieldHandle.Kind == HandleKind.MemberReference)
                    {
                        var field = metadata.GetMemberReference((MemberReferenceHandle)fieldHandle);
                        string parent = field.Parent.Kind switch
                        {
                            HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)field.Parent),
                            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)field.Parent).Namespace) + "." +
                                metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)field.Parent).Name),
                            _ => ""
                        };
                        if (parent == "Terraria.Projectile" && metadata.GetString(field.Name) == "damage") writes = true;
                    }
                }
                position += size;
            }
            if (writes) writers.Add(TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name));
        }
        Assert.That(writers, Does.Contain("Terraria.MessageBuffer.GetData"));
        Assert.That(writers, Does.Contain("Terraria.NPC.mfwh_ReflectProjectile"));
        Assert.That(writers, Does.Contain("Terraria.Projectile.mfwh_AI_001"));
        TestContext.Out.WriteLine("Direct/byref Projectile.damage native writers (field stores are candidates, guarded branch review remains necessary):\n" + string.Join("\n", writers));
    }
}
