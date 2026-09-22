using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AntiCheat.Core;
using AntiCheat.Persistence;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;
using CompatibilityAudit;

namespace AntiCheat.Plugin.TShock;

[ApiVersion(2, 1)]
public sealed class AntiCheatPlugin : TerrariaPlugin
{
    public const string ReadyMarker = "ANTICHEAT_M1_INITIALIZED";
    private sealed record Binding(SessionKey Key, TSPlayer Player)
    {
        private int _disconnectRequested;
        private int _revokedPacketReported;
        private int _buffPassReported;
        private readonly HashSet<string> _reportedBusiness = [];
        public bool TryReportBusiness(BusinessRuleResult result)
        {
            lock (_reportedBusiness)
                return _reportedBusiness.Count < 96 && _reportedBusiness.Add(result.RuleId + "/" + result.Verdict + "/" + result.Reason);
        }
        public void ResetReportedBusiness() { lock (_reportedBusiness) _reportedBusiness.Clear(); }
        public bool TryReportRevokedPacket() => Interlocked.Exchange(ref _revokedPacketReported, 1) == 0;
        public bool TryReportBuffPass() => Interlocked.Exchange(ref _buffPassReported, 1) == 0;
        public bool NetworkRegistered { get; set; }
        public M10ConnectionPhaseAdapter? NetworkTransport { get; set; }
        public M16TimeoutTransportRetirement? TimeoutRetirement { get; set; }
        private int _timeoutTerminationRequested;
        public bool TimeoutTerminationRequested => Volatile.Read(ref _timeoutTerminationRequested) != 0;
        public void MarkTimeoutTerminationRequested() => Interlocked.Exchange(ref _timeoutTerminationRequested, 1);
        public string RateKey { get; } = $"{Key.ServerRunId:N}/{Key.WorldEpoch}/{Key.Slot}/{Key.Generation}";
        public void DisconnectOnce(string reason)
        {
            if (TimeoutTerminationRequested) return;
            if (Interlocked.Exchange(ref _disconnectRequested, 1) == 0) Player.Disconnect(reason);
        }
    }
    private readonly object _bindingsLock = new();
    private readonly Binding?[] _bindings = new Binding?[256];
    private readonly ServerThreadDispatcher _dispatcher = new(32);
    private readonly NetworkControls _network = new(TimeProvider.System);
    private readonly M16TimeoutRetirementQueue _timeoutRetirements = new(TimeProvider.System);
    private readonly M17PreHelloDeadlineGuard _preHelloDeadlines = new(TimeProvider.System);
    private readonly ApplicationRequestBudget _applicationRequests = new(TimeProvider.System);
    private M16RequestEgressGuard? _requestEgress;
    private M17CommandWorkGuard? _commandWork;
    private M17RestWorkGuard? _restWork;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposeStarted;
    // Generous lab defaults, bounded per-connection byte accounting; these are resource controls, never proof.
    private readonly BoundedRateLimiter _bytes = new(TimeProvider.System,
        new RateLimitOptions(256, 1024 * 1024, 256 * 1024, TimeSpan.FromMinutes(15)));
    private FileEnforcementJournal? _journal;
    private AntiCheatEngine? _engine;
    private Task? _operation;
    private RuntimeStatus _runtime = new(false, "unknown", "not-initialized");
    private TargetRuntimeStatus _targetRuntime = new(false, "unknown", "unknown", "not-initialized");
    private ExecutionScope _scope;
    private int _productionHardRuleCount;
    private M2BusinessAdapter? _business;
    private M3InventoryContexts? _inventory;
    private M3CombatContexts? _combat;
    private M10SummonBudgetGuard? _summonBudget;
    private M4CombatContexts? _combatMechanism;
    private M5CombatContexts? _arrowLifecycle;
    private M6ArrowCandidateContexts? _arrowCandidates;
    private M18ArrowSourceResetIntervals? _arrowResetInputs;
    private M18ResetInputDiagnostics? _arrowResetDiagnostics;
    private M7NpcStrikeCauseContexts? _npcStrikeCauses;
    private M16CombatNpcImmunityGuard? _npcImmunity;
    private M7ConnectionDiagnostics? _labConnections;
    private M10ConnectionLifecycleDiagnostics? _labLifecycle;
    private M16CombatLabDiagnostics? _combatLabDiagnostics;
    private long _nextLabLifecycleFlush;
    private M4VitalContexts? _vitals;
    private M3ProgressionPolicy? _progressionPolicy;
    private M5ProgressionContexts? _naturalProgression;
    private M6WiringExecutionGuard? _wiringExecution;
    private M8LiquidExecutionGuard? _liquidExecution;
    private M9PaintRecovery? _paintRecovery;
    private M8ShimmerItemTransactions? _shimmerItems;
    private M8MovementObservations? _movementObservations;
    private readonly HashSet<string> _reportedContextFaults = [];
    private bool _tablesReported;
    private bool _infrastructureFailed;
    private int _maintenanceCallbackFailed;
    private int _phaseObservationFaultReported;
    private bool _recoverNext;
    private long _nextAttemptTimestamp;
    private long _parsedCandidates;
    private long _unknownCandidates;
    private long _blockedMalformed;
    private long _maxDispatchTicks;

    public AntiCheatPlugin(Main game) : base(game)
    {
        Order = 100;
        _preHelloDeadlines.IntegrityFault = error => ReportContextFault("PreHelloDeadline", error);
        _timeoutRetirements.RetirementFailed = retirement => ReportContextFault("TimeoutTransportRetirement",
            new IOException("Owned timeout retirement exhausted its bounded attempts: " + retirement.LastFailure));
    }
    public override string Name => "AntiCheat.M2";
    public override string Author => "AntiCheat contributors";
    public override string Description => "Version-locked business rules with isolated lab enforcement and bounded recovery.";
    public override Version Version => new(0, 2, 0);
    public Task<bool> ShutdownCompletion { get; private set; } = Task.FromResult(false);

    public override void Initialize()
    {
        _runtime = BaselineRuntime.Inspect();
        _targetRuntime = TargetRuntime.Inspect();
        var (configuration, configurationReason) = M2Configuration.Load(ServerTShock.SavePath, Netplay.ServerIP);
        _scope = _targetRuntime.Verified ? configuration.ExecutionScope : ExecutionScope.ObserveOnly;
        if (_targetRuntime.Verified)
        {
            try { _business = new(_targetRuntime.Fingerprint, Path.Combine(AppContext.BaseDirectory, "data", "progression")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                ServerApi.LogWriter.PluginWriteLine(this, "AntiCheat progression catalog unavailable: " + ex.GetType().Name, TraceLevel.Warning);
                _business = new(_targetRuntime.Fingerprint, Path.Combine(ServerTShock.SavePath, "absent-candidate-data"));
            }
        }
        StartRecovery();
        if (_targetRuntime.Verified && _business is not null)
        {
            _business.IntegrityFault = OnBusinessIntegrityFault;
            _inventory = new M3InventoryContexts(_targetRuntime.Fingerprint, slot =>
            {
                var binding = GetBinding(slot);
                return (binding?.Key, binding?.Player, binding is not null && !Maintenance && _engine?.CanWrite(binding.Key) == true);
            }, (session, player, result) =>
            {
                var binding = GetBinding(session.Slot);
                return binding is null || binding.Key != session || !ReferenceEquals(binding.Player, player)
                    || ApplyBusinessResult(0, false, binding, result);
            });
            _business.InventoryContexts = _inventory;
            _inventory.IntegrityFault = DisableInventory;
            _inventory.FinalCraftIntegrityFault = error => ReportContextFault("FinalCraftGuard", error);
            if (_scope == ExecutionScope.TestLab && configurationReason == "isolated-loopback-testlab")
                _inventory.EquipmentExecutions.EffectObservation = evidence =>
                    ServerApi.LogWriter.PluginWriteLine(this, "ANTICHEAT_M11_EQUIPMENT_EFFECT " +
                        System.Text.Json.JsonSerializer.Serialize(evidence), TraceLevel.Info);
            RunInventory(context => context.Install());
            _combat = new M3CombatContexts(_targetRuntime.Fingerprint) { IntegrityFault = DisableCombat };
            RunCombat(context => context.Install());
        }
        if (_targetRuntime.Verified)
        {
            _summonBudget = new(_targetRuntime.Fingerprint, slot =>
            {
                var binding = GetBinding(slot);
                return (binding?.Key, binding?.Player, binding is not null && !Maintenance && _engine?.CanWrite(binding.Key) == true);
            }, allowIsolatedScaffold: _scope == ExecutionScope.TestLab &&
                configurationReason == "isolated-loopback-testlab")
            { IntegrityFault = error => ReportContextFault("NativeSummonBudget", error) };
            RunSummonBudget(context => context.Install());
            _combatMechanism = new M4CombatContexts(_targetRuntime.Fingerprint) { IntegrityFault = DisableCombatMechanism };
            RunCombatMechanism(context => context.Install());
            _arrowLifecycle = new M5CombatContexts(_targetRuntime.Fingerprint) { IntegrityFault = DisableArrowLifecycle };
            RunArrowLifecycle(context => context.Install());
            _arrowCandidates = new M6ArrowCandidateContexts(_targetRuntime.Fingerprint) { IntegrityFault = DisableArrowCandidates };
            RunArrowCandidates(context => context.Install());
            if (_arrowCandidates is { } arrowInputs)
            {
                M18ArrowSourceResetIntervals? resetInputs = null;
                try
                {
                    resetInputs = new(_targetRuntime.Fingerprint, slot =>
                    {
                        var binding = GetBinding(slot);
                        return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player,
                            binding is not null && !Maintenance && _engine?.CanWrite(binding.Key) == true);
                    });
                    resetInputs.Install();
                    arrowInputs.AttachResetIntervals(resetInputs);
                    _arrowResetInputs = resetInputs;
                }
                catch (Exception error)
                {
                    try { resetInputs?.Dispose(); } catch (Exception cleanup) { ReportContextFault("ArrowResetInputsCleanup", cleanup); }
                    ReportContextFault("ArrowResetInputs", error);
                }
            }
            _npcStrikeCauses = new M7NpcStrikeCauseContexts(_targetRuntime.Fingerprint) { IntegrityFault = DisableNpcStrikeCauses };
            RunNpcStrikeCauses(context => context.Install());
            _npcImmunity = new(_targetRuntime.Fingerprint, slot =>
            {
                var binding = GetBinding(slot);
                return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
            });
            try { _preHelloDeadlines.Install(); }
            catch (Exception error) { ReportContextFault("PreHelloDeadlineInstall", error); }
            try
            {
                _requestEgress = new(TimeProvider.System, slot =>
                {
                    var binding = GetBinding(slot);
                    return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
                }) { IntegrityFault = error => ReportContextFault("ChatEgressBudget", error) };
                _requestEgress.Install();
            }
            catch (Exception error)
            {
                var guard = _requestEgress; _requestEgress = null;
                try { guard?.Dispose(); } catch (Exception cleanup) { ReportContextFault("ChatEgressBudgetCleanup", cleanup); }
                ReportContextFault("ChatEgressBudget", error);
            }
            try
            {
                if (_requestEgress is not { Healthy: true })
                    throw new InvalidOperationException("Credential command work scope is unavailable.");
                _commandWork = new(TimeProvider.System, _requestEgress.CurrentSynchronousActor)
                    { IntegrityFault = error => ReportContextFault("CredentialCommandWork", error) };
                _commandWork.Install();
            }
            catch (Exception error)
            {
                var guard = _commandWork; _commandWork = null;
                try { guard?.Dispose(); } catch (Exception cleanup) { ReportContextFault("CredentialCommandWorkCleanup", cleanup); }
                ReportContextFault("CredentialCommandWork", error);
            }
            try
            {
                if (ServerTShock.RestApi is null || ServerTShock.RestManager is null)
                    throw new InvalidOperationException("Native REST authority is unavailable.");
                _restWork = new(TimeProvider.System, ServerTShock.RestApi, ServerTShock.RestManager)
                    { IntegrityFault = error => ReportContextFault("RestAccountWork", error) };
                _restWork.Install();
            }
            catch (Exception error)
            {
                var guard = _restWork; _restWork = null;
                try { guard?.Dispose(); } catch (Exception cleanup) { ReportContextFault("RestAccountWorkCleanup", cleanup); }
                ReportContextFault("RestAccountWork", error);
            }
            _naturalProgression = new M5ProgressionContexts(_targetRuntime.Fingerprint);
            RunNaturalProgression(context => context.Install());
            _wiringExecution = new M6WiringExecutionGuard(TimeProvider.System, slot =>
            {
                var binding = GetBinding(slot);
                return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
            }, writesAvailable: () => !Maintenance);
            _wiringExecution.IntegrityFault = error => ReportContextFault("WiringExecution", error);
            _wiringExecution.PumpIntegrityFault = error => ReportContextFault("PumpExecution", error);
            _wiringExecution.Install();
            _shimmerItems = new(TimeProvider.System, _targetRuntime.Fingerprint)
                { IntegrityFault = error => ReportContextFault("ShimmerItemExecution", error) };
            _shimmerItems.Install();
            _movementObservations = new(TimeProvider.System, _targetRuntime.Fingerprint)
                { IntegrityFault = error => ReportContextFault("MovementObservation", error) };
            _movementObservations.Install(slot =>
            {
                var binding = GetBinding(slot);
                return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
            });
            try
            {
                _paintRecovery = new(TimeProvider.System, slot =>
                {
                    var binding = GetBinding(slot);
                    return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
                }, () => !Maintenance, _scope);
                _paintRecovery.Install();
            }
            catch (Exception error)
            {
                try { _paintRecovery?.Dispose(); } catch (Exception cleanup) { ReportContextFault("PaintRecoveryCleanup", cleanup); }
                _paintRecovery = null;
                ReportContextFault("PaintRecovery", error);
            }
            try
            {
                if (configuration.ExperimentalLiquidScheduler)
                {
                    _liquidExecution = new(TimeProvider.System, () => !Maintenance)
                        { IntegrityFault = error => ReportContextFault("LiquidExecution", error) };
                    _liquidExecution.Install();
                }
            }
            catch (Exception error)
            {
                try { _liquidExecution?.Dispose(); } catch (Exception cleanup) { ReportContextFault("LiquidExecutionCleanup", cleanup); }
                _liquidExecution = null;
                ReportContextFault("LiquidExecution", error);
            }
            _vitals = new M4VitalContexts(_targetRuntime.Fingerprint) { IntegrityFault = DisableVitals };
            RunVitals(context => context.Install(slot =>
            {
                var binding = GetBinding(slot);
                return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
            }));
            try
            {
                _progressionPolicy = M3ProgressionPolicy.Load(ServerTShock.SavePath,
                Path.Combine(AppContext.BaseDirectory, "data", "progression"), _targetRuntime.Fingerprint, _scope);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or InvalidDataException)
            {
                ServerApi.LogWriter.PluginWriteLine(this, "AntiCheat active-creation policy unavailable: " + ex.GetType().Name, TraceLevel.Warning);
            }
        }
        try
        {
            File.WriteAllText(Path.Combine(ServerTShock.SavePath, "anticheat-runtime-evidence.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Runtime = _targetRuntime,
                    Scope = _scope.ToString(),
                    QualifiedRules = M2RuleRegistry.Create(_targetRuntime, _scope, _business?.ProgressionRuleIds)
                        .Where(rule => rule.Qualification == RuleQualification.ProductionQualified)
                        .Select(rule => new { rule.RuleId, rule.Version, rule.AuditReference }).ToArray(),
                    LoadedAssemblies = TargetRuntime.LastEvidence,
                    SupportingContracts = new
                    {
                        EquipmentCalculation = new { Kind = "observation", Installed = _inventory?.EquipmentExecutions.Healthy == true,
                            ClientMultiSlotCompletion = "not-proven", ItemAcquisition = "not-proven" },
                        NativeAvengerEffect = new { Kind = "block", Installed = _inventory?.EquipmentExecutions.AvengerEffectGuardHealthy == true,
                            Contract = M8EquipmentExecutionObserver.AvengerEffectRuleId, Version = M8EquipmentExecutionObserver.AvengerEffectVersion,
                            Scope = "server-UpdateEquips-type935-four-additive-fields-only", AccountBan = false,
                            ClientMultiSlotCompletion = "not-required-no-account-verdict" },
                        NativeClassEmblemEffect = new { Kind = "block", Installed = _inventory?.EquipmentExecutions.ClassEmblemEffectGuardHealthy == true,
                            Contract = M8EquipmentExecutionObserver.ClassEmblemEffectRuleId, Version = M8EquipmentExecutionObserver.ClassEmblemEffectVersion,
                            Scope = "server-UpdateEquips-per-type-489-490-491-2998-base-plus-0.15-once", AccountBan = false,
                            ClientCombatBenefitElimination = false },
                        NativeManaAccessoryEffect = new { Kind = "block", Installed = _inventory?.EquipmentExecutions.ManaAccessoryEffectGuardHealthy == true,
                            Contract = M8EquipmentExecutionObserver.ManaAccessoryEffectRuleId, Version = M8EquipmentExecutionObserver.ManaAccessoryEffectVersion,
                            Scope = "server-UpdateEquips-per-type-111-1595-2221-982-6188-6189-base-mana-additions-once", AccountBan = false,
                            ClientManaConsumptionEnforced = false },
                        NativeNpcImmunity = new { Kind = "block", Installed = _npcImmunity is not null,
                            Contract = M16CombatImmunityRules.RuleId, Version = M16CombatImmunityRules.Version,
                            Scope = "current-same-generation-MoonLordCore398-native-immune-phases-client28", AccountBan = false,
                            GeneralDamageCeiling = false },
                        NativeSlimeSummon = new { Kind = "resource-block", Installed = _summonBudget?.Healthy == true,
                            Contract = M10SummonBudgetRules.RuleId, Version = M10SummonBudgetRules.Version,
                            Scope = "fresh-own-Slime266-native-commit-lower-bound-and-conservative-native-capacity", AccountBan = false },
                        NativeFrostHydraSentry = new { Kind = "resource-block", Installed = _summonBudget?.Healthy == true && _summonBudget.SentryBudget.TargetValidated,
                            Contract = M11SentryBudgetRules.RuleId, Version = M11SentryBudgetRules.Version,
                            Scope = "fresh-or-converted-own-FrostHydra308-native-commit-capacity-plus-spawn-before-retire-transient", AccountBan = false },
                        QuickStackSources = new { Kind = "unsafe-input-block", Version = M10NaturalQuickStackSafety.Version,
                            Scope = "packet85-complete-unique-source-indices-and-native-mapped-array-bounds-before-reader", AccountBan = false },
                        CraftFinalConsumption = new { Kind = "block", Installed = _inventory?.FinalCraftGuardHealthy == true,
                            Scope = "after-native-final-chest-filter-before-CountMatches-and-Consume", AccountBan = false },
                        ShimmerItems = new { Kind = "block-and-observation", Installed = _shimmerItems?.Healthy == true,
                            Contract = M8ShimmerItemTransactions.ContractId, Scope = "four-native-MoonLord-shimmer-transform-directions",
                            AccountAttribution = "none-no-account-verdict" },
                        LiquidScheduler = new { Kind = "resource-block", Installed = _liquidExecution is not null,
                            Enabled = configuration.ExperimentalLiquidScheduler, Experimental = true,
                            Scope = "whole-ordinary-native-scheduler-step", Excluded = "panic-and-transitive-CPU-cost", RequiresGameUpdateBinding = true,
                            LargeFacilityConvergenceQualified = false },
                        PaintCommit = new { Kind = "block-and-limited-recovery", Installed = _paintRecovery is not null,
                            Contract = M9PaintRecovery.ContractId, WallContract = M9PaintRecovery.WallContractId,
                            Scope = "packet63/64-single-block-or-wall-color-authorization-transition", AccountBan = false },
                        ApplicationRequests = new { Kind = "resource-block", Scope = "pre-TShock-chat-format-and-command-dispatch",
                            Burst = 32, RefillPerSecond = 8, Cost = "1+UTF16Characters/256", Replays = false, AccountBan = false },
                        ChatEgress = new { Kind = "resource-block", Installed = _requestEgress?.Healthy == true,
                            Contract = M16RequestEgressBudget.ContractId,
                            Scope = "synchronous-authenticated-chat-dispatch-NetTextModule-serialized-bytes-per-recipient",
                            AccountBan = false, ExpensiveCommandExecutionPrevented = false },
                        CredentialCommands = new { Kind = "resource-block", Installed = _commandWork?.Healthy == true && _requestEgress?.Healthy == true,
                            Contract = M17CommandWorkBudget.Contract, Scope = "audited-native-credential-command-delegate-after-permission-before-execution",
                            Inputs = "exact-synchronous-server-session-and-null-or-authenticated-sender-account",
                            RejectedWork = "no-native-handler-invocation-no-fake-login-failure", AccountBan = false },
                        RestAccountWork = new { Kind = "resource-block", Installed = _restWork?.Healthy == true,
                            EnabledByNativeConfiguration = ServerTShock.Config?.Settings?.RestApiEnabled,
                            HttpServerSha256 = _restWork?.VerifiedHttpServerSha256,
                            Contract = M17RestWorkBudget.Contract, Scope = "native-REST-user-create/update-after-token-permission-before-callback",
                            Inputs = "actual-HTTP-transport-source-and-global-cost-no-gameplay-session-or-account-assumption",
                            RejectedWork = "HTTP429-before-selected-native-handler", AccountBan = false },
                        ConnectionPhases = new { Kind = "availability-control", Installed = _targetRuntime.Verified,
                            Scope = "same-bounded-timeout-scan-current-session-player-client-socket-native-state",
                            MaximumSlotsPerMaintenance = 16, UnknownPhase = "retain-independent-idle-and-resource-controls",
                            PhaseClock = "monotonic-observation-time", AccountAuthentication = false, SscDeliveryProof = false, AccountBan = false },
                        PlayerTeleport = new { Kind = "unsafe-input-block", Contract = M9PlayerTeleportGuard.RuleId,
                            Scope = "packet65-target-index-and-consumed-player-coordinates;packet96-finite-position-velocity", AccountBan = false },
                        Movement = new { Kind = "soft-observation", Installed = _movementObservations?.Healthy == true,
                            PositionSource = "client-declared-and-server-accepted-state", CanSupplyHardProof = false }
                    }
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServerApi.LogWriter.PluginWriteLine(this, "AntiCheat runtime evidence report unavailable: " + ex.GetType().Name, TraceLevel.Warning);
        }
        // No core/Bouncer registration or setting is removed or changed.
        ServerApi.Hooks.ServerConnect.Register(this, OnConnect, -1000);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave, 1000);
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, 1000);
        ServerApi.Hooks.ServerChat.Register(this, OnChat, 1000);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate, -1000);
        // Dedicated-server main loop skips GameUpdate when the last client leaves.
        // Its idle callback is still on the verified main thread; only infrastructure
        // maintenance may run there, never a fabricated gameplay/context tick.
        Main.OnTickForThirdPartySoftwareOnly += OnIdleMaintenance;
        ServerApi.Hooks.GameWorldConnect.Register(this, OnWorldChanged, 1000);
        ServerApi.Hooks.GameWorldDisconnect.Register(this, OnWorldChanged, 1000);
        PlayerHooks.PlayerPostLogin += OnLogin;
        PlayerHooks.PlayerLogout += OnLogout;
        InitializeLabConnections(configurationReason);
        InitializeCombatLabDiagnostics(configurationReason);
        try
        {
            _arrowResetDiagnostics = M18ResetInputDiagnostics.TryCreate(Path.Combine(ServerTShock.SavePath, "anticheat"),
                _targetRuntime.Verified && _scope == ExecutionScope.TestLab && configurationReason == "isolated-loopback-testlab");
        }
        catch (Exception error) { ReportContextFault("ArrowResetDiagnostics", error); }
        ServerApi.LogWriter.PluginWriteLine(this, $"{ReadyMarker} target=1.4.5.8 runtime={_runtime.GameVersion} " +
            $"exactTemporaryBaseline={_runtime.ExactTemporaryBaseline} dotnetVersion={Environment.Version} " +
            $"frameworkDescription=\"{RuntimeInformation.FrameworkDescription}\" scope={_scope} runtimeVerified={_targetRuntime.Verified} m2=true " +
            $"productionHardRules={_productionHardRuleCount} reason={_targetRuntime.Reason} configuration={configurationReason}", TraceLevel.Info);
    }

    private void StartRecovery()
    {
        try
        {
            _journal ??= new FileEnforcementJournal(Path.Combine(ServerTShock.SavePath, "anticheat", "enforcement"));
            var policy = new RulePolicy(BaselineRuntime.OtApiSha256, RuleQualification.Unqualified,
                "docs/RULE_CANDIDATES.md:A02", ImmutableArray.Create((byte)PacketTypes.PlayerSlot), "player-slot-1.4.5.6-observation-v1");
            if (_engine is null)
            {
                var rules = M2RuleRegistry.Create(_targetRuntime, _scope, _business?.ProgressionRuleIds);
                _engine = new AntiCheatEngine(TimeProvider.System, new EngineOptions { Scope = _scope },
                    policy, _journal, new TShockAccountBanStore(_dispatcher), rules);
                _productionHardRuleCount = rules.Count(rule => rule.Qualification == RuleQualification.ProductionQualified);
            }
            _operation = _engine.RecoverAsync(_shutdown.Token).AsTask();
            _infrastructureFailed = false;
            _recoverNext = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _infrastructureFailed = true;
            _recoverNext = true;
            _nextAttemptTimestamp = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            ServerApi.LogWriter.PluginWriteLine(this, $"AntiCheat maintenance: journal initialization failed ({ex.GetType().Name}).", TraceLevel.Error);
        }
    }

    private bool Maintenance => _infrastructureFailed || _engine is null || _engine.IsMaintenanceMode;

    private void InitializeLabConnections(string configurationReason)
    {
        if (_scope != ExecutionScope.TestLab || configurationReason != "isolated-loopback-testlab" ||
            Environment.GetEnvironmentVariable("ANTICHEAT_M7_HELLO_DIAGNOSTICS") != "1") return;
        try
        {
            // Load() already validated the loopback binding, absolute lab root, marker and save ancestors.
            // The diagnostic destination is fixed and never derived from a player or network address.
            string parent = Path.Combine(ServerTShock.SavePath, "anticheat");
            string directory = Path.Combine(parent, "m7-lab-diagnostics");
            if (new[] { parent, directory }.Any(path => Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Lab diagnostic reparse path rejected.");
            Directory.CreateDirectory(directory);
            _labConnections = new(() => directory, this, observeSocketCallbacks: true);
            _labConnections.Install();
            _labLifecycle = new(() => directory);
            _labLifecycle.Install();
        }
        catch (Exception error)
        {
            var observer = _labConnections; _labConnections = null;
            try { observer?.Dispose(); } catch { }
            var lifecycle = _labLifecycle; _labLifecycle = null;
            try { lifecycle?.Dispose(); } catch { }
            ServerApi.LogWriter.PluginWriteLine(this,
                "AntiCheat optional lab connection diagnostics unavailable: " + error.GetType().Name, TraceLevel.Warning);
        }
    }

    private void InitializeCombatLabDiagnostics(string configurationReason)
    {
        if (_scope != ExecutionScope.TestLab || configurationReason != "isolated-loopback-testlab" ||
            Environment.GetEnvironmentVariable("ANTICHEAT_M16_COMBAT_DIAGNOSTICS") != "1") return;
        try
        {
            // The existing configuration loader has verified loopback, the owned lab marker,
            // and save ancestors. This fixed observer path never comes from player input.
            string parent = Path.Combine(ServerTShock.SavePath, "anticheat");
            string directory = Path.Combine(parent, "m16-combat-diagnostics");
            if (new[] { parent, directory }.Any(path => Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Lab diagnostic reparse path rejected.");
            Directory.CreateDirectory(directory);
            _combatLabDiagnostics = new(() => directory, this);
            _combatLabDiagnostics.Install();
        }
        catch (Exception error)
        {
            var observer = _combatLabDiagnostics; _combatLabDiagnostics = null;
            try { observer?.Dispose(); } catch { }
            ReportContextFault("CombatLabDiagnostics", error);
        }
    }

    private void OnConnect(ConnectEventArgs args)
    {
        if (args.Handled) { _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.EarlierHandled); return; }
        if (args.Who < 0 || args.Who >= _bindings.Length || Maintenance)
        {
            args.Handled = true;
            _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.InvalidSlotOrMaintenance);
            return;
        }
        var player = ServerTShock.Players[args.Who];
        if (player is null) { _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.MissingPlayer); return; }
        lock (_bindingsLock)
        {
            if (_preHelloDeadlines.IsTerminal(args.Who)) { args.Handled = true; return; }
            var old = _bindings[args.Who];
            if (old is not null && ReferenceEquals(old.Player, player) &&
                (!old.NetworkRegistered || old.NetworkTransport?.MatchesCurrent(old.Key, player) == true))
            {
                if (!_engine!.CanWrite(old.Key)) args.Handled = true;
                _labConnections?.ObserveRootConnect(args.Who, args.Handled ?
                    RootConnectBoundary.ExistingPlayerRevoked : RootConnectBoundary.ExistingPlayerAccepted);
                return;
            }
            var key = _engine!.OpenSession(args.Who);
            if (key is null)
            {
                args.Handled = true;
                _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.SessionCapacityRejected);
                return;
            }
            if (old is not null) _bytes.Forget(old.RateKey);
            if (old is not null) { _network.Close(old.Key); _applicationRequests.Forget(old.Key); _requestEgress?.Forget(old.Key); _commandWork?.Forget(old.Key); }
            var binding = new Binding(key.Value, player);
            if (player.Client is { Socket: { } socket } client && socket.IsConnected() && socket.GetRemoteAddress() is Terraria.Net.TcpAddress peer)
            {
                var admission = _network.Open(key.Value, peer.Address.ToString());
                if (admission.Disposition is NetworkDisposition.Block or NetworkDisposition.Disconnect)
                {
                    args.Handled = true;
                    _engine.Disconnect(key.Value);
                    _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.NetworkBudgetRejected);
                    return;
                }
                binding.NetworkRegistered = true;
                binding.NetworkTransport = new(key.Value, player, client, socket);
                binding.TimeoutRetirement = M16TimeoutTransportRetirement.Capture(key.Value, player, client, socket);
            }
            _bindings[args.Who] = binding;
            if (player.Client is { Socket: { } boundSocket } boundClient)
                _labLifecycle?.ObserveRootBinding(binding.Key, boundClient, boundSocket);
            RunArrowCandidates(context => context.Connected(binding.Key));
            RunNaturalProgression(context => context.ObserveConnection(binding.Key, player));
            _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.BindingAccepted);
        }
    }

    private Binding? GetBinding(int slot)
    {
        if (slot < 0 || slot >= _bindings.Length) return null;
        lock (_bindingsLock)
        {
            var binding = _bindings[slot];
            return binding is not null && ReferenceEquals(ServerTShock.Players[slot], binding.Player) ? binding : null;
        }
    }

    private void OnLogin(PlayerPostLoginEventArgs args)
    {
        // PostLogin is authentication attribution only. It does NOT attest SSC client acknowledgement.
        var binding = GetBinding(args.Player.Index);
        if (binding is null || !ReferenceEquals(binding.Player, args.Player)) return;
        if (Maintenance) { args.Player.Disconnect("AntiCheat persistence maintenance."); return; }
        if (binding.TimeoutTerminationRequested) return;
        if (!args.Player.IsLoggedIn || args.Player.Account is null) return;
        var result = _engine!.Authenticate(binding.Key, args.Player.Account.ID);
        if (result == AuthenticationResult.Authenticated)
        {
            binding.ResetReportedBusiness();
            RunArrowCandidates(context => context.Authenticated(binding.Key, args.Player.Account.ID));
        }
        if (result != AuthenticationResult.Authenticated) args.Player.Disconnect("AntiCheat account admission rejected.");
    }

    private void OnLogout(PlayerLogoutEventArgs args)
    {
        var binding = GetBinding(args.Player.Index);
        if (binding is null || !ReferenceEquals(binding.Player, args.Player)) return;
        lock (_bindingsLock)
        {
            if (!ReferenceEquals(_bindings[args.Player.Index], binding)) return;
            // An authenticated identity never carries over to an unauthenticated continuation.
            // A revoked connection remains revoked until an actual leave/new connection.
            if (!_engine!.CanWrite(binding.Key)) return;
            _engine.Disconnect(binding.Key);
            RunArrowCandidates(context => context.Left(binding.Key));
            RunInventory(context => context.Forget(binding.Key));
            RunSummonBudget(context => context.Forget(binding.Key));
            _bytes.Forget(binding.RateKey);
            _network.Close(binding.Key);
            _applicationRequests.Forget(binding.Key); _requestEgress?.Forget(binding.Key); _commandWork?.Forget(binding.Key);
            var key = _engine.OpenSession(args.Player.Index);
            _bindings[args.Player.Index] = key is null ? null : new(key.Value, args.Player);
            if (key is not null && args.Player.Client is { Socket: { } socket } client && socket.IsConnected()
                && socket.GetRemoteAddress() is Terraria.Net.TcpAddress peer)
            {
                var network = _network.Open(key.Value, peer.Address.ToString());
                _bindings[args.Player.Index]!.NetworkRegistered = network.Disposition == NetworkDisposition.Allow;
                if (network.Disposition == NetworkDisposition.Allow)
                {
                    _bindings[args.Player.Index]!.NetworkTransport = new(key.Value, args.Player, client, socket);
                    _bindings[args.Player.Index]!.TimeoutRetirement = M16TimeoutTransportRetirement.Capture(key.Value, args.Player, client, socket);
                    _labLifecycle?.ObserveRootBinding(key.Value, client, socket);
                }
                if (network.Disposition is NetworkDisposition.Block or NetworkDisposition.Disconnect)
                    _bindings[args.Player.Index]!.DisconnectOnce("AntiCheat connection resource budget exceeded.");
            }
        }
    }

    private void OnLeave(LeaveEventArgs args)
    {
        // TSAPI may call Leave on its server loop thread. Capture a binding, then compare it under lock.
        var binding = GetBinding(args.Who);
        if (binding is null) return;
        lock (_bindingsLock)
        {
            if (!ReferenceEquals(_bindings[args.Who], binding)) return;
            _engine?.Disconnect(binding.Key);
            RunArrowCandidates(context => context.Left(binding.Key));
            RunInventory(context => context.Forget(binding.Key));
            RunSummonBudget(context => context.Forget(binding.Key));
            _bytes.Forget(binding.RateKey);
            _network.Close(binding.Key);
            _applicationRequests.Forget(binding.Key); _requestEgress?.Forget(binding.Key); _commandWork?.Forget(binding.Key);
            _bindings[args.Who] = null;
        }
    }

    private void OnWorldChanged(EventArgs args)
    {
        lock (_bindingsLock)
        {
            _engine?.AdvanceWorld();
            _business?.ResetWorld();
            RunProgression(context => context.ResetWorld());
            RunNaturalProgression(context => context.ResetWorld());
            RunArrowCandidates(context => context.ResetWorld());
            RunSummonBudget(context => context.ResetWorld());
            foreach (var binding in _bindings)
                if (binding is not null) { _bytes.Forget(binding.RateKey); _network.Close(binding.Key); _applicationRequests.Forget(binding.Key); _requestEgress?.Forget(binding.Key); _commandWork?.Forget(binding.Key); }
            Array.Clear(_bindings);
        }
    }

    private void OnChat(ServerChatEventArgs args)
    {
        // TSAPI dispatches chat before NetGetData, so it needs its own revocation guard.
        if (args.Handled) return; // Never reinstate an earlier cancellation or amplify its logs.
        if (Maintenance) { args.Handled = true; return; }
        var binding = GetBinding(args.Who);
        if (binding is not null && !_engine!.CanWrite(binding.Key)) args.Handled = true;
        else if (binding is not null)
        {
            _engine!.Touch(binding.Key);
            if (binding.NetworkRegistered)
            {
                var control = _network.Consume(binding.Key, Math.Min(65535, Math.Max(1, System.Text.Encoding.UTF8.GetByteCount(args.Text ?? ""))),
                    NetworkRequestKind.BroadcastAmplification);
                if (control.Disposition is NetworkDisposition.Block or NetworkDisposition.Disconnect)
                {
                    args.Handled = true;
                    if (control.Disposition == NetworkDisposition.Disconnect) binding.DisconnectOnce("AntiCheat chat resource budget exceeded.");
                    return;
                }
            }
            // Priority1000 runs before TShock's priority0 OnChat. That handler returns on Handled
            // before formatting, command splitting/alias lookup, permission logging and dispatch.
            // All chat command IDs and both configured prefixes take this same gate, including /me.
            var admission = _applicationRequests.Consume(binding.Key, _engine.GetSession(binding.Key)?.AccountId,
                args.Text?.Length ?? 0);
            if (!admission.Allowed)
            {
                args.Handled = true;
                var report = _network.ReportApplicationRejection(binding.Key);
                if (report.SourceEvent is not null)
                    ServerApi.LogWriter.PluginWriteLine(this, "ANTICHEAT_NETWORK_DRYRUN " + System.Text.Json.JsonSerializer.Serialize(report.SourceEvent), TraceLevel.Warning);
            }
        }
    }

    private void OnGetData(GetDataEventArgs args)
    {
        var binding = GetBinding(args.Msg.whoAmI);
        if (binding?.TimeoutTerminationRequested == true) { args.Handled = true; return; }
        if (binding is not null) RunSummonBudget(context => context.ObserveIncoming(binding.Key, args));
        if (Maintenance)
        {
            // Target-326 packet154 has an empty body and only replies to the sender.
            // No gameplay input is queued for replay after recovery. A revoked subject
            // cannot use even this exception; physical socket close/leave remains available.
            bool ping = _targetRuntime.Verified && (byte)args.MsgID == 154 && args.Length == 1 &&
                binding is { NetworkRegistered: true } &&
                _engine?.GetSession(binding.Key) is { Revoked: false, AccountId: not null };
            if (ping)
                ping = _network.Consume(binding!.Key, 3, NetworkRequestKind.BroadcastAmplification).Disposition == NetworkDisposition.Allow;
            if (!ping) args.Handled = true;
            return;
        }
        if (binding is not null && !_engine!.CanWrite(binding.Key))
        {
            args.Handled = true;
            if (_scope == ExecutionScope.TestLab && binding.TryReportRevokedPacket())
            {
                try { ServerApi.LogWriter.PluginWriteLine(this, $"ANTICHEAT_REVOKED_PACKET slot={binding.Key.Slot} packet={(byte)args.MsgID}", TraceLevel.Info); }
                catch { /* A failed diagnostic must not escape the already committed revocation gate. */ }
            }
            // Packet callbacks are main-update callbacks in the locked baseline. Never enqueue an unbounded disconnect.
            binding.DisconnectOnce("AntiCheat session revoked.");
            return;
        }
        if (binding is not null) _engine!.Touch(binding.Key);
        if (binding is not null) RunNaturalProgression(context => context.ObserveConnection(binding.Key, binding.Player));
        var craftingWork = M13DisplayEntityPacketSafety.Read(args, _targetRuntime.Verified)
            ?? M14ObjectPacketSafety.Read(args, _targetRuntime.Verified)
            ?? M14LObjectPlacementSafety.Read(args, _targetRuntime.Verified)
            ?? M14ResumeTileEntityPlacementSafety.Read(args, _targetRuntime.Verified)
            ?? M15LeashedAnchorItemSafety.Read(args, _targetRuntime.Verified)
            ?? M14ResumePhantasmTargetSafety.Read(args, _targetRuntime.Verified)
            ?? M10NaturalQuickStackSafety.Read(args, _targetRuntime.Verified)
            ?? M16InventorySlotSafety.Read(args, _targetRuntime.Verified)
            ?? M7LoadoutPacketSafety.Read(args, _targetRuntime.Verified)
            ?? M6CraftingPacketSafety.Read(args, _targetRuntime.Verified);
        if (binding is { NetworkRegistered: true })
        {
            var work = craftingWork ?? M5WorldCost.Read(args, _targetRuntime.Verified);
            // Rejected frames still consume receipt/request capacity. An invalid shape never
            // grants expensive work or escapes connection/source/global admission budgets.
            var resource = _network.Consume(binding.Key, Math.Max(1, args.Length), work.Kind,
                work.RejectMalformed ? 0 : work.WorkUnits);
            if (resource.Disposition is NetworkDisposition.Block or NetworkDisposition.Disconnect)
            {
                args.Handled = true;
                if (resource.SourceEvent is not null)
                    ServerApi.LogWriter.PluginWriteLine(this, "ANTICHEAT_NETWORK_DRYRUN " + System.Text.Json.JsonSerializer.Serialize(resource.SourceEvent), TraceLevel.Warning);
                if (resource.Disposition == NetworkDisposition.Disconnect) binding.DisconnectOnce("AntiCheat connection resource budget exceeded.");
                return;
            }
            if (work.RejectMalformed)
            {
                args.Handled = true;
                if (craftingWork is not null) Interlocked.Increment(ref _blockedMalformed);
                return;
            }
        }
        if (craftingWork is { RejectMalformed: true })
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (binding is not null && _bytes.TryConsume(binding.RateKey, Math.Max(1, args.Length)).Behavior == ControlAction.Block)
        {
            args.Handled = true;
            binding.DisconnectOnce("AntiCheat connection resource budget exceeded.");
            return;
        }
        if (_targetRuntime.Verified)
        {
            if (binding is not null)
            {
                RunInventory(context => context.ObserveLoadout(binding.Key, binding.Player, args));
                ProcessM2(args, binding);
            }
            return;
        }
        var parsed = PlayerSlotPacketReader.Read(args.MsgID, args.Msg.readBuffer, args.Index, args.Length,
            _runtime.ExactTemporaryBaseline);
        if (parsed.Kind == PacketReadKind.Malformed)
        {
            PlayerSlotPacketReader.PreserveOrBlock(args, true);
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (parsed.Kind != PacketReadKind.Parsed || binding is null) return;
        Interlocked.Increment(ref _parsedCandidates);
        // Client body is untrusted. No SSC completeness or legal-exception qualification is invented.
        var context = new ProofContext(BaselineRuntime.OtApiSha256, "player-slot-1.4.5.6-observation-v1",
            ParseComplete: true, ClientOrigin: true, AttributionComplete: true, ExceptionsExcluded: false);
        var decision = _engine!.Observe(new(binding.Key, (byte)args.MsgID, parsed.Packet!.ClaimedPlayerSlot, context));
        if (decision.Verdict == Verdict.Unknown) Interlocked.Increment(ref _unknownCandidates);
        PlayerSlotPacketReader.PreserveOrBlock(args, decision.Behavior == ControlAction.Block);
        if (decision.Incident is not null) binding.DisconnectOnce("AntiCheat proven violation.");
    }

    private void ProcessM2(GetDataEventArgs args, Binding binding)
    {
        var creditsRead = M15ProtocolPacketReader.Read(args, _targetRuntime.Verified);
        if (creditsRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (creditsRead.Packet is { } credits)
        {
            ApplyBusiness(args, binding, M15ProtocolPacketReader.Evaluate(credits,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        var cannonRead = M15ProjectilePacketReader.Read(args, _targetRuntime.Verified);
        if (cannonRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (cannonRead.Packet is { } cannon)
        {
            ApplyBusiness(args, binding, M15ProjectilePacketReader.Evaluate(cannon,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (args.Handled || !_engine!.CanWrite(binding.Key)) return;
        }
        var cavernRead = M14RCavernMonsterPacketReader.Read(args, _targetRuntime.Verified);
        if (cavernRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (cavernRead.Packet is { } cavern)
        {
            ApplyBusiness(args, binding, M14RCavernMonsterPacketReader.Evaluate(cavern,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        var alignmentRead = M14RWorldAlignmentPacketReader.Read(args, _targetRuntime.Verified);
        if (alignmentRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (alignmentRead.Packet is { } alignment)
        {
            ApplyBusiness(args, binding, M14RWorldAlignmentPacketReader.Evaluate(alignment,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        var eventStateRead = M14LProtocolPacketReader.Read(args, _targetRuntime.Verified);
        if (eventStateRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (eventStateRead.Packet is { } eventState)
        {
            ApplyBusiness(args, binding, M14LProtocolPacketReader.Evaluate(eventState,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        var playerBuffAddRead = M14LBuffAddPacketReader.Read(args, _targetRuntime.Verified);
        if (playerBuffAddRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (playerBuffAddRead.Packet is { } playerBuffAdd)
        {
            ApplyBusiness(args, binding, M14LBuffAddPacketReader.Evaluate(playerBuffAdd,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (args.Handled || !_engine!.CanWrite(binding.Key)) return;
        }
        var npcBuffStateRead = M14NpcBuffStatePacketReader.Read(args, _targetRuntime.Verified);
        if (npcBuffStateRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (npcBuffStateRead.Packet is { } npcBuffState)
        {
            ApplyBusiness(args, binding, M14NpcBuffStatePacketReader.Evaluate(npcBuffState,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        // The request itself has a closed native send contract. Observe it before
        // core rejection/spawn side effects, while preserving every prior cancellation.
        var npcAuthorityRead = M13NpcAuthorityPacketReader.Read(args, _targetRuntime.Verified);
        if (npcAuthorityRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (npcAuthorityRead.Packet is { } npcAuthority)
        {
            ApplyBusiness(args, binding, M13NpcAuthorityPacketReader.Evaluate(npcAuthority,
                binding.Key, binding.Player, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        var npcBuffRead = M13NpcBuffPacketReader.Read(args, _targetRuntime.Verified);
        if (npcBuffRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (npcBuffRead.Packet is { } npcBuff)
        {
            if (npcBuff.Operation == M13NpcBuffOperation.Add)
            {
                ApplyBusiness(args, binding, M14LBuffAddPacketReader.EvaluateNpc(npcBuff,
                    binding.Key, binding.Player, _targetRuntime.Fingerprint));
                if (!_engine!.CanWrite(binding.Key)) return;
                ApplyBusiness(args, binding, M14RNpcShimmerAdapter.Evaluate(npcBuff,
                    binding.Key, binding.Player, _targetRuntime.Fingerprint));
                if (!_engine!.CanWrite(binding.Key)) return;
                ApplyBusiness(args, binding, M15NpcBuffTypeAdapter.Evaluate(npcBuff,
                    binding.Key, binding.Player, _targetRuntime.Fingerprint));
                if (args.Handled || !_engine!.CanWrite(binding.Key)) return;
            }
            ApplyBusiness(args, binding, M13NpcBuffPacketReader.Evaluate(npcBuff,
                binding.Key, _targetRuntime.Fingerprint));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        BusinessRuleResult? naturalResult = null;
        RunNaturalProgression(context => naturalResult = context.Evaluate(args, binding.Key, binding.Player, _targetRuntime.Verified));
        if (naturalResult is not null)
        {
            ApplyBusiness(args, binding, naturalResult);
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        var teleportRead = M9PlayerTeleportGuard.Read(args, true);
        if (teleportRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (teleportRead.Packet is { } teleport)
        {
            Interlocked.Increment(ref _parsedCandidates);
            ApplyBusiness(args, binding, M9PlayerTeleportGuard.Evaluate(teleport, binding.Key, _targetRuntime.Fingerprint));
            BusinessRuleResult? rodResult = null;
            RunNaturalProgression(context => rodResult = context.EvaluateNaturalTeleport(args,
                binding.Key, binding.Player, _targetRuntime.Verified));
            if (rodResult is not null) ApplyBusiness(args, binding, rodResult);
            return;
        }
        var strikeRead = M6NpcStrikeReader.Read(args, true);
        if (strikeRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (strikeRead.Packet is { } strike)
        {
            Interlocked.Increment(ref _parsedCandidates);
            var strikeResult = M6NpcStrikeReader.Evaluate(strike, binding.Key, _targetRuntime.Fingerprint);
            if (strikeResult.Action != ControlAction.Block)
            {
                BusinessRuleResult? immunityResult = null;
                RunNpcImmunity(context => immunityResult = context.Evaluate(strike, binding.Key, binding.Player));
                if (immunityResult is not null)
                {
                    ApplyBusiness(args, binding, immunityResult);
                    // A refused request must not enter the ordinary strike-cause transaction.
                    if (args.Handled || !_engine!.CanWrite(binding.Key)) return;
                }
            }
            RunNpcStrikeCauses(context => strikeResult = context.Enrich(strikeResult, strike, binding.Key, binding.Player));
            ApplyBusiness(args, binding, strikeResult);
            return;
        }
        var vitalRead = M4VitalPacketReader.Read(args, true);
        if (vitalRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (vitalRead.Packet is { } vital)
        {
            Interlocked.Increment(ref _parsedCandidates);
            BusinessRuleResult? result = null;
            RunVitals(context => result = context.Evaluate(vital, binding.Key, binding.Player));
            if (result is not null) ApplyBusiness(args, binding, result);
            return;
        }
        var buffListRead = M3BuffListReader.Read(args, true);
        if (buffListRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (buffListRead.Packet is { } buffList)
        {
            Interlocked.Increment(ref _parsedCandidates);
            ApplyBusiness(args, binding, M3BuffListReader.Evaluate(buffList, binding.Key,
                _targetRuntime.Fingerprint, alreadyCancelled: args.Handled));
            return;
        }
        var chestSizeRead = M3ChestSizePacketReader.Read(args, true);
        if (chestSizeRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (chestSizeRead.Packet is { } chestSize)
        {
            Interlocked.Increment(ref _parsedCandidates);
            ApplyBusiness(args, binding, M3ChestSizePacketReader.Evaluate(chestSize, binding.Key,
                binding.Player, _targetRuntime.Fingerprint));
            return;
        }
        var cheatRead = M3CheatPacketReader.Read(args, true);
        if (cheatRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (cheatRead.Packet is { } cheatPacket)
        {
            Interlocked.Increment(ref _parsedCandidates);
            ApplyBusiness(args, binding, M3CheatPacketReader.Evaluate(cheatPacket, binding.Key, _targetRuntime.Fingerprint));
            return;
        }
        if (args.MsgID == PacketTypes.ChestOpen) RunInventory(context => context.ObserveActiveTransition(binding.Key));
        var worldRead = M3WorldPacketReader.Read(args, true);
        if (worldRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (worldRead.Packet is { } worldPacket)
        {
            ApplyBusiness(args, binding, M3WorldContexts.Evaluate(worldPacket, binding.Key, binding.Player,
                _targetRuntime.Fingerprint, args.Handled));
            return;
        }
        ApplyBusiness(args, binding, ProtocolRules.EvaluateDirection((byte)args.MsgID, true, true));
        var parsed = M2PacketReader.Read(args, true);
        if (parsed.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            return;
        }
        if (parsed.Packet is not { } packet) return;
        BusinessRuleResult? summonResult = null;
        RunSummonBudget(context => summonResult = context.Evaluate(packet, binding.Key, binding.Player, args.Handled));
        if (summonResult is not null) ApplyBusiness(args, binding, summonResult);
        _movementObservations?.Observe(packet, binding.Key, binding.Player, args.Handled);
        ArrowCandidateEnvelope? arrowEnvelope = null;
        RunArrowCandidates(context => arrowEnvelope = context.Observe(packet, binding.Key, binding.Player));
        IReadOnlyList<BusinessRuleResult> arrowResults = [];
        RunArrowLifecycle(context => arrowResults = context.Evaluate(packet, binding.Key, binding.Player, args.Handled));
        foreach (var arrowResult in arrowResults)
        {
            ApplyBusiness(args, binding, arrowEnvelope?.AddEvidence(arrowResult) ?? arrowResult);
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        BusinessRuleResult? projectionResult = null;
        RunArrowLifecycle(context => projectionResult = context.EvaluateProjection(packet, binding.Key, binding.Player, args.Handled));
        if (projectionResult is not null)
        {
            ApplyBusiness(args, binding, projectionResult);
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        BusinessRuleResult? progressResult = null;
        RunProgression(context => progressResult = context.Evaluate(packet, binding.Key, binding.Player, args.Handled));
        if (progressResult is not null)
        {
            ApplyBusiness(args, binding, progressResult);
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        if (packet.Kind == M2PacketKind.ChestOpen)
        {
            BusinessRuleResult? openResult = null;
            RunInventory(context => openResult = context.ObserveOpen(binding.Key, binding.Player,
                M2PacketReader.Int16(packet.Payload, 0), M2PacketReader.Int16(packet.Payload, 2), args.Handled));
            if (openResult is not null) ApplyBusiness(args, binding, openResult);
        }
        if (packet.Kind == M2PacketKind.PlayerUpdate && packet.Payload[0] == binding.Key.Slot && binding.NetworkRegistered)
            _network.RecordPlayerUpdate(binding.Key);
        Interlocked.Increment(ref _parsedCandidates);
        if (packet.Kind is M2PacketKind.Emoji or M2PacketKind.PlayerSlot)
        {
            var kind = packet.Kind == M2PacketKind.Emoji ? SelfIdentityMessage.Emoji : SelfIdentityMessage.InventorySlot;
            ApplyBusiness(args, binding, ProtocolRules.EvaluateIdentity(kind, binding.Key.Slot, packet.Payload[0], true, true, true));
            if (!_engine!.CanWrite(binding.Key)) return;
        }
        if (packet.Kind is M2PacketKind.Tile or M2PacketKind.Liquid or M2PacketKind.ChestOpen)
        {
            int offset = packet.Kind == M2PacketKind.Tile ? 1 : 0;
            var kind = packet.Kind == M2PacketKind.Tile ? WorldActionKind.TileEdit :
                packet.Kind == M2PacketKind.Liquid ? WorldActionKind.LiquidSet : WorldActionKind.ContainerOpen;
            var observation = new WorldActionObservation(binding.Key, kind,
                M2PacketReader.Int16(packet.Payload, offset), M2PacketReader.Int16(packet.Payload, offset + 2))
            {
                CurrentSession = binding.Key,
                EventId = Guid.NewGuid(),
                RuntimeFingerprint = _targetRuntime.Fingerprint,
                ParseComplete = true,
                ClientOrigin = true,
                BeforeSideEffects = true,
                EditAction = packet.Kind == M2PacketKind.Tile ? packet.Payload[0] : 0,
                EditData = packet.Kind == M2PacketKind.Tile ? M2PacketReader.Int16(packet.Payload, 5) : 0,
                LiquidAmount = packet.Kind == M2PacketKind.Liquid ? packet.Payload[4] : 0,
                LiquidType = packet.Kind == M2PacketKind.Liquid ? packet.Payload[5] : 0,
                Geometry = new(binding.Key.WorldEpoch, _targetRuntime.Fingerprint, "runtime326-geometry", Main.maxTilesX,
                    Main.maxTilesY, Terraria.ID.TileID.Count, Terraria.ID.WallID.Count, Main.sign?.Length ?? 0, true)
            };
            // Bounds precede the core's tile lookups. Permissions and object footprints stay with core
            // until an exact immutable authorization snapshot can be constructed for this event.
            ApplyBusiness(args, binding, WorldRules.Evaluate(observation));
        }
        if (_engine!.CanWrite(binding.Key))
        {
            IReadOnlyList<BusinessRuleResult> mechanismResults = [];
            RunCombatMechanism(context => mechanismResults = context.Evaluate(packet, binding.Key, binding.Player, args.Handled));
            foreach (var result in mechanismResults)
            {
                ApplyBusiness(args, binding, result);
                if (!_engine.CanWrite(binding.Key)) return;
            }
        }
        if (_business is not null && _engine.CanWrite(binding.Key))
        {
            IReadOnlyList<BusinessRuleResult> combatResults = [];
            RunCombat(context => combatResults = context.Evaluate(packet, binding.Key, binding.Player, args.Handled));
            foreach (var result in combatResults)
            {
                ApplyBusiness(args, binding, result);
                if (!_engine.CanWrite(binding.Key)) return;
            }
            var results = _business.Evaluate(packet, binding.Key, binding.Player, slot =>
            {
                var target = GetBinding(slot);
                return (target is null ? null : _engine.GetSession(target.Key), target?.Player);
            }, args.Handled);
            foreach (var result in results)
            {
                ApplyBusiness(args, binding, result);
                if (!_engine.CanWrite(binding.Key)) break;
            }
        }
    }

    private void ApplyBusiness(GetDataEventArgs args, Binding binding, BusinessRuleResult result)
        => ApplyBusinessResult((byte)args.MsgID, args.Handled, binding, result, cancelled => args.Handled = cancelled);

    private bool ApplyBusinessResult(byte packetId, bool alreadyCancelled, Binding binding, BusinessRuleResult result,
        Action<bool>? commitCancellation = null)
    {
        // Also covers direct target callbacks such as nearby crafting, outside NetGetData.
        if (Maintenance)
        {
            commitCancellation?.Invoke(true);
            return true;
        }
        result = result with { Facts = result.Facts.SetItem("alreadyCancelledBeforeRule", alreadyCancelled.ToString()) };
        var context = new ProofContext(_targetRuntime.Fingerprint, M2RuleRegistry.ContextVersion,
            ParseComplete: true, ClientOrigin: true, AttributionComplete: true, ExceptionsExcluded: true);
        var decision = _engine!.ObserveBusiness(new(binding.Key, packetId, result, context));
        if (decision.Verdict == Verdict.Unknown) Interlocked.Increment(ref _unknownCandidates);
        bool cancelled = alreadyCancelled || decision.Behavior == ControlAction.Block;
        // Commit the raw hook result before any fallible diagnostics or disconnect. TSAPI may
        // continue later handlers after an exception; revocation alone does not cancel this packet.
        commitCancellation?.Invoke(cancelled);
        void ReportNotificationFault(string operation, Exception exception)
        {
            try { ReportContextFault(operation, exception); }
            catch { /* Diagnostic failure cannot undo a decision or escape the crafting return callback. */ }
        }
        try
        {
            if ((_scope == ExecutionScope.TestLab || _scope == ExecutionScope.Production &&
                M2RuleRegistry.ProductionRules.TryGetValue(result.RuleId, out var admittedVersion) && admittedVersion == result.Version)
                && binding.TryReportBusiness(result))
            {
                // Reuse the existing bounded per-session diagnostic gate. This component union
                // is observable research context, never a complete legal projectile damage limit.
                string candidateSummary = result.RuleId == M5CombatRules.ArrowEvolutionRuleId &&
                    result.Facts.TryGetValue("canonicalSourceCount", out string? sources)
                    ? $" canonicalSources={sources} finalDamageBound=unproved" : "";
                if (result.RuleId == M5CombatRules.ArrowEvolutionRuleId &&
                    result.Facts.TryGetValue("allowedDamageUnion", out string? allowedUnion))
                    candidateSummary += " allowedDamageUnion=" + allowedUnion;
                if (result.RuleId == M7ProgressionRules.SigilRuleId)
                    candidateSummary = string.Concat(new[] { "worldBaselineComplete", "currentAccountAndActorBound", "pluginContractComplete",
                        "worldExportObservationHealthy", "sigilExportOffThreadCount", "sigilExportFirstThread", "updateThread", "sigilStateComplete", "initialSynchronization" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                if (result.RuleId == M13NaturalGolemRules.RuleId)
                    candidateSummary = string.Concat(new[] { "worldBaselineComplete", "currentAccountAndActorBound", "pluginContractComplete",
                        "worldExportObservationHealthy", "initialSynchronization", "hardMode", "planteraDefeated" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                if (result.RuleId == M14NaturalRodRules.RuleId)
                    candidateSummary = string.Concat(new[] { "geometryHistoryComplete", "currentAccountAndActorBound", "pluginContractComplete",
                        "worldExportObservationHealthy", "initialSynchronization", "widthTiles", "heightTiles" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                if (result.RuleId is M11NaturalItemRules.SolarTabletRuleId or M12NaturalMechanicalRules.RuleId)
                    candidateSummary = string.Concat(new[] { "observedPossibleUseItemIds", "possibleUseSourceSetComplete",
                        "unobservedAnimationStartStillPossible", "useSourceHistoryLostEntries", "sscExportProvesClientReceipt",
                        "clientVariantHistoryComplete", "nativeMechdusaWorld" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                if (result.RuleId is M14LBuffAddRules.PlayerRuleId or M14LBuffAddRules.NpcRuleId or
                    M14RNpcShimmerRules.RuleId or M15NpcBuffTypeRules.RuleId or ContainerRules.RuleId)
                    candidateSummary = string.Concat(new[] { "npcIndex", "buffType", "wireTimeInt16", "targetSlot", "wireTimeInt32",
                        "nativeHostContractComplete", "currentAccountAndActorBound", "contextComplete",
                        "leaseConfirmed", "serverChestAligned", "nativeContainerProtocol" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                ServerApi.LogWriter.PluginWriteLine(this, $"ANTICHEAT_RULE_INPUT rule={result.RuleId} verdict={result.Verdict} " +
                    $"reason={result.Reason} prerequisites={result.PrerequisitesComplete} accountId={_engine.GetSession(binding.Key)?.AccountId} " +
                    $"slot={binding.Key.Slot} packet={packetId} action={decision.Behavior} canceled={cancelled} alreadyCanceled={alreadyCancelled}{candidateSummary}", TraceLevel.Info);
            }
            if (_scope == ExecutionScope.TestLab && result.RuleId == "C5.BuffProtocol" && result.Verdict == Verdict.Pass
                && !cancelled && binding.TryReportBuffPass())
                ServerApi.LogWriter.PluginWriteLine(this, $"ANTICHEAT_RULE_PASS rule=C5.BuffProtocol accountId={_engine.GetSession(binding.Key)?.AccountId} " +
                    $"slot={binding.Key.Slot} canceled=False", TraceLevel.Info);
            if (decision.Incident is { } incident)
                ServerApi.LogWriter.PluginWriteLine(this, $"ANTICHEAT_INCIDENT rule={incident.Evidence.RuleId} accountId={incident.AccountId} " +
                    $"slot={binding.Key.Slot} incident={incident.IncidentId:N} canceled={cancelled} revoked={!_engine.CanWrite(binding.Key)}", TraceLevel.Info);
        }
        catch (Exception exception) { ReportNotificationFault("EnforcementDiagnostic", exception); }
        if (decision.Incident is not null)
        {
            try { binding.DisconnectOnce("AntiCheat proven violation."); }
            catch (Exception exception) { ReportNotificationFault("EnforcementDisconnect", exception); }
        }
        return cancelled;
    }

    private void OnUpdate(EventArgs args)
    {
        RunSummonBudget(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0));
        _liquidExecution?.BindExecutionThread();
        try { _paintRecovery?.Tick(_engine?.CurrentWorldEpoch ?? 0); }
        catch (Exception error) { _paintRecovery?.InvalidateObservation(); ReportContextFault("PaintRecovery", error); }
        _shimmerItems?.Tick(_engine?.CurrentWorldEpoch ?? 0);
        _movementObservations?.Tick(_engine?.CurrentWorldEpoch ?? 0);
        RunVitals(context => context.Tick());
        RunProgression(context => context.Update(_engine?.CurrentWorldEpoch ?? 0));
        if (_targetRuntime.Verified && _business is not null)
        {
            _business.Update(slot => { var binding = GetBinding(slot); return binding is null ? null : _engine?.GetSession(binding.Key); },
                    _engine?.CurrentWorldEpoch ?? 0);
            RunCombat(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0, slot =>
                {
                    var binding = GetBinding(slot);
                    return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
                }));
        }
        RunCombatMechanism(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0, slot =>
        {
            var binding = GetBinding(slot);
            return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
        }));
        RunArrowLifecycle(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0, slot =>
        {
            var binding = GetBinding(slot);
            return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
        }));
        RunArrowCandidates(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0, slot =>
        {
            var binding = GetBinding(slot);
            return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
        }, ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(ServerTShock) || x.Plugin.GetType() == typeof(AntiCheatPlugin))));
        RunNpcStrikeCauses(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0));
        if (_arrowResetDiagnostics is { Healthy: true } resetDiagnostics)
        {
            bool nativeHost = ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(ServerTShock) || x.Plugin.GetType() == typeof(AntiCheatPlugin));
            for (int slot = 0; slot < 255; slot++)
            {
                var binding = GetBinding(slot);
                if (binding is not null)
                    resetDiagnostics.Observe(binding.Key, _engine?.GetSession(binding.Key)?.AccountId, _arrowResetInputs, nativeHost);
            }
        }
        RunNpcImmunity(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0));
        RunNaturalProgression(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0, slot =>
        {
            var binding = GetBinding(slot);
            return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
        }));
        if (!_tablesReported && _business is { ItemTableReady: true, BuffTableReady: true, ProjectileTableReady: true })
        {
            _tablesReported = true;
            ServerApi.LogWriter.PluginWriteLine(this, "ANTICHEAT_TABLES_READY items=True buffs=True projectiles=True", TraceLevel.Info);
        }
        RunInfrastructureMaintenance("GameUpdate");
    }

    private void OnIdleMaintenance()
    {
        // The target also raises this event during active updates. Avoid double-draining
        // the per-update budget; active gameplay continues to use OnUpdate.
        if (Volatile.Read(ref _disposeStarted) != 0 || Main.netMode != 2 || Netplay.HasFullyConnectedClients) return;
        RunInfrastructureMaintenance("NativeIdle");
    }

    private void RunInfrastructureMaintenance(string maintenancePath)
    {
        if (Volatile.Read(ref _maintenanceCallbackFailed) != 0) return;
        try { MaintainInfrastructure(maintenancePath); }
        catch (Exception exception)
        {
            // Unlike TSAPI GameUpdate, the target's idle Action has no handler isolation.
            // Stop this dispatcher after an integrity failure; admission remains in maintenance.
            _infrastructureFailed = true;
            if (Interlocked.Exchange(ref _maintenanceCallbackFailed, 1) == 0)
            {
                try { ReportContextFault("InfrastructureMaintenance", exception); }
                catch { /* A failed logger must not escape into Terraria's dedicated-server loop. */ }
            }
        }
    }

    private void MaintainInfrastructure(string maintenancePath)
    {
        if (_labLifecycle is { } lifecycle && Stopwatch.GetTimestamp() >= _nextLabLifecycleFlush)
        {
            // Explicit isolated lab checkpoints; never disk IO inside a socket callback.
            _nextLabLifecycleFlush = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            lifecycle.Flush();
        }
        _timeoutRetirements.Maintain();
        MaintainNetworkConnections(maintenancePath);
        long before = Stopwatch.GetTimestamp();
        _dispatcher.Drain(1); // Count bounded; synchronous TShock database latency itself is not preemptible.
        _maxDispatchTicks = Math.Max(_maxDispatchTicks, Stopwatch.GetTimestamp() - before);
        if (_operation is not null && !_operation.IsCompleted) return;
        if (_operation is Task<bool> recovery)
        {
            _recoverNext = !recovery.IsCompletedSuccessfully || !recovery.Result;
            if (_recoverNext) _nextAttemptTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        }
        else if (_operation is Task<PumpResult> pump && (!pump.IsCompletedSuccessfully || pump.Result.Failed > 0))
            _nextAttemptTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        _operation = null;
        if (Stopwatch.GetTimestamp() < _nextAttemptTimestamp) return;
        if (_recoverNext) StartRecovery();
        else if (_engine is not null && !_infrastructureFailed && _engine.PendingCount > 0)
            _operation = _engine.PumpAsync(1, _shutdown.Token).AsTask();
    }

    private M10AcceptedConnectionPhase? ReadAcceptedConnectionPhase(SessionKey session)
    {
        if (!_targetRuntime.Verified || (uint)session.Slot >= _bindings.Length) return null;
        // NetworkControls invokes this outside its own gate. Leave/Open already use the
        // binding-lock -> network-gate order, which must never be reversed by observation.
        lock (_bindingsLock)
        {
            var binding = _bindings[session.Slot];
            return binding is { NetworkRegistered: true } && binding.Key == session
                ? binding.NetworkTransport?.ReadAccepted(session, binding.Player) : null;
        }
    }

    private void MaintainNetworkConnections(string maintenancePath)
    {
        foreach (var retirement in _preHelloDeadlines.Inspect(16))
        {
            lock (_bindingsLock)
            {
                int slot = retirement.NativeSlot;
                if ((uint)slot >= _bindings.Length || _bindings[slot] is not null ||
                    ServerTShock.Players[slot] is not null || !retirement.TryAuthorizeBeforeHello()) continue;
                _timeoutRetirements.EnqueueAuthorized(retirement);
            }
        }
        // Only the existing isolated-loopback diagnostics gate allocates native witnesses.
        // This local map cannot outlive a scan or contain more than its 16 selected slots.
        Dictionary<SessionKey, int>? nativeStates = _labConnections is null ? null : new(16);
        var scan = _network.InspectTimeouts(16, session =>
        {
            var accepted = ReadAcceptedConnectionPhase(session);
            if (accepted is not null && nativeStates is not null) nativeStates.Add(session, accepted.NativeState);
            return accepted?.Phase;
        });
        foreach (var observation in scan.Observations)
        {
            if (observation.Status == ConnectionPhaseReadStatus.Faulted &&
                Interlocked.Exchange(ref _phaseObservationFaultReported, 1) == 0)
            {
                try { ServerApi.LogWriter.PluginWriteLine(this,
                    "AntiCheat connection phase observation unavailable for the selected session; independent controls remain active.", TraceLevel.Warning); }
                catch { }
            }
            if (nativeStates is not null && observation.Status == ConnectionPhaseReadStatus.Accepted && observation.After is { } after &&
                after.Phase > observation.Before.Phase && nativeStates.TryGetValue(observation.Session, out int state))
            {
                // At most three strictly advancing phases per live binding. This is the
                // actual scan result, never a claim inferred from the next inbound packet.
                try { ServerApi.LogWriter.PluginWriteLine(this, "ANTICHEAT_M10_PHASE_SCAN " +
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        observation.Session, NativeState = state,
                        AcceptedPhase = observation.AcceptedPhase?.ToString(), Status = observation.Status.ToString(),
                        observation.Before, After = after, MaintenancePath = maintenancePath,
                        ObservedUtc = DateTimeOffset.UtcNow
                    }), TraceLevel.Info); }
                catch { }
            }
        }
        foreach (var timeout in scan.Decisions) DisconnectTimedOutConnection(timeout);
    }

    private void DisconnectTimedOutConnection(NetworkControlDecision timeout)
    {
        try
        {
            lock (_bindingsLock)
            {
                if ((uint)timeout.Session.Slot >= _bindings.Length) return;
                var binding = _bindings[timeout.Session.Slot];
                if (binding?.Key == timeout.Session && binding.NetworkTransport?.MatchesCurrent(timeout.Session, binding.Player) == true)
                {
                    if (binding.TimeoutRetirement is not { } retirement)
                    {
                        // Unrecognized providers keep the pre-existing notification path;
                        // they are outside the audited captured-TCP retirement contract.
                        binding.DisconnectOnce("AntiCheat connection timed out: " + timeout.Reason);
                        ReportContextFault("TimeoutTransportUnsupported", new NotSupportedException("No audited captured TCP transport."));
                        return;
                    }
                    if (!retirement.TryAuthorize(binding.Key, binding.Player)) return;
                    binding.MarkTimeoutTerminationRequested();
                    _engine?.Disconnect(binding.Key); // Availability retirement only; never an account sanction.
                    _timeoutRetirements.EnqueueAuthorized(retirement);
                }
            }
        }
        catch (Exception exception)
        {
            // Revalidation is fallible too: an unavailable transport table or failed peer
            // notification must not invalidate the independent dispatcher and journal.
            try { ReportContextFault("TimeoutDisconnect", exception); } catch { }
        }
    }

    private void RunInventory(Action<M3InventoryContexts> action)
    {
        if (_inventory is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableInventory(exception); }
    }

    private void RunSummonBudget(Action<M10SummonBudgetGuard> action)
    {
        if (_summonBudget is not { } context) return;
        try { action(context); }
        catch (Exception error)
        {
            _summonBudget = null;
            try { context.Dispose(); } catch (Exception cleanup) { ReportContextFault("NativeSummonBudgetCleanup", cleanup); }
            ReportContextFault("NativeSummonBudget", error);
        }
    }

    private void OnBusinessIntegrityFault(string producer, Exception exception)
    {
        if (producer == "Inventory") DisableInventory(exception);
        else ReportContextFault(producer, exception);
    }

    private void RunCombat(Action<M3CombatContexts> action)
    {
        if (_combat is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableCombat(exception); }
    }

    private void RunCombatMechanism(Action<M4CombatContexts> action)
    {
        if (_combatMechanism is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableCombatMechanism(exception); }
    }

    private void RunVitals(Action<M4VitalContexts> action)
    {
        if (_vitals is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableVitals(exception); }
    }

    private void RunArrowLifecycle(Action<M5CombatContexts> action)
    {
        if (_arrowLifecycle is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableArrowLifecycle(exception); }
    }

    private void DisableArrowLifecycle(Exception exception)
    {
        var context = _arrowLifecycle; _arrowLifecycle = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("ArrowLifecycleCleanup", cleanup); }
        ReportContextFault("ArrowLifecycle", exception);
    }

    private void RunArrowCandidates(Action<M6ArrowCandidateContexts> action)
    {
        if (_arrowCandidates is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableArrowCandidates(exception); }
    }

    private void DisableArrowCandidates(Exception exception)
    {
        var context = _arrowCandidates; _arrowCandidates = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("ArrowCandidatesCleanup", cleanup); }
        ReportContextFault("ArrowCandidates", exception);
    }

    private void RunNpcImmunity(Action<M16CombatNpcImmunityGuard> action)
    {
        if (_npcImmunity is not { } context) return;
        try { action(context); }
        catch (Exception error)
        {
            _npcImmunity = null;
            ReportContextFault("NpcImmunityPolicy", error);
        }
    }

    private void RunNpcStrikeCauses(Action<M7NpcStrikeCauseContexts> action)
    {
        if (_npcStrikeCauses is not { } context) return;
        try { action(context); } catch (Exception exception) { DisableNpcStrikeCauses(exception); }
    }

    private void DisableNpcStrikeCauses(Exception exception)
    {
        var context = _npcStrikeCauses; _npcStrikeCauses = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("NpcStrikeCauseCleanup", cleanup); }
        ReportContextFault("NpcStrikeCause", exception);
    }

    private void RunNaturalProgression(Action<M5ProgressionContexts> action)
    {
        if (_naturalProgression is not { } context) return;
        try { action(context); }
        catch (Exception exception)
        {
            _naturalProgression = null;
            try { context.Dispose(); } catch (Exception cleanup) { ReportContextFault("NaturalProgressionCleanup", cleanup); }
            ReportContextFault("NaturalProgression", exception);
        }
    }

    private void DisableCombatMechanism(Exception exception)
    {
        var context = _combatMechanism;
        _combatMechanism = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("CombatMechanismCleanup", cleanup); }
        ReportContextFault("CombatMechanism", exception);
    }

    private void DisableVitals(Exception exception)
    {
        var context = _vitals;
        _vitals = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("VitalsCleanup", cleanup); }
        ReportContextFault("Vitals", exception);
    }

    private void RunProgression(Action<M3ProgressionPolicy> action)
    {
        if (_progressionPolicy is not { } context) return;
        try { action(context); }
        catch (Exception exception)
        {
            _progressionPolicy = null;
            ReportContextFault("ProgressionPolicy", exception);
        }
    }

    private void DisableInventory(Exception exception)
    {
        var context = _inventory;
        _inventory = null;
        if (_business is not null) _business.InventoryContexts = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("InventoryCleanup", cleanup); }
        ReportContextFault("Inventory", exception);
    }

    private void DisableCombat(Exception exception)
    {
        var context = _combat;
        _combat = null;
        try { context?.Dispose(); } catch (Exception cleanup) { ReportContextFault("CombatCleanup", cleanup); }
        ReportContextFault("Combat", exception);
    }

    private void ReportContextFault(string producer, Exception exception)
    {
        try
        {
            lock (_reportedContextFaults)
                if (_reportedContextFaults.Count < 16 && _reportedContextFaults.Add(producer))
                    ServerApi.LogWriter.PluginWriteLine(this, "AntiCheat context disabled after integrity fault: "
                        + producer + " " + exception.GetType().Name, TraceLevel.Error);
        }
        catch { /* Notification failure must not interrupt independent producers or infrastructure maintenance. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            ServerApi.Hooks.ServerConnect.Deregister(this, OnConnect);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.ServerChat.Deregister(this, OnChat);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
            Main.OnTickForThirdPartySoftwareOnly -= OnIdleMaintenance;
            ServerApi.Hooks.GameWorldConnect.Deregister(this, OnWorldChanged);
            ServerApi.Hooks.GameWorldDisconnect.Deregister(this, OnWorldChanged);
            PlayerHooks.PlayerPostLogin -= OnLogin;
            PlayerHooks.PlayerLogout -= OnLogout;
            // A failed detour cleanup must not skip independent subscriptions or durable shutdown.
            Cleanup("Inventory", () => _inventory?.Dispose());
            Cleanup("Combat", () => _combat?.Dispose());
            Cleanup("NativeSummonBudget", () => _summonBudget?.Dispose());
            Cleanup("CombatMechanism", () => _combatMechanism?.Dispose());
            Cleanup("ArrowLifecycle", () => _arrowLifecycle?.Dispose());
            Cleanup("ArrowCandidates", () => _arrowCandidates?.Dispose());
            Cleanup("ArrowResetDiagnostics", () => _arrowResetDiagnostics?.Dispose());
            _arrowResetInputs = null;
            Cleanup("NpcStrikeCauses", () => _npcStrikeCauses?.Dispose());
            Cleanup("NaturalProgression", () => _naturalProgression?.Dispose());
            Cleanup("WiringExecution", () => _wiringExecution?.Dispose());
            Cleanup("ShimmerItems", () => _shimmerItems?.Dispose());
            Cleanup("MovementObservations", () => _movementObservations?.Dispose());
            Cleanup("LiquidExecution", () => _liquidExecution?.Dispose());
            Cleanup("PaintRecovery", () => _paintRecovery?.Dispose());
            Cleanup("RestAccountWork", () => _restWork?.Dispose());
            Cleanup("CredentialCommandWork", () => _commandWork?.Dispose());
            Cleanup("ChatEgressBudget", () => _requestEgress?.Dispose());
            Cleanup("PreHelloDeadline", () => _preHelloDeadlines.Dispose());
            Cleanup("TimeoutRetirements", () => _timeoutRetirements.Dispose());
            Cleanup("Vitals", () => _vitals?.Dispose());
            Cleanup("CombatLabDiagnostics", () => _combatLabDiagnostics?.Dispose());
            var labConnections = _labConnections; _labConnections = null;
            try { labConnections?.Dispose(); }
            catch (Exception error) { ReportContextFault("LabConnectionDiagnosticsCleanup", error); }
            var labLifecycle = _labLifecycle; _labLifecycle = null;
            try { labLifecycle?.Dispose(); }
            catch (Exception error) { ReportContextFault("LabLifecycleDiagnosticsCleanup", error); }
            Cleanup("ShutdownCancellation", () => _shutdown.Cancel());
            Cleanup("Dispatcher", () => _dispatcher.Dispose());
            // One bounded shutdown continuation. A canceled/failed worker cannot cause a clean marker
            // until the engine verifies every known proof has durable structured evidence.
            ShutdownCompletion = CompleteShutdownAsync(_operation, _engine, _journal);
            // TSAPI disposes plugins synchronously and may end the process immediately afterward.
            // Give the existing durable completion a bounded exit window; only its actual result
            // can confirm clean shutdown. A timeout/fault never writes or invents a clean marker.
            var shutdownWait = M16ShutdownBarrier.WaitForExit(ShutdownCompletion);
            ServerApi.LogWriter.PluginWriteLine(this, $"AntiCheat stopping parsed={_parsedCandidates} unknown={_unknownCandidates} " +
                $"malformedBlocked={_blockedMalformed} maxDispatchMs={_maxDispatchTicks * 1000.0 / Stopwatch.Frequency:F3} " +
                $"shutdownOutcome={shutdownWait.Outcome} shutdownFailureType={shutdownWait.FailureType ?? "none"}", TraceLevel.Info);
            void Cleanup(string component, Action cleanup)
            {
                try { cleanup(); }
                catch (Exception error) { ReportContextFault(component + "Cleanup", error); }
            }
        }
        base.Dispose(disposing);
    }

    private static async Task<bool> CompleteShutdownAsync(Task? operation, AntiCheatEngine? engine, FileEnforcementJournal? journal)
    {
        try
        {
            if (operation is not null)
            {
                try { await operation.ConfigureAwait(false); }
                catch (Exception) { /* The durable guard and engine state, not task completion, decide whether clean is safe. */ }
            }
            return engine is not null && await engine.CompleteShutdownAsync().ConfigureAwait(false);
        }
        finally { journal?.Dispose(); }
    }
}
