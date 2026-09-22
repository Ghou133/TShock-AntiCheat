using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M15ProtocolProjectileRuleTests
{
    private static readonly M15CreditsRollObservation Credits = new(0, 28800);
    private static readonly M15CannonFiringObservation Cannon = new(50, 3, 20, 30, 4, 4, 7);

    [Test]
    public void NativeServerPublicationsAndScopedExtensionsRemainLegal()
    {
        foreach (int time in new[] { int.MinValue, -1, 0, 28800, int.MaxValue })
        {
            Pass(M15ProtocolRules.Evaluate(Credits with { RemainingTime = time }, Input with { ClientOrigin = false }, true));
            Pass(M15ProtocolRules.Evaluate(Credits with { RemainingTime = time }, Input, true, scopedClientExtensionAuthorized: true));
        }
        Pass(M15ProjectileRules.Evaluate(Cannon, Input with { ClientOrigin = false }, true));
        Pass(M15ProjectileRules.Evaluate(Cannon with { Damage = short.MinValue, Ammo = short.MaxValue, TargetPlayer = 255 },
            Input with { ClientOrigin = false }, true));
        Pass(M15ProjectileRules.Evaluate(Cannon, Input, true, scopedClientExtensionAuthorized: true));
    }

    [TestCase((byte)1)] [TestCase((byte)2)] [TestCase((byte)3)] [TestCase((byte)255)]
    public void Other140OperationsAreOutsideThisRuleEvenWithoutContext(byte operation) =>
        Pass(M15ProtocolRules.Evaluate(new(operation, int.MinValue), Input with { SnapshotComplete = false }, false));

    [Test]
    public void FirstExactClientPublicationIsProofOfItsRoleNotParameterMagnitude()
    {
        Candidate(M15ProtocolRules.Evaluate(Credits, Input, true));
        Candidate(M15ProtocolRules.Evaluate(new(0, int.MinValue), Input, true));
        Candidate(M15ProjectileRules.Evaluate(Cannon, Input, true));
        Candidate(M15ProjectileRules.Evaluate(Cannon with { Damage = 0, Ammo = 0, TargetPlayer = 22 }, Input, true));
    }

    [Test]
    public void MissingIdentityGenerationRuntimeParserOrHostNeverBecomesProof()
    {
        foreach (var context in new[] { Input with { ParserComplete = false }, Input with { SnapshotFingerprint = "other" },
            Input with { SnapshotComplete = false }, Input with { AttributionComplete = false },
            Input with { ExceptionsExcluded = false }, Input with { Session = Input.Session with { Generation = 0 } } })
        {
            Unknown(M15ProtocolRules.Evaluate(Credits, context, true));
            Unknown(M15ProjectileRules.Evaluate(Cannon, context, true));
        }
        Unknown(M15ProtocolRules.Evaluate(Credits, Input, false));
        Unknown(M15ProjectileRules.Evaluate(Cannon, Input, false));
        Unknown(M15ProtocolRules.Evaluate(Credits, Input, true, "other"));
        Unknown(M15ProjectileRules.Evaluate(Cannon, Input, true, "other"));
        Unknown(M15ProtocolRules.Evaluate(Credits, Input, true, scopedClientExtensionAuthorized: null));
        Unknown(M15ProjectileRules.Evaluate(Cannon, Input, true, scopedClientExtensionAuthorized: null));
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
    public void NonfiniteCannonValueIsOnlySafetyRejection(float value)
    {
        var result = M15ProjectileRules.Evaluate(Cannon with { Knockback = value }, Input, true);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.PredicateSatisfied, Is.False);
    }
}
