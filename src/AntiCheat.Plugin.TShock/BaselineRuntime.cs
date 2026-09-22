using System.Reflection;
using System.Security.Cryptography;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record RuntimeStatus(bool ExactTemporaryBaseline, string GameVersion, string Reason);

/// <summary>The release artifact hashes identify a temporary 1.4.5.6 baseline, never target qualification.</summary>
public static class BaselineRuntime
{
    public const string TargetVersion = "1.4.5.8";
    public const string TemporaryVersion = "1.4.5.6";
    public const string TShockSha256 = "DBF9E0D01B007A4FD8671B7452C5F779226264F1B22D76CF8CDC1CEE56D00FE7";
    public const string TsApiSha256 = "4BD65F285E261133545821C1249CBF800A5352A716B49989152378519B6B83E1";
    public const string OtApiSha256 = "AC9276C730493BB6F2B38BCD6BD2D1A174F25B0B1313FFC069871457124363D3";

    public static RuntimeStatus Inspect()
    {
        try
        {
            var matched = Matches(typeof(TSPlayer).Assembly, TShockSha256)
                && Matches(typeof(TerrariaPlugin).Assembly, TsApiSha256)
                && Matches(typeof(Main).Assembly, OtApiSha256);
            matched = matched && Main.versionNumber.TrimStart('v') == TemporaryVersion;
            return new(matched, Main.versionNumber, matched
                ? "temporary-baseline; A02-production-proof-unqualified"
                : "unrecognized-runtime; affected-rule-observation-disabled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(false, "unknown", "runtime-hash-unavailable");
        }
    }

    private static bool Matches(Assembly assembly, string expected)
    {
        // Single-file hosts may expose embedded assemblies with an empty Location. Until the launcher
        // plus embedded module identity is independently locked, this is Unknown and never an init failure.
        if (string.IsNullOrEmpty(assembly.Location)) return false;
        using var stream = File.OpenRead(assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream)) == expected;
    }
}
