using System.Text.Json;
using NUnit.Framework;

namespace AntiCheat.Core.Tests;

[TestFixture]
public sealed class ScenarioReplayTests
{
    public sealed record Scenario
    {
        public required string Id { get; init; }
        public int ServerSlot { get; init; }
        public int ClaimedSlot { get; init; }
        public bool Authenticated { get; init; } = true;
        public bool ParseComplete { get; init; } = true;
        public bool ClientOrigin { get; init; } = true;
        public bool ExceptionsExcluded { get; init; } = true;
        public bool KnownLegalException { get; init; }
        public string RuntimeFingerprint { get; init; } = "test-lab-only/fixture-v1";
        public required string Expected { get; init; }
    }

    public static IEnumerable<TestCaseData> Cases()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "self-slot-scenarios.json");
        var scenarios = JsonSerializer.Deserialize<Scenario[]>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var scenario in scenarios)
            yield return new TestCaseData(scenario).SetName("Replay_" + scenario.Id);
    }

    [TestCaseSource(nameof(Cases))]
    public async Task ReplayOneActionWithExplicitModelSideEffectAndFirstSanction(Scenario scenario)
    {
        var lab = new Lab();
        await lab.Engine.RecoverAsync();
        var key = lab.Engine.OpenSession(scenario.ServerSlot)!.Value;
        if (scenario.Authenticated) lab.Engine.Authenticate(key, 100);
        var context = Lab.Complete with
        {
            RuntimeFingerprint = scenario.RuntimeFingerprint,
            ParseComplete = scenario.ParseComplete,
            ClientOrigin = scenario.ClientOrigin,
            ExceptionsExcluded = scenario.ExceptionsExcluded,
            KnownLegalException = scenario.KnownLegalException
        };
        var decision = lab.Engine.Observe(new(key, 5, scenario.ClaimedSlot, context));
        // This is deliberately a Core dispatch model, not a TShock state-write or client test.
        var modeledStateWrites = decision.Behavior == ControlAction.Block ? 0 : 1;
        await lab.Engine.PumpAsync();
        var expected = Enum.Parse<Verdict>(scenario.Expected);
        Assert.That(decision.Verdict, Is.EqualTo(expected));
        if (expected == Verdict.ProvenCheat)
        {
            Assert.That(modeledStateWrites, Is.Zero);
            Assert.That(lab.Engine.CanWrite(key), Is.False);
            Assert.That(lab.Bans.Accounts.ContainsKey(100), Is.True);
        }
        else
        {
            Assert.That(lab.Bans.Accounts, Is.Empty);
            Assert.That(lab.Engine.CanWrite(key), Is.True);
            if (expected is Verdict.Pass or Verdict.Unknown) Assert.That(modeledStateWrites, Is.EqualTo(1));
        }
    }
}
