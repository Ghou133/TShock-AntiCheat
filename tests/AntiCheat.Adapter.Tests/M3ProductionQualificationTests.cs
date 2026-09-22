using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Adapter.Tests;

[TestFixture]
public sealed class M3ProductionQualificationTests
{
    private static readonly TargetRuntimeStatus Locked = new(true, "v1.4.5.8", TargetRuntime.Fingerprint, "verified-fixture");
    private static readonly ProofContext Context = new(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true);

    [Test]
    public void OnlyReviewedExactRuleVersionsHaveProductionQualificationAndAuditReference()
    {
        var policies = M2RuleRegistry.Create(Locked, ExecutionScope.Production, ["M3.UnreviewedCandidate"]);
        var admitted = policies.Where(x => x.Qualification == RuleQualification.ProductionQualified).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(admitted.Select(x => (x.RuleId, x.Version)), Is.EquivalentTo(new[] {
                ("A01.EmojiSenderMismatch", "m2.1"), ("A02.InventorySenderMismatch", "m2.1"),
                ("NPC01.ServerDebuffDamage", "1.0.0"), ("NPC02.ServerPortalTeleport", "1.0.0"),
                ("C2.CultistRitualRole", "1.0.0"), ("C6.PortalPlacementDamage", "1.0.0"),
                ("VITAL01.RawLifeMaximum", "1.0.0"), ("VITAL02.RawManaMaximum", "1.0.0"),
                ("CONTAINER01.ChestResizeAuthority", "1.0.0"), ("B4.ContainerAuthorization", "1.1.0"),
                ("VITAL04.NonPvpHurtTarget", "1.0.0"), ("PG-NAT-012.MechdusaSummonWorld", "1.0.0"),
                ("C7.WoodenArrowDamageProjection", "1.0.0"), ("PG-NAT-121.CelestialSigilWorld", "1.0.0"),
                ("NPC03.BossPartSummonRequest", "1.0.0"), ("PG-NAT-108.GolemSummonWorld", "1.0.0"),
                ("G03.NpcBuffRemovalContract", "1.0.0"), ("NPC04.BuffStateSyncAuthority", "1.0.0"),
                ("G05.NaturalRodWorldBorder", "1.0.0"),
                ("NPC05.EventStateAuthority", "1.0.0"), ("G03.PlayerBuffAddContract", "1.0.0"),
                ("G03.NpcShadowFlameAddContract", "1.0.0"),
                ("G03.NpcShimmerAddContract", "1.0.0"), ("WORLD01.WorldAlignmentStateAuthority", "1.0.0"),
                ("NPC06.CavernMonsterStateAuthority", "1.0.0"),
                ("WORLD02.CreditsRollStateAuthority", "1.0.0"), ("PROJ01.CannonFiringAuthority", "1.0.0"),
                ("G03.NpcBuffTypeContract", "1.0.0") }));
            Assert.That(admitted.All(x => M2RuleRegistry.ProductionRules[x.RuleId] == x.Version && x.RuntimeFingerprint == TargetRuntime.Fingerprint &&
                x.AuditReference == M2RuleRegistry.ProductionAuditReference), Is.True);
            Assert.That(policies.Where(x => x.Qualification != RuleQualification.ProductionQualified)
                .All(x => x.Qualification == RuleQualification.Unqualified), Is.True);
        });
    }

    [TestCase("unverified")]
    [TestCase("other-game")]
    [TestCase("other-image")]
    [TestCase("unknown-image")]
    [TestCase("different-case")]
    public void RuntimeAdmissionCannotBeInferredFromASimilarOrUnverifiedFingerprint(string missing)
    {
        var runtime = missing switch
        {
            "unverified" => Locked with { Verified = false },
            "other-game" => Locked with { GameVersion = "v1.4.5.6" },
            "other-image" => Locked with { Fingerprint = TargetRuntime.Fingerprint + "-changed" },
            "unknown-image" => Locked with { Fingerprint = "unknown" },
            _ => Locked with { Fingerprint = TargetRuntime.Fingerprint.ToLowerInvariant() }
        };
        Assert.That(M2RuleRegistry.Create(runtime, ExecutionScope.Production)
            .All(x => x.Qualification == RuleQualification.Unqualified), Is.True);
    }

    [TestCase(SelfIdentityMessage.Emoji, ExecutionScope.Production, true)]
    [TestCase(SelfIdentityMessage.InventorySlot, ExecutionScope.Production, true)]
    [TestCase(SelfIdentityMessage.Emoji, ExecutionScope.TestLab, true)]
    [TestCase(SelfIdentityMessage.InventorySlot, ExecutionScope.TestLab, true)]
    [TestCase(SelfIdentityMessage.Emoji, ExecutionScope.ObserveOnly, false)]
    [TestCase(SelfIdentityMessage.InventorySlot, ExecutionScope.ObserveOnly, false)]
    public async Task RealRegistryModeGatePassesSelfAndOnlyAdmittedFirstMismatchRevokes(SelfIdentityMessage kind,
        ExecutionScope scope, bool shouldSanction)
    {
        var io = new MemoryEnforcement();
        var engine = await CreateEngine(scope, Locked, io);
        var sender = engine.OpenSession(7)!.Value;
        var recipient = engine.OpenSession(8)!.Value;
        engine.Authenticate(sender, 707);
        engine.Authenticate(recipient, 808);
        byte packet = kind == SelfIdentityMessage.Emoji ? (byte)120 : (byte)5;
        var legal = engine.ObserveBusiness(new(sender, packet, ProtocolRules.EvaluateIdentity(kind, 7, 7, true, true, true), Context));
        Assert.That(legal.Behavior, Is.EqualTo(ControlAction.Pass));
        Assert.That(engine.SanctionCount, Is.Zero);
        var hit = engine.ObserveBusiness(new(sender, packet, ProtocolRules.EvaluateIdentity(kind, 7, 8, true, true, true), Context));
        Assert.Multiple(() =>
        {
            Assert.That(engine.CanWrite(sender), Is.EqualTo(!shouldSanction));
            Assert.That(engine.CanWrite(recipient), Is.True);
            Assert.That(hit.Incident is not null, Is.EqualTo(shouldSanction));
            Assert.That(engine.SanctionCount, Is.EqualTo(shouldSanction ? 1 : 0));
            Assert.That(hit.Behavior, Is.EqualTo(shouldSanction ? ControlAction.Block : ControlAction.Unknown));
            Assert.That(io.Appends, Is.Zero, "First revocation must precede persistence.");
            Assert.That(io.Bans, Is.Zero);
        });
        if (shouldSanction)
        {
            Assert.That(hit.Incident!.AccountId, Is.EqualTo(707));
            Assert.That(hit.Incident.Evidence.Qualification, Is.EqualTo(scope == ExecutionScope.Production
                ? RuleQualification.ProductionQualified : RuleQualification.TestLab));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(io.Bans, Is.EqualTo(1));
            Assert.That((await engine.PumpAsync()).Attempted, Is.Zero);
        }
    }

    [TestCase("A02.GolfSenderMismatch")]
    [TestCase("B2.ActiveAccessoryConflict")]
    [TestCase("C6.WoodenArrowDamageEscalation")]
    [TestCase("C1.ProjectileAuthority")]
    [TestCase("M3.UnreviewedCandidate")]
    public async Task OtherCompleteCandidatePredicatesRemainUnqualifiedInProduction(string ruleId)
    {
        var policies = M2RuleRegistry.Create(Locked, ExecutionScope.Production, ["M3.UnreviewedCandidate"]);
        var policy = policies.Single(x => x.RuleId == ruleId);
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.Production }, RulePolicy.Disabled,
            new MemoryEnforcement(), new MemoryEnforcement(), policies);
        Assert.That(await engine.RecoverAsync(), Is.True);
        var session = engine.OpenSession(7)!.Value;
        engine.Authenticate(session, 707);
        var candidate = new BusinessRuleResult(ruleId, policy.Version, ControlAction.Block, Verdict.ProvenCheat,
            "complete-test-predicate", true, true, ImmutableDictionary<string, string>.Empty);
        Assert.That(engine.ObserveBusiness(new(session, 5, candidate, Context)).Behavior, Is.EqualTo(ControlAction.Unknown));
        Assert.That(engine.CanWrite(session), Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase("runtime")]
    [TestCase("authentication")]
    [TestCase("rule-version")]
    public async Task ProductionQualificationDoesNotSupplyMissingProofPremises(string missing)
    {
        var engine = await CreateEngine(ExecutionScope.Production, Locked, new MemoryEnforcement());
        var session = engine.OpenSession(7)!.Value;
        if (missing != "authentication") engine.Authenticate(session, 707);
        var context = missing == "runtime" ? Context with { RuntimeFingerprint = "changed-after-registration" } : Context;
        var hit = ProtocolRules.EvaluateIdentity(SelfIdentityMessage.Emoji, 7, 8, true, true, true);
        if (missing == "rule-version") hit = hit with { Version = "unreviewed" };
        Assert.That(engine.ObserveBusiness(new(session, 120, hit, context)).Behavior, Is.EqualTo(ControlAction.Unknown));
        Assert.That(engine.CanWrite(session), Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase("1.1.0", true)]
    [TestCase("1.0.0", false)]
    [TestCase("unreviewed", false)]
    public async Task ContainerPromotionKeepsUnconfirmedSwitchLegalAndOnlyReviewedFirstMismatchRevokes(string version, bool sanction)
    {
        var io = new MemoryEnforcement();
        var engine = await CreateEngine(ExecutionScope.Production, Locked, io);
        var session = engine.OpenSession(7)!.Value;
        engine.Authenticate(session, 707);
        var input = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true);
        var container = new ContainerContext(input, session, session.WorldEpoch, 1, 1, true, 40,
            44, true, false, true, true, [], false);
        var own = ContainerRules.Evaluate(new(44, 0, ContainerOperation.SlotWrite), container);
        Assert.That(engine.ObserveBusiness(new(session, 32, own, Context)).Behavior, Is.EqualTo(ControlAction.Pass));
        var switching = ContainerRules.Evaluate(new(29, 0, ContainerOperation.SlotWrite),
            container with { LeaseComplete = false, SwitchInProgress = true });
        Assert.That(engine.ObserveBusiness(new(session, 32, switching, Context)).Behavior, Is.EqualTo(ControlAction.Unknown));
        Assert.That(engine.CanWrite(session), Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
        var first = ContainerRules.Evaluate(new(29, 0, ContainerOperation.SlotWrite), container) with { Version = version };
        var decision = engine.ObserveBusiness(new(session, 32, first, Context));
        Assert.That(decision.Behavior, Is.EqualTo(sanction ? ControlAction.Block : ControlAction.Unknown));
        Assert.That(engine.CanWrite(session), Is.EqualTo(!sanction));
        Assert.That(engine.SanctionCount, Is.EqualTo(sanction ? 1 : 0));
        Assert.That(io.Bans, Is.Zero, "Synchronous revocation precedes durable punishment.");
        if (sanction)
        {
            Assert.That(decision.Incident!.Evidence.RuleVersion, Is.EqualTo("1.1.0"));
            Assert.That(decision.Incident.Evidence.Qualification, Is.EqualTo(RuleQualification.ProductionQualified));
            Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
            Assert.That(io.Bans, Is.EqualTo(1));
        }
    }

    [TestCase("projection", "1.0.0", true)]
    [TestCase("projection", "1.1.0", false)]
    [TestCase("projection", "unreviewed", false)]
    [TestCase("sigil", "1.0.0", true)]
    [TestCase("sigil", "1.1.0", false)]
    [TestCase("sigil", "unreviewed", false)]
    public async Task M7PromotionsPreserveLegalAndIncompleteContextsAndOnlyExactVersionRevokes(string mechanism, string version, bool sanction)
    {
        // This tests the registry/engine boundary with the real rule functions. The
        // production context producers and write/relay ordering have separate native
        // hook fixtures and actual NetworkLab TCP/restart evidence.
        var io = new MemoryEnforcement();
        var engine = await CreateEngine(ExecutionScope.Production, Locked, io);
        var session = engine.OpenSession(7)!.Value;
        var other = engine.OpenSession(8)!.Value;
        engine.Authenticate(session, 707); engine.Authenticate(other, 808);
        var input = new RuleInputContext(session, TargetRuntime.Fingerprint, TargetRuntime.Fingerprint, true, true, true, true);
        byte packet;
        BusinessRuleResult first;
        var controls = new List<BusinessRuleResult>();
        if (mechanism == "projection")
        {
            packet = 27;
            var observation = new ProjectileObservation(new(7, 303, 7), ProjectileOperation.Update, 1);
            var complete = new ArrowProjectionContext(input, true, true, true, 9, false);
            controls.Add(M7ArrowProjectionRules.Evaluate(observation with { Operation = ProjectileOperation.Create }, 1005,
                complete with { SameLiveEntity = false, InitialDeclarationConfirmed = false }));
            controls.Add(M7ArrowProjectionRules.Evaluate(observation, 16385, complete with { InitialWireDamage = 4 }));
            controls.Add(M7ArrowProjectionRules.Evaluate(observation, 9, complete));
            controls.Add(M7ArrowProjectionRules.Evaluate(observation, 1005, complete with { SourceException = true }));
            controls.Add(M7ArrowProjectionRules.Evaluate(observation, 1005, complete with { InitialDeclarationConfirmed = false }));
            first = M7ArrowProjectionRules.Evaluate(observation, 1005, complete);
        }
        else
        {
            packet = 61;
            var complete = new M7SigilContext(input, true, false, false, false, true);
            controls.Add(M7ProgressionRules.EvaluateSigil(7, -8, complete with { HardMode = true, GolemDefeated = true }));
            controls.Add(M7ProgressionRules.EvaluateSigil(7, -8, complete with { WorldUnchangedSinceConnection = false }));
            controls.Add(M7ProgressionRules.EvaluateSigil(7, -8, complete with { InitialSynchronization = true }));
            controls.Add(M7ProgressionRules.EvaluateSigil(7, -8, complete with { PluginContractComplete = false }));
            first = M7ProgressionRules.EvaluateSigil(7, -8, complete);
        }
        foreach (var result in controls)
        {
            Assert.That(result.Verdict is Verdict.Pass or Verdict.Unknown, Is.True);
            var decision = engine.ObserveBusiness(new(session, packet, result, Context));
            Assert.That(decision.Behavior is ControlAction.Pass or ControlAction.Unknown, Is.True);
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
        }
        Assert.That(first.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        var hit = engine.ObserveBusiness(new(session, packet, first with { Version = version }, Context));
        Assert.Multiple(() =>
        {
            Assert.That(hit.Behavior, Is.EqualTo(sanction ? ControlAction.Block : ControlAction.Unknown));
            Assert.That(engine.CanWrite(session), Is.EqualTo(!sanction));
            Assert.That(engine.CanWrite(other), Is.True);
            Assert.That(engine.SanctionCount, Is.EqualTo(sanction ? 1 : 0));
            Assert.That(io.Appends, Is.Zero, "The first complete proof revokes before durable work.");
            Assert.That(io.Bans, Is.Zero);
            Assert.That(M2RuleRegistry.Create(Locked, ExecutionScope.Production)
                .Single(x => x.RuleId == M5CombatRules.ArrowEvolutionRuleId).Qualification,
                Is.EqualTo(RuleQualification.Unqualified), "The withdrawn C6 rule is not promoted by C7 admission.");
        });
        if (!sanction) return;
        Assert.That(hit.Incident!.AccountId, Is.EqualTo(707));
        Assert.That(hit.Incident.Evidence.RuleVersion, Is.EqualTo("1.0.0"));
        Assert.That(hit.Incident.Evidence.Qualification, Is.EqualTo(RuleQualification.ProductionQualified));
        Assert.That(hit.Incident.Evidence.Preconditions.Complete, Is.True);
        Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(io.Bans, Is.EqualTo(1));
        Assert.That((await engine.PumpAsync()).Attempted, Is.Zero);
    }

    private static async Task<AntiCheatEngine> CreateEngine(ExecutionScope scope, TargetRuntimeStatus runtime, MemoryEnforcement io)
    {
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = scope }, RulePolicy.Disabled, io, io,
            M2RuleRegistry.Create(runtime, scope));
        Assert.That(await engine.RecoverAsync(), Is.True);
        return engine;
    }

    // This fixture proves the qualification/mode boundary only. Actual durable storage and
    // restart evidence remain the existing NetworkLab's real SQLite/server scenarios.
    private sealed class MemoryEnforcement : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public int Appends { get; private set; }
        public int Bans { get; private set; }
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default)
        { Appends++; return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default)
        { Bans++; return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
