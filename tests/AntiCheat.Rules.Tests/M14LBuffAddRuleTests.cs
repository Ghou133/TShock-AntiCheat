using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M14LBuffAddRuleTests
{
    private static BusinessRuleResult Check(M14LPlayerBuffAddObservation packet, RuleInputContext? input = null,
        bool intact = true, bool? authorized = false) => M14LBuffAddRules.EvaluatePlayer(packet, input ?? Input,
            intact, scopedClientExtensionAuthorized: authorized);

    [TestCase(20)] [TestCase(24)] [TestCase(44)] [TestCase(397)] [TestCase(400)]
    public void RemotePvpTypesRemainEffectivePassWithoutEquipmentTargetRangeOrHistory(int type)
        => Pass(Check(new((Input.Session.Slot + 1) % 255, type, 216000)));

    [Test]
    public void SoleOriginalProducerCannotPublishLocalTargetAndNonPvpType()
    {
        var self = Check(new(Input.Session.Slot, 24, 600));
        Candidate(self); Assert.That(self.RuleId, Is.EqualTo(M14LBuffAddRules.PlayerRuleId));
        Candidate(Check(new((Input.Session.Slot + 1) % 255, 1, 600)));
    }

    [Test]
    public void ServerAndScopedExtensionsPassWhileIncompleteProofNeverBans()
    {
        var packet = new M14LPlayerBuffAddObservation(Input.Session.Slot, 1, 600);
        Pass(Check(packet, Input with { ClientOrigin = false })); Pass(Check(packet, authorized: true));
        Unknown(Check(packet, intact: false)); Unknown(Check(packet, authorized: null));
        Unknown(Check(packet, Input with { ParserComplete = false }));
        Unknown(Check(packet, Input with { SnapshotComplete = false }));
        Unknown(Check(packet, Input with { AttributionComplete = false }));
        Unknown(Check(packet, Input with { ExceptionsExcluded = false }));
        Unknown(Check(packet, Input with { SnapshotFingerprint = "different" }));
    }

    [TestCase(255, 24, 600)] [TestCase(1, 0, 600)] [TestCase(1, 401, 600)]
    [TestCase(1, 65535, 600)] [TestCase(1, 24, 0)] [TestCase(1, 24, -1)]
    public void InvalidFieldsAreOnlySafetyBlock(int target, int type, int time) => BlockOnly(Check(new(target, type, time)));

    [Test]
    public void ShadowFlameBoundDoesNotApplyToOtherBuffsOrCumulativeDuration()
    {
        BusinessRuleResult Npc(int type, int time, RuleInputContext? input = null, bool? authorized = false) =>
            M14LBuffAddRules.EvaluateNpc(new(M13NpcBuffOperation.Add, 3, type, time), input ?? Input,
                scopedClientExtensionAuthorized: authorized);
        Pass(Npc(153, 600)); Pass(Npc(153, 1));
        Pass(Npc(24, 19392)); Pass(Npc(24, short.MinValue));
        Candidate(Npc(153, 601)); Candidate(Npc(153, 19392));
        BlockOnly(Npc(153, 0)); BlockOnly(Npc(153, -1));
        Pass(Npc(153, 19392, Input with { ClientOrigin = false }));
        Pass(Npc(153, 19392, authorized: true)); Unknown(Npc(153, 19392, authorized: null));
        Unknown(Npc(153, 19392, Input with { ExceptionsExcluded = false }));
        Unknown(Npc(153, 19392, Input with { AttributionComplete = false }));
        Unknown(Npc(153, 19392, Input with { SnapshotComplete = false }));
    }
}
