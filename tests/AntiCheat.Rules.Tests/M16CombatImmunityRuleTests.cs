using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M16CombatImmunityRuleTests
{
    private static NpcStrikeObservation Hit => new(11, 3, 9, 0, 2, 0);
    private static M16CombatTargetSnapshot Core => new(11, 3, 398, 398, 77, true, 10000, 0, true);
    private static BusinessRuleResult Evaluate(M16CombatTargetSnapshot? target, NpcStrikeObservation? hit = null) =>
        M16CombatImmunityRules.Evaluate(hit ?? Hit, Input, target, true);

    [TestCase(short.MinValue, 0)] [TestCase(-1, 0)] [TestCase(0, 0)] [TestCase(9, 0)]
    [TestCase(1005, 0)] [TestCase(short.MaxValue, 0)] [TestCase(short.MaxValue, 1)]
    public void VulnerablePhaseKeepsEntireDamageWireDomain(int amount, int critical) =>
        Pass(Evaluate(Core with { Phase = 1, DontTakeDamage = false }, Hit with { Damage = amount, CriticalFlag = critical }));

    [TestCase(396)] [TestCase(397)] [TestCase(1)] [TestCase(517)]
    public void OtherBossPartsAndNpcMechanismsRemainUnchanged(int type) =>
        Unknown(Evaluate(Core with { Type = type, NetId = type }));

    [Test]
    public void OldGenerationInactiveTargetAndNativeHostKeepTheirExistingNoops()
    {
        Pass(Evaluate(Core with { Generation = 4 }));
        Pass(Evaluate(Core with { Active = false }));
        Pass(Evaluate(Core with { Life = 0 }));
        Pass(M16CombatImmunityRules.Evaluate(Hit, Input with { ClientOrigin = false }, Core, true));
    }

    [TestCase(-2)] [TestCase(-1)] [TestCase(0)] [TestCase(2)] [TestCase(3)]
    public void CurrentSupportedImmunePhasesRejectOnlyCurrentAction(float phase) =>
        BlockOnly(Evaluate(Core with { Phase = phase }));

    [TestCase(short.MinValue)] [TestCase(-1)] [TestCase(0)] [TestCase(9)] [TestCase(short.MaxValue)]
    public void ZeroAndNegativeWireAreAlsoBlockedBecauseNativeStrikeWouldTakeAtLeastOneLife(int damage) =>
        BlockOnly(Evaluate(Core, Hit with { Damage = damage }));

    [Test]
    public void ContradictoryOrUnverifiedStateDoesNotFreezeCombat()
    {
        Unknown(Evaluate(null)); Unknown(Evaluate(Core with { Slot = 12 }));
        Unknown(Evaluate(Core with { Phase = 1 })); Unknown(Evaluate(Core with { DontTakeDamage = false }));
        Unknown(Evaluate(Core with { Phase = float.NaN })); Unknown(Evaluate(Core with { Phase = 17 }));
        Unknown(Evaluate(Core with { NetId = 397 })); Unknown(Evaluate(Core with { AiStyle = 76 }));
        Unknown(M16CombatImmunityRules.Evaluate(Hit, Input, Core, false));
        Unknown(M16CombatImmunityRules.Evaluate(Hit, Input with { SnapshotFingerprint = "wrong" }, Core, true));
        Unknown(M16CombatImmunityRules.Evaluate(Hit, Input with { AttributionComplete = false }, Core, true));
        Unknown(M16CombatImmunityRules.Evaluate(Hit, Input with { ExceptionsExcluded = false }, Core, true));
    }

    [Test]
    public void PhaseChangesNeverAccumulateSuspicionOrRequireAnExtraPlayerStep()
    {
        var request = Hit with { Damage = 1005, CriticalFlag = 1 };
        Pass(Evaluate(Core with { Phase = 1, DontTakeDamage = false }, request));
        BlockOnly(Evaluate(Core with { Phase = 2 }, request)); // includes a previously legal delayed hit
        Pass(Evaluate(Core with { Phase = 1, DontTakeDamage = false }, request));
        Assert.That(Evaluate(Core, request).PredicateSatisfied, Is.False);
    }
}
