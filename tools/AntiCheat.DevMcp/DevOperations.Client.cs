using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AntiCheat.DevMcp;

public sealed partial class DevOperations
{
    internal const int ClientStateMaximumBytes = 1024 * 1024;
    internal const int ClientResultMaximumBytes = 256 * 1024;
    internal static readonly TimeSpan ClientStateMaximumAge = TimeSpan.FromMilliseconds(1500);

    private JsonObject ReadClientSession(string sessionId, string? candidateId = null)
    {
        if (sessionId is null || !Regex.IsMatch(sessionId, "^[a-f0-9]{32}$"))
            throw new DevProblem("invalid_client_session_id", "Use the audited 32-character lowercase hexadecimal session ID.");
        var session = files.ReadObject(".lab/devmcp/client-sessions/" + sessionId + ".json", 64 * 1024);
        if (session["schemaVersion"]?.GetValue<int>() != 1 || session["sessionId"]?.GetValue<string>() != sessionId)
            throw new DevProblem("client_session_mismatch", "Session schema or identity does not match the registered filename.");
        string sessionCandidate = CandidateHash(session["candidateId"]);
        if (candidateId is not null && sessionCandidate != candidateId)
            throw new DevProblem("client_candidate_mismatch", "The audited client session does not match the current frozen candidate.");
        ClientStatePath(session);
        return session;
    }

    private string ClientStatePath(JsonObject session)
    {
        string path = session["serverStatePath"]?.GetValue<string>()
            ?? throw new DevProblem("client_state_unconfigured", "The audited session has no serverStatePath.");
        // Reject traversal even in absolute paths before Path.GetFullPath could normalize it away.
        if (path.Split(['/', '\\']).Any(p => p is "." or ".."))
            throw new DevProblem("unsafe_path", "Client state paths must be normalized.");
        string relative = Path.IsPathFullyQualified(path) ? files.Relative(path) : path.Replace('\\', '/');
        if (!relative.StartsWith(".lab/", StringComparison.Ordinal) && !relative.StartsWith("artifacts/", StringComparison.Ordinal))
            throw new DevProblem("client_state_outside_isolation", "Client state must be under this project's .lab or artifacts directory.");
        files.PathFor(relative); // Existing pins reject reparse points, hard links and ancestor replacement on read.
        return relative;
    }

    public Task<DevReply> ClientState(string sessionId)
    {
        try
        {
            var session = ReadClientSession(sessionId);
            string relative = ClientStatePath(session);
            var state = files.ReadObject(relative, ClientStateMaximumBytes);
            var selected = SelectClientState(state, session, DateTimeOffset.UtcNow);
            selected["evidence"] = new JsonObject { ["path"] = relative, ["source"] = "server_observation_snapshot",
                ["mutable_latest_snapshot"] = true, ["read_only"] = true };
            return Task.FromResult(Ok(selected, selected["status"]!.GetValue<string>()));
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    internal static JsonObject SelectClientState(JsonObject state, JsonObject session, DateTimeOffset now)
    {
        if (state["schemaVersion"]?.GetValue<int>() != 1)
            throw new DevProblem("client_state_schema", "Expected version-1 server observation snapshot.");
        if (!DateTimeOffset.TryParse(state["utc"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var utc) || now - utc > ClientStateMaximumAge || utc - now > TimeSpan.FromMilliseconds(250))
            throw new DevProblem("client_state_stale", "Server snapshot is stale, missing its timestamp or outside the accepted clock window; no state is inferred.");
        foreach (string identity in new[] { "serverSessionId", "worldId" })
            if (session[identity] is null || !JsonNode.DeepEquals(session[identity], state[identity]))
                throw new DevProblem("client_state_identity_mismatch", "Server snapshot differs from the audited " + identity + ".");
        long number = state["observationSequence"] is JsonValue sequence && sequence.TryGetValue<long>(out long longNumber) ? longNumber :
            state["observationSequence"] is JsonValue smallSequence && smallSequence.TryGetValue<int>(out int intNumber) ? intNumber : 0;
        if (number < 1)
            throw new DevProblem("client_state_sequence_missing", "Server snapshot requires a positive observation sequence.");
        if (session["playerName"] is null || session["accountId"] is null)
            throw new DevProblem("client_player_unconfigured", "The audited session must identify the authenticated player and account.");
        var players = state["players"] as JsonArray ?? [];
        if (players.Count > 256) throw new DevProblem("client_state_player_capacity", "Server snapshot exceeds the bounded player capacity.");
        var matches = players.OfType<JsonObject>().Where(p => JsonNode.DeepEquals(p["name"], session["playerName"]) &&
            JsonNode.DeepEquals(p["accountId"], session["accountId"]) && p["active"]?.GetValue<bool>() == true &&
            p["isLoggedIn"]?.GetValue<bool>() == true &&
            (session["sessionGeneration"] is null || JsonNode.DeepEquals(session["sessionGeneration"], p["sessionGeneration"]))).ToArray();
        if (matches.Length != 1)
            throw new DevProblem("client_player_unknown", "Exactly one active authenticated player with the audited identity must be observed.");
        var player = Select(matches[0], "slot", "name", "accountId", "sessionGeneration", "active", "dead", "position",
            "selectedItem", "currentLoadoutIndex", "activeChest");
        player["inventory"] = CompactItems(matches[0]["inventory"] as JsonArray, 20);
        var chests = state["chests"] as JsonArray ?? [];
        if (chests.Count > 64) throw new DevProblem("client_state_chest_capacity", "Server snapshot exceeds the bounded chest capacity.");
        var compactChests = new JsonArray(chests.OfType<JsonObject>().Take(4).Select(c =>
        {
            var compact = Select(c, "id", "x", "y");
            compact["slots"] = CompactItems(c["slots"] as JsonArray, 12);
            return (JsonNode)compact;
        }).ToArray());
        bool healthy = state["health"]?["status"]?.GetValue<string>() == "ok";
        return new JsonObject
        {
            ["status"] = healthy ? "ok" : "unknown", ["session_id"] = session["sessionId"]?.DeepClone(),
            ["candidate_id"] = session["candidateId"]?.DeepClone(), ["utc"] = utc.ToString("O"),
            ["observation_sequence"] = number, ["age_ms"] = (long)Math.Max(0, (now - utc).TotalMilliseconds),
            ["server_session_id"] = state["serverSessionId"]?.DeepClone(), ["world_id"] = state["worldId"]?.DeepClone(),
            ["health"] = Select(state["health"] as JsonObject ?? new JsonObject(), "status", "unknown"),
            ["player"] = player, ["chests"] = compactChests, ["chests_total"] = chests.Count,
            ["chests_truncated"] = chests.Count > 4,
            ["client_ui"] = new JsonObject { ["status"] = "unknown", ["reason"] = "server-observation-only" },
            ["observation_boundary"] = "Server-accepted state is observation evidence, not independent proof of legality or rendered GUI state."
        };
    }

    private static JsonObject Select(JsonObject source, params string[] keys)
        => new(keys.Where(source.ContainsKey).Select(k => new KeyValuePair<string, JsonNode?>(k, source[k]?.DeepClone())));

    private static JsonObject CompactItems(JsonArray? source, int limit)
    {
        if (source is null) return new JsonObject { ["status"] = "unknown", ["reason"] = "items-not-observed" };
        if (source.Count > 256) throw new DevProblem("client_state_item_capacity", "Server snapshot exceeds the bounded item capacity.");
        var items = source.OfType<JsonObject>().Where(item => item["type"]?.GetValue<int>() > 0 && item["stack"]?.GetValue<int>() > 0).ToArray();
        return new JsonObject { ["nonempty_count"] = items.Length, ["truncated"] = items.Length > limit,
            ["items"] = new JsonArray(items.Take(limit).Select(i => (JsonNode)Select(i, "slot", "type", "stack", "prefix", "favorited")).ToArray()) };
    }

    private string? ExtractClientResult(JobRecord job)
    {
        string resultPath = JobStore.DirectoryFor(job.JobId) + "/client-result.json";
        if (!File.Exists(files.PathFor(resultPath))) return "Client executor did not publish client-result.json; input outcome remains unknown and must not be retried blindly.";
        var result = files.ReadObject(resultPath, ClientResultMaximumBytes);
        AddArtifact(job, resultPath, "client_original_input_observations");
        string? problem = ValidateClientResult(result, job);
        // Keep ordinary replies compact; before/after and full evidence stay in the registered result artifact.
        job.ClientResult = Select(result, "status", "executedActions", "failure", "stateChanges", "metrics", "cleanup");
        job.ClientResult["evidence_artifact_id"] = job.Artifacts.First(a => a.RelativePath == resultPath).ArtifactId;
        foreach (var reference in (result["evidenceRefs"] as JsonArray ?? []).Take(32))
        {
            string? path = reference is JsonValue v && v.TryGetValue<string>(out string? text) ? text : reference?["path"]?.GetValue<string>();
            if (path is null) continue;
            string relative = Path.IsPathFullyQualified(path) ? files.Relative(path) : path.Replace('\\', '/');
            if (!relative.StartsWith(JobStore.DirectoryFor(job.JobId) + "/", StringComparison.Ordinal)) continue;
            AddArtifact(job, relative, "client_executor_evidence");
        }
        return problem;
    }

    internal static string? ValidateClientResult(JsonObject result, JobRecord job)
    {
        if (result["schemaVersion"]?.GetValue<int>() != 1 || result["scenarioId"]?.GetValue<string>() != job.ScenarioId ||
            result["candidateId"]?.GetValue<string>() != job.CandidateId || result["sessionId"]?.GetValue<string>() != job.ClientSessionId)
            return "Client result schema, scenario, candidate or session identity does not match this job.";
        job.Prepared = true;
        job.ActualCandidateVerified = true;
        int actions = result["executedActions"]?.GetValue<int>() ?? 0;
        var assertions = result["assertions"] as JsonArray ?? [];
        if (actions is < 0 or > 64 || assertions.Count > 128) return "Client result exceeds the registered action/assertion capacity.";
        job.Triggered = actions > 0;
        job.ExecutedAssertions = assertions.Count;
        job.PassedAssertions = assertions.Count(a => a?["passed"]?.GetValue<bool>() == true &&
            !string.IsNullOrWhiteSpace(a?["name"]?.GetValue<string>()));
        job.FailedAssertions = assertions.Count - job.PassedAssertions;
        job.FirstFailedAssertion = assertions.FirstOrDefault(a => a?["passed"]?.GetValue<bool>() != true)?["name"]?.GetValue<string>();
        job.Observed = assertions.Count > 0 && result["before"] is JsonObject && result["after"] is JsonObject;
        string? status = result["status"]?.GetValue<string>();
        if (status != "passed") return "Client executor status is " + (status ?? "unknown") + ": " +
            DevFiles.SafeText(result["failure"]?.ToString()) + ". No automatic retry was performed.";
        if (!job.Triggered || !job.Observed || job.FailedAssertions != 0)
            return "Client pass requires observed before/after state, executed input and a nonempty set of passing named assertions.";
        if (result["cleanup"]?["inputReleased"]?.GetValue<bool>() != true ||
            result["cleanup"]?["externalClientPreserved"]?.GetValue<bool>() != true)
            return "Client executor did not verify released input and preservation of the external game process.";
        if (result["failure"] is not null && !string.IsNullOrWhiteSpace(result["failure"]?.ToString()))
            return "Client result cannot pass while retaining an executor failure.";
        return null;
    }
}
