using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M13NpcBuffRuleTests
{
    private static BusinessRuleResult Check(M13NpcBuffObservation packet, RuleInputContext? input = null,
        bool intact = true, string contract = M13NpcBuffRules.ContractVersion, bool? authorized = false)
        => M13NpcBuffRules.Evaluate(packet, input ?? Input, 200, intact, contract, authorized);

    [TestCase(24, 600)] [TestCase(310, 240)] [TestCase(362, 240)] [TestCase(153, 32767)]
    public void OriginalClientNpcBuffAddIsAnEffectivePassBeforeRemovalProof(int type, int time)
    {
        var add = new M13NpcBuffObservation(M13NpcBuffOperation.Add, 3, type, time);
        Pass(Check(add));
        Candidate(Check(add with { Operation = M13NpcBuffOperation.Remove, Time = 0 }));
    }

    [Test]
    public void FirstCompleteRemovalProofNeedsNoPreviousNpcHistoryOrRepeatedAttempt()
    {
        var result = Check(new(M13NpcBuffOperation.Remove, 199, 24));
        Candidate(result);
        Assert.That(result.RuleId, Is.EqualTo(M13NpcBuffRules.RuleId));
        Assert.That(result.Version, Is.EqualTo(M13NpcBuffRules.Version));
        Assert.That(result.Reason, Is.EqualTo("client-requested-nonremovable-npc-buff"));
    }

    [Test]
    public void MissingVersionAttributionExceptionAndTableInputsStayUnknown()
    {
        var packet = new M13NpcBuffObservation(M13NpcBuffOperation.Remove, 3, 24);
        Unknown(Check(packet, Input with { ParserComplete = false }));
        Unknown(Check(packet, Input with { SnapshotFingerprint = "other" }));
        Unknown(Check(packet, Input with { AttributionComplete = false }));
        Unknown(Check(packet, Input with { SnapshotComplete = false }));
        Unknown(Check(packet, Input with { ExceptionsExcluded = false }));
        Unknown(Check(packet, intact: false));
        Unknown(Check(packet, contract: "old-version"));
        Unknown(Check(packet, authorized: null));
    }

    [TestCase(-1, 24)] [TestCase(200, 24)] [TestCase(3, 0)] [TestCase(3, 401)] [TestCase(3, 65535)]
    public void InvalidStructureNeverBecomesAccountProof(int npc, int buff)
        => BlockOnly(Check(new(M13NpcBuffOperation.Remove, npc, buff)));

    [Test]
    public void ServerAndPreciselyScopedClientExtensionsAreSeparateExceptions()
    {
        var packet = new M13NpcBuffObservation(M13NpcBuffOperation.Remove, 3, 24);
        Pass(Check(packet, Input with { ClientOrigin = false }));
        Pass(Check(packet, authorized: true));
        Candidate(Check(packet));
    }
}
