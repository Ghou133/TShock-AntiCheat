using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M14RNpcShimmerRuleTests
{
    private static BusinessRuleResult Check(int time, RuleInputContext? input = null, bool? authorized = false,
        int type = 353, int target = 3) => M14RNpcShimmerRules.Evaluate(
        new(M13NpcBuffOperation.Add, target, type, time), input ?? Input, authorized);

    [Test]
    public void NativeShimmerArrowAndUnrelatedLongTorchSlimeRequestsRemainAllowed()
    {
        Pass(Check(100)); Pass(Check(1)); Pass(Check(19392, type: 24)); Pass(Check(600, type: 153));
        Pass(Check(19392, Input with { ClientOrigin = false })); Pass(Check(19392, authorized: true));
        Pass(M14RNpcShimmerRules.Evaluate(new(M13NpcBuffOperation.Remove, 3, 353, 1000), Input));
    }

    [TestCase(101)] [TestCase(19392)] [TestCase(32767)]
    public void FirstPositiveDomainRequestOutsideEveryNativeSourceIsProof(int time)
    {
        var result = Check(time); Candidate(result);
        Assert.That(result.RuleId, Is.EqualTo(M14RNpcShimmerRules.RuleId));
        Assert.That(result.Facts["npcIndex"], Is.EqualTo("3"));
    }

    [TestCase(0)] [TestCase(-1)] [TestCase(-32768)] [TestCase(32768)]
    public void MalformedDurationIsOnlySafetyBlock(int time) => BlockOnly(Check(time));

    [Test]
    public void MissingIdentityHostVersionOrScopedAuthorizationDoesNotBan()
    {
        Unknown(Check(101, Input with { AttributionComplete = false }));
        Unknown(Check(101, Input with { SnapshotComplete = false }));
        Unknown(Check(101, Input with { ParserComplete = false }));
        Unknown(Check(101, Input with { ExceptionsExcluded = false }));
        Unknown(Check(101, Input with { SnapshotFingerprint = "different" }));
        Unknown(Check(101, authorized: null)); BlockOnly(Check(101, target: 200));
    }
}
