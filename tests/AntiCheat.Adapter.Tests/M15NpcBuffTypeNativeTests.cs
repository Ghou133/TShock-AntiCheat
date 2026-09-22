using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.Utilities;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M15NpcBuffTypeNativeTests
{
    private const int ActorSlot = 21, NpcSlot = 3;
    private int oldMode, oldMyPlayer;
    private Player[] oldPlayers = null!;
    private NPC[] oldNpcs = null!;
    private bool oldRemix;
    private bool[] oldPvp = null!;
    private UnifiedRandom oldRandom = null!;
    private TSPlayer actor = null!;
    private readonly SessionKey session = new(Guid.NewGuid(), 1, ActorSlot, 1);
    private readonly List<M13NpcBuffObservation> requests = [];

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldPlayers = Main.player;
        oldNpcs = Main.npc; oldRandom = Main.rand; oldRemix = Main.remixWorld; oldPvp = Main.pvpBuff;
        Main.player = Enumerable.Range(0, 256).Select(i => new Player { whoAmI = i, active = true }).ToArray();
        Main.npc = Enumerable.Range(0, 200).Select(i => new NPC { whoAmI = i, active = true }).ToArray();
        Main.pvpBuff = Enumerable.Range(0, 401).Select(M14LBuffAddRules.IsNativePvpBuff).ToArray();
        Main.netMode = 1; Main.myPlayer = ActorSlot; Main.remixWorld = false;
        Main.rand = new UnifiedRandom(Enumerable.Range(0, 100).First(seed => new UnifiedRandom(seed).Next(3) == 0));
        actor = new TSPlayer(ActorSlot) { IsLoggedIn = true, Group = new Group("m15-b-native"),
            Account = new UserAccount { ID = 1521, Name = "m15-b-native" } };
        requests.Clear(); HookEvents.Terraria.NetMessage.SendData += Capture;
    }

    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Capture;
        Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.player = oldPlayers;
        Main.npc = oldNpcs; Main.rand = oldRandom; Main.remixWorld = oldRemix; Main.pvpBuff = oldPvp;
    }

    private void Capture(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (args.msgType == 53) requests.Add(new(M13NpcBuffOperation.Add, args.number, (int)args.number2,
            unchecked((short)(int)args.number3)));
        args.ContinueExecution = false;
    }

    [TestCase(172, 44)] [TestCase(1033, 362)] [TestCase(849, 310)] [TestCase(182, 375)]
    [TestCase(1117, 399)] [TestCase(1122, 400)] [TestCase(1127, 397)]
    public void NativeLegalUnusualAndNewProjectileSourcesProduceAllowedNpcCategories(int projectile, int buff)
    {
        new Projectile { owner = ActorSlot, type = projectile }.StatusNPC(NpcSlot);
        Assert.That(requests.Any(request => request.BuffType == buff), Is.True,
            "The actual target StatusNPC method must have executed this producer branch.");
        Main.netMode = 2; Main.myPlayer = 255;
        foreach (var request in requests)
        {
            var result = M15NpcBuffTypeAdapter.Evaluate(request, session, actor, TargetRuntime.Fingerprint, true);
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass));
            Assert.That(result.Facts["contextComplete"], Is.EqualTo("True"));
            Assert.That(Main.npc[NpcSlot].FindBuffIndex(request.BuffType), Is.GreaterThanOrEqualTo(0));
        }
    }

    [TestCase(false, 24)] [TestCase(true, 323)]
    public void NativeLegalDynamicProjectileTypeUsesBothWorldBranches(bool remix, int expected)
    {
        Main.remixWorld = remix;
        Main.rand = new UnifiedRandom(Enumerable.Range(0, 100).First(seed => new UnifiedRandom(seed).Next(2) == 0));
        new Projectile { owner = ActorSlot, type = 15 }.StatusNPC(NpcSlot);
        Assert.That(requests.Single().BuffType, Is.EqualTo(expected));
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(M15NpcBuffTypeAdapter.Evaluate(requests.Single(), session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void NativeLegalPassiveNpcReceiptUsesQuietAndCannotEchoOrImplicateRecipient()
    {
        byte[] body = [NpcSlot, 0, 5, 0, 60, 0];
        var buffer = new MessageBuffer { whoAmI = 256 };
        buffer.readBuffer[0] = 53; body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader();
        buffer.GetData(0, body.Length + 1, out _);
        Assert.That(Main.npc[NpcSlot].FindBuffIndex(5), Is.GreaterThanOrEqualTo(0));
        Assert.That(requests, Is.Empty, "Received arbitrary53 is quiet, so no original-client53 producer is added.");
    }

    [Test]
    public void NativeLegalLongTorchSlimeProjectionIsNotABuffCategoryViolation()
    {
        Main.npc[NpcSlot].AddBuff(24, 216000);
        Assert.That(requests.Single().Time, Is.EqualTo(19392));
        Main.netMode = 2; Main.myPlayer = 255;
        Assert.That(M15NpcBuffTypeAdapter.Evaluate(requests.Single(), session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Pass));
    }

    [Test]
    public void UnsupportedHostIsExplicitEvenWhenTheLegalBranchReturnsPass()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        foreach (bool host in new[] { false, true })
        {
            BusinessRuleResult[] results = [
                M14LBuffAddPacketReader.EvaluateNpc(new(M13NpcBuffOperation.Add, NpcSlot, 153, 600), session, actor, TargetRuntime.Fingerprint, host),
                M14LBuffAddPacketReader.Evaluate(new(22, 24, 180), session, actor, TargetRuntime.Fingerprint, host),
                M14RNpcShimmerAdapter.Evaluate(new(M13NpcBuffOperation.Add, NpcSlot, 353, 100), session, actor, TargetRuntime.Fingerprint, host),
                M15NpcBuffTypeAdapter.Evaluate(new(M13NpcBuffOperation.Add, NpcSlot, 24, 180), session, actor, TargetRuntime.Fingerprint, host)];
            foreach (var result in results)
            {
                Assert.That(result.Verdict, Is.EqualTo(Verdict.Pass));
                Assert.That(result.Facts["nativeHostContractComplete"], Is.EqualTo(host.ToString()));
                Assert.That(result.Facts["contextComplete"], Is.EqualTo(host.ToString()));
                Assert.That(result.Facts["currentAccountAndActorBound"], Is.EqualTo("True"));
            }
        }
        Assert.That(M15NpcBuffTypeAdapter.Evaluate(new(M13NpcBuffOperation.Add, NpcSlot, 5, 60),
            session, actor, TargetRuntime.Fingerprint, false).Verdict, Is.EqualTo(Verdict.Unknown));
        actor.IsLoggedIn = false;
        var noIdentity = M15NpcBuffTypeAdapter.Evaluate(new(M13NpcBuffOperation.Add, NpcSlot, 5, 60),
            session, actor, TargetRuntime.Fingerprint, true);
        Assert.That(noIdentity.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(noIdentity.Facts["contextComplete"], Is.EqualTo("False"));
        Assert.That(noIdentity.Facts["currentAccountAndActorBound"], Is.EqualTo("False"));
    }

    [Test]
    public void ScopedNpcBuffPermissionDoesNotAuthorizePlayerBuffSpoofing()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var packet = new M13NpcBuffObservation(M13NpcBuffOperation.Add, NpcSlot, 5, 60);
        Assert.That(M15NpcBuffTypeAdapter.Evaluate(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.ProvenCheat));
        actor.Group.AddPermission(Permissions.ignorenpcbuffdetection);
        Assert.That(M15NpcBuffTypeAdapter.Evaluate(packet, session, actor, TargetRuntime.Fingerprint, true).Verdict,
            Is.EqualTo(Verdict.Pass));
        Assert.That(M14LBuffAddPacketReader.Evaluate(new(ActorSlot, 24, 180), session, actor,
            TargetRuntime.Fingerprint, true).Verdict, Is.EqualTo(Verdict.ProvenCheat));
    }

    [Test]
    public void ExistingRaw53ReaderKeepsCompleteShapeAndOriginalCancellation()
    {
        foreach (int length in new[] { 0, 1, 5, 7, 128 })
            Assert.That(M13NpcBuffPacketReader.ReadPayload(53, new byte[length]).Kind, Is.EqualTo(PacketReadKind.Malformed));
        var args = M2ContractsTests.Packet((PacketTypes)53, [3, 0, 5, 0, 60, 0], ActorSlot); args.Handled = true;
        Assert.That(M13NpcBuffPacketReader.Read(args, false).Kind, Is.EqualTo(PacketReadKind.UnknownRuntime));
        var parsed = M13NpcBuffPacketReader.Read(args, true);
        Assert.That(parsed.Kind, Is.EqualTo(PacketReadKind.Parsed)); Assert.That(args.Handled, Is.True);
        Assert.That(parsed.Packet, Is.EqualTo(new M13NpcBuffObservation(M13NpcBuffOperation.Add, 3, 5, 60)));
    }
}
