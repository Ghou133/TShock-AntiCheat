using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M14RCavernMonsterRuleTests
{
    private static M14RCavernMonsterObservation State => new([0, 1, 2, 199, 65534, 65535]);
    private static BusinessRuleResult Check(M14RCavernMonsterObservation? state = null, RuleInputContext? input = null,
        bool host = true, bool? extension = false) => M14RCavernMonsterRules.Evaluate(state ?? State, input ?? Input,
            host, scopedClientExtensionAuthorized: extension);

    [Test]
    public void ServerAndExplicitExtensionMayPublishFullWireDomainAndRepeatedTypes()
    {
        Pass(Check(input: Input with { ClientOrigin = false })); Pass(Check(extension: true));
        Pass(Check(new([0, 0, 0, 0, 0, 0]), Input with { ClientOrigin = false }));
    }

    [Test]
    public void ExactClientPublicationIsFirstRoleProofIndependentOfMonsterRangeOrExistingEntity()
    {
        Candidate(Check()); Candidate(Check(new([0, 0, 0, 0, 0, 0])));
        Assert.That(Check().Facts["npcSpawnOrTargetHistoryRequired"], Is.EqualTo("False"));
    }

    [Test]
    public void UnverifiedHostIdentityRuntimeGenerationAndFrameNeverBecomeProof()
    {
        Unknown(Check(host: false)); Unknown(Check(extension: null)); Unknown(Check(new(default)));
        Unknown(Check(input: Input with { ParserComplete = false }));
        Unknown(Check(input: Input with { SnapshotFingerprint = "other" }));
        Unknown(Check(input: Input with { SnapshotComplete = false }));
        Unknown(Check(input: Input with { AttributionComplete = false }));
        Unknown(Check(input: Input with { ExceptionsExcluded = false }));
        Unknown(Check(input: Input with { Session = Input.Session with { Generation = 0 } }));
        Unknown(M14RCavernMonsterRules.Evaluate(State, Input, true, "other-contract"));
    }

    [Test]
    public void WrongShapeAndWidthRemainSafetyOnly()
    {
        foreach (var state in new M14RCavernMonsterObservation[] { new([]), new([0]), new([0, 0, 0, 0, 0, -1]),
            new([0, 0, 0, 0, 0, 65536]), new([0, 0, 0, 0, 0, 0, 0]) })
        {
            var result = Check(state); Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block)); Assert.That(result.PredicateSatisfied, Is.False);
        }
    }
}
