using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

// Only this NUnit output copy of data is temporarily changed. Never touches source catalogs,
// running lab servers, game clients or production settings. Originals are restored in teardown.
[TestFixture, NonParallelizable]
public sealed class M5OptionalDataInitializationTests
{
    private const int Slot = 7;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string dataDirectory = Path.Combine(AppContext.BaseDirectory, "data", "progression");
    private byte[] itemBytes = null!, entityBytes = null!;
    private string oldSavePath = null!, directory = null!;
    private string? oldLabRoot;
    private IPAddress oldBind = null!;
    private TSPlayer? oldPlayer;
    private AntiCheatPlugin? plugin;

    [SetUp]
    public void SetUp()
    {
        itemBytes = File.ReadAllBytes(Path.Combine(dataDirectory, "candidates.json"));
        entityBytes = File.ReadAllBytes(Path.Combine(dataDirectory, "entity-candidates.json"));
        oldSavePath = ServerTShock.SavePath; oldBind = Netplay.ServerIP;
        oldLabRoot = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        oldPlayer = ServerTShock.Players[Slot];
        directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", "m5-optional-data-" + Guid.NewGuid().ToString("N"));
        ServerTShock.SavePath = Path.Combine(directory, "tshock");
        Directory.CreateDirectory(ServerTShock.SavePath);
        File.WriteAllText(Path.Combine(directory, ".anticheat-lab"), "isolated NUnit fixture");
        Netplay.ServerIP = IPAddress.Loopback;
        Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", directory);
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            if (plugin is not null) { plugin.Dispose(); await plugin.ShutdownCompletion; }
        }
        finally
        {
            File.WriteAllBytes(Path.Combine(dataDirectory, "candidates.json"), itemBytes);
            File.WriteAllBytes(Path.Combine(dataDirectory, "entity-candidates.json"), entityBytes);
            ServerTShock.SavePath = oldSavePath; Netplay.ServerIP = oldBind;
            ServerTShock.Players[Slot] = oldPlayer!;
            Environment.SetEnvironmentVariable("ANTICHEAT_LAB_ROOT", oldLabRoot);
        }
    }

    [Test]
    public void ValidProductionCatalogsRemainAvailableAlongsideEveryCompiledRule()
    {
        Initialize(ExecutionScope.Production);
        Assert.That(Get<M2BusinessAdapter>("_business").ProgressionRuleIds, Is.Not.Empty);
        Assert.That(Get<int>("_productionHardRuleCount"), Is.EqualTo(M2RuleRegistry.ProductionRules.Count));
        AssertIndependentHooksHealthy();
    }

    [Test]
    public void ValidTestLabSourcePolicyRemainsEnabled()
    {
        Initialize(ExecutionScope.TestLab, enablePolicy: true);
        Assert.That(Get<M3ProgressionPolicy>("_progressionPolicy").Enabled, Is.True);
        Assert.That(Get<M2BusinessAdapter>("_business").ProgressionRuleIds, Is.Not.Empty);
        AssertIndependentHooksHealthy();
    }

    [TestCase("items", false)]
    [TestCase("entities", false)]
    [TestCase("items", true)]
    public void InvalidOptionalCatalogDisablesOnlyItsDataWithoutAbortingInitialization(string which, bool syntax)
    {
        string file = Path.Combine(dataDirectory, which == "items" ? "candidates.json" : "entity-candidates.json");
        if (syntax) File.WriteAllText(file, "{ malformed fixture");
        else
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["schemaVersion"] = 999;
            File.WriteAllText(file, json.ToJsonString());
        }
        Initialize(ExecutionScope.Production);
        Assert.That(Get<M2BusinessAdapter>("_business").ProgressionRuleIds, Is.Empty);
        Assert.That(Get<int>("_productionHardRuleCount"), Is.EqualTo(M2RuleRegistry.ProductionRules.Count));
        AssertIndependentHooksHealthy();
    }

    [Test]
    public void ChangedPolicySourceHashRetainsCatalogsAndIndependentRules()
    {
        // Still valid JSON with identical candidate conditions; only the audited source hash changes.
        File.AppendAllText(Path.Combine(dataDirectory, "entity-candidates.json"), " ");
        Initialize(ExecutionScope.TestLab, enablePolicy: true);
        Assert.That(Get<M3ProgressionPolicy?>("_progressionPolicy"), Is.Null);
        Assert.That(Get<M2BusinessAdapter>("_business").ProgressionRuleIds, Is.Not.Empty);
        AssertIndependentHooksHealthy();
    }

    [Test]
    public void InvalidPolicyWhitelistDoesNotRemoveIndependentRules()
    {
        Initialize(ExecutionScope.TestLab, enablePolicy: true, invalidWhitelist: true);
        Assert.That(Get<M3ProgressionPolicy?>("_progressionPolicy"), Is.Null);
        Assert.That(Get<M2BusinessAdapter>("_business").ProgressionRuleIds, Is.Not.Empty);
        AssertIndependentHooksHealthy();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OptionalCatalogMergedRegistryRespectsExistingCoreCapacity(bool overCapacity)
    {
        int compiled = M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.ObserveOnly).Count;
        var entities = JsonNode.Parse(entityBytes)!["rules"]!.AsArray();
        int count = 128 - compiled - entities.Count + (overCapacity ? 1 : 0);
        Assert.That(count, Is.GreaterThan(0));
        var catalog = JsonNode.Parse(itemBytes)!;
        var template = catalog["rules"]![0]!.DeepClone();
        var rules = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            var rule = template.DeepClone(); rule["id"] = "M5.OPTIONAL.CAPACITY." + i;
            rules.Add(rule);
        }
        catalog["rules"] = rules;
        File.WriteAllText(Path.Combine(dataDirectory, "candidates.json"), catalog.ToJsonString());
        Initialize(ExecutionScope.Production);
        var loaded = Get<M2BusinessAdapter>("_business").ProgressionRuleIds.ToArray();
        if (overCapacity) Assert.That(loaded, Is.Empty, "Only the oversized optional data is disabled.");
        else Assert.That(compiled + loaded.Length, Is.EqualTo(128), "The valid upper boundary remains usable.");
        Assert.That(Get<int>("_productionHardRuleCount"), Is.EqualTo(M2RuleRegistry.ProductionRules.Count));
        AssertIndependentHooksHealthy();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReservedOrCrossCatalogRuleIdCollisionIsAnOptionalDataFault(bool crossCatalog)
    {
        var catalog = JsonNode.Parse(itemBytes)!;
        catalog["rules"]![0]!["id"] = crossCatalog
            ? JsonNode.Parse(entityBytes)!["rules"]![0]!["rule"]!["id"]!.GetValue<string>()
            : "A01.EmojiSenderMismatch";
        File.WriteAllText(Path.Combine(dataDirectory, "candidates.json"), catalog.ToJsonString());
        Initialize(ExecutionScope.Production);
        Assert.That(Get<M2BusinessAdapter>("_business").ProgressionRuleIds, Is.Empty);
        Assert.That(Get<int>("_productionHardRuleCount"), Is.EqualTo(M2RuleRegistry.ProductionRules.Count));
        AssertIndependentHooksHealthy();
    }

    private void Initialize(ExecutionScope scope, bool enablePolicy = false, bool invalidWhitelist = false)
    {
        File.WriteAllText(Path.Combine(ServerTShock.SavePath, "anticheat.json"), JsonSerializer.Serialize(new
        {
            ExecutionScope = scope.ToString(), ProgressionActiveCreationPolicy = new
            {
                Enabled = enablePolicy, PolicyContract = M3ProgressionPolicy.PolicyContract,
                WhitelistedProjectileTypes = invalidWhitelist ? new[] { 1 } : Array.Empty<int>()
            }
        }));
        var inspected = TargetRuntime.Inspect();
        Assert.That(inspected.Verified, Is.True, inspected.Reason);
        plugin = new AntiCheatPlugin(null!);
        Assert.DoesNotThrow(plugin.Initialize);
        Get<Task>("_operation").GetAwaiter().GetResult();
        Assert.That(Get<AntiCheatEngine>("_engine").IsMaintenanceMode, Is.False);
        Assert.That(Get<ExecutionScope>("_scope"), Is.EqualTo(scope));
    }

    private void AssertIndependentHooksHealthy()
    {
        var actor = new CapturingPlayer(Slot) { Account = new UserAccount { ID = 707, Name = "optional-data-normal" }, IsLoggedIn = true };
        ServerTShock.Players[Slot] = actor;
        var connect = new ConnectEventArgs();
        typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
        ServerApi.Hooks.ServerConnect.Invoke(connect);
        Assert.That(connect.Handled, Is.False);
        PlayerHooks.OnPlayerPostLogin(actor);
        var legal = M2ContractsTests.Packet(PacketTypes.Emoji, [(byte)Slot, 0], Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False, "Ordinary self-attributed input is not frozen by optional data faults.");
        var malformed = M2ContractsTests.Packet(PacketTypes.Emoji, [(byte)Slot], Slot);
        ServerApi.Hooks.NetGetData.Invoke(malformed);
        Assert.That(malformed.Handled, Is.True, "The independent versioned raw safety hook remains registered.");
        Assert.That(Get<AntiCheatEngine>("_engine").SanctionCount, Is.Zero);
        Assert.That(actor.Disconnects, Is.Zero, "Data faults do not become account penalties.");
    }

    private T Get<T>(string name) => (T)typeof(AntiCheatPlugin).GetField(name, Private)!.GetValue(plugin)!;
    private sealed class CapturingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects { get; private set; }
        public override void Disconnect(string reason) => Disconnects++;
    }
}
