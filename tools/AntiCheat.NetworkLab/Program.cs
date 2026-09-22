using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

if (args.Length is < 3 or > 6) throw new ArgumentException("Expected isolated run directory, report directory, loopback port, optional mode; maintenance-load may additionally specify samples per phase and maximum load seconds.");
bool vanilla = args.Length == 4 && args[3].StartsWith("vanilla-", StringComparison.Ordinal);
bool inventoryCases = args.Length == 4 && args[3] == "inventory-testlab";
bool mcpIdentity = args.Length == 4 && args[3] == "production-mcp-identity";
bool productionIdentity = mcpIdentity || args.Length == 4 && args[3] == "production-identity";
bool handshakeCases = args.Length == 4 && args[3] == "handshake-testlab";
bool crashRecovery = args.Length == 4 && args[3] == "crash-recovery";
bool soleActor = args.Length == 4 && args[3] == "sole-actor";
bool observedReplay = args.Length == 4 && args[3] == "observed-tool-replay";
bool maintenanceLoad = args.Length >= 4 && args[3] == "maintenance-load";
bool containerCausality = args.Length == 4 && args[3] == "container-causality";
bool runtimeDiagnostics = args.Length == 4 && args[3] is "runtime-diagnostics" or "runtime-control";
bool gameplaySafety = args.Length == 4 && args[3] == "gameplay-safety";
bool gameplayBusiness = args.Length == 4 && args[3] == "gameplay-business";
bool protectionCases = args.Length == 5 && args[3] == "protection-cases";
string protectionSlice = protectionCases ? args[4] : "";
if (protectionCases && protectionSlice is not ("application" or "craft" or "world" or "teleport" or "quickstack" or "summon" or "sentry" or "solartablet" or "classemblems" or "m16equipment" or "m16combat" or "receiveisolation" or "liquidcontrolon" or "liquidcontroloff" or "golemlegal" or "displaysafety" or "objectsafety" or "objectplacement" or "lowcontext" or "resumelowcontext" or "m15lowcontext" or "tileentityplacement" or "m16-world-paint" or "m16-request-egress" or "m16-connection-timeouts" or "m16inventoryslots" or "m16itemstructure" or "m17containersort" or "m17-command-work" or "m17progressionworld" or "m18storage" or "m18resetinputs"))
    throw new ArgumentException("Unknown protection slice.");
bool helloDiagnostics = Environment.GetEnvironmentVariable("ANTICHEAT_M7_HELLO_DIAGNOSTICS") == "1";
bool m10HandshakeProbe = handshakeCases && helloDiagnostics;
string pacingValue = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_CONNECTION_PACING_MS") ?? "0";
if (!int.TryParse(pacingValue, out int connectionPacingMilliseconds) || connectionPacingMilliseconds is < 0 or > 500)
    throw new ArgumentOutOfRangeException(nameof(connectionPacingMilliseconds), "Lab disposal-to-next-connect pacing must be 0..500ms.");
if (args.Length > 4 && !protectionCases && (!maintenanceLoad || args.Length != 6))
    throw new ArgumentException("Only protection-cases and maintenance-load accept additional options.");
int loadSamplesPerPhase = 80, maximumLoadSeconds = 30;
if (args.Length == 6 && (!int.TryParse(args[4], out loadSamplesPerPhase) || loadSamplesPerPhase is < 68 or > 256 ||
    !int.TryParse(args[5], out maximumLoadSeconds) || maximumLoadSeconds is < 10 or > 30))
    throw new ArgumentOutOfRangeException(nameof(args), "Use 68..256 samples per phase and 10..30 maximum load seconds.");
string expectedScope = productionIdentity || protectionCases && protectionSlice == "m17progressionworld" ? "Production" : "TestLab";
if (args.Length == 4 && args[3] == "protection-cases")
    throw new ArgumentException("protection-cases requires an explicit protection slice.");
string actualScenario = protectionCases ? "protection-cases:" + protectionSlice : args.Length > 3 ? args[3] : "regression";
string requestedScenario = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_REQUESTED_SCENARIO") ?? actualScenario;
if (!string.Equals(requestedScenario, actualScenario, StringComparison.Ordinal))
    throw new ArgumentException($"Requested scenario '{requestedScenario}' differs from actual scenario '{actualScenario}'.");
bool withoutAntiCheat = args.Length == 4 && args[3] is "vanilla-control" or "runtime-control";
if (args.Length == 4 && args[3] is not ("vanilla-testlab" or "vanilla-control" or "inventory-testlab" or "production-identity" or "production-mcp-identity" or "handshake-testlab" or "crash-recovery" or "sole-actor" or "observed-tool-replay" or "maintenance-load" or "container-causality" or "runtime-diagnostics" or "runtime-control" or "gameplay-safety" or "gameplay-business" or "protection-cases")) throw new ArgumentException("Unknown lab mode.");
string run = Path.GetFullPath(args[0]), report = Path.GetFullPath(args[1]);
int port = int.Parse(args[2]);
if (!File.Exists(Path.Combine(run, ".anticheat-lab"))) throw new InvalidOperationException("Missing isolation marker.");
Directory.CreateDirectory(report);
using var devLifecycle = DevRunLifecycle.Open(Path.GetDirectoryName(Path.GetDirectoryName(run))!, run, report, port, !vanilla);
var checks = new List<object>();
var clients = new List<LabClient>();
var handshakeProbes = new List<M10HandshakeProbe>(2);
LabClient? vanillaPeerObserver = null;
bool vanillaPeerStartRequested = false;
string vanillaPeerStatus = "not-started", vanillaPeerFailure = "";
long vanillaPeerAccount = 0;
string? vanillaPeerGroup = null;
DateTimeOffset? vanillaPeerStartedUtc = null, vanillaPeerStoppedUtc = null;
var console = new ConcurrentQueue<string>();
var timer = Stopwatch.StartNew();
var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var tables = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var provedAccounts = new List<(string Name, long Account, string Rule)>();
bool initialized = false, ready = false, verified = false, forcedStop = false;
bool started = false, restarted = false, runSafetyClean = false;
string actualScope = "unobserved";
int productionHardRules = -1;
var expectedProductionRules = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["A01.EmojiSenderMismatch"] = "m2.1", ["A02.InventorySenderMismatch"] = "m2.1",
    ["NPC01.ServerDebuffDamage"] = "1.0.0", ["NPC02.ServerPortalTeleport"] = "1.0.0",
    ["C2.CultistRitualRole"] = "1.0.0", ["C6.PortalPlacementDamage"] = "1.0.0",
    ["VITAL01.RawLifeMaximum"] = "1.0.0", ["VITAL02.RawManaMaximum"] = "1.0.0",
    ["CONTAINER01.ChestResizeAuthority"] = "1.0.0", ["B4.ContainerAuthorization"] = "1.1.0",
    ["VITAL04.NonPvpHurtTarget"] = "1.0.0", ["PG-NAT-012.MechdusaSummonWorld"] = "1.0.0",
    ["C7.WoodenArrowDamageProjection"] = "1.0.0", ["PG-NAT-121.CelestialSigilWorld"] = "1.0.0",
    ["NPC03.BossPartSummonRequest"] = "1.0.0", ["PG-NAT-108.GolemSummonWorld"] = "1.0.0",
    ["G03.NpcBuffRemovalContract"] = "1.0.0",
    ["NPC04.BuffStateSyncAuthority"] = "1.0.0", ["G05.NaturalRodWorldBorder"] = "1.0.0"
};
// Exact M14 low-context additions after independent native/root and TestLab TCP proof.
// Production runs must now prove these alongside the accepted nineteen-rule baseline.
var m14lProductionRuleCandidates = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["G03.PlayerBuffAddContract"] = "1.0.0",
    ["G03.NpcShadowFlameAddContract"] = "1.0.0",
    ["NPC05.EventStateAuthority"] = "1.0.0"
};
foreach (var entry in m14lProductionRuleCandidates) expectedProductionRules.Add(entry.Key, entry.Value);
// M14 resume: native/root contracts and independent TestLab TCP/restart proof precede admission.
expectedProductionRules.Add("G03.NpcShimmerAddContract", "1.0.0");
expectedProductionRules.Add("WORLD01.WorldAlignmentStateAuthority", "1.0.0");
expectedProductionRules.Add("NPC06.CavernMonsterStateAuthority", "1.0.0");
expectedProductionRules.Add("WORLD02.CreditsRollStateAuthority", "1.0.0");
expectedProductionRules.Add("PROJ01.CannonFiringAuthority", "1.0.0");
expectedProductionRules.Add("G03.NpcBuffTypeContract", "1.0.0");
var exitCodes = new List<int>();
var databaseEvidence = new List<object>();
string status = "failed", failure = "";
int? serverExitCode = null;
var info = new ProcessStartInfo(Path.Combine(run, "app", "TShock.Server.exe"))
{
    WorkingDirectory = Path.Combine(run, "app"), UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    StandardInputEncoding = new UnicodeEncoding(false, false), StandardOutputEncoding = new UTF8Encoding(false),
    StandardErrorEncoding = new UTF8Encoding(false)
};
foreach (var arg in new[] { "-config", Path.Combine(run, "serverconfig.txt"), "-ip", "127.0.0.1", "-port", port.ToString(), "-configpath", Path.Combine(run, "tshock"), "-logpath", Path.Combine(run, "logs") })
    info.ArgumentList.Add(arg);
info.Environment["ANTICHEAT_LAB_ROOT"] = run;
info.Environment["ANTICHEAT_M16_COMBAT_DIAGNOSTICS"] = protectionCases && protectionSlice == "m16combat" ? "1" : "0";
info.Environment["ANTICHEAT_M18_RESET_DIAGNOSTICS"] = protectionCases && protectionSlice == "m18resetinputs" ? "1" : "0";
if (mcpIdentity) info.Environment["TERRARIA_MCP_SERVER_CONFIG"] = Path.Combine(run, "server-bridge.json");
info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(run, "bundle-cache");
if (vanilla || inventoryCases || observedReplay || maintenanceLoad || runtimeDiagnostics || gameplaySafety || gameplayBusiness || (protectionCases && protectionSlice is not ("solartablet" or "classemblems" or "m16equipment" or "m16combat" or "golemlegal" or "lowcontext" or "resumelowcontext" or "m15lowcontext" or "m18resetinputs")))
{
    info.Environment["COMPAT_QA_ROOT"] = run;
    info.Environment["COMPAT_QA_OUTPUT"] = report;
}
using var server = new Process { StartInfo = info };
Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
try
{
    DevRunLifecycle.Check();
    await StartServer(false);
    devLifecycle?.Phase("scenario");
    if (mcpIdentity)
    {
        Assert(verified && ready && actualScope == "Production", "mcp:actual-loaded-runtime-and-Production-scope");
        await McpIdentityScenario.RunAsync(run, report, server, name => Connect(name), Scalar,
            CharacterInventory, () => console.ToArray(), Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule)));
        CaptureDatabase("mcp-first-proof");
        status = "passed";
    }
    else if (protectionCases)
    {
        var host = new M4ObservedReplayHarness(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => ready && verified, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule)),
            (name, address) => Connect(name, localBind: address));
        switch (protectionSlice)
        {
            case "application": await M9ApplicationScenario.RunAsync(host); break;
            case "craft": await M9CraftScenario.RunAsync(host); break;
            case "world": await M9WorldScenario.RunAsync(host); break;
            case "m16-world-paint": await M16WorldPaintScenario.RunAsync(host); break;
            case "m16-request-egress": await M16RequestEgressScenario.RunAsync(host); break;
            case "m16-connection-timeouts": await M16ConnectionTimeoutScenario.RunAsync(host); break;
            case "m17-command-work": await M17CommandWorkScenario.RunAsync(host); break;
            case "teleport": await M9TeleportScenario.RunAsync(host); break;
            case "quickstack": await M10QuickStackScenario.RunAsync(host); break;
            case "summon": await M10SummonScenario.RunAsync(host); break;
            case "sentry": await M11SentryScenario.RunAsync(host); break;
            case "solartablet":
                await M12NaturalSourcesScenario.RunAsync(host);
                await M11SolarTabletScenario.RunAsync(host);
                break;
            case "classemblems": await M12EquipmentScenario.RunAsync(host); break;
            case "m16equipment": await M16EquipmentScenario.RunAsync(host); break;
            case "m16combat": await M16CombatScenario.RunAsync(host, Environment.GetEnvironmentVariable("ANTICHEAT_M17_NATIVE_WITNESS_INPUT")); break;
            case "m16inventoryslots": await M16InventorySlotScenario.RunAsync(host); break;
            case "m16itemstructure": await M16ItemStructureScenario.RunAsync(host); break;
            case "m18storage": await M18StorageScenario.RunAsync(host, Path.Combine(AppContext.BaseDirectory, "native-sort", "NativeSortCorpus.dll")); break;
            case "m18resetinputs": await M18ResetInputsScenario.RunAsync(host, (name, uuid) => Connect(name, uuid)); break;
            case "m17progressionworld": await M17ProgressionWorldScenario.RunAsync(host); break;
            case "m17containersort": await M17ContainerSortScenario.RunAsync(host, Path.Combine(AppContext.BaseDirectory, "native-sort", "NativeSortCorpus.dll")); break;
            case "golemlegal": await M13GolemLegalScenario.RunAsync(host); break;
            case "displaysafety": await M13DisplaySafetyScenario.RunAsync(host); break;
            case "objectsafety": await M14ObjectSafetyScenario.RunAsync(host); break;
            case "objectplacement": await M14LObjectPlacementScenario.RunAsync(host); break;
            case "lowcontext": await M14LLowContextSlice(); break;
            case "resumelowcontext": await M14RLowContextSlice(); break;
            case "m15lowcontext": await M15LowContextSlice(); break;
            case "tileentityplacement": await M14ResumeTileEntityPlacementScenario.RunAsync(host); break;
            case "receiveisolation": await M12ReceiveIsolationScenario.RunAsync(host); break;
            case "liquidcontrolon": await M10LiquidControlScenario.RunAsync(host, true); break;
            case "liquidcontroloff": await M10LiquidControlScenario.RunAsync(host, false); break;
            default: throw new InvalidOperationException("The selected protection slice has no implemented runner.");
        }
        status = "passed";
    }
    else if (gameplayBusiness)
    {
        await M7GameplayBusinessScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => ready && verified, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))));
        status = "passed";
    }
    else if (gameplaySafety)
    {
        await M6GameplaySafetyScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => ready && verified, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))));
        status = "passed";
    }
    else if (runtimeDiagnostics)
    {
        await M6RuntimeDiagnosticsScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => ready && (withoutAntiCheat || verified), Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))), !withoutAntiCheat);
        status = "passed";
    }
    else if (soleActor)
    {
        await M4SoleActorScenario.RunAsync(new(run, report, server, port, StartServer, StopServer,
            name => Connect(name), predicate => console.Any(predicate), () => verified && ready,
            () => clients.Count, Assert));
        restarted = true;
        status = "passed";
    }
    else if (crashRecovery)
    {
        await M4RecoveryScenarios.RunAsync(new(run, report, server, StartServer, name => Connect(name),
            () => Task.WhenAll(stdout, stderr), predicate => console.Any(predicate), () => verified && ready,
            Assert, code => { forcedStop = true; serverExitCode = code; exitCodes.Add(code); started = false; }));
        restarted = true;
        status = "passed";
    }
    else if (observedReplay)
    {
        await M4ObservedToolReplay.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))));
        await M5EffectsScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))));
        foreach (var proof in provedAccounts)
        {
            var replayReconnect = await Connect(proof.Name);
            await replayReconnect.Join(); await replayReconnect.LoginAndExpectRejected();
            Assert(replayReconnect.IsBanRejection, "observed-replay:" + proof.Rule + "-same-account-real-tcp-reconnect-rejected");
            await replayReconnect.DisposeAsync(); await Task.Delay(150);
        }
        status = "passed";
    }
    else if (maintenanceLoad)
    {
        await M5MaintenanceLoadScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule)),
            (name, localBind) => Connect(name, localBind: localBind)),
            samplesPerPhase: loadSamplesPerPhase, maximumLoadSeconds: maximumLoadSeconds);
        status = "passed";
    }
    else if (containerCausality)
    {
        await M5ContainerCausalityScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))));
        status = "passed";
    }
    else if (vanilla)
    {
        Assert(ready && (withoutAntiCheat || initialized && verified), "vanilla-lab-server-ready");
        Console.WriteLine($"VANILLA_LAB_READY port={port} report={report} control={withoutAntiCheat}");
        // A local operator supplies fixture-only console commands. Actual gameplay stays in the vanilla UI.
        // EOF or the explicit stop word closes this owned isolated server through the existing shutdown path.
        while (await Console.In.ReadLineAsync() is { } command)
        {
            if (command == "stop-lab") break;
            if (command.Length > 512) throw new InvalidOperationException("Console command exceeds lab bound.");
            if (command == "start-peer-observer") { await StartVanillaPeerObserver(); continue; }
            if (command == "stop-peer-observer") { await StopVanillaPeerObserver(); continue; }
            await server.StandardInput.WriteLineAsync(command);
            await server.StandardInput.FlushAsync();
        }
        status = "prepared_only";
    }
    else
    {
    Assert(initialized && ready, "real-server-initialized");
    Assert(verified, "actual-loaded-target-identity");
    if (productionIdentity) Assert(actualScope == "Production" && productionHardRules == expectedProductionRules.Count, "production-identity:actual-scope-and-reviewed-compiled-rule-count");
    var control = await Connect("M2Control");
    M10HandshakeProbe? firstHandshakeProbe = null;
    if (m10HandshakeProbe)
    {
        firstHandshakeProbe = new("first-connection-idle", true, () => console.ToArray(), Assert);
        handshakeProbes.Add(firstHandshakeProbe);
    }
    await control.Join(firstHandshakeProbe);
    await control.RegisterAndLogin();
    firstHandshakeProbe?.Complete(control, Scalar("SELECT COUNT(*) FROM PlayerBans", null));
    Assert(control.Authenticated && control.SscSlots.Count >= 350, "innocent-authenticated-and-ssc-restored");
    await DevRunLifecycle.WaitAsync(tables.Task, TimeSpan.FromSeconds(15));
    Assert(tables.Task.IsCompletedSuccessfully, "target-item-buff-projectile-tables-built-in-real-update");
    if (handshakeCases)
    {
        // Bounded reproducer for the M3 hello-stage disconnect. No retries or delay that
        // could hide a failed connection: each immediately follows a normal client's close.
        var handshakeTimer = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++)
        {
            if (handshakeTimer.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Handshake experiment time bound exceeded.");
            var cycle = await Connect("M4Handshake" + i);
            M10HandshakeProbe? activeHandshakeProbe = null;
            if (i == 0 && m10HandshakeProbe)
            {
                activeHandshakeProbe = new("subsequent-connection-active", false, () => console.ToArray(), Assert);
                handshakeProbes.Add(activeHandshakeProbe);
            }
            await cycle.Join(activeHandshakeProbe);
            await cycle.RegisterAndLogin();
            activeHandshakeProbe?.Complete(cycle, Scalar("SELECT COUNT(*) FROM PlayerBans", null));
            Assert(cycle.Authenticated && cycle.SscSlots.Count >= 350, $"handshake:{i}:normal-authenticated-ssc-before-immediate-close");
            // Keep the real recipient consuming join/SSC broadcasts. Otherwise the lab's
            // bounded receive channel fills while the repeated clients are being exercised.
            await control.Drain(TimeSpan.FromMilliseconds(50));
            await cycle.DisposeAsync();
        }
        Assert(!control.Closed && Scalar("SELECT COUNT(*) FROM PlayerBans", null) == 0,
            "handshake:normal-reconnections-do-not-produce-account-bans");
        status = "passed";
    }
    else
    {
    var attacker = await Connect("M2Attacker");
    await attacker.Join();
    await attacker.RegisterAndLogin();
    Assert(attacker.Authenticated && attacker.SscSlots.Count >= 350, "subject-authenticated-and-ssc-restored");
    long attackerId = Scalar("SELECT ID FROM Users WHERE Username=$name", attacker.Name);
    long controlId = Scalar("SELECT ID FROM Users WHERE Username=$name", control.Name);
    Assert(attackerId > 0 && controlId > 0 && attackerId != controlId, "distinct-server-authenticated-account-ids");
    await attacker.Send(120, w => { w.Write(attacker.Slot); w.Write((byte)0); });
    await control.WaitUntil(() => control.Bubbles.Any(x => x.Player == attacker.Slot && x.Emote == 0), TimeSpan.FromSeconds(5));
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == 0, "legal-own-emoji-broadcast-without-ban");
    int bubblesBefore = control.Bubbles.Count;
    int inventoryBefore = control.InventoryUpdates.Count;
    // Exactly one proof candidate, followed by a normal self inventory update in the same TCP write.
    // The second frame must not survive the synchronous session revocation.
    byte[] malicious = LabClient.Packet(120, w => { w.Write(control.Slot); w.Write((byte)1); });
    byte[] later = LabClient.Packet(5, w => { w.Write(attacker.Slot); w.Write((short)10); w.Write((short)1); w.Write((byte)0); w.Write((short)9); w.Write((byte)0); });
    await attacker.SendBatch(malicious, later);
    await attacker.WaitUntil(() => attacker.DisconnectReason is not null || attacker.Closed, TimeSpan.FromSeconds(8));
    await control.Drain(TimeSpan.FromSeconds(2));
    Assert(attacker.DisconnectReason == "AntiCheat proven violation.", "first-proof-disconnected");
    await Until(() => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + attacker.Name) == 1, TimeSpan.FromSeconds(10));
    Assert(Scalar("SELECT Date FROM PlayerBans WHERE Identifier=$name", "acc:" + attacker.Name) < DateTime.UtcNow.Ticks, "permanent-account-ban-active-now");
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + control.Name) == 0, "victim-and-shared-loopback-source-not-banned");
    Assert(control.Bubbles.Skip(bubblesBefore).All(x => x.Emote != 1), "spoofed-emoji-no-outbound-bubble");
    Assert(control.InventoryUpdates.Skip(inventoryBefore).All(x => x.Player != attacker.Slot || x.Slot != 10 || x.Item != 9), "coalesced-after-proof-inventory-not-broadcast");
    // Production deliberately omits this TestLab trace. Both scopes still require the actual
    // recipient and SSC side-effect assertions and the incident's synchronous revoked flag.
    if (!productionIdentity)
        Assert(console.Any(x => x.Contains("ANTICHEAT_REVOKED_PACKET") && x.Contains("slot=" + attacker.Slot) && x.Contains("packet=5")), "coalesced-after-proof-frame-hit-revocation-guard");
    Assert(CharacterInventory(attackerId).Split('~')[10].StartsWith("0,"), "coalesced-after-proof-inventory-not-persisted");
    Assert(console.Any(x => x.Contains("ANTICHEAT_INCIDENT") && x.Contains("accountId=" + attackerId) && x.Contains("canceled=true", StringComparison.OrdinalIgnoreCase) && x.Contains("revoked=true", StringComparison.OrdinalIgnoreCase)), "real-hook-attributed-canceled-and-revoked");
    provedAccounts.Add((attacker.Name, attackerId, "A01.EmojiSenderMismatch"));
    // Reconnection uses a newly accepted TCP connection and the same account's normal UUID login.
    await attacker.DisposeAsync();
    await Task.Delay(300);
    var reconnect = await Connect(attacker.Name, attacker.Uuid);
    try { await reconnect.Join(); }
    catch (IOException) when (reconnect.DisconnectReason is not null || reconnect.Closed) { }
    await reconnect.WaitUntil(() => reconnect.DisconnectReason is not null || reconnect.Closed, TimeSpan.FromSeconds(8));
    Assert(reconnect.IsBanRejection, "banned-account-reconnect-rejected");
    await reconnect.DisposeAsync();
    await Task.Delay(300); // Release the rejected connection before asserting reuse of its slot.
    await control.Send(120, w => { w.Write(control.Slot); w.Write((byte)2); });
    await control.WaitUntil(() => control.Bubbles.Any(x => x.Player == control.Slot && x.Emote == 2), TimeSpan.FromSeconds(5));
    Assert(!control.Closed && control.DisconnectReason is null, "innocent-existing-session-still-works");
    var newcomer = await Connect("M2Newcomer");
    await newcomer.Join();
    await newcomer.RegisterAndLogin();
    Assert(newcomer.Authenticated && newcomer.SscSlots.Count >= 350 && newcomer.DisconnectReason is null, "innocent-new-account-joins-after-sanction");
    Assert(newcomer.Slot == attacker.Slot, "reused-slot-does-not-inherit-prior-sanction");
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == 1, "exactly-one-permanent-account-record-no-source-ban");
    await newcomer.DisposeAsync();
    var changedUuid = await Connect(attacker.Name);
    await changedUuid.Join();
    await changedUuid.LoginAndExpectRejected();
    Assert(changedUuid.Uuid != attacker.Uuid && changedUuid.LoginAttempts == 1 && changedUuid.IsBanRejection,
        "changed-uuid-normal-password-login-still-rejected");
    await changedUuid.DisposeAsync();

    if (!productionIdentity)
    {
    uint foreignProjectileKey = LabClient.ProjectileKey(control.Slot, 10, 3);
    await AdditionalRule("M2Projectile", "C1.ProjectileAuthority", control,
        async actor =>
        {
            uint own = LabClient.ProjectileKey(actor.Slot, 9, 2);
            await actor.Send(27, w => actor.WriteProjectile(w, own));
            await control.WaitUntil(() => control.Projectiles.Any(x => x.Key == own && x.Type == 1), TimeSpan.FromSeconds(5));
        },
        actor => LabClient.Packet(27, w => actor.WriteProjectile(w, foreignProjectileKey)),
        () => control.Projectiles.All(x => x.Key != foreignProjectileKey));

    // The old self55/87 comparator is impossible under the now-audited native
    // AddBuff producer. Migrate this existing test to an actual remote PvP add,
    // then attribute its new self-target proof to the rule that now handles it.
    // InventoryCases installs the existing QA scaffold and is outside the new
    // native-host HARD contract. Its own real inventory/BuffList checks remain.
    if (!inventoryCases) await M14LPlayerBuffInput(control);
    }

    int victimInventoryCount = control.InventoryUpdates.Count;
    await AdditionalRule("M2Inventory", "A02.InventorySenderMismatch", control,
        async actor =>
        {
            int before = control.InventoryUpdates.Count;
            await actor.Send(5, w => LabClient.WriteInventory(w, actor.Slot, 0, 0));
            await control.WaitUntil(() => control.InventoryUpdates.Skip(before).Any(x => x.Player == actor.Slot && x.Slot == 10 && x.Item == 0), TimeSpan.FromSeconds(5));
        },
        actor => LabClient.Packet(5, w => LabClient.WriteInventory(w, control.Slot, 1, 9)),
        () => control.InventoryUpdates.Skip(victimInventoryCount).All(x => x.Player != control.Slot || x.Slot != 10 || x.Item != 9));
    if (!productionIdentity)
    {
    await CombatAndWorldInputs(control);
    await BuffListSafetyInputs(control);
    await M4CombatInputs(control);
    if (!inventoryCases) await M4VitalInputs(control);
    await NpcAuthorityInputs(control, false);
    if (inventoryCases) await NpcAuthorityInputs(control, true);
    if (inventoryCases)
    {
        await InventoryInputs(control);
        await ChestResizeInputs(control);
    }
    else
    {
    uint forbiddenProgressionKey = 0;
    await AdditionalRule("M3Progression", "PG-PRJ-POL-001.ActiveCreation", control,
        async actor =>
        {
            long id = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(27, w => actor.WriteProjectile(w, LabClient.ProjectileKey(actor.Slot, 81, 3), 881));
            await RuleOutcome(id, "PG-PRJ-POL-001.ActiveCreation", "Pass", "explicit-projectile-type-whitelist");
        },
        actor =>
        {
            forbiddenProgressionKey = LabClient.ProjectileKey(actor.Slot, 82, 3);
            return LabClient.Packet(27, w => actor.WriteProjectile(w, forbiddenProgressionKey, 406));
        },
        () => control.Projectiles.All(x => x.Key != forbiddenProgressionKey));
    }
    }
    // The legitimate King Slime comparator changes the world. Run it after the older
    // pre-King-Slime policy, preserving that policy's active-boss counterexample.
    if (!productionIdentity && !inventoryCases)
        await M7ProjectileCleanupScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))), control);
    if (!productionIdentity && !inventoryCases) await M5BusinessInputs(control);
    if (!productionIdentity && !inventoryCases)
    {
        await M13SourceDrivenInputs(control);
        await M14SourceDrivenInputs(control);
        await M14LNpcBuffInput(control);
        await M14LEventStateInput(control, 101);
    }
    if (productionIdentity)
    {
        await NpcAuthorityInputs(control, false);
        await M4CombatInputs(control);
        await M4VitalInputs(control);
        await M6QualifiedBusinessInputs(control);
        await ProductionChestResizeInputs(control);
        await M5ContainerCausalityScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))), "Production");
        await M7CombatScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand,
            FixtureSnapshot, () => console.ToArray(), () => verified && ready, Assert,
            (name, account, rule) => provedAccounts.Add((name, account, rule))), control, "Production");
        await M7SigilWorldInputs(control);
        await M13SourceDrivenInputs(control);
        await M14SourceDrivenInputs(control);
        if (m14lProductionRuleCandidates.Keys.All(expectedProductionRules.ContainsKey))
        {
            await M14LPlayerBuffInput(control);
            await M14LNpcBuffInput(control);
            await M14LEventStateInput(control, 101);
        }
        await M14RNpcShimmerInput(control);
        await M14RWorldAlignmentInput(control);
        await M14RCavernMonsterInput(control);
        await M15LowContextInputs(control);
        // The withdrawn C6 monotonic-damage claim remains ineligible in every scope.
        // Its native wrap/reflection counterexample is also accepted by independent C7.
        var unadmitted = await Connect("M5Unadmitted");
        await unadmitted.Join(); await unadmitted.RegisterAndLogin();
        uint unadmittedKey = LabClient.ProjectileKey(unadmitted.Slot, 361, 7);
        await unadmitted.Send(27, w => unadmitted.WriteProjectileDamage(w, unadmittedKey, 1, 4));
        await unadmitted.Send(27, w => unadmitted.WriteProjectileDamage(w, unadmittedKey, 1, 16385));
        await unadmitted.Send(120, w => { w.Write(unadmitted.Slot); w.Write((byte)13); });
        await control.WaitUntil(() => control.Bubbles.Any(x => x.Player == unadmitted.Slot && x.Emote == 13), TimeSpan.FromSeconds(5));
        Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + unadmitted.Name) == 0,
            "production-identity:withdrawn-C6-native-wrap-counterexample-remains-allowed");
        await unadmitted.DisposeAsync();
        Assert(provedAccounts.Select(x => x.Rule).Order(StringComparer.Ordinal).SequenceEqual(expectedProductionRules.Keys.Order(StringComparer.Ordinal)),
            "production-identity:every-exact-admitted-rule-has-its-own-first-proof-actor");
    }
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == provedAccounts.Count, "one-record-per-proven-actor-across-qualified-lab-rules");
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + control.Name) == 0, "shared-recipient-account-unbanned-across-all-rules");
    CaptureDatabase("before-restart");
    foreach (var client in clients) await client.DisposeAsync();
    await StopServer();
    Assert(runSafetyClean && !forcedStop && exitCodes.Last() == 0, "clean-shutdown-before-restart");
    server.Close();
    await StartServer(true);
    restarted = true;
    Assert(verified && ready, "same-database-restart-target-identity");
    if (productionIdentity) Assert(actualScope == "Production" && productionHardRules == expectedProductionRules.Count, "production-identity:restart-keeps-scope-and-compiled-admission");
    // Keep a normal authenticated account online while checking this whole rule set.
    // The dedicated SoleActor mode independently covers the zero-online persistence path.
    var restoredControl = await Connect(control.Name);
    await restoredControl.Join();
    await restoredControl.Login();
    Assert(restoredControl.Authenticated && restoredControl.SscSlots.Count >= 350 && restoredControl.DisconnectReason is null,
        "innocent-account-login-and-ssc-after-restart");
    foreach (var account in provedAccounts)
    {
        var restoredAttacker = await Connect(account.Name);
        await restoredAttacker.Join();
        await restoredAttacker.LoginAndExpectRejected();
        Assert(restoredAttacker.LoginAttempts == 1 && restoredAttacker.IsBanRejection, account.Rule + ":permanent-account-ban-survives-server-restart");
        Assert(CharacterInventory(account.Account).Split('~')[10].StartsWith("0,"), account.Rule + ":revoked-inventory-unchanged-after-restart");
        await restoredAttacker.DisposeAsync();
    }
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == provedAccounts.Count, "restart-no-duplicate-account-ban");
    CaptureDatabase("after-restart");
    status = "passed";
    }
    }
}
catch (DevRunCanceledException ex)
{
    status = "canceled";
    failure = ex.GetType().Name + ": " + ex.Message;
    Console.WriteLine(failure);
}
catch (Exception ex)
{
    failure = ex.GetType().Name + ": " + ex.Message;
    Console.WriteLine(failure);
}
finally
{
    // A cancellation request only unwinds the scenario. It must never interrupt the
    // existing client disposal, native session drain, or strict server shutdown.
    try { devLifecycle?.EnterCleanup(); }
    catch (Exception ex) { status = "failed"; failure += " lifecycle-cleanup:" + ex.GetType().Name; }
    if (vanillaPeerStartRequested)
    {
        try { await StopVanillaPeerObserver(); }
        catch (Exception ex) { status = "failed"; failure += " peer-observer-stop:" + ex.GetType().Name; }
    }
    foreach (var client in clients) await client.DisposeAsync();
    if (started)
    {
        try { await StopServer(); }
        catch (Exception ex)
        {
            status = "failed";
            failure += " shutdown-report-failure:" + ex.GetType().Name;
            if (!server.HasExited) { forcedStop = true; server.Kill(true); await server.WaitForExitAsync(); }
            serverExitCode = server.ExitCode;
        }
    }
    if (crashRecovery)
    {
        // This scenario intentionally kills the first server, then expects a normal shutdown
        // of the restricted restart. It must retain its unclean marker and unresolved intent.
        if (!forcedStop || !restarted || serverExitCode != 0 || runSafetyClean) status = "failed";
    }
    else if ((status != "canceled" || serverExitCode.HasValue) &&
        (forcedStop || serverExitCode != 0 || !withoutAntiCheat && !runSafetyClean)) status = "failed";
    if (devLifecycle is not null && await devLifecycle.FinishMonitoringAsync() is { } monitorFault)
    { status = "failed"; failure += " lifecycle-monitor:" + monitorFault; }
    if (m10HandshakeProbe) await M10HandshakeProbe.WriteReport(report, status, handshakeProbes);
    var result = new { schemaVersion = 1, status, failure, requestedScenario, actualScenario, target = "Terraria 1.4.5.8 / protocol 326", scope = expectedScope, actualScope, productionHardRules,
        evidenceSource = vanilla ? "interactive-client-identity-requires-separate-launch-and-gameplay-evidence"
            : observedReplay ? "recorded-tool-frame-replay-with-synthetic-controls" : "synthetic-tcp",
        syntheticClient = !vanilla, vanillaGui = vanilla ? "requires-scenario-evidence" : "not_executed", withoutAntiCheat, inventoryCases, productionIdentity, mcpIdentity, validatedProductionRuleScope = mcpIdentity ? "A01.EmojiSenderMismatch/m2.1 only" : null, handshakeCases, m10HandshakeProbe, crashRecovery, runtimeDiagnostics, gameplaySafety, gameplayBusiness, protectionCases, helloDiagnostics,
        revokedPacketDiagnostics = productionIdentity ? "TestLab-only trace not emitted; incident revoked flag and recipient/SSC side effects checked" : protectionCases && protectionSlice == "m17progressionworld" ? "Not applicable: E01 Production legal/Unknown scenario has no sanction event" : "TestLab trace required",
        isolatedDirectory = run, loopbackPort = port,
        actualRuntimeIdentityVerified = verified, batchedWriteCalls = clients.Sum(x => x.ProofBatches),
        batchedWriteCountMeaning = "SendBatch calls, including legal controls; first-proof claims require per-scenario evidence",
        provedAccounts = provedAccounts.Select(x => new { x.Name, x.Account, x.Rule }),
        forcedStop, serverExitCode, runSafetyClean, restarted, exitCodes, databaseEvidence, elapsedMs = timer.ElapsedMilliseconds, checks,
        clients = clients.Select(c => c.Evidence()), incidentLines = console.Where(x => x.Contains("ANTICHEAT_INCIDENT") || x.Contains("ANTICHEAT_REVOKED_PACKET")).ToArray() };
    await File.WriteAllTextAsync(Path.Combine(report, "summary.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { status, failure, report, checks = checks.Count, elapsedMs = timer.ElapsedMilliseconds }));
    devLifecycle?.Complete(status, status == "canceled" ? 130 : status is "passed" or "prepared_only" ? 0 : 1,
        failure, forcedStop, runSafetyClean, serverExitCode);
}
return status == "canceled" ? 130 : status is "passed" or "prepared_only" ? 0 : 1;

async Task StartServer(bool restart)
{
    DevRunLifecycle.Check();
    devLifecycle?.Phase(restart ? "server_restarting" : "server_starting");
    initialized = ready = verified = false;
    actualScope = "unobserved"; productionHardRules = -1;
    startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    tables = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // Capture the bytes actually present before each process start. TShock may migrate/rewrite
    // partial config files at startup; these copies are not claims about those later effective values.
    var configurationDirectory = Path.Combine(report, restart ? "configuration-before-restart" : "configuration-before-start");
    Directory.CreateDirectory(configurationDirectory);
    var configurationFiles = new List<object>();
    foreach (string relative in new[] { "serverconfig.txt", "tshock/config.json", "tshock/sscconfig.json", "tshock/anticheat.json" })
    {
        string sourcePath = Path.Combine(run, relative);
        byte[] bytes = await File.ReadAllBytesAsync(sourcePath);
        string copyName = relative.Replace('/', '-');
        await File.WriteAllBytesAsync(Path.Combine(configurationDirectory, copyName), bytes);
        configurationFiles.Add(new { runRelativePath = relative, snapshotFile = copyName,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes = bytes.Length });
    }
    await File.WriteAllTextAsync(Path.Combine(configurationDirectory, "manifest.json"), JsonSerializer.Serialize(new
    { utc = DateTimeOffset.UtcNow, beforeProcessStart = true, restartedProcess = restart, files = configurationFiles,
        scope = "startup-input-bytes; effective runtime settings and compiled qualifications are observed separately" },
        new JsonSerializerOptions { WriteIndented = true }));
    if (!server.Start()) throw new InvalidOperationException("Server did not start.");
    started = true;
    devLifecycle?.ServerStarted(server);
    stdout = Capture(server.StandardOutput, restart ? "stdout-restart.log" : "stdout.log", true);
    stderr = Capture(server.StandardError, restart ? "stderr-restart.log" : "stderr.log", false);
    await DevRunLifecycle.WaitAsync(startup.Task, TimeSpan.FromSeconds(90));
    var evidence = Path.Combine(run, "tshock", "anticheat-runtime-evidence.json");
    if (File.Exists(evidence)) File.Copy(evidence, Path.Combine(report, restart ? "runtime-evidence-restart.json" : "runtime-evidence.json"), true);
    if (productionIdentity)
    {
        using var runtimeEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidence));
        Assert(runtimeEvidence.RootElement.GetProperty("Scope").GetString() == "Production", "production-identity:runtime-evidence-confirms-production" + (restart ? "-restart" : ""));
        var admitted = runtimeEvidence.RootElement.GetProperty("QualifiedRules").EnumerateArray()
            .Select(x => (Id: x.GetProperty("RuleId").GetString()!, Version: x.GetProperty("Version").GetString()!)).ToArray();
        Assert(admitted.Length == expectedProductionRules.Count && admitted.All(x =>
            expectedProductionRules.TryGetValue(x.Id, out var version) && x.Version == version),
            "production-identity:runtime-evidence-matches-exact-reviewed-ID-version-set" + (restart ? "-restart" : ""));
    }
}
async Task StopServer()
{
    using var devCancellationSuppression = DevRunLifecycle.SuppressCancellation();
    var shutdownTimer = Stopwatch.StartNew();
    string shutdownStage = "already-exited", shutdownFailure = "";
    int emptyQueries = 0;
    bool drainConfirmed = false, forcedThisStop = false;
    string[] pendingLeaves = Array.Empty<string>();
    string? serverLog = null;
    int damagedLogLines = 0, recoveredLogRecords = 0;
    bool unresolvedLifecycleLog = false;
    var logDamageSamples = new List<object>(16);
    if (!server.HasExited)
    {
        try
        {
            shutdownStage = "client-disposal";
            if (clients.Any(client => !client.Disposed))
                throw new InvalidOperationException("Shutdown requires disposal of every lab client before draining server sessions.");

            // Closing our TCP sockets is earlier than TShock.OnLeave clearing Players[slot].
            // who alone excludes inactive/unfinished players, whereas Utils.StopServer visits
            // every non-null TSPlayer. TShock's file log records the post-clear disconnect;
            // stdout is unsuitable because concurrent who/leave output can share a line.
            // Read only this process's log, then require a fresh empty who response.
            shutdownStage = "server-session-drain";
            serverLog = Directory.EnumerateFiles(Path.Combine(run, "logs"), "*.log")
                .Where(path => File.GetCreationTimeUtc(path) >= server.StartTime.ToUniversalTime().AddSeconds(-1))
                .OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault();
            if (serverLog is null) throw new IOException("No TShock log for the current server process.");
            var drainDeadline = TimeSpan.FromSeconds(5);
            while (!server.HasExited && shutdownTimer.Elapsed < drainDeadline && !drainConfirmed)
            {
                int boundary = console.Count;
                await server.StandardInput.WriteLineAsync("who");
                await server.StandardInput.FlushAsync();
                emptyQueries++;
                var queryDeadline = shutdownTimer.Elapsed + TimeSpan.FromMilliseconds(500);
                while (!server.HasExited && shutdownTimer.Elapsed < drainDeadline && shutdownTimer.Elapsed < queryDeadline)
                {
                    var snapshot = console.ToArray();
                    var joined = new Dictionary<string, int>(StringComparer.Ordinal);
                    using var logStream = new FileStream(serverLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (logStream.Length > 8 * 1024 * 1024) throw new IOException("Bounded shutdown log limit exceeded.");
                    using var logReader = new StreamReader(logStream);
                    int logLines = 0;
                    damagedLogLines = 0; recoveredLogRecords = 0; unresolvedLifecycleLog = false; logDamageSamples.Clear();
                    while (logReader.ReadLine() is { } line)
                    {
                        if (++logLines > 100000) throw new IOException("Bounded shutdown log line limit exceeded.");
                        var parsedLog = NativeShutdownLogParser.Parse(line);
                        recoveredLogRecords += parsedLog.RecoveredRecords;
                        unresolvedLifecycleLog |= parsedLog.UnresolvedLifecycle;
                        if (parsedLog.Damaged || parsedLog.UnresolvedLifecycle)
                        {
                            damagedLogLines++;
                            if (logDamageSamples.Count < 16) logDamageSamples.Add(new { lineNumber = logLines,
                                rawLine = line.Length <= 2048 ? line : line[..2048], rawLineTruncated = line.Length > 2048,
                                rawLineSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(line))),
                                parsedLog.HeaderCount, parsedLog.RecoveredRecords, parsedLog.UnresolvedLifecycle, parsedLog.Events });
                        }
                        foreach (var entry in parsedLog.Events)
                        {
                            if (entry.Joined) { joined[entry.Name] = joined.GetValueOrDefault(entry.Name) + 1; continue; }
                            // UUID rejection may leave before a join broadcast; its cleanup
                            // must not cancel a later connection generation with this name.
                            if (joined.GetValueOrDefault(entry.Name) > 0) joined[entry.Name]--;
                        }
                    }
                    pendingLeaves = joined.Where(pair => pair.Value > 0).Select(pair => pair.Key + ":" + pair.Value).Take(64).ToArray();
                    drainConfirmed = !unresolvedLifecycleLog && pendingLeaves.Length == 0 && snapshot.Skip(boundary).Any(line =>
                        line.Trim().TrimStart(':').TrimStart() == "There are currently no players online.");
                    if (drainConfirmed) break;
                    await Task.Delay(25);
                }
            }
            if (!drainConfirmed)
                throw new TimeoutException("No fresh empty-server response with completed join/leave cleanup within 5 seconds; pending=" + string.Join(",", pendingLeaves));

            shutdownStage = "process-exit";
            await server.StandardInput.WriteLineAsync("exit-nosave");
            await server.StandardInput.FlushAsync();
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            shutdownStage = "exited";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            shutdownFailure = shutdownStage + ":" + ex.GetType().Name + ": " + ex.Message;
            failure += (failure.Length == 0 ? "" : " | ") + "shutdown-" + (exitCodes.Count + 1) + ":" + shutdownFailure;
            Console.WriteLine("SHUTDOWN FAILURE " + shutdownFailure);
            forcedStop = true;
            forcedThisStop = true;
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    serverExitCode = server.ExitCode;
    exitCodes.Add(server.ExitCode);
    if (helloDiagnostics)
    {
        string diagnostic = Path.Combine(run, "tshock", "anticheat", "m7-lab-diagnostics", "m7-connection-diagnostics.json");
        if (File.Exists(diagnostic))
            File.Copy(diagnostic, Path.Combine(report, $"m7-connection-diagnostics-stop-{exitCodes.Count}.json"), true);
        string lifecycle = Path.Combine(run, "tshock", "anticheat", "m7-lab-diagnostics", "m10-connection-lifecycle.json");
        if (File.Exists(lifecycle))
            File.Copy(lifecycle, Path.Combine(report, $"m10-connection-lifecycle-stop-{exitCodes.Count}.json"), true);
    }
    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
    var state = Path.Combine(run, "tshock", "anticheat", "enforcement", "run-safety.state");
    if (File.Exists(state))
    {
        using var json = JsonDocument.Parse(File.ReadAllText(state));
        runSafetyClean = json.RootElement.GetProperty("State").GetProperty("Clean").GetBoolean();
        File.Copy(state, Path.Combine(report, "run-safety-" + exitCodes.Count + ".state"), true);
    }
    await File.WriteAllTextAsync(Path.Combine(report, "shutdown-" + exitCodes.Count + ".json"),
        JsonSerializer.Serialize(new { serverProcessId = server.Id, serverLog, shutdownStage, shutdownFailure, drainConfirmed,
            emptyQueries, pendingLeaves, damagedLogLines, recoveredLogRecords, unresolvedLifecycleLog, logDamageSamples,
            forcedThisStop, serverExitCode, runSafetyClean, elapsedMs = shutdownTimer.ElapsedMilliseconds },
            new JsonSerializerOptions { WriteIndented = true }));
    started = false;
}

async Task Capture(StreamReader reader, string name, bool observe)
{
    await using var writer = new StreamWriter(Path.Combine(report, name), false, new UTF8Encoding(false));
    int retained = 0;
    while (await reader.ReadLineAsync() is { } line)
    {
        var safe = Regex.Replace(line, @"(?i)(/setup|/auth)\s+\d+", "$1 [REDACTED]");
        safe = Regex.Replace(safe, @"(?i)(/register|/login)\s+\S+", "$1 [REDACTED]");
        safe = safe.Replace("LocalLab784!", "[REDACTED]", StringComparison.Ordinal);
        if (++retained > 100000) throw new IOException("Bounded server log limit exceeded.");
        await writer.WriteLineAsync(safe); await writer.FlushAsync();
        console.Enqueue(safe);
        if (!observe) continue;
        if (line.Contains("ANTICHEAT_M1_INITIALIZED"))
        {
            initialized = true;
            actualScope = Regex.Match(line, @"\bscope=(\w+)").Groups[1].Value;
            if (int.TryParse(Regex.Match(line, @"\bproductionHardRules=(\d+)").Groups[1].Value, out int count)) productionHardRules = count;
            verified = line.Contains("runtimeVerified=true", StringComparison.OrdinalIgnoreCase) && actualScope == expectedScope;
        }
        if (line.Trim().EndsWith("Server started")) ready = true;
        if (line.Contains("ANTICHEAT_TABLES_READY") && line.Contains("items=True", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("buffs=True", StringComparison.OrdinalIgnoreCase) && line.Contains("projectiles=True", StringComparison.OrdinalIgnoreCase)) tables.TrySetResult();
        if ((initialized || withoutAntiCheat) && ready) startup.TrySetResult();
        if (line.Contains("Startup aborted")) startup.TrySetException(new IOException("Server startup aborted; inspect stdout.log"));
    }
}
void Assert(bool condition, string check)
{
    // An already evaluated failed assertion remains a failure even when cancellation races it.
    if (condition) DevRunLifecycle.Check();
    checks.Add(new { check, passed = condition, elapsedMs = timer.ElapsedMilliseconds });
    Console.WriteLine((condition ? "PASS " : "FAIL ") + check);
    if (!condition) throw new InvalidOperationException(check);
}
async Task<LabClient> Connect(string name, string? uuid = null, IPAddress? localBind = null)
{
    DevRunLifecycle.Check();
    // Optional, explicitly recorded lab isolation condition. A client-side packet2/Dispose
    // precedes complete native Reset; rapid reconnect races remain a separate unresolved
    // runtime boundary. Pacing is not a server fix, cleanup acknowledgement or packet retry.
    double pacingWait = 0;
    long lastDisposal = clients.Select(x => x.DisposalCompletedTimestamp).DefaultIfEmpty().Max();
    if (connectionPacingMilliseconds > 0 && lastDisposal > 0)
    {
        var remaining = TimeSpan.FromMilliseconds(connectionPacingMilliseconds) - Stopwatch.GetElapsedTime(lastDisposal);
        if (remaining > TimeSpan.Zero)
        {
            long started = Stopwatch.GetTimestamp();
            await Task.Delay(remaining);
            pacingWait = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }
    var client = new LabClient(name, uuid ?? Guid.NewGuid().ToString());
    client.PreConnectPacingWaitMilliseconds = pacingWait;
    clients.Add(client);
    await client.Connect(port, localBind);
    return client;
}
async Task StartVanillaPeerObserver()
{
    if (vanillaPeerStartRequested)
    {
        Console.WriteLine($"VANILLA_PEER_OBSERVER_ALREADY_CREATED status={vanillaPeerStatus}; one observer connection is permitted per run.");
        return;
    }
    vanillaPeerStartRequested = true;
    vanillaPeerStartedUtc = DateTimeOffset.UtcNow;
    vanillaPeerStatus = "starting";
    try
    {
        vanillaPeerObserver = await Connect("M7VanillaPeer");
        vanillaPeerObserver.EnableVanillaPeerCapture();
        await vanillaPeerObserver.Join();
        await vanillaPeerObserver.RegisterAndLogin();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(run, "tshock", "tshock.sqlite") + ";Mode=ReadOnly"))
        {
            db.Open(); using var query = db.CreateCommand();
            query.CommandText = "SELECT ID, UserGroup FROM Users WHERE Username=$name";
            query.Parameters.AddWithValue("$name", vanillaPeerObserver.Name);
            using var row = query.ExecuteReader();
            if (!row.Read()) throw new InvalidOperationException("The observer's authenticated account row is missing.");
            vanillaPeerAccount = row.GetInt64(0); vanillaPeerGroup = row.GetString(1);
        }
        if (!vanillaPeerObserver.Authenticated || vanillaPeerObserver.Closed || vanillaPeerObserver.SscSlots.Count < 350 ||
            vanillaPeerAccount <= 0 || vanillaPeerGroup != "default")
            throw new InvalidOperationException("The observer must complete normal SSC login as an ordinary default-group account.");
        vanillaPeerObserver.StartPassiveObservation();
        vanillaPeerStatus = "observing";
        await SaveVanillaPeerObserver();
        Console.WriteLine($"VANILLA_PEER_OBSERVER_READY name={vanillaPeerObserver.Name} slot={vanillaPeerObserver.Slot} accountId={vanillaPeerAccount} group={vanillaPeerGroup}; stop-peer-observer saves final S2C evidence.");
    }
    catch (Exception ex)
    {
        vanillaPeerStatus = "failed"; vanillaPeerFailure = ex.GetType().Name + ": " + ex.Message;
        if (vanillaPeerObserver is not null) await vanillaPeerObserver.DisposeAsync();
        vanillaPeerStoppedUtc = DateTimeOffset.UtcNow;
        await SaveVanillaPeerObserver();
        Console.WriteLine("VANILLA_PEER_OBSERVER_FAILED " + vanillaPeerFailure);
    }
}
async Task StopVanillaPeerObserver()
{
    if (!vanillaPeerStartRequested) { Console.WriteLine("VANILLA_PEER_OBSERVER_NOT_STARTED"); return; }
    if (vanillaPeerObserver is { Closed: true, Disposed: false })
    {
        vanillaPeerStatus = "failed";
        vanillaPeerFailure = "Observer connection closed before the local stop; inspect captured receive diagnostics.";
    }
    if (vanillaPeerObserver is not null) await vanillaPeerObserver.DisposeAsync();
    vanillaPeerStoppedUtc ??= DateTimeOffset.UtcNow;
    if (vanillaPeerStatus != "failed") vanillaPeerStatus = "stopped";
    await SaveVanillaPeerObserver();
    Console.WriteLine("VANILLA_PEER_OBSERVER_SAVED " + Path.Combine(report, "vanilla-peer-observer.json"));
}
async Task SaveVanillaPeerObserver()
{
    var evidence = new
    {
        schemaVersion = 1, status = vanillaPeerStatus, failure = vanillaPeerFailure,
        direction = "server-to-this-independent-peer", source = "normal-loopback-LabClient-receiving-server-output-during-separate-vanilla-GUI-experiment",
        claimLimit = "Received server frames are not the GUI client's original sent frames and do not by themselves prove a particular attack cause.",
        observerName = vanillaPeerObserver?.Name, observerSlot = vanillaPeerObserver?.Slot,
        authenticatedAccountId = vanillaPeerAccount, accountGroup = vanillaPeerGroup,
        vanillaPeerStartedUtc, vanillaPeerStoppedUtc,
        guiPlayerMovedByObserver = false, attackFramesGeneratedByObserver = false, scaffoldRequired = false, permissionsChanged = false,
        captured = vanillaPeerObserver?.VanillaPeerCaptureEvidence()
    };
    await File.WriteAllTextAsync(Path.Combine(report, "vanilla-peer-observer.json"),
        JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
}
async Task M4CombatInputs(LabClient observer)
{
    uint ritualKey = 0, portalKey = 0;
    await AdditionalRule("M4Ritual", "C2.CultistRitualRole", observer,
        async actor =>
        {
            uint legal = LabClient.ProjectileKey(actor.Slot, 201, 6);
            await actor.Send(27, w => actor.WriteProjectile(w, legal, 1));
            await observer.WaitUntil(() => observer.Projectiles.Any(x => x.Key == legal && x.Type == 1), TimeSpan.FromSeconds(5));
        },
        actor =>
        {
            ritualKey = LabClient.ProjectileKey(actor.Slot, 202, 6);
            return LabClient.Packet(27, w => actor.WriteProjectile(w, ritualKey, 490));
        }, () => observer.Projectiles.All(x => x.Key != ritualKey));
    await AdditionalRule("M4PortalDamage", "C6.PortalPlacementDamage", observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(27, w => actor.WriteProjectileDamage(w, LabClient.ProjectileKey(actor.Slot, 203, 6), 602, 0));
            await Until(() => console.Any(x => x.Contains("rule=C6.PortalPlacementDamage ") && x.Contains("verdict=Pass ") &&
                x.Contains("accountId=" + account + " ")), TimeSpan.FromSeconds(5));
            await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)4); });
            await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 4), TimeSpan.FromSeconds(5));
        },
        actor =>
        {
            portalKey = LabClient.ProjectileKey(actor.Slot, 204, 6);
            return LabClient.Packet(27, w => actor.WriteProjectileDamage(w, portalKey, 602, 1));
        }, () => observer.Projectiles.All(x => x.Key != portalKey));
}
async Task M4VitalInputs(LabClient observer)
{
    foreach (var vital in new[] { (Packet: (byte)16, Rule: "VITAL01.RawLifeMaximum", Native: (short)500, Limit: (short)504, Bad: (short)505),
        (Packet: (byte)42, Rule: "VITAL02.RawManaMaximum", Native: (short)200, Limit: (short)219, Bad: (short)220) })
    {
        byte subjectSlot = 255;
        int before = observer.VitalUpdates.Count;
        await AdditionalRule("M4Vital" + vital.Packet, vital.Rule, observer,
            async actor =>
            {
                subjectSlot = actor.Slot;
                long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
                await actor.Send(vital.Packet, w => { w.Write(actor.Slot); w.Write((short)100); w.Write(vital.Native); });
                await Until(() => console.Any(x => x.Contains("rule=" + vital.Rule + " ") && x.Contains("verdict=Pass ") &&
                    x.Contains("accountId=" + account + " ")), TimeSpan.FromSeconds(5));
                // No claim that core must accept noncanonical maxima: only this rule must not turn
                // a possible old-save/native increment into permanent punishment.
                await actor.Send(vital.Packet, w => { w.Write(actor.Slot); w.Write((short)100); w.Write(vital.Limit); });
                await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)4); });
                await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 4), TimeSpan.FromSeconds(5));
                Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0,
                    vital.Rule + ":noncanonical-native-increment-not-permanently-punished");
            }, actor => LabClient.Packet(vital.Packet, w => { w.Write(actor.Slot); w.Write((short)100); w.Write(vital.Bad); }),
            () => observer.VitalUpdates.Skip(before).All(x => x.Packet != vital.Packet || x.Player != subjectSlot || x.Maximum != vital.Bad));
    }
}
async Task M5BusinessInputs(LabClient observer)
{
    await M7CombatScenario.RunAsync(new(run, report, name => Connect(name), ConsoleCommand, FixtureSnapshot,
        () => console.ToArray(), () => verified && ready, Assert,
        (name, account, rule) => provedAccounts.Add((name, account, rule))), observer);
    await M6QualifiedBusinessInputs(observer);
    await M7SigilWorldInputs(observer);
}
M15LowContextHost M15Host(LabClient observer) => new(observer,
    (name, rule, witness, legal, request, noSideEffect) => AdditionalRule(name, rule, witness, legal, request, noSideEffect),
    RuleOutcome, Scalar, Assert, report);

async Task M15LowContextInputs(LabClient observer)
{
    var host = M15Host(observer);
    await M15ProtocolProjectileScenario.RunAsync(host);
    await M15NpcBuffTypeScenario.RunAsync(host);
}

async Task M15LowContextSlice()
{
    Assert(ready && verified && actualScope == "TestLab", "M15:actual-verified-testlab-scope");
    var observer = await Connect("M15Witness"); await observer.Join(); await observer.RegisterAndLogin();
    Assert(observer.Authenticated && observer.SscSlots.Count >= 350, "M15:independent-authenticated-witness");
    await M15LowContextInputs(observer);
    Assert(provedAccounts.Count == 3 && provedAccounts.Select(row => row.Rule).Distinct().Count() == 3,
        "M15:three-independent-first-proof-accounts");
    CaptureDatabase("m15-before-restart");
    foreach (var client in clients) await client.DisposeAsync();
    await StopServer();
    Assert(runSafetyClean && !forcedStop && exitCodes.Last() == 0, "M15:clean-shutdown-before-restart");
    server.Close(); await StartServer(true); restarted = true;
    var restored = await Connect(observer.Name); await restored.Join(); await restored.Login();
    Assert(restored.Authenticated && !restored.Closed, "M15:innocent-account-restored");
    foreach (var account in provedAccounts)
    {
        var attacker = await Connect(account.Name); await attacker.Join(); await attacker.LoginAndExpectRejected();
        Assert(attacker.LoginAttempts == 1 && attacker.IsBanRejection, account.Rule + ":restart-permanent-ban");
        Assert(CharacterInventory(account.Account).Split('~')[10].StartsWith("0,"), account.Rule + ":restart-revoked-write-absent");
        await attacker.DisposeAsync();
    }
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == 3 &&
        Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + observer.Name) == 0,
        "M15:exact-three-bans-no-innocent-or-duplicate-record");
    CaptureDatabase("m15-after-restart");
    await File.WriteAllTextAsync(Path.Combine(report, "m15-low-context.json"), JsonSerializer.Serialize(new
    {
        scope = actualScope, source = "synthetic-loopback-TCP", guiExecution = false,
        provedAccounts = provedAccounts.Select(row => new { row.Name, row.Account, row.Rule }),
        enforcement = "first-proof cancellation and revocation; same-batch successor absent; permanent account records; immediate and restarted rejection"
    }, new JsonSerializerOptions { WriteIndented = true }));
}

async Task M14RLowContextSlice()
{
    Assert(ready && verified && actualScope == "TestLab", "M14R:actual-verified-testlab-scope");
    var observer = await Connect("M14RWitness"); await observer.Join(); await observer.RegisterAndLogin();
    Assert(observer.Authenticated && observer.SscSlots.Count >= 350, "M14R:independent-authenticated-witness");
    await M14RNpcShimmerInput(observer);
    await M14RWorldAlignmentInput(observer);
    await M14RCavernMonsterInput(observer);
    Assert(provedAccounts.Count == 3 && provedAccounts.Select(row => row.Rule).Distinct().Count() == 3,
        "M14R:three-independent-first-proof-accounts");
    CaptureDatabase("m14r-before-restart");
    foreach (var client in clients) await client.DisposeAsync();
    await StopServer();
    Assert(runSafetyClean && !forcedStop && exitCodes.Last() == 0, "M14R:clean-shutdown-before-restart");
    server.Close(); await StartServer(true); restarted = true;
    var restored = await Connect(observer.Name); await restored.Join(); await restored.Login();
    Assert(restored.Authenticated && !restored.Closed, "M14R:innocent-account-restored");
    foreach (var account in provedAccounts)
    {
        var attacker = await Connect(account.Name); await attacker.Join(); await attacker.LoginAndExpectRejected();
        Assert(attacker.LoginAttempts == 1 && attacker.IsBanRejection, account.Rule + ":restart-permanent-ban");
        Assert(CharacterInventory(account.Account).Split('~')[10].StartsWith("0,"), account.Rule + ":restart-revoked-write-absent");
        await attacker.DisposeAsync();
    }
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == 3 &&
        Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + observer.Name) == 0,
        "M14R:exact-three-bans-no-innocent-or-duplicate-record");
    CaptureDatabase("m14r-after-restart");
    await File.WriteAllTextAsync(Path.Combine(report, "m14-resume-low-context.json"), JsonSerializer.Serialize(new
    {
        scope = actualScope, source = "synthetic-loopback-TCP", guiExecution = false,
        provedAccounts = provedAccounts.Select(row => new { row.Name, row.Account, row.Rule }),
        nativeNpcEffect = "NPC353/100 AddBuff and serialization tested separately at native method layer; TCP target199 activity not asserted",
        worldLegalDirection = "actual native server57 received during ordinary join",
        enforcement = "first-proof cancellation and revocation; same-batch successor absent; permanent account records; immediate and restarted rejection"
    }, new JsonSerializerOptions { WriteIndented = true }));
}

async Task M14RNpcShimmerInput(LabClient observer)
{
    const string rule = "G03.NpcShimmerAddContract";
    int before = 0;
    await AdditionalRule("M14RShimmer", rule, observer, async actor =>
    {
        long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
        Assert(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
            rule + ":ordinary-account-no-added-npc-buff-bypass");
        await actor.Send(53, w => { w.Write((short)199); w.Write((ushort)353); w.Write((short)100); });
        await RuleOutcome(account, rule, "Pass", "native-shimmer-add-duration");
        await actor.PingAsync(); await observer.PingAsync(); before = observer.NpcBuffUpdates.Count;
    }, actor => LabClient.Packet(53, w => { w.Write((short)199); w.Write((ushort)353); w.Write((short)101); }),
        () => observer.NpcBuffUpdates.Skip(before).All(update => update.Npc != 199));
}

async Task M14RWorldAlignmentInput(LabClient observer)
{
    const string rule = "WORLD01.WorldAlignmentStateAuthority";
    int before = 0;
    await AdditionalRule("M14RAlignment", rule, observer, async actor =>
    {
        Assert(actor.PacketCount(57) > 0, rule + ":native-server-publication-received-on-join");
        await actor.PingAsync(); await observer.PingAsync(); before = observer.PacketCount(57);
    }, actor => LabClient.Packet(57, w => { w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); }),
        () => observer.PacketCount(57) == before);
}

async Task M14RCavernMonsterInput(LabClient observer)
{
    const string rule = "NPC06.CavernMonsterStateAuthority";
    int before = 0;
    await AdditionalRule("M14RCavern", rule, observer, async actor =>
    {
        Assert(actor.PacketCount(136) > 0, rule + ":native-server-matrix-received-on-join");
        await actor.PingAsync(); await observer.PingAsync(); before = observer.PacketCount(136);
    }, actor => LabClient.Packet(136, w => { for (int i = 0; i < 6; i++) w.Write((ushort)i); }),
        () => observer.PacketCount(136) == before);
}

async Task M14LLowContextSlice()
{
    Assert(ready && verified && actualScope == "TestLab", "M14L:actual-verified-testlab-scope");
    using (var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "tshock", "anticheat-runtime-evidence.json"))))
    {
        // QualifiedRules intentionally lists Production only and is empty in
        // TestLab. Effective test eligibility is verified by each exact engine
        // incident plus permanent account outcome below, never by an empty list.
        Assert(evidence.RootElement.GetProperty("Scope").GetString() == "TestLab",
            "M14L:runtime-evidence-confirms-testlab");
    }
    var observer = await Connect("M14LWitness"); await observer.Join(); await observer.RegisterAndLogin();
    Assert(observer.Authenticated && observer.SscSlots.Count >= 350, "M14L:independent-authenticated-witness");
    await M14LPlayerBuffInput(observer);
    await M14LNpcBuffInput(observer);
    await M14LEventStateInput(observer, 101);
    await M14LEventStateInput(observer, 103);
    Assert(provedAccounts.Count == 4 && provedAccounts.Select(row => row.Rule).Distinct().Count() == 3,
        "M14L:four-distinct-account-proofs-cover-three-rules-two-event-messages");
    Assert(m14lProductionRuleCandidates.Keys.All(rule => provedAccounts.Any(row => row.Rule == rule)),
        "M14L:all-three-testlab-rule-eligibilities-proved-by-real-first-sanctions");
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans", null) == 4 && !observer.Closed &&
        Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + observer.Name) == 0,
        "M14L:only-four-proved-actors-banned-witness-unpunished");
    await File.WriteAllTextAsync(Path.Combine(report, "m14l-low-context-summary.json"), JsonSerializer.Serialize(new
    {
        scope = "TestLab", source = "synthetic-loopback-TCP", guiExecution = false,
        rules = m14lProductionRuleCandidates, provedAccounts = provedAccounts.Select(row => new { row.Name, row.Account, row.Rule }),
        exactPlayerComparator = "remote55/pvpBuff24/time180; sender and target set hostile30; real target55 received",
        npcComparator = "complete53/153/600 to target199, actual rule PASS; target activity/effect not claimed in this TCP fixture",
        npcSideEffectScope = "no candidate54 publication to witness; exact native active-NPC mutation/cancellation is separately tested in M14LBuffAddNativeTests/root",
        nativeEventComparator = "join101 targets the new actor; join103 broadcasts to the already-connected witness before the new actor enables broadcast at spawn12; exact Serialize/Receive/core-noop in M14LProtocolTests",
        enforcement = "first proof canceled/revoked; following inventory write absent from peer/persistence; one permanent account record and reconnect denial per actor"
    }, new JsonSerializerOptions { WriteIndented = true }));
}

async Task M14LPlayerBuffInput(LabClient observer)
{
    const string rule = "G03.PlayerBuffAddContract";
    int buffBefore = 0;
    await AdditionalRule("M14LPlayerBuff", rule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            Assert(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                rule + ":ordinary-default-account-no-added-bypass-grant");
            Assert(actor.Slot != observer.Slot, rule + ":target-is-distinct-from-sender");
            await actor.Send(30, w => { w.Write(actor.Slot); w.Write(true); });
            await observer.Send(30, w => { w.Write(observer.Slot); w.Write(true); });
            await actor.PingAsync(); await observer.PingAsync();
            int prior = observer.AddedBuffs.Count;
            await actor.Send(55, w => { w.Write(observer.Slot); w.Write((ushort)24); w.Write(180); });
            await RuleOutcome(account, rule, "Pass", "native-remote-player-buff-add-shape");
            await observer.WaitUntil(() => observer.AddedBuffs.Skip(prior).Any(buff => buff.Player == observer.Slot && buff.Type == 24 && buff.Ticks == 180), TimeSpan.FromSeconds(5));
            Assert(true, rule + ":actual-remote-pvp-buff-reached-innocent-recipient");
            await actor.PingAsync(); await observer.PingAsync(); buffBefore = observer.AddedBuffs.Count;
        }, actor => LabClient.Packet(55, w => { w.Write(actor.Slot); w.Write((ushort)24); w.Write(180); }),
        () => observer.AddedBuffs.Count == buffBefore);
    await observer.Send(30, w => { w.Write(observer.Slot); w.Write(false); }); await observer.PingAsync();
}

async Task M14LNpcBuffInput(LabClient observer)
{
    const string rule = "G03.NpcShadowFlameAddContract";
    int buffBefore = 0;
    await AdditionalRule("M14LNpcBuff", rule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            Assert(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                rule + ":ordinary-default-account-no-npc-buff-bypass-grant");
            await actor.Send(53, w => { w.Write((short)199); w.Write((ushort)153); w.Write((short)600); });
            await RuleOutcome(account, rule, "Pass", "native-shadowflame-add-duration");
            await actor.PingAsync(); await observer.PingAsync(); buffBefore = observer.NpcBuffUpdates.Count;
        }, actor => LabClient.Packet(53, w => { w.Write((short)199); w.Write((ushort)153); w.Write((short)19392); }),
        () => observer.NpcBuffUpdates.Skip(buffBefore).All(update => update.Npc != 199));
}

async Task M14LEventStateInput(LabClient observer, byte message)
{
    const string rule = "NPC05.EventStateAuthority";
    await observer.PingAsync();
    int witnessCountdownBeforeJoin = observer.PacketCount(103);
    int shieldBefore = 0, countdownBefore = 0;
    await AdditionalRule("M14LEventState" + message, rule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            // This ordinary request checks shared playability. The precise legal
            // opposite direction is native serialization/passive receive in the
            // M14LProtocolTests fixture, plus native join publications below.
            await actor.Send(61, w => { w.Write((short)actor.Slot); w.Write((short)50); });
            await RuleOutcome(account, "NPC03.BossPartSummonRequest", "Pass", "outside-exact-prime-part-request-contract");
            await actor.PingAsync(); await observer.PingAsync();
            // Native receive8 targets101 to whoAmI but broadcasts103. The new
            // connection enables buffer.broadcast only in later spawn12, so the
            // already-connected witness receives103 while this new actor need not.
            Assert(actor.PacketCount(101) > 0 && observer.PacketCount(103) > witnessCountdownBeforeJoin,
                rule + ":native-targeted101-and-existing-witness-broadcast103-received");
            await File.WriteAllTextAsync(Path.Combine(report, "m14l-event-native-delivery-" + message + ".json"), JsonSerializer.Serialize(new
            {
                requestedCandidateMessage = message, actor = actor.Name, actorSlot = actor.Slot,
                actor101 = actor.PacketCount(101), actor103 = actor.PacketCount(103),
                witness = observer.Name, witnessSlot = observer.Slot, witness103BeforeJoin = witnessCountdownBeforeJoin,
                witness103AfterJoin = observer.PacketCount(103),
                native101 = "case8 TrySendData(101,whoAmI): targeted delivery",
                native103 = "case8 TrySendData(103): only connected broadcast-enabled recipients; this new actor enables broadcast at later case12",
                evidenceSource = "actual-synthetic-TCP-receipt-matched-to-audited-native-sender-branches"
            }, new JsonSerializerOptions { WriteIndented = true }));
            shieldBefore = observer.PacketCount(101); countdownBefore = observer.PacketCount(103);
        }, actor => LabClient.Packet(message, w =>
        {
            if (message == 101) { w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); }
            else { w.Write(720); w.Write(720); }
        }),
        () => observer.PacketCount(101) == shieldBefore && observer.PacketCount(103) == countdownBefore);
}

async Task M14SourceDrivenInputs(LabClient observer)
{
    const string rule = "NPC04.BuffStateSyncAuthority";
    int buffBefore = 0;
    await AdditionalRule("M14NpcBuffPublisher", rule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(53, w => { w.Write((short)199); w.Write((ushort)24); w.Write((short)1); });
            await RuleOutcome(account, "G03.NpcBuffRemovalContract", "Pass", "native-npc-buff-add-request");
            await actor.PingAsync();
            await observer.PingAsync();
            buffBefore = observer.NpcBuffUpdates.Count;
        }, actor => LabClient.Packet(54, w =>
        {
            w.Write((short)199); w.Write((ushort)24); w.Write((ushort)321); w.Write((ushort)0);
        }),
        () => observer.NpcBuffUpdates.Skip(buffBefore).All(x => x.Npc != 199));

    const string rodRule = "G05.NaturalRodWorldBorder";
    int teleportBefore = 0;
    byte rodSlot = 255;
    await AdditionalRule("M14RodBorder", rodRule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await ConsoleCommand("group add M14RodUse tshock.tp.rod");
            await Until(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", "M14RodUse") == 1, TimeSpan.FromSeconds(5));
            await ConsoleCommand("group parent M14RodUse default");
            await Until(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default' AND Commands='tshock.tp.rod'", "M14RodUse") == 1, TimeSpan.FromSeconds(5));
            await ConsoleCommand("user group " + actor.Name + " M14RodUse");
            await Until(() => Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='M14RodUse'", actor.Name) == 1, TimeSpan.FromSeconds(5));
            Assert(true, rodRule + ":only-normal-item-teleport-permission-over-default-SSC-group");
            var position = actor.InitialSpawnPosition;
            rodSlot = actor.Slot;
            int before = observer.TeleportUpdates.Count;
            await actor.Send(65, w =>
            {
                w.Write((byte)0); w.Write((short)actor.Slot); w.Write(position.X + 16); w.Write(position.Y); w.Write((byte)1);
            });
            await RuleOutcome(account, rodRule, "Pass", "within-native-rod-world-interior");
            await observer.WaitUntil(() => observer.TeleportUpdates.Skip(before).Any(x =>
                x.Player == actor.Slot && x.X == position.X + 16 && x.Y == position.Y && x.Style == 1), TimeSpan.FromSeconds(5));
            Assert(console.Any(line => line.Contains("rule=" + rodRule + " ") && line.Contains("accountId=" + account + " ") &&
                line.Contains("action=Pass canceled=False ") && line.Contains("geometryHistoryComplete=True ") &&
                line.Contains("currentAccountAndActorBound=True ") && line.Contains("pluginContractComplete=True ") &&
                line.Contains("initialSynchronization=False ")), rodRule + ":effective-native-interior-pass-and-real-peer-relay");
            await actor.PingAsync(); await observer.PingAsync();
            teleportBefore = observer.TeleportUpdates.Count;
        }, actor => LabClient.Packet(65, w =>
        {
            // Inside the finite world, exactly at the original rod's excluded 50px boundary.
            w.Write((byte)0); w.Write((short)actor.Slot); w.Write(50f); w.Write(actor.InitialSpawnPosition.Y); w.Write((byte)1);
        }),
        () => observer.TeleportUpdates.Skip(teleportBefore).All(x => x.Player != rodSlot || x.X != 50f));
}
async Task M13SourceDrivenInputs(LabClient observer)
{
    // Same first-proof/persistence/restart harness as the existing qualified rules.
    // These are synthetic TCP requests; source/native/GUI evidence is reported separately.
    int npcBefore = 0, requestLogStart = 0;
    await AdditionalRule("M13PrimePart", "NPC03.BossPartSummonRequest", observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(61, w => { w.Write((short)actor.Slot); w.Write((short)50); });
            await RuleOutcome(account, "NPC03.BossPartSummonRequest", "Pass", "outside-exact-prime-part-request-contract");
            await actor.PingAsync();
            await observer.PingAsync();
            npcBefore = observer.NpcTypes.Count;
            requestLogStart = console.Count;
        }, actor => LabClient.Packet(61, w => { w.Write((short)actor.Slot); w.Write((short)128); }),
        () => observer.NpcTypes.Skip(npcBefore).All(x => x.Type is not (128 or 129 or 130 or 131)) &&
            !console.Skip(requestLogStart).Any(x => x.Contains("M13PrimePart summoned", StringComparison.Ordinal)));

    await AdditionalRule("M13GolemWorld", "PG-NAT-108.GolemSummonWorld", observer,
        async actor =>
        {
            // The real 245 open-world legal comparator is the separate native altar test.
            // This ordinary request only checks shared network playability before the proof.
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(61, w => { w.Write((short)actor.Slot); w.Write((short)50); });
            await RuleOutcome(account, "NPC03.BossPartSummonRequest", "Pass", "outside-exact-prime-part-request-contract");
            await actor.PingAsync();
            await observer.PingAsync();
            npcBefore = observer.NpcTypes.Count;
            requestLogStart = console.Count;
        }, actor => LabClient.Packet(61, w => { w.Write((short)actor.Slot); w.Write((short)245); }),
        () => observer.NpcTypes.Skip(npcBefore).All(x => x.Type is not (245 or 246 or 247 or 248 or 249)) &&
            !console.Skip(requestLogStart).Any(x => x.Contains("M13GolemWorld summoned", StringComparison.Ordinal)));

    const string buffRule = "G03.NpcBuffRemovalContract";
    int buffBefore = 0;
    await AdditionalRule("M13NpcBuffRemove", buffRule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(53, w => { w.Write((short)199); w.Write((ushort)24); w.Write((short)1); });
            await RuleOutcome(account, buffRule, "Pass", "native-npc-buff-add-request");
            await actor.PingAsync();
            await observer.PingAsync();
            buffBefore = observer.NpcBuffUpdates.Count;
        }, actor => LabClient.Packet(137, w => { w.Write((short)199); w.Write((ushort)24); }),
        () => observer.NpcBuffUpdates.Skip(buffBefore).All(x => x.Npc != 199));
}
async Task M7SigilWorldInputs(LabClient observer)
{
    int sigilWorldBefore = -1, sigilCountdownBefore = -1;
    await AdditionalRule("M7SigilWorld", "PG-NAT-121.CelestialSigilWorld", observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(61, w => { w.Write((short)actor.Slot); w.Write((short)50); });
            await RuleOutcome(account, "PG-NAT-012.MechdusaSummonWorld", "Pass", "other-summon-outside-mechdusa-contract");
            await actor.PingAsync();
            await observer.Drain(TimeSpan.FromMilliseconds(200));
            sigilWorldBefore = observer.PacketCount(7);
            sigilCountdownBefore = observer.PacketCount(103);
        }, actor => LabClient.Packet(61, w => { w.Write((short)actor.Slot); w.Write((short)-8); }),
        () => sigilWorldBefore >= 0 && sigilCountdownBefore >= 0 &&
            observer.PacketCount(7) == sigilWorldBefore && observer.PacketCount(103) == sigilCountdownBefore &&
            !console.Any(x => x.Contains("M7SigilWorld summoned a Moon Lord!", StringComparison.Ordinal)));
}

async Task M6QualifiedBusinessInputs(LabClient observer)
{
    const string hurtRule = "VITAL04.NonPvpHurtTarget";
    int hurtBefore = observer.HurtDeclarations.Count;
    await AdditionalRule("M5HurtRole", hurtRule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await actor.Send(117, w => LabClient.WriteHurt(w, actor.Slot, false, 1));
            await RuleOutcome(account, hurtRule, "Pass", "native-self-hurt-declaration");
            // Finite real TCP delay; a hostile toggle/core denial cannot turn a legitimate PvP role into this proof.
            await Task.Delay(120);
            await actor.Send(117, w => LabClient.WriteHurt(w, observer.Slot, true, 1));
            await RuleOutcome(account, hurtRule, "Pass", "native-cross-player-pvp-role");
            await Task.Delay(40);
        }, actor => LabClient.Packet(117, w => LabClient.WriteHurt(w, observer.Slot, false, 23)),
        () => observer.HurtDeclarations.Skip(hurtBefore).All(x => x.Target != observer.Slot || x.Damage != 23 || x.Pvp));

    const string naturalRule = "PG-NAT-012.MechdusaSummonWorld";
    int npcBefore = observer.NpcTypes.Count;
    await AdditionalRule("M5NaturalSummon", naturalRule, observer,
        async actor =>
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            // Standard King Slime request exercises the ordinary summon route; outcome remains governed by core.
            await actor.Send(61, w => { w.Write((short)actor.Slot); w.Write((short)50); });
            await RuleOutcome(account, naturalRule, "Pass", "other-summon-outside-mechdusa-contract");
        }, actor => LabClient.Packet(61, w => { w.Write((short)actor.Slot); w.Write((short)-16); }),
        () => !console.Any(x => x.Contains("M5NaturalSummon summoned Mechdusa", StringComparison.Ordinal)) &&
            observer.NpcTypes.Skip(npcBefore).All(x => x.Type is not (125 or 126 or 127 or 128 or 129 or 130 or 131 or 134 or 135 or 136)));
}
async Task AdditionalRule(string name, string rule, LabClient observer, Func<LabClient, Task> legal,
    Func<LabClient, byte[]> candidate, Func<bool> noForbiddenSideEffect, Func<Task>? refreshSideEffects = null)
{
    var actor = await Connect(name);
    await actor.Join(); await actor.RegisterAndLogin();
    long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
    Assert(actor.Authenticated && actor.SscSlots.Count >= 350 && account > 0, rule + ":fresh-actor-normal-auth-and-ssc");
    await observer.Drain(TimeSpan.FromMilliseconds(200));
    await legal(actor);
    Assert(!actor.Closed && actor.DisconnectReason is null && Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0,
        rule + ":legal-comparator-not-sanctioned");
    int logStart = console.Count, inventoryStart = observer.InventoryUpdates.Count;
    await actor.SendBatch(candidate(actor), LabClient.Packet(5, w => LabClient.WriteInventory(w, actor.Slot, 1, 9)));
    await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed, TimeSpan.FromSeconds(8));
    await observer.Drain(TimeSpan.FromSeconds(1));
    Assert(actor.DisconnectReason == "AntiCheat proven violation.", rule + ":first-proof-disconnected");
    await Until(() => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + actor.Name) == 1, TimeSpan.FromSeconds(10));
    Assert(console.Skip(logStart).Count(line => line.Contains("ANTICHEAT_INCIDENT") && line.Contains("rule=" + rule + " ") &&
        line.Contains("accountId=" + account + " ") && line.Contains("canceled=True", StringComparison.OrdinalIgnoreCase) && line.Contains("revoked=True", StringComparison.OrdinalIgnoreCase)) == 1,
        rule + ":exactly-one-canceled-revoked-incident-correct-account");
    if (refreshSideEffects is not null) await refreshSideEffects();
    Assert(noForbiddenSideEffect(), rule + ":no-forbidden-outbound-side-effect");
    if (!productionIdentity)
        Assert(console.Skip(logStart).Any(line => line.Contains("ANTICHEAT_REVOKED_PACKET") && line.Contains("slot=" + actor.Slot + " ") && line.Contains("packet=5")),
            rule + ":coalesced-followup-frame-revoked");
    Assert(observer.InventoryUpdates.Skip(inventoryStart).All(x => x.Player != actor.Slot || x.Slot != 10 || x.Item != 9) &&
        CharacterInventory(account).Split('~')[10].StartsWith("0,"), rule + ":followup-inventory-no-broadcast-or-persistence");
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + observer.Name) == 0 && !observer.Closed,
        rule + ":recipient-not-punished");
    provedAccounts.Add((actor.Name, account, rule));
    await actor.DisposeAsync();
    await Task.Delay(200);
    var reconnect = await Connect(actor.Name, actor.Uuid);
    try { await reconnect.Join(); }
    catch (IOException) when (reconnect.DisconnectReason is not null || reconnect.Closed) { }
    await reconnect.WaitUntil(() => reconnect.DisconnectReason is not null || reconnect.Closed, TimeSpan.FromSeconds(8));
    Assert(reconnect.IsBanRejection, rule + ":same-account-real-TCP-reconnect-rejected");
    await reconnect.DisposeAsync();
    await Task.Delay(100);
}
async Task RuleOutcome(long account, string rule, string verdict, string reason)
{
    await Until(() => console.Any(line => line.Contains("ANTICHEAT_RULE_INPUT ") &&
        line.Contains("accountId=" + account + " ") && line.Contains("rule=" + rule + " ") &&
        line.Contains("verdict=" + verdict + " ") && line.Contains("reason=" + reason + " ")), TimeSpan.FromSeconds(5));
}
async Task ConsoleCommand(string command)
{
    await DevRunLifecycle.RunCancelable(async token =>
    {
        await server.StandardInput.WriteLineAsync(command.AsMemory(), token);
        await server.StandardInput.FlushAsync(token);
    });
}
async Task<JsonElement> FixtureSnapshot()
{
    DevRunLifecycle.Check();
    var before = Directory.GetFiles(report, "gameplay-status-*.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
    await ConsoleCommand("qa_status");
    string? path = null;
    await Until(() => (path = Directory.GetFiles(report, "gameplay-status-*.json").FirstOrDefault(x => !before.Contains(x))) is not null, TimeSpan.FromSeconds(5));
    using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
    return data.RootElement.GetProperty("payload").Clone();
}
async Task InventoryInputs(LabClient observer)
{
    // The QA plugin is deliberately outside B4's supported production-source set.
    // Keep its real fixture/write checks, and check this domain never gains hard proof.
    // The separate no-scaffold ContainerCausality scenario owns the actual1.1.0 first proof.
    var actor = await Connect("M3Container");
    await actor.Join(); await actor.RegisterAndLogin();
    long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            await ConsoleCommand("qa_prepare " + actor.Name);
            await Until(() => File.Exists(Path.Combine(run, "qa-scaffold-room.json")), TimeSpan.FromSeconds(5));
            await actor.Drain(TimeSpan.FromMilliseconds(350));
            await ConsoleCommand("qa_chests " + actor.Name);
            await Until(() => File.Exists(Path.Combine(run, "qa-chests.json")), TimeSpan.FromSeconds(5));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "qa-chests.json")));
            var primary = manifest.RootElement.GetProperty("primary");
            var secondary = manifest.RootElement.GetProperty("secondary");
            short primaryId = primary.GetProperty("id").GetInt16(), secondaryId = secondary.GetProperty("id").GetInt16();
            await actor.Send(31, w => { w.Write(primary.GetProperty("x").GetInt16()); w.Write(primary.GetProperty("y").GetInt16()); });
            await actor.WaitUntil(() => actor.ActiveChest == primaryId, TimeSpan.FromSeconds(5));
            await actor.Send(32, w => { w.Write(primaryId); w.Write((byte)0); w.Write((short)1); w.Write((byte)0); w.Write((short)9); });
            await actor.PingAsync();
            var snapshot = await FixtureSnapshot();
            var pair = snapshot.GetProperty("chestPair");
            Assert(pair.GetProperty("primary").GetProperty("items").EnumerateArray().Any(x => x.GetProperty("slot").GetInt32() == 0 &&
                x.GetProperty("type").GetInt32() == 9 && x.GetProperty("stack").GetInt32() == 1), "B4:real-open-confirmation-and-authorized-chest-write-reached-target-state");
    int prior = console.Count;
    await actor.Send(32, w => { w.Write(secondaryId); w.Write((byte)1); w.Write((short)1); w.Write((byte)0); w.Write((short)9); });
    await actor.Drain(TimeSpan.FromMilliseconds(300));
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0 &&
        !console.Skip(prior).Any(x => x.Contains("ANTICHEAT_INCIDENT ") && x.Contains("accountId=" + account + " ")),
        "B4:unknown-extra-plugin-domain-never-upgrades-client-target-to-hard-proof");
    var after = await FixtureSnapshot();
    await File.WriteAllTextAsync(Path.Combine(report, "m5-container-extra-plugin.json"), JsonSerializer.Serialize(new {
        before = pair, after = after.GetProperty("chestPair"), productionSourceContractComplete = false,
        actualInputs = new { primaryId, secondaryId, packet = 32 },
        coreDisposition = actor.DisconnectReason, autoBan = false,
        note = "Actual fixture domain retained as an unknown-plugin negative; no claim that the invalid target was blocked by AntiCheat." }));
    await actor.DisposeAsync(); await Task.Delay(200);
}
async Task BuffListSafetyInputs(LabClient observer)
{
    var actor = await Connect("M3BuffList");
    await actor.Join(); await actor.RegisterAndLogin();
    long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
    Assert(actor.Authenticated && actor.SscSlots.Count >= 350 && account > 0, "C5.BuffListSafety:fresh-actor-normal-auth-and-ssc");
    int before = observer.BuffLists.Count;
    await actor.Send(50, w => { w.Write(actor.Slot); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)0); });
    await RuleOutcome(account, "C5.BuffListSafety", "Pass", "legal-self-buff-list-shape-and-domain");
    await observer.WaitUntil(() => observer.BuffLists.Skip(before).Any(x => x.Player == actor.Slot && x.Types.SequenceEqual(new[] { 1, 2 })), TimeSpan.FromSeconds(5));
    Assert(true, "C5.BuffListSafety:legal-self-two-id-list-forwarded-through-real-hook");
    int invalidStart = observer.BuffLists.Count, logStart = console.Count;
    // One unsafe type is filtered. There is no duration field and no complete cheat/source proof.
    await actor.Send(50, w => { w.Write(actor.Slot); w.Write(ushort.MaxValue); w.Write((ushort)0); });
    await RuleOutcome(account, "C5.BuffListSafety", "UnsafeInput", "buff-list-type-outside-target-domain");
    await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)8); });
    await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 8), TimeSpan.FromSeconds(5));
    await observer.Drain(TimeSpan.FromMilliseconds(500));
    Assert(console.Skip(logStart).Any(x => x.Contains("rule=C5.BuffListSafety ") && x.Contains("accountId=" + account + " ") &&
        x.Contains("action=Block ") && x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase)), "C5.BuffListSafety:invalid-id-blocked-at-real-raw-hook");
    Assert(observer.BuffLists.Skip(invalidStart).All(x => !x.Types.Contains(ushort.MaxValue)), "C5.BuffListSafety:invalid-id-never-broadcast-to-recipient");
    Assert(!actor.Closed && actor.DisconnectReason is null && Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0 &&
        !console.Skip(logStart).Any(x => x.Contains("ANTICHEAT_INCIDENT") && x.Contains("accountId=" + account + " ")),
        "C5.BuffListSafety:unsafe-input-does-not-revoke-or-invent-permanent-ban");
    await actor.DisposeAsync();
    await Task.Delay(200);
}
async Task ProductionChestResizeInputs(LabClient observer)
{
    const string rule = "CONTAINER01.ChestResizeAuthority";
    // Normal SendSection supplied this real chest and size. Keep Production's known plugin set
    // unchanged; the separate TestLab replay inspects every target's actual item/capacity state.
    var native = observer.ChestSizes.First(x => x.Chest >= 0 && x.Size == 40);
    short chest = checked((short)native.Chest), original = checked((short)native.Size);
    var allowed = await Connect("M5ProdResizeOk");
    await allowed.Join(); await allowed.RegisterAndLogin();
    long id = Scalar("SELECT ID FROM Users WHERE Username=$name", allowed.Name);
    await ConsoleCommand("group add M5ResizeOnly tshock.ignore.resizechests");
    await Until(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", "M5ResizeOnly") == 1, TimeSpan.FromSeconds(5));
    await ConsoleCommand("group parent M5ResizeOnly default");
    await Until(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default' AND Commands='tshock.ignore.resizechests'", "M5ResizeOnly") == 1, TimeSpan.FromSeconds(5));
    await ConsoleCommand("user group " + allowed.Name + " M5ResizeOnly");
    await Until(() => Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='M5ResizeOnly'", allowed.Name) == 1, TimeSpan.FromSeconds(5));
    await allowed.Send(155, w => { w.Write(chest); w.Write(checked((short)(original + 1))); });
    await RuleOutcome(id, rule, "Pass", "scoped-tshock-chest-resize-permission");
    await allowed.PingAsync();
    await ReadNativeSize("M5SizeRead1", original + 1);
    Assert(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + allowed.Name) == 0,
        rule + ":production-scoped-permission-real-write-confirmed-by-fresh-SendSection");
    await allowed.Send(155, w => { w.Write(chest); w.Write(original); });
    await allowed.PingAsync();
    await ReadNativeSize("M5SizeRead2", original);
    await allowed.DisposeAsync(); await Task.Delay(200);
    int blockedStart = observer.ChestSizes.Count;
    await AdditionalRule("M5ProdResizeBad", rule, observer,
        async actor =>
        {
            await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)11); });
            await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 11), TimeSpan.FromSeconds(5));
        }, actor => LabClient.Packet(155, w => { w.Write(chest); w.Write(checked((short)(original + 2))); }),
        () => observer.ChestSizes.Skip(blockedStart).All(x => x.Chest != chest || x.Size != original + 2));
    await ReadNativeSize("M5SizeRead3", original);
    async Task ReadNativeSize(string name, int expected)
    {
        var reader = await Connect(name); await reader.Join();
        Assert(reader.ChestSizes.Any(x => x.Chest == chest && x.Size == expected) &&
            reader.ChestSizes.All(x => x.Chest != chest || x.Size == expected),
            rule + ":production-actual-chest-size-via-new-native-section-" + expected + "-" + name);
        await reader.DisposeAsync(); await Task.Delay(150);
    }
}
async Task ChestResizeInputs(LabClient observer)
{
    const string rule = "CONTAINER01.ChestResizeAuthority";
    const string group = "M3ResizeOnly";
    const string permission = "tshock.ignore.resizechests";
    var initial = await FixtureSnapshot();
    var primary = initial.GetProperty("chestPair").GetProperty("primary");
    short chest = primary.GetProperty("id").GetInt16();
    short originalSize = primary.GetProperty("maxItems").GetInt16();
    short permittedSize = checked((short)(originalSize + 1));
    var allowed = await Connect("M3ResizeAllowed");
    await allowed.Join(); await allowed.RegisterAndLogin();
    long allowedAccount = Scalar("SELECT ID FROM Users WHERE Username=$name", allowed.Name);
    await ConsoleCommand("group add " + group + " " + permission);
    await Until(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name", group) == 1, TimeSpan.FromSeconds(5));
    await ConsoleCommand("group parent " + group + " default");
    await Until(() => Scalar("SELECT COUNT(*) FROM GroupList WHERE GroupName=$name AND Parent='default' AND Commands='tshock.ignore.resizechests'", group) == 1, TimeSpan.FromSeconds(5));
    await ConsoleCommand("user group " + allowed.Name + " " + group);
    await Until(() => Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='M3ResizeOnly'", allowed.Name) == 1, TimeSpan.FromSeconds(5));
    var granted = await FixtureSnapshot();
    Assert(granted.GetProperty("players").EnumerateArray().Single(x => x.GetProperty("Name").GetString() == allowed.Name).GetProperty("group").GetString() == group,
        rule + ":console-grants-only-scoped-resize-permission-over-default-group");
    await allowed.Send(155, w => { w.Write(chest); w.Write(permittedSize); });
    await RuleOutcome(allowedAccount, rule, "Pass", "scoped-tshock-chest-resize-permission");
    var resized = await FixtureSnapshot();
    Assert(resized.GetProperty("chestPair").GetProperty("primary").GetProperty("maxItems").GetInt32() == permittedSize &&
        !allowed.Closed && allowed.DisconnectReason is null && Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + allowed.Name) == 0,
        rule + ":scoped-permission-real-packet-changes-target-capacity-without-sanction");
    // Restore the one empty added slot through the same permitted network path.
    await allowed.Send(155, w => { w.Write(chest); w.Write(originalSize); });
    await allowed.Send(120, w => { w.Write(allowed.Slot); w.Write((byte)9); });
    await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == allowed.Slot && x.Emote == 9), TimeSpan.FromSeconds(5));
    var restored = await FixtureSnapshot();
    Assert(restored.GetProperty("chestPair").GetProperty("primary").GetProperty("maxItems").GetInt32() == originalSize,
        rule + ":scoped-permission-network-restore-keeps-original-capacity");
    await allowed.DisposeAsync();
    await Task.Delay(200);
    JsonElement before = default, after = default;
    int noticesBefore = observer.ChestSizes.Count;
    await AdditionalRule("M3ResizeDenied", rule, observer,
        async actor =>
        {
            Assert(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                rule + ":unprivileged-actor-retains-default-group");
            await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)10); });
            await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 10), TimeSpan.FromSeconds(5));
            before = (await FixtureSnapshot()).GetProperty("chestPair").Clone();
        },
        actor => LabClient.Packet(155, w => { w.Write(chest); w.Write(permittedSize); }),
        () => before.GetRawText() == after.GetRawText() && observer.ChestSizes.Skip(noticesBefore).All(x => x.Chest != chest || x.Size != permittedSize),
        async () => { after = (await FixtureSnapshot()).GetProperty("chestPair").Clone(); });
}
async Task NpcAuthorityInputs(LabClient observer, bool earlyCancel)
{
    foreach (byte message in new byte[] { 153, 100 })
    {
        string rule = message == 153 ? "NPC01.ServerDebuffDamage" : "NPC02.ServerPortalTeleport";
        int noticesBefore = observer.NpcAuthorityMessages.Count;
        int npcIndex = 199;
        JsonElement beforeNpc = default, afterNpc = default;
        long account = 0;
        await AdditionalRule("M3Npc" + message + (earlyCancel ? "Early" : ""), rule, observer,
            async actor =>
            {
                account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
                await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)6); });
                await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 6), TimeSpan.FromSeconds(5));
                if (earlyCancel)
                {
                    var snapshot = await FixtureSnapshot();
                    // Normal NPC spawning consumes the earliest empty slot. Use the last unused slot
                    // so the state comparator does not accidentally track a newly spawned bystander.
                    beforeNpc = snapshot.GetProperty("npcs").EnumerateArray().Last(x => !x.GetProperty("active").GetBoolean()).Clone();
                    npcIndex = beforeNpc.GetProperty("index").GetInt32();
                    await ConsoleCommand("qa_cancel_next " + actor.Name + " " + message);
                    await Until(() => console.Any(x => x.Contains("QA_CANCEL_ARMED") && x.Contains("accountId=" + account + " ") && x.Contains("packetId=" + message + " ")), TimeSpan.FromSeconds(5));
                }
            },
            actor => message == 153
                ? LabClient.Packet(message, w => { w.Write((byte)npcIndex); w.Write((short)1); })
                : LabClient.Packet(message, w => { w.Write((ushort)npcIndex); w.Write((short)0); w.Write(128f); w.Write(128f); w.Write(0f); w.Write(0f); }),
            () => observer.NpcAuthorityMessages.Skip(noticesBefore).All(x => x.Message != message || x.Npc != npcIndex) &&
                (!earlyCancel || beforeNpc.GetRawText() == afterNpc.GetRawText()),
            earlyCancel ? async () =>
            {
                var snapshot = await FixtureSnapshot();
                afterNpc = snapshot.GetProperty("npcs").EnumerateArray().Single(x => x.GetProperty("index").GetInt32() == npcIndex).Clone();
                Assert(console.Any(x => x.Contains("ANTICHEAT_RULE_INPUT rule=" + rule + " ") && x.Contains("accountId=" + account + " ") &&
                    x.Contains("alreadyCanceled=True")), rule + ":pre-cancelled-raw-frame-still-independently-proven");
            } : null);
    }
}
async Task CombatAndWorldInputs(LabClient observer)
{
    var actor = await Connect("M3Business");
    await actor.Join(); await actor.RegisterAndLogin();
    long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
    int controlsBefore = observer.ControlUpdates.Count;
    byte[] cameraControls = actor.CameraControls();
    Assert(cameraControls.Length - 3 == 22, "PlayerUpdate:camera-enabled-controls-have-target-22-byte-body");
    await actor.Send(13, w => w.Write(cameraControls.AsSpan(3)));
    await observer.WaitUntil(() => observer.ControlUpdates.Skip(controlsBefore).Any(x => x.Player == actor.Slot && x.CameraX == 2468f && x.CameraY == 1357f), TimeSpan.FromSeconds(5));
    Assert(!actor.Closed && actor.DisconnectReason is null, "PlayerUpdate:legal-camera-control-forwarded-without-malformed-rejection");
    await actor.Send(5, w => { w.Write(actor.Slot); w.Write((short)0); w.Write((short)1); w.Write((byte)0); w.Write((short)39); w.Write((byte)0); });
    await actor.UseItem(true);
    await actor.Drain(TimeSpan.FromMilliseconds(150));
    uint arrow = LabClient.ProjectileKey(actor.Slot, 61, 4);
    await actor.Send(27, w => actor.WriteProjectile(w, arrow));
    await RuleOutcome(account, "C6.WeaponProjectileCausality", "Pass", "accepted-use-projectile-compatible-not-exclusive-cause");
    Assert(true, "C6:raw-use-confirmed-by-real-update-produces-positive-compatibility-only");
    await observer.WaitUntil(() => observer.Projectiles.Any(x => x.Key == arrow), TimeSpan.FromSeconds(5));
    await actor.UseItem(false);
    await actor.Send(27, w => actor.WriteProjectile(w, LabClient.ProjectileKey(actor.Slot, 62, 4), 387));
    await RuleOutcome(account, "C3.SummonBudget", "Pass", "observed-summon-fits-without-replacement");
    Assert(true, "C3:real-runtime-half-slot-projectile-fits-existing-capacity");
    await actor.Send(5, w => { w.Write(actor.Slot); w.Write((short)0); w.Write((short)1); w.Write((byte)0); w.Write((short)2289); w.Write((byte)0); });
    await actor.UseItem(true);
    await actor.Drain(TimeSpan.FromMilliseconds(150));
    uint bobber = LabClient.ProjectileKey(actor.Slot, 63, 4);
    await actor.Send(27, w => actor.WriteProjectile(w, bobber, 360));
    await RuleOutcome(account, "C4.FishingMechanism", "Pass", "observed-bobber-compatible-with-existing-entity-or-accepted-cast");
    await observer.WaitUntil(() => observer.Projectiles.Any(x => x.Key == bobber), TimeSpan.FromSeconds(5));
    await actor.Send(27, w => actor.WriteProjectile(w, bobber, 360));
    await RuleOutcome(account, "C4.FishingMechanism", "Pass", "observed-bobber-compatible-with-existing-entity-or-accepted-cast");
    Assert(true, "C4:accepted-fishing-control-and-bobber-sync-produce-positive-compatibility-only");
    await actor.Send(122, w => { w.Write(-1); w.Write(actor.Slot); });
    await RuleOutcome(account, "WORLD-PASS", "Pass", "display-anchor-release");
    Assert(true, "E:real-display-anchor-release-accepted");
    await actor.Send(47, w => { w.Write((short)0); w.Write((short)-1); w.Write((short)0); w.Write("lab-invalid-coordinate"); w.Write(actor.Slot); w.Write((byte)0); });
    await RuleOutcome(account, "WORLD-BOUNDS", "UnsafeInput", "world-coordinate-outside-bounds");
    await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)5); });
    await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 5), TimeSpan.FromSeconds(5));
    Assert(actor.DisconnectReason is null && Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0,
        "E:unsafe-coordinate-filter-does-not-invent-account-cheat-proof");
    await actor.DisposeAsync();
    await Task.Delay(200);
}
long Scalar(string query, string? value)
{
    using var connection = new SqliteConnection("Data Source=" + Path.Combine(run, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
    connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = query;
    if (value is not null) command.Parameters.AddWithValue("$name", value);
    return Convert.ToInt64(command.ExecuteScalar());
}
string CharacterInventory(long accountId)
{
    using var connection = new SqliteConnection("Data Source=" + Path.Combine(run, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = "SELECT Inventory FROM tsCharacter WHERE Account=$id"; command.Parameters.AddWithValue("$id", accountId);
    return Convert.ToString(command.ExecuteScalar()) ?? throw new InvalidOperationException("SSC character row missing.");
}
void CaptureDatabase(string phase)
{
    using var connection = new SqliteConnection("Data Source=" + Path.Combine(run, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = "SELECT b.TicketNumber, b.Identifier, b.Reason, b.BanningUser, b.Date, b.Expiration, u.ID FROM PlayerBans b LEFT JOIN Users u ON b.Identifier = 'acc:' || u.Username";
    using var reader = command.ExecuteReader();
    while (reader.Read()) databaseEvidence.Add(new { phase, ticket = reader.GetInt64(0), identifier = reader.GetString(1), reason = reader.GetString(2),
        banningUser = reader.GetString(3), startsTicks = reader.GetInt64(4), expirationTicks = reader.GetInt64(5), authenticatedAccountId = reader.GetInt64(6) });
}
static async Task Until(Func<bool> predicate, TimeSpan timeout)
{
    DevRunLifecycle.Check();
    var watch = Stopwatch.StartNew();
    while (!predicate())
    {
        DevRunLifecycle.Check();
        if (watch.Elapsed > timeout) throw new TimeoutException("Database acceptance timed out.");
        await Task.Delay(100);
    }
}

sealed class LabClient : IAsyncDisposable
{
    readonly M7TransportDiagnostics _transport = new();
    readonly TcpClient _tcp = new() { NoDelay = true };
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _writes = new(1, 1);
    readonly Channel<byte[]> _received = Channel.CreateBounded<byte[]>(4096);
    readonly Dictionary<byte, int> _counts = new();
    readonly Dictionary<byte, int> _sent = new();
    readonly List<string> _messages = new();
    Task _reader = Task.CompletedTask;
    Task _heartbeat = Task.CompletedTask;
    Task _passiveObservation = Task.CompletedTask;
    const int PeerFrameLimit = 2048, PeerFrameBytesLimit = 1024, PeerTotalWireBytesLimit = 2 * 1024 * 1024;
    readonly object _peerCaptureSync = new();
    sealed record PeerFrame(long Sequence, DateTimeOffset Utc, long ElapsedMs, string Direction, byte PacketId, int WireBytes, string FrameHex);
    List<PeerFrame>? _peerFrames;
    volatile bool _vanillaPeerCapture;
    bool _passiveObservationStarted;
    long _peerEligibleFrames, _peerDroppedFrames, _peerOversizeFrames;
    int _peerCapturedWireBytes;
    string? _passiveObservationFailure;
    bool _disposed;
    string _phase = "created";
    string? _readFailure;
    string? _localAddress;
    string? _localAddressRaw;
    int? _localPort;
    readonly Stopwatch _lifetime = Stopwatch.StartNew();
    readonly List<object> _lifecycle = new(16);
    void Phase(string phase)
    {
        _phase = phase;
        _transport.Phase(phase);
        if (_lifecycle.Count < 24) _lifecycle.Add(new { phase, elapsedMs = _lifetime.ElapsedMilliseconds });
    }
    short _spawnX, _spawnY;
    public string Name { get; }
    public string Uuid { get; }
    public byte Slot { get; private set; } = 255;
    public bool Closed { get; private set; }
    public bool Disposed => _disposed;
    private int _pauseHeartbeat;
    public bool PauseHeartbeat { get => Volatile.Read(ref _pauseHeartbeat) != 0; set => Volatile.Write(ref _pauseHeartbeat, value ? 1 : 0); }
    public long DisposalCompletedTimestamp { get; private set; }
    public double PreConnectPacingWaitMilliseconds { get; set; }
    public bool Authenticated { get; private set; }
    public string? DisconnectReason { get; private set; }
    public bool IsBanRejection => DisconnectReason is not null &&
        (DisconnectReason.Contains("You are banned:", StringComparison.Ordinal) || DisconnectReason == "AntiCheat account admission rejected.");
    public HashSet<short> SscSlots { get; } = new();
    public byte[]? LastWorldInfo { get; private set; }
    public byte[]? LastTimeInfo { get; private set; }
    public List<(int Player, int Emote)> Bubbles { get; } = new();
    public List<(int Player, int Slot, int Item, int Stack, int Prefix)> InventoryUpdates { get; } = new();
    public Dictionary<(int Player, int Slot), byte> InventorySlotFlags { get; } = new();
    public List<(int Player, int Loadout, ushort Visibility)> LoadoutUpdates { get; } = new();
    public ushort? LiquidModuleId { get; set; }
    public List<(int X, int Y, byte Liquid, byte Type)> LiquidUpdates { get; } = new();
    public int LiquidUpdatesDropped { get; private set; }
    public ushort? LeashedModuleId { get; set; }
    public List<(int Slot, int Type, int X, int Y)> LeashedFullSyncs { get; } = new();
    public int LeashedFullSyncsDropped { get; private set; }
    public List<(uint Key, int Type)> Projectiles { get; } = new();
    public List<(uint Key, int Type, int Damage)> ProjectileClaims { get; } = new();
    public List<(uint Key, float X, float Y)> ProjectileRemovals { get; } = new();
    public List<(int Target, int Damage, bool Pvp)> HurtDeclarations { get; } = new();
    public List<(ushort Module, bool Approved)> CraftResponses { get; } = new();
    public List<(int Slot, byte Generation, int Type)> NpcTypes { get; } = new();
    public List<(int Slot, byte Generation, int Type)> NpcStatueTypes { get; } = new();
    public List<(int Target, byte Generation, int Damage, float Knockback, byte Direction)> NpcStrikes { get; } = new();
    public List<(int Player, int Type, int Ticks)> AddedBuffs { get; } = new();
    public List<(int Player, int[] Types)> BuffLists { get; } = new();
    public List<(int Chest, int Size)> ChestSizes { get; } = new();
    public List<(short Chest, byte Slot, short Stack, byte Prefix, short Item)> ChestItems { get; } = new();
    public List<(int Player, float? CameraX, float? CameraY)> ControlUpdates { get; } = new();
    public List<(int Message, int Npc)> NpcAuthorityMessages { get; } = new();
    public List<(int Npc, string BodyHex)> NpcBuffUpdates { get; } = new();
    public List<(int Player, float X, float Y, byte Style)> TeleportUpdates { get; } = new();
    public (float X, float Y) InitialSpawnPosition => (_spawnX * 16f, _spawnY * 16f - 42);
    public List<(byte Packet, int Player, int Current, int Maximum)> VitalUpdates { get; } = new();
    // Credential-work scenarios keep native messages for assertions but omit their text from persisted evidence.
    public bool OmitChatMessageTextFromEvidence { get; set; }
    public IReadOnlyList<string> Messages => _messages;
    public int PacketCount(byte id) => _counts.GetValueOrDefault(id);
    public int SentPacketCount => _sent.Values.Sum();
    // One outstanding ping per client. Drain old responses before taking the send timestamp.
    public async Task<double> PingAsync(TimeSpan? timeout = null)
    {
        while (_received.Reader.TryRead(out var queued)) Observe(queued);
        int prior = PacketCount(154);
        long started = Stopwatch.GetTimestamp();
        await Send(154, _ => { });
        await WaitUntil(() => PacketCount(154) > prior, timeout ?? TimeSpan.FromSeconds(3));
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    public int ActiveChest { get; private set; } = -1;
    float _positionX, _positionY;
    bool _usingItem;
    public int ProofBatches { get; private set; }
    public int LoginAttempts { get; private set; }
    public string? ProofBatchHex { get; private set; }
    long _proofSentTimestamp, _disconnectObservedTimestamp;
    public LabClient(string name, string uuid) { Name = name; Uuid = uuid; }
    public void EnableVanillaPeerCapture()
    {
        if (_vanillaPeerCapture) throw new InvalidOperationException("Peer capture is already enabled.");
        lock (_peerCaptureSync) _peerFrames = new List<PeerFrame>(PeerFrameLimit);
        _vanillaPeerCapture = true;
    }
    public void StartPassiveObservation()
    {
        if (!_vanillaPeerCapture || !Authenticated || _passiveObservationStarted || Closed || _disposed)
            throw new InvalidOperationException("A single authenticated observer must own passive queue consumption.");
        _passiveObservationStarted = true;
        _passiveObservation = ConsumePassiveObservation();
    }
    async Task ConsumePassiveObservation()
    {
        try
        {
            await foreach (var packet in _received.Reader.ReadAllAsync(_stop.Token)) Observe(packet);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _passiveObservationFailure = ex.GetType().Name;
            _stop.Cancel(); _tcp.Dispose();
        }
    }
    void CapturePeerFrame(byte[] header, byte[] data, int wireBytes)
    {
        if (!_vanillaPeerCapture || data[0] is not (23 or 27 or 28 or 29 or 54 or 86 or 121 or 124)) return;
        lock (_peerCaptureSync)
        {
            long sequence = ++_peerEligibleFrames;
            if (wireBytes > PeerFrameBytesLimit) { _peerDroppedFrames++; _peerOversizeFrames++; return; }
            if (_peerFrames!.Count >= PeerFrameLimit || wireBytes > PeerTotalWireBytesLimit - _peerCapturedWireBytes)
            { _peerDroppedFrames++; return; }
            byte[] frame = new byte[wireBytes]; header.CopyTo(frame, 0); data.CopyTo(frame, 2);
            _peerFrames.Add(new(sequence, DateTimeOffset.UtcNow, _lifetime.ElapsedMilliseconds,
                "server-to-this-independent-peer", data[0], wireBytes, Convert.ToHexString(frame)));
            _peerCapturedWireBytes += wireBytes;
        }
    }
    public object VanillaPeerCaptureEvidence()
    {
        lock (_peerCaptureSync)
            return new
            {
                enabled = _vanillaPeerCapture, passiveConsumptionStarted = _passiveObservationStarted,
                passiveConsumptionFailure = _passiveObservationFailure, Closed, Disposed, Authenticated, DisconnectReason,
                allowedPacketIds = new[] { 23, 27, 28, 29, 54, 86, 121, 124 }, rawCredentialPacket82Captured = false,
                frameEncoding = "hex of exact received length-prefixed wire frame; two-byte little-endian length then packet id and payload",
                maximumFrames = PeerFrameLimit, maximumFrameWireBytes = PeerFrameBytesLimit, maximumTotalWireBytes = PeerTotalWireBytesLimit,
                lifetime = "this single observer connection; saved on stop-peer-observer, stop-lab or EOF",
                overflow = "drop-new eligible frames while normal receive consumption continues",
                eligibleFrames = _peerEligibleFrames, droppedFrames = _peerDroppedFrames, oversizeDroppedFrames = _peerOversizeFrames,
                capturedWireBytes = _peerCapturedWireBytes, frames = _peerFrames?.ToArray() ?? []
            };
    }
    public async Task Connect(int port, IPAddress? localBind = null)
    {
        DevRunLifecycle.Check();
        if (localBind is not null)
        {
            if (localBind.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(localBind))
                throw new ArgumentException("NetworkLab local binding requires an IPv4 loopback address.", nameof(localBind));
            var bindAddress = _tcp.Client.AddressFamily == AddressFamily.InterNetworkV6 ? localBind.MapToIPv6() : localBind;
            _tcp.Client.Bind(new IPEndPoint(bindAddress, 0));
        }
        await DevRunLifecycle.RunCancelable(token =>
            _tcp.ConnectAsync(IPAddress.Loopback, port, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var endpoint = (IPEndPoint?)_tcp.Client.LocalEndPoint;
        _localAddressRaw = endpoint?.Address.ToString();
        _localAddress = endpoint?.Address.IsIPv4MappedToIPv6 == true
            ? endpoint.Address.MapToIPv4().ToString() : _localAddressRaw;
        _localPort = endpoint?.Port;
        Phase("tcp-connected");
        _reader = ReadLoop();
    }
    public async Task Join(M10HandshakeProbe? probe = null, int? stopAtNativeState = null, Func<int, Task>? pauseAcceptedPhase = null)
    {
        try
        {
        if (stopAtNativeState.HasValue && stopAtNativeState is not (-1 or 0 or 1 or 2 or 3)) throw new ArgumentOutOfRangeException(nameof(stopAtNativeState));
        if (stopAtNativeState == 0) { Phase("i02-stopped-before-hello"); return; }
        if (probe is null) await Send(1, w => w.Write("Terraria326"));
        else await SendFragmentedHello(probe);
        Phase("hello-sent");
        if (stopAtNativeState == -1)
        { await WaitUntil(() => PacketCount(37) > 0, TimeSpan.FromSeconds(5)); Phase("i02-stopped-at-password"); return; }
        await WaitUntil(() => Slot != 255, TimeSpan.FromSeconds(5));
        Phase("slot-assigned");
        if (probe is not null) await probe.PauseForAcceptedPhase(this, 1, 1, "after-hello-before-character-upload");
        if (pauseAcceptedPhase is not null) await pauseAcceptedPhase(1);
        if (stopAtNativeState == 1) return;
        await Send(4, w =>
        {
            w.Write(Slot); w.Write((byte)0); w.Write((byte)0); w.Write(0f); w.Write((byte)0); w.Write(Name);
            w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0);
            for (int i = 0; i < 7; i++) { w.Write((byte)100); w.Write((byte)100); w.Write((byte)100); }
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        });
        await Send(68, w => w.Write(Uuid));
        await Send(16, w => { w.Write(Slot); w.Write((short)100); w.Write((short)100); });
        await Send(42, w => { w.Write(Slot); w.Write((short)20); w.Write((short)20); });
        await Send(50, w => { w.Write(Slot); w.Write((ushort)0); });
        await Send(147, w => { w.Write(Slot); w.Write((byte)0); w.Write((ushort)0); });
        int uploadedSlots = 0;
        foreach (var slot in Enumerable.Range(0, 99).Concat(Enumerable.Range(99, 40)).Concat(Enumerable.Range(299, 40))
            .Concat(new[] { 499 }).Concat(Enumerable.Range(500, 40)).Concat(Enumerable.Range(700, 40)).Concat(Enumerable.Range(900, 90)))
        {
            await Send(5, w => { w.Write(Slot); w.Write((short)slot); w.Write((short)0); w.Write((byte)0); w.Write((short)0); w.Write((byte)0); });
            uploadedSlots++;
            if (probe is not null && uploadedSlots is 100 or 250) await probe.PauseInventoryBatch(this, uploadedSlots);
        }
        await Send(6, _ => { });
        Phase("initial-character-uploaded");
        await WaitUntil(() => _spawnX > 0, TimeSpan.FromSeconds(8));
        if (pauseAcceptedPhase is not null) await pauseAcceptedPhase(2);
        if (stopAtNativeState == 2) return;
        await Send(8, w => { w.Write(-1); w.Write(-1); w.Write((byte)0); });
        await WaitPacket(49, TimeSpan.FromSeconds(15));
        Phase("world-section-received");
        if (probe is not null) await probe.PauseForAcceptedPhase(this, 3, 2, "after-world-request-before-spawn");
        if (pauseAcceptedPhase is not null) await pauseAcceptedPhase(3);
        if (stopAtNativeState == 3) return;
        await Send(12, w => { w.Write(Slot); w.Write((short)-1); w.Write((short)-1); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write((byte)0); w.Write((byte)1); });
        await Drain(TimeSpan.FromMilliseconds(500));
        if (DisconnectReason is not null || Closed) throw new IOException("Join disconnected: " + DisconnectReason);
        if (probe is not null) await probe.PauseForAcceptedPhase(this, 10, 3, "after-spawn-before-heartbeat");
        if (pauseAcceptedPhase is not null) await pauseAcceptedPhase(10);
        _heartbeat = KeepAlive();
        Phase("joined");
        }
        catch (Exception error) { probe?.Failed(error); throw; }
    }
    private async Task SendFragmentedHello(M10HandshakeProbe probe)
    {
        // Exact existing Hello frame, three finite writes, one logical packet and no retry.
        await DevRunLifecycle.RunCancelable(async token =>
        {
            await _writes.WaitAsync(token);
            try
            {
                byte[] packet = Packet(1, w => w.Write("Terraria326"));
                _sent[1] = _sent.GetValueOrDefault((byte)1) + 1;
                int offset = 0, part = 0;
                foreach (int count in new[] { 1, 2, packet.Length - 3 })
                {
                    await _tcp.GetStream().WriteAsync(packet.AsMemory(offset, count), token);
                    probe.Fragment(++part, count);
                    offset += count;
                    if (offset < packet.Length) await Task.Delay(80, token);
                }
                _transport.CompleteWrite(1, packet.Length);
            }
            catch (Exception error) { _transport.Failure(error, "write-fragmented-hello", token.IsCancellationRequested); throw; }
            finally { _writes.Release(); }
        }, _stop.Token);
    }
    private int _incompleteHelloFragmentCount;
    // Fixed isolated slow-input stimulus. No complete frame or game acceptance is claimed.
    public async Task<int> SendIncompleteHelloFragment()
    {
        if (_vanillaPeerCapture || Slot != 255 || _phase != "i02-stopped-before-hello" || _incompleteHelloFragmentCount >= 16)
            throw new InvalidOperationException("Incomplete-Hello writes require this owned pre-Hello client and a bounded fragment count.");
        int written = 0;
        await DevRunLifecycle.RunCancelable(async token =>
        {
            await _writes.WaitAsync(token);
            try
            {
                byte[] bytes = _incompleteHelloFragmentCount++ == 0 ? [(byte)100, (byte)0, (byte)1] : [(byte)84];
                await _tcp.GetStream().WriteAsync(bytes, token);
                written = bytes.Length;
            }
            catch (Exception error) { _transport.Failure(error, "write-incomplete-hello-fragment", token.IsCancellationRequested); throw; }
            finally { _writes.Release(); }
        }, _stop.Token);
        return written;
    }
    public async Task RegisterAndLogin()
    {
        const string password = "LocalLab784!";
        await Chat("/register " + password);
        await WaitUntil(() => _messages.Any(x => x.Contains("registered", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(8));
        await Login();
    }
    public async Task Login()
    {
        LoginAttempts++;
        await Chat("/login LocalLab784!");
        await WaitUntil(() => Authenticated, TimeSpan.FromSeconds(8));
        Phase("authenticated");
        await Drain(TimeSpan.FromMilliseconds(600));
    }
    public async Task LoginAndExpectRejected()
    {
        LoginAttempts++;
        await Chat("/login LocalLab784!");
        await WaitUntil(() => DisconnectReason is not null || Closed, TimeSpan.FromSeconds(8));
    }
    public Task Chat(string text) => Send(82, w => { w.Write((ushort)1); w.Write("Say"); w.Write(text); });
    public async Task Send(byte id, Action<BinaryWriter> write)
    {
        if (_vanillaPeerCapture && id is not (1 or 4 or 5 or 6 or 8 or 12 or 13 or 16 or 42 or 50 or 68 or 82 or 147))
            throw new InvalidOperationException("The vanilla peer observer only sends normal admission, login and idle controls.");
        await DevRunLifecycle.RunCancelable(async token =>
        {
            await _writes.WaitAsync(token);
            try
            {
                _sent[id] = _sent.GetValueOrDefault(id) + 1;
                var packet = Packet(id, write);
                await _tcp.GetStream().WriteAsync(packet, token);
                _transport.CompleteWrite(id, packet.Length);
            }
            catch (Exception ex) { _transport.Failure(ex, "write", token.IsCancellationRequested); throw; }
            finally { _writes.Release(); }
        }, _stop.Token);
    }
    public async Task SendBatch(params byte[][] packets)
    {
        if (_vanillaPeerCapture) throw new InvalidOperationException("The vanilla peer observer does not generate experimental or attack batches.");
        await DevRunLifecycle.RunCancelable(async token =>
        {
            await _writes.WaitAsync(token);
            try
            {
                ProofBatches++;
                var bytes = packets.SelectMany(x => x).ToArray();
                ProofBatchHex = Convert.ToHexString(bytes);
                foreach (var packet in packets) _sent[packet[2]] = _sent.GetValueOrDefault(packet[2]) + 1;
                _proofSentTimestamp = Stopwatch.GetTimestamp();
                await _tcp.GetStream().WriteAsync(bytes, token);
                foreach (var packet in packets) _transport.CompleteWrite(packet[2], packet.Length);
            }
            catch (Exception ex) { _transport.Failure(ex, "write-batch", token.IsCancellationRequested); throw; }
            finally { _writes.Release(); }
        }, _stop.Token);
    }
    public static byte[] Packet(byte id, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)0); writer.Write(id); write(writer); writer.Flush();
        var packet = stream.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(packet, checked((ushort)packet.Length)); return packet;
    }
    public static uint ProjectileKey(byte spawner, int index, int generation) => (uint)(spawner | ((index & 1023) << 8) | ((generation & 16383) << 18));
    public void WriteProjectile(BinaryWriter writer, uint key, short type = 1)
    {
        writer.Write(key); writer.Write(_positionX > 0 ? _positionX : _spawnX * 16f); writer.Write(_positionY > 0 ? _positionY : _spawnY * 16f - 42);
        writer.Write(1f); writer.Write(0f); writer.Write(type); writer.Write((byte)0);
    }
    public void WriteProjectileDamage(BinaryWriter writer, uint key, short type, short damage)
    {
        writer.Write(key); writer.Write(_positionX > 0 ? _positionX : _spawnX * 16f); writer.Write(_positionY > 0 ? _positionY : _spawnY * 16f - 42);
        writer.Write(1f); writer.Write(0f); writer.Write(type); writer.Write((byte)16); writer.Write(damage);
    }
    public static void WriteHurt(BinaryWriter writer, byte target, bool pvp, short damage)
    {
        writer.Write(target); writer.Write((byte)0); // Empty PlayerDeathReason has no optional fields.
        writer.Write(damage); writer.Write((byte)1); writer.Write(pvp ? (byte)2 : (byte)0); writer.Write((sbyte)-1);
    }
    public Task UseItem(bool value)
    {
        if (_vanillaPeerCapture) throw new InvalidOperationException("The vanilla peer observer does not use items.");
        _usingItem = value;
        return Send(13, WriteControls);
    }
    public Task MoveTo(float x, float y)
    {
        if (_vanillaPeerCapture) throw new InvalidOperationException("The vanilla peer observer remains at its normal admission location.");
        _positionX = x; _positionY = y;
        return Send(13, WriteControls);
    }
    public byte[] CameraControls() => Packet(13, w => WriteControls(w, true));
    private void WriteControls(BinaryWriter writer) => WriteControls(writer, false);
    private void WriteControls(BinaryWriter writer, bool camera)
    {
        writer.Write(Slot); writer.Write(_usingItem ? (byte)32 : (byte)0);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write(camera ? (byte)32 : (byte)0); writer.Write((byte)0);
        writer.Write(_positionX > 0 ? _positionX : _spawnX * 16f);
        writer.Write(_positionY > 0 ? _positionY : _spawnY * 16f - 42);
        if (camera) { writer.Write(2468f); writer.Write(1357f); }
    }
    public static void WriteInventory(BinaryWriter writer, byte owner, short stack, short item)
    {
        writer.Write(owner); writer.Write((short)10); writer.Write(stack); writer.Write((byte)0); writer.Write(item); writer.Write((byte)0);
    }
    async Task KeepAlive()
    {
        try
        {
            while (!_stop.IsCancellationRequested && !Closed)
            {
                await Task.Delay(1000, _stop.Token);
                if (Closed) return;
                if (PauseHeartbeat) continue;
                await Send(13, WriteControls);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    async Task ReadLoop()
    {
        try
        {
            var stream = _tcp.GetStream(); byte[] head = new byte[2];
            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(head, _stop.Token);
                int size = BinaryPrimitives.ReadUInt16LittleEndian(head);
                if (size < 3) throw new IOException("Invalid frame size.");
                byte[] data = new byte[size - 2]; await stream.ReadExactlyAsync(data, _stop.Token);
                _transport.CompleteRead(data[0], size);
                CapturePeerFrame(head, data, size);
                await _received.Writer.WriteAsync(data, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _transport.Failure(ex, "read", _stop.IsCancellationRequested);
            if (!_stop.IsCancellationRequested) _readFailure = ex.GetType().Name + ": " + ex.Message;
        }
        finally { Closed = true; _received.Writer.TryComplete(); }
    }
    public async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            DevRunLifecycle.Check();
            while (_received.Reader.TryRead(out var queued)) Observe(queued);
            if (predicate()) return;
            if (DisconnectReason is not null) throw new IOException(Name + " disconnected: " + DisconnectReason);
            if (Closed && !_received.Reader.TryPeek(out _)) throw new IOException($"{Name} socket closed during {_phase}; localPort={_localPort}; transport={_readFailure}");
            if (watch.Elapsed > timeout) throw new TimeoutException(Name + " condition timed out; messages: " + string.Join(" | ", _messages.TakeLast(5)));
            if (await Next(TimeSpan.FromMilliseconds(200)) is { } packet) Observe(packet);
        }
    }
    async Task WaitPacket(byte id, TimeSpan timeout)
    {
        int prior = _counts.GetValueOrDefault(id);
        await WaitUntil(() => _counts.GetValueOrDefault(id) > prior, timeout);
    }
    public async Task Drain(TimeSpan duration)
    {
        DevRunLifecycle.Check();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            DevRunLifecycle.Check();
            if (Closed && !_received.Reader.TryPeek(out _)) return;
            if (await Next(TimeSpan.FromMilliseconds(100)) is { } packet) Observe(packet);
        }
    }
    async Task<byte[]?> Next(TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        try { return await _received.Reader.ReadAsync(limit.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { return null; }
    }
    void Observe(byte[] packet)
    {
        byte id = packet[0]; _counts[id] = _counts.GetValueOrDefault(id) + 1;
        // Decode only the explicitly selected native LeashedEntity module's FullSync header.
        // Other packet82 modules, including chat/login credentials, are never retained here.
        if (id == 82 && LeashedModuleId is { } leashedModule && packet.Length >= 4 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1, 2)) == leashedModule)
        {
            if (packet[3] != 1) return;
            using var leashedReader = new BinaryReader(new MemoryStream(packet, 4, packet.Length - 4, false));
            int slot = leashedReader.Read7BitEncodedInt(), type = leashedReader.Read7BitEncodedInt();
            int x = leashedReader.ReadInt16(), y = leashedReader.ReadInt16();
            if (LeashedFullSyncs.Count < 1024) LeashedFullSyncs.Add((slot, type, x, y));
            else if (LeashedFullSyncsDropped < int.MaxValue) LeashedFullSyncsDropped++;
            return;
        }
        if (id == 82 && LiquidModuleId is { } liquidModule && packet.Length >= 5 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1, 2)) == liquidModule)
        {
            int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3, 2));
            if (packet.Length != 5 + count * 6) throw new InvalidDataException("Native NetLiquidModule length differs from locked format.");
            for (int i = 0; i < count; i++)
            {
                int p = 5 + i * 6, packed = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(p, 4));
                if (LiquidUpdates.Count < 4096) LiquidUpdates.Add(((packed >> 16) & 65535, packed & 65535, packet[p + 4], packet[p + 5]));
                else if (LiquidUpdatesDropped < int.MaxValue) LiquidUpdatesDropped++;
            }
            return;
        }
        using var reader = new BinaryReader(new MemoryStream(packet, 1, packet.Length - 1, false));
        if (id == 2)
        {
            _transport.Close("server-protocol-disconnect-observed");
            DisconnectReason = ReadText(reader);
            _disconnectObservedTimestamp = Stopwatch.GetTimestamp();
        }
        if (id == 3) Slot = reader.ReadByte();
        if (id == 147 && packet.Length == 5 && LoadoutUpdates.Count < 10000)
            LoadoutUpdates.Add((reader.ReadByte(), reader.ReadByte(), reader.ReadUInt16()));
        if (id is 16 or 42 && packet.Length == 6 && VitalUpdates.Count < 4000)
            VitalUpdates.Add((id, reader.ReadByte(), reader.ReadInt16(), reader.ReadInt16()));
        if (id == 7)
        {
            LastWorldInfo = (byte[])packet.Clone(); // One bounded current world frame; never an unbounded history.
            reader.ReadInt32(); reader.ReadByte(); reader.ReadByte(); reader.ReadInt16(); reader.ReadInt16();
            _spawnX = reader.ReadInt16(); _spawnY = reader.ReadInt16();
        }
        if (id == 18 && packet.Length == 10)
            LastTimeInfo = (byte[])packet.Clone(); // Native time18 has day/time/sun/moon only; eclipse is in world7.
        if (id == 5 && packet.Length >= 10)
        {
            int player = reader.ReadByte(); short slot = reader.ReadInt16(); short stack = reader.ReadInt16();
            byte prefix = reader.ReadByte(); int item = reader.ReadInt16();
            byte flags = reader.ReadByte();
            if (InventoryUpdates.Count < 10000)
            { InventoryUpdates.Add((player, slot, item, stack, prefix)); InventorySlotFlags[(player, slot)] = flags; }
            if (player == Slot) SscSlots.Add(slot);
        }
        if (id == 13 && packet.Length >= 15)
        {
            int player = reader.ReadByte();
            reader.ReadByte(); byte flags2 = reader.ReadByte(), flags3 = reader.ReadByte(), flags4 = reader.ReadByte(); reader.ReadByte();
            float x = reader.ReadSingle(), y = reader.ReadSingle();
            if (player == Slot) { _positionX = x; _positionY = y; }
            if ((flags2 & 4) != 0) reader.ReadBytes(8);
            if ((flags2 & 128) != 0) reader.ReadUInt16();
            if ((flags3 & 64) != 0) reader.ReadBytes(16);
            float? cameraX = null, cameraY = null;
            if ((flags4 & 32) != 0) { cameraX = reader.ReadSingle(); cameraY = reader.ReadSingle(); }
            if (ControlUpdates.Count < 10000) ControlUpdates.Add((player, cameraX, cameraY));
        }
        if (id == 27 && packet.Length >= 24)
        {
            uint key = reader.ReadUInt32(); reader.ReadBytes(16); int type = reader.ReadInt16();
            if (Projectiles.Count < 10000) Projectiles.Add((key, type));
            byte flags = reader.ReadByte(); if ((flags & 4) != 0) reader.ReadByte();
            if ((flags & 1) != 0) reader.ReadSingle(); if ((flags & 2) != 0) reader.ReadSingle();
            if ((flags & 8) != 0) reader.ReadUInt16();
            int damage = (flags & 16) != 0 ? reader.ReadInt16() : 0;
            if (ProjectileClaims.Count < 10000) ProjectileClaims.Add((key, type, damage));
        }
        if (id == 29 && packet.Length == 13 && ProjectileRemovals.Count < 10000)
            ProjectileRemovals.Add((reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle()));
        if (id == 117 && packet.Length == 8 && packet[2] == 0 && HurtDeclarations.Count < 2000)
        {
            int target = reader.ReadByte(); reader.ReadByte(); int damage = reader.ReadInt16();
            reader.ReadByte(); bool pvp = (reader.ReadByte() & 2) != 0;
            HurtDeclarations.Add((target, damage, pvp));
        }
        if (id == 55 && packet.Length >= 8)
        {
            int player = reader.ReadByte(), type = reader.ReadUInt16(), ticks = reader.ReadInt32();
            if (AddedBuffs.Count < 2000) AddedBuffs.Add((player, type, ticks));
        }
        if (id == 50 && packet.Length >= 4)
        {
            int player = reader.ReadByte(); var types = new List<int>(44);
            while (reader.BaseStream.Position + 2 <= reader.BaseStream.Length)
            {
                int type = reader.ReadUInt16();
                if (type == 0) break;
                if (types.Count == 44) throw new IOException("Server buff list exceeds target capacity.");
                types.Add(type);
            }
            if (BuffLists.Count < 2000) BuffLists.Add((player, types.ToArray()));
        }
        if (id == 155 && packet.Length == 5 && ChestSizes.Count < 2000) ChestSizes.Add((reader.ReadInt16(), reader.ReadInt16()));
        if (id == 32 && packet.Length == 9 && ChestItems.Count < 10000)
            ChestItems.Add((reader.ReadInt16(), reader.ReadByte(), reader.ReadInt16(), reader.ReadByte(), reader.ReadInt16()));
        if (id == 33 && packet.Length >= 3) ActiveChest = reader.ReadInt16();
        if (id == 153 && packet.Length == 4 && NpcAuthorityMessages.Count < 2000) NpcAuthorityMessages.Add((id, reader.ReadByte()));
        if (id == 100 && packet.Length == 21 && NpcAuthorityMessages.Count < 2000) NpcAuthorityMessages.Add((id, reader.ReadUInt16()));
        if (id == 54 && packet.Length >= 5 && NpcBuffUpdates.Count < 2000)
            NpcBuffUpdates.Add((reader.ReadInt16(), Convert.ToHexString(packet.AsSpan(1))));
        if (id == 65 && packet.Length >= 13 && TeleportUpdates.Count < 2000)
        {
            reader.ReadByte();
            TeleportUpdates.Add((reader.ReadInt16(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadByte()));
        }
        if (id == 23 && packet.Length >= 25 && NpcTypes.Count < 10000)
        {
            int npc = reader.ReadByte(); byte generation = reader.ReadByte(); reader.ReadBytes(18);
            byte flags = reader.ReadByte(), flags2 = reader.ReadByte();
            for (int ai = 0; ai < 4; ai++) if ((flags & (4 << ai)) != 0) reader.ReadSingle();
            int type = reader.ReadInt16();
            NpcTypes.Add((npc, generation, type));
            if ((flags2 & 2) != 0 && NpcStatueTypes.Count < 10000) NpcStatueTypes.Add((npc, generation, type));
        }
        if (id == 28 && packet.Length == 11 && NpcStrikes.Count < 2000)
            NpcStrikes.Add((reader.ReadByte(), reader.ReadByte(), reader.ReadInt16(), reader.ReadSingle(), reader.ReadByte()));
        if (id == 82 && packet.Length == 4 && CraftResponses.Count < 2000)
            CraftResponses.Add((reader.ReadUInt16(), reader.ReadBoolean()));
        if (id == 82 && packet.Length > 4 && reader.ReadUInt16() == 1)
        {
            reader.ReadByte(); string text = ReadText(reader).Replace("LocalLab784!", "[REDACTED]", StringComparison.Ordinal);
            if (_messages.Count < 2000) _messages.Add(text);
            if (text.Contains("Authenticated as", StringComparison.OrdinalIgnoreCase) || text.Contains("authenticated successfully", StringComparison.OrdinalIgnoreCase)) Authenticated = true;
        }
        if (id == 91 && packet.Length >= 10)
        {
            reader.ReadInt32(); int anchor = reader.ReadByte();
            if (anchor != 255)
            {
                int player = reader.ReadUInt16(); reader.ReadUInt16(); int emote = reader.ReadByte();
                if (anchor == 1 && Bubbles.Count < 2000) Bubbles.Add((player, emote));
            }
        }
    }
    static string ReadText(BinaryReader reader)
    {
        byte mode = reader.ReadByte(); string text = reader.ReadString();
        if (mode != 0)
        {
            int count = reader.ReadByte(); var substitutions = new List<string>();
            for (int i = 0; i < count; i++) substitutions.Add(ReadText(reader));
            if (substitutions.Count > 0) text += " [" + string.Join(", ", substitutions) + "]";
        }
        return text;
    }
    public object Evidence() => new { Name, Slot, Authenticated, sscDistinctSlots = SscSlots.Count,
        PreConnectPacingWaitMilliseconds,
        transport = _transport.Snapshot(),
        protocolAddressSpaceCount = 990, actualNativeInitialSlotCount = 350,
        Closed, DisconnectReason, phase = _phase, localAddress = _localAddress, localAddressRaw = _localAddressRaw, localPort = _localPort, readFailure = _readFailure, lifecycle = _lifecycle,
        proofBatches = ProofBatches, proofBatchesMeaning = "legacy name for SendBatch calls, not number of proven events",
        LoginAttempts, received = _counts.OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString(), x => x.Value),
        sent = _sent.OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString(), x => x.Value), ProofBatchHex,
        proofToDisconnectObservedMs = _proofSentTimestamp > 0 && _disconnectObservedTimestamp > 0 ? (double?)(_disconnectObservedTimestamp - _proofSentTimestamp) * 1000 / Stopwatch.Frequency : null,
        messages = OmitChatMessageTextFromEvidence ? [] : _messages.ToArray(), messageCount = _messages.Count, messageTextOmitted = OmitChatMessageTextFromEvidence, bubbles = Bubbles.Select(x => new { player = x.Player, emote = x.Emote }),
        loadouts = LoadoutUpdates.Select(x => new { player = x.Player, loadout = x.Loadout, visibility = x.Visibility }),
        projectiles = Projectiles.Select(x => new { key = x.Key, type = x.Type }), addedBuffs = AddedBuffs.Select(x => new { player = x.Player, type = x.Type, ticks = x.Ticks }),
        projectileRemovals = ProjectileRemovals.Select(x => new { key = x.Key,
            x = x.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            y = x.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) }),
        buffLists = BuffLists.Select(x => new { player = x.Player, types = x.Types }), chestSizes = ChestSizes.Select(x => new { chest = x.Chest, size = x.Size }),
        cameraControls = ControlUpdates.Where(x => x.CameraX is not null).Select(x => new { player = x.Player, cameraX = x.CameraX, cameraY = x.CameraY }),
        vitals = VitalUpdates.Select(x => new { packet = x.Packet, player = x.Player, current = x.Current, maximum = x.Maximum }) };
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _transport.Close("local-dispose");
        _stop.Cancel(); _tcp.Dispose(); await Task.WhenAll(_reader, _heartbeat, _passiveObservation);
        _stop.Dispose(); _writes.Dispose();
        DisposalCompletedTimestamp = Stopwatch.GetTimestamp();
    }
}
