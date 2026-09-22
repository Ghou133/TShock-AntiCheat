using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class ProofBoundaryTests
{
    private static IEnumerable<TestCaseData> MissingContext()
    {
        yield return new TestCaseData(Lab.Complete with { RuntimeFingerprint = "other-version" }).SetName("Unknown_RuntimeMismatch");
        yield return new TestCaseData(Lab.Complete with { ParseComplete = false }).SetName("Unknown_IncompleteParse");
        yield return new TestCaseData(Lab.Complete with { ClientOrigin = false }).SetName("Unknown_ServerOrPluginOrigin");
        yield return new TestCaseData(Lab.Complete with { AttributionComplete = false }).SetName("Unknown_AttributionMissing");
        yield return new TestCaseData(Lab.Complete with { ExceptionsExcluded = false }).SetName("Unknown_LegalExceptionsUnresolved");
        yield return new TestCaseData(Lab.Complete with { ContextVersion = "" }).SetName("Unknown_ContextUnversioned");
        yield return new TestCaseData(Lab.Complete with { ContextVersion = "future-v99" }).SetName("Unknown_ContextVersionMismatch");
    }

    [TestCaseSource(nameof(MissingContext))]
    public async Task MissingProofNeverBlocksNormalAssetsOrEscalatesByRepetition(ProofContext context)
    {
        var lab = new Lab();
        var key = await lab.Login();
        for (var index = 0; index < 128; index++)
        {
            var decision = lab.Engine.Observe(Lab.Violation(key) with { Context = context });
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Unknown));
        }
        await lab.Engine.PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(lab.Engine.CanWrite(key), Is.True);
            Assert.That(lab.Engine.SanctionCount, Is.Zero);
            Assert.That(lab.Bans.Calls, Is.Zero);
        });
    }

    [Test]
    public async Task KnownLegalExceptionIsPassEvenWithAnotherSlot()
    {
        var lab = new Lab();
        var key = await lab.Login();
        var decision = lab.Engine.Observe(Lab.Violation(key) with { Context = Lab.Complete with { KnownLegalException = true } });
        Assert.That(decision.Verdict, Is.EqualTo(Verdict.Pass));
        Assert.That(lab.Engine.CanWrite(key), Is.True);
        Assert.That(lab.Engine.SanctionCount, Is.Zero);
    }

    [Test]
    public async Task ClientClaimingVictimSlotSanctionsOnlyAuthenticatedSender()
    {
        var lab = new Lab();
        var attacker = await lab.Login(4, 100);
        var victim = await lab.Login(5, 200);
        var decision = lab.Engine.Observe(new(attacker, 5, victim.Slot, Lab.Complete));
        await lab.Engine.PumpAsync();
        Assert.Multiple(() =>
        {
            Assert.That(decision.Incident!.AccountId, Is.EqualTo(100));
            Assert.That(lab.Bans.Accounts.ContainsKey(200), Is.False);
            Assert.That(lab.Engine.CanWrite(victim), Is.True);
        });
    }

    [Test]
    public async Task UnauthenticatedInputDoesNotInventAnAccount()
    {
        var lab = new Lab();
        await lab.Engine.RecoverAsync();
        var key = lab.Engine.OpenSession(4)!.Value;
        var decision = lab.Engine.Observe(Lab.Violation(key));
        Assert.That(decision.Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(lab.Engine.CanWrite(key), Is.True);
        Assert.That(lab.Engine.SanctionCount, Is.Zero);
    }

    [TestCase(ExecutionScope.ObserveOnly, RuleQualification.TestLab)]
    [TestCase(ExecutionScope.Production, RuleQualification.TestLab)]
    [TestCase(ExecutionScope.TestLab, RuleQualification.ProductionQualified)]
    [TestCase(ExecutionScope.TestLab, RuleQualification.Unqualified)]
    public async Task QualificationMustMatchExecutionDomain(ExecutionScope scope, RuleQualification qualification)
    {
        var lab = new Lab(new() { Scope = scope }, Lab.Policy with { Qualification = qualification });
        var key = await lab.Login();
        Assert.That(lab.Engine.Observe(Lab.Violation(key)).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(lab.Engine.CanWrite(key), Is.True);
    }

    [Test]
    public async Task PacketOutsideAdmittedSelfOnlySetIsUnknown()
    {
        var lab = new Lab();
        var key = await lab.Login();
        Assert.That(lab.Engine.Observe(Lab.Violation(key) with { PacketId = 28 }).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task MissingAuditReferenceDisablesHardRule(string? audit)
    {
        var lab = new Lab(policy: Lab.Policy with { AuditReference = audit! });
        var key = await lab.Login();
        Assert.That(lab.Engine.Observe(Lab.Violation(key)).Verdict, Is.EqualTo(Verdict.Unknown));
    }

    [TestCase(-1)]
    [TestCase(256)]
    public async Task ImpossibleParserRepresentationIsBlockedAsFaultOrUnsafeInputWithoutBan(int claimed)
    {
        var lab = new Lab();
        var key = await lab.Login();
        var decision = lab.Engine.Observe(new(key, 5, claimed, Lab.Complete));
        Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Block));
        Assert.That(decision.Verdict, Is.EqualTo(Verdict.UnsafeInput));
        Assert.That(lab.Engine.CanWrite(key), Is.True);
        Assert.That(lab.Engine.PendingCount, Is.Zero);
    }

    [Test]
    public async Task OversizedServerMetadataCannotCreateUnboundedEvidence()
    {
        var lab = new Lab(policy: Lab.Policy with { AuditReference = new string('a', 1025) });
        var key = await lab.Login();
        Assert.That(lab.Engine.Observe(Lab.Violation(key)).Verdict, Is.EqualTo(Verdict.Unknown));
    }
}
