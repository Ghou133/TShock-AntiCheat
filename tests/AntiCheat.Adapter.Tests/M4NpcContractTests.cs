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
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

/// <summary>Actual locked vanilla producer calls in an isolated in-process scene; no GUI or TCP claim.</summary>
[TestFixture, NonParallelizable]
public sealed class M4NpcContractTests
{
    private const int Slot = 7, NpcSlot = 3;
    private NPC[] oldNpcs = null!;
    private Player[] oldPlayers = null!;
    private Projectile[] oldProjectiles = null!;
    private Chest[] oldChests = null!;
    private CombatText[] oldCombatText = null!;
    private int[,] oldPortals = null!;
    private int[] oldPlayerCooldowns = null!, oldNpcCooldowns = null!;
    private bool oldAnyPortal, oldDedicated;
    private int oldMode, oldMyPlayer, oldWidth, oldHeight;
    private readonly List<(int X, int Y, ITile? Value)> oldTiles = [];
    private readonly List<(int Id, int Target, float Value, int Remote, int Ignore)> outgoing = [];
    private SessionKey session;

    [SetUp]
    public void SetUp()
    {
        oldNpcs = Main.npc; oldPlayers = Main.player; oldProjectiles = Main.projectile; oldChests = Main.chest;
        oldCombatText = Main.combatText;
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldDedicated = Main.dedServ;
        oldWidth = Main.maxTilesX; oldHeight = Main.maxTilesY;
        oldPortals = PortalHelper.FoundPortals; oldPlayerCooldowns = PortalHelper.PortalCooldownForPlayers;
        oldNpcCooldowns = PortalHelper.PortalCooldownForNPCs; oldAnyPortal = PortalHelper.anyPortalAtAll;
        Main.netMode = 2; Main.myPlayer = 255; Main.dedServ = true; Main.maxTilesX = 500; Main.maxTilesY = 500;
        Main.npc = Enumerable.Range(0, Main.maxNPCs).Select(i => new NPC { whoAmI = i, realLife = -1 }).ToArray();
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i }).ToArray();
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Main.chest = new Chest[8000];
        // A full cosmetic text pool is a legal state and avoids requiring GPU font assets in this headless scene.
        Main.combatText = Enumerable.Range(0, 100).Select(_ => new CombatText { active = true }).ToArray();
        PortalHelper.FoundPortals = new int[256, 2];
        PortalHelper.PortalCooldownForPlayers = new int[256]; PortalHelper.PortalCooldownForNPCs = new int[Main.maxNPCs];
        session = new(Guid.NewGuid(), 1, Slot, 1);
        outgoing.Clear(); oldTiles.Clear();
        HookEvents.Terraria.NetMessage.SendData += CaptureSend;
        HookEvents.Terraria.NetMessage.TrySendData += CaptureTrySend;
        HookEvents.Terraria.Main.TeleportEffect += SuppressCosmeticTeleportEffect;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= CaptureSend;
        HookEvents.Terraria.NetMessage.TrySendData -= CaptureTrySend;
        HookEvents.Terraria.Main.TeleportEffect -= SuppressCosmeticTeleportEffect;
        foreach (var tile in oldTiles) Main.tile[tile.X, tile.Y] = tile.Value!;
        Main.npc = oldNpcs; Main.player = oldPlayers; Main.projectile = oldProjectiles; Main.chest = oldChests;
        Main.combatText = oldCombatText;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated;
        Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
        PortalHelper.FoundPortals = oldPortals; PortalHelper.PortalCooldownForPlayers = oldPlayerCooldowns;
        PortalHelper.PortalCooldownForNPCs = oldNpcCooldowns; PortalHelper.anyPortalAtAll = oldAnyPortal;
    }

    // Actual target producer invocations are captured, then the fixture transport is stopped.
    private void CaptureSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        outgoing.Add((args.msgType, args.number, args.number2, args.remoteClient, args.ignoreClient));
        args.ContinueExecution = false;
    }
    private void CaptureTrySend(object? sender, HookEvents.Terraria.NetMessage.TrySendDataEventArgs args)
    {
        outgoing.Add((args.msgType, args.number, args.number2, args.remoteClient, args.ignoreClient));
        args.ContinueExecution = false; args.HookReturnValue = true;
    }
    private static void SuppressCosmeticTeleportEffect(object? sender, HookEvents.Terraria.Main.TeleportEffectEventArgs args)
        => args.ContinueExecution = false;

    private RuleInputContext Input(bool clientOrigin) => new(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint,
        true, true, true, true, clientOrigin);

    [TestCase(0, 450, 0)]
    [TestCase(1, 500, 0)]
    [TestCase(2, 450, 1)]
    public void RealEelWhipDotDamagesActiveNpcOnlyOnAuthorityAndEmits153OnlyOnServer(int mode, int life, int sends)
    {
        Main.netMode = mode;
        var npc = Main.npc[NpcSlot]; npc.active = true; npc.type = NPCID.BlueSlime;
        npc.life = npc.lifeMax = 500; npc.width = npc.height = 32; npc.position = new(320, 320);
        npc.electricEelCounter = 58;
        npc.ApplyEelWhipDoT();
        Assert.That(npc.life, Is.EqualTo(500)); Assert.That(outgoing, Is.Empty);
        npc.ApplyEelWhipDoT();
        Assert.That(npc.active, Is.True); Assert.That(npc.life, Is.EqualTo(life));
        Assert.That(outgoing.Count(x => x.Id == 153), Is.EqualTo(sends));
        if (sends == 0) return;
        var actual = outgoing.Single(x => x.Id == 153);
        Assert.That(actual.Target, Is.EqualTo(NpcSlot)); Assert.That(actual.Value, Is.EqualTo(50));
        var observation = new NpcServerAuthorityObservation(NpcServerAuthorityMessage.DebuffDamage, actual.Target, (int)actual.Value);
        Assert.That(M3CheatAttemptRules.Evaluate(observation, Input(false), Main.maxNPCs, M3CheatAttemptRules.ContractVersion).Verdict,
            Is.EqualTo(Verdict.Pass));
        Assert.That(M3CheatPacketReader.Evaluate(observation, session, TargetRuntime.Fingerprint).Verdict,
            Is.EqualTo(Verdict.ProvenCheat), "The same fields from a client are a role violation, not a magnitude heuristic.");
    }

    [TestCase(false, 0, 0)]
    [TestCase(false, 1, 96)]
    [TestCase(false, 2, 0)]
    [TestCase(true, 0, 0)]
    [TestCase(true, 1, 0)]
    [TestCase(true, 2, 100)]
    public void ActualPortalTraversalDistinguishesPlayer96FromServerNpc100(bool npcEntity, int mode, int expectedMessage)
    {
        for (int x = 12; x < 48; x++)
        for (int y = 12; y < 30; y++)
        {
            oldTiles.Add((x, y, Main.tile[x, y])); Main.tile[x, y] = new Tile();
        }
        Main.netMode = mode; Main.myPlayer = mode == 2 ? 255 : Slot;
        for (int form = 0; form < 2; form++)
        {
            var portal = Main.projectile[form]; portal.SetDefaults(602); portal.owner = Slot; portal.active = true;
            portal.Center = new(320 + form * 320, 320); portal.ai[0] = 0; portal.ai[1] = form;
        }
        PortalHelper.UpdatePortalPoints(); Assert.That(PortalHelper.anyPortalAtAll, Is.True);
        Entity target;
        if (npcEntity)
        {
            var npc = Main.npc[NpcSlot]; npc.type = NPCID.BlueSlime; npc.active = true; npc.life = npc.lifeMax = 500;
            target = npc;
        }
        else
        {
            Main.player[Slot].active = true; target = Main.player[Slot];
        }
        target.width = 20; target.height = 40; target.position = new(310, 300); target.velocity = new(0, 2);
        PortalHelper.TryGoingThroughPortals(target);
        Assert.That(target.position.X, Is.GreaterThan(600), "The real traversal must reach the portal branch, not merely remain unpunished.");
        Assert.That(outgoing.Where(x => x.Id is 96 or 100).Select(x => x.Id),
            Is.EqualTo(expectedMessage == 0 ? Array.Empty<int>() : new[] { expectedMessage }));
        if (expectedMessage == 96)
            Assert.That(M3CheatPacketReader.ReadPayload(96, new byte[20]).Kind, Is.EqualTo(PacketReadKind.Unrelated));
        if (expectedMessage == 100)
        {
            var observation = new NpcServerAuthorityObservation(NpcServerAuthorityMessage.PortalTeleport, NpcSlot,
                X: target.position.X, Y: target.position.Y, VelocityX: target.velocity.X, VelocityY: target.velocity.Y);
            Assert.That(M3CheatAttemptRules.Evaluate(observation, Input(false), Main.maxNPCs, M3CheatAttemptRules.ContractVersion).Verdict,
                Is.EqualTo(Verdict.Pass));
            Assert.That(M3CheatPacketReader.Evaluate(observation, session, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        }
    }

    [Test]
    public void RealChestResizeAndMultiChestContentsExportAreServerActionsIndependentOfClientResizePermission()
    {
        var actor = new TSPlayer(Slot) { IsLoggedIn = true, Group = new Group("m4-normal-no-resize"),
            Account = new UserAccount { ID = 77, Name = "m4-npc-boundary" } };
        Assert.That(actor.HasPermission(Permissions.resizechests), Is.False);
        for (int index = 0; index < 2; index++)
        {
            var chest = new Chest(index, 20 + index * 3, 20); chest.FillWithEmptyInstances(); Main.chest[index] = chest;
            chest.Resize(40 + index); // Actual vanilla data operation, not a claimed GUI action.
            Assert.That(outgoing, Is.Empty, "Resize itself does not turn a normal player action into packet155.");
        }
        NetMessage.SendChestContentsTo(0, Slot); NetMessage.SendChestContentsTo(1, Slot);
        var sizes = outgoing.Where(x => x.Id == 155).ToArray();
        Assert.That(sizes.Select(x => (x.Target, x.Value)), Is.EqualTo(new[] { (0, 40f), (1, 41f) }));
        Assert.That(sizes.All(x => x.Remote == Slot), Is.True);
        Assert.That(outgoing.Count(x => x.Id == 32), Is.EqualTo(81));
        foreach (var actual in sizes)
        {
            var observation = new ChestResizeObservation(actual.Target, (int)actual.Value);
            Assert.That(M3CheatAttemptRules.EvaluateChestResize(observation, Input(false), Main.maxChests, true, false,
                M3CheatAttemptRules.ContractVersion).Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(M3ChestSizePacketReader.Evaluate(observation, session, actor, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        }
        actor.Group.AddPermission(Permissions.resizechests);
        Assert.That(M3ChestSizePacketReader.Evaluate(new(1, 41), session, actor, TargetRuntime.Fingerprint).Verdict, Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void LockedChestSizeNativeProducerCallersRemainOnlyJoinSectionAndServerOpenPath()
    {
        using var image = File.OpenRead(typeof(Projectile).Assembly.Location);
        Assert.That(Convert.ToHexString(SHA256.HashData(image)), Is.EqualTo(TargetRuntime.OtApiSha256)); image.Position = 0;
        using var pe = new PEReader(image); var metadata = pe.GetMetadataReader();
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        var callers = new List<string>();
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
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle); if (method.RelativeVirtualAddress == 0) continue;
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            for (int i = 0; i < il.Length;)
            {
                ushort code = il[i++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
                var opcode = opcodes[code];
                if (opcode.OperandType == OperandType.InlineMethod)
                {
                    var target = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        var called = metadata.GetMethodDefinition((MethodDefinitionHandle)target);
                        if (TypeName(called.GetDeclaringType()) == "Terraria.NetMessage" && metadata.GetString(called.Name) == "SendChestContentsTo")
                            callers.Add(TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name));
                    }
                    else if (target.Kind == HandleKind.MemberReference)
                    {
                        var called = metadata.GetMemberReference((MemberReferenceHandle)target);
                        if (TypeName(called.Parent) == "Terraria.NetMessage" && metadata.GetString(called.Name) == "SendChestContentsTo")
                            callers.Add(TypeName(method.GetDeclaringType()) + "." + metadata.GetString(method.Name));
                    }
                }
                i += opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i), _ => 4
                };
            }
        }
        TestContext.Out.WriteLine("Actual SendChestContentsTo callsites:\n" + string.Join("\n", callers));
        Assert.That(callers, Is.EquivalentTo(new[] { "Terraria.MessageBuffer.GetData", "Terraria.NetMessage.mfwh_SyncChestContentsForSection" }),
            "These exact callers still need their audited netMode guards; this is not an arbitrary permission-denial heuristic.");
    }
}
