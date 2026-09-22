using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;

namespace AntiCheat.DevMcp;

public sealed record DevReply(string Status, JsonObject Data, string? Error = null);
public enum ScenarioCategory { Core, Tcp, Control, Client }
public enum ScenarioId
{
    CoreFirstProof, CoreSessionIsolation, CoreEmptySelectionControl,
    TcpApplication, TcpCraft, TcpWorld, TcpTeleport, TcpHandshake,
    TcpSentry, TcpSummon, TcpQuickStack, TcpSolarTablet,
    TcpLiquidControlOn, TcpLiquidControlOff, TcpRegression, TcpProduction, TcpClassEmblems, TcpReceiveIsolation,
    ClientLoadoutCycle, ClientQuickStack, ClientHotbarCycle
}

public sealed class JobParameters
{
    [AllowedValues(0, 200)]
    [Description("Explicit lab disposal-to-next-connect gap: 0 (default) or 200 milliseconds. Never adjusted automatically.")]
    public int ConnectionPacingMilliseconds { get; set; }

    [RegularExpression("^[a-f0-9]{32}$"), MaxLength(32)]
    [Description("Required only for Client scenarios: previously audited isolated client session ID. The executor never launches or owns the external client.")]
    public string? ClientSessionId { get; set; }
}

internal sealed record Scenario(ScenarioId Id, ScenarioCategory Category, string Description,
    string EvidenceLayer, string Script, string[] FixedArguments, string[] Tags, int TimeoutSeconds)
{
    public bool IsTcp => Category == ScenarioCategory.Tcp;
    public bool IsClient => Category == ScenarioCategory.Client;
}

internal sealed class IndexRecord
{
    public int SchemaVersion { get; set; } = 1;
    public List<JobIndexEntry> Jobs { get; set; } = [];
}

internal sealed class JobIndexEntry
{
    public string JobId { get; set; } = "";
    public string DeduplicationKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}

internal sealed class JobRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string JobId { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public int ServicePid { get; set; }
    public DateTimeOffset ServiceStartedUtc { get; set; }
    public string ServiceExecutable { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public string CandidateId { get; set; } = "";
    public string? ClientSessionId { get; set; }
    public JsonObject? ClientResult { get; set; }
    public string EvidenceLayer { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string Phase { get; set; } = "admission";
    public string? Failure { get; set; }
    public string? FirstFailedAssertion { get; set; }
    public int? ExitCode { get; set; }
    public int PassedAssertions { get; set; }
    public int FailedAssertions { get; set; }
    public int ExecutedAssertions { get; set; }
    public bool ActualCandidateVerified { get; set; }
    public bool Prepared { get; set; }
    public bool Triggered { get; set; }
    public bool Observed { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public long AdmissionMilliseconds { get; set; }
    public long? ExecutionMilliseconds { get; set; }
    public long? SummaryMilliseconds { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelRequestedUtc { get; set; }
    public string Cleanup { get; set; } = "not_started";
    public bool ForcedCleanup { get; set; }
    public bool? OwnedProcessesExited { get; set; }
    public string? ProcessJobName { get; set; }
    public int? RunnerPid { get; set; }
    public DateTimeOffset? RunnerStartedUtc { get; set; }
    public string? RunnerExecutable { get; set; }
    public List<ProcessIdentity> Members { get; set; } = [];
    public bool MemberSnapshotIncomplete { get; set; }
    public string? ReportDirectory { get; set; }
    public string? IsolatedDirectory { get; set; }
    public int? LoopbackPort { get; set; }
    public JsonObject? Lifecycle { get; set; }
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }
    public long DroppedOutputBytes { get; set; }
    public List<ArtifactEntry> Artifacts { get; set; } = [];
}

internal sealed record ProcessIdentity(int Pid, DateTimeOffset StartedUtc, string ExecutablePath);
internal sealed record ArtifactEntry(string ArtifactId, string RelativePath, string EvidenceLayer, long Bytes);
internal sealed record Candidate(string Id, string FreezePath, string FreezeSha256, JsonObject Freeze,
    Dictionary<string, string> Assemblies);

internal sealed class DevProblem(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
