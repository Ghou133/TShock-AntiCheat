using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Plugin.TShock;

/// <summary>
/// Tracks bounded maxima observed in self-directed send attempts (delivery is not inferred). A peer relay excluding that client
/// cannot authorize its later declarations. Nothing here accepts Main.player/SSC values as independent legality.
/// </summary>
public sealed class M4VitalContexts : IDisposable
{
    private sealed class State(SessionKey session, bool fromStart)
    {
        public SessionKey Session { get; } = session;
        public bool FromStart { get; } = fromStart;
        public int LifeCeiling = M4VitalRules.BaselineLifeMaximum, ManaCeiling = M4VitalRules.BaselineManaMaximum;
        public int LifeExports, ManaExports;
        public bool LifeHealthy = true, ManaHealthy = true;
        public bool HurtPluginContractComplete = true;
    }

    private readonly State?[] states = new State?[256];
    private readonly string fingerprint;
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? targetAtSlot;
    private bool installed, failed;
    private volatile bool hurtPluginObservationFailed;
    private int updateThread;
    public Action<Exception>? IntegrityFault { get; set; }

    public M4VitalContexts(string fingerprint) => this.fingerprint = fingerprint;

    /// <summary>Install before accepting connections so initial SSC exports are covered; late sessions stay Unknown.</summary>
    public void Install(Func<int, (SessionSnapshot? Session, TSPlayer? Player)> lookup)
    {
        if (installed || failed) return;
        targetAtSlot = lookup;
        HookEvents.Terraria.NetMessage.SendData += OnSendData;
        installed = true;
    }

    public void Dispose()
    {
        if (installed) HookEvents.Terraria.NetMessage.SendData -= OnSendData;
        installed = false;
        targetAtSlot = null;
        Array.Clear(states);
    }

    public void Tick()
    {
        if (targetAtSlot is null) return;
        updateThread = Environment.CurrentManagedThreadId;
        bool known = KnownPluginSet();
        for (int slot = 0; slot < states.Length; slot++)
        {
            var target = targetAtSlot(slot);
            if (target.Session is not { } session || target.Player is not { } actor)
                states[slot] = null;
            else EnsureState(session.Key, actor, known);
        }
    }

    private State EnsureState(SessionKey session, TSPlayer actor, bool? knownPlugins = null)
    {
        if (states[session.Slot] is not { } state || state.Session != session)
            states[session.Slot] = state = new(session, !actor.ReceivedInfo);
        // A removed plugin can leave client-side state behind: a raw packet3 may have
        // reassigned the client's local player slot, which later native self-hurt uses.
        // Only this packet-role contract is withdrawn for the affected session.
        state.HurtPluginContractComplete &= knownPlugins ?? KnownPluginSet();
        return state;
    }

    private static bool KnownPluginSet() => ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(ServerTShock) ||
        x.Plugin.GetType() == typeof(AntiCheatPlugin));

    private void OnSendData(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (failed || !installed || !args.ContinueExecution || Main.netMode != 2 || targetAtSlot is null) return;
        // Observe every outgoing kind before the vital-export filter: a plugin can send
        // packet3 and unload between updates, leaving the client's assigned role changed.
        try
        {
            if (!KnownPluginSet())
            {
                if (Environment.CurrentManagedThreadId != updateThread) hurtPluginObservationFailed = true;
                else for (int slot = 0; slot < states.Length; slot++)
                {
                    // A newly bound connection may not have reached a Tick or vital export.
                    // Include the actual current binding before its plugin can disappear.
                    var target = targetAtSlot(slot);
                    if (target.Session is { } session && target.Player is { } actor)
                        EnsureState(session.Key, actor, false);
                    else if (states[slot] is { } tracked) tracked.HurtPluginContractComplete = false;
                }
            }
        }
        catch { hurtPluginObservationFailed = true; }
        if (!Main.ServerSideCharacter || args.msgType is not (16 or 42) || args.number is < 0 or >= 256) return;
        // Vanilla C2S life relays use remote=-1, ignore=sender. That is not a self export or permission grant.
        if (!(args.remoteClient == args.number || args.remoteClient == -1 && args.ignoreClient != args.number)) return;
        if (Environment.CurrentManagedThreadId != updateThread)
        {
            failed = true; // Cannot read mutable game state on an unverified execution context.
            IntegrityFault?.Invoke(new InvalidOperationException("Vital self export executed outside the verified update context."));
            return;
        }
        try
        {
            var target = targetAtSlot(args.number);
            if (target.Session is not { } session || target.Player is not { } actor) return;
            var state = EnsureState(session.Key, actor);
            int raw = args.msgType == 16 ? actor.TPlayer.statLifeMax : actor.TPlayer.statManaMax;
            if (raw is < 0 or > short.MaxValue)
            {
                if (args.msgType == 16) state.LifeHealthy = false; else state.ManaHealthy = false;
                return;
            }
            int upper = M4VitalRules.NativeUpperBoundAfterServerExport(args.msgType == 16 ? M4VitalKind.Life : M4VitalKind.Mana, raw);
            if (args.msgType == 16)
            {
                state.LifeCeiling = Math.Max(state.LifeCeiling, upper);
                if (state.LifeExports < int.MaxValue) state.LifeExports++;
            }
            else
            {
                state.ManaCeiling = Math.Max(state.ManaCeiling, upper);
                if (state.ManaExports < int.MaxValue) state.ManaExports++;
            }
        }
        catch (Exception exception)
        {
            failed = true;
            IntegrityFault?.Invoke(exception);
        }
    }

    public BusinessRuleResult Evaluate(M4VitalObservation observation, SessionKey session, TSPlayer actor)
    {
        if (session.Slot is < 0 or >= 256) throw new ArgumentOutOfRangeException(nameof(session));
        bool known = KnownPluginSet();
        State state = EnsureState(session, actor, known);
        if (observation.Kind == M4VitalKind.HurtDeclaration)
        {
            bool synchronizing = !actor.IsLoggedIn || !actor.HasSentInventory || actor.IgnoreSSCPackets;
            bool contextReady = installed && !failed && Environment.CurrentManagedThreadId == updateThread;
            // This packet-local relation needs neither historic vital values nor a victim's current hostile flag.
            var hurtInput = new RuleInputContext(session, fingerprint, fingerprint, true, contextReady,
                actor.Index == session.Slot, known && state.HurtPluginContractComplete && !hurtPluginObservationFailed && !synchronizing);
            var hurtResult = M5VitalsRules.EvaluateHurt(observation, hurtInput,
                known && state.HurtPluginContractComplete && !hurtPluginObservationFailed, synchronizing);
            return hurtResult with { Facts = hurtResult.Facts
                .SetItem("knownPluginSet", known.ToString())
                .Add("hurtPluginContractComplete", state.HurtPluginContractComplete.ToString())
                .Add("hurtPluginObservationHealthy", (!hurtPluginObservationFailed).ToString()) };
        }
        bool permission = observation.Kind switch
        {
            M4VitalKind.Life => actor.HasPermission(Permissions.ignorehp) || ServerTShock.Config.Settings.MaxHP > 500,
            M4VitalKind.Mana => actor.HasPermission(Permissions.ignoremp) || ServerTShock.Config.Settings.MaxMP > 200,
            _ => false
        };
        bool healthy = !failed && installed && Environment.CurrentManagedThreadId == updateThread &&
            (observation.Kind == M4VitalKind.Life ? state.LifeHealthy : state.ManaHealthy);
        // This slice's server-export premise is audited only for SSC. A non-SSC deployment does not
        // silently gain a proof contract just because it does not use the export collector.
        bool syncing = !Main.ServerSideCharacter || !actor.IsLoggedIn || actor.IgnoreSSCPackets || !actor.HasSentInventory;
        int ceiling = observation.Kind == M4VitalKind.Life ? state.LifeCeiling : state.ManaCeiling;
        var input = new RuleInputContext(session, fingerprint, fingerprint, true,
            installed && !failed && state.FromStart, actor.Index == session.Slot,
            healthy && known && !permission && !syncing);
        var result = M4VitalRules.Evaluate(observation, new(input, M4VitalRules.ContractVersion,
            state.FromStart, syncing, known, permission, ceiling, healthy));
        int exports = observation.Kind == M4VitalKind.Life ? state.LifeExports : state.ManaExports;
        return result with { Facts = result.Facts
            .Add("serverExportSource", exports == 0 ? "native-baseline-no-self-export" : "target-SendData-self-SSC-attempt")
            .Add("serverExportCount", exports.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("historyFromConnectionStart", state.FromStart.ToString())
            .Add("knownPluginSet", known.ToString())
            .Add("sscEnabled", Main.ServerSideCharacter.ToString())
            .Add("synchronizing", syncing.ToString()) };
    }
}
