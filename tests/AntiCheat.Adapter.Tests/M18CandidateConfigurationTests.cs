using System.Net;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M18CandidateConfigurationTests
{
    [Test]
    public void ObserveOnlyDefaultsToRecordsWithoutCandidateControls()
    {
        var configuration = new M2Configuration();
        Assert.Multiple(() =>
        {
            Assert.That(configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.Auto));
            Assert.That(M18ExecutionModePolicy.RecordEnabled(configuration.ExecutionScope,
                configuration.M18CandidateMode), Is.True);
            Assert.That(M18ExecutionModePolicy.CandidateControlsEnabled(configuration.ExecutionScope,
                configuration.M18CandidateMode), Is.False);
        });
    }

    [Test]
    public void FailedTestLabBoundaryLosesRuleQualificationWhileValidCandidateRemainsEnabled()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(M18CandidateConfigurationTests), Guid.NewGuid().ToString("N"));
        string save = Path.Combine(root, "server-save");
        string? previousRoot = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        try
        {
            Directory.CreateDirectory(save);
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root);
            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"TestLab\",\"M18CandidateMode\":\"TestLabCandidate\"," +
                "\"M18EnablePermanentSanctions\":true}");

            var missingMarker = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            AssertFailedTestLabBoundary(missingMarker);

            File.WriteAllText(Path.Combine(root, ".anticheat-lab"), "test");
            AssertFailedTestLabBoundary(M2Configuration.Load(save, IPAddress.Any).Configuration);

            var valid = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.Multiple(() =>
            {
                Assert.That(valid.ExecutionScope, Is.EqualTo(ExecutionScope.TestLab));
                Assert.That(valid.M18CandidateMode, Is.EqualTo(M18CandidateMode.TestLabCandidate));
                Assert.That(M18ExecutionModePolicy.CandidateControlsEnabled(valid.ExecutionScope,
                    valid.M18CandidateMode), Is.True);
                Assert.That(M2RuleRegistry.Create("test", valid.ExecutionScope)
                    .Any(policy => policy.Qualification == RuleQualification.TestLab), Is.True);
            });

            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"TestLab\",\"M18CandidateMode\":\"ProductionCandidate\"}");
            AssertFailedTestLabBoundary(M2Configuration.Load(save, IPAddress.Loopback).Configuration);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", previousRoot);
            DeleteExact(root);
        }
    }

    [Test]
    public void FailedProductionCandidateBoundaryCannotEnableM18Controls()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(M18CandidateConfigurationTests), Guid.NewGuid().ToString("N"));
        string save = Path.Combine(root, "server-save");
        string? previousRoot = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        try
        {
            Directory.CreateDirectory(save);
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root);
            File.WriteAllText(Path.Combine(save, "anticheat.json"),
                "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"ProductionCandidate\"," +
                "\"M18EnableBlocks\":true,\"M18EnableServiceKick\":true," +
                "\"M18EnablePermanentSanctions\":true}");

            var failed = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.Multiple(() =>
            {
                Assert.That(failed.ExecutionScope, Is.EqualTo(ExecutionScope.Production));
                Assert.That(failed.M18CandidateMode, Is.EqualTo(M18CandidateMode.Auto));
                Assert.That(failed.M18EnableBlocks, Is.False);
                Assert.That(failed.M18EnableServiceKick, Is.False);
                Assert.That(failed.M18EnablePermanentSanctions, Is.False);
                Assert.That(M18ExecutionModePolicy.CandidateControlsEnabled(failed.ExecutionScope,
                    failed.M18CandidateMode), Is.False);
            });

            File.WriteAllText(Path.Combine(root, ".anticheat-lab"), "test");
            var valid = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
            Assert.That(M18ExecutionModePolicy.CandidateControlsEnabled(valid.ExecutionScope,
                valid.M18CandidateMode), Is.True);
            Assert.That(valid.M18CandidateMode, Is.EqualTo(M18CandidateMode.ProductionCandidate));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", previousRoot);
            DeleteExact(root);
        }
    }

    private static void AssertFailedTestLabBoundary(M2Configuration configuration)
    {
        Assert.Multiple(() =>
        {
            Assert.That(configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            Assert.That(configuration.M18EnableBlocks, Is.False);
            Assert.That(configuration.M18EnablePermanentSanctions, Is.False);
            Assert.That(M18ExecutionModePolicy.CandidateControlsEnabled(configuration.ExecutionScope,
                configuration.M18CandidateMode), Is.False);
            Assert.That(M2RuleRegistry.Create("test", configuration.ExecutionScope)
                .All(policy => policy.Qualification != RuleQualification.TestLab), Is.True);
        });
    }

    private static void DeleteExact(string root)
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), nameof(M18CandidateConfigurationTests)));
        string path = Path.GetFullPath(root);
        if (Path.GetDirectoryName(path) == parent && Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
            Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
