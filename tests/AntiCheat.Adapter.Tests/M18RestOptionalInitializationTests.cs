using System.Reflection;
using System.Text.Json;
using AntiCheat.Plugin.TShock;
using HttpServer;
using NUnit.Framework;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M18RestOptionalInitializationTests
{
    [TestCase(null)]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingNativeRestAuthorityPreservesIndependentInitializationAndTruthfulEvidence(bool? configuredEnabled)
    {
        var oldConfig = ServerTShock.Config;
        var oldApi = ServerTShock.RestApi;
        var oldManager = ServerTShock.RestManager;
        var fixture = new M5OptionalDataInitializationTests();
        bool setupCompleted = false;
        try
        {
            fixture.SetUp(); setupCompleted = true;
            ServerTShock.Config = configuredEnabled.HasValue ? new() : null!;
            if (configuredEnabled.HasValue) ServerTShock.Config.Settings.RestApiEnabled = configuredEnabled.Value;
            ServerTShock.RestApi = null!;
            ServerTShock.RestManager = null!;

            // The real dependency remains pinned in this same test CLR. Missing native
            // authority is a separate optional-context condition, not a dependency fault.
            var validate = typeof(M17RestWorkGuard).GetMethod("ValidateHttpServerDependency", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(validate.Invoke(null, [typeof(IHttpContext).Assembly]), Is.EqualTo(M17RestWorkGuard.HttpServerSha256));

            // Reuse the existing real root fixture: native initialization, recovery,
            // catalog + exact qualifications, legal own emoji, malformed rejection,
            // and no account sanction or disconnect. No server/listener is started.
            fixture.ValidProductionCatalogsRemainAvailableAlongsideEveryCompiledRule();
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(ServerTShock.SavePath, "anticheat-runtime-evidence.json")));
            var rest = report.RootElement.GetProperty("SupportingContracts").GetProperty("RestAccountWork");
            Assert.Multiple(() =>
            {
                Assert.That(rest.GetProperty("Installed").GetBoolean(), Is.False);
                Assert.That(rest.GetProperty("HttpServerSha256").ValueKind, Is.EqualTo(JsonValueKind.Null), "No guard install was attempted without native authority.");
                var enabled = rest.GetProperty("EnabledByNativeConfiguration");
                if (configuredEnabled.HasValue) Assert.That(enabled.GetBoolean(), Is.EqualTo(configuredEnabled.Value));
                else Assert.That(enabled.ValueKind, Is.EqualTo(JsonValueKind.Null), "Unavailable configuration must remain unknown, not false.");
                Assert.That(report.RootElement.GetProperty("QualifiedRules").GetArrayLength(), Is.EqualTo(M2RuleRegistry.ProductionRules.Count));
            });
        }
        finally
        {
            try { if (setupCompleted) await fixture.TearDown(); }
            finally { ServerTShock.Config = oldConfig; ServerTShock.RestApi = oldApi; ServerTShock.RestManager = oldManager; }
        }
    }
}
