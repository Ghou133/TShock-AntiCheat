using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M5VitalsRulesTests
{
    private static M4VitalObservation Hurt(bool other, bool pvp, int damage = 20) => new(M4VitalKind.HurtDeclaration,
        Actor.Slot + (other ? 1 : 0), Pvp: pvp, HurtDamage: damage);

    [TestCase(false, false)] // NPC/environment/Paladin shield damage is self-reported.
    [TestCase(false, true)]
    [TestCase(true, true)] // Melee, projectile and Inferno cross-player damage.
    public void NativeRolesRemainValidRegardlessOfDamage(bool other, bool pvp)
    {
        foreach (int damage in new[] { -1, 0, 20, short.MaxValue })
            Pass(M5VitalsRules.EvaluateHurt(Hurt(other, pvp, damage), Input, true, false));
    }

    [Test]
    public void FirstCompleteNonPvpCrossPlayerDeclarationIsProven()
    {
        var result = M5VitalsRules.EvaluateHurt(Hurt(true, false), Input, true, false);
        Candidate(result);
        Assert.That(result.RuleId, Is.EqualTo(M5VitalsRules.HurtRuleId));
    }

    [TestCase("version")][TestCase("parser")][TestCase("snapshot")][TestCase("attribution")]
    [TestCase("exceptions")][TestCase("origin")][TestCase("plugins")][TestCase("sync")]
    public void IncompletePremiseNeverAccumulatesIntoProof(string missing)
    {
        var input = missing switch
        {
            "version" => Input with { SnapshotFingerprint = "old" },
            "parser" => Input with { ParserComplete = false },
            "snapshot" => Input with { SnapshotComplete = false },
            "attribution" => Input with { AttributionComplete = false },
            "exceptions" => Input with { ExceptionsExcluded = false },
            "origin" => Input with { ClientOrigin = false },
            _ => Input
        };
        for (int i = 0; i < 5; i++)
            Unknown(M5VitalsRules.EvaluateHurt(Hurt(true, false), input, missing != "plugins", missing == "sync"));
    }

    [Test]
    public void ReservedTargetAndCurrentVitalValuesCannotBecomeHurtProof()
    {
        Unknown(M5VitalsRules.EvaluateHurt(Hurt(true, false) with { ClaimedSlot = 255 }, Input, true, false));
        Unknown(M5VitalsRules.EvaluateHurt(new(M4VitalKind.Life, Actor.Slot, 100, 100), Input, true, false));
    }
}
