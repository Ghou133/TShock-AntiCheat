using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace AntiCheat.DevMcp;

internal sealed class JobStore
{
    public const string Base = "artifacts/devmcp";
    public const int MaximumJobs = 256;
    private readonly DevFiles files;
    private readonly string instanceId;
    public string OwnerSid { get; }

    public JobStore(DevFiles files, string instanceId)
    {
        if (!OperatingSystem.IsWindows()) throw new DevProblem("unsupported_host", "Windows user identity is required.");
        using var identity = WindowsIdentity.GetCurrent();
        OwnerSid = identity.User?.Value ?? throw new DevProblem("missing_principal", "Windows user identity is required.");
        this.files = files;
        this.instanceId = instanceId;
        files.MakeDirectory(Base + "/jobs");
        files.MakeDirectory(".lab/devmcp/leases");
        using var guard = Lock();
        if (!File.Exists(files.PathFor(Base + "/index.json"))) files.Write(Base + "/index.json", new IndexRecord());
    }

    public CrossProcessLease Lock()
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            var result = CrossProcessLease.TryAcquire(files.PathFor(".lab/devmcp/leases/index.lock"), LeaseOwner.Current(instanceId, "index"));
            if (result.Acquired) return result.Lease!;
            Thread.Sleep(15);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(3));
        throw new DevProblem("index_busy", "Another local instance is committing a short index update; no job was launched.");
    }

    public IndexRecord Index() => files.Read<IndexRecord>(Base + "/index.json", 512 * 1024);
    public void SaveIndex(IndexRecord value) => files.Write(Base + "/index.json", value);
    public static string DirectoryFor(string jobId) => Base + "/jobs/" + jobId;

    public JobRecord ReadOwned(string jobId)
    {
        ValidateId(jobId);
        var entry = Index().Jobs.FirstOrDefault(j => j.JobId == jobId && j.OwnerSid == OwnerSid)
            ?? throw new DevProblem("unknown_job", "Job is not registered to this tool and local user.");
        var job = files.Read<JobRecord>(DirectoryFor(entry.JobId) + "/job.json", 512 * 1024);
        var marker = files.ReadObject(DirectoryFor(entry.JobId) + "/.terraria-dev-owned", 16 * 1024);
        if (job.JobId != jobId || job.OwnerSid != OwnerSid || marker["jobId"]?.GetValue<string>() != jobId ||
            marker["ownerSid"]?.GetValue<string>() != OwnerSid || marker["instanceId"]?.GetValue<string>() != job.InstanceId ||
            !string.Equals(marker["projectRoot"]?.GetValue<string>(), files.Root, StringComparison.OrdinalIgnoreCase))
            throw new DevProblem("ownership_mismatch", "Registered job and ownership marker disagree.");
        return job;
    }

    public void Save(JobRecord job)
    {
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        files.Write(DirectoryFor(job.JobId) + "/job.json", job);
    }

    public JobRecord Update(string id, Action<JobRecord> mutation)
    {
        using var guard = Lock();
        var job = ReadOwned(id);
        mutation(job);
        Save(job);
        return job;
    }

    public static void ValidateId(string jobId)
    {
        if (jobId is null || !Regex.IsMatch(jobId, "^[a-f0-9]{32}$"))
            throw new DevProblem("invalid_job_id", "Expected the exact job_id returned by this tool.");
    }

    public static bool Terminal(string status) => status is "passed" or "failed" or "blocked" or "canceled" or "interrupted";
}
