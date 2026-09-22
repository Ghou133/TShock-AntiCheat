using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M13NpcAuthorityRuleTests
{
    private static BusinessRuleResult Evaluate(int type, RuleInputContext? context = null, bool host = true,
        string contract = M13NpcAuthorityRules.ContractVersion, int? sender = null) =>
        M13NpcAuthorityRules.Evaluate(new(sender ?? Input.Session.Slot, type), context ?? Input, host, contract);

    [TestCase(127)] [TestCase(-16)] [TestCase(125)] [TestCase(126)] [TestCase(134)]
    [TestCase(-12)] [TestCase(-13)] [TestCase(-14)] [TestCase(-15)]
    [TestCase(-6)] [TestCase(-8)] [TestCase(398)] [TestCase(0)] [TestCase(32767)]
    public void OtherRequestsAreOutsideThisNarrowRule(int type) => Pass(Evaluate(type));

    [TestCase(128)] [TestCase(129)] [TestCase(130)] [TestCase(131)]
    public void FirstOwnClientPartRequestIsCompleteWithoutSuccessfulSpawning(int type)
    {
        Candidate(Evaluate(type));
        Assert.That(Evaluate(type).Version, Is.EqualTo("1.0.0"));
        Assert.That(Evaluate(type).Facts["spawnSuccessRequired"], Is.EqualTo("False"));
        Pass(Evaluate(type, Input with { ClientOrigin = false }));
    }

    [Test]
    public void MissingIdentitySnapshotHostRuntimeAndAttributionCannotBan()
    {
        Unknown(Evaluate(128, host: false));
        Unknown(Evaluate(128, contract: "another-version"));
        Unknown(Evaluate(128, sender: Input.Session.Slot + 1));
        Unknown(Evaluate(128, Input with { SnapshotFingerprint = "unknown" }));
        Unknown(Evaluate(128, Input with { ParserComplete = false }));
        Unknown(Evaluate(128, Input with { SnapshotComplete = false }));
        Unknown(Evaluate(128, Input with { AttributionComplete = false }));
        Unknown(Evaluate(128, Input with { ExceptionsExcluded = false }));
        Unknown(Evaluate(128, Input with { Session = Input.Session with { Generation = 0 } }));
    }
}
