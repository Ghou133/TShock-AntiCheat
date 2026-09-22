namespace AntiCheat.DevMcp;

// This is the only registered catalog. CLI and MCP both enumerate and execute this table.
internal static class ScenarioCatalog
{
    public static readonly Scenario[] All =
    [
        new(ScenarioId.CoreFirstProof, ScenarioCategory.Core,
            "Existing first-proven-event Core test; no game runtime or GUI.", "core_unit",
            "scripts/Test-Core.ps1", ["-CoreOnly", "-Filter", "FullyQualifiedName~AntiCheat.Core.Tests.FirstSanctionTests"],
            ["core", "first-proof", "L04"], 180),
        new(ScenarioId.CoreSessionIsolation, ScenarioCategory.Core,
            "Existing SessionTests and ProofBoundaryTests: account/session generation and proof prerequisites.", "core_unit",
            "scripts/Test-Core.ps1", ["-CoreOnly", "-Filter", "SessionTests|ProofBoundaryTests"],
            ["core", "session", "B03", "L04"], 180),
        new(ScenarioId.CoreEmptySelectionControl, ScenarioCategory.Control,
            "Negative development control: the existing runner selects no matching tests and must not pass. Not gameplay coverage.", "runner_negative_control",
            "scripts/Test-Core.ps1", ["-CoreOnly", "-Filter", "FullyQualifiedName=DevMcp.Intentional.NoMatchingTest"],
            ["control", "failure", "L04"], 180),
        Tcp(ScenarioId.TcpApplication, "Application", "Existing command/chat admission scenario.", ["application", "I04", "A05"]),
        Tcp(ScenarioId.TcpCraft, "Craft", "Existing final crafting consumption safety scenario.", ["craft", "C06"]),
        Tcp(ScenarioId.TcpWorld, "World", "Existing paint recovery and liquid calibration scenario.", ["world", "H05", "H06"]),
        Tcp(ScenarioId.TcpTeleport, "Teleport", "Existing player teleport parameter scenario, including the M10 minimal rod-permission legal packet65 control.", ["teleport", "G05"]),
        Tcp(ScenarioId.TcpSentry, "Sentry", "M11 ordinary FrostHydra308 creation/retirement resource safety and legal replacement.", ["sentry", "F03"]),
        Tcp(ScenarioId.TcpSummon, "Summon", "Existing M10 Slime266 conservative-capacity and replacement regression.", ["summon", "F03"]),
        Tcp(ScenarioId.TcpQuickStack, "QuickStack", "Existing packet85 source safety and normal recovery regression.", ["quickstack", "C05", "C06"]),
        Tcp(ScenarioId.TcpSolarTablet, "SolarTablet", "M11 exact Solar Tablet active world-condition scenario; qualification remains a separate local decision.", ["natural", "E01", "E03"]),
        Tcp(ScenarioId.TcpClassEmblems, "ClassEmblems", "M12 class-emblem shared native damage-bonus protection; server effects only, no account sanction.", ["equipment", "C04"]),
        Tcp(ScenarioId.TcpReceiveIsolation, "ReceiveIsolation", "M12 owned old-read completion across actual admission and authenticated account reuse, with new-account first-proof enforcement.", ["receive", "A03", "B03"]),
        Tcp(ScenarioId.TcpLiquidControlOn, "LiquidControlOn", "Bounded checkerboard liquid investigation with the guard enabled; measurement completion is not conservation acceptance.", ["liquid", "H05", "L05"]),
        Tcp(ScenarioId.TcpLiquidControlOff, "LiquidControlOff", "Bounded checkerboard liquid investigation with the guard disabled; measurement completion is not conservation acceptance.", ["liquid", "H05", "L05"]),
        new(ScenarioId.TcpRegression, ScenarioCategory.Tcp,
            "Existing common TestLab first-proof, persistence, connection and unaffected-client regression, zero extra pacing by default.",
            "real_server_synthetic_tcp", "scripts/Test-NetworkLab.ps1",
            ["-HelloDiagnostics", "-NoBuild"], ["regression", "B03", "L04"], 300),
        new(ScenarioId.TcpProduction, ScenarioCategory.Tcp,
            "Existing isolated Production-identity exact-rule qualification regression; never deploys a production server.",
            "real_server_synthetic_tcp", "scripts/Test-NetworkLab.ps1",
            ["-ProductionIdentity", "-NoBuild"], ["production-qualification", "L04"], 300),
        new(ScenarioId.TcpHandshake, ScenarioCategory.Tcp,
            "Existing HandshakeCases with M10 finite legal fragmentation/phase observations and the original twelve rapid reconnect checks.",
            "real_server_synthetic_tcp", "scripts/Test-NetworkLab.ps1",
            ["-HandshakeCases", "-HelloDiagnostics", "-NoBuild"], ["handshake", "A03", "L04"], 300),
        Client(ScenarioId.ClientLoadoutCycle, "Cycle the existing isolated client's loadout with original keyboard input and verify each observed server transition.", ["client", "loadout", "C04"]),
        Client(ScenarioId.ClientQuickStack, "Use original quick-stack input once; verify bounded inventory/chest deltas without retrying an uncertain transfer.", ["client", "quickstack", "C05"]),
        Client(ScenarioId.ClientHotbarCycle, "Cycle the isolated client's hotbar with original keyboard input and verify server selected-item transitions.", ["client", "hotbar", "C04"])
    ];

    private static Scenario Client(ScenarioId id, string description, string[] tags) =>
        new(id, ScenarioCategory.Client, description, "client_original_input_server_observation",
            "scripts/Invoke-ClientScenario.ps1", ["-ScenarioId", id.ToString()], tags, 30);

    private static Scenario Tcp(ScenarioId id, string slice, string description, string[] tags) =>
        new(id, ScenarioCategory.Tcp, description, "real_server_synthetic_tcp",
            "scripts/Test-NetworkLab.ps1", ["-ProtectionCases", "-ProtectionSlice", slice, "-NoBuild"],
            tags, 300);

    public static Scenario Get(ScenarioId id) => All.FirstOrDefault(s => s.Id == id)
        ?? throw new DevProblem("unknown_scenario", "Only scenario_list entries can be started.");

    // Derive the runner selection from the existing registration, never from a result's exit code.
    internal static string NetworkSelection(Scenario scenario)
    {
        if (!scenario.IsTcp) throw new ArgumentException("A network scenario is required.", nameof(scenario));
        int slice = Array.IndexOf(scenario.FixedArguments, "-ProtectionSlice");
        if (slice >= 0 && slice + 1 < scenario.FixedArguments.Length)
            return "protection-cases:" + scenario.FixedArguments[slice + 1].ToLowerInvariant();
        if (scenario.FixedArguments.Contains("-ProductionIdentity")) return "production-identity";
        if (scenario.FixedArguments.Contains("-HandshakeCases")) return "handshake-testlab";
        if (scenario.Id == ScenarioId.TcpRegression) return "regression";
        throw new DevProblem("invalid_scenario_registration", "Network registration has no verified runner selection.");
    }
}
