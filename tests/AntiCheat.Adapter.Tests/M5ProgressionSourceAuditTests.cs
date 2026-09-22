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
public sealed class M5ProgressionSourceAuditTests
{
    [Test]
    public void WholeTargetAssemblySummonAndResizeSenderInventoryMatchesReviewedRoutes()
    {
        using var file = File.OpenRead(typeof(NPC).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(file)), Is.EqualTo(TargetRuntime.OtApiSha256)); file.Position = 0;
        using var pe = new PEReader(file); var metadata = pe.GetMetadataReader();
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        var summonCallers = new HashSet<string>(); var summonWriters = new HashSet<string>();
        var resizeWriters = new HashSet<string>(); var chestSlotWriters = new HashSet<string>();
        var sigilWriters = new HashSet<string>(); var petCallers = new HashSet<string>();
        string TypeName(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace) + "." + metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace) + "." + metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
            _ => "unknown"
        };
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle); if (method.RelativeVirtualAddress == 0) continue;
            string name = TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name);
            byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            bool minus16 = false, minus8 = false, size155 = false, chest32 = false, sends = false;
            for (int offset = 0; offset < il.Length;)
            {
                ushort code = il[offset++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[offset++]);
                var op = codes[code];
                if (op == OpCodes.Ldc_R4 && BitConverter.ToSingle(il, offset) == -16) minus16 = true;
                if (op == OpCodes.Ldc_R4 && BitConverter.ToSingle(il, offset) == -8) minus8 = true;
                if (op == OpCodes.Ldc_I4 && BitConverter.ToInt32(il, offset) == 155) size155 = true;
                if (op == OpCodes.Ldc_I4_S && (sbyte)il[offset] == 32 || op == OpCodes.Ldc_I4 && BitConverter.ToInt32(il, offset) == 32) chest32 = true;
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    var target = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset));
                    string targetType = "", targetName = "";
                    if (target.Kind == HandleKind.MethodDefinition)
                    { var m = metadata.GetMethodDefinition((MethodDefinitionHandle)target); targetType = TypeName(m.GetDeclaringType()); targetName = metadata.GetString(m.Name); }
                    else if (target.Kind == HandleKind.MemberReference)
                    { var m = metadata.GetMemberReference((MemberReferenceHandle)target); targetType = TypeName(m.Parent); targetName = metadata.GetString(m.Name); }
                    if (targetType == "Terraria.NPC" && targetName == "SpawnMechQueen") summonCallers.Add(name);
                    if (targetType == "Terraria.NPC" && targetName == "UnlockOrExchangePet") petCallers.Add(name);
                    if (targetType == "Terraria.NetMessage" && targetName is "SendData" or "TrySendData" or "mfwh_SendData") sends = true;
                }
                offset += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset), _ => 4
                };
            }
            if (minus16 && sends) summonWriters.Add(name);
            if (minus8 && sends) sigilWriters.Add(name);
            if (size155 && sends) resizeWriters.Add(name);
            if (chest32 && sends) chestSlotWriters.Add(name);
        }
        TestContext.Out.WriteLine("SpawnMechQueen callers:\n" + string.Join("\n", summonCallers));
        TestContext.Out.WriteLine("Literal -16 float plus send candidates:\n" + string.Join("\n", summonWriters));
        TestContext.Out.WriteLine("Literal 155 plus send candidates:\n" + string.Join("\n", resizeWriters));
        TestContext.Out.WriteLine("Literal 32 plus send candidates:\n" + string.Join("\n", chestSlotWriters));
        TestContext.Out.WriteLine("M7 literal -8 float plus send candidates:\n" + string.Join("\n", sigilWriters));
        TestContext.Out.WriteLine("M7 variable61 pet helper callers:\n" + string.Join("\n", petCallers));
        Assert.That(sigilWriters, Is.EquivalentTo(new[] { "Terraria.MessageBuffer.GetData", "Terraria.NPC.mfwh_AI",
            "Terraria.NPC.mfwh_AI_078_MoonLordHands", "Terraria.NPC.mfwh_AI_015_KingSlime", "Terraria.NPC.mfwh_AI_121_QueenSlime",
            "Terraria.NPC.mfwh_AI_003_Fighters", "Terraria.NPC.mfwh_AI_107_ImprovedWalkers", "Terraria.Player.Update",
            "Terraria.Player.ItemCheck_UseEventItems", "Terraria.Projectile.mfwh_Shimmer", "Terraria.Projectile.mfwh_AI" }));
        Assert.That(petCallers, Is.EquivalentTo(new[] { "Terraria.MessageBuffer.GetData", "Terraria.Player.LicenseOrExchangePet" }));
        Assert.That(summonCallers, Is.EquivalentTo(new[] { "Terraria.Player.ItemCheck_UseBossSpawners", "Terraria.MessageBuffer.GetData", "Terraria.Main.mfwh_UpdateTime" }));
        Assert.That(summonWriters, Is.EquivalentTo(new[] { "Terraria.NPC.mfwh_SpawnMechQueen", "Terraria.NPC.mfwh_AI",
            "Terraria.NPC.mfwh_AI_003_Fighters", "Terraria.Player.Update", "Terraria.Projectile.mfwh_NewProjectile",
            "Terraria.Projectile.mfwh_Update", "Terraria.Projectile.mfwh_AI" }));
        Assert.That(resizeWriters, Is.EquivalentTo(new[] { "Terraria.NetMessage.mfwh_SendChestContentsTo",
            "Terraria.NPC.mfwh_HitEffect", "Terraria.Player.ItemCheck_OwnerOnlyCode", "Terraria.Player.ApplyNPCOnHitEffects",
            "Terraria.Projectile.mfwh_Update", "Terraria.Projectile.mfwh_HandleMovement", "Terraria.Projectile.mfwh_AI",
            "Terraria.Projectile.mfwh_Kill", "Terraria.Projectile.mfwh_Kill_ExplodeTiles" }));
        // Literal inventories are a bounded audit aid. The report reviews all additional candidates,
        // and the native producer tests separately exercise the branches and source exceptions.
    }
}
