using System.Reflection;
using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>Gate the actual selected native credential command delegate after CanRun and
/// immediately before invocation. Aliases and player-controlled command text are not identity.</summary>
public sealed class M17CommandWorkGuard(TimeProvider clock,
    Func<TSPlayer, (SessionKey Session, long? Account)?> currentRequest,
    M17CommandWorkOptions? options = null) : IDisposable
{
    private readonly M17CommandWorkBudget budget = new(clock, options);
    private IReadOnlyDictionary<CommandDelegate, M17CommandWorkKind> native = ImmutableDictionary<CommandDelegate, M17CommandWorkKind>.Empty;
    private ILHook? commandHook;
    private volatile bool failed;
    public bool Healthy => !failed && commandHook is not null;
    public Action<Exception>? IntegrityFault { get; init; }
    public long Admitted => budget.Admitted;
    public long Blocked => budget.Blocked;
    public int AccountBucketCount => budget.AccountBucketCount;
    public void Install()
    {
        if (failed || commandHook is not null) return;
        if (typeof(Terraria.Netplay).Assembly.GetName().Version != new Version(1, 4, 5, 8) ||
            typeof(Commands).Assembly.GetName().Version != new Version(6, 1, 0, 0))
            throw new NotSupportedException("Credential work admission requires the audited1.4.5.8/TShock6.1 command contract.");
        var methods = ImmutableDictionary.CreateBuilder<CommandDelegate, M17CommandWorkKind>();
        foreach (var (method, kind) in new[] { ("AttemptLogin", M17CommandWorkKind.Login), ("RegisterUser", M17CommandWorkKind.Register),
            ("PasswordUser", M17CommandWorkKind.PasswordChange), ("ManageUsers", M17CommandWorkKind.AccountAdministration) })
        {
            var info = typeof(Commands).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic, [typeof(CommandArgs)])
                ?? throw new MissingMethodException(typeof(Commands).FullName, method);
            methods.Add((CommandDelegate)info.CreateDelegate(typeof(CommandDelegate)), kind);
        }
        native = methods.ToImmutable();
        try { commandHook = new ILHook(typeof(Command).GetMethod(nameof(Command.Run), [typeof(CommandArgs)])!, Instrument); }
        catch { Dispose(); throw; }
    }
    private void Instrument(ILContext il)
    {
        static bool IsCall(Instruction instruction, string type, string name) => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == type && method.Name == name;
        if (il.Body.Instructions.Count(i => IsCall(i, "TShockAPI.Command", "CanRun")) != 1 ||
            il.Body.Instructions.Count(i => IsCall(i, "TShockAPI.Command", "get_CommandDelegate")) != 1 ||
            il.Body.Instructions.Count(i => IsCall(i, "TShockAPI.CommandDelegate", "Invoke")) != 1)
            throw new NotSupportedException("Audited Command.Run permission/delegate landmarks changed.");
        var cursor = new ILCursor(il);
        cursor.GotoNext(i => IsCall(i, "TShockAPI.CommandDelegate", "Invoke"));
        // The actual delegate value and args are already on the stack. Replacing precisely
        // this call closes the mutable property/alias gap without reading it a second time.
        cursor.Remove(); cursor.EmitDelegate<Action<CommandDelegate, CommandArgs>>(Invoke);
    }
    private void Invoke(CommandDelegate handler, CommandArgs args)
    {
        bool allowed = true;
        try
        {
            if (Healthy && native.TryGetValue(handler, out var kind) && !NativeLoginLimitAlreadyReached(kind, args.Player) &&
                currentRequest(args.Player) is { } request)
                allowed = budget.Consume(request.Session, request.Account, kind).Allowed;
        }
        catch (Exception error)
        {
            failed = true;
            try { IntegrityFault?.Invoke(error); } catch { }
        }
        // Native exceptions retain Command.Run's existing catch. An observer fault does
        // not catch/retry a handler that already ran, nor turn it into a login failure.
        if (allowed) handler(args);
    }
    private static bool NativeLoginLimitAlreadyReached(M17CommandWorkKind kind, TSPlayer actor) =>
        kind == M17CommandWorkKind.Login && TShockAPI.TShock.Config.Settings.MaximumLoginAttempts != -1 &&
        actor.LoginAttempts > TShockAPI.TShock.Config.Settings.MaximumLoginAttempts;
    public void Forget(SessionKey session) => budget.Forget(session);
    public void Dispose() { var hook = commandHook; commandHook = null; hook?.Dispose(); native = ImmutableDictionary<CommandDelegate, M17CommandWorkKind>.Empty; }
}
