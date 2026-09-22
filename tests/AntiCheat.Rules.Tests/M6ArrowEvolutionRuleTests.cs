using AntiCheat.Core;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

public sealed class M6ArrowEvolutionRuleTests
{
    private readonly SessionKey session = new(Guid.NewGuid(), 1, 7, 1);
    private WoodenArrowEvolutionContext Context(bool rangeProven) => new(
        new(session, "locked-target", "locked-target", true, true, true, true), true, true, true, 4, false, rangeProven);
    private static ProjectileObservation Arrow() => new(new(7, 3, 1), ProjectileOperation.Update, 1);

    [TestCase(0)] [TestCase(4)]
    public void SafeNonIncreaseRemainsUnblockedWithoutAMagnitudeCertificate(int damage)
    {
        Assert.That(M5CombatRules.EvaluateArrowEvolution(Arrow(), damage, Context(false)).Verdict, Is.EqualTo(Verdict.Pass));
    }
    [Test]
    public void NativeWireWrapPatternCannotBecomeProofWithoutInternalRangeCertificate()
    {
        var result = M5CombatRules.EvaluateArrowEvolution(Arrow(), 16385, Context(false));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown)); Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(result.PrerequisitesComplete, Is.False); Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(result.Version, Is.EqualTo("1.1.0")); Assert.That(result.Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"));
    }
    [Test]
    public void PurePredicateStillRequiresEveryPremiseIncludingExplicitInternalRangeCertificate()
    {
        Assert.That(M5CombatRules.EvaluateArrowEvolution(Arrow(), 5, Context(true)).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(M5CombatRules.EvaluateArrowEvolution(Arrow(), 5, Context(true) with { SourceException = true }).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(M5CombatRules.EvaluateArrowEvolution(Arrow(), 5, Context(true) with { InitialDeclarationConfirmed = false }).Verdict, Is.EqualTo(Verdict.Unknown));
    }
}
