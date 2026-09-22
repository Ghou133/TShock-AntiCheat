using System.Collections;
using System.Reflection;
using System.Text.Json;
using AntiCheat.Core;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Isolated E01 native-field preparation, real raw receive and passive result witness.
/// No rule evaluation, sanction, completeness, clock, or qualification is supplied by this fixture.</summary>
public sealed partial class GameplayScaffold
{
    private static readonly FieldInfo[] M17ProgressionFields = new (Type Type, string Name)[]
    {
        (typeof(Main), "hardMode"), (typeof(Main), "drunkWorld"), (typeof(Main), "getGoodWorld"),
        (typeof(Main), "zenithWorld"), (typeof(Main), "tenthAnniversaryWorld"), (typeof(Main), "remixWorld"),
        (typeof(NPC), "downedSlimeKing"), (typeof(NPC), "downedBoss1"), (typeof(NPC), "downedBoss3"),
        (typeof(NPC), "downedDeerclops"), (typeof(NPC), "downedQueenSlime"), (typeof(NPC), "downedMechBoss1"),
        (typeof(NPC), "downedMechBoss2"), (typeof(NPC), "downedMechBoss3"), (typeof(NPC), "downedPlantBoss"),
        (typeof(NPC), "downedGolemBoss"), (typeof(NPC), "downedAncientCultist"), (typeof(NPC), "downedMoonlord"),
        (typeof(NPC), "downedFishron"), (typeof(NPC), "downedEmpressOfLight")
    }.Select(value => value.Type.GetField(value.Name) ?? throw new MissingFieldException(value.Name)).ToArray();
    private TSPlayer? m17ProgressionActor;
    private int m17ProgressionSlot;
    private bool[]? m17ProgressionOriginal;
    private int m17ProgressionOriginalWorld;
    private object? m17ProgressionPlugin, m17ProgressionBusiness;
    private ILHook? m17ProgressionResultHook;
    private bool m17ProgressionSubscribed, m17ProgressionRestored;
    private long m17ProgressionSequence, m17ProgressionFaults;
    private M17ProgressionArm? m17ProgressionArm;
    private M17ProgressionPending? m17ProgressionPending;
    private object? m17ProgressionLast, m17ProgressionLifecycle;
    private sealed record M17ProgressionArm(string Label, int Field, byte Packet, SessionKey Session);
    private sealed record M17ProgressionCache(long Tick, int Stable, long Revision, long Epoch, int WorldId, int Facts);
    private sealed class M17ProgressionPending(GetDataEventArgs args, M17ProgressionArm arm, bool[] flags, int world,
        M17ProgressionCache before, int thread, string payload)
    {
        public GetDataEventArgs Args { get; } = args;
        public M17ProgressionArm Arm { get; } = arm;
        public bool[] Flags { get; } = flags;
        public int World { get; } = world;
        public M17ProgressionCache Before { get; } = before;
        public int Thread { get; } = thread;
        public string Payload { get; } = payload;
        public List<object> Results { get; } = new(2);
    }

    private static bool[] ReadM17ProgressionFlags() => M17ProgressionFields.Select(field => (bool)field.GetValue(null)!).ToArray();
    private bool CurrentM17ProgressionActor() => m17ProgressionActor is { Active: true } actor &&
        ReferenceEquals(TShock.Players[actor.Index], actor) && ReferenceEquals(Main.player[actor.Index], actor.TPlayer);
    private object? M17ProgressionBinding() => m17ProgressionActor is null ? null :
        ((Array)m17ProgressionPlugin!.GetType().GetField("_bindings", PrivateM5)!.GetValue(m17ProgressionPlugin)!).GetValue(m17ProgressionActor.Index);
    private SessionKey? M17ProgressionSession() => M17ProgressionBinding() is { } binding ?
        (SessionKey)binding.GetType().GetProperty("Key")!.GetValue(binding)! : null;
    private AntiCheatEngine M17ProgressionEngine() => (AntiCheatEngine)m17ProgressionPlugin!.GetType()
        .GetField("_engine", PrivateM5)!.GetValue(m17ProgressionPlugin)!;
    private M17ProgressionCache ReadM17ProgressionCache()
    {
        var business = m17ProgressionBusiness!;
        object Value(string name) => business.GetType().GetField(name, PrivateM5)!.GetValue(business)!;
        object facts = Value("worldFacts");
        return new((long)Value("tick"), (int)Value("worldStableTicks"), (long)Value("worldRevision"),
            (long)Value("capturedWorldEpoch"), (int)Value("capturedWorldId"), (int)facts.GetType().GetProperty("Count")!.GetValue(facts)!);
    }

    private void M17ProgressionWorldCommand(string[] args)
    {
        Require(args.Length >= 1, "Use qa_m17_e01 bind <actor>, arm <label> <field:-1..20> <5|13>, state, epoch, or restore.");
        if (args[0] == "bind")
        {
            Require(args.Length == 2 && !CurrentM17ProgressionActor() && m17ProgressionArm is null && m17ProgressionPending is null,
                "Bind only a fresh real actor after the prior transport has left.");
            var actor = ResolvePlayer(args[1]);
            Require(actor.IsLoggedIn && actor.HasSentInventory && !actor.IgnoreSSCPackets &&
                !actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc), "Ordinary authenticated SSC actor required.");
            m17ProgressionPlugin = M5Plugin();
            m17ProgressionBusiness = m17ProgressionPlugin.GetType().GetField("_business", PrivateM5)!.GetValue(m17ProgressionPlugin);
            Require(m17ProgressionBusiness?.GetType().GetField("worldFactSnapshot", PrivateM5) is not null,
                "This scenario requires the actual E01 candidate, not the earlier DLL.");
            if (m17ProgressionOriginal is null)
            {
                m17ProgressionOriginal = ReadM17ProgressionFlags(); m17ProgressionOriginalWorld = Main.worldID;
                try
                {
                    // Explicit special-world setup is a legal Red Potion counterexample.
                    Main.drunkWorld = true; Main.getGoodWorld = false; Main.zenithWorld = false; Main.tenthAnniversaryWorld = false;
                    var apply = m17ProgressionPlugin.GetType().GetMethod("ApplyBusinessResult", PrivateM5)!;
                    m17ProgressionResultHook = InstallM17ProgressionResultWitness(apply);
                    ServerApi.Hooks.NetGetData.Register(this, PrepareM17ProgressionBefore, 2001);
                    ServerApi.Hooks.NetGetData.Register(this, ObserveM17ProgressionAfter, -1001);
                    m17ProgressionSubscribed = true;
                }
                catch { DisposeM17ProgressionWorld(); throw; }
            }
            m17ProgressionActor = actor;
            // Reconnection preserves the previously granted SSC item. No asset is erased.
            m17ProgressionSlot = Array.FindIndex(actor.TPlayer.inventory, 0, 10, item => item.type == ItemID.RedPotion && item.stack == 1);
            if (m17ProgressionSlot < 0)
            {
                m17ProgressionSlot = Array.FindIndex(actor.TPlayer.inventory, 0, 10, item => item.IsAir);
                Require(m17ProgressionSlot >= 0, "An empty native hotbar slot is required for the explicit isolated grant.");
                actor.TPlayer.inventory[m17ProgressionSlot].SetDefaults(ItemID.RedPotion);
                actor.TPlayer.inventory[m17ProgressionSlot].stack = 1;
                actor.PlayerData.CopyCharacter(actor);
                NetMessage.SendData(5, number: actor.Index, number2: m17ProgressionSlot);
            }
            Require(M17ProgressionSession() is not null, "The production hook must have bound this authenticated transport.");
            Record("m17-e01-preparation", new { actor.Name, actor.Index, slot = m17ProgressionSlot,
                fields = "native special-world preparation only; no client consumption or complete world load claimed", productPremiseWritten = false });
        }
        else if (args[0] == "arm")
        {
            Require(args.Length == 4 && args[1].Length is > 0 and <= 48 && int.TryParse(args[2], out _) && byte.TryParse(args[3], out _), "Invalid bounded E01 arm.");
            int field = int.Parse(args[2]); byte packet = byte.Parse(args[3]);
            Require(CurrentM17ProgressionActor() && !m17ProgressionRestored && m17ProgressionArm is null && m17ProgressionPending is null &&
                m17ProgressionSequence < 64 && field is >= -1 and <= 20 && packet is 5 or 13, "One bounded preparation at a time.");
            m17ProgressionArm = new(args[1], field, packet, M17ProgressionSession()!.Value);
        }
        else if (args[0] == "epoch")
        {
            Require(args.Length == 1 && m17ProgressionLifecycle is null && m17ProgressionOriginal is not null &&
                m17ProgressionArm is null && m17ProgressionPending is null && !TShock.Players.Take(255).Any(player => player is { Active: true }),
                "Lifecycle hook preparation requires all real player transports closed and no pending raw preparation.");
            var disconnect = ReadM17WorldHandlers(ServerApi.Hooks.GameWorldDisconnect);
            var connect = ReadM17WorldHandlers(ServerApi.Hooks.GameWorldConnect);
            Require(disconnect.Length == 1 && connect.Length == 1, "Unknown live world-hook subscribers prevent this lifecycle preparation.");
            long before = M17ProgressionEngine().CurrentWorldEpoch;
            typeof(HookManager).GetMethod("InvokeGameWorldDisconnect", PrivateM5)!.Invoke(ServerApi.Hooks, null);
            long afterDisconnect = M17ProgressionEngine().CurrentWorldEpoch; var disconnected = ReadM17ProgressionCache();
            typeof(HookManager).GetMethod("InvokeGameWorldConnect", PrivateM5)!.Invoke(ServerApi.Hooks, null);
            m17ProgressionLifecycle = new { before, afterDisconnect, afterConnect = M17ProgressionEngine().CurrentWorldEpoch,
                disconnected, connected = ReadM17ProgressionCache(), disconnectHandlers = disconnect, connectHandlers = connect,
                kind = "actual TSAPI lifecycle Hook invocation with observed current subscribers; no world file load", fullWorldFileLoad = false };
        }
        else if (args[0] == "restore")
        { Require(args.Length == 1 && m17ProgressionPending is null, "Restore only outside a raw callback."); RestoreM17ProgressionWorld(); }
        else Require(args.Length == 1 && args[0] == "state", "Unknown E01 command.");
        WriteM17ProgressionWorldState();
    }

    private ILHook InstallM17ProgressionResultWitness(MethodInfo apply)
    {
        var parameters = apply.GetParameters();
        Require(!apply.IsStatic && parameters.Length == 5 && parameters[0].ParameterType == typeof(byte) &&
            parameters[3].ParameterType == typeof(BusinessRuleResult), "Unexpected actual business-result method signature.");
        return new(apply, il =>
        {
            // MonoMod's DMD makes the instance explicit at argument 0. Its parameter
            // table therefore includes this; entry 3 is Binding, not BusinessRuleResult.
            Require(!il.Method.HasThis && il.Method.Parameters.Count == 6 &&
                il.Method.Parameters[4].ParameterType.FullName == typeof(BusinessRuleResult).FullName,
                "Unexpected result-witness DMD argument layout.");
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1); cursor.Emit(OpCodes.Ldarg_3); cursor.Emit(OpCodes.Ldarg, 4);
            cursor.EmitDelegate<Action<object, byte, object, BusinessRuleResult>>(ObserveM17ProgressionResult);
        });
    }

    private object[] ReadM17WorldHandlers(object collection)
    {
        var registrations = ((IEnumerable)collection).Cast<object>().Take(9).ToArray();
        Require(registrations.Length <= 8, "World-hook subscriber inventory exceeds the fixed observation cap.");
        return registrations.Select(registration =>
        {
            var type = registration.GetType(); var owner = type.GetProperty("Registrator")!.GetValue(registration)!;
            var handler = (Delegate)type.GetProperty("Handler")!.GetValue(registration)!;
            Require(ReferenceEquals(owner, m17ProgressionPlugin) && ReferenceEquals(handler.Target, m17ProgressionPlugin) && handler.Method.Name == "OnWorldChanged",
                "An unaudited actual world-hook subscriber is present; no lifecycle callback was invoked.");
            return (object)new { owner = owner.GetType().FullName, assembly = owner.GetType().Assembly.FullName,
                mvid = owner.GetType().Module.ModuleVersionId, method = handler.Method.Name, priority = type.GetProperty("Priority")!.GetValue(registration) };
        }).ToArray();
    }

    private void PrepareM17ProgressionBefore(GetDataEventArgs args)
    {
        var arm = m17ProgressionArm;
        if (arm is null || (byte)args.MsgID != arm.Packet || args.Msg?.whoAmI != m17ProgressionActor?.Index) return;
        if (!CurrentM17ProgressionActor() || M17ProgressionSession() != arm.Session) { m17ProgressionFaults++; m17ProgressionArm = null; return; }
        byte[] buffer = args.Msg!.readBuffer; int offset = args.Index;
        if (args.Length < (arm.Packet == 13 ? 14 : 9) || buffer[offset] != arm.Session.Slot ||
            (arm.Packet == 13 ? buffer[offset + 5] != m17ProgressionSlot : BitConverter.ToInt16(buffer, offset + 1) != m17ProgressionSlot)) return;
        m17ProgressionArm = null;
        try
        {
            Require(m17ProgressionPending is null, "Raw preparation unexpectedly reentered.");
            m17ProgressionPending = new(args, arm, ReadM17ProgressionFlags(), Main.worldID, ReadM17ProgressionCache(),
                Environment.CurrentManagedThreadId, Convert.ToHexString(buffer.AsSpan(offset, Math.Min(args.Length, 24))));
            m17ProgressionSequence++;
            if (arm.Field is >= 0 and < 20) M17ProgressionFields[arm.Field].SetValue(null, !m17ProgressionPending.Flags[arm.Field]);
            if (arm.Field == 20) Main.ActiveWorldFileData.WorldId = Main.worldID == int.MaxValue ? Main.worldID - 1 : Main.worldID + 1;
        }
        catch { m17ProgressionFaults++; if (m17ProgressionPending is { } pending) RestoreM17ProgressionMutation(pending); m17ProgressionPending = null; }
    }
    private void ObserveM17ProgressionResult(object plugin, byte packet, object binding, BusinessRuleResult result)
    {
        var pending = m17ProgressionPending;
        if (pending is null || !ReferenceEquals(plugin, m17ProgressionPlugin) || packet != pending.Arm.Packet || result.RuleId != "PG-NAT-002") return;
        try
        {
            Require(pending.Results.Count < 4 && (SessionKey)binding.GetType().GetProperty("Key")!.GetValue(binding)! == pending.Arm.Session,
                "E01 result must belong to this actual bounded request/session.");
            pending.Results.Add(new { result.RuleId, result.Version, action = result.Action.ToString(), verdict = result.Verdict.ToString(),
                result.Reason, result.PredicateSatisfied, result.PrerequisitesComplete, result.Facts,
                actualFields = ReadM17ProgressionFlags(), worldId = Main.worldID, cache = ReadM17ProgressionCache(),
                thread = Environment.CurrentManagedThreadId });
        }
        catch { m17ProgressionFaults++; }
    }
    private static void RestoreM17ProgressionMutation(M17ProgressionPending pending)
    {
        if (pending.Arm.Field is >= 0 and < 20) M17ProgressionFields[pending.Arm.Field].SetValue(null, pending.Flags[pending.Arm.Field]);
        if (pending.Arm.Field == 20) Main.ActiveWorldFileData.WorldId = pending.World;
    }
    private void ObserveM17ProgressionAfter(GetDataEventArgs args)
    {
        var pending = m17ProgressionPending;
        if (pending is null || !ReferenceEquals(args, pending.Args)) return;
        try
        {
            var after = ReadM17ProgressionCache();
            try { RestoreM17ProgressionMutation(pending); }
            finally
            {
                var restored = ReadM17ProgressionFlags();
                m17ProgressionLast = new { sequence = m17ProgressionSequence, pending.Arm.Label, pending.Arm.Field, pending.Arm.Packet,
                    pending.Arm.Session, pending.Payload, before = pending.Before, after, restoredCache = ReadM17ProgressionCache(),
                    flagsBefore = pending.Flags, flagsRestored = restored, worldBefore = pending.World, worldRestored = Main.worldID,
                    restored = restored.SequenceEqual(pending.Flags) && Main.worldID == pending.World,
                    sameThread = pending.Thread == Environment.CurrentManagedThreadId, sameTick = pending.Before.Tick == after.Tick,
                    handled = args.Handled, results = pending.Results.ToArray(), preparation = "one native-field change before product raw hook; finally restored after real product callback" };
                Record("m17-e01-raw-result", m17ProgressionLast);
            }
        }
        catch { m17ProgressionFaults++; }
        finally
        {
            // A diagnostic read can throw before the normal restored-state record is made.
            // The owned native preparation still has to be removed on that path.
            try { RestoreM17ProgressionMutation(pending); }
            catch { m17ProgressionFaults++; }
            finally { m17ProgressionPending = null; }
        }
    }

    private void WriteM17ProgressionWorldState()
    {
        Require(m17ProgressionBusiness is not null, "Bind the actual E01 candidate first.");
        var actor = m17ProgressionActor!; var engine = M17ProgressionEngine();
        var catalog = ((IEnumerable<string>)m17ProgressionBusiness!.GetType().GetProperty("ProgressionRuleIds")!.GetValue(m17ProgressionBusiness)!).ToArray();
        var policies = (IReadOnlyDictionary<string, BusinessRulePolicy>)typeof(AntiCheatEngine).GetField("businessPolicies", PrivateM5)!.GetValue(engine)!;
        var payload = new { utc = DateTimeOffset.UtcNow, actor = actor.Name, account = actor.Account?.ID, slot = actor.Index,
            itemSlot = m17ProgressionSlot, position = new { x = actor.TPlayer.position.X, y = actor.TPlayer.position.Y }, currentActor = CurrentM17ProgressionActor(),
            session = M17ProgressionSession(), epoch = engine.CurrentWorldEpoch, scope = m17ProgressionPlugin!.GetType().GetField("_scope", PrivateM5)!.GetValue(m17ProgressionPlugin)!.ToString(),
            catalog = catalog.Select(id => new { id, qualification = policies[id].Qualification.ToString() }),
            witnessInstalled = m17ProgressionSubscribed && m17ProgressionResultHook is not null,
            fields = M17ProgressionFields.Select(field => field.DeclaringType!.Name + "." + field.Name), values = ReadM17ProgressionFlags(),
            cache = ReadM17ProgressionCache(), armed = m17ProgressionArm, sequence = m17ProgressionSequence,
            faults = m17ProgressionFaults, last = m17ProgressionLast, lifecycle = m17ProgressionLifecycle,
            restored = m17ProgressionRestored, originalValues = m17ProgressionOriginal, originalWorld = m17ProgressionOriginalWorld, actualWorld = Main.worldID };
        WriteM16ItemSnapshotFile(Path.Combine(output!, "m17-progression-world-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
    private void RestoreM17ProgressionWorld()
    {
        m17ProgressionArm = null;
        if (m17ProgressionOriginal is null || m17ProgressionRestored) return;
        try
        {
            for (int index = 0; index < M17ProgressionFields.Length; index++) M17ProgressionFields[index].SetValue(null, m17ProgressionOriginal[index]);
        }
        finally
        {
            Main.ActiveWorldFileData.WorldId = m17ProgressionOriginalWorld;
            m17ProgressionRestored = ReadM17ProgressionFlags().SequenceEqual(m17ProgressionOriginal) && Main.worldID == m17ProgressionOriginalWorld;
            Require(m17ProgressionRestored, "Native world preparation restoration did not read back exactly.");
        }
    }
    private void DisposeM17ProgressionWorld()
    {
        try
        {
            if (m17ProgressionPending is { } pending) RestoreM17ProgressionMutation(pending);
            RestoreM17ProgressionWorld();
        }
        finally
        {
            if (m17ProgressionSubscribed)
            { ServerApi.Hooks.NetGetData.Deregister(this, PrepareM17ProgressionBefore); ServerApi.Hooks.NetGetData.Deregister(this, ObserveM17ProgressionAfter); }
            m17ProgressionSubscribed = false; m17ProgressionResultHook?.Dispose(); m17ProgressionResultHook = null; m17ProgressionPending = null;
        }
    }
}
