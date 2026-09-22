using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

internal static class BusinessTestData
{
    internal const string Fingerprint = "test-target-1.4.5.8-fingerprint";
    internal static readonly SessionKey Actor = new(Guid.Parse("273333AA-8C63-4AE5-B0FC-F95F746330DB"), 4, 7, 12);
    internal static RuleInputContext Input => new(Actor, Fingerprint, Fingerprint, true, true, true, true);
    internal static void Pass(BusinessRuleResult result) => Assert.Multiple(() =>
    {
        Assert.That(result.Action, Is.EqualTo(ControlAction.Pass), result.Reason);
        Assert.That(result.PredicateSatisfied, Is.False);
    });
    internal static void Unknown(BusinessRuleResult result) => Assert.Multiple(() =>
    {
        Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown), result.Reason);
        Assert.That(result.PredicateSatisfied, Is.False);
    });
    internal static void BlockOnly(BusinessRuleResult result) => Assert.Multiple(() =>
    {
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block), result.Reason);
        Assert.That(result.PredicateSatisfied, Is.False);
        Assert.That(result.Verdict, Is.Not.EqualTo(Verdict.ProvenCheat));
    });
    internal static void Candidate(BusinessRuleResult result) => Assert.Multiple(() =>
    {
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block), result.Reason);
        Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(result.PredicateSatisfied, Is.True);
        Assert.That(result.PrerequisitesComplete, Is.True);
    });
}
