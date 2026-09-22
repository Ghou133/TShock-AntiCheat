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
        public bool TryReportBusiness(BusinessRuleResult result, string? discriminator = null)
        {
            lock (_reportedBusiness)
                return _reportedBusiness.Count < 96 && _reportedBusiness.Add(
                    result.RuleId + "/" + result.Verdict + "/" + result.Reason + "/" + discriminator);
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
    private M18ObservationJournal? _m18ObservationJournal;
    private AntiCheatEngine? _engine;
    private Task? _operation;
    private RuntimeStatus _runtime = new(false, "unknown", "not-initialized");
    private TargetRuntimeStatus _targetRuntime = new(false, "unknown", "unknown", "not-initialized");
    private ExecutionScope _scope;
    private M18CandidateMode _m18CandidateMode;
    private bool _m18ObservationActive;
    private bool _m18CandidateActive;
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
    private M18LockHealthContext? _lockHealth;
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
    private int _particleRuntimeDiagnosticWritten;
    private int _m18P0DWorldRuleDiagnosticMask;
    private int _m18GroundItemPacketDiagnosticCount;
    private int _m18RootConnectDiagnosticCount;
    private readonly object _m18GroundItemPostStateLock = new();
    private readonly Queue<(M18GroundItemPacketTrace Trace, BusinessRuleResult Result)> _m18GroundItemPostStates = [];
    private int _m18GroundItemPostStateDropped;
    private long _m18GroundItemPostUpdateCount;
    private long _m4VitalsUpdateCount;
    private int _m18ServerSendDiagnosticCount;
    private int _m18PacketStageDiagnosticCount;
    private long _m18GameUpdateDiagnosticCount;

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
        _m18CandidateMode = configuration.M18CandidateMode;
        var m18WorldEditOptions = M18WorldEditQueueOptions.ForExecutionScope(_scope,
            configuration.M18CandidateMode, configuration.M18RecordObservations, configuration.M18EnableBlocks);
        var m18ParticleOptions = M18ParticleQueueOptions.ForExecutionScope(_scope,
            configuration.M18CandidateMode, configuration.M18RecordObservations, configuration.M18EnableBlocks);
        var m18ImportantItemOptions = M18ImportantItemQueueOptions.ForExecutionScope(_scope,
            configuration.M18CandidateMode, configuration.M18RecordObservations);
        var m18GroundItemClearOptions = M18GroundItemClearQueueOptions.ForExecutionScope(_scope,
            configuration.M18CandidateMode, configuration.M18RecordObservations, configuration.M18EnableBlocks,
            configuration.M18EnablePermanentSanctions);
        var m18NpcStrikeOptions = M18NpcStrikeQueueOptions.ForExecutionScope(_scope,
            configuration.M18CandidateMode, configuration.M18RecordObservations, configuration.M18EnableBlocks,
            configuration.M18EnablePermanentSanctions);
        var m18ServiceKick = configuration.M18EnableServiceKick ??
            (configuration.M18CandidateMode == M18CandidateMode.Auto && _scope == ExecutionScope.TestLab);
        var m18LockHealthOptions = M18LockHealthOptions.ForExecutionScope(_scope,
            configuration.M18CandidateMode, configuration.M18RecordObservations, configuration.M18EnableBlocks,
            m18ServiceKick);
        _m18ObservationActive = _targetRuntime.Verified &&
            (m18WorldEditOptions.Enabled || m18ParticleOptions.Enabled ||
             m18ImportantItemOptions.Enabled || m18GroundItemClearOptions.Enabled ||
             m18NpcStrikeOptions.Enabled || m18LockHealthOptions.Enabled);
        _m18CandidateActive = _targetRuntime.Verified &&
            M18ExecutionModePolicy.CandidateControlsEnabled(_scope, configuration.M18CandidateMode) &&
            (m18WorldEditOptions.Enabled || m18ParticleOptions.Enabled ||
             m18GroundItemClearOptions.Enabled || m18NpcStrikeOptions.Enabled || m18LockHealthOptions.Enabled);
        if (_targetRuntime.Verified)
        {
            try { _business = new(_targetRuntime.Fingerprint, Path.Combine(AppContext.BaseDirectory, "data", "progression"),
                m18WorldEditOptions, m18ParticleOptions,
                m18ImportantItemOptions, M18ImportantItemCatalog.Items,
                m18GroundItemClearOptions,
                enablePermanentSanctionCandidates: m18NpcStrikeOptions.EnablePermanentSanctions); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                ServerApi.LogWriter.PluginWriteLine(this, "AntiCheat progression catalog unavailable: " + ex.GetType().Name, TraceLevel.Warning);
                _business = new(_targetRuntime.Fingerprint, Path.Combine(ServerTShock.SavePath, "absent-candidate-data"),
                m18WorldEditOptions, m18ParticleOptions,
                    m18ImportantItemOptions, M18ImportantItemCatalog.Items,
                    m18GroundItemClearOptions,
                    enablePermanentSanctionCandidates: m18NpcStrikeOptions.EnablePermanentSanctions);
            }
        }
        StartRecovery();
        try
        {
            _m18ObservationJournal = new M18ObservationJournal(
                Path.Combine(ServerTShock.SavePath, "anticheat", "m18-observations.json"));
        }
        catch (Exception error)
        {
            // Observation persistence is a health signal only. The existing
            // packet and sanction paths remain independently bounded.
            ReportContextFault("M18ObservationJournal", error);
        }
        if (_business is not null)
        {
            _business.ImportantItemObservationRecorded = RecordM18ImportantItemObservation;
            _business.GroundItemPacketObserved = RecordM18GroundItemPacket;
            _business.GroundItemDecisionObserved = QueueM18GroundItemPostState;
        }
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
            _npcStrikeCauses = new M7NpcStrikeCauseContexts(_targetRuntime.Fingerprint,
                m18NpcStrikeOptions,
                m18ImportantItemOptions,
                M18ImportantItemCatalog.Items)
            {
                IntegrityFault = DisableNpcStrikeCauses,
                OptionalRewardObservationFault = error => ReportContextFault("M18ImportantItemRewardObserver", error),
                StrikeObservationRecorded = RecordM18StrikeObservation,
                StrikeCompletionRecorded = RecordM18StrikeCompletion,
                ImportantItemRewardObserved = RecordM18RewardObservation,
                SummonAuxiliaryContext = session => _summonBudget?.CaptureNpcStrikeAuxiliary(session),
            };
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
            _vitals = new M4VitalContexts(_targetRuntime.Fingerprint)
            {
                IntegrityFault = DisableVitals,
                SendDataObserved = RecordM18ServerSend,
            };
            RunVitals(context => context.Install(slot =>
            {
                var binding = GetBinding(slot);
                return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
            }));
            _lockHealth = new(TimeProvider.System, _targetRuntime.Fingerprint,
                m18LockHealthOptions)
            { IntegrityFault = error => ReportContextFault("LockHealthCandidate", error) };
            _lockHealth.Install(slot =>
            {
                var binding = GetBinding(slot);
                return (binding is null ? null : _engine?.GetSession(binding.Key), binding?.Player);
            });
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
                    M18Candidates = new
                    {
                        RequestedMode = _m18CandidateMode.ToString(),
                        Active = _m18CandidateActive,
                        ObservationActive = _m18ObservationActive,
                        RecordMode = "Auto records in ObserveOnly/Production/TestLab; explicit candidate modes require their matching scope",
                        CandidateControlMode = _m18CandidateActive ? "candidate-controls-enabled" : "record-only-or-disabled",
                        RecordObservations = configuration.M18RecordObservations,
                        Blocks = configuration.M18EnableBlocks,
                        ServiceKick = m18LockHealthOptions.EnableServiceKick,
                        PermanentSanctions = m18NpcStrikeOptions.EnablePermanentSanctions,
                        ConfigurationReason = configurationReason,
                        NpcStrikeQueue = m18NpcStrikeOptions.Enabled,
                        WorldEditQueue = m18WorldEditOptions.Enabled,
                        ParticleQueue = m18ParticleOptions.Enabled,
                        ImportantItemQueue = m18ImportantItemOptions.Enabled,
                        GroundItemClearQueue = m18GroundItemClearOptions.Enabled,
                        LockHealth = m18LockHealthOptions.Enabled,
                        ObservationJournal = new
                        {
                            Installed = _m18ObservationJournal is not null,
                            Healthy = _m18ObservationJournal?.Healthy == true,
                            Path = "anticheat/m18-observations.json",
                            StrikeBuckets = _m18ObservationJournal?.StrikeBucketCount ?? 0,
                            ImportantItemBuckets = _m18ObservationJournal?.ImportantItemBucketCount ?? 0,
                            ErrorType = _m18ObservationJournal?.LastErrorType,
                        },
                        Vitals = new
                        {
                            Installed = _vitals?.Installed == true,
                            Failed = _vitals?.Failed == true,
                            InstallThreadId = _vitals?.InstallThreadId ?? 0,
                            LastTickThreadId = _vitals?.LastTickThreadId ?? 0,
                            TickCount = _vitals?.TickCount ?? 0,
                            HurtPluginObservationHealthy = _vitals?.HurtPluginObservationHealthy == true,
                            Note = "Own lifecycle state; ObservationJournal health is reported separately."
                        },
                    },
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
        ServerApi.Hooks.GamePostUpdate.Register(this, OnPostUpdate, -1000);
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
            $"productionHardRules={_productionHardRuleCount} m18Candidate={_m18CandidateActive} " +
            $"reason={_targetRuntime.Reason} configuration={configurationReason}", TraceLevel.Info);
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
        TraceM18RootConnect(args, "entry");
        if (args.Handled) { _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.EarlierHandled); TraceM18RootConnect(args, "earlier-handled"); return; }
        if (args.Who < 0 || args.Who >= _bindings.Length || Maintenance)
        {
            args.Handled = true;
            _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.InvalidSlotOrMaintenance); TraceM18RootConnect(args, "invalid-slot-or-maintenance");
            return;
        }
        var player = ServerTShock.Players[args.Who];
        if (player is null) { _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.MissingPlayer); TraceM18RootConnect(args, "missing-player"); return; }
        lock (_bindingsLock)
        {
            if (_preHelloDeadlines.IsTerminal(args.Who)) { args.Handled = true; TraceM18RootConnect(args, "prehello-terminal"); return; }
            var old = _bindings[args.Who];
            if (old is not null && ReferenceEquals(old.Player, player) &&
                (!old.NetworkRegistered || old.NetworkTransport?.MatchesCurrent(old.Key, player) == true))
            {
                if (!_engine!.CanWrite(old.Key)) args.Handled = true;
                _labConnections?.ObserveRootConnect(args.Who, args.Handled ?
                    RootConnectBoundary.ExistingPlayerRevoked : RootConnectBoundary.ExistingPlayerAccepted);
                TraceM18RootConnect(args, args.Handled ? "existing-revoked" : "existing-accepted");
                return;
            }
            var key = _engine!.OpenSession(args.Who);
            if (key is null)
            {
                args.Handled = true;
                _labConnections?.ObserveRootConnect(args.Who, RootConnectBoundary.SessionCapacityRejected);
                TraceM18RootConnect(args, "session-capacity-rejected");
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
                    TraceM18RootConnect(args, "network-budget-rejected");
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
            TraceM18RootConnect(args, "binding-accepted");
        }
    }

    private void TraceM18RootConnect(ConnectEventArgs args, string stage)
    {
        if (!_m18CandidateActive || Volatile.Read(ref _m18RootConnectDiagnosticCount) >= 64) return;
        try
        {
            int slot = args.Who;
            var client = (uint)slot < Netplay.Clients.Length ? Netplay.Clients[slot] : null;
            string player = (uint)slot < ServerTShock.Players.Length && ServerTShock.Players[slot] is not null ? "present" : "none";
            if (Interlocked.Increment(ref _m18RootConnectDiagnosticCount) > 64) return;
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_ROOT_CONNECT stage={stage} slot={slot} handled={args.Handled} maintenance={Maintenance} " +
                $"player={player} clientState={client?.State.ToString() ?? "none"} socket={(client?.Socket is null ? "none" : client.Socket.GetType().Name)} " +
                $"pending={client?.PendingTermination} approved={client?.PendingTerminationApproved}", TraceLevel.Info);
        }
        catch { }
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
            RunNpcStrikeCauses(context => context.Forget(binding.Key));
            _business?.Forget(binding.Key);
            _lockHealth?.Forget(binding.Key);
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
            RunNpcStrikeCauses(context => context.Forget(binding.Key));
            _business?.Forget(binding.Key);
            _lockHealth?.Forget(binding.Key);
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
            _lockHealth?.ResetWorld();
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
        RecordM18PacketStage(args, binding, "entry");
        TraceM18Handshake(args, binding, "entry");
        if (binding?.TimeoutTerminationRequested == true) { args.Handled = true; TraceM18Handshake(args, binding, "timeout"); return; }
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
            TraceM18Handshake(args, binding, "maintenance");
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
            TraceM18Handshake(args, binding, "revoked");
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
                TraceM18Handshake(args, binding, "resource");
                return;
            }
            if (work.RejectMalformed)
            {
                args.Handled = true;
                if (craftingWork is not null) Interlocked.Increment(ref _blockedMalformed);
                TraceM18Handshake(args, binding, "resource-malformed");
                return;
            }
        }
        if (craftingWork is { RejectMalformed: true })
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            TraceM18Handshake(args, binding, "malformed");
            return;
        }
        if (binding is not null && _bytes.TryConsume(binding.RateKey, Math.Max(1, args.Length)).Behavior == ControlAction.Block)
        {
            args.Handled = true;
            binding.DisconnectOnce("AntiCheat connection resource budget exceeded.");
            TraceM18Handshake(args, binding, "rate");
            return;
        }
        var particleRead = M18ParticlePacketReader.Read(args, _targetRuntime.Verified);
        if (_m18CandidateActive && (byte)args.MsgID == M18ParticlePacketReader.MessageId &&
            Interlocked.Exchange(ref _particleRuntimeDiagnosticWritten, 1) == 0)
        {
            try
            {
                ServerApi.LogWriter.PluginWriteLine(this, $"ANTICHEAT_M18_PARTICLE_RUNTIME packet=82 " +
                    $"readKind={particleRead.Kind} {M18ParticlePacketReader.DescribeRuntime(_targetRuntime.Verified)}", TraceLevel.Info);
            }
            catch { /* A bounded diagnostic must not affect packet handling. */ }
        }
        if (particleRead.Kind == PacketReadKind.Malformed)
        {
            args.Handled = true;
            Interlocked.Increment(ref _blockedMalformed);
            TraceM18Handshake(args, binding, "malformed");
            return;
        }
        if (particleRead.Packet is { } particle && binding is not null && _business is not null)
        {
            var particleResult = _business.EvaluateParticle(particle, binding.Key, binding.Player, args.Handled);
            if (particleResult is not null) ApplyBusiness(args, binding, particleResult);
            if (args.Handled || !_engine!.CanWrite(binding.Key)) { TraceM18Handshake(args, binding, "particle-result"); return; }
        }
        if (_targetRuntime.Verified)
        {
            if (binding is not null)
            {
                RunInventory(context => context.ObserveLoadout(binding.Key, binding.Player, args));
                RecordM18PacketStage(args, binding, "before-process-m2");
                ProcessM2(args, binding);
                RecordM18PacketStage(args, binding, "after-process-m2");
            }
            TraceM18Handshake(args, binding, "verified-return");
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

    private void TraceM18Handshake(GetDataEventArgs args, Binding? binding, string stage)
    {
        if (!_m18CandidateActive || (byte)args.MsgID != 1) return;
        try
        {
            string writable = binding is null ? "none" : _engine?.CanWrite(binding.Key).ToString() ?? "null";
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_HANDSHAKE stage={stage} slot={args.Msg.whoAmI} handled={args.Handled} " +
                $"binding={(binding is null ? "none" : "present")} networkRegistered={binding?.NetworkRegistered} " +
                $"timeoutTermination={binding?.TimeoutTerminationRequested} canWrite={writable}", TraceLevel.Info);
        }
        catch { }
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
            var lockDecision = vital.Kind == M4VitalKind.Life
                ? _lockHealth?.ObserveLifeSync(vital, binding.Key, binding.Player, args.Handled)
                : null;
            if (lockDecision?.RuleResult is { } lockResult)
            {
                ApplyBusiness(args, binding, lockResult);
                if (lockDecision.Kick && args.Handled && _engine!.CanWrite(binding.Key))
                    binding.DisconnectOnce("AntiCheat service rule: sustained one-point full-life sync.");
            }
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
        RunNpcStrikeCauses(context => context.ObservePlayerControls(packet, binding.Key, binding.Player, args.Handled));
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
            if (packet.Kind is M2PacketKind.WorldItemDrop or M2PacketKind.WorldItemDespawn)
                TraceM18P0DWorldRule(args, binding, results);
            foreach (var result in results)
            {
                ApplyBusiness(args, binding, result);
                if (!_engine.CanWrite(binding.Key)) break;
            }
        }
    }

    private void TraceM18P0DWorldRule(GetDataEventArgs args, Binding binding,
        IReadOnlyList<BusinessRuleResult> results)
    {
        if (!_m18CandidateActive || (byte)args.MsgID is not (21 or 90 or 151)) return;
        int bit = (byte)args.MsgID switch
        {
            21 => 1,
            90 => 2,
            151 => 4,
            _ => 0,
        };
        if ((Interlocked.Or(ref _m18P0DWorldRuleDiagnosticMask, bit) & bit) != 0) return;
        try
        {
            var itemRule = results.FirstOrDefault(x => x.RuleId == InventoryRules.RuleId);
            var clearRule = results.FirstOrDefault(x => x.RuleId == M18GroundItemClearQueueRules.RuleId);
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_P0D_ITEM_RULE packet={(byte)args.MsgID} slot={binding.Key.Slot} " +
                $"rule={itemRule?.RuleId ?? "none"} verdict={itemRule?.Verdict.ToString() ?? "none"} " +
                $"resultAction={itemRule?.Action.ToString() ?? "none"} reason={itemRule?.Reason ?? "none"} " +
                $"clearRule={clearRule?.RuleId ?? "none"} clearVerdict={clearRule?.Verdict.ToString() ?? "none"} " +
                $"clearAction={clearRule?.Action.ToString() ?? "none"} clearReason={clearRule?.Reason ?? "none"} " +
                $"resultCount={results.Count}", TraceLevel.Info);
        }
        catch { /* Bounded diagnostic cannot affect packet handling. */ }
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
            bool m18CandidateRule = _m18CandidateActive && result.RuleId is
                M18NpcStrikeQueueRules.RuleId or M18WorldEditQueueRules.RuleId or
                M18ParticleQueueRules.RuleId or M18LockHealthRules.RuleId or
                M18ImportantItemQueueRules.RuleId or M18GroundItemClearQueueRules.RuleId or
                InventoryRules.RuleId;
            if ((_scope == ExecutionScope.TestLab || m18CandidateRule || _scope == ExecutionScope.Production &&
                M2RuleRegistry.ProductionRules.TryGetValue(result.RuleId, out var admittedVersion) && admittedVersion == result.Version)
                && binding.TryReportBusiness(result, result.RuleId == M18GroundItemClearQueueRules.RuleId &&
                    result.Facts.TryGetValue("targetSlot", out var targetSlot) &&
                    result.Facts.TryGetValue("targetGeneration", out var targetGeneration)
                    ? targetSlot + "/" + targetGeneration : null))
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
                if (result.RuleId == M18NpcStrikeQueueRules.RuleId)
                    candidateSummary = string.Concat(new[] { "strikeQueue", "strikeQueueSamples", "strikeQueueClassification",
                        "strikeQueueActionContract" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                if (result.RuleId == M18GroundItemClearQueueRules.RuleId)
                    candidateSummary = string.Concat(new[] { "contractVersion", "targetSlot", "targetGeneration",
                        "targetType", "targetStack", "targetCoordinates", "requestCoordinates",
                        "remoteOrMismatched", "capacityExhausted", "sessionClearCount", "eventsRetained",
                        "positionSequence", "identityComplete", "actionContract", "sanctionContract" }
                        .Where(result.Facts.ContainsKey).Select(key => $" {key}={result.Facts[key]}"));
                ServerApi.LogWriter.PluginWriteLine(this, $"ANTICHEAT_RULE_INPUT rule={result.RuleId} verdict={result.Verdict} " +
                    $"reason={result.Reason} prerequisites={result.PrerequisitesComplete} accountId={_engine.GetSession(binding.Key)?.AccountId} " +
                    $"slot={binding.Key.Slot} packet={packetId} action={decision.Behavior} decisionReason={decision.Reason} " +
                    $"facts={result.Facts.Count} canceled={cancelled} alreadyCanceled={alreadyCancelled}{candidateSummary}", TraceLevel.Info);
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
        RecordM18GameUpdate();
        RunSummonBudget(context => context.Tick(_engine?.CurrentWorldEpoch ?? 0));
        _liquidExecution?.BindExecutionThread();
        try { _paintRecovery?.Tick(_engine?.CurrentWorldEpoch ?? 0); }
        catch (Exception error) { _paintRecovery?.InvalidateObservation(); ReportContextFault("PaintRecovery", error); }
        _shimmerItems?.Tick(_engine?.CurrentWorldEpoch ?? 0);
        _movementObservations?.Tick(_engine?.CurrentWorldEpoch ?? 0);
        _lockHealth?.Tick(_engine?.CurrentWorldEpoch ?? 0);
        RunVitals(context => context.Tick());
        RecordM4VitalsHealth("GameUpdate");
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
        try { _m18ObservationJournal?.FlushIfDue(); }
        catch (Exception error) { ReportContextFault("M18ObservationJournalFlush", error); }
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

    private void RecordM4VitalsHealth(string source)
    {
        if (!_m18CandidateActive || _vitals is not { } vitals) return;
        long count = Interlocked.Increment(ref _m4VitalsUpdateCount);
        if (count != 1 && count % 600 != 0) return;
        try
        {
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M4_VITALS_HEALTH source={source} updateCount={count} " +
                $"installed={vitals.Installed} failed={vitals.Failed} " +
                $"installThread={vitals.InstallThreadId} lastTickThread={vitals.LastTickThreadId} " +
                $"currentThread={Environment.CurrentManagedThreadId} " +
                $"threadMatch={vitals.CurrentThreadMatchesUpdateThread} " +
                $"vitalsTickCount={vitals.TickCount} " +
                $"hurtPluginObservationHealthy={vitals.HurtPluginObservationHealthy}", TraceLevel.Info);
        }
        catch (Exception error) { ReportContextFault("M4VitalsHealthDiagnostic", error); }
    }

    private void RecordM18PacketStage(GetDataEventArgs args, Binding? binding, string stage)
    {
        if (!_m18CandidateActive) return;
        int ordinal = Interlocked.Increment(ref _m18PacketStageDiagnosticCount);
        if (ordinal > 256) return;
        try
        {
            var session = binding is null ? null : _engine?.GetSession(binding.Key);
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_PACKET_STAGE stage={stage} packet={(byte)args.MsgID} " +
                $"slot={args.Msg.whoAmI} index={args.Index} length={args.Length} handled={args.Handled} " +
                $"accountId={session?.AccountId} playerActive={Main.player[args.Msg.whoAmI]?.active} " +
                $"thread={Environment.CurrentManagedThreadId} ordinal={ordinal}", TraceLevel.Info);
        }
        catch { }
    }

    private void RecordM18GameUpdate()
    {
        if (!_m18CandidateActive) return;
        long ordinal = Interlocked.Increment(ref _m18GameUpdateDiagnosticCount);
        if (ordinal != 1 && ordinal % 600 != 0) return;
        try
        {
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_GAME_UPDATE ordinal={ordinal} thread={Environment.CurrentManagedThreadId} " +
                $"netMode={Main.netMode} fullyConnected={Netplay.HasFullyConnectedClients} " +
                $"playerCount={Main.player.Count(player => player is not null && player.active)} " +
                $"candidate={_m18CandidateActive}", TraceLevel.Info);
        }
        catch { }
    }

    private void RecordM18ServerSend(HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!_m18CandidateActive || args.msgType is not (3 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 16 or 42 or 49))
            return;
        int ordinal = Interlocked.Increment(ref _m18ServerSendDiagnosticCount);
        if (ordinal > 128) return;
        try
        {
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_SERVER_SEND packet={args.msgType} remote={args.remoteClient} " +
                $"ignore={args.ignoreClient} number={args.number} number2={args.number2} " +
                $"number3={args.number3} number4={args.number4} continue={args.ContinueExecution} " +
                $"thread={Environment.CurrentManagedThreadId} ordinal={ordinal}", TraceLevel.Info);
        }
        catch { }
    }

    private void QueueM18GroundItemPostState(M18GroundItemPacketTrace trace, BusinessRuleResult result)
    {
        if (!_m18CandidateActive || result.RuleId != M18GroundItemClearQueueRules.RuleId) return;
        lock (_m18GroundItemPostStateLock)
        {
            if (_m18GroundItemPostStates.Count >= 64)
            {
                _m18GroundItemPostStates.Dequeue();
                if (_m18GroundItemPostStateDropped < int.MaxValue) _m18GroundItemPostStateDropped++;
            }
            _m18GroundItemPostStates.Enqueue((trace, result));
        }
    }

    private void OnPostUpdate(EventArgs args)
    {
        if (!_m18CandidateActive) return;
        int remaining = 64;
        while (remaining-- > 0)
        {
            (M18GroundItemPacketTrace Trace, BusinessRuleResult Result) pending;
            lock (_m18GroundItemPostStateLock)
            {
                if (_m18GroundItemPostStates.Count == 0) break;
                pending = _m18GroundItemPostStates.Dequeue();
            }

            try
            {
                Interlocked.Increment(ref _m18GroundItemPostUpdateCount);
                var trace = pending.Trace;
                var result = pending.Result;
                int id = trace.TargetSlot;
                var item = id >= 0 && id < Main.maxItems && id < Main.item.Length ? Main.item[id] : null;
                string post = item is null ? "missing" :
                    $"exists=True/active={item.active}/type={item.type}/stack={item.stack}/" +
                    $"grabbed={item.beingGrabbed}/pos={item.position.X},{item.position.Y}";
                string targetGeneration = result.Facts.TryGetValue("targetGeneration", out var generation) ? generation : "unknown";
                bool nativeEligible = !trace.AlreadyCancelled && result.Action != ControlAction.Block;
                ServerApi.LogWriter.PluginWriteLine(this,
                    $"ANTICHEAT_M18_F08_POST packet={trace.PacketId} accountId={trace.AccountId} " +
                    $"slot={trace.Session.Slot} id={id} targetGeneration={targetGeneration} " +
                    $"ruleAction={result.Action} verdict={result.Verdict} reason={result.Reason} " +
                    $"decisionCancelled={trace.AlreadyCancelled || result.Action == ControlAction.Block} " +
                    $"nativeBoundary={(nativeEligible ? "eligible-after-guard" : "blocked-before-native")} " +
                    $"preServerItem={trace.TargetExists}/{trace.TargetActive}/{trace.ServerTargetType}/{trace.ServerTargetStack}/grabbed={trace.TargetBeingGrabbed} " +
                    $"postServerItem={post} postHook=GamePostUpdate dropped={Volatile.Read(ref _m18GroundItemPostStateDropped)}",
                    TraceLevel.Info);
            }
            catch (Exception error) { ReportContextFault("M18GroundItemPostStateDiagnostic", error); }
        }
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

    private void RecordM18GroundItemPacket(M18GroundItemPacketTrace trace)
    {
        if (!_m18CandidateActive || trace.PacketId is not ((byte)PacketTypes.ItemDrop or
            (byte)PacketTypes.UpdateItemDrop or (byte)PacketTypes.SyncItemDespawn))
            return;
        int ordinal = Interlocked.Increment(ref _m18GroundItemPacketDiagnosticCount);
        if (ordinal > 256) return;
        try
        {
            string shape = trace.PacketId == (byte)PacketTypes.SyncItemDespawn ?
                "sync-item-despawn" : "world-item-frame";
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_F08_RAW packet={trace.PacketId} shape={shape} bytes={trace.PayloadLength} " +
                $"accountId={trace.AccountId} slot={trace.Session.Slot} id={trace.TargetSlot} " +
                $"requestPos={(trace.RequestCoordinatesComplete ? $"{trace.RequestX},{trace.RequestY}" : "unavailable(packet151-id-only)")} " +
                $"actorPos={(trace.ActorPositionSnapshotComplete ? $"{trace.ActorX},{trace.ActorY}" : "unavailable")} " +
                $"vel={trace.VelocityX},{trace.VelocityY} " +
                $"stack={trace.Stack} prefix={trace.Prefix} flags={trace.Flags} type={trace.Type} " +
                $"serverItem={trace.TargetExists}/{trace.TargetActive}/{trace.ServerTargetType}/" +
                $"{trace.ServerTargetStack}/grabbed={trace.TargetBeingGrabbed} " +
                $"attributed={trace.AttributionComplete} alreadyCanceled={trace.AlreadyCancelled} ordinal={ordinal}",
                TraceLevel.Info);
        }
        catch (Exception error)
        {
            ReportContextFault("M18GroundItemPacketDiagnostic", error);
        }
    }

    private void RecordM18StrikeObservation(M18NpcStrikeObservation observation,
        M18NpcStrikeQueueDecision decision)
    {
        var journal = _m18ObservationJournal;
        if (journal is null) return;
        journal.RecordStrike(new M18StrikeObservationRecord(
            SessionId(observation.Session),
            observation.AccountId,
            observation.Session.WorldEpoch,
            observation.WorldId,
            observation.Stage.ToString(),
            observation.TargetSlot,
            observation.TargetGeneration,
            observation.TargetType,
            observation.WireDamage,
            observation.ReceiverDamage,
            observation.TargetLife,
            observation.TargetLifeMax,
            observation.StageSnapshotComplete,
            observation.TargetActive,
            observation.TargetGenerationMatchesCurrent,
            observation.AttributionComplete,
            observation.LegalExceptionsExcluded,
            decision.Counted,
            decision.LowDamage,
            decision.ExtremeDamage,
            observation.ClientOrigin ? "client-packet28" : "unknown",
            decision.Action.ToString(),
            decision.Verdict.ToString(),
            decision.Reason,
            DateTimeOffset.UtcNow)
        {
            ButcherPatternDetected = decision.ButcherPatternDetected,
            ButcherPositionContextComplete = decision.ButcherPositionContextComplete,
            ButcherPreControlAtTarget = decision.ButcherPreControlAtTarget,
            ButcherControlJumpDetected = decision.ButcherControlJumpDetected,
            ButcherReturnPositionStable = decision.ButcherReturnPositionStable,
            ButcherRepeatedAttackSignature = decision.ButcherRepeatedAttackSignature,
            ButcherCrossTargetContinuation = decision.ButcherCrossTargetContinuation,
            ButcherCompletedTargetSequences = decision.ButcherCompletedTargetSequences,
            ButcherCurrentTargetStrikes = decision.ButcherCurrentTargetStrikes,
            SummonContextComplete = decision.SummonContextComplete,
            SummonMaintenanceBuffObserved = decision.SummonMaintenanceBuffObserved,
            MatchingSummonEntityObserved = decision.MatchingSummonEntityObserved,
            MatchingSummonEntityCount = decision.MatchingSummonEntityCount,
            InterceptionBoundary = decision.IsButcherPatternBlock || decision.IsExtremeDamageBlock
                ? "packet28-before-native-receiver"
                : "packet28-observation-before-native-receiver",
        });
    }

    private void RecordM18StrikeCompletion(M8NpcStrikeCompletion completion,
        M18NpcStrikeQueueDecision decision)
    {
        var postNative = new M18NpcStrikePostNativeObservation(
            completion.Session,
            completion.AccountId,
            completion.TargetSlot,
            completion.TargetGeneration,
            completion.TargetType,
            completion.WireDamage,
            completion.ReceiverDamage,
            completion.TargetLifeMax,
            completion.LifeBefore,
            completion.LifeAfter,
            completion.TargetFriendly,
            completion.TargetDummy,
            completion.InitialQueueDecision.Stage,
            completion.InitialQueueDecision.StageSnapshotComplete,
            completion.InitialQueueDecision.ButcherPositionContextComplete,
            completion.InitialQueueDecision.ButcherPreControlAtTarget,
            completion.InitialQueueDecision.ButcherControlJumpDetected,
            ClientOrigin: true,
            completion.AttributionComplete,
            completion.LegalExceptionsExcluded,
            completion.NativeStrikeEntryObserved,
            completion.LootMethodEntryObserved,
            completion.RelayAttemptObserved);
        try
        {
            _m18ObservationJournal?.RecordStrike(new M18StrikeObservationRecord(
                SessionId(completion.Session),
                completion.AccountId,
                completion.Session.WorldEpoch,
                completion.InitialQueueDecision.WorldId,
                postNative.Stage.ToString(),
                completion.TargetSlot,
                completion.TargetGeneration,
                completion.TargetType,
                completion.WireDamage,
                completion.ReceiverDamage,
                completion.LifeBefore,
                completion.TargetLifeMax,
                postNative.StageSnapshotComplete,
                completion.ActiveAfter,
                true,
                completion.AttributionComplete,
                completion.LegalExceptionsExcluded,
                decision.Counted,
                decision.LowDamage,
                decision.ExtremeDamage,
                "client-packet28-post-native-relay",
                decision.Action.ToString(),
                decision.Verdict.ToString(),
                decision.Reason,
                DateTimeOffset.UtcNow)
            {
                ButcherPatternDetected = decision.ButcherPatternDetected,
                ButcherPositionContextComplete = decision.ButcherPositionContextComplete,
                ButcherPreControlAtTarget = decision.ButcherPreControlAtTarget,
                ButcherControlJumpDetected = decision.ButcherControlJumpDetected,
                ButcherReturnPositionStable = decision.ButcherReturnPositionStable,
                ButcherRepeatedAttackSignature = decision.ButcherRepeatedAttackSignature,
                ButcherCrossTargetContinuation = decision.ButcherCrossTargetContinuation,
                ButcherCompletedTargetSequences = decision.ButcherCompletedTargetSequences,
                ButcherCurrentTargetStrikes = decision.ButcherCurrentTargetStrikes,
                PostNativeOneShotDeathEvidence = decision.PostNativeOneShotDeathEvidence,
                PostNativeSanctionCandidate = decision.PostNativeSanctionCandidate,
                SummonContextComplete = decision.SummonContextComplete,
                SummonMaintenanceBuffObserved = decision.SummonMaintenanceBuffObserved,
                MatchingSummonEntityObserved = decision.MatchingSummonEntityObserved,
                MatchingSummonEntityCount = decision.MatchingSummonEntityCount,
                InterceptionBoundary = "packet28-after-native-relay-post-death-evidence",
            });
        }
        catch (Exception error)
        {
            ReportContextFault("M18StrikeCompletionJournal", error);
        }

        try
        {
            ServerApi.LogWriter.PluginWriteLine(this,
                $"ANTICHEAT_M18_F06_POST_NATIVE accountId={completion.AccountId} slot={completion.Session.Slot} " +
                $"npc={completion.TargetSlot}/{completion.TargetGeneration}/{completion.TargetType} " +
                $"damage={completion.WireDamage}/{completion.ReceiverDamage} life={completion.LifeBefore}->{completion.LifeAfter}/{completion.TargetLifeMax} " +
                $"death={postNative.ObservedDeath} overkill={postNative.OverkillBeyondTargetMaximum} " +
                $"native={completion.NativeStrikeEntryObserved} loot={completion.LootMethodEntryObserved} relay={completion.RelayAttemptObserved} " +
                $"position={completion.InitialQueueDecision.ButcherPositionContextComplete}/" +
                $"{completion.InitialQueueDecision.ButcherPreControlAtTarget}/" +
                $"{completion.InitialQueueDecision.ButcherControlJumpDetected} " +
                $"postNativeEvidence={decision.PostNativeOneShotDeathEvidence} sanctionCandidate={decision.PostNativeSanctionCandidate}",
                TraceLevel.Info);
        }
        catch (Exception error)
        {
            ReportContextFault("M18StrikeCompletionDiagnostic", error);
        }

        if (!decision.IsPostNativeSanctionCandidate || _engine is null)
            return;
        var binding = GetBinding(completion.Session.Slot);
        if (binding is null || binding.Key != completion.Session)
            return;
        var input = new RuleInputContext(completion.Session, _targetRuntime.Fingerprint,
            TargetRuntime.Fingerprint, ParserComplete: true, SnapshotComplete: true,
            AttributionComplete: completion.AttributionComplete,
            ExceptionsExcluded: completion.LegalExceptionsExcluded, ClientOrigin: true);
        var result = M18NpcStrikeQueueRules.PostNativeObserve(input, postNative, decision);
        ApplyBusinessResult(28, false, binding, result);
    }

    private void RecordM18RewardObservation(M18ImportantItemRewardObservation observation)
    {
        var journal = _m18ObservationJournal;
        if (journal is null) return;
        foreach (var item in observation.Items)
            journal.RecordImportantItem(new M18ImportantItemObservationRecord(
                SessionId(observation.Session),
                observation.AccountId,
                observation.Session.WorldEpoch,
                observation.NpcSlot,
                observation.NpcGeneration,
                observation.NpcType,
                item.ItemIndex,
                item.ItemId,
                item.ItemName,
                item.Stack,
                item.PreviousStack,
                item.Delta,
                item.MaxStack,
                "NativeReward",
                observation.SourceContextKnown,
                observation.SourceAttributionComplete,
                true,
                true,
                false,
                "Observe",
                "Unknown",
                "native-reward-observation",
                observation.Boundary,
                DateTimeOffset.UtcNow));
    }

    private void RecordM18ImportantItemObservation(M18ImportantItemObservation observation,
        M18ImportantItemQueueDecision decision)
    {
        var journal = _m18ObservationJournal;
        if (journal is null || !decision.Important) return;
        string itemName = M18ImportantItemCatalog.Items.TryGetValue(decision.ItemId, out var name)
            ? name : "unknown";
        var record = new M18ImportantItemObservationRecord(
            SessionId(observation.Session),
            observation.AccountId,
            observation.Session.WorldEpoch,
            -1,
            0,
            0,
            observation.Slot,
            decision.ItemId,
            itemName,
            decision.Stack,
            decision.PreviousSlotStack,
            decision.Delta,
            0,
            observation.Source.ToString(),
            observation.SourceContextKnown,
            observation.SourceAttributionComplete,
            decision.Recorded,
            decision.GrowthObserved,
            decision.PossibleSorting,
            decision.Action.ToString(),
            decision.Verdict.ToString(),
            decision.Reason,
            "packet-time important-item observation; creator and ownership proof unavailable",
            DateTimeOffset.UtcNow)
        {
            PreviousTotal = decision.PreviousTotal,
            CurrentTotal = decision.CurrentTotal,
            PreviousSlotStack = decision.PreviousSlotStack,
            CurrentSlotStack = decision.Stack,
        };
        journal.RecordImportantItem(record);
    }

    private static string SessionId(SessionKey session)
        => $"{session.ServerRunId:N}/{session.WorldEpoch}/{session.Slot}/{session.Generation}";

    private void ReportContextFault(string producer, Exception exception)
    {
        try
        {
            bool first;
            lock (_reportedContextFaults)
                first = _reportedContextFaults.Count < 16 && _reportedContextFaults.Add(producer);
            if (!first) return;
            ServerApi.LogWriter.PluginWriteLine(this, "AntiCheat context disabled after integrity fault: "
                + producer + " " + exception.GetType().Name, TraceLevel.Error);
            if (producer == "Vitals")
            {
                string detail = exception.ToString();
                if (detail.Length > 8192) detail = detail[..8192] + " [truncated]";
                ServerApi.LogWriter.PluginWriteLine(this,
                    "AntiCheat Vitals fault detail: " + detail, TraceLevel.Error);
            }
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
            ServerApi.Hooks.GamePostUpdate.Deregister(this, OnPostUpdate);
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
            Cleanup("M18ObservationJournal", () => _m18ObservationJournal?.Dispose());
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
            Cleanup("LockHealthCandidate", () => _lockHealth?.Dispose());
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
