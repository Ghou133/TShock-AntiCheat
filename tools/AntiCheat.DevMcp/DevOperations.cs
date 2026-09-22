using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AntiCheat.DevMcp;

public sealed partial class DevOperations : IAsyncDisposable
{
    private readonly DevFiles files;
    private readonly JobStore store;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, RunningJob> running = new(StringComparer.Ordinal);
    private readonly object admissionGate = new();
    private int disposing;
    private const int MaximumArtifacts = 128;
    private sealed class RunningJob(OwnedProcess process, CrossProcessLease lease, Candidate candidate, Scenario scenario)
    {
        public OwnedProcess Process { get; } = process;
        public CrossProcessLease Lease { get; } = lease;
        public Candidate Candidate { get; } = candidate;
        public Scenario Scenario { get; } = scenario;
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool ServiceStopping { get; set; }
    }

    public DevOperations(string projectRoot)
    {
        files = new DevFiles(projectRoot);
        store = new JobStore(files, instanceId);
    }

    private static DevReply Ok(JsonObject data, string status = "ok") => new(status, data);
    private static DevReply Error(Exception error) => new("error", new JsonObject
    {
        ["message"] = DevFiles.SafeText(error.Message),
        ["no_qualification_change"] = true
    }, error is DevProblem problem ? problem.Code : "local_tool_error");

    private static void Page(int offset, int limit, int max = 100)
    {
        if (offset is < 0 or > 256 || limit < 1 || limit > max)
            throw new DevProblem("invalid_page", $"offset must be 0..256 and limit 1..{max}.");
    }

    private Candidate CurrentCandidate(bool verifyFiles, JsonObject? manifest = null)
    {
        manifest ??= files.ReadObject("manifest.json");
        var status = manifest["current_status"] as JsonObject
            ?? throw new DevProblem("missing_candidate_index", "Existing manifest has no current status.");
        bool development = status["development_candidate"] is not null;
        JsonNode? freezePointer = status["development_candidate"];
        if (!development)
        {
            var index = ReadHashedObject(status["final_evidence"], "candidate_index_changed", out _, out _);
            freezePointer = index["freeze"] ?? index["candidate"];
        }
        // A development pointer selects test inputs only. It never edits the delivered product,
        // its existing qualification evidence, or records belonging to earlier jobs.
        var freeze = ReadHashedObject(freezePointer, "freeze_changed", out string freezePath, out string freezeHash);
        if (freeze["schemaVersion"]?.GetValue<int>() != 1 || freeze["targetTerraria"]?.GetValue<string>() != "1.4.5.8" ||
            freeze["protocol"]?.GetValue<int>() != 326)
            throw new DevProblem("incomplete_freeze", "Expected the existing version-1 Terraria 1.4.5.8 / protocol326 freeze format.");
        var assemblyFiles = CandidateFiles(freeze, "assemblies", ["AntiCheat.Plugin.TShock.dll", "AntiCheat.Core.dll",
            "AntiCheat.Persistence.dll", "AntiCheat.Progression.dll", "AntiCheat.Rules.dll"], named: true);
        var runtimeFiles = CandidateFiles(freeze, "runtimeFiles", ["TShock.Server.exe", "TShockAPI.dll", "TerrariaServer.dll",
            "OTAPI.dll", "OTAPI.Runtime.dll"]);
        var progressionData = CandidateFiles(freeze, "progressionData", ["candidates.json", "entity-candidates.json"]);
        var assemblies = assemblyFiles.ToDictionary(a => a.Key, a => a.Value.Hash, StringComparer.Ordinal);
        string id = assemblies["AntiCheat.Plugin.TShock.dll"];
        string productId = CandidateHash(status["source_frozen_plugin_sha256"]);
        if (!development && productId != id)
            throw new DevProblem("candidate_mismatch", "Manifest and frozen five-DLL candidate disagree.");
        string lockPath = freeze["runtimeLock"]?["path"]?.GetValue<string>()
            ?? throw new DevProblem("incomplete_freeze", "Frozen candidate has no runtime-lock path.");
        if (lockPath.Replace('\\', '/') != "docs/target-runtime-lock.json")
            throw new DevProblem("incomplete_freeze", "Frozen candidate must reference the project's accepted runtime lock.");
        string lockHash = CandidateHash(freeze["runtimeLock"]?["sha256"]);
        if (verifyFiles)
        {
            foreach (var item in assemblyFiles.Values.Concat(runtimeFiles.Values).Concat(progressionData.Values))
                if (files.Hash(item.Path) != item.Hash)
                    throw new DevProblem("candidate_bytes_changed", "Frozen candidate input differs: " + item.Path);
            if (files.Hash(lockPath) != lockHash)
                throw new DevProblem("runtime_lock_changed", "The accepted runtime lock differs from the frozen candidate.");
        }
        return new(id, freezePath, freezeHash, freeze, assemblies);
    }

    private JsonObject ReadHashedObject(JsonNode? pointer, string mismatchCode, out string path, out string hash)
    {
        path = pointer?["path"]?.GetValue<string>()
            ?? throw new DevProblem("missing_candidate_index", "Candidate metadata requires an explicit project-relative path and SHA-256.");
        hash = CandidateHash(pointer?["sha256"]);
        using var pins = WindowsPathPins.ForFile(files.PathFor(path));
        byte[] bytes = pins.ReadBounded(4 * 1024 * 1024, allowAtomicReplace: true);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash)
            throw new DevProblem(mismatchCode, "Candidate metadata differs from its recorded SHA-256: " + path);
        return JsonNode.Parse(bytes) as JsonObject
            ?? throw new DevProblem("incomplete_freeze", "Candidate metadata must contain a JSON object.");
    }

    private static string CandidateHash(JsonNode? value)
    {
        string? hash = value?.GetValue<string>();
        if (hash is null || !Regex.IsMatch(hash, "^[A-Fa-f0-9]{64}$"))
            throw new DevProblem("incomplete_freeze", "Candidate metadata requires a complete SHA-256 identity.");
        return hash.ToUpperInvariant();
    }

    private static Dictionary<string, (string Path, string Hash)> CandidateFiles(JsonObject freeze, string group,
        string[] expectedNames, bool named = false)
    {
        var rows = freeze[group] as JsonArray
            ?? throw new DevProblem("incomplete_freeze", "Missing " + group);
        var result = new Dictionary<string, (string Path, string Hash)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string path = row?["path"]?.GetValue<string>()
                ?? throw new DevProblem("incomplete_freeze", "Missing input path in " + group);
            string name = Path.GetFileName(path.Replace('\\', '/'));
            if ((named && row?["name"]?.GetValue<string>() != name) || !expectedNames.Contains(name, StringComparer.Ordinal) ||
                !result.TryAdd(name, (path, CandidateHash(row?["sha256"]))))
                throw new DevProblem("incomplete_freeze", "Duplicate or unexpected input identity in " + group);
        }
        if (result.Count != expectedNames.Length)
            throw new DevProblem("incomplete_freeze", "Missing required inputs in " + group);
        return result;
    }

    private Dictionary<string, ArtifactEntry> ProjectArtifacts()
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest.json"] = "recorded_delivery", ["docs/target-runtime-lock.json"] = "runtime_identity",
            ["docs/m3-target-ledger.json"] = "original_72_ledger"
        };
        try
        {
            var manifest = files.ReadObject("manifest.json");
            if (manifest["current_status"]?["development_candidate"]?["path"]?.GetValue<string>() is { } developmentFreeze)
                paths[developmentFreeze] = "development_candidate_freeze";
            string? indexPath = manifest["current_status"]?["final_evidence"]?["path"]?.GetValue<string>();
            if (indexPath is not null)
            {
                paths[indexPath] = "recorded_evidence_index";
                var index = files.ReadObject(indexPath);
                if ((index["freeze"] ?? index["candidate"])?["path"]?.GetValue<string>() is { } freeze) paths[freeze] = "frozen_candidate";
                foreach (var failure in index["retainedFailures"]?.AsArray().Take(16) ?? [])
                    if (failure?["path"]?.GetValue<string>() is { } path) paths[path] = "historical_failed_synthetic_tcp";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or DevProblem) { /* Base facts remain queryable. */ }
        return paths.Select(pair =>
        {
            string full = files.PathFor(pair.Key);
            return new ArtifactEntry("project:" + DevFiles.HashText(pair.Key)[..24].ToLowerInvariant(), pair.Key, pair.Value,
                new FileInfo(full).Length);
        }).ToDictionary(a => a.ArtifactId, StringComparer.Ordinal);
    }

    public Task<DevReply> ProjectStatus(string? taskId, int offset, int limit)
    {
        try
        {
            Page(offset, limit);
            if (taskId is not null && !Regex.IsMatch(taskId, @"^(v2:)?[A-L][0-9]{2}$"))
                throw new DevProblem("invalid_original_task", "Use an original 72-item ID such as v2:C04 or C04.");
            var ledger = files.ReadObject("docs/m3-target-ledger.json");
            var tasks = ledger["v2Tasks"]!.AsArray();
            if (tasks.Count != 72) throw new DevProblem("ledger_shape_changed", "Expected the original 72-item ledger; no replacement is generated.");
            string? normalized = taskId is null ? null : taskId.StartsWith("v2:", StringComparison.Ordinal) ? taskId : "v2:" + taskId;
            var selected = tasks.Where(t => normalized is null || t!["id"]!.GetValue<string>() == normalized).ToArray();
            if (normalized is not null && selected.Length == 0) throw new DevProblem("unknown_original_task", "The ID is not present in the original ledger.");
            var manifest = files.ReadObject("manifest.json");
            var candidate = CurrentCandidate(false, manifest);
            string productCandidateId = CandidateHash(manifest["current_status"]?["source_frozen_plugin_sha256"]);
            bool developmentCandidate = manifest["current_status"]?["development_candidate"] is not null;
            string candidateHealth = "verified";
            string? candidateIssue = null;
            try { CurrentCandidate(true, manifest); }
            catch (Exception ex) { candidateHealth = "changed_or_unavailable"; candidateIssue = DevFiles.SafeText(ex.Message); }
            List<JobRecord> jobs;
            using (var guard = store.Lock())
                jobs = store.Index().Jobs.Where(j => j.OwnerSid == store.OwnerSid).Select(j => store.ReadOwned(j.JobId)).ToList();
            foreach (var job in jobs.Where(j => !JobStore.Terminal(j.Status))) ReconcileOrphan(job);
            return Task.FromResult(Ok(new JsonObject
            {
                ["target"] = "Terraria 1.4.5.8 / protocol326", ["recorded_checkpoint"] = ledger["checkpoint"]?.DeepClone(),
                ["current_product_candidate_id"] = productCandidateId, ["current_test_candidate_id"] = candidate.Id,
                ["current_test_candidate_source"] = developmentCandidate ? "development_candidate" : "final_evidence",
                ["candidate_health"] = candidateHealth, ["candidate_issue"] = candidateIssue,
                ["frozen_evidence"] = candidate.FreezePath, ["qualified_rule_count"] = ledger["runtimeQualifications"]?.AsArray().Count,
                ["qualification_candidate_id"] = productCandidateId,
                ["qualifications_apply_to_current_test_candidate"] = !developmentCandidate,
                ["qualification_source"] = "existing ledger for qualification_candidate_id only; development candidates never inherit qualification",
                ["recorded_archive"] = manifest["development_archive"]?.DeepClone(),
                ["scope"] = "Recorded delivery and explicit development candidate are reported separately; qualification changes only through the existing local delivery process",
                ["original_task_count"] = 72, ["matching_tasks"] = selected.Length,
                ["tasks"] = new JsonArray(selected.Skip(offset).Take(limit).Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t!["id"]?.DeepClone(), ["name"] = t["name"]?.DeepClone(),
                    ["observed_scope"] = DevFiles.SafeText(t["currentCodeAssessment"]?.ToString(), 700),
                    ["next_task"] = t["nextTask"]?.DeepClone(), ["mcp_changes_status"] = false
                }).ToArray()),
                ["next_offset"] = offset + limit < selected.Length ? offset + limit : null,
                ["active_jobs"] = DevFiles.Node(jobs.Where(j => !JobStore.Terminal(j.Status)).Take(16).Select(ShortJob)),
                ["recent_jobs"] = DevFiles.Node(jobs.OrderByDescending(j => j.CreatedUtc).Take(8).Select(ShortJob)),
                ["project_artifacts"] = DevFiles.Node(ProjectArtifacts().Values),
                ["coordination_boundary"] = "All terraria-dev instances share an OS-backed build/network lease. External bare dotnet or old script invocations do not acquire it.",
                ["limits"] = new JsonObject { ["indexed_jobs"] = JobStore.MaximumJobs, ["logs_per_stream_bytes"] = OwnedProcess.MaximumLogBytes,
                    ["retention"] = "No automatic deletion or historical rescan. At capacity new jobs are blocked; original records remain." }
            }));
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    public Task<DevReply> ScenarioList(ScenarioCategory? category, string? tag, int offset, int limit)
    {
        try
        {
            Page(offset, limit);
            if (category.HasValue && !Enum.IsDefined(category.Value)) throw new DevProblem("unknown_category", "Unknown category.");
            if (tag is not null && (tag.Length > 40 || !Regex.IsMatch(tag, "^[a-zA-Z0-9_-]+$")))
                throw new DevProblem("invalid_tag", "Tag must be a bounded catalog tag.");
            var matching = ScenarioCatalog.All.Where(s => (!category.HasValue || s.Category == category) &&
                (tag is null || s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))).ToArray();
            return Task.FromResult(Ok(new JsonObject
            {
                ["scenarios"] = DevFiles.Node(matching.Skip(offset).Take(limit).Select(s => new
                {
                    scenario_id = s.Id.ToString(), category = s.Category.ToString(), description = s.Description,
                    evidence_layer = s.EvidenceLayer, entry = s.Script, fixed_arguments = s.FixedArguments,
                    parameters = s.IsClient ? (object)new { clientSessionId = new { required = true, pattern = "^[a-f0-9]{32}$" } } :
                        s.IsTcp ? new { connectionPacingMilliseconds = new { @default = 0, allowed = new[] { 0, 200 } } } : null,
                    resources = new[] { "shared-build-output", "owned-process-tree", s.IsClient ? "exclusive-audited-client-window-and-fresh-state" :
                        s.IsTcp ? "isolated-loopback-world-database-port" : "isolated-test-report" },
                    tags = s.Tags, timeout_seconds = s.TimeoutSeconds,
                    support = File.Exists(files.PathFor(s.Script)) ? "registered_existing_entry" : "blocked_missing_entry",
                    qualification_action = "none", client_gui = s.IsClient ? "requires_audited_original_input_session" : "not_executed"
                })),
                ["matching"] = matching.Length, ["next_offset"] = offset + limit < matching.Length ? offset + limit : null,
                ["catalog_source"] = "tools/AntiCheat.DevMcp/ScenarioCatalog.cs; the same table drives CLI and MCP"
            }));
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    private static object ShortJob(JobRecord j) => new { job_id = j.JobId, status = j.Status, phase = j.Phase,
        scenario_id = j.ScenarioId, candidate_id = j.CandidateId, j.CreatedUtc, j.CompletedUtc, j.Failure };

    private void ReconcileOrphan(JobRecord job)
    {
        if (running.ContainsKey(job.JobId)) return;
        if (JobStore.Terminal(job.Status) && job.Status != "interrupted")
        {
            if (job.ProcessJobName is null || job.OwnedProcessesExited == true) return;
            var terminalInspection = OwnedProcess.InspectJob(job.ProcessJobName);
            bool confirmed = terminalInspection.ErrorCode is null && (!terminalInspection.Exists || terminalInspection.ActiveProcessCount == 0) &&
                job.Members.All(m => OwnedProcess.IsSameProcessAlive(new(m.Pid, m.StartedUtc, m.ExecutablePath)) == false);
            if (confirmed) store.Update(job.JobId, j =>
            { j.OwnedProcessesExited = true; j.Cleanup = "subsequent_kernel_exit_observed_original_outcome_retained"; });
            return;
        }
        bool? ownerAlive = OwnedProcess.IsSameProcessAlive(new(job.ServicePid, job.ServiceStartedUtc, job.ServiceExecutable));
        if (ownerAlive != false) return;
        bool allKnownExited = job.Members.All(m =>
            OwnedProcess.IsSameProcessAlive(new(m.Pid, m.StartedUtc, m.ExecutablePath)) == false);
        var inspection = job.ProcessJobName is null ? null : OwnedProcess.InspectJob(job.ProcessJobName);
        // The kernel job name is persisted before any process creation. A null name and
        // null runner identity remain proof of prelaunch interruption across later reads.
        bool kernelEnded = inspection is null ? job.ProcessJobName is null && job.RunnerPid is null :
            inspection.ErrorCode is null && (!inspection.Exists || inspection.ActiveProcessCount == 0);
        store.Update(job.JobId, j =>
        {
            j.Status = "interrupted"; j.Phase = "owner_process_lost"; j.CompletedUtc = DateTimeOffset.UtcNow;
            j.Failure = "Registered owner process no longer matches its recorded start identity. No external process was killed or adopted.";
            j.OwnedProcessesExited = allKnownExited && kernelEnded;
            j.Cleanup = j.OwnedProcessesExited == true ? "kernel_exit_observed_normal_cleanup_unproven" : "unverified_recovery_required";
        });
        job.Status = "interrupted";
    }

    public Task<DevReply> ArtifactRead(string artifactId, int cursor, int maxChars)
    {
        try
        {
            if (cursor < 0 || maxChars is < 1 or > 16384 || artifactId is null || artifactId.Length > 100)
                throw new DevProblem("invalid_artifact_range", "Use an indexed artifact_id, nonnegative cursor and 1..16384 characters.");
            ArtifactEntry entry;
            if (artifactId.StartsWith("project:", StringComparison.Ordinal))
                entry = ProjectArtifacts().GetValueOrDefault(artifactId) ?? throw new DevProblem("unknown_artifact", "Artifact is not in the project index.");
            else
            {
                string[] parts = artifactId.Split(':');
                if (parts.Length != 3 || parts[0] != "job") throw new DevProblem("unknown_artifact", "Expected an artifact_id returned by this tool.");
                using var guard = store.Lock();
                var job = store.ReadOwned(parts[1]);
                entry = job.Artifacts.FirstOrDefault(a => a.ArtifactId == artifactId)
                    ?? throw new DevProblem("unknown_artifact", "Artifact is not registered in the owned job.");
            }
            using var pins = WindowsPathPins.ForFile(files.PathFor(entry.RelativePath));
            byte[] bytes = pins.ReadBounded(4 * 1024 * 1024, allowConcurrentWrite: true, allowAtomicReplace: true);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            string original = reader.ReadToEnd();
            string text = DevFiles.SafeText(original, int.MaxValue);
            if (cursor > text.Length) throw new DevProblem("invalid_artifact_cursor", "Cursor exceeds the current redacted text length.");
            int length = Math.Min(maxChars, text.Length - cursor);
            return Task.FromResult(Ok(new JsonObject
            {
                ["artifact_id"] = artifactId, ["path"] = entry.RelativePath, ["evidence_layer"] = entry.EvidenceLayer,
                ["content"] = text.Substring(cursor, length), ["cursor"] = cursor,
                ["next_cursor"] = cursor + length < text.Length ? cursor + length : null,
                ["total_characters"] = text.Length, ["returned_characters"] = length,
                ["redacted"] = text != original, ["is_instruction_source"] = false,
                ["raw_original_preserved"] = true
            }));
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    public async ValueTask DisposeAsync()
    {
        KeyValuePair<string, RunningJob>[] owned;
        lock (admissionGate)
        {
            if (Interlocked.Exchange(ref disposing, 1) != 0) return;
            owned = running.ToArray();
        }
        foreach (var (id, active) in owned)
        {
            try { await RequestCancellation(id, "stdio_or_foreground_cli_owner_stopping", active); }
            catch (Exception) { active.Process.TerminateOwnedTree("owner_shutdown_signal_failed"); }
        }
        try { await Task.WhenAll(owned.Select(j => j.Value.Completion)).WaitAsync(TimeSpan.FromSeconds(40)); }
        catch (TimeoutException)
        {
            foreach (var active in running.Values) active.Process.TerminateOwnedTree("bounded_owner_shutdown_timeout");
        }
    }
}
