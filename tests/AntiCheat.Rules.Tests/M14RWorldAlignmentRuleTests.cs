using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M14RWorldAlignmentRuleTests
{
    private static BusinessRuleResult Evaluate(M14RWorldAlignmentObservation? state = null,
        RuleInputContext? input = null, bool contract = true, bool? extension = false) =>
        M14RWorldAlignmentRules.Evaluate(state ?? new(0, 0, 0), input ?? Input, contract,
            scopedClientExtensionAuthorized: extension);

    [TestCase(0, 0, 0)] [TestCase(100, 1, 1)] [TestCase(255, 255, 255)]
    public void OriginalServerAndExplicitExtensionAcceptEveryByteWithoutPercentageInference(int h, int c, int r)
    {
        Pass(Evaluate(new(h, c, r), Input with { ClientOrigin = false }));
        Pass(Evaluate(new(h, c, r), extension: true));
    }

    [TestCase(0, 0, 0)] [TestCase(100, 1, 1)] [TestCase(255, 255, 255)]
    public void FirstExactClientPublicationIsRoleProofWithoutTileMutationOrHistory(int h, int c, int r)
    {
        var result = Evaluate(new(h, c, r)); Candidate(result);
        Assert.That(result.Facts["worldTileHistoryRequired"], Is.EqualTo("False"));
        Assert.That(result.Facts["serverMutationSucceededRequired"], Is.EqualTo("False"));
    }

    [Test]
    public void MissingContractIdentityRuntimeGenerationAndParserDoNotBecomeProof()
    {
        Unknown(Evaluate(contract: false)); Unknown(Evaluate(extension: null));
        Unknown(Evaluate(input: Input with { ParserComplete = false }));
        Unknown(Evaluate(input: Input with { SnapshotFingerprint = "different-runtime" }));
        Unknown(Evaluate(input: Input with { SnapshotComplete = false }));
        Unknown(Evaluate(input: Input with { AttributionComplete = false }));
        Unknown(Evaluate(input: Input with { ExceptionsExcluded = false }));
        Unknown(Evaluate(input: Input with { Session = Input.Session with { Generation = 0 } }));
        Unknown(M14RWorldAlignmentRules.Evaluate(new(0, 0, 0), Input, true, "another-contract"));
    }

    [TestCase(-1, 0, 0)] [TestCase(0, 256, 0)] [TestCase(0, 0, int.MaxValue)]
    public void OutsideSerializedDomainIsOnlySafetyBlock(int h, int c, int r)
    {
        var result = Evaluate(new(h, c, r));
        Assert.That(result.Action, Is.EqualTo(ControlAction.Block));
        Assert.That(result.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(result.PredicateSatisfied, Is.False);
    }
}
