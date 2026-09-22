using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using TerrariaApi.Server;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [TestCase(false)] [TestCase(true)]
    public async Task M14RCavernMonster_FirstPublicationCancelsRevokesAndPersistsOnlyRealAccount(bool earlierCancelled)
    {
        var innocent = engine.OpenSession(22)!.Value; engine.Authenticate(innocent, 1472);
        int later = 0;
        void Observe(GetDataEventArgs args) { if (!args.Handled) later++; }
        ServerApi.Hooks.NetGetData.Register(plugin, Observe, 0);
        try
        {
            var args = M2ContractsTests.Packet((PacketTypes)136, new byte[12], Slot); args.Handled = earlierCancelled;
            ServerApi.Hooks.NetGetData.Invoke(args);
            Assert.That(args.Handled, Is.True); Assert.That(later, Is.Zero); Assert.That(sent, Is.Empty);
            Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.CanWrite(innocent), Is.True);
            Assert.That(engine.SanctionCount, Is.EqualTo(1));
            var followup = M2ContractsTests.Packet((PacketTypes)5, new byte[8], Slot);
            ServerApi.Hooks.NetGetData.Invoke(followup);
            Assert.That(followup.Handled, Is.True); Assert.That(later, Is.Zero); Assert.That(sent, Is.Empty);
            var result = M14RCavernMonsterPacketReader.Evaluate(new([0, 0, 0, 0, 0, 0]), session,
                ServerTShock.Players[Slot], TargetRuntime.Fingerprint, true);
            var existing = engine.ObserveBusiness(new(session, 136, result,
                new(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
            Assert.That(existing.Incident!.AccountId, Is.EqualTo(6207));
            Assert.That(existing.Incident.Evidence.RuleId, Is.EqualTo(M14RCavernMonsterRules.RuleId));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1)); Assert.That(store.Bans, Is.EqualTo(1));
            var reconnect = engine.OpenSession(Slot)!.Value;
            Assert.That(engine.Authenticate(reconnect, 6207), Is.EqualTo(AuthenticationResult.AccountBlocked));
            Assert.That(engine.CanWrite(innocent), Is.True);
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, Observe); }
    }

    [Test]
    public void M14RCavernMonster_MalformedOnlyBlocksAndLaterLegalRequestRemainsPossible()
    {
        foreach (int length in new[] { 0, 11, 13 })
        {
            var args = M2ContractsTests.Packet((PacketTypes)136, new byte[length], Slot);
            ServerApi.Hooks.NetGetData.Invoke(args); Assert.That(args.Handled, Is.True);
        }
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        var legal = M2ContractsTests.Packet((PacketTypes)53, [NpcSlot, 0, 24, 0, 88, 2], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal); Assert.That(legal.Handled, Is.False);
        Assert.That(engine.CanWrite(session), Is.True);
    }
}
