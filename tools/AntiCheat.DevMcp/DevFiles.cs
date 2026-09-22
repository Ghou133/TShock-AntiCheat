using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AntiCheat.DevMcp;

internal sealed class DevFiles
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 64,
        Converters = { new JsonStringEnumConverter() }
    };
    public string Root { get; }
    public DevFiles(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!OperatingSystem.IsWindows()) throw new DevProblem("unsupported_host", "First version requires Windows job objects.");
        CheckAncestors(Root);
        if (!File.Exists(Path.Combine(Root, "AGENTS.md")) || !File.Exists(Path.Combine(Root, "docs/target-runtime-lock.json")))
            throw new DevProblem("not_project_root", "Expected this AntiCheat project root.");
    }

    public string PathFor(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split(['/', '\\']).Any(p => p is "." or ".."))
            throw new DevProblem("unsafe_path", "Only normalized project-relative indexed paths are accepted.");
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DevProblem("outside_project", "Path leaves the registered project.");
        CheckAncestors(full);
        return full;
    }

    public string Relative(string absolute)
    {
        string full = Path.GetFullPath(absolute);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DevProblem("outside_project", "Runner reference leaves the registered project.");
        string result = Path.GetRelativePath(Root, full).Replace('\\', '/');
        PathFor(result);
        return result;
    }

    public void MakeDirectory(string relative)
    {
        string path = PathFor(relative);
        Directory.CreateDirectory(path);
        CheckAncestors(path);
    }

    public T Read<T>(string relative, long maxBytes = 4 * 1024 * 1024)
    {
        using var pins = WindowsPathPins.ForFile(PathFor(relative));
        byte[] bytes = pins.ReadBounded(checked((int)maxBytes), allowAtomicReplace: true);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new DevProblem("invalid_json", "Empty JSON record.");
    }

    public JsonObject ReadObject(string relative, long maxBytes = 4 * 1024 * 1024) => Read<JsonObject>(relative, maxBytes);

    public FileStream OpenRead(string relative, bool allowConcurrentWrite = false)
    {
        string full = PathFor(relative);
        // Native pins additionally reject hard links and keep the directory chain from being replaced.
        using var pins = WindowsPathPins.ForFile(full);
        return pins.OpenRead(allowConcurrentWrite);
    }

    public void Write<T>(string relative, T value)
    {
        using var pins = WindowsPathPins.ForFile(PathFor(relative));
        pins.AtomicWrite(JsonSerializer.SerializeToUtf8Bytes(value, Json), 4 * 1024 * 1024);
    }

    public string Hash(string relative)
    {
        using var stream = OpenRead(relative);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static void CheckAncestors(string full)
    {
        for (string? path = full; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new DevProblem("reparse_point", "Reparse points are outside this tool's authorized paths.");
        }
    }

    public static string SafeText(string? text, int maximum = 1024)
    {
        if (text is null) return "";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"(?i)(password|passwd|credential|authorization|bearer|\btoken\b|access.?token|refresh.?token|setup.?code|auth.?code|secret|uuid|chat.?text|chat.?message)"))
                lines[i] = "[credential/private-content line omitted; original retained locally]";
        }
        string cleaned = string.Join('\n', lines);
        return cleaned.Length <= maximum ? cleaned : cleaned[..maximum];
    }

    public static JsonNode? Node(object? value) => JsonSerializer.SerializeToNode(value, Json);
}
