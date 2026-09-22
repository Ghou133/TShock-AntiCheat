using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M8DamageAllowedResultsTests
{
    [TestCase(65540, 65540, 1)]
    [TestCase(65540, 65540, 4)]
    [TestCase(-131073, -65539, 4)]
    [TestCase(32000, 40000, 1)]
    [TestCase(-15, 15, 4)]
    public void ProjectionMatchesEveryNativeNarrowingInBoundedInputInterval(int minimum, int maximum, int divisor)
    {
        var expected = Enumerable.Range(minimum, maximum - minimum + 1).Select(x => unchecked((short)(x / divisor))).ToHashSet();
        var actual = M8DamageWireSet.Project(minimum, maximum, divisor);
        for (int candidate = short.MinValue; candidate <= short.MaxValue; candidate++)
            Assert.That(actual.Contains(candidate), Is.EqualTo(expected.Contains((short)candidate)), $"candidate={candidate}");
    }

    [Test]
    public void CompleteUnionKeepsAlternateSourcesAndNativeWrapRatherThanUsingFirstOrLargestWireValue()
    {
        var results = M8DamageAllowedResults.Build([
            new("ordinary", 9, 9), new("host-custom", 65540, 65540, true), new("delayed-release", 1005, 1005)], []);
        Assert.That(results.Complete, Is.True);
        foreach (int legal in new[] { 9, 4, 16385, 1005 }) Assert.That(results.AllowedResults.Contains(legal), Is.True);
        Assert.That(results.CanExclude(1006), Is.True);
    }

    [Test]
    public void EveryUnknownBranchWidensTheActualUnionAndCannotBeDeletedToExclude1005()
    {
        var results = M8DamageAllowedResults.Build([new("canonical-component", 0, 100)], ["unobserved-effect-history"]);
        Assert.That(results.SupportedResults.Contains(1005), Is.False);
        Assert.That(results.AllowedResults.IsFull, Is.True);
        Assert.That(results.AllowedResults.Contains(1005), Is.True);
        Assert.That(results.CanExclude(1005), Is.False);
        Assert.That(results.MissingPremises, Does.Contain("unobserved-effect-history"));
    }

    [Test]
    public void TargetFormulaKeepsSeparateFloatTruncationsAndSharpBarbBeforeAmmoScale()
    {
        var branch = M8DamageAllowedResults.ArrowComponents("native", 7, 5, 1.3f, 1.3f, true);
        Assert.That(branch.Maximum, Is.EqualTo((int)(7 * 1.3f + 0.000005f) + (int)(6 * 1.3f)));
        var bad = M8DamageAllowedResults.ArrowComponents("nonfinite", 7, 5, float.NaN, 1, false);
        Assert.That(M8DamageWireSet.Project(bad.Minimum, bad.Maximum).IsFull, Is.True);
        Assert.That(M8DamageAllowedResults.ArrowComponents("overflow", int.MaxValue, 5, 2, 1, false).Minimum, Is.EqualTo(int.MinValue));
    }

    [Test]
    public void RangeAndBranchCapacityOnlyWidenAndNegative28ClampsBeforeNativeDamage()
    {
        Assert.That(M8DamageWireSet.Project(int.MinValue, int.MaxValue).IsFull, Is.True);
        var many = M8DamageAllowedResults.Build(Enumerable.Range(0, 65).Select(x => new M8DamageSourceRange($"branch{x}", x * 2, x * 2)), []);
        Assert.That(many.AllowedResults.IsFull, Is.True); Assert.That(many.Complete, Is.False);
        Assert.That(M8DamageAllowedResults.NpcReceiverDamage(-1), Is.Zero);
        Assert.That(M8DamageAllowedResults.NpcReceiverDamage(1005), Is.EqualTo(1005));
    }
}
