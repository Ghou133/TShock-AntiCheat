using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntiCheat.Core;

namespace AntiCheat.Plugin.TShock;

public sealed record M2Configuration
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecutionScope ExecutionScope { get; init; } = ExecutionScope.ObserveOnly;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public M18CandidateMode M18CandidateMode { get; init; } = M18CandidateMode.Auto;

    // Candidate controls do not grant a rule production qualification.
    public bool M18RecordObservations { get; init; } = true;
    public bool M18EnableBlocks { get; init; } = true;
    public bool? M18EnableServiceKick { get; init; }
    public bool M18EnablePermanentSanctions { get; init; }

    // Disabled by default. This does not control packet48, pump, statue or region protections.
    public bool ExperimentalLiquidScheduler { get; init; }

    public static (M2Configuration Configuration, string Reason) Load(string savePath, IPAddress? bindAddress)
    {
        try
        {
            var path = Path.Combine(savePath, "anticheat.json");
            if (!File.Exists(path)) return (new(), "default-observation");
            using var stream = File.OpenRead(path);
            if (stream.Length > 8192) return (new(), "configuration-too-large");
            var config = JsonSerializer.Deserialize<M2Configuration>(stream) ?? new();
            if (!Enum.IsDefined(config.ExecutionScope)) return (new(), "invalid-execution-scope");
            if (!Enum.IsDefined(config.M18CandidateMode)) return (new(), "invalid-m18-candidate-mode");
            if (config.M18CandidateMode == M18CandidateMode.TestLabCandidate &&
                config.ExecutionScope != ExecutionScope.TestLab)
                return (DisableM18Candidate(config), "m18-candidate-scope-mismatch-candidate-disabled");
            if (config.M18CandidateMode == M18CandidateMode.ProductionCandidate &&
                config.ExecutionScope != ExecutionScope.Production)
                return (DisableM18Candidate(config), "m18-candidate-scope-mismatch-candidate-disabled");

            // Production admission is compiled independently. Explicit candidates additionally
            // require the same owned loopback lab boundary; this is not a public-server mode.
            if (config.ExecutionScope == ExecutionScope.Production &&
                config.M18CandidateMode == M18CandidateMode.ProductionCandidate)
            {
                var productionBoundary = ValidateIsolatedBoundary(savePath, bindAddress);
                return productionBoundary is null
                    ? (config, "isolated-loopback-production-candidate")
                    : (DisableM18Candidate(config, preserveProductionObservations: true),
                        "production-candidate-" + productionBoundary + "-candidate-disabled-production-preserved");
            }

            if (config.ExecutionScope != ExecutionScope.TestLab)
                return (config, "production-rules-require-code-admission");

            var labBoundary = ValidateIsolatedBoundary(savePath, bindAddress);
            if (labBoundary is not null)
                return (DisableM18Candidate(config), labBoundary + "-m18-candidate-disabled");
            return (config, "isolated-loopback-testlab");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (new(), "configuration-unavailable-" + ex.GetType().Name);
        }
    }

    private static string? ValidateIsolatedBoundary(string savePath, IPAddress? bindAddress)
    {
        var labRoot = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        if (bindAddress is null || !IPAddress.IsLoopback(bindAddress) || string.IsNullOrWhiteSpace(labRoot)
            || !Path.IsPathFullyQualified(labRoot)) return "testlab-isolation-not-established";
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(labRoot));
        var save = Path.GetFullPath(savePath);
        // Windows and Unix have different path-comparison semantics. A case-distinct Unix
        // sibling must never inherit the marker or rule qualification of the intended lab.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var marker = Path.Combine(root, ".anticheat-lab");
        if (!save.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || !File.Exists(marker)) return "testlab-marker-or-savepath-mismatch";
        if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            return "testlab-reparse-marker-rejected";

        bool rootFound = false;
        // Inspect ancestors above the lab root too: an otherwise ordinary lab directory can
        // itself be reached through a symlink/junction in one of its parent components.
        // This startup check is not a sandbox against concurrent edits by a local administrator.
        for (var directory = new DirectoryInfo(save); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) return "testlab-boundary-not-found";
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return "testlab-reparse-path-rejected";
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), root, comparison))
                rootFound = true;
        }
        return rootFound ? null : "testlab-boundary-not-found";
    }

    private static M2Configuration DisableM18Candidate(M2Configuration config,
        bool preserveProductionObservations = false)
        => config with
        {
            // Failed isolation must also revoke global TestLab rule qualification.
            ExecutionScope = !preserveProductionObservations && config.ExecutionScope == ExecutionScope.TestLab
                ? ExecutionScope.ObserveOnly
                : config.ExecutionScope,
            M18CandidateMode = preserveProductionObservations ? M18CandidateMode.Auto : M18CandidateMode.Disabled,
            M18EnableBlocks = false,
            M18EnableServiceKick = false,
            M18EnablePermanentSanctions = false,
        };
}
