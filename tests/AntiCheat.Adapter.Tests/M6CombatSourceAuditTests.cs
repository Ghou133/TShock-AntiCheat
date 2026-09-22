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
public sealed class M6CombatSourceAuditTests
{
    [Test]
    public void LockedCompleteAssemblyEnumeratesCreationCallersAndArrowMultiplierWriters()
    {
        using var image = File.OpenRead(typeof(Projectile).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(image)), Is.EqualTo(TargetRuntime.OtApiSha256)); image.Position = 0;
        using var pe = new PEReader(image); var metadata = pe.GetMetadataReader();
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        string TypeName(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeDefinition)
            {
                var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            }
            if (handle.Kind == HandleKind.TypeReference)
            {
                var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
                return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            }
            return "unresolved";
        }
        (string Type, string Name) Member(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.MemberReference)
            {
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                return (TypeName(member.Parent), metadata.GetString(member.Name));
            }
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var member = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (TypeName(member.GetDeclaringType()), metadata.GetString(member.Name));
            }
            if (handle.Kind == HandleKind.FieldDefinition)
            {
                var member = metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
                return (TypeName(member.GetDeclaringType()), metadata.GetString(member.Name));
            }
            return ("unresolved", "unresolved");
        }
        string[] fields = ["rangedDamage", "rangedMultDamage", "arrowDamage", "arrowDamageAdditiveStack"];
        var writers = new SortedSet<string>(); var callers = new SortedSet<string>(); var itemWriters = new SortedSet<string>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle); if (method.RelativeVirtualAddress == 0) continue;
            string caller = TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name);
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            for (int i = 0; i < il.Length;)
            {
                ushort value = il[i++]; if (value == 0xfe) value = (ushort)(0xfe00 | il[i++]);
                var opcode = opcodes[value];
                if (opcode == OpCodes.Stfld || opcode == OpCodes.Ldflda || opcode == OpCodes.Call || opcode == OpCodes.Callvirt)
                {
                    var target = Member(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i)));
                    if (target.Type == "Terraria.Player" && fields.Contains(target.Name) &&
                        (opcode == OpCodes.Stfld || opcode == OpCodes.Ldflda)) writers.Add(caller + " -> " + target.Name);
                    if (target.Type == "Terraria.Projectile" && target.Name == "NewProjectile") callers.Add(caller);
                    if (target.Type == "Terraria.Item" && target.Name == "damage" &&
                        (opcode == OpCodes.Stfld || opcode == OpCodes.Ldflda)) itemWriters.Add(caller);
                }
                i += opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i), _ => 4
                };
            }
        }
        Assert.That(callers, Does.Contain("Terraria.Player.ItemCheck_Shoot"));
        Assert.That(writers, Does.Contain("Terraria.Player.GrantArmorBenefits -> rangedDamage"));
        TestContext.Out.WriteLine("All target creation callers (each remains subject to type/branch audit):\n" + string.Join("\n", callers));
        TestContext.Out.WriteLine("All direct/byref arrow multiplier writers:\n" + string.Join("\n", writers));
        TestContext.Out.WriteLine("All direct/byref native Item.damage writers:\n" + string.Join("\n", itemWriters));
    }
}
