using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using TerrariaApi.Server;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [TestCase(101, false)] [TestCase(101, true)] [TestCase(103, false)] [TestCase(103, true)]
    public async Task M14LProtocol_FirstPublicationPreservesEarlierCancelAndOnlyRevokesAuthenticatedActor(int id, bool earlierCancelled)
    {
        var body = new byte[8];
        var innocent = engine.OpenSession(22)!.Value; engine.Authenticate(innocent, 1452);
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
            var result = M14LProtocolPacketReader.Evaluate(M14LProtocolPacketReader.ReadPayload(id, body).Packet!,
                session, ServerTShock.Players[Slot], TargetRuntime.Fingerprint, true);
            var existing = engine.ObserveBusiness(new(session, (byte)id, result,
                new(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
            Assert.That(existing.Incident!.AccountId, Is.EqualTo(6207));
            Assert.That(existing.Incident.Evidence.RuleId, Is.EqualTo(M14LProtocolRules.RuleId));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1)); Assert.That(store.Bans, Is.EqualTo(1));
            var reconnect = engine.OpenSession(Slot)!.Value;
            Assert.That(engine.Authenticate(reconnect, 6207), Is.EqualTo(AuthenticationResult.AccountBlocked));
            Assert.That(engine.CanWrite(innocent), Is.True);
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, Observe); }
    }

    [TestCase(101)] [TestCase(103)]
    public void M14LProtocol_MalformedShapeSafetyBlockPreservesSameSessionNormalWrites(int id)
    {
        foreach (int length in new[] { 0, 7, 9 })
        {
            var args = M2ContractsTests.Packet((PacketTypes)id, new byte[length], Slot);
            ServerApi.Hooks.NetGetData.Invoke(args); Assert.That(args.Handled, Is.True);
        }
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        var legal = M2ContractsTests.Packet((PacketTypes)53, [NpcSlot, 0, 24, 0, 88, 2], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal); Assert.That(legal.Handled, Is.False);
        Assert.That(engine.CanWrite(session), Is.True);
    }
}
