using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AntiCheat.DevMcp;

public sealed partial class DevOperations
{
    public Task<DevReply> JobStart(ScenarioId scenarioId, string candidateId, JobParameters? parameters, string deduplicationKey)
    {
        lock (admissionGate) return StartOwnedJob(scenarioId, candidateId, parameters, deduplicationKey);
    }

    private Task<DevReply> StartOwnedJob(ScenarioId scenarioId, string candidateId, JobParameters? parameters, string deduplicationKey)
    {
        string? admittedJob = null;
        CrossProcessLease? lease = null;
        OwnedProcess? process = null;
        try
        {
            if (Volatile.Read(ref disposing) != 0) throw new DevProblem("owner_stopping", "This tool instance is closing.");
            var admissionTimer = Stopwatch.StartNew();
            var scenario = ScenarioCatalog.Get(scenarioId);
            parameters ??= new JobParameters();
            ValidateParameters(scenario, parameters);
            if (deduplicationKey is null || !Regex.IsMatch(deduplicationKey, @"^[A-Za-z0-9_.:-]{1,80}$"))
                throw new DevProblem("invalid_deduplication_key", "Use 1..80 letters, digits, dots, colons, underscores or hyphens.");
            if (candidateId is null || !Regex.IsMatch(candidateId, "^[A-Fa-f0-9]{64}$"))
                throw new DevProblem("invalid_candidate_id", "Use the frozen candidate ID returned by project_status.");
            candidateId = candidateId.ToUpperInvariant();
            // Preserve earlier Core/TCP request hashes so adding client parameters cannot make
            // an uncertain existing start look like a conflicting new request.
            string requestHash = DevFiles.HashText(scenario.IsClient ? JsonSerializer.Serialize(new { scenarioId, candidateId,
                parameters.ConnectionPacingMilliseconds, parameters.ClientSessionId }, DevFiles.Json) :
                JsonSerializer.Serialize(new { scenarioId, candidateId, parameters.ConnectionPacingMilliseconds }, DevFiles.Json));
            string id = Guid.NewGuid().ToString("N");
            JobRecord record;
            using (var guard = store.Lock())
            {
                var index = store.Index();
                var duplicate = index.Jobs.FirstOrDefault(j => j.OwnerSid == store.OwnerSid && j.DeduplicationKey == deduplicationKey);
                if (duplicate is not null)
                {
                    if (duplicate.RequestHash != requestHash)
                        throw new DevProblem("deduplication_conflict", "This key already identifies a different request; its result has not been overwritten.");
                    var previous = store.ReadOwned(duplicate.JobId);
                    return Task.FromResult(Ok(JobData(previous, true), previous.Status));
                }
                if (index.Jobs.Count >= JobStore.MaximumJobs)
                    throw new DevProblem("job_index_capacity", "The bounded job index is full. Preserve/archive completed records explicitly before starting more jobs.");
                files.MakeDirectory(JobStore.DirectoryFor(id));
                var owner = LeaseOwner.Current(instanceId, id);
                record = new JobRecord
                {
                    JobId = id, OwnerSid = store.OwnerSid, InstanceId = instanceId,
                    ServicePid = owner.Pid, ServiceStartedUtc = owner.StartedUtc, ServiceExecutable = owner.ExecutablePath,
                    ScenarioId = scenarioId.ToString(), CandidateId = candidateId, EvidenceLayer = scenario.EvidenceLayer,
                    ClientSessionId = parameters.ClientSessionId,
                    CreatedUtc = DateTimeOffset.UtcNow, MemberSnapshotIncomplete = false
                };
                files.Write(JobStore.DirectoryFor(id) + "/.terraria-dev-owned", new
                { schemaVersion = 1, projectRoot = files.Root, jobId = id, ownerSid = store.OwnerSid, instanceId });
                store.Save(record);
                index.Jobs.Add(new() { JobId = id, OwnerSid = store.OwnerSid, DeduplicationKey = deduplicationKey,
                    RequestHash = requestHash, CreatedUtc = record.CreatedUtc });
                store.SaveIndex(index);
                admittedJob = id;
            }

            // Resolve the recorded freeze, never a file picked by newest modification time.
            var candidate = CurrentCandidate(true);
            if (candidate.Id != candidateId) throw new DevProblem("candidate_not_registered", "The requested candidate is not the current recorded frozen candidate.");
            CheckRecoveryBeforeStart(id);
            var acquired = CrossProcessLease.TryAcquire(files.PathFor(".lab/devmcp/leases/build-and-network.lock"), LeaseOwner.Current(instanceId, id));
            if (!acquired.Acquired)
            {
                record = store.Update(id, j =>
                {
                    j.Status = "blocked"; j.Phase = "resource_conflict"; j.Failure = acquired.BlockedReason;
                    j.CompletedUtc = DateTimeOffset.UtcNow; j.AdmissionMilliseconds = admissionTimer.ElapsedMilliseconds;
                });
                return Task.FromResult(Ok(JobData(record), "blocked"));
            }
            lease = acquired.Lease!;
            // Verify again while holding the shared lease; external uncoordinated builds remain explicit.
            candidate = CurrentCandidate(true);
            if (candidate.Id != candidateId) throw new DevProblem("candidate_changed_during_admission", "The recorded candidate changed during admission; nothing was launched.");
            if (scenario.IsClient) ReadClientSession(parameters.ClientSessionId!, candidateId);
            string executable = FindPowerShell();
            var arguments = new List<string> { "-NoProfile", "-File", files.PathFor(scenario.Script) };
            arguments.AddRange(scenario.FixedArguments);
            if (scenario.IsClient) arguments.AddRange(["-ClientSessionId", parameters.ClientSessionId!]);
            if (scenario.IsTcp && parameters.ConnectionPacingMilliseconds == 200)
                arguments.AddRange(["-ConnectionPacingMilliseconds", "200"]);
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ANTICHEAT_DEV_JOB_DIR"] = files.PathFor(JobStore.DirectoryFor(id)),
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1", ["UseSharedCompilation"] = "false"
            };
            var sourceInputs = new List<object>
            {
                new { path = scenario.Script, sha256 = files.Hash(scenario.Script) },
                new { path = "tools/AntiCheat.DevMcp/ScenarioCatalog.cs", sha256 = files.Hash("tools/AntiCheat.DevMcp/ScenarioCatalog.cs") }
            };
            if (scenario.IsClient)
            {
                foreach (string clientSource in new[] { "tools/client-automation/client_scenarios.py",
                    ".lab/devmcp/client-sessions/" + parameters.ClientSessionId + ".json" })
                    sourceInputs.Add(new { path = clientSource, sha256 = files.Hash(clientSource) });
            }
            if (scenario.IsTcp) environment["ANTICHEAT_DEV_EXPECTED_SCENARIO"] = ScenarioCatalog.NetworkSelection(scenario);
            string sourceDirectory = scenario.IsClient ? "tools/AntiCheat.DevMcp" : scenario.IsTcp ? "tools/AntiCheat.NetworkLab" : "tests/AntiCheat.Core.Tests";
            foreach (string source in Directory.EnumerateFiles(files.PathFor(sourceDirectory), "*.cs").Order(StringComparer.Ordinal).Take(128))
            {
                string relative = files.Relative(source);
                sourceInputs.Add(new { path = relative, sha256 = files.Hash(relative) });
            }
            files.Write(JobStore.DirectoryFor(id) + "/request.json", new
            {
                schemaVersion = 1, jobId = id, deduplicationKey, requestHash, instanceId, ownerSid = store.OwnerSid,
                scenarioId = scenario.Id.ToString(), requestedScenario = scenario.IsTcp ? ScenarioCatalog.NetworkSelection(scenario) : null, candidateId, parameters, executable, arguments,
                workingDirectory = files.Root, candidateFreeze = new { path = candidate.FreezePath, sha256 = candidate.FreezeSha256 },
                expectedAssemblies = candidate.Assemblies, expectedRuntimeAndData = candidate.Freeze,
                runnerSourceInputs = sourceInputs, createdUtc = record.CreatedUtc,
                ownedResource = "build-and-network.lock", timeoutSeconds = scenario.TimeoutSeconds,
                evidenceLayer = scenario.EvidenceLayer, productQualificationAction = "none",
                automaticRetries = 0, defaultPacingChanged = false,
                externalUncoordinatedBuildsAreExcluded = true
            });
            string ownedJobName = "Local\\terraria-dev-" + id;
            store.Update(id, j => { j.ProcessJobName = ownedJobName; j.Phase = "starting_owned_process"; });
            process = OwnedProcess.Start(executable, arguments, files.Root, environment,
                files.PathFor(JobStore.DirectoryFor(id) + "/stdout.log"), files.PathFor(JobStore.DirectoryFor(id) + "/stderr.log"), ownedJobName);
            record = store.Update(id, j =>
            {
                j.Status = "running"; j.Phase = "runner_started"; j.StartedUtc = DateTimeOffset.UtcNow;
                j.RunnerPid = process.Pid; j.RunnerStartedUtc = process.StartedUtc; j.RunnerExecutable = process.ExecutablePath;
                j.ProcessJobName = process.JobName; j.AdmissionMilliseconds = admissionTimer.ElapsedMilliseconds;
                j.Members = [new(process.Pid, process.StartedUtc, process.ExecutablePath)];
                AddArtifact(j, JobStore.DirectoryFor(id) + "/request.json", "actual_invocation");
                AddArtifact(j, JobStore.DirectoryFor(id) + "/stdout.log", "runner_stdout");
                AddArtifact(j, JobStore.DirectoryFor(id) + "/stderr.log", "runner_stderr");
            });
            var active = new RunningJob(process, lease, candidate, scenario);
            if (!running.TryAdd(id, active)) throw new InvalidOperationException("Duplicate active job identity.");
            process = null; lease = null; // Ownership is now held until the monitor completes.
            active.Completion = Monitor(id, active);
            return Task.FromResult(Ok(JobData(record), "running"));
        }
        catch (Exception ex)
        {
            process?.Dispose(); lease?.Dispose();
            if (admittedJob is null) return Task.FromResult(Error(ex));
            try
            {
                var blocked = store.Update(admittedJob, j =>
                {
                    j.Status = ex is DevProblem ? "blocked" : "failed"; j.Phase = "admission_failed";
                    j.Failure = DevFiles.SafeText(ex.Message); j.CompletedUtc = DateTimeOffset.UtcNow;
                });
                return Task.FromResult(Ok(JobData(blocked), blocked.Status));
            }
            catch (Exception) { return Task.FromResult(Error(ex)); }
        }
    }

    private void CheckRecoveryBeforeStart(string newId)
    {
        List<JobRecord> pending;
        using (var guard = store.Lock())
            pending = store.Index().Jobs.Where(j => j.JobId != newId && j.OwnerSid == store.OwnerSid)
                .Select(j => store.ReadOwned(j.JobId)).Where(j => !JobStore.Terminal(j.Status) || j.Status == "interrupted" ||
                    j.ProcessJobName is not null && j.OwnedProcessesExited != true).ToList();
        foreach (var old in pending)
        {
            ReconcileOrphan(old);
            using var guard = store.Lock();
            var fresh = store.ReadOwned(old.JobId);
            if (JobStore.Terminal(fresh.Status) && fresh.OwnedProcessesExited != true &&
                (fresh.Status == "interrupted" || fresh.ProcessJobName is not null))
                throw new DevProblem("interrupted_cleanup_unverified", "An interrupted owned job requires process-identity cleanup verification before resources can be reused: " + old.JobId);
        }
    }

    private static string FindPowerShell()
    {
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) continue;
            string candidate = Path.Combine(path, "pwsh.exe");
            if (!File.Exists(candidate)) continue;
            using var pins = WindowsPathPins.ForFile(candidate);
            using var stream = pins.OpenRead();
            return pins.FullPath;
        }
        throw new DevProblem("pwsh_missing", "PowerShell 7 executable was not found on the existing local PATH.");
    }

    public Task<DevReply> JobStatus(string jobId, int cursor, int maxChars)
    {
        try
        {
            if (cursor < 0 || maxChars is < 1 or > 16384) throw new DevProblem("invalid_status_range", "Use nonnegative cursor and 1..16384 max_chars.");
            JobRecord record;
            using (var guard = store.Lock()) record = store.ReadOwned(jobId);
            ReconcileOrphan(record);
            using (var guard = store.Lock()) record = store.ReadOwned(jobId);
            var data = JobData(record);
            // Status calls return finite structured facts; log pagination uses artifact_read only.
            string progress = $"{record.Status}: {record.Phase}\n{DevFiles.SafeText(record.Failure)}";
            int start = Math.Min(cursor, progress.Length);
            int count = Math.Min(maxChars, progress.Length - start);
            data["progress"] = progress.Substring(start, count);
            data["next_cursor"] = start + count < progress.Length ? start + count : null;
            return Task.FromResult(Ok(data, record.Status));
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    public Task<DevReply> JobCancel(string jobId, string reason)
        => RequestCancellation(jobId, reason);

    private Task<DevReply> RequestCancellation(string jobId, string reason, RunningJob? stoppingOwner = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 || reason.Any(char.IsControl))
                throw new DevProblem("invalid_cancel_reason", "Supply a nonempty single-line reason of at most 256 characters.");
            using var guard = store.Lock();
            var job = store.ReadOwned(jobId);
            if (JobStore.Terminal(job.Status)) return Task.FromResult(Ok(JobData(job), job.Status));
            bool firstCancellationRequest = job.CancelRequestedUtc is null;
            // Preserve an already-recorded explicit cancellation across owner disposal.
            // With no prior request, owner shutdown remains an interrupted experiment.
            // The shared index lock orders this decision against other CLI/MCP instances.
            if (stoppingOwner is not null) stoppingOwner.ServiceStopping = job.CancelRequestedUtc is null;
            job.CancelReason ??= reason;
            job.CancelRequestedUtc ??= DateTimeOffset.UtcNow;
            files.Write(JobStore.DirectoryFor(jobId) + "/cancel.request", new
            { jobId, reason = job.CancelReason, requestedUtc = job.CancelRequestedUtc });
            store.Save(job);
            var data = JobData(job);
            data["cancellation_requested"] = true;
            data["first_cancellation_request"] = firstCancellationRequest;
            data["bounded_grace_seconds"] = 30;
            return Task.FromResult(Ok(data, "cancel_requested"));
        }
        catch (Exception ex) { return Task.FromResult(Error(ex)); }
    }

    private async Task Monitor(string id, RunningJob active)
    {
        var elapsed = Stopwatch.StartNew();
        string? executionProblem = null;
        bool timedOut = false;
        int? exitCode = null;
        var exit = active.Process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (!exit.IsCompleted)
            {
                var job = store.Update(id, j =>
                {
                    UpdateLifecycle(j);
                    var members = active.Process.GetMembers();
                    j.Members = members.Select(m => new ProcessIdentity(m.Pid, m.StartedUtc, m.ExecutablePath)).ToList();
                    j.MemberSnapshotIncomplete = active.Process.MemberSnapshotIncomplete;
                    j.StdoutBytes = active.Process.Stdout.WrittenBytes; j.StderrBytes = active.Process.Stderr.WrittenBytes;
                    j.DroppedOutputBytes = active.Process.Stdout.DroppedBytes + active.Process.Stderr.DroppedBytes;
                });
                if (elapsed.Elapsed.TotalSeconds > active.Scenario.TimeoutSeconds && job.CancelRequestedUtc is null)
                {
                    var deadlineRequest = await RequestCancellation(id, "registered_scenario_deadline_exceeded");
                    // The old monitor snapshot may predate another instance's cancel.
                    // Only the first request, selected under the shared index lock, can
                    // classify this run as a registered-deadline failure.
                    timedOut = deadlineRequest.Data["first_cancellation_request"]?.GetValue<bool>() == true;
                }
                if (job.CancelRequestedUtc is { } requested && DateTimeOffset.UtcNow - requested > TimeSpan.FromSeconds(30))
                {
                    active.Process.TerminateOwnedTree("graceful_cancellation_deadline_exceeded");
                    store.Update(id, j => { j.ForcedCleanup = true; j.Cleanup = "forced_owned_tree_cleanup"; });
                }
                await Task.WhenAny(exit, Task.Delay(250));
            }
            exitCode = await exit;
        }
        catch (Exception ex)
        {
            executionProblem = DevFiles.SafeText(ex.Message);
            active.Process.TerminateOwnedTree("monitor_or_process_failure");
            try { exitCode = await exit.WaitAsync(TimeSpan.FromSeconds(7)); } catch (Exception) { }
        }
        finally
        {
            // A root exit is not proof that inherited child processes exited.
            bool hadRemainingChildren = !active.Process.AllExited;
            if (hadRemainingChildren) active.Process.TerminateOwnedTree("owned_children_remained_after_runner_exit");
            active.Process.Dispose();
            try
            {
                var summaryTimer = Stopwatch.StartNew();
                store.Update(id, job =>
                {
                    UpdateLifecycle(job);
                    job.ExitCode = exitCode;
                    job.ExecutionMilliseconds = elapsed.ElapsedMilliseconds;
                    job.ForcedCleanup |= hadRemainingChildren || active.Process.Cleanup.TerminationRequested;
                    job.OwnedProcessesExited = active.Process.Cleanup.ActiveJobProcesses == 0;
                    job.StdoutBytes = active.Process.Stdout.WrittenBytes; job.StderrBytes = active.Process.Stderr.WrittenBytes;
                    job.DroppedOutputBytes = active.Process.Stdout.DroppedBytes + active.Process.Stderr.DroppedBytes;
                    AddArtifact(job, JobStore.DirectoryFor(id) + "/lifecycle.json", "runner_lifecycle");
                    string? resultProblem = ExtractResult(job, active);
                    bool outputHealthy = active.Process.Stdout.Error is null && active.Process.Stderr.Error is null &&
                        active.Process.Stdout.ReachedEof && active.Process.Stderr.ReachedEof;
                    if (!outputHealthy) executionProblem ??= "Runner output capture was incomplete; original retained bytes and dropped counts are recorded.";
                    // A recorded assertion failure takes precedence over a coincident cancel request.
                    if (job.FailedAssertions > 0 || job.Lifecycle?["state"]?.GetValue<string>() == "failed")
                    {
                        job.Status = "failed"; job.Failure ??= resultProblem ?? "Runner recorded a failed assertion or lifecycle.";
                    }
                    else if (timedOut)
                    {
                        job.Status = "failed"; job.Failure = "Registered scenario deadline exceeded; no automatic retry was performed.";
                    }
                    else if (active.ServiceStopping)
                    {
                        job.Status = "interrupted"; job.Failure = "The owning stdio/foreground CLI instance stopped before terminal observation.";
                    }
                    else if (job.CancelRequestedUtc is not null)
                    {
                        job.Status = "canceled"; job.Failure = "Canceled: " + DevFiles.SafeText(job.CancelReason);
                    }
                    else if (executionProblem is not null || resultProblem is not null || exitCode != 0 || !job.ActualCandidateVerified ||
                        !job.Observed || job.ForcedCleanup || job.OwnedProcessesExited != true)
                    {
                        job.Status = "failed";
                        job.Failure = executionProblem ?? resultProblem ?? "Missing verified result, candidate identity, or clean owned-process completion.";
                    }
                    else job.Status = "passed";
                    job.Cleanup = job.OwnedProcessesExited != true ? "owned_process_exit_unverified" :
                        job.ForcedCleanup ? "forced_owned_tree_cleanup" : "owned_tree_exited";
                    job.Phase = "completed"; job.CompletedUtc = DateTimeOffset.UtcNow;
                    job.SummaryMilliseconds = summaryTimer.ElapsedMilliseconds;
                    files.Write(JobStore.DirectoryFor(id) + "/result.json", new
                    {
                        schemaVersion = 1, jobId = id, status = job.Status, phase = job.Phase, candidateId = job.CandidateId,
                        evidenceLayer = job.EvidenceLayer, exitCode, job.PassedAssertions, job.FailedAssertions, job.ExecutedAssertions,
                        job.FirstFailedAssertion, job.Failure, job.ActualCandidateVerified, job.Prepared, job.Triggered, job.Observed,
                        job.Cleanup, job.ForcedCleanup, job.OwnedProcessesExited, processCleanup = active.Process.Cleanup,
                        stdout = active.Process.Stdout, stderr = active.Process.Stderr,
                        job.AdmissionMilliseconds, job.ExecutionMilliseconds, job.SummaryMilliseconds,
                        lifecycle = job.Lifecycle, clientResult = job.ClientResult, qualificationChanged = false, automaticRetries = 0,
                        clientGui = active.Scenario.IsClient ? "original_input_server_observation_only" : "not_executed"
                    });
                    AddArtifact(job, JobStore.DirectoryFor(id) + "/result.json", "tool_result_with_runner_references");
                    string artifactIndexPath = JobStore.DirectoryFor(id) + "/artifact-index.json";
                    files.Write(artifactIndexPath, new { artifacts = job.Artifacts, capacity = MaximumArtifacts });
                    AddArtifact(job, artifactIndexPath, "complete_bounded_artifact_index");
                });
            }
            catch (Exception ex)
            {
                try { store.Update(id, j => { j.Status = "failed"; j.Phase = "result_extraction_failed";
                    j.Failure = DevFiles.SafeText(ex.Message); j.CompletedUtc = DateTimeOffset.UtcNow;
                    j.OwnedProcessesExited = active.Process.Cleanup.ActiveJobProcesses == 0; }); } catch (Exception) { }
            }
            finally { active.Lease.Dispose(); running.TryRemove(id, out _); }
        }
    }

    private void UpdateLifecycle(JobRecord job)
    {
        string path = JobStore.DirectoryFor(job.JobId) + "/lifecycle.json";
        if (!File.Exists(files.PathFor(path))) return;
        var lifecycle = files.ReadObject(path, 256 * 1024);
        if (lifecycle["jobId"]?.GetValue<string>() != job.JobId) throw new DevProblem("lifecycle_owner_mismatch", "Lifecycle belongs to a different job.");
        job.Lifecycle = lifecycle;
        job.Phase = lifecycle["phase"]?.GetValue<string>() ?? job.Phase;
        string? report = lifecycle["reportDirectory"]?.GetValue<string>();
        string? run = lifecycle["runDirectory"]?.GetValue<string>();
        if (report is not null)
        {
            string relative = files.Relative(report);
            if (!relative.StartsWith("artifacts/test-runs/", StringComparison.Ordinal) && !relative.StartsWith("artifacts/network-runs/", StringComparison.Ordinal))
                throw new DevProblem("unexpected_report_root", "Lifecycle report is outside the existing runner report roots.");
            job.ReportDirectory = relative;
        }
        if (run is not null)
        {
            string relative = files.Relative(run);
            if (!relative.StartsWith(".lab/", StringComparison.Ordinal) && !relative.StartsWith("artifacts/test-runs/", StringComparison.Ordinal))
                throw new DevProblem("unexpected_run_root", "Lifecycle run is outside the existing isolation roots.");
            job.IsolatedDirectory = relative;
        }
        job.LoopbackPort = lifecycle["loopbackPort"]?.GetValue<int?>();
        AddArtifact(job, path, "runner_lifecycle");
    }

    private string? ExtractResult(JobRecord job, RunningJob active)
    {
        if (active.Scenario.IsClient) return ExtractClientResult(job);
        if (job.ReportDirectory is null) return "Runner never published its actual report directory.";
        string path = job.ReportDirectory + "/summary.json";
        if (!File.Exists(files.PathFor(path))) return "Runner summary is missing; process start/exit is not test success.";
        var summary = files.ReadObject(path);
        AddArtifact(job, path, active.Scenario.EvidenceLayer);
        // Failed scenarios need the same bounded detailed evidence inventory as successful
        // ones; classification below must not hide the native exception/terminal snapshot.
        var reportOptions = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 4, IgnoreInaccessible = false };
        foreach (string artifact in Directory.EnumerateFiles(files.PathFor(job.ReportDirectory), "*.json", reportOptions).Take(96))
            AddArtifact(job, files.Relative(artifact), active.Scenario.EvidenceLayer);
        job.Prepared = true;
        if (active.Scenario.IsTcp)
        {
            string inputPath = job.ReportDirectory + "/inputs.json";
            if (!File.Exists(files.PathFor(inputPath))) return "Actual runner inputs are missing.";
            var input = files.ReadObject(inputPath);
            AddArtifact(job, inputPath, "actual_runner_inputs");
            string expectedSelection = ScenarioCatalog.NetworkSelection(active.Scenario);
            if (input["requestedScenario"]?.GetValue<string>() != expectedSelection ||
                input["actualScenario"]?.GetValue<string>() != expectedSelection ||
                summary["requestedScenario"]?.GetValue<string>() != expectedSelection ||
                summary["actualScenario"]?.GetValue<string>() != expectedSelection)
                return "Requested and actual network scenarios do not match the registered selection.";
            var actual = input["stagedPluginHashes"]?.AsArray().ToDictionary(n => n!["name"]!.GetValue<string>(), n => n!["sha256"]!.GetValue<string>());
            job.ActualCandidateVerified = actual is not null && actual.Count == active.Candidate.Assemblies.Count &&
                actual.All(kv => active.Candidate.Assemblies.TryGetValue(kv.Key, out string? expected) && expected == kv.Value) &&
                summary["actualRuntimeIdentityVerified"]?.GetValue<bool>() == true;
            var stagedRuntime = input["stagedRuntimeHashes"]?.AsArray().ToDictionary(
                x => x!["path"]!.GetValue<string>(), x => x!["sha256"]!.GetValue<string>());
            var expectedRuntime = active.Candidate.Freeze["runtimeFiles"]!.AsArray().ToDictionary(
                x => x!["path"]!.GetValue<string>().Split("win-x64-framework-dependent/", StringSplitOptions.None)[^1],
                x => x!["sha256"]!.GetValue<string>());
            var stagedData = input["stagedProgressionDataHashes"]?.AsArray().ToDictionary(
                x => x!["path"]!.GetValue<string>(), x => x!["sha256"]!.GetValue<string>());
            var expectedData = active.Candidate.Freeze["progressionData"]!.AsArray().ToDictionary(
                x => "data/progression/" + Path.GetFileName(x!["path"]!.GetValue<string>()), x => x!["sha256"]!.GetValue<string>());
            job.ActualCandidateVerified &= stagedRuntime is not null && stagedRuntime.Count == expectedRuntime.Count &&
                expectedRuntime.All(kv => stagedRuntime.GetValueOrDefault(kv.Key) == kv.Value) &&
                stagedData is not null && stagedData.Count == expectedData.Count && expectedData.All(kv => stagedData.GetValueOrDefault(kv.Key) == kv.Value);
            var request = files.ReadObject(JobStore.DirectoryFor(job.JobId) + "/request.json");
            int expectedPacing = request["parameters"]?["connectionPacingMilliseconds"]?.GetValue<int>() ?? 0;
            if (input["connectionPacingMilliseconds"]?.GetValue<int>() != expectedPacing)
                return "Actual connection pacing differs from the explicit request.";
            var checks = summary["checks"]?.AsArray() ?? [];
            job.PassedAssertions = checks.Count(c => c?["passed"]?.GetValue<bool>() == true);
            job.FailedAssertions = checks.Count - job.PassedAssertions; job.ExecutedAssertions = checks.Count;
            job.FirstFailedAssertion = checks.FirstOrDefault(c => c?["passed"]?.GetValue<bool>() != true)?["check"]?.GetValue<string>();
            job.Triggered = checks.Count > 0; job.Observed = job.Triggered && job.ActualCandidateVerified;
            string? resultStatus = summary["status"]?.GetValue<string>();
            string? failure = summary["failure"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(failure)) job.Failure = DevFiles.SafeText(failure);
            if (resultStatus != "passed") return "Runner summary status is " + resultStatus + ": " + DevFiles.SafeText(failure);
            if (summary["syntheticClient"]?.GetValue<bool>() != true || summary["runSafetyClean"]?.GetValue<bool>() != true ||
                summary["forcedStop"]?.GetValue<bool>() != false || summary["exitCodes"]?.AsArray().Any(c => c!.GetValue<int>() != 0) != false)
                return "Real server result lacks synthetic-client identity and strict clean shutdown.";
            // These scenarios reuse existing runners, whose report names retain their original
            // milestone. Common/Production runs already validated their full summary above.
            if (active.Scenario.Id is not (ScenarioId.TcpRegression or ScenarioId.TcpProduction))
            {
                string? detailProblem = ReadScenarioCompletion(files, active.Scenario.Id, job.ReportDirectory, out string detailedPath);
                AddArtifact(job, detailedPath, "selected_scenario_result");
                if (detailProblem is not null) return detailProblem;
                if (active.Scenario.Id == ScenarioId.TcpSolarTablet)
                {
                    string sourcesPath = job.ReportDirectory + "/m12-natural-sources/summary.json";
                    if (!File.Exists(files.PathFor(sourcesPath))) return "M12 natural-source subscenario summary is missing.";
                    AddArtifact(job, sourcesPath, "natural_source_premise_result");
                    if (files.ReadObject(sourcesPath)["status"]?.GetValue<string>() != "passed")
                        return "M12 natural-source subscenario did not pass.";
                }
            }
        }
        else
        {
            var results = summary["results"]?.AsArray() ?? [];
            static int Count(JsonNode? n) => int.TryParse(n?.ToString(), out int result) ? result : 0;
            job.PassedAssertions = results.Sum(r => Count(r?["passed"]));
            job.FailedAssertions = results.Sum(r => Count(r?["failed"]));
            job.ExecutedAssertions = results.Sum(r => Count(r?["executed"]));
            job.Triggered = job.ExecutedAssertions > 0; job.Observed = job.Triggered;
            string loadedIdentityPath = JobStore.DirectoryFor(job.JobId) + "/core-loaded-identity.json";
            if (File.Exists(files.PathFor(loadedIdentityPath)))
            {
                var loaded = files.ReadObject(loadedIdentityPath, 64 * 1024);
                job.ActualCandidateVerified = loaded["jobId"]?.GetValue<string>() == job.JobId &&
                    loaded["instanceId"]?.GetValue<string>() == job.InstanceId &&
                    loaded["core"]?["sha256"]?.GetValue<string>() == active.Candidate.Assemblies["AntiCheat.Core.dll"] &&
                    loaded["core"]?["fileMvidMatchesLoaded"]?.GetValue<bool>() == true;
                AddArtifact(job, loadedIdentityPath, "actual_core_test_process_loaded_identity");
            }
            foreach (string trx in Directory.EnumerateFiles(files.PathFor(job.ReportDirectory), "*.trx").Take(8))
            {
                AddArtifact(job, files.Relative(trx), "core_trx");
                if (job.FailedAssertions > 0 && job.FirstFailedAssertion is null)
                {
                    using var xml = files.OpenRead(files.Relative(trx));
                    var document = System.Xml.Linq.XDocument.Load(xml);
                    job.FirstFailedAssertion = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "UnitTestResult" &&
                        x.Attribute("outcome")?.Value == "Failed")?.Attribute("testName")?.Value;
                }
            }
            if (results.Count == 0 || results.Any(r => r?["status"]?.GetValue<string>() != "passed") || job.ExecutedAssertions == 0)
                return "Existing Core runner did not execute a passing test selection (zero selected tests is not success).";
        }
        return null;
    }

    internal static string? ReadScenarioCompletion(DevFiles files, ScenarioId scenarioId, string reportDirectory, out string detailedPath)
    {
        var (relativeDetail, expectedDetailStatus) = scenarioId switch
        {
            ScenarioId.TcpHandshake => ("m10-a03/summary.json", "passed"),
            ScenarioId.TcpSentry => ("m11-sentry/summary.json", "passed"),
            ScenarioId.TcpSummon => ("m10-summon/summary.json", "passed"),
            ScenarioId.TcpQuickStack => ("m10-quick-stack/summary.json", "passed"),
            ScenarioId.TcpSolarTablet => ("m11-solar-tablet/summary.json", "passed"),
            ScenarioId.TcpClassEmblems => ("m12-equipment/summary.json", "passed"),
            ScenarioId.TcpReceiveIsolation => ("m12-receive-isolation/summary.json", "passed"),
            // Completion of these measurements is not conservation or scheduler qualification.
            ScenarioId.TcpLiquidControlOn => ("m10-liquid-control/summary.json", "guard-on-observed"),
            ScenarioId.TcpLiquidControlOff => ("m10-liquid-control/summary.json", "guard-off-control-observed"),
            _ => ("m9-" + scenarioId.ToString()[3..].ToLowerInvariant() + "/summary.json", "passed")
        };
        detailedPath = reportDirectory + "/" + relativeDetail;
        if (!File.Exists(files.PathFor(detailedPath))) return "Selected scenario summary is missing.";
        // Only registered liquid detail snapshots get this explicit bound. Common summaries,
        // inputs and all other details retain DevFiles' 4 MiB actual-byte read limit.
        JsonObject detailed;
        if (scenarioId is ScenarioId.TcpLiquidControlOn or ScenarioId.TcpLiquidControlOff)
        {
            using var pins = WindowsPathPins.ForFile(files.PathFor(detailedPath));
            detailed = JsonSerializer.Deserialize<JsonObject>(pins.ReadRegisteredLiquidDetail(), DevFiles.Json)
                ?? throw new DevProblem("invalid_json", "Empty JSON record.");
        }
        else detailed = files.ReadObject(detailedPath);
        return detailed["status"]?.GetValue<string>() == expectedDetailStatus
            ? null : "Selected scenario did not report its registered completion status.";
    }

    private void AddArtifact(JobRecord job, string relative, string layer)
    {
        string path = files.PathFor(relative);
        if (!File.Exists(path)) return;
        string id = "job:" + job.JobId + ":" + DevFiles.HashText(relative)[..20].ToLowerInvariant();
        if (job.Artifacts.Any(a => a.ArtifactId == id)) return;
        if (job.Artifacts.Count >= MaximumArtifacts) return; // Original material remains on disk; result exposes the bounded index.
        job.Artifacts.Add(new(id, relative, layer, new FileInfo(path).Length));
    }

    private JsonObject JobData(JobRecord job, bool duplicate = false) => new()
    {
        ["job_id"] = job.JobId, ["status"] = job.Status, ["phase"] = job.Phase, ["duplicate_request"] = duplicate,
        ["scenario_id"] = job.ScenarioId, ["candidate_id"] = job.CandidateId, ["evidence_layer"] = job.EvidenceLayer,
        ["exit_code"] = job.ExitCode, ["passed_assertions"] = job.PassedAssertions, ["failed_assertions"] = job.FailedAssertions,
        ["executed_assertions"] = job.ExecutedAssertions, ["first_failed_assertion"] = job.FirstFailedAssertion,
        ["failure"] = DevFiles.SafeText(job.Failure), ["actual_candidate_verified"] = job.ActualCandidateVerified,
        ["prepared"] = job.Prepared, ["triggered"] = job.Triggered, ["observed"] = job.Observed,
        ["cancel_requested"] = job.CancelRequestedUtc is not null, ["cancel_reason"] = DevFiles.SafeText(job.CancelReason),
        ["cleanup"] = job.Cleanup, ["forced_cleanup"] = job.ForcedCleanup, ["owned_processes_exited"] = job.OwnedProcessesExited,
        ["owner"] = new JsonObject { ["instance_id"] = job.InstanceId, ["service_pid"] = job.ServicePid,
            ["service_started_utc"] = job.ServiceStartedUtc.ToString("O"), ["runner_pid"] = job.RunnerPid,
            ["job_object"] = job.ProcessJobName, ["members"] = DevFiles.Node(job.Members),
            ["member_snapshot_incomplete"] = job.MemberSnapshotIncomplete },
        ["report_directory"] = job.ReportDirectory, ["isolated_directory"] = job.IsolatedDirectory, ["loopback_port"] = job.LoopbackPort,
        ["timings_ms"] = new JsonObject { ["admission"] = job.AdmissionMilliseconds, ["execution"] = job.ExecutionMilliseconds,
            ["summary"] = job.SummaryMilliseconds },
        ["captured_stdout_bytes"] = job.StdoutBytes, ["captured_stderr_bytes"] = job.StderrBytes,
        ["dropped_output_bytes"] = job.DroppedOutputBytes, ["artifacts"] = DevFiles.Node(job.Artifacts.Take(32)),
        ["artifact_index_id"] = job.Artifacts.FirstOrDefault(a => a.RelativePath.EndsWith("/artifact-index.json", StringComparison.Ordinal))?.ArtifactId,
        ["result_artifact_id"] = job.Artifacts.FirstOrDefault(a => a.RelativePath == JobStore.DirectoryFor(job.JobId) + "/result.json")?.ArtifactId,
        ["phase_timings"] = job.Lifecycle?["phaseTimings"]?.DeepClone(),
        ["artifact_index_count"] = job.Artifacts.Count, ["artifact_index_capacity"] = MaximumArtifacts,
        ["client_session_id"] = job.ClientSessionId, ["client_result"] = job.ClientResult?.DeepClone(),
        ["qualification_changed"] = false, ["client_gui"] = job.ClientSessionId is null ? "not_executed" : "original_input_server_observation_only",
        ["automatic_retries"] = 0
    };

    internal static void ValidateParameters(Scenario scenario, JobParameters parameters)
    {
        if (parameters.ConnectionPacingMilliseconds is not (0 or 200) || !scenario.IsTcp && parameters.ConnectionPacingMilliseconds != 0)
            throw new DevProblem("invalid_parameters", "Only TCP scenarios accept explicit pacing 0 or 200; the default is always 0.");
        if (scenario.IsClient ? parameters.ClientSessionId is null || !Regex.IsMatch(parameters.ClientSessionId, "^[a-f0-9]{32}$") :
            parameters.ClientSessionId is not null)
            throw new DevProblem("invalid_parameters", "Only Client scenarios require and accept an audited 32-character lowercase hexadecimal clientSessionId.");
    }
}
