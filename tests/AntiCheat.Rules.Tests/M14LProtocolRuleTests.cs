using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M14LProtocolRuleTests
{
    private static M14LEventStateObservation State(int id = 101) => id == 101
        ? new(id, [0, 1, 150, ushort.MaxValue]) : new(id, [int.MinValue, int.MaxValue]);
    private static BusinessRuleResult Evaluate(M14LEventStateObservation? state = null, RuleInputContext? input = null,
        bool contract = true, bool? extension = false) => M14LProtocolRules.Evaluate(state ?? State(), input ?? Input,
            contract, scopedClientExtensionAuthorized: extension);

    [TestCase(101)] [TestCase(103)]
    public void OriginalServerAndExplicitExtensionUseTheWholeWireDomain(int id)
    {
        Pass(Evaluate(State(id), Input with { ClientOrigin = false }));
        Pass(Evaluate(State(id), extension: true));
        Pass(Evaluate(new(61, [Input.Session.Slot, -8])));
    }

    [TestCase(101)] [TestCase(103)]
    public void FirstExactClientPublicationIsRoleProofIndependentOfEventStateAndMagnitude(int id)
    {
        var result = Evaluate(State(id)); Candidate(result);
        Assert.That(result.RuleId, Is.EqualTo(M14LProtocolRules.RuleId));
        Assert.That(result.Facts["gameplayHistoryRequired"], Is.EqualTo("False"));
        Assert.That(result.Facts["serverMutationSucceededRequired"], Is.EqualTo("False"));
        Candidate(Evaluate(id == 101 ? new(id, [0, 0, 0, 0]) : new(id, [0, 0])));
    }

    [Test]
    public void MissingContractIdentityRuntimeAndGenerationCannotBecomeProof()
    {
        Unknown(Evaluate(contract: false)); Unknown(Evaluate(extension: null));
        Unknown(Evaluate(input: Input with { SnapshotFingerprint = "unknown" }));
        Unknown(Evaluate(input: Input with { ParserComplete = false }));
        Unknown(Evaluate(input: Input with { SnapshotComplete = false }));
        Unknown(Evaluate(input: Input with { AttributionComplete = false }));
        Unknown(Evaluate(input: Input with { ExceptionsExcluded = false }));
        Unknown(Evaluate(input: Input with { Session = Input.Session with { Generation = 0 } }));
        Unknown(M14LProtocolRules.Evaluate(State(), Input, true, "another-contract"));
        Unknown(Evaluate(new(101, default)));
    }

    [Test]
    public void IncompleteFieldShapeOrOutOfWidthIsSafetyOnly()
    {
        foreach (var state in new M14LEventStateObservation[] { new(101, [0]), new(103, [0]),
            new(101, [0, 0, 0, -1]), new(101, [0, 0, 0, 65536]) })
        {
            var result = Evaluate(state);
            Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
            Assert.That(result.PredicateSatisfied, Is.False);
        }
    }
}
