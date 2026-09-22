using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M6CombatRuleTests
{
    private static NpcStrikeObservation Strike => new(11, 3, 9, 0, 2, 0);
    private static BusinessRuleResult Evaluate(NpcStrikeObservation strike) =>
        M6CombatRules.EvaluateStrike(strike, Input, 200, true, true, 3);

    [TestCase(-32768)] [TestCase(-1)] [TestCase(0)] [TestCase(9)] [TestCase(1000)] [TestCase(1005)] [TestCase(32767)]
    public void OrdinaryAndHighDamageWithNoExclusiveCauseStayUnblocked(int damage) =>
        Unknown(Evaluate(Strike with { Damage = damage }));

    [TestCase(0, 0)] [TestCase(1, 0)] [TestCase(2, 0)] [TestCase(0, 1)] [TestCase(1, 1)] [TestCase(2, 1)]
    public void NativeDirectionAndCriticalValuesDoNotBecomePunishment(int direction, int critical) =>
        Unknown(Evaluate(Strike with { EncodedDirection = direction, CriticalFlag = critical }));

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
    public void NonfiniteKnockbackIsOnlyStructuralBlock(float knockback) =>
        BlockOnly(Evaluate(Strike with { Knockback = knockback }));

    [TestCase(3, 0)] [TestCase(255, 0)] [TestCase(2, 2)] [TestCase(2, 255)]
    public void NoncanonicalFlagsAreNotAnAutomaticBan(int direction, int critical) =>
        BlockOnly(Evaluate(Strike with { EncodedDirection = direction, CriticalFlag = critical }));

    [Test] public void StaleNpcGenerationRetainsNativeNoopBeforeNumericFields() =>
        Pass(Evaluate(Strike with { TargetGeneration = 2, Knockback = float.NaN }));

    [Test] public void ServerCleanupAndMissingTargetDoNotBlameTheAccount()
    {
        Pass(M6CombatRules.EvaluateStrike(Strike with { Damage = -1 }, Input with { ClientOrigin = false }, 200, true, true, 3));
        Unknown(M6CombatRules.EvaluateStrike(Strike, Input, 200, false, false, -1));
        Unknown(M6CombatRules.EvaluateStrike(Strike, Input with { SnapshotFingerprint = "changed" }, 200, true, true, 3));
        BlockOnly(Evaluate(Strike with { TargetSlot = 255 }));
    }
}
