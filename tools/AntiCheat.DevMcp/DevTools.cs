using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AntiCheat.DevMcp;

[McpServerToolType]
internal static class DevTools
{
    [McpServerTool(Name = "client_state", ReadOnly = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("Read the selected player and bounded chest state from an audited isolated session's fresh server snapshot. UI-only facts remain unknown. No input, screenshot, process launch or internal client call is performed.")]
    public static Task<CallToolResult> ClientState(DevOperations operations, CancellationToken cancellationToken,
        [Required, MinLength(32), MaxLength(32), RegularExpression("^[a-f0-9]{32}$")] string session_id)
        => Invoke(() => operations.ClientState(session_id), cancellationToken);

    [McpServerTool(Name = "project_status", ReadOnly = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("Read the current locked target, candidate, original-task status and recorded blockers without starting a test.")]
    public static Task<CallToolResult> ProjectStatus(DevOperations operations, CancellationToken cancellationToken,
        [Description("Optional original 72-task ID."), MaxLength(6), RegularExpression(@"^(v2:)?[A-L][0-9]{2}$")] string? task_id = null,
        [Range(0, 256)] int offset = 0, [Range(1, 100)] int limit = 25)
        => Invoke(() => operations.ProjectStatus(task_id, offset, limit), cancellationToken);

    [McpServerTool(Name = "scenario_list", ReadOnly = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("List registered scenarios, actual supported parameters and required resources; no test is started.")]
    public static Task<CallToolResult> ScenarioList(DevOperations operations, CancellationToken cancellationToken,
        ScenarioCategory? category = null, [MaxLength(40), RegularExpression("^[a-zA-Z0-9_-]+$")] string? tag = null,
        [Range(0, 256)] int offset = 0, [Range(1, 100)] int limit = 25)
        => Invoke(() => operations.ScenarioList(category, tag, offset, limit), cancellationToken);

    [McpServerTool(Name = "job_start", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("Start one registered owned scenario against an explicit frozen candidate. Return a job ID promptly; query it for the actual outcome. Reuse the deduplication key only for the same intended start.")]
    public static Task<CallToolResult> JobStart(DevOperations operations, CancellationToken cancellationToken,
        ScenarioId scenario_id, [Required, MinLength(64), MaxLength(64), RegularExpression("^[A-Fa-f0-9]{64}$")] string candidate_id,
        [Required, MinLength(1), MaxLength(80), RegularExpression(@"^[A-Za-z0-9_.:-]{1,80}$")] string deduplication_key, JobParameters? parameters = null)
        => Invoke(() => operations.JobStart(scenario_id, candidate_id, parameters, deduplication_key), cancellationToken);

    [McpServerTool(Name = "job_status", ReadOnly = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("Read a bounded status and evidence page for a registered owned job. A running process or exit code alone is not test acceptance.")]
    public static Task<CallToolResult> JobStatus(DevOperations operations, CancellationToken cancellationToken,
        [Required, MinLength(32), MaxLength(32), RegularExpression("^[a-f0-9]{32}$")] string job_id, [Range(0, int.MaxValue)] int cursor = 0,
        [Range(1, 16384)] int max_chars = 4096)
        => Invoke(() => operations.JobStatus(job_id, cursor, max_chars), cancellationToken);

    [McpServerTool(Name = "job_cancel", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("Request cancellation and cleanup of this tool's registered owned job. It does not stop arbitrary PIDs or alter test outcomes.")]
    public static Task<CallToolResult> JobCancel(DevOperations operations, CancellationToken cancellationToken,
        [Required, MinLength(32), MaxLength(32), RegularExpression("^[a-f0-9]{32}$")] string job_id, [Required, MaxLength(256)] string reason)
        => Invoke(() => operations.JobCancel(job_id, reason), cancellationToken);

    [McpServerTool(Name = "artifact_read", ReadOnly = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DevReply))]
    [Description("Read a bounded page from a registered artifact ID. Arbitrary filesystem paths and unregistered files are not accepted.")]
    public static Task<CallToolResult> ArtifactRead(DevOperations operations, CancellationToken cancellationToken,
        [Required, MaxLength(100)] string artifact_id, [Range(0, int.MaxValue)] int cursor = 0,
        [Range(1, 16384)] int max_chars = 4096)
        => Invoke(() => operations.ArtifactRead(artifact_id, cursor, max_chars), cancellationToken);

    private static async Task<CallToolResult> Invoke(Func<Task<DevReply>> operation, CancellationToken cancellationToken)
    {
        // This cancels admission to this request, not an independently registered job.
        // Operations validate input and retain job ownership/cleanup beyond the request.
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await operation().ConfigureAwait(false);
        return new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(reply, Cli.JsonOptions),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(reply, Cli.JsonOptions) }],
            IsError = reply.Error is not null
        };
    }
}
