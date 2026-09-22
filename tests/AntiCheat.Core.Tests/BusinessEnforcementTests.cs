using System.Collections.Immutable;
using AntiCheat.Core;
using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class BusinessEnforcementTests
{
    private static readonly BusinessRulePolicy Rule = new("M2.TestPredicate", "m2.1", "test-runtime", "m2.1",
        RuleQualification.TestLab, "tests/business-contract");
    private static readonly ProofContext Context = new("test-runtime", "m2.1", true, true, true, true);
    private static BusinessRuleResult Hit => new(Rule.RuleId, Rule.Version, ControlAction.Block, Verdict.ProvenCheat,
        "complete-candidate-predicate", true, true, ImmutableDictionary<string, string>.Empty.Add("targetId", "93"));

    private static async Task<(AntiCheatEngine Engine, SessionKey Key, MemoryJournal Journal, MemoryBans Bans)> Create(
        ExecutionScope scope = ExecutionScope.TestLab, BusinessRulePolicy? rule = null)
    {
        var journal = new MemoryJournal();
        var bans = new MemoryBans();
        var engine = new AntiCheatEngine(new ControlledClock(), new() { Scope = scope }, RulePolicy.Disabled,
            journal, bans, [rule ?? Rule]);
        Assert.That(await engine.RecoverAsync(), Is.True);
        var key = engine.OpenSession(6)!.Value;
        Assert.That(engine.Authenticate(key, 42), Is.EqualTo(AuthenticationResult.Authenticated));
        return (engine, key, journal, bans);
    }

    [Test]
    public async Task FirstCompleteBusinessProofRevokesBeforeIoAndPersistsCorrectAccountOnce()
    {
        var (engine, key, journal, bans) = await Create();
        var innocent = engine.OpenSession(7)!.Value;
        engine.Authenticate(innocent, 93); // Target id in facts is never a sanctioned account selector.
        var first = engine.ObserveBusiness(new(key, 120, Hit, Context));
        Assert.Multiple(() =>
        {
            Assert.That(first.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(first.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(engine.CanWrite(key), Is.False);
            Assert.That(engine.CanWrite(innocent), Is.True);
            Assert.That(first.Incident!.AccountId, Is.EqualTo(42));
            Assert.That(first.Incident.Evidence.RuleId, Is.EqualTo(Rule.RuleId));
            Assert.That(first.Incident.Evidence.PredicateFactsJson, Does.Contain("targetId"));
            Assert.That(journal.Appends, Is.Zero);
            Assert.That(bans.Calls, Is.Zero);
        });
        var repeated = engine.ObserveBusiness(new(key, 120, Hit, Context));
        Assert.That(repeated.Incident!.IncidentId, Is.EqualTo(first.Incident!.IncidentId));
        Assert.That((await engine.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(bans.Accounts.Keys, Is.EquivalentTo(new long[] { 42 }));
        Assert.That((await engine.PumpAsync()).Attempted, Is.Zero);
    }

    [TestCase(ExecutionScope.ObserveOnly)]
    [TestCase(ExecutionScope.Production)]
    public async Task TestLabQualificationCannotRunInOtherScope(ExecutionScope scope)
    {
        var (engine, key, _, _) = await Create(scope);
        Assert.That(engine.ObserveBusiness(new(key, 120, Hit, Context)).Behavior, Is.EqualTo(ControlAction.Unknown));
        Assert.That(engine.CanWrite(key), Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [TestCase("parse")][TestCase("client")][TestCase("attribution")][TestCase("exceptions")]
    [TestCase("runtime")][TestCase("context")][TestCase("legal")][TestCase("predicate")]
    [TestCase("prerequisites")][TestCase("registration")][TestCase("version")][TestCase("qualification")]
    public async Task MissingPremiseDoesNotBecomeBlockOrSanction(string missing)
    {
        var (engine, key, _, _) = await Create(rule: missing == "qualification" ? Rule with { Qualification = RuleQualification.Unqualified } : Rule);
        var context = missing switch
        {
            "parse" => Context with { ParseComplete = false }, "client" => Context with { ClientOrigin = false },
            "attribution" => Context with { AttributionComplete = false }, "exceptions" => Context with { ExceptionsExcluded = false },
            "runtime" => Context with { RuntimeFingerprint = "other" }, "context" => Context with { ContextVersion = "other" },
            "legal" => Context with { KnownLegalException = true }, _ => Context
        };
        var result = missing switch
        {
            "predicate" => Hit with { PredicateSatisfied = false }, "prerequisites" => Hit with { PrerequisitesComplete = false },
            "registration" => Hit with { RuleId = "unregistered" }, "version" => Hit with { Version = "unverified" }, _ => Hit
        };
        Assert.That(engine.ObserveBusiness(new(key, 120, result, context)).Behavior, Is.EqualTo(ControlAction.Unknown));
        Assert.That(engine.CanWrite(key), Is.True);
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public async Task UnauthenticatedConnectionCanBeSafetyFilteredButCannotBePermanentlyBanned()
    {
        var (engine, _, _, _) = await Create();
        var key = engine.OpenSession(8)!.Value;
        Assert.That(engine.ObserveBusiness(new(key, 120, Hit, Context)).Verdict, Is.EqualTo(Verdict.Unknown));
        var unsafeInput = Hit with { Verdict = Verdict.UnsafeInput, PredicateSatisfied = false };
        Assert.That(engine.ObserveBusiness(new(key, 120, unsafeInput, Context)).Behavior, Is.EqualTo(ControlAction.Block));
        Assert.That(engine.SanctionCount, Is.Zero);
    }

    [Test]
    public async Task OldGenerationAndWorldCannotPunishReplacement()
    {
        var (engine, key, _, _) = await Create();
        engine.Disconnect(key);
        var replacement = engine.OpenSession(key.Slot)!.Value;
        engine.Authenticate(replacement, 94);
        Assert.That(engine.ObserveBusiness(new(key, 120, Hit, Context)).Incident, Is.Null);
        Assert.That(engine.CanWrite(replacement), Is.True);
        engine.AdvanceWorld();
        Assert.That(engine.ObserveBusiness(new(replacement, 120, Hit, Context)).Incident, Is.Null);
    }

    [Test]
    public async Task ConcurrentRulesForSameAccountGenerateOneIncident()
    {
        var (engine, key, _, _) = await Create();
        var observations = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => engine.ObserveBusiness(new(key, 120, Hit, Context)))));
        Assert.That(observations.Select(x => x.Incident!.IncidentId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(engine.SanctionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SlowOrFailedStoreCannotReopenRevokedConnection()
    {
        var (engine, key, journal, bans) = await Create();
        journal.FailAppend = true; bans.Fail = true;
        engine.ObserveBusiness(new(key, 120, Hit, Context));
        var pump = await engine.PumpAsync();
        Assert.That(pump.Failed, Is.EqualTo(1));
        Assert.That(engine.CanWrite(key), Is.False);
        Assert.That(engine.IsMaintenanceMode, Is.True);
    }

    [Test]
    public async Task BusinessIntentCanRecoverWithStableSerializedFacts()
    {
        var (engine, key, journal, _) = await Create();
        var first = engine.ObserveBusiness(new(key, 120, Hit, Context));
        await journal.AppendAsync(first.Incident!);
        var restoredBans = new MemoryBans();
        var next = new AntiCheatEngine(new ControlledClock(), new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            journal, restoredBans, [Rule]);
        Assert.That(await next.RecoverAsync(), Is.True);
        Assert.That((await next.PumpAsync()).Applied, Is.EqualTo(1));
        Assert.That(restoredBans.Accounts.ContainsKey(42), Is.True);
    }

    [Test]
    public async Task OversizedFactPayloadDoesNotCreateUnpersistableProof()
    {
        var (engine, key, _, _) = await Create();
        var oversized = Hit with { Facts = Hit.Facts.Add("oversized", new string('x', 257)) };
        Assert.That(engine.ObserveBusiness(new(key, 120, oversized, Context)).Verdict, Is.EqualTo(Verdict.Unknown));
        Assert.That(engine.CanWrite(key), Is.True);
    }
}
