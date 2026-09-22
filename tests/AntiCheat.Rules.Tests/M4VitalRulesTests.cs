using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class M4VitalRulesTests
{
    private static M4VitalContext Context(M4VitalKind kind) => new(Input, M4VitalRules.ContractVersion,
        true, false, true, false, kind == M4VitalKind.Life ? M4VitalRules.BaselineLifeMaximum : M4VitalRules.BaselineManaMaximum, true);
    private static M4VitalObservation Claim(M4VitalKind kind, int raw, int current = 1) => new(kind, Actor.Slot, current, raw);

    [TestCase(M4VitalKind.Life, 500, 600)] // Lifeforce affects Max2, not the wire's raw maximum.
    [TestCase(M4VitalKind.Mana, 200, 400)] // Equipment can raise effective mana.
    [TestCase(M4VitalKind.Life, 40, 40)] // Target's surviveHardcoreDeath may reduce raw life below 100.
    [TestCase(M4VitalKind.Life, 0, 0)]
    [TestCase(M4VitalKind.Mana, 0, 0)] // SSC StartingMana may be zero.
    [TestCase(M4VitalKind.Life, 499, 300)] // No discrete-upgrade assumption about imported/SSC values.
    [TestCase(M4VitalKind.Life, 501, 400)] // Imported noncanonical raw values are not standalone actor proof.
    [TestCase(M4VitalKind.Life, 504, 400)]
    [TestCase(M4VitalKind.Mana, 201, 100)]
    [TestCase(M4VitalKind.Mana, 219, 100)]
    public void LegalRawBoundariesNeverCompareCurrentToRawMaximum(M4VitalKind kind, int raw, int current) =>
        Pass(M4VitalRules.Evaluate(Claim(kind, raw, current), Context(kind)));

    [TestCase(M4VitalKind.Life, 500, 500)]
    [TestCase(M4VitalKind.Life, 499, 504)]
    [TestCase(M4VitalKind.Life, 1, 501)]
    [TestCase(M4VitalKind.Life, 1000, 1000)]
    [TestCase(M4VitalKind.Mana, 200, 200)]
    [TestCase(M4VitalKind.Mana, 199, 219)]
    [TestCase(M4VitalKind.Mana, 201, 201)]
    public void ServerExportHasFiniteNativeReachabilityCeiling(M4VitalKind kind, int raw, int upper) =>
        Assert.That(M4VitalRules.NativeUpperBoundAfterServerExport(kind, raw), Is.EqualTo(upper));

    [TestCase(M4VitalKind.Life, 504)]
    [TestCase(M4VitalKind.Mana, 219)]
    public void BoundedServerEchoAllowsItsEnvelopeButNotTheNextValue(M4VitalKind kind, int ceiling)
    {
        var context = Context(kind) with { MaximumFromServerExports = ceiling };
        Pass(M4VitalRules.Evaluate(Claim(kind, ceiling), context));
        Candidate(M4VitalRules.Evaluate(Claim(kind, ceiling + 1), context));
    }

    [TestCase(M4VitalKind.Life, 505)]
    [TestCase(M4VitalKind.Mana, 220)]
    public void FirstCompleteRawMaximumProofIsCandidate(M4VitalKind kind, int raw) =>
        Candidate(M4VitalRules.Evaluate(Claim(kind, raw), Context(kind)));

    [TestCase("history")][TestCase("sync")][TestCase("plugins")][TestCase("permission")]
    [TestCase("health")][TestCase("runtime")][TestCase("exceptions")][TestCase("contract")]
    public void MissingPremiseCannotBeRepairedByRepeatedClaims(string missing)
    {
        var context = Context(M4VitalKind.Life);
        context = missing switch
        {
            "history" => context with { HistoryFromConnectionStart = false },
            "sync" => context with { Synchronizing = true },
            "plugins" => context with { KnownPluginSet = false },
            "permission" => context with { ScopedPermission = true },
            "health" => context with { ServerExportHistoryHealthy = false },
            "runtime" => context with { Input = Input with { SnapshotFingerprint = "other" } },
            "exceptions" => context with { Input = Input with { ExceptionsExcluded = false } },
            _ => context with { ContractVersion = "old-contract" }
        };
        for (int index = 0; index < 4; index++) Unknown(M4VitalRules.Evaluate(Claim(M4VitalKind.Life, 1000), context));
    }

    [Test]
    public void ClaimedOtherPlayerAndNegativeDeathValuesDoNotProveVitalCheating()
    {
        Unknown(M4VitalRules.Evaluate(Claim(M4VitalKind.Life, 1000) with { ClaimedSlot = Actor.Slot + 1 }, Context(M4VitalKind.Life)));
        Unknown(M4VitalRules.Evaluate(Claim(M4VitalKind.Life, 500, -30), Context(M4VitalKind.Life)));
    }

    [Test]
    public void PermanentUnlockBooleansIncludingReservedBitNeverProveAcquisition()
    {
        for (int flags = 0; flags < 256; flags++)
            Unknown(M4VitalRules.Evaluate(new(M4VitalKind.PermanentUnlocks, Actor.Slot, UnlockFlags: (byte)flags),
                Context(M4VitalKind.PermanentUnlocks)));
    }
}
