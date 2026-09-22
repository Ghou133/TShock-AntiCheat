using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture, NonParallelizable]
public sealed class LiquidResultExtractionNativeTests
{
    private const int CommonLimit = 4 * 1024 * 1024;
    private const int LiquidLimit = 16 * 1024 * 1024;
    private const string ReportDirectory = "artifacts/network-runs/liquid-result-fixture";
    private static readonly Type FilesType = typeof(DevOperations).Assembly.GetType("AntiCheat.DevMcp.DevFiles", true)!;
    private static readonly MethodInfo ReadCompletion = typeof(DevOperations).GetMethod("ReadScenarioCompletion", BindingFlags.Static | BindingFlags.NonPublic)!;

    [TestCase(ScenarioId.TcpLiquidControlOn, "guard-on-observed")]
    [TestCase(ScenarioId.TcpLiquidControlOff, "guard-off-control-observed")]
    public void RegisteredLiquidDetailReadsBeyondFourMiBThroughItsExactSixteenMiBBound(ScenarioId scenario, string status)
    {
        using var lab = new NativeTestDirectory();
        object files = CreateProject(lab);
        string path = DetailPath(lab, "m10-liquid-control");
        WriteDetail(path, status, 7_272_546); // Size of the retained real M12 liquid result.
        Assert.That(Completion(files, scenario), Is.Null);
        WriteDetail(path, status, LiquidLimit);
        Assert.That(Completion(files, scenario), Is.Null, "The exact registered limit remains readable.");
        lab.Record("liquid-detail-accepted", new { scenario, status, firstBytes = 7_272_546, exactLimitBytes = LiquidLimit });
    }

    [TestCase(ScenarioId.TcpLiquidControlOn, "guard-on-observed")]
    [TestCase(ScenarioId.TcpLiquidControlOff, "guard-off-control-observed")]
    public void RegisteredLiquidDetailRejectsOneByteBeyondSixteenMiB(ScenarioId scenario, string status)
    {
        using var lab = new NativeTestDirectory();
        object files = CreateProject(lab);
        string path = DetailPath(lab, "m10-liquid-control");
        WriteDetail(path, status, LiquidLimit + 1);
        Assert.That(() => Completion(files, scenario), Throws.TypeOf<InvalidDataException>().With.Message.Contains("actual byte-read limit"));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(LiquidLimit + 1), "Rejected evidence must remain intact.");
        lab.Record("liquid-detail-rejected", new { scenario, bytes = LiquidLimit + 1 });
    }

    [TestCase(ScenarioId.TcpHandshake, "m10-a03")]
    [TestCase(ScenarioId.TcpReceiveIsolation, "m12-receive-isolation")]
    public void NonLiquidDetailRetainsTheFourMiBActualReadLimit(ScenarioId scenario, string detailDirectory)
    {
        using var lab = new NativeTestDirectory();
        object files = CreateProject(lab);
        string path = DetailPath(lab, detailDirectory);
        WriteDetail(path, "passed", CommonLimit);
        Assert.That(Completion(files, scenario), Is.Null);
        WriteDetail(path, "passed", CommonLimit + 1);
        Assert.That(() => Completion(files, scenario), Throws.TypeOf<InvalidDataException>().With.Message.Contains("actual byte-read limit"));
        lab.Record("non-liquid-detail-limit", new { scenario, acceptedBytes = CommonLimit, rejectedBytes = CommonLimit + 1 });
    }

    [TestCase(ScenarioId.TcpLiquidControlOn, "guard-off-control-observed")]
    [TestCase(ScenarioId.TcpLiquidControlOff, "guard-on-observed")]
    [TestCase(ScenarioId.TcpLiquidControlOff, "guard-off-observed")]
    [TestCase(ScenarioId.TcpLiquidControlOn, "passed")]
    [TestCase(ScenarioId.TcpLiquidControlOff, "passed")]
    [TestCase(ScenarioId.TcpLiquidControlOn, "failed")]
    [TestCase(ScenarioId.TcpLiquidControlOff, null)]
    public void LiquidMeasurementRequiresItsOwnExactCompletionStatus(ScenarioId scenario, string? status)
    {
        using var lab = new NativeTestDirectory();
        object files = CreateProject(lab);
        // A success-shaped exit field cannot compensate for an absent/wrong detail status.
        File.WriteAllText(DetailPath(lab, "m10-liquid-control"), JsonSerializer.Serialize(new { status, exitCode = 0 }));
        Assert.That(Completion(files, scenario), Is.EqualTo("Selected scenario did not report its registered completion status."));
        lab.Record("liquid-status-rejected", new { scenario, status, exitCode = 0 });
    }

    [Test]
    public void MissingLiquidDetailCannotBecomeCompletion()
    {
        using var lab = new NativeTestDirectory();
        object files = CreateProject(lab);
        Assert.That(Completion(files, ScenarioId.TcpLiquidControlOn), Is.EqualTo("Selected scenario summary is missing."));
        lab.Record("missing-liquid-detail", new { completionRejected = true });
    }

    [Test]
    public void GeneralReadAndWriteCannotRequestTheLiquidOnlyLimit()
    {
        using var lab = new NativeTestDirectory();
        string path = lab.File("ordinary.json");
        File.WriteAllText(path, "{}");
        using var pins = WindowsPathPins.ForFile(path);
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => pins.ReadBounded(LiquidLimit));
            Assert.Throws<ArgumentOutOfRangeException>(() => pins.AtomicWrite([123, 125], LiquidLimit));
        });
        lab.Record("generic-cap-preserved", new { rejectedRequestedLimit = LiquidLimit, existingLimit = CommonLimit });
    }

    private static object CreateProject(NativeTestDirectory lab)
    {
        File.WriteAllText(lab.File("AGENTS.md"), "Owned result-extraction test fixture.");
        Directory.CreateDirectory(lab.File("docs"));
        File.WriteAllText(lab.File("docs/target-runtime-lock.json"), "{}");
        return Activator.CreateInstance(FilesType, lab.Root)!;
    }

    private static string DetailPath(NativeTestDirectory lab, string directory)
    {
        string path = lab.File(ReportDirectory + "/" + directory + "/summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static void WriteDetail(string path, string status, int bytes)
    {
        string prefix = "{\"status\":" + JsonSerializer.Serialize(status) + ",\"snapshots\":\"";
        const string suffix = "\"}";
        File.WriteAllText(path, prefix + new string('x', bytes - Encoding.UTF8.GetByteCount(prefix + suffix)) + suffix, new UTF8Encoding(false));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(bytes));
    }

    private static string? Completion(object files, ScenarioId scenario)
    {
        object?[] arguments = [files, scenario, ReportDirectory, null];
        try { return (string?)ReadCompletion.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
