using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture, NonParallelizable]
public sealed class ClientScenarioTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";
    private static readonly string CandidateId = new('A', 64);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T10:20:30Z");

    [Test]
    public void CatalogUsesThreeExactExistingRunnerSelections()
    {
        var entries = ScenarioCatalog.All.Where(s => s.IsClient).ToArray();
        Assert.That(entries.Select(s => s.Id), Is.EquivalentTo(new[] {
            ScenarioId.ClientLoadoutCycle, ScenarioId.ClientQuickStack, ScenarioId.ClientHotbarCycle }));
        foreach (var entry in entries)
        {
            Assert.That(entry.Script, Is.EqualTo("scripts/Invoke-ClientScenario.ps1"));
            Assert.That(entry.FixedArguments, Is.EqualTo(new[] { "-ScenarioId", entry.Id.ToString() }));
            Assert.That(entry.TimeoutSeconds, Is.EqualTo(30));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ABCDEF0123456789abcdef0123456789")]
    [TestCase("../0123456789abcdef0123456789ab")]
    public void ClientRequiresAnExactAuditedSessionId(string? session)
    {
        Assert.Throws<DevProblem>(() => DevOperations.ValidateParameters(ScenarioCatalog.Get(ScenarioId.ClientQuickStack),
            new JobParameters { ClientSessionId = session }));
    }

    [Test]
    public void ClientParametersNeverBecomeASecondTcpControlChannel()
    {
        Assert.DoesNotThrow(() => DevOperations.ValidateParameters(ScenarioCatalog.Get(ScenarioId.ClientQuickStack),
            new JobParameters { ClientSessionId = SessionId }));
        Assert.Throws<DevProblem>(() => DevOperations.ValidateParameters(ScenarioCatalog.Get(ScenarioId.TcpQuickStack),
            new JobParameters { ClientSessionId = SessionId }));
        Assert.Throws<DevProblem>(() => DevOperations.ValidateParameters(ScenarioCatalog.Get(ScenarioId.ClientQuickStack),
            new JobParameters { ClientSessionId = SessionId, ConnectionPacingMilliseconds = 200 }));
    }

    [Test]
    public async Task DedupReusesUncertainOutcomeAndRejectsChangedSession()
    {
        using var lab = CreateProject();
        await using var operations = new DevOperations(lab.Root);
        // Admission fails on missing candidate metadata before a runner can launch. Its identity
        // remains durable, and no second attempt is created for this same intended transfer.
        var first = await operations.JobStart(ScenarioId.ClientQuickStack, CandidateId,
            new() { ClientSessionId = SessionId }, "client-uncertain-transfer");
        var duplicate = await operations.JobStart(ScenarioId.ClientQuickStack, CandidateId,
            new() { ClientSessionId = SessionId }, "client-uncertain-transfer");
        Assert.That(duplicate.Data["job_id"]?.ToString(), Is.EqualTo(first.Data["job_id"]?.ToString()));
        Assert.That(duplicate.Data["duplicate_request"]?.GetValue<bool>(), Is.True);
        var changed = await operations.JobStart(ScenarioId.ClientQuickStack, CandidateId,
            new() { ClientSessionId = new string('b', 32) }, "client-uncertain-transfer");
        Assert.That(changed.Error, Is.EqualTo("deduplication_conflict"));
        lab.Record("dedup", new { first, duplicate, changed });
    }

    [Test]
    public void PassingResultRequiresActualActionsNamedAssertionsAndCleanup()
    {
        Assert.That(DevOperations.ValidateClientResult(Result(), Job()), Is.Null);
        foreach (string missing in new[] { "executedActions", "assertions", "before", "after", "cleanup", "sessionId", "candidateId", "scenarioId" })
        {
            var result = Result();
            result.Remove(missing);
            Assert.That(DevOperations.ValidateClientResult(result, Job()), Is.Not.Null, missing);
        }
        var zero = Result(); zero["executedActions"] = 0;
        Assert.That(DevOperations.ValidateClientResult(zero, Job()), Is.Not.Null);
        var failed = Result(); failed["assertions"]![0]!["passed"] = false;
        Assert.That(DevOperations.ValidateClientResult(failed, Job()), Is.Not.Null);
        var unnamed = Result(); unnamed["assertions"]![0]!["name"] = "";
        Assert.That(DevOperations.ValidateClientResult(unnamed, Job()), Is.Not.Null);
    }

    [TestCase("unknown")]
    [TestCase("failed")]
    [TestCase("blocked")]
    public void SideEffectOutcomeCannotPassOrSuggestRetryWhenExecutorDidNotPass(string status)
    {
        var result = Result(); result["status"] = status;
        result["failure"] = "Input was sent once; transfer acknowledgement was not observed.";
        var job = Job();
        string? problem = DevOperations.ValidateClientResult(result, job);
        Assert.That(problem, Does.Contain(status).And.Contain("No automatic retry"));
        Assert.That(job.Triggered, Is.True);
    }

    [Test]
    public void FreshServerStateSelectsOnlyAuditedPlayerAndMarksUiUnknown()
    {
        var selected = DevOperations.SelectClientState(State(Now), Session(), Now);
        Assert.That(selected["status"]?.ToString(), Is.EqualTo("ok"));
        Assert.That(selected["player"]?["accountId"]?.GetValue<int>(), Is.EqualTo(7));
        Assert.That(selected["client_ui"]?["status"]?.ToString(), Is.EqualTo("unknown"));
        Assert.That(selected["player"]?["chatText"], Is.Null);
    }

    [TestCase(-1501)]
    [TestCase(251)]
    public void StaleOrFutureSnapshotCannotSupplyCurrentState(int delta)
    {
        var error = Assert.Throws<DevProblem>(() => DevOperations.SelectClientState(State(Now.AddMilliseconds(delta)), Session(), Now));
        Assert.That(error!.Code, Is.EqualTo("client_state_stale"));
    }

    [Test]
    public void SessionWorldAndAccountGenerationCannotBeReused()
    {
        foreach (string field in new[] { "serverSessionId", "worldId" })
        {
            var changed = State(Now); changed[field] = "different";
            Assert.Throws<DevProblem>(() => DevOperations.SelectClientState(changed, Session(), Now));
        }
        var reconnect = State(Now); reconnect["players"]![0]!["sessionGeneration"] = 10;
        Assert.That(Assert.Throws<DevProblem>(() => DevOperations.SelectClientState(reconnect, Session(), Now))!.Code,
            Is.EqualTo("client_player_unknown"));
    }

    [TestCase("docs/state.json", "client_state_outside_isolation")]
    [TestCase(".lab/../state.json", "unsafe_path")]
    [TestCase("C:/not-this-project/state.json", "outside_project")]
    public async Task StatePathMustRemainInAuditedIsolation(string path, string error)
    {
        using var lab = CreateProject();
        var session = Session(); session["serverStatePath"] = path;
        Write(lab, ".lab/devmcp/client-sessions/" + SessionId + ".json", session);
        await using var operations = new DevOperations(lab.Root);
        var reply = await operations.ClientState(SessionId);
        Assert.That(reply.Error, Is.EqualTo(error));
        lab.Record("path-rejection", reply);
    }

    [Test]
    public async Task SnapshotReadRejectsReparsePointAndActualOversize()
    {
        using var lab = CreateProject();
        var session = Session();
        Write(lab, ".lab/devmcp/client-sessions/" + SessionId + ".json", session);
        Write(lab, ".lab/state.json", State(DateTimeOffset.UtcNow));
        await using var operations = new DevOperations(lab.Root);
        Assert.That((await operations.ClientState(SessionId)).Error, Is.Null);
        File.WriteAllText(lab.File(".lab/state.json"), new string(' ', DevOperations.ClientStateMaximumBytes + 1));
        Assert.That((await operations.ClientState(SessionId)).Error, Is.Not.Null);
        Directory.CreateDirectory(lab.File("outside"));
        NativeTestLinks.CreateJunction(lab.File(".lab/link"), lab.File("outside"));
        session["serverStatePath"] = ".lab/link/state.json";
        Write(lab, ".lab/devmcp/client-sessions/" + SessionId + ".json", session);
        var reply = await operations.ClientState(SessionId);
        Assert.That(reply.Error, Is.EqualTo("reparse_point"));
        lab.Record("pinned-state-read", reply);
    }

    [Test]
    public async Task CandidateAliasStillRequiresAllRecordedHashes()
    {
        using var lab = CreateProject();
        string[] names = ["AntiCheat.Plugin.TShock.dll", "AntiCheat.Core.dll", "AntiCheat.Persistence.dll", "AntiCheat.Progression.dll", "AntiCheat.Rules.dll"];
        JsonArray Group(string[] values, bool named = false) => new(values.Select(name =>
        {
            string relative = ".lab/candidate/" + name;
            Write(lab, relative, new JsonObject { ["fixture"] = name });
            var row = Pointer(relative); if (named) row["name"] = name;
            return (JsonNode)row;
        }).ToArray());
        JsonObject Pointer(string path) => new() { ["path"] = path,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lab.File(path)))) };
        var freeze = new JsonObject { ["schemaVersion"] = 1, ["targetTerraria"] = "1.4.5.8", ["protocol"] = 326,
            ["assemblies"] = Group(names, true), ["runtimeFiles"] = Group(["TShock.Server.exe", "TShockAPI.dll", "TerrariaServer.dll", "OTAPI.dll", "OTAPI.Runtime.dll"]),
            ["progressionData"] = Group(["candidates.json", "entity-candidates.json"]), ["runtimeLock"] = Pointer("docs/target-runtime-lock.json") };
        Write(lab, ".lab/freeze.json", freeze);
        Write(lab, "artifacts/index.json", new JsonObject { ["candidate"] = Pointer(".lab/freeze.json") });
        Write(lab, "manifest.json", new JsonObject { ["current_status"] = new JsonObject {
            ["source_frozen_plugin_sha256"] = freeze["assemblies"]![0]!["sha256"]!.DeepClone(), ["final_evidence"] = Pointer("artifacts/index.json") } });
        await using var operations = new DevOperations(lab.Root);
        var method = typeof(DevOperations).GetMethod("CurrentCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.DoesNotThrow(() => method.Invoke(operations, [true, null]));
        File.AppendAllText(lab.File(".lab/candidate/AntiCheat.Core.dll"), "changed");
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(operations, [true, null]));
        Assert.That(error!.InnerException, Is.TypeOf<DevProblem>());
        Assert.That(((DevProblem)error.InnerException!).Code, Is.EqualTo("candidate_bytes_changed"));
        lab.Record("candidate-alias", new { aliasAccepted = true, changedHashRejected = true });
    }

    private static NativeTestDirectory CreateProject()
    {
        var lab = new NativeTestDirectory();
        File.WriteAllText(lab.File("AGENTS.md"), "Owned client tool test fixture.");
        Write(lab, "docs/target-runtime-lock.json", new JsonObject());
        return lab;
    }

    private static void Write(NativeTestDirectory lab, string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lab.File(path))!);
        File.WriteAllText(lab.File(path), value.ToJsonString());
    }

    private static JobRecord Job() => new() { ScenarioId = "ClientQuickStack", CandidateId = CandidateId, ClientSessionId = SessionId };
    private static JsonObject Result() => new() { ["schemaVersion"] = 1, ["status"] = "passed", ["scenarioId"] = "ClientQuickStack",
        ["candidateId"] = CandidateId, ["sessionId"] = SessionId, ["executedActions"] = 1,
        ["assertions"] = new JsonArray(new JsonObject { ["name"] = "transfer-observed", ["passed"] = true }),
        ["before"] = new JsonObject(), ["after"] = new JsonObject(), ["failure"] = null,
        ["cleanup"] = new JsonObject { ["inputReleased"] = true, ["externalClientPreserved"] = true } };
    private static JsonObject Session() => new() { ["schemaVersion"] = 1, ["sessionId"] = SessionId, ["candidateId"] = CandidateId,
        ["serverStatePath"] = ".lab/state.json", ["serverSessionId"] = "server-1", ["worldId"] = 44,
        ["playerName"] = "Audited", ["accountId"] = 7, ["sessionGeneration"] = 9 };
    private static JsonObject State(DateTimeOffset utc) => new() { ["schemaVersion"] = 1, ["utc"] = utc.ToString("O"),
        ["observationSequence"] = 1, ["serverSessionId"] = "server-1", ["worldId"] = 44,
        ["health"] = new JsonObject { ["status"] = "ok", ["unknown"] = new JsonArray() },
        ["players"] = new JsonArray(new JsonObject { ["slot"] = 0, ["name"] = "Audited", ["accountId"] = 7,
            ["isLoggedIn"] = true, ["active"] = true, ["sessionGeneration"] = 9, ["selectedItem"] = 0,
            ["currentLoadoutIndex"] = 0, ["inventory"] = new JsonArray(), ["chatText"] = "must not be returned" }),
        ["chests"] = new JsonArray() };
}
