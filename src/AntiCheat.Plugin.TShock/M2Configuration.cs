using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntiCheat.Core;

namespace AntiCheat.Plugin.TShock;

public sealed record M2Configuration
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecutionScope ExecutionScope { get; init; } = ExecutionScope.ObserveOnly;

    // The ordinary full-step liquid scheduler quota has a verified single-step
    // contract, but large-facility convergence/timing is still experimental.
    // This switch does not control packet48, pump, statue or region protections.
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
            if (config.ExecutionScope != ExecutionScope.TestLab) return (config, "production-rules-require-code-admission");
            var labRoot = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
            if (bindAddress is null || !IPAddress.IsLoopback(bindAddress) || string.IsNullOrWhiteSpace(labRoot)
                || !Path.IsPathFullyQualified(labRoot)) return (new(), "testlab-isolation-not-established");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(labRoot));
            var save = Path.GetFullPath(savePath);
            if (!save.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(root, ".anticheat-lab")))
                return (new(), "testlab-marker-or-savepath-mismatch");
            for (var directory = new DirectoryInfo(save); directory is not null; directory = directory.Parent)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return (new(), "testlab-reparse-path-rejected");
                if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                    return (config, "isolated-loopback-testlab");
            }
            return (new(), "testlab-boundary-not-found");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (new(), "configuration-unavailable-" + ex.GetType().Name);
        }
    }
}
