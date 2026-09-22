using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M15NpcBuffTypeRuleTests
{
    private static BusinessRuleResult Check(int type, int time = 60, RuleInputContext? input = null,
        bool? authorized = false, int target = 3) => M15NpcBuffTypeRules.Evaluate(
        new(M13NpcBuffOperation.Add, target, type, time), input ?? Input, authorized);

    [Test]
    public void NativeLegalProducerUnionIncludesUnusualAndNewSourcesWithFullSignedWireTimes()
    {
        // Independent audited producer union, including 25 Tipsy, 44 Burning and the new 1.4.5 branches.
        int[] native = [20,24,25,30,31,36,39,44,69,70,72,103,119,120,137,151,153,165,169,183,186,189,
            203,204,310,320,323,324,337,344,353,362,375,395,397,398,399,400];
        foreach (int type in native)
            foreach (int time in new[] { short.MinValue, -1, 0, 1, 100, 600, 19392, short.MaxValue })
                Pass(Check(type, time));
        Assert.That(Enumerable.Range(1, 400).Where(M15NpcBuffTypeRules.IsNativeProducerType), Is.EqualTo(native));
    }

    [Test]
    public void NativeLegalServerScopedAndOtherOperationsNeverImplicateTheRecipient()
    {
        Pass(Check(5, input: Input with { ClientOrigin = false }));
        Pass(Check(5, authorized: true));
        Pass(M15NpcBuffTypeRules.Evaluate(new(M13NpcBuffOperation.Remove, 3, 5), Input));
        Pass(M15NpcBuffTypeRules.Evaluate(new((M13NpcBuffOperation)54, 3, 5), Input));
    }

    [TestCase(1)] [TestCase(5)] [TestCase(87)] [TestCase(196)] [TestCase(396)]
    public void FirstPositiveCompleteRequestForAnUnproducibleTypeIsProof(int type)
    {
        var result = Check(type); Candidate(result);
        Assert.That(result.RuleId, Is.EqualTo(M15NpcBuffTypeRules.RuleId));
        Assert.That(result.Facts["npcIndex"], Is.EqualTo("3"));
        Assert.That(result.Facts["nativeProducerType"], Is.EqualTo("False"));
    }

    [Test]
    public void MissingIdentityHostVersionParserAndExtensionLeaveProofUnknown()
    {
        foreach (var input in new[] { Input with { AttributionComplete = false }, Input with { SnapshotComplete = false },
            Input with { ParserComplete = false }, Input with { ExceptionsExcluded = false },
            Input with { SnapshotFingerprint = "different" }, Input with { Session = Actor with { Generation = 0 } } })
            Unknown(Check(5, input: input));
        Unknown(Check(5, authorized: null));
        Unknown(M15NpcBuffTypeRules.Evaluate(new(M13NpcBuffOperation.Add, 3, 5, 60), Input,
            contractVersion: "future-contract"));
    }

    [Test]
    public void MalformedDomainAndNonpositiveUnknownCategoryAreOnlySafetyBlocks()
    {
        foreach (int type in new[] { -1, 0, 401, ushort.MaxValue }) BlockOnly(Check(type));
        foreach (int target in new[] { -1, 200, 255 }) BlockOnly(Check(5, target: target));
        foreach (int time in new[] { -32769, 32768, -32768, -1, 0 }) BlockOnly(Check(5, time));
    }

    [Test]
    public void NpcSlotDoesNotSelectTheResponsiblePlayerOrInheritAnyEvidenceHistory()
    {
        Candidate(Check(5, target: 199));
        Candidate(Check(5, input: Input with { Session = Actor with { Generation = Actor.Generation + 1 } }));
        Pass(Check(24, target: 199));
        Unknown(Check(5, input: Input with { AttributionComplete = false }));
    }
}
