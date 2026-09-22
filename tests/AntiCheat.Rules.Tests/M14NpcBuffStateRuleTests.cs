using System.Collections.Immutable;
using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M14NpcBuffStateRuleTests
{
    private static M14NpcBuffStateObservation State(int npc = 3, int count = 1, int type = 24, int time = 600) =>
        new(npc, Enumerable.Repeat(new M14NpcBuffStateEntry(type, time), count).ToImmutableArray());
    private static BusinessRuleResult Evaluate(M14NpcBuffStateObservation? state = null, RuleInputContext? input = null,
        bool contract = true, bool? extension = false) => M14NpcBuffStateRules.Evaluate(state ?? State(), input ?? Input,
            contract, scopedClientExtensionAuthorized: extension);

    [TestCase(0, 0)] [TestCase(1, 600)] [TestCase(20, 0)] [TestCase(20, 65535)]
    public void ActualServerPublicationDomainIncludesEmptyFullAndZeroTimeLists(int count, int time)
    {
        Pass(Evaluate(State(count: count, time: time), Input with { ClientOrigin = false }));
        Pass(Evaluate(State(count: count, time: time), extension: true));
    }

    [TestCase(0)] [TestCase(199)]
    public void FirstClientFullStateRequestUsesConnectionNotNpcAsTheSubject(int npc)
    {
        var result = Evaluate(State(npc)); Candidate(result);
        Assert.That(result.RuleId, Is.EqualTo(M14NpcBuffStateRules.RuleId));
        Assert.That(result.Facts["targetNpcStateRequired"], Is.EqualTo("False"));
        Assert.That(result.Facts["serverMutationSucceededRequired"], Is.EqualTo("False"));
        Assert.That(result.Version, Is.EqualTo("1.0.0"));
    }

    [TestCase(-1, 1, 24, 600)] [TestCase(200, 1, 24, 600)] [TestCase(3, 21, 24, 600)]
    [TestCase(3, 1, 0, 600)] [TestCase(3, 1, 401, 600)] [TestCase(3, 1, 24, -1)] [TestCase(3, 1, 24, 65536)]
    public void UnsupportedWireDomainIsOnlySafetyBlock(int npc, int count, int type, int time)
    {
        var result = Evaluate(State(npc, count, type, time));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block)); Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.PredicateSatisfied, Is.False); Assert.That(result.PrerequisitesComplete, Is.False);
    }

    [Test]
    public void MissingRuntimeParserHostIdentityOrGenerationCannotBecomeAccountProof()
    {
        Unknown(Evaluate(contract: false)); Unknown(Evaluate(extension: null));
        Unknown(Evaluate(input: Input with { SnapshotFingerprint = "unknown" }));
        Unknown(Evaluate(input: Input with { ParserComplete = false }));
        Unknown(Evaluate(input: Input with { SnapshotComplete = false }));
        Unknown(Evaluate(input: Input with { AttributionComplete = false }));
        Unknown(Evaluate(input: Input with { ExceptionsExcluded = false }));
        Unknown(Evaluate(input: Input with { Session = Input.Session with { Generation = 0 } }));
        Unknown(M14NpcBuffStateRules.Evaluate(State(), Input, true, "another-contract"));
        Unknown(Evaluate(new(3, default)));
    }
}
