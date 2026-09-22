using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AntiCheat.DevMcp;

internal sealed class CliInputException(string message) : Exception(message);

internal static class Cli
{
    internal const int MaximumRequestBytes = 64 * 1024;
    internal static readonly TimeSpan CancellationWaitLimit = TimeSpan.FromSeconds(45);
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 16,
            IncludeFields = true,
            RespectNullableAnnotations = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    internal static async Task<int> InvokeAsync(DevOperations operations, string tool, CancellationToken cancellationToken)
    {
        if (tool == "job_start")
        {
            await WriteAsync(Error("use_run", "A short CLI cannot own a background job. Use run --project-root ABS with the same job_start JSON request."));
            return 2;
        }
        if (tool is not ("project_status" or "scenario_list" or "job_status" or "job_cancel" or "artifact_read" or "client_state"))
            throw new CliInputException("Unknown tool name.");

        using var request = await ReadRequestAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var reply = tool switch
        {
            "project_status" => await ProjectStatus(operations, Deserialize<ProjectStatusInput>(request)),
            "scenario_list" => await ScenarioList(operations, Deserialize<ScenarioListInput>(request)),
            "job_status" => await JobStatus(operations, Deserialize<JobStatusInput>(request)),
            "job_cancel" => await JobCancel(operations, Deserialize<JobCancelInput>(request)),
            "artifact_read" => await ArtifactRead(operations, Deserialize<ArtifactReadInput>(request)),
            "client_state" => await ClientState(operations, Deserialize<ClientStateInput>(request)),
            _ => throw new CliInputException("Unknown tool name.")
        };
        await WriteAsync(reply);
        return reply.Error is null ? 0 : 2;
    }

    internal static async Task<int> RunAsync(DevOperations operations, CancellationToken cancellationToken)
    {
        using var request = await ReadRequestAsync(cancellationToken);
        var start = Deserialize<JobStartInput>(request);
        cancellationToken.ThrowIfCancellationRequested();

        return await RunForegroundAsync(
            () => operations.JobStart(start.ScenarioId, start.CandidateId, start.Parameters, start.DeduplicationKey),
            id => operations.JobStatus(id, 0, 1), operations.JobCancel, WriteAsync,
            cancellationToken, CancellationWaitLimit);
    }

    // The narrow delegate boundary lets tests exercise the actual foreground lifecycle without
    // starting a server or mutating the shared candidate. Production always uses DevOperations.
    internal static async Task<int> RunForegroundAsync(Func<Task<DevReply>> startJob,
        Func<string, Task<DevReply>> readStatus, Func<string, string, Task<DevReply>> cancelJob,
        Func<DevReply, Task> writeReply, CancellationToken cancellationToken, TimeSpan cancellationWaitLimit)
    {
        if (cancellationWaitLimit <= TimeSpan.Zero || cancellationWaitLimit > CancellationWaitLimit)
            throw new ArgumentOutOfRangeException(nameof(cancellationWaitLimit));
        string? jobId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = await startJob();
            jobId = started.Error is null ? StringValue(started.Data, "job_id") : null;
            // Capture identity before fallible output so a coincident Ctrl-C can request cancellation.
            await writeReply(started);
            if (started.Error is not null) return 2;
            if (string.IsNullOrWhiteSpace(jobId))
                throw new InvalidOperationException("A successful job_start reply did not include job_id.");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await readStatus(jobId);
                if (status.Error is not null)
                {
                    await writeReply(status);
                    return 2;
                }
                string state = JobState(status);
                if (IsTerminal(state))
                {
                    await writeReply(status);
                    return TerminalExitCode(state);
                }
                if (state is not ("queued" or "running" or "canceling" or "cancelling" or "stopping"))
                {
                    await writeReply(Error("unknown_job_state", "The status reply did not contain a recognized job state; ending the foreground owner."));
                    return 2;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (jobId is not null)
            {
                var canceled = await cancelJob(jobId, "CLI foreground run canceled by Ctrl-C.");
                // Do not reuse the canceled input token. The foreground owner must remain alive
                // through monitor classification and owned cleanup, including a broken output pipe.
                var terminal = canceled.Error is not null ? canceled :
                    await WaitForCancellationTerminalAsync(jobId, canceled, readStatus, cancellationWaitLimit);
                await writeReply(terminal);
                return terminal.Error is not null ? 2 : TerminalExitCode(JobState(terminal));
            }
            await writeReply(Error("canceled", "The run was canceled before a job ID was returned."));
            return 130;
        }
        // Program's await using owns cleanup on every exit. An output/input/status failure is
        // owner interruption, and must not manufacture an explicit public job_cancel request.
    }

    private static async Task<DevReply> WaitForCancellationTerminalAsync(string jobId, DevReply latest,
        Func<string, Task<DevReply>> readStatus, TimeSpan limit)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (latest.Error is not null || IsTerminal(JobState(latest))) return latest;
            if (JobState(latest) is not ("queued" or "running" or "canceling" or "cancelling" or "stopping"))
                return Error("unknown_job_state", "The cancellation reply did not contain a recognized job state; ending the foreground owner.");
            var remaining = limit - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return CancellationWaitExpired();
            try { latest = await readStatus(jobId).WaitAsync(remaining); }
            catch (TimeoutException) { return CancellationWaitExpired(); }
            if (latest.Error is not null || IsTerminal(JobState(latest))) return latest;
            remaining = limit - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return CancellationWaitExpired();
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250));
        }
    }

    private static DevReply CancellationWaitExpired() => Error("cancellation_wait_timeout",
        "Cancellation was requested, but no terminal result was observed within the foreground wait limit. Owned shutdown cleanup will continue; no successful cancellation is claimed.");

    private static int TerminalExitCode(string state) => state == "passed" ? 0 : state == "canceled" ? 130 : 1;

    private static string JobState(DevReply reply)
        => (StringValue(reply.Data, "status") ?? StringValue(reply.Data, "state") ?? reply.Status).ToLowerInvariant();

    private static bool IsTerminal(string state)
        => state is "passed" or "failed" or "blocked" or "canceled" or "interrupted";

    private static string? StringValue(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static async Task<JsonDocument> ReadRequestAsync(CancellationToken cancellationToken)
    {
        // One complete JSON object, terminated by stdin EOF. Read bytes so the cap also
        // bounds UTF-8 input; JsonDocument rejects malformed encoding and trailing JSON.
        var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumRequestBytes)
                throw new CliInputException("The JSON request exceeds the 64 KiB input limit.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 16,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new CliInputException("The request must be exactly one JSON object.");
                RejectDuplicateProperties(document.RootElement);
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw new CliInputException("The request must be valid UTF-8 JSON with a maximum depth of 16."); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CliInputException("Duplicate JSON property names are not accepted.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static T Deserialize<T>(JsonDocument document) where T : class
    {
        try { return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new CliInputException("The request cannot be null."); }
        catch (JsonException)
        {
            throw new CliInputException("Request fields do not match the selected tool. Use exact field names and registered string enum values.");
        }
    }

    internal static DevReply Error(string code, string message) => new("error", new JsonObject { ["code"] = code }, message);
    internal static Task WriteAsync(DevReply reply) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions));

    private static Task<DevReply> ProjectStatus(DevOperations operations, ProjectStatusInput request)
        => operations.ProjectStatus(request.TaskId, request.Offset, request.Limit);
    private static Task<DevReply> ScenarioList(DevOperations operations, ScenarioListInput request)
        => operations.ScenarioList(request.Category, request.Tag, request.Offset, request.Limit);
    private static Task<DevReply> JobStatus(DevOperations operations, JobStatusInput request)
        => operations.JobStatus(request.JobId, request.Cursor, request.MaxChars);
    private static Task<DevReply> JobCancel(DevOperations operations, JobCancelInput request)
        => operations.JobCancel(request.JobId, request.Reason);
    private static Task<DevReply> ArtifactRead(DevOperations operations, ArtifactReadInput request)
        => operations.ArtifactRead(request.ArtifactId, request.Cursor, request.MaxChars);
    private static Task<DevReply> ClientState(DevOperations operations, ClientStateInput request)
        => operations.ClientState(request.SessionId);

    private sealed class ClientStateInput
    {
        [JsonPropertyName("session_id")] public required string SessionId { get; init; }
    }

    private sealed class ProjectStatusInput
    {
        [JsonPropertyName("task_id")] public string? TaskId { get; init; }
        public int Offset { get; init; }
        public int Limit { get; init; } = 25;
    }
    private sealed class ScenarioListInput
    {
        public ScenarioCategory? Category { get; init; }
        public string? Tag { get; init; }
        public int Offset { get; init; }
        public int Limit { get; init; } = 25;
    }
    private sealed class JobStartInput
    {
        [JsonPropertyName("scenario_id")] public required ScenarioId ScenarioId { get; init; }
        [JsonPropertyName("candidate_id")] public required string CandidateId { get; init; }
        [JsonPropertyName("deduplication_key")] public required string DeduplicationKey { get; init; }
        public JobParameters? Parameters { get; init; }
    }
    private sealed class JobStatusInput
    {
        [JsonPropertyName("job_id")] public required string JobId { get; init; }
        public int Cursor { get; init; }
        [JsonPropertyName("max_chars")] public int MaxChars { get; init; } = 4096;
    }
    private sealed class JobCancelInput
    {
        [JsonPropertyName("job_id")] public required string JobId { get; init; }
        public required string Reason { get; init; }
    }
    private sealed class ArtifactReadInput
    {
        [JsonPropertyName("artifact_id")] public required string ArtifactId { get; init; }
        public int Cursor { get; init; }
        [JsonPropertyName("max_chars")] public int MaxChars { get; init; } = 4096;
    }
}
