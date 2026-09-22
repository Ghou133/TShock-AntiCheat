using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// A small observation plan for the existing LabClient.Join path. It neither replaces
// the protocol implementation nor alters the original twelve reconnection assertions.
internal sealed class M10HandshakeProbe(string label, bool firstConnection, Func<string[]> consoleLines,
    Action<bool, string> assert)
{
    private const string Marker = "ANTICHEAT_M10_PHASE_SCAN ";
    private readonly int consoleStart = consoleLines().Length;
    private readonly List<object> observations = new(12);
    private readonly List<object> fragments = new(3);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private string? session;
    private string? failure;
    private bool complete;
    private int phaseWitnesses, inventoryPauses;
    public string Label => label;
    public bool FirstConnection => firstConnection;

    public void Fragment(int index, int bytes) => fragments.Add(new { index, bytes, elapsedMs = elapsed.ElapsedMilliseconds });

    public async Task PauseForAcceptedPhase(LabClient client, int expectedNativeState, int expectedPhase, string phase)
    {
        int sentBefore = client.SentPacketCount;
        var started = DateTimeOffset.UtcNow;
        // The receiver keeps draining. No outgoing packet, resend, heartbeat, or timing
        // configuration change is used to prompt the server's accepted-phase observation.
        await client.Drain(TimeSpan.FromMilliseconds(900));
        int sentAfter = client.SentPacketCount;
        var matching = new List<JsonObject>(3);
        foreach (string line in consoleLines().Skip(consoleStart))
        {
            int marker = line.IndexOf(Marker, StringComparison.Ordinal);
            if (marker < 0) continue;
            var node = JsonNode.Parse(line[(marker + Marker.Length)..])!.AsObject();
            if (node["Session"]?["Slot"]?.GetValue<int>() == client.Slot &&
                node["After"]?["Phase"]?.GetValue<int>() == expectedPhase) matching.Add(node);
            if (matching.Count > 3) break;
        }
        Check(sentBefore == sentAfter, phase + ":no-next-inbound-packet-during-observation");
        Check(!client.Closed && client.DisconnectReason is null, phase + ":legal-paused-client-remains-connected");
        Check(matching.Count == 1, phase + ":exactly-one-actual-phase-advance-witness");
        JsonObject witness = matching.Single();
        string currentSession = witness["Session"]!.ToJsonString();
        Check(session is null || session == currentSession, phase + ":same-server-session-generation");
        session ??= currentSession;
        Check(witness["NativeState"]?.GetValue<int>() == expectedNativeState &&
            witness["Status"]?.GetValue<string>() == "Accepted", phase + ":actual-native-accepted-state");
        Check(witness["Before"]!["Phase"]!.GetValue<int>() < expectedPhase &&
            witness["After"]!["PhaseEnteredAt"]!.GetValue<long>() >= witness["Before"]!["PhaseEnteredAt"]!.GetValue<long>(),
            phase + ":strict-forward-transition-observed-clock");
        Check(witness["Before"]!["LastPacketAt"]!.GetValue<long>() == witness["After"]!["LastPacketAt"]!.GetValue<long>() &&
            witness["Before"]!["LastPlayerUpdateAt"]!.GetValue<long>() == witness["After"]!["LastPlayerUpdateAt"]!.GetValue<long>(),
            phase + ":sampling-does-not-invent-packet-or-heartbeat");
        if (expectedPhase is 1 or 2)
            Check(witness["MaintenancePath"]?.GetValue<string>() == (firstConnection ? "NativeIdle" : "GameUpdate"),
                phase + ":expected-idle-or-active-maintenance-path");
        observations.Add(new { phase, startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
            sentBefore, sentAfter, slot = client.Slot, witness });
        phaseWitnesses++;
    }

    public async Task PauseInventoryBatch(LabClient client, int uploadedSlots)
    {
        int sentBefore = client.SentPacketCount;
        await client.Drain(TimeSpan.FromMilliseconds(150));
        Check(!client.Closed && client.DisconnectReason is null, "inventory-batch-" + uploadedSlots + ":legal-client-connected");
        Check(client.SentPacketCount == sentBefore, "inventory-batch-" + uploadedSlots + ":receiver-only-pause");
        observations.Add(new { phase = "inventory-upload-batch", uploadedSlots, sentBefore,
            sentAfter = client.SentPacketCount, elapsedMs = elapsed.ElapsedMilliseconds });
        inventoryPauses++;
    }

    public void Complete(LabClient client, long actualBanRows)
    {
        Check(phaseWitnesses == 3 && inventoryPauses == 2 && fragments.Count == 3,
            "all-three-phases-and-finite-fragment-plan-executed");
        Check(client.Authenticated && client.SscSlots.Count >= 350 && !client.Closed,
            "normal-authenticated-ssc-receipt-after-segmented-join");
        Check(actualBanRows == 0, "zero-account-bans");
        complete = true;
        observations.Add(new { phase = "completed", slot = client.Slot, authenticated = client.Authenticated,
            sscReceivedSlots = client.SscSlots.Count, actualBanRows,
            sscMeaning = "synthetic recipient observed slot packets; not atomic stock-client SSC completion" });
    }

    public void Failed(Exception error) => failure ??= error.GetType().Name + ": " + error.Message;
    private void Check(bool condition, string name) => assert(condition, "m10-a03:" + label + ":" + name);

    public static async Task WriteReport(string reportDirectory, string parentStatus, IReadOnlyList<M10HandshakeProbe> probes)
    {
        string directory = Path.Combine(reportDirectory, "m10-a03");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, status = probes.Count == 2 && probes.All(p => p.complete) ? "passed" : "failed",
            parentRunStatus = parentStatus, actualLoopbackTcp = true, actualClientUiThisRun = false,
            evidenceLayer = "real_server_synthetic_tcp", requiredProbeCount = 2,
            qualificationChanged = false, originalTwelveReconnectAssertionsRetained = true,
            defaultConnectionPacingChanged = false, automaticInputRetries = 0,
            timeoutScope = "short real paused input plus separate controlled-clock tests; native 7200-update timeout remains enabled",
            productInputs = "../inputs.json", parentSummary = "../summary.json",
            probes = probes.Select(p => new { label = p.Label, firstConnection = p.FirstConnection, status = p.complete ? "passed" : "failed",
                p.failure, p.session, p.phaseWitnesses, p.inventoryPauses,
                maximumHelloFragments = 3, helloFragments = p.fragments, observations = p.observations,
                elapsedMs = p.elapsed.ElapsedMilliseconds, rawPacketPayloadsRecorded = false })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
