using System.Collections.Immutable;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Progression;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture]
public sealed class ProgressionBusinessTests
{
    private const string Fingerprint = "test-lab-only/terraria-1.4.5.8/progression-fixture";
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly SessionKey Key = new(Guid.Parse("E718AC4E-FB78-4CD5-914E-01D9D8B27C2F"), 1, 4, 1);
    private static ProgressionCatalog Catalog => ProgressionCatalog.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "candidates.json"));
    private static ProgressionEntityCatalog EntityCatalog => ProgressionEntityCatalog.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "entity-candidates.json"));

    [Test]
    public void AllSixteenCandidatesHaveActualPositivePredicatesWithCompleteLabMechanisms()
    {
        var catalog = Catalog;
        Assert.That(catalog.Rules, Has.Length.EqualTo(16));
        foreach (var rule in catalog.Rules)
        {
            var result = ProgressionBusinessRules.Evaluate(rule, Complete(rule));
            Assert.Multiple(() =>
            {
                Assert.That(result.Verdict, Is.EqualTo(Verdict.ProvenCheat), rule.Id + ": " + result.Reason);
                Assert.That(result.PredicateSatisfied, Is.True, rule.Id);
                Assert.That(result.PrerequisitesComplete, Is.True, rule.Id);
                Assert.That(rule.Qualification.ProductionEligible, Is.False);
            });
        }
    }

    [TestCase(ProgressionActionKind.PassiveReceipt)]
    [TestCase(ProgressionActionKind.InventorySync)]
    public void PassivePickupAndInventoryEchoNeverProveProgression(ProgressionActionKind kind)
    {
        var rule = Catalog.Rules[0];
        Assert.That(ProgressionBusinessRules.Evaluate(rule, Complete(rule) with { Action = kind }).Action, Is.EqualTo(ControlAction.Pass));
    }

    [TestCase("exception.sourceWhitelistMatchesItem")]
    [TestCase("exception.authorizedServerGrant")]
    [TestCase("exception.legacyOrImportedAsset")]
    [TestCase("exception.passiveReceipt")]
    [TestCase("exception.otherPlayerCausedState")]
    public void EachLegalExceptionAllowsTheAction(string fact)
    {
        var rule = Catalog.Rules[0];
        Assert.That(ProgressionBusinessRules.Evaluate(rule, ChangeFact(Complete(rule), fact, true)).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void ActiveActionDoesNotReplaceMissingAcquisitionEvidence()
    {
        var rule = Catalog.Rules[0];
        var observation = Complete(rule);
        var result = ProgressionBusinessRules.Evaluate(rule, observation with
        {
            Mechanism = observation.Mechanism! with { AcquisitionPathsVerified = false }
        });
        Assert.Multiple(() =>
        {
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown));
            Assert.That(result.Action, Is.EqualTo(ControlAction.Unknown));
            Assert.That(result.PredicateSatisfied, Is.True);
            Assert.That(result.PrerequisitesComplete, Is.False);
        });
    }

    [Test]
    public void ExplicitPolicyHasDifferentPrerequisitesFromNaturalImpossibility()
    {
        var rule = Catalog.Rules.Single(x => x.Id == "PG-POL-001");
        var observation = Complete(rule);
        observation = observation with { Mechanism = observation.Mechanism! with { AcquisitionPathsVerified = false } };
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observation).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observation with
        {
            Mechanism = observation.Mechanism! with { PolicyApplicable = false }
        }).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, ChangeFact(observation, "policy.mklpSubsetEnabled", false)).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void AnyAndAllMechanicalBossPoliciesKeepDifferentBooleanMeaning()
    {
        var any = Catalog.Rules.Single(x => x.Id == "PG-POL-005");
        var all = Catalog.Rules.Single(x => x.Id == "PG-POL-006");
        var anyObservation = ChangeFact(Complete(any), "NPC.downedMechBoss1", true);
        var allObservation = ChangeFact(Complete(all), "NPC.downedMechBoss1", true);
        Assert.That(ProgressionBusinessRules.Evaluate(any, anyObservation).Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(ProgressionBusinessRules.Evaluate(all, allObservation).Verdict, Is.EqualTo(Verdict.ProvenCheat));
        allObservation = ChangeFact(ChangeFact(allObservation, "NPC.downedMechBoss2", true), "NPC.downedMechBoss3", true);
        Assert.That(ProgressionBusinessRules.Evaluate(all, allObservation).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void SameBossEventExemptsDropBeforeThePersistentFlagChanges()
    {
        var rule = Catalog.Rules.Single(x => x.Id == "PG-POL-001");
        Assert.That(ProgressionBusinessRules.Evaluate(rule, ChangeFact(Complete(rule), "currentbossdefeated", "BossDType.KingSlime")).Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, ChangeFact(Complete(rule), "currentbossdefeated", "KingSlime")).Action, Is.EqualTo(ControlAction.Unknown), "Unrecognized enum text cannot silently become a missing-boss fact.");
    }

    [TestCase("Main.drunkWorld")]
    [TestCase("Main.getGoodWorld")]
    [TestCase("Main.zenithWorld")]
    [TestCase("Main.tenthAnniversaryWorld")]
    public void EachSpecialSeedExceptionIsPreserved(string flag)
    {
        var rule = Catalog.Rules.Single(x => x.Id == "PG-NAT-002");
        Assert.That(ProgressionBusinessRules.Evaluate(rule, ChangeFact(Complete(rule), flag, true)).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void MissingExceptionFactIsUnknownRatherThanFalse()
    {
        var rule = Catalog.Rules[0];
        var observation = Complete(rule);
        observation = observation with { World = observation.World! with { Facts = observation.World.Facts.Remove("exception.authorizedServerGrant") } };
        Assert.That(ProgressionBusinessRules.Evaluate(rule, observation).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void SnapshotTransitionStalenessWorldResetAndSlotReuseInvalidateOnlyThisProof()
    {
        var rule = Catalog.Rules[0];
        var good = Complete(rule);
        var invalid = new[]
        {
            good with { World = good.World! with { TransitionSettled = false } },
            good with { World = good.World! with { CapturedUtc = Now - TimeSpan.FromSeconds(3) } },
            good with { World = good.World! with { CapturedUtc = Now + TimeSpan.FromMilliseconds(1) } },
            good with { World = good.World! with { ValidUntilUtc = Now - TimeSpan.FromMilliseconds(1) } },
            good with { World = good.World! with { WorldEpoch = 2 } },
            good with { CurrentSession = Key with { Generation = 2 } },
            good with { CurrentSession = Key with { ServerRunId = Guid.NewGuid() } },
            good with { BeforeSideEffects = false },
            good with { ActiveActionAttributed = false },
            good with { Authenticated = false },
            good with { ParseComplete = false },
            good with { ClientOrigin = false }
        };
        foreach (var observation in invalid)
            Assert.That(ProgressionBusinessRules.Evaluate(rule, observation).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void PriorAcceptedStateAuthorizedGrantAndOtherPlayerChangesAreNotProofs()
    {
        var rule = Catalog.Rules[0];
        var good = Complete(rule);
        foreach (var observation in new[] { good with { IsSynchronization = true }, good with { AuthorizedServerAction = true }, good with { OtherPlayerCausedAction = true } })
            Assert.That(ProgressionBusinessRules.Evaluate(rule, observation).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public void VersionSeedItemActionAndAuditContractCannotBeBorrowedFromAnotherContext()
    {
        var rule = Catalog.Rules[0];
        var good = Complete(rule);
        var mechanism = good.Mechanism!;
        var invalid = new[]
        {
            mechanism with { TerrariaVersion = "1.4.5.6" }, mechanism with { RuntimeFingerprint = "other" },
            mechanism with { WorldSeedSignature = "other-seed" }, mechanism with { AuditReference = "" },
            mechanism with { AuditReference = new string('a', 257) }, mechanism with { CoveredItems = [9999] },
            mechanism with { CoveredActions = [ProgressionActionKind.PlaceWall] }, mechanism with { RuleId = "other-rule" },
            mechanism with { RuleVersion = "old" }, mechanism with { LegalExceptionsComplete = false }
        };
        foreach (var contract in invalid)
            Assert.That(ProgressionBusinessRules.Evaluate(rule, good with { Mechanism = contract }).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(ProgressionBusinessRules.Evaluate(rule, good with { TerrariaVersion = "1.4.6.8" }).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void EntityCatalogUsesRealPredicatesAndDistinctTypeNamespaces()
    {
        var catalog = EntityCatalog;
        Assert.That(catalog.Rules, Has.Length.EqualTo(8));
        foreach (var entity in catalog.Rules)
        {
            var observation = CompleteEntity(entity);
            Assert.That(ProgressionBusinessRules.Evaluate(entity, observation).Verdict, Is.EqualTo(Verdict.ProvenCheat), entity.Rule.Id);
            Assert.That(ProgressionBusinessRules.Evaluate(entity.Rule, observation).Action, Is.EqualTo(ControlAction.Pass), "Entity types must not enter the item API.");
            Assert.That(ProgressionBusinessRules.Evaluate(entity, observation with { SubjectKind = ProgressionSubjectKind.Item }).Action, Is.EqualTo(ControlAction.Pass));
        }
    }

    [Test]
    public void TileStyleRequiresExactRuleMatchAndExactMechanismCoverage()
    {
        var entity = EntityCatalog.Rules.Single(x => x.Rule.Id == "PG-TILE-POL-001");
        var observation = CompleteEntity(entity);
        Assert.That(ProgressionBusinessRules.Evaluate(entity, observation with { Style = 0 }).Action, Is.EqualTo(ControlAction.Pass));
        Assert.That(ProgressionBusinessRules.Evaluate(entity, observation with { Style = null }).Action, Is.EqualTo(ControlAction.Unknown));
        Assert.That(ProgressionBusinessRules.Evaluate(entity, observation with
        {
            Mechanism = observation.Mechanism! with { CoveredTargets = [] }
        }).Action, Is.EqualTo(ControlAction.Unknown));
    }

    [Test]
    public void ServerOrEnvironmentProjectileCreationAndWallSeedExceptionAreLegal()
    {
        var projectile = EntityCatalog.Rules.Single(x => x.Rule.Id == "PG-PRJ-NAT-001");
        Assert.That(ProgressionBusinessRules.Evaluate(projectile, ChangeFact(CompleteEntity(projectile), "exception.serverOrEnvironmentalCreation", true)).Action, Is.EqualTo(ControlAction.Pass));
        var wall = EntityCatalog.Rules.Single(x => x.Rule.Id == "PG-WALL-POL-004");
        Assert.That(ProgressionBusinessRules.Evaluate(wall, ChangeFact(CompleteEntity(wall), "Main.remixWorld", true)).Action, Is.EqualTo(ControlAction.Pass));
    }

    [Test]
    public async Task FirstProvenEventFromProgressionSynchronouslyRevokesThenPersistsOneAccountInTestLab()
    {
        var rule = Catalog.Rules[0];
        var store = new ProgressionTestStore();
        var engine = Engine(rule, store, ExecutionScope.TestLab);
        Assert.That(await engine.RecoverAsync(), Is.True);
        var key = engine.OpenSession(4)!.Value;
        Assert.That(engine.Authenticate(key, 100), Is.EqualTo(AuthenticationResult.Authenticated));
        var innocent = engine.OpenSession(5)!.Value;
        engine.Authenticate(innocent, 101);
        var observation = Complete(rule, key);
        var result = ProgressionBusinessRules.Evaluate(rule, observation);
        var decision = engine.ObserveBusiness(new(key, 13, result, Proof));
        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(engine.CanWrite(key), Is.False);
            Assert.That(engine.CanWrite(innocent), Is.True);
            Assert.That(store.Banned, Is.Empty, "No I/O in synchronous verdict path.");
        });
        var duplicate = engine.ObserveBusiness(new(key, 13, result, Proof));
        Assert.That(duplicate.Incident!.IncidentId, Is.EqualTo(decision.Incident!.IncidentId));
        Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(store.Banned, Is.EquivalentTo(new long[] { 100 }));
        Assert.That(store.Appends, Is.EqualTo(1));
        engine.Disconnect(key);
        var rejoin = engine.OpenSession(4)!.Value;
        Assert.That(engine.Authenticate(rejoin, 100), Is.EqualTo(AuthenticationResult.AccountBlocked));
    }

    [Test]
    public async Task TestLabCandidateCannotBePromotedByProductionScopeOrCompleteFacts()
    {
        var rule = Catalog.Rules[0];
        var store = new ProgressionTestStore();
        var engine = Engine(rule, store, ExecutionScope.Production);
        Assert.That(await engine.RecoverAsync(), Is.True);
        var key = engine.OpenSession(4)!.Value;
        engine.Authenticate(key, 100);
        var candidate = ProgressionBusinessRules.Evaluate(rule, Complete(rule, key));
        Assert.That(candidate.Verdict, Is.EqualTo(Verdict.ProvenCheat), "Business predicate is implemented independently of admission.");
        var decision = engine.ObserveBusiness(new(key, 13, candidate, Proof));
        Assert.Multiple(() =>
        {
            Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Unknown));
            Assert.That(engine.CanWrite(key), Is.True);
            Assert.That(engine.PendingCount, Is.Zero);
            Assert.That(store.Banned, Is.Empty);
        });
    }

    private static readonly ProofContext Proof = new(Fingerprint, "progression-fixture-v1", true, true, true, true);
    private static AntiCheatEngine Engine(ProgressionRule rule, ProgressionTestStore store, ExecutionScope scope)
        => new(TimeProvider.System, new() { Scope = scope }, RulePolicy.Disabled, store, store,
            [new(rule.Id, ProgressionBusinessRules.Version, Fingerprint, "progression-fixture-v1", RuleQualification.TestLab, "tests/ProgressionBusinessTests")]);

    private static ProgressionActionObservation Complete(ProgressionRule rule, SessionKey? key = null)
    {
        var session = key ?? Key;
        var facts = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        Collect(rule.Condition, facts); Collect(rule.Exceptions, facts);
        return new(session, rule.Items[0], ProgressionActionKind.UseItem)
        {
            CurrentSession = session, RuntimeFingerprint = Fingerprint, TerrariaVersion = "1.4.5.8", NowUtc = Now,
            ParseComplete = true, Authenticated = true, ClientOrigin = true, ActiveActionAttributed = true, BeforeSideEffects = true,
            World = new(session.WorldEpoch, Fingerprint, "world-fixture-v1", "ordinary-seed-fixture", Now, Now + TimeSpan.FromSeconds(1), true, true, facts.ToImmutable()),
            Mechanism = new(rule.Id, ProgressionBusinessRules.Version, "1.4.5.8", Fingerprint, "ordinary-seed-fixture",
                "test-lab-mechanism-fixture-not-a-live-acquisition-audit", rule.Items.ToImmutableHashSet(),
                [ProgressionActionKind.UseItem, ProgressionActionKind.Equip], true, true, true)
        };
    }

    private static ProgressionActionObservation CompleteEntity(ProgressionEntityRule entity)
    {
        var target = entity.Targets[0];
        var kind = entity.SubjectKind switch
        {
            ProgressionSubjectKind.Projectile => ProgressionActionKind.CreateProjectile,
            ProgressionSubjectKind.Tile => ProgressionActionKind.PlaceTile,
            _ => ProgressionActionKind.PlaceWall
        };
        var observation = Complete(entity.Rule) with { SubjectKind = entity.SubjectKind, Style = target.Style, Action = kind, ItemId = target.TypeId };
        return observation with { Mechanism = observation.Mechanism! with { SubjectKind = entity.SubjectKind, CoveredActions = [kind], CoveredTargets = entity.Targets.ToImmutableHashSet() } };
    }

    private static void Collect(Condition condition, ImmutableDictionary<string, JsonElement>.Builder facts)
    {
        if (condition.Op == "fact") facts[condition.Name!] = condition.Expected.ValueKind == JsonValueKind.String
            ? JsonSerializer.SerializeToElement("BossDType.NA") : JsonSerializer.SerializeToElement(condition.Name!.StartsWith("policy.", StringComparison.Ordinal));
        if (condition.Arg is not null) Collect(condition.Arg, facts);
        foreach (var child in condition.Args) Collect(child, facts);
    }

    private static ProgressionActionObservation ChangeFact<T>(ProgressionActionObservation observation, string key, T value)
        => observation with { World = observation.World! with { Facts = observation.World.Facts.SetItem(key, JsonSerializer.SerializeToElement(value)) } };

    private sealed class ProgressionTestStore : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        private readonly Dictionary<Guid, BanIntent> pending = [];
        internal HashSet<long> Banned { get; } = [];
        internal int Appends { get; private set; }
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>(pending.Values.ToArray());
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) { pending.TryAdd(intent.IncidentId, intent); Appends++; return ValueTask.CompletedTask; }
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) { pending.Remove(incidentId); return ValueTask.CompletedTask; }
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Banned.Add(intent.AccountId); return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
