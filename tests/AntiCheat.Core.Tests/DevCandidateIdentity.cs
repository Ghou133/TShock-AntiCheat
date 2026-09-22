using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AntiCheat.DevMcp;
using NUnit.Framework;

// Assembly-wide NUnit infrastructure witness, deliberately outside a namespace.
// Source: this project's Core test integration; no business assertions or third-party code copied.
// WindowsPathPins.cs is the project's existing implementation, reused by Compile Link solely in
// this test assembly. No runtime product or test reference to the MCP executable/SDK is added.
[SetUpFixture]
public sealed class DevCandidateIdentity
{
    private const int MaximumAssemblyBytes = 4 * 1024 * 1024;
    private List<IDisposable>? held;

    [OneTimeSetUp]
    public void RecordActualLoadedCore()
    {
        string? suppliedJob = Environment.GetEnvironmentVariable("ANTICHEAT_DEV_JOB_DIR");
        if (string.IsNullOrWhiteSpace(suppliedJob)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned development identity recording requires Windows file handles.");
        held = [];
        try
        {
            Assembly tests = typeof(DevCandidateIdentity).Assembly;
            string projectRoot = FindProjectRoot(tests.Location);
            string jobDirectory = WindowsPathPins.Normalize(suppliedJob);
            string jobId = Path.GetFileName(jobDirectory);
            if (jobId.Length != 32 || jobId.Any(c => !Uri.IsHexDigit(c)) ||
                !string.Equals(Path.GetDirectoryName(jobDirectory), Path.Combine(projectRoot, "artifacts", "devmcp", "jobs"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Core identity output is outside this project's owned job directory.");

            var jobPins = WindowsPathPins.ForDirectory(jobDirectory); held.Add(jobPins);
            byte[] markerBytes = ReadPinned(Path.Combine(jobDirectory, ".terraria-dev-owned"), 16384);
            using var marker = JsonDocument.Parse(markerBytes);
            var value = marker.RootElement;
            using var owner = WindowsIdentity.GetCurrent();
            string? ownerSid = owner.User?.Value;
            string? instanceId = value.GetProperty("instanceId").GetString();
            if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("jobId").GetString() != jobId ||
                !string.Equals(WindowsPathPins.Normalize(value.GetProperty("projectRoot").GetString()!), projectRoot, StringComparison.OrdinalIgnoreCase) ||
                ownerSid is null || value.GetProperty("ownerSid").GetString() != ownerSid ||
                string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 128)
                throw new InvalidDataException("Core identity marker does not match this project, job, instance, or Windows owner.");

            // The Assembly object comes from the actual bound Core type in this test process.
            // Location is observed from that object; no bin directory or copied-DLL path is guessed.
            var core = LoadedAssembly(typeof(AntiCheat.Core.AntiCheatEngine).Assembly);
            var testAssembly = LoadedAssembly(tests);
            using var process = Process.GetCurrentProcess();
            byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, jobId, instanceId, projectRoot, capturedUtc = DateTimeOffset.UtcNow,
                source = "NUnit assembly OneTimeSetUp / typeof(AntiCheat.Core.AntiCheatEngine).Assembly",
                process = new { pid = process.Id, startedUtc = process.StartTime.ToUniversalTime(), executable = Environment.ProcessPath },
                marker = new { sha256 = Convert.ToHexString(SHA256.HashData(markerBytes)), ownerSid },
                core, testAssembly,
                filePinsHeldUntil = "NUnit assembly OneTimeTearDown",
                hashMeaning = "SHA256 of the pinned file at the actual loaded Assembly.Location; file MVID checked against the loaded module"
            }, new JsonSerializerOptions { WriteIndented = true });
            using var output = WindowsPathPins.ForFile(Path.Combine(jobDirectory, "core-loaded-identity.json"));
            output.AtomicWrite(evidence, 16384);
        }
        catch (Exception failure)
        {
            try { ReleasePins(); }
            catch (Exception cleanup) { throw new AggregateException("Core identity recording and pin cleanup failed.", failure, cleanup); }
            throw; // A witness failure is this run's setup/infrastructure failure, never a pass.
        }
    }

    [OneTimeTearDown]
    public void ReleaseActualLoadedCorePins() => ReleasePins();

    private LoadedIdentity LoadedAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
            throw new InvalidDataException("A loaded candidate assembly lacks an inspectable file location.");
        string location = WindowsPathPins.Normalize(assembly.Location);
        Guid loadedMvid = assembly.ManifestModule.ModuleVersionId;
        byte[] bytes = ReadPinned(location, MaximumAssemblyBytes);
        using var image = new PEReader(new MemoryStream(bytes, writable: false));
        if (!image.HasMetadata) throw new InvalidDataException("Loaded candidate file has no managed metadata.");
        var metadata = image.GetMetadataReader();
        Guid fileMvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        if (loadedMvid == Guid.Empty || fileMvid != loadedMvid)
            throw new InvalidDataException("The file at the loaded assembly location no longer matches the loaded module MVID.");
        return new(assembly.GetName().Name!, location, loadedMvid, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, true);
    }

    private byte[] ReadPinned(string location, int maximumBytes)
    {
        var pins = WindowsPathPins.ForFile(location); held!.Add(pins);
        var stream = pins.OpenRead(); held.Add(stream);
        // Keep this verified ordinary-file handle without write/delete sharing throughout tests.
        // The actual read is bounded independently of mutable file-length metadata.
        byte[] bytes = new byte[maximumBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes) throw new InvalidDataException("Core identity input exceeds its actual byte-read bound.");
        Array.Resize(ref bytes, count);
        return bytes;
    }

    private static string FindProjectRoot(string testAssemblyLocation)
    {
        string location = WindowsPathPins.Normalize(testAssemblyLocation);
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(location)!); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "START_HERE.md")) &&
                File.Exists(Path.Combine(directory.FullName, "tests", "AntiCheat.Core.Tests", "AntiCheat.Core.Tests.csproj")))
                return WindowsPathPins.Normalize(directory.FullName);
        }
        throw new DirectoryNotFoundException("The actual Core test assembly is outside its project checkout.");
    }

    private void ReleasePins()
    {
        if (held is null) return;
        var pins = held; held = null;
        List<Exception>? failures = null;
        for (int index = pins.Count - 1; index >= 0; index--)
        {
            try { pins[index].Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Core identity file pins could not all be released.", failures);
    }

    private sealed record LoadedIdentity(string assemblyName, string location, Guid mvid, string sha256,
        int sizeBytes, bool fileMvidMatchesLoaded);
}
