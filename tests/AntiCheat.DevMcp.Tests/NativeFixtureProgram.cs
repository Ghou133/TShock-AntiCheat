using System.Diagnostics;
using System.Text.Json;
using AntiCheat.DevMcp;

namespace AntiCheat.DevMcp.Tests;

// This executable is only a test fixture. There is no command, script, URL, or arbitrary
// executable mode. Each process has a short independent deadline in case its test fails.
internal static class NativeFixtureProgram
{
    internal const string EntryArgument = "--devmcp-native-fixture";
    internal const string TokenVariable = "ANTICHEAT_DEVMCP_NATIVE_FIXTURE_TOKEN";
    internal const int FloodBytes = 4 * 1024 * 1024 + 256 * 1024;
    internal static string Executable => Path.ChangeExtension(typeof(NativeFixtureProgram).Assembly.Location, ".exe");

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args[0] != EntryArgument ||
            !Guid.TryParseExact(Environment.GetEnvironmentVariable(TokenVariable), "N", out _)) return 64;
        using var watchdog = new Timer(static _ => Environment.Exit(72), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        try
        {
            switch (args[1])
            {
                case "echo":
                    Console.Write(JsonSerializer.Serialize(args[2..]));
                    return 0;
                case "flood":
                    if (args.Length != 2) return 64;
                    await Task.WhenAll(Flood(Console.OpenStandardOutput(), (byte)'O'), Flood(Console.OpenStandardError(), (byte)'E'));
                    return 0;
                case "hold":
                    if (args.Length != 4 || args[3] is not ("sentinel" or "grandchild")) return 64;
                    string holdDirectory = ValidateDirectory(args[2]);
                    Publish(holdDirectory, args[3] + ".json", Self());
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    return 0;
                case "tree-child":
                    if (args.Length != 3) return 64;
                    string childDirectory = ValidateDirectory(args[2]);
                    using (var grandchild = StartFixture("hold", childDirectory, "grandchild"))
                    {
                        await UntilFile(childDirectory, "grandchild.json");
                        Publish(childDirectory, "child.json", Self());
                        await Task.Delay(TimeSpan.FromSeconds(20));
                    }
                    return 0;
                case "tree-root":
                case "tree-root-exit":
                    if (args.Length != 3) return 64;
                    string treeDirectory = ValidateDirectory(args[2]);
                    using (var child = StartFixture("tree-child", treeDirectory))
                    {
                        await UntilFile(treeDirectory, "child.json");
                        Publish(treeDirectory, "root.json", Self());
                        if (args[1] == "tree-root") await Task.Delay(TimeSpan.FromSeconds(20));
                    }
                    return 0;
                case "lease":
                    if (args.Length != 4 || args[3] is not ("first" or "second" or "third")) return 64;
                    string leaseDirectory = ValidateDirectory(args[2]), label = args[3];
                    var result = CrossProcessLease.TryAcquire(Path.Combine(leaseDirectory, "shared.lock"),
                        LeaseOwner.Current("native-fixture-" + label, label));
                    using (result.Lease)
                    {
                        Publish(leaseDirectory, label + ".json", new { result.Acquired, result.BlockedReason, Owner = Self() });
                        if (!result.Acquired) return 23;
                        await UntilFile(leaseDirectory, label + ".release", TimeSpan.FromSeconds(12));
                    }
                    return 0;
                default: return 64;
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 70; }
    }

    internal static Process StartFixture(params string[] arguments)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(EntryArgument);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Native fixture did not start.");
    }

    internal static OwnedProcessMember Self()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime(), Environment.ProcessPath!);
    }

    private static async Task Flood(Stream stream, byte value)
    {
        using (stream)
        {
            byte[] block = new byte[65536]; Array.Fill(block, value);
            for (int written = 0; written < FloodBytes; written += block.Length)
                await stream.WriteAsync(block.AsMemory(0, Math.Min(block.Length, FloodBytes - written)));
            await stream.FlushAsync();
        }
    }

    internal static string ProjectRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "START_HERE.md")) &&
                File.Exists(Path.Combine(directory.FullName, "tools", "AntiCheat.DevMcp", "AntiCheat.DevMcp.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Test executable must remain inside the owned project checkout.");
    }

    private static string ValidateDirectory(string supplied)
    {
        string full = WindowsPathPins.Normalize(supplied);
        string parent = Path.Combine(ProjectRoot(), ".lab", "devmcp-native-tests");
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new InvalidDataException("Fixture directory is outside its owned test root.");
        using var pins = WindowsPathPins.ForFile(Path.Combine(full, ".native-fixture.json"));
        using var stream = pins.OpenRead();
        if (stream.Length > 1024) throw new InvalidDataException("Fixture marker is oversized.");
        using var marker = JsonDocument.Parse(stream);
        if (marker.RootElement.GetProperty("token").GetString() != Environment.GetEnvironmentVariable(TokenVariable))
            throw new InvalidDataException("Fixture marker belongs to another test.");
        return full;
    }

    private static async Task UntilFile(string directory, string file, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, file)))
        {
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture handshake timed out: " + file);
            await Task.Delay(25);
        }
    }

    private static void Publish(string directory, string file, object value)
    {
        string path = Path.Combine(directory, file), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path);
    }
}
