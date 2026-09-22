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
public sealed class M5VitalsSourceTests
{
    [Test]
    public void LockedTargetHurtCallerInventoryMatchesAllReviewedRoles()
    {
        using var image = File.OpenRead(typeof(Player).Assembly.Location);
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
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        var callers = new HashSet<string>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            for (int cursor = 0; cursor < bytes.Length;)
            {
                ushort value = bytes[cursor++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[cursor++]);
                var opcode = opcodes[value];
                int size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, cursor),
                    _ => 4
                };
                if (opcode.OperandType == OperandType.InlineMethod)
                {
                    var targetHandle = MetadataTokens.EntityHandle(BitConverter.ToInt32(bytes, cursor));
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
                    if (targetType == "Terraria.NetMessage" && targetName is "SendPlayerHurt" or "mfwh_SendPlayerHurt")
                        callers.Add(TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name));
                }
                cursor += size;
            }
        }
        TestContext.Out.WriteLine("All direct target SendPlayerHurt/mfwh caller methods:\n" + string.Join("\n", callers.Order()));
        Assert.That(callers, Is.EquivalentTo(new[] {
            "Terraria.NetMessage.SendPlayerHurt", "Terraria.MessageBuffer.GetData",
            "Terraria.Player.UpdateBuffs", "Terraria.Player.Hurt", "Terraria.Player.ItemCheck_MeleeHitPVP",
            "Terraria.Projectile.mfwh_Damage_PVP" }));
    }
}
