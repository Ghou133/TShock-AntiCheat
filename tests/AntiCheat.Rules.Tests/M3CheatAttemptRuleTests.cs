using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M3CheatAttemptRuleTests
{
    private static BusinessRuleResult Evaluate(NpcServerAuthorityObservation packet, RuleInputContext? input = null,
        string version = M3CheatAttemptRules.ContractVersion) => M3CheatAttemptRules.Evaluate(packet, input ?? Input, 200, version);

    [TestCase(NpcServerAuthorityMessage.DebuffDamage)]
    [TestCase(NpcServerAuthorityMessage.PortalTeleport)]
    public void LegitimateServerAuthorityIsAllowedBeforeTestingClientImpersonation(NpcServerAuthorityMessage message)
    {
        var packet = new NpcServerAuthorityObservation(message, 4, 50, X: 400, Y: 300, VelocityY: -3);
        Pass(Evaluate(packet, Input with { ClientOrigin = false }));
        Candidate(Evaluate(packet));
    }

    [TestCase(short.MinValue)] [TestCase(0)] [TestCase(1)] [TestCase(50)] [TestCase(short.MaxValue)]
    public void AmountDoesNotSelectTheVerdictTheClientProtocolRoleDoes(int amount)
    {
        var packet = new NpcServerAuthorityObservation(NpcServerAuthorityMessage.DebuffDamage, 9, amount);
        Candidate(Evaluate(packet));
        Assert.That(Evaluate(packet).Reason, Is.EqualTo("client-issued-server-only-npc-debuff-damage"));
    }

    [Test]
    public void MalformedNonfiniteAndUnknownContextsAreNeverPromotedToAccountProof()
    {
        var packet = new NpcServerAuthorityObservation(NpcServerAuthorityMessage.PortalTeleport, 9);
        BlockOnly(Evaluate(packet with { X = float.NaN }));
        BlockOnly(Evaluate(packet with { VelocityY = float.PositiveInfinity }));
        BlockOnly(Evaluate(packet with { NpcIndex = 200 }));
        Unknown(Evaluate(packet, Input with { ParserComplete = false }));
        Unknown(Evaluate(packet, Input with { SnapshotFingerprint = "different-runtime" }));
        Unknown(Evaluate(packet, Input with { AttributionComplete = false }));
        Unknown(Evaluate(packet, version: "old-target"));
    }

    [Test]
    public void ChestResizeAllowsServerAndExplicitLimitedPermissionBeforeProvingUnauthorizedClient()
    {
        var packet = new ChestResizeObservation(1, 40);
        BusinessRuleResult Check(RuleInputContext input, bool? permitted, bool exists = true) =>
            M3CheatAttemptRules.EvaluateChestResize(packet, input, 8000, exists, permitted, M3CheatAttemptRules.ContractVersion);
        Pass(Check(Input with { ClientOrigin = false }, false));
        Pass(Check(Input, true));
        Candidate(Check(Input, false));
        Unknown(Check(Input, null));
        Unknown(Check(Input with { ExceptionsExcluded = false }, false));
        BlockOnly(Check(Input, false, false));
        BlockOnly(M3CheatAttemptRules.EvaluateChestResize(packet with { NewSize = -1 }, Input, 8000, true,
            false, M3CheatAttemptRules.ContractVersion));
    }
}
