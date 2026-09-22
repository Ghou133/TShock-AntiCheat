using System.Net;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;

namespace AntiCheat.Rules.Tests;

[TestFixture, NonParallelizable]
public sealed class ConfigurationBoundaryTests
{
    private string directory = null!;
    private string root = null!;
    private string save = null!;
    private string? previousRoot;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "anticheat-config-" + Guid.NewGuid().ToString("N"));
        root = Path.Combine(directory, "CaseLab");
        save = Path.Combine(root, "save");
        Directory.CreateDirectory(save);
        previousRoot = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", root);
        File.WriteAllText(Path.Combine(root, ".anticheat-lab"), "owned test fixture\n");
        WriteConfiguration(save);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", previousRoot);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public void ValidOwnedLoopbackLabRetainsItsScope()
    {
        var loaded = M2Configuration.Load(save, IPAddress.Loopback);
        Assert.That(loaded.Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.TestLab));
        Assert.That(loaded.Reason, Is.EqualTo("isolated-loopback-testlab"));
    }

    [Test]
    public void MissingConfigurationUsesObservation()
    {
        File.Delete(Path.Combine(save, "anticheat.json"));
        var loaded = M2Configuration.Load(save, IPAddress.Loopback);
        Assert.That(loaded.Configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
        Assert.That(loaded.Reason, Is.EqualTo("default-observation"));
    }

    [TestCase("{")]
    [TestCase("{\"ExecutionScope\":999}")]
    [TestCase("{\"M18CandidateMode\":999}")]
    public void InvalidConfigurationCannotEnableEnforcement(string json)
    {
        File.WriteAllText(Path.Combine(save, "anticheat.json"), json);
        var configuration = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
        Assert.That(configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
        Assert.That(configuration.M18EnablePermanentSanctions, Is.False);
    }

    [Test]
    public void OversizedConfigurationUsesObservation()
    {
        File.WriteAllText(Path.Combine(save, "anticheat.json"), new string(' ', 8193));
        Assert.That(M2Configuration.Load(save, IPAddress.Loopback).Reason, Is.EqualTo("configuration-too-large"));
    }

    [TestCase(null)]
    [TestCase("0.0.0.0")]
    [TestCase("::")]
    public void NonLoopbackBindingRevokesLabQualification(string? address)
    {
        AssertDisabled(M2Configuration.Load(save, address is null ? null : IPAddress.Parse(address)).Configuration);
    }

    [Test]
    public void MissingMarkerRevokesLabQualification()
    {
        File.Delete(Path.Combine(root, ".anticheat-lab"));
        AssertDisabled(M2Configuration.Load(save, IPAddress.Loopback).Configuration);
    }

    [Test]
    public void CaseDistinctSiblingCannotBorrowAnotherLabMarker()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Case-distinct sibling regression targets case-sensitive filesystems.");
        var otherRoot = Path.Combine(directory, "caselab");
        if (Directory.Exists(otherRoot)) Assert.Ignore("Temporary filesystem is case-insensitive.");
        var otherSave = Path.Combine(otherRoot, "save");
        Directory.CreateDirectory(otherSave);
        WriteConfiguration(otherSave);
        AssertDisabled(M2Configuration.Load(otherSave, IPAddress.Loopback).Configuration);
    }

    [Test]
    public void PrefixSiblingIsNotADescendant()
    {
        var otherSave = Path.Combine(root + "-outside", "save");
        Directory.CreateDirectory(otherSave);
        WriteConfiguration(otherSave);
        AssertDisabled(M2Configuration.Load(otherSave, IPAddress.Loopback).Configuration);
    }

    [Test]
    public void SymlinkMarkerCannotAuthorizeLab()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux CI exercises symlinks without elevated privileges.");
        var outside = Path.Combine(directory, "outside-marker");
        File.WriteAllText(outside, "not a marker in the lab");
        var marker = Path.Combine(root, ".anticheat-lab");
        File.Delete(marker);
        File.CreateSymbolicLink(marker, outside);
        try { AssertDisabled(M2Configuration.Load(save, IPAddress.Loopback).Configuration); }
        finally { File.Delete(marker); }
    }

    [Test]
    public void SymlinkAboveLabRootCannotAuthorizeLab()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux CI exercises symlinks without elevated privileges.");
        var link = Path.Combine(directory, "linked-parent");
        Directory.CreateSymbolicLink(link, root);
        var nestedRoot = Path.Combine(root, "nested-lab");
        var nestedSave = Path.Combine(nestedRoot, "save");
        Directory.CreateDirectory(nestedSave);
        File.WriteAllText(Path.Combine(nestedRoot, ".anticheat-lab"), "test");
        WriteConfiguration(nestedSave);
        Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", Path.Combine(link, "nested-lab"));
        try
        {
            AssertDisabled(M2Configuration.Load(Path.Combine(link, "nested-lab", "save"), IPAddress.Loopback).Configuration);
        }
        finally { Directory.Delete(link); }
    }

    [Test]
    public void FailedProductionCandidateDoesNotEnableCandidateControls()
    {
        File.Delete(Path.Combine(root, ".anticheat-lab"));
        File.WriteAllText(Path.Combine(save, "anticheat.json"),
            "{\"ExecutionScope\":\"Production\",\"M18CandidateMode\":\"ProductionCandidate\",\"M18EnablePermanentSanctions\":true}");
        var configuration = M2Configuration.Load(save, IPAddress.Loopback).Configuration;
        Assert.Multiple(() =>
        {
            Assert.That(configuration.ExecutionScope, Is.EqualTo(ExecutionScope.Production));
            Assert.That(configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.Auto));
            Assert.That(configuration.M18EnableBlocks, Is.False);
            Assert.That(configuration.M18EnableServiceKick, Is.False);
            Assert.That(configuration.M18EnablePermanentSanctions, Is.False);
        });
    }

    private static void WriteConfiguration(string path) => File.WriteAllText(Path.Combine(path, "anticheat.json"),
        "{\"ExecutionScope\":\"TestLab\",\"M18CandidateMode\":\"TestLabCandidate\",\"M18EnablePermanentSanctions\":true}");

    private static void AssertDisabled(M2Configuration configuration)
    {
        Assert.Multiple(() =>
        {
            Assert.That(configuration.ExecutionScope, Is.EqualTo(ExecutionScope.ObserveOnly));
            Assert.That(configuration.M18CandidateMode, Is.EqualTo(M18CandidateMode.Disabled));
            Assert.That(configuration.M18EnableBlocks, Is.False);
            Assert.That(configuration.M18EnableServiceKick, Is.False);
            Assert.That(configuration.M18EnablePermanentSanctions, Is.False);
        });
    }
}
