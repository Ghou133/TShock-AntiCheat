using System.Buffers.Binary;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    private static byte[] M15PublicationBody(int id)
    {
        if (id == 140) { var credits = new byte[5]; BinaryPrimitives.WriteInt32LittleEndian(credits.AsSpan(1), 28800); return credits; }
        var shot = new byte[15]; BinaryPrimitives.WriteInt16LittleEndian(shot, 50);
        BinaryPrimitives.WriteSingleLittleEndian(shot.AsSpan(2), 3);
        BinaryPrimitives.WriteInt16LittleEndian(shot.AsSpan(6), 20);
        BinaryPrimitives.WriteInt16LittleEndian(shot.AsSpan(8), 30);
        BinaryPrimitives.WriteInt16LittleEndian(shot.AsSpan(10), 4);
        BinaryPrimitives.WriteInt16LittleEndian(shot.AsSpan(12), 4); shot[14] = 22; return shot;
    }

    [TestCase(140, false)] [TestCase(140, true)] [TestCase(108, false)] [TestCase(108, true)]
    public async Task M15ProtocolProjectile_FirstRoleProofCancelsAndOnlyRevokesRealSender(int id, bool earlierCancelled)
    {
        var body = M15PublicationBody(id);
        var innocent = engine.OpenSession(22)!.Value; engine.Authenticate(innocent, 1552);
        int later = 0;
        void Observe(GetDataEventArgs args) { if (!args.Handled) later++; }
        ServerApi.Hooks.NetGetData.Register(plugin, Observe, 0);
        try
        {
            var args = M2ContractsTests.Packet((PacketTypes)id, body, Slot); args.Handled = earlierCancelled;
            ServerApi.Hooks.NetGetData.Invoke(args);
            Assert.That(args.Handled, Is.True); Assert.That(later, Is.Zero); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.CanWrite(innocent), Is.True);
            Assert.That(engine.SanctionCount, Is.EqualTo(1));
            var followup = M2ContractsTests.Packet((PacketTypes)5, new byte[8], Slot);
            ServerApi.Hooks.NetGetData.Invoke(followup);
            Assert.That(followup.Handled, Is.True); Assert.That(later, Is.Zero); Assert.That(sent, Is.Empty);
            BusinessRuleResult result = id == 140
                ? M15ProtocolPacketReader.Evaluate(M15ProtocolPacketReader.ReadPayload(id, body).Packet!, session,
                    ServerTShock.Players[Slot], TargetRuntime.Fingerprint, true)
                : M15ProjectilePacketReader.Evaluate(M15ProjectilePacketReader.ReadPayload(id, body).Packet!, session,
                    ServerTShock.Players[Slot], TargetRuntime.Fingerprint, true);
            var existing = engine.ObserveBusiness(new(session, (byte)id, result,
                new(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
            Assert.That(existing.Incident!.AccountId, Is.EqualTo(6207));
            Assert.That(existing.Incident.Evidence.RuleId, Is.EqualTo(id == 140 ? M15ProtocolRules.RuleId : M15ProjectileRules.RuleId));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1)); Assert.That(store.Bans, Is.EqualTo(1));
            var reconnect = engine.OpenSession(Slot)!.Value;
            Assert.That(engine.Authenticate(reconnect, 6207), Is.EqualTo(AuthenticationResult.AccountBlocked));
            Assert.That(engine.CanWrite(innocent), Is.True);
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, Observe); }
    }

    [TestCase(140)] [TestCase(108)]
    public void M15ProtocolProjectile_MalformedOnlyBlocksAndSameSessionKeepsLegalRequests(int id)
    {
        int length = id == 140 ? 5 : 15;
        foreach (int wrong in new[] { 0, length - 1, length + 1 })
        {
            var args = M2ContractsTests.Packet((PacketTypes)id, new byte[wrong], Slot);
            ServerApi.Hooks.NetGetData.Invoke(args); Assert.That(args.Handled, Is.True);
        }
        if (id == 108)
        {
            var nonfinite = M15PublicationBody(id); BinaryPrimitives.WriteSingleLittleEndian(nonfinite.AsSpan(2), float.NaN);
            var args = M2ContractsTests.Packet((PacketTypes)108, nonfinite, Slot);
            ServerApi.Hooks.NetGetData.Invoke(args); Assert.That(args.Handled, Is.True);
        }
        foreach (byte operation in new byte[] { 1, 2 })
        {
            var legal = M15PublicationBody(140); legal[0] = operation;
            var args = M2ContractsTests.Packet((PacketTypes)140, legal, Slot);
            ServerApi.Hooks.NetGetData.Invoke(args); Assert.That(args.Handled, Is.False);
        }
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
    }
}
