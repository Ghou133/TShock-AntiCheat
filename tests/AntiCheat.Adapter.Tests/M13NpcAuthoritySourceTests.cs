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
public sealed class M13NpcAuthoritySourceTests
{
    [Test]
    public void WholeLockedAssemblyPacket61CandidatesAndDynamicPetCallersRemainAuditable()
    {
        using var file = File.OpenRead(typeof(NPC).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(file)), Is.EqualTo(TargetRuntime.OtApiSha256)); file.Position = 0;
        using var pe = new PEReader(file); var metadata = pe.GetMetadataReader();
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        var candidates = new SortedSet<string>(); var petCallers = new SortedSet<string>();
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
            bool literal61 = false, sends = false;
            for (int offset = 0; offset < il.Length;)
            {
                ushort code = il[offset++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[offset++]);
                var op = codes[code];
                if (op == OpCodes.Ldc_I4_S && (sbyte)il[offset] == 61 || op == OpCodes.Ldc_I4 && BitConverter.ToInt32(il, offset) == 61)
                    literal61 = true;
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    var target = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset));
                    string targetType = "", targetName = "";
                    if (target.Kind == HandleKind.MethodDefinition)
                    { var m = metadata.GetMethodDefinition((MethodDefinitionHandle)target); targetType = TypeName(m.GetDeclaringType()); targetName = metadata.GetString(m.Name); }
                    else if (target.Kind == HandleKind.MemberReference)
                    { var m = metadata.GetMemberReference((MemberReferenceHandle)target); targetType = TypeName(m.Parent); targetName = metadata.GetString(m.Name); }
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
            if (literal61 && sends) candidates.Add(name);
        }
        TestContext.Out.WriteLine("Whole-assembly literal61 plus send candidates (manual argument audit required):\n" + string.Join("\n", candidates));
        Assert.That(petCallers, Is.EquivalentTo(new[] { "Terraria.MessageBuffer.GetData", "Terraria.Player.LicenseOrExchangePet" }));
        Assert.That(candidates, Is.EquivalentTo(new[]
        {
            "Terraria.Main.mfwh_StartSlimeRain", "Terraria.NPC.mfwh_AI", "Terraria.NPC.mfwh_AI_107_ImprovedWalkers",
            "Terraria.NPC.mfwh_HitEffect", "Terraria.NPC.mfwh_SpawnMechQueen", "Terraria.NPC.mfwh_UnlockOrExchangePet",
            "Terraria.Player.ItemCheck_CheckFishingBobber_PullBobber", "Terraria.Player.ItemCheck_CutTiles",
            "Terraria.Player.ItemCheck_Shoot", "Terraria.Player.ItemCheck_UseBossSpawners", "Terraria.Player.ItemCheck_UseCombatBook",
            "Terraria.Player.ItemCheck_UseEventItems", "Terraria.Player.ItemCheck_UsePeddlersSatchel", "Terraria.Player.NinjaDodge",
            "Terraria.Player.StickyMovement", "Terraria.Player.TileInteractionsUse", "Terraria.Player.Update",
            "Terraria.Projectile.mfwh_AI", "Terraria.Projectile.mfwh_HandleMovement", "Terraria.Projectile.mfwh_Kill",
            "Terraria.Projectile.mfwh_Kill_Bombs_DoUsualKillCode", "Terraria.WorldGen.mfwh_Convert",
            "Terraria.WorldGen.mfwh_UpdateWorld_OvergroundTile", "Terraria.WorldGen.mfwh_UpdateWorld_UndergroundTile"
        }));
        // Literal presence is an audit aid, never a proof that every send in a method has message61.
    }
}
