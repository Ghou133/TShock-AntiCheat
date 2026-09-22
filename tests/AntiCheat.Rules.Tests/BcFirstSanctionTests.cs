using System.Collections.Immutable;
using AntiCheat.Core;
using NUnit.Framework;
using static AntiCheat.Rules.Tests.BusinessTestData;

namespace AntiCheat.Rules.Tests;

/// <summary>Actual B/C predicates through the shared sanction gateway; network/parser coverage is separate.</summary>
[TestFixture]
public sealed class BcFirstSanctionTests
{
    [TestCase("item-stack")]
    [TestCase("equipment-conflict")]
    [TestCase("equipment-unlock")]
    [TestCase("container-target")]
    [TestCase("projectile-owner")]
    [TestCase("projectile-source")]
    [TestCase("buff-target")]
    [TestCase("healing-bound")]
    [TestCase("weapon-source")]
    [TestCase("weapon-cooldown")]
    public async Task FirstProvenEventRevokesActorBeforeIoAndLeavesRecipientWritable(string scenario)
    {
        var store = new Store();
        var template = Evaluate(scenario, Actor, Actor with { Slot = 8 }, violation: true);
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, [new(template.RuleId, template.Version, Fingerprint, "bc-first-event-fixture",
                RuleQualification.TestLab, "tests/BcFirstSanctionTests")]);
        Assert.That(await engine.RecoverAsync(), Is.True);
        var actor = engine.OpenSession(7)!.Value;
        var recipient = engine.OpenSession(8)!.Value;
        Assert.That(engine.Authenticate(actor, 71), Is.EqualTo(AuthenticationResult.Authenticated));
        Assert.That(engine.Authenticate(recipient, 81), Is.EqualTo(AuthenticationResult.Authenticated));
        var proof = new ProofContext(Fingerprint, "bc-first-event-fixture", true, true, true, true);

        var legal = engine.ObserveBusiness(new(actor, 120, Evaluate(scenario, actor, recipient, violation: false), proof));
        Assert.That(legal.Behavior, Is.EqualTo(ControlAction.Pass), legal.Reason);
        Assert.That(engine.CanWrite(actor), Is.True);
        var first = engine.ObserveBusiness(new(actor, 120, Evaluate(scenario, actor, recipient, violation: true), proof));
        Assert.Multiple(() =>
        {
            Assert.That(first.Verdict, Is.EqualTo(Verdict.ProvenCheat), first.Reason);
            Assert.That(first.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(engine.CanWrite(actor), Is.False);
            Assert.That(engine.CanWrite(recipient), Is.True);
            Assert.That(first.Incident!.AccountId, Is.EqualTo(71));
            Assert.That(first.Incident.Evidence.Session, Is.EqualTo(actor));
            Assert.That(store.Banned, Is.Empty, "Synchronous decision cannot wait for database I/O.");
        });
        var sameTickFollowup = engine.ObserveBusiness(new(actor, 120, Evaluate(scenario, actor, recipient, violation: false), proof));
        Assert.That(sameTickFollowup.Behavior, Is.EqualTo(ControlAction.Block));
        Assert.That(sameTickFollowup.Incident!.IncidentId, Is.EqualTo(first.Incident!.IncidentId));
        Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(store.Banned, Is.EquivalentTo(new long[] { 71 }));
        Assert.That(store.Appends, Is.EqualTo(1));
        engine.Disconnect(actor);
        var rejoined = engine.OpenSession(7)!.Value;
        Assert.That(engine.Authenticate(rejoined, 71), Is.EqualTo(AuthenticationResult.AccountBlocked));
        Assert.That(engine.CanWrite(recipient), Is.True);
    }

    private static BusinessRuleResult Evaluate(string scenario, SessionKey actor, SessionKey target, bool violation)
    {
        var input = Input with { Session = actor };
        return scenario switch
        {
            "item-stack" => InventoryRules.Evaluate(new(1, violation ? 10000 : 9999, 0, 5, ItemLocation.Inventory),
                new(input, 990, false, true, false),
                new(Fingerprint, "fixture", -48, 10000, 256,
                    ImmutableDictionary<int, ItemTypeDefinition>.Empty.Add(1, new(1, 9999, ImmutableHashSet.Create(0), true)), true)),
            "equipment-conflict" => EquipmentRules.Evaluate(new(4, violation ? 20 : 10, EquipmentSlotKind.FunctionalAccessory, 0),
                new(input, actor, 0, ImmutableArray.Create(new EquipmentSlot(3, 20, EquipmentSlotKind.FunctionalAccessory, 0)),
                    ImmutableHashSet.Create(3, 4), true, false, false, true, EffectiveSnapshotComplete: true), EquipmentTable()),
            "equipment-unlock" => EquipmentRules.Evaluate(new(9, 10, EquipmentSlotKind.FunctionalAccessory, 0),
                new(input, actor, 0, [], ImmutableHashSet.Create(3, 4), true, false, false, violation, EffectiveSnapshotComplete: true), EquipmentTable()),
            "container-target" => ContainerRules.Evaluate(new(violation ? 11 : 10, 1, ContainerOperation.SlotWrite),
                new(input, actor, actor.WorldEpoch, 1, 1, true, 40, 10, true, false, true, true, [], true)),
            "projectile-owner" => ProjectileRules.EvaluateAuthority(new(new(violation ? target.Slot : actor.Slot, 9, 2), ProjectileOperation.Create, 15),
                new(input, actor, actor.WorldEpoch, 1, 1, null, true, true, false, false, false, 1000, 2000)),
            "projectile-source" => ProjectileRules.EvaluateSource(new(new(actor.Slot, 9, 2), ProjectileOperation.Create, violation ? 16 : 15),
                new(input, true, false, true, null, null, false),
                new(Fingerprint, "fixture", ImmutableDictionary<int, ProjectileCategory>.Empty
                    .Add(15, new(ProjectileSourceConstraint.Allowed, [], []))
                    .Add(16, new(ProjectileSourceConstraint.ServerOnly, [], [])), true)),
            "buff-target" => CombatRules.EvaluateBuff(new(target.Slot, violation ? 2 : 1, 600),
                new(input, new(target, true, true), target, true, false, true),
                new(Fingerprint, "fixture", 400, ImmutableDictionary<int, BuffDefinition>.Empty
                    .Add(1, new(600, false, false)).Add(2, new(600, true, false)), true)),
            "healing-bound" => CombatRules.EvaluateHeal(new(target.Slot, violation ? 21 : 20),
                new(input, new(target, true, true), target, true, false, true),
                new(Fingerprint, "fixture", true, actor, target, 20, true)),
            "weapon-source" or "weapon-cooldown" => CombatRules.EvaluateAttack(new(10,
                    scenario == "weapon-source" && violation ? 30 : 20, scenario == "weapon-cooldown" && violation ? 99 : 100),
                new(input, actor, true, false, false, true, 100),
                new(Fingerprint, "fixture", ImmutableDictionary<int, ImmutableHashSet<int>>.Empty.Add(10, ImmutableHashSet.Create(20)), true, true)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static EquipmentCatalog EquipmentTable() => new(Fingerprint, "fixture", ImmutableHashSet.Create(10, 20), [], true, true);
    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
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
