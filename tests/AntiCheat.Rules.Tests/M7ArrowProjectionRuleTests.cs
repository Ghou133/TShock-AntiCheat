using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M7ArrowProjectionRuleTests
{
    private static ProjectileObservation Arrow => new(new(Actor.Slot, 3, 1), ProjectileOperation.Update, 1);
    private static ArrowProjectionContext Context(int first = 9) => new(Input, true, true, true, first, false);

    [TestCase(4, 16385)] [TestCase(9, 9)] [TestCase(9, 2)] [TestCase(9, 16386)]
    [TestCase(9, -32766)] [TestCase(9, 3)] [TestCase(500, 125)] [TestCase(500, 0)]
    public void FullInt32PreimagesRetainWrapReflectionAndCleanup(int first, int next) =>
        Pass(M7ArrowProjectionRules.Evaluate(Arrow, next, Context(first)));

    [TestCase(9, 1005)] [TestCase(4, 1005)] [TestCase(500, 400)] [TestCase(9, 10)]
    public void ImpossibleProjectionIsAnIndependentFirstProof(int first, int next) =>
        Candidate(M7ArrowProjectionRules.Evaluate(Arrow, next, Context(first)));

    [Test]
    public void CompletePositiveAndNegativeRepresentativeResiduesMatchNativeIntegerDivision()
    {
        // Every 16-bit residue and every quarter-output high-bit residue, on both sides of zero.
        // Adding 262144 to an Int32 preimage adds exactly 65536 after /4, leaving the wire unchanged.
        for (int residue = 0; residue <= ushort.MaxValue; residue++)
        for (int multiple = -4; multiple <= 3; multiple++)
        {
            int internalValue = residue + multiple * 65536;
            int first = unchecked((short)internalValue);
            int reflected = unchecked((short)(internalValue / 2 / 2));
            if (!M7ArrowProjectionRules.IsReachable(first, reflected))
                Assert.Fail($"Missing native preimage internal={internalValue}, first={first}, reflected={reflected}");
        }
    }

    [Test]
    public void NoSourcePermissionOrLifecycleIsInventedFromAHighFirstDeclaration()
    {
        Unknown(M7ArrowProjectionRules.Evaluate(Arrow with { Operation = ProjectileOperation.Create }, 1005, Context()));
        Unknown(M7ArrowProjectionRules.Evaluate(Arrow, 1005, Context() with { SourceException = true }));
        Unknown(M7ArrowProjectionRules.Evaluate(Arrow, 1005, Context() with { SameLiveEntity = false }));
        Unknown(M7ArrowProjectionRules.Evaluate(Arrow, 1005, Context() with { InitialDeclarationConfirmed = false }));
        Unknown(M7ArrowProjectionRules.Evaluate(Arrow, 1005, Context() with { LifecycleVerified = false }));
        Unknown(M7ArrowProjectionRules.Evaluate(Arrow, 1005, Context() with { Input = Input with { SnapshotFingerprint = "other" } }));
    }

    [Test]
    public void WithdrawnC6RemainsUnknownForTheNativeWrapCounterexample() =>
        Unknown(M5CombatRules.EvaluateArrowEvolution(Arrow, 16385, new(Input, true, true, true, 4, false)));
}
