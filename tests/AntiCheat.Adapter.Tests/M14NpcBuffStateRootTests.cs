using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    private static byte[] M14StateBody() => [NpcSlot, 0, BuffID.OnFire, 0, 88, 2, 0, 0];
    private GetDataEventArgs M14Root(byte id, byte[] body, bool cancelled = false)
    {
        var args = M2ContractsTests.Packet((PacketTypes)id, body, Slot); args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args); return args;
    }
    private static void M14Native(byte id, byte[] body)
    {
        var buffer = new MessageBuffer { whoAmI = Slot }; buffer.readBuffer[0] = id;
        body.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, body.Length + 1, out _);
    }

    [TestCase(false)] [TestCase(true)]
    public async Task M14Npc_FirstComplete54CancelsBeforeLaterHandlersAndRevokesOnlyItsAccount(bool earlierCancelled)
    {
        byte[] legal = [NpcSlot, 0, BuffID.OnFire, 0, 88, 2];
        Assert.That(M14Root(53, legal).Handled, Is.False); M14Native(53, legal);
        Assert.That(Target.FindBuffIndex(BuffID.OnFire), Is.GreaterThanOrEqualTo(0));
        Assert.That(sent.Count(x => x.Id == 54), Is.EqualTo(1)); sent.Clear();
        var innocent = engine.OpenSession(NpcSlot)!.Value; engine.Authenticate(innocent, 1411);
        int later = 0;
        void Observe(GetDataEventArgs args) { if (!args.Handled) later++; }
        ServerApi.Hooks.NetGetData.Register(plugin, Observe, 0);
        try
        {
            var beforeTypes = Target.buffType.ToArray(); var beforeTimes = Target.buffTime.ToArray();
            Assert.That(M14Root(54, M14StateBody(), earlierCancelled).Handled, Is.True);
            Assert.That(later, Is.Zero); Assert.That(sent, Is.Empty);
            Assert.That(Target.buffType, Is.EqualTo(beforeTypes)); Assert.That(Target.buffTime, Is.EqualTo(beforeTimes));
            Assert.That(engine.CanWrite(session), Is.False); Assert.That(engine.CanWrite(innocent), Is.True);
            Assert.That(engine.SanctionCount, Is.EqualTo(1));
            Assert.That(M14Root(53, legal).Handled, Is.True); Assert.That(later, Is.Zero); Assert.That(sent, Is.Empty);
            var result = M14NpcBuffStatePacketReader.Evaluate(M14NpcBuffStatePacketReader.ReadPayload(M14StateBody()).Packet!,
                session, ServerTShock.Players[Slot], TargetRuntime.Fingerprint, true);
            var existing = engine.ObserveBusiness(new(session, 54, result,
                new(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
            Assert.That(existing.Incident!.AccountId, Is.EqualTo(6207));
            Assert.That(existing.Incident.Evidence.RuleId, Is.EqualTo(M14NpcBuffStateRules.RuleId));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1)); Assert.That(store.Bans, Is.EqualTo(1));
            var reconnect = engine.OpenSession(Slot)!.Value;
            Assert.That(engine.Authenticate(reconnect, 6207), Is.EqualTo(AuthenticationResult.AccountBlocked));
            Assert.That(engine.CanWrite(innocent), Is.True);
        }
        finally { ServerApi.Hooks.NetGetData.Deregister(plugin, Observe); }
    }

    [Test]
    public void M14Npc_Malformed54OnlyBlocksThenSameSessionActual53StillWorks()
    {
        foreach (var body in new byte[][] { [], new byte[8], [255, 127, 0, 0], [NpcSlot, 0, 145, 1, 1, 0, 0, 0] })
            Assert.That(M14Root(54, body).Handled, Is.True);
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero); Assert.That(sent, Is.Empty);
        byte[] legal = [NpcSlot, 0, BuffID.OnFire, 0, 88, 2];
        Assert.That(M14Root(53, legal).Handled, Is.False); M14Native(53, legal);
        Assert.That(Target.buffTime[Target.FindBuffIndex(BuffID.OnFire)], Is.EqualTo(600));
        Assert.That(sent.Single().Id, Is.EqualTo(54)); Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void M14Npc_CurrentEngineAuthenticationAndGenerationRemainMandatory()
    {
        var result = M14NpcBuffStatePacketReader.Evaluate(M14NpcBuffStatePacketReader.ReadPayload(M14StateBody()).Packet!,
            session, ServerTShock.Players[Slot], TargetRuntime.Fingerprint, true);
        var context = new ProofContext(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true);
        var unauthed = engine.OpenSession(22)!.Value;
        Assert.That(engine.ObserveBusiness(new(unauthed, 54, result, context)).Verdict, Is.EqualTo(Verdict.Unknown));
        engine.Disconnect(session); var replacement = engine.OpenSession(Slot)!.Value; engine.Authenticate(replacement, 1422);
        Assert.That(engine.ObserveBusiness(new(session, 54, result, context)).Incident, Is.Null);
        Assert.That(engine.CanWrite(replacement), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        engine.AdvanceWorld();
        Assert.That(engine.ObserveBusiness(new(replacement, 54, result, context)).Incident, Is.Null);
        Assert.That(engine.SanctionCount, Is.Zero);
    }
}
