using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [Test]
    public async Task M16_RegisteredRootPreservesLegalStrikeThenCancelsShieldWithoutRevokingOrBan()
    {
        var actor = TShockAPI.TShock.Players[Slot]!;
        var guard = new M16CombatNpcImmunityGuard(TargetRuntime.Fingerprint,
            slot => slot == Slot ? (engine.GetSession(session), actor) : (null, null));
        guard.Tick(session.WorldEpoch);
        typeof(AntiCheatPlugin).GetField("_npcImmunity", Private)!.SetValue(plugin, guard);
        Target.SetDefaults(NPCID.MoonLordCore); Target.active = true; Target.generation = 3;
        Target.life = Target.lifeMax = 100000; Target.defense = 0; Target.ai[0] = 1; Target.dontTakeDamage = false;
        var policy = M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.Production)
            .Single(rule => rule.RuleId == M16CombatImmunityRules.RuleId);
        Assert.That(policy.Qualification, Is.EqualTo(RuleQualification.Unqualified));
        Assert.That(M2RuleRegistry.ProductionRules.ContainsKey(policy.RuleId), Is.False);
        Assert.That(Root(Body(1005, critical: 1)).Handled, Is.False);
        Receive(Body(1005, critical: 1)); Assert.That(Target.life, Is.LessThan(100000));
        Target.ai[0] = 0; Target.dontTakeDamage = true; Target.justHit = false;
        Array.Clear(Target.playerInteraction); sent.Clear(); int before = Target.life;
        var blocked = Root(Body(9));
        Assert.That(blocked.Handled, Is.True, "Registered Core result must not be swallowed as business-rule-not-registered.");
        Assert.That(Target.life, Is.EqualTo(before)); Assert.That(Target.justHit, Is.False);
        Assert.That(Target.playerInteraction[Slot], Is.False); Assert.That(sent, Is.Empty);
        Assert.That(engine.CanWrite(session), Is.True); Assert.That(engine.SanctionCount, Is.Zero);
        Assert.That((await engine.PumpAsync()).Attempted, Is.Zero); Assert.That(store.Bans, Is.Zero);
        Target.ai[0] = 1; Target.dontTakeDamage = false;
        Assert.That(Root(Body()).Handled, Is.False); Receive(Body());
        Assert.That(Target.life, Is.LessThan(before)); Assert.That(engine.CanWrite(session), Is.True);
    }
}
