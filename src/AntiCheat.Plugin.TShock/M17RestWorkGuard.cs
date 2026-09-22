using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using AntiCheat.Rules;
using HttpServer;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Rests;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>Wrap only the actual selected REST callback, under the exact native
/// SecureRest permission-success invocation. Does not claim an account or gameplay session.</summary>
public sealed class M17RestWorkGuard(TimeProvider clock, SecureRest authority, RestManager manager,
    M17RestWorkOptions? options = null) : IDisposable
{
    private sealed record Request(M17RestWorkGuard Owner, SecureRestCommand Command, IRequest NativeRequest, IHttpContext Context);
    [ThreadStatic] private static Request? current;
    private readonly M17RestWorkBudget budget = new(clock, options);
    private IImmutableSet<RestCommandD> selected = ImmutableHashSet<RestCommandD>.Empty;
    private ILHook? permissionHook, callbackHook;
    private volatile bool failed;
    public bool Healthy => !failed && permissionHook is not null && callbackHook is not null;
    public long Admitted => budget.Admitted;
    public long Blocked => budget.Blocked;
    public int SourceCount => budget.SourceCount;
    public string? VerifiedHttpServerSha256 { get; private set; }
    public const string HttpServerSha256 = "FE0E9B8B76963E706713CD3F229A694B7BC685EA78F495D5CE4E3D7EA75F9C38";
    public Action<Exception>? IntegrityFault { get; init; }

    public void Install()
    {
        if (failed || permissionHook is not null || callbackHook is not null) return;
        VerifiedHttpServerSha256 = ValidateHttpServerDependency(typeof(IHttpContext).Assembly);
        if (typeof(Terraria.Netplay).Assembly.GetName().Version != new Version(1, 4, 5, 8) ||
            typeof(Commands).Assembly.GetName().Version != new Version(6, 1, 0, 0))
            throw new NotSupportedException("REST work admission requires the audited 1.4.5.8/TShock6.1 contract.");
        selected = new[] { "UserCreateV2", "UserUpdateV2" }.Select(name =>
            (RestCommandD)(typeof(RestManager).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, [typeof(RestRequestArgs)])
                ?? throw new MissingMethodException(typeof(RestManager).FullName, name)).CreateDelegate(typeof(RestCommandD), manager)).ToImmutableHashSet();
        try
        {
            callbackHook = new ILHook(typeof(SecureRestCommand).GetMethod(nameof(SecureRestCommand.Execute),
                [typeof(RestVerbs), typeof(IParameterCollection), typeof(SecureRest.TokenData), typeof(IRequest), typeof(IHttpContext)])!, InstrumentCallback);
            permissionHook = new ILHook(typeof(SecureRest).GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.NonPublic)!, InstrumentPermission);
        }
        catch { Dispose(); throw; }
    }
    private static string ValidateHttpServerDependency(Assembly assembly)
    {
        if (assembly.IsDynamic || assembly.GetName().Name != "HttpServer" || assembly.GetName().Version != new Version(2, 0, 0, 0) ||
            string.IsNullOrWhiteSpace(assembly.Location))
            throw new NotSupportedException("REST work requires the pinned file-backed HttpServer dependency; byte-loaded/unknown providers are unsupported.");
        using var file = File.OpenRead(assembly.Location);
        if (file.Length != 171520)
            throw new NotSupportedException("REST work HttpServer dependency size is unsupported.");
        string hash = Convert.ToHexString(SHA256.HashData(file));
        if (hash != HttpServerSha256)
            throw new NotSupportedException("REST work HttpServer dependency hash is unsupported.");
        return hash;
    }
    private static bool Calls(Instruction instruction, string type, string name) => instruction.Operand is MethodReference method &&
        method.DeclaringType.FullName == type && method.Name == name;
    private void InstrumentPermission(ILContext il)
    {
        // HasPermission is inside the compiler-generated All predicate, not this body.
        // The locked runtime audit binds that predicate; these retain the actual branch/call landmarks.
        if (il.Body.Instructions.Count(i => Calls(i, "Rests.SecureRestCommand", "Execute")) != 1 ||
            il.Body.Instructions.Count(i => Calls(i, "System.Linq.Enumerable", "All")) != 1 ||
            il.Body.Instructions.Count(i => Calls(i, "Rests.SecureRestCommand", "get_Permissions")) != 2)
            throw new NotSupportedException("Native REST token/permission/delegate contract changed.");
        var cursor = new ILCursor(il); cursor.GotoNext(i => Calls(i, "Rests.SecureRestCommand", "Execute"));
        cursor.Remove(); cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<SecureRestCommand, RestVerbs, IParameterCollection, SecureRest.TokenData, IRequest, IHttpContext, SecureRest, object>>(WithinPermission);
    }
    private object WithinPermission(SecureRestCommand command, RestVerbs verbs, IParameterCollection parameters,
        SecureRest.TokenData token, IRequest request, IHttpContext context, SecureRest actualAuthority)
    {
        if (!Healthy || !ReferenceEquals(actualAuthority, authority)) return command.Execute(verbs, parameters, token, request, context);
        var previous = current;
        try
        {
            current = new(this, command, request, context);
            return command.Execute(verbs, parameters, token, request, context);
        }
        finally { current = previous; }
    }
    private void InstrumentCallback(ILContext il)
    {
        if (il.Body.Instructions.Count(i => Calls(i, "Rests.RestCommandD", "Invoke")) != 1)
            throw new NotSupportedException("Native secure REST callback contract changed.");
        var cursor = new ILCursor(il); cursor.GotoNext(i => Calls(i, "Rests.RestCommandD", "Invoke"));
        cursor.Remove(); cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<RestCommandD, RestRequestArgs, SecureRestCommand, object>>(Invoke);
    }
    private object Invoke(RestCommandD handler, RestRequestArgs args, SecureRestCommand command)
    {
        bool allowed = true;
        try
        {
            if (Healthy && selected.Contains(handler) && current is { } request && ReferenceEquals(request.Owner, this) &&
                ReferenceEquals(request.Command, command) && ReferenceEquals(request.NativeRequest, args.Request) && ReferenceEquals(request.Context, args.Context))
                allowed = budget.Consume(args.Context.RemoteEndPoint.Address);
        }
        catch (Exception error)
        {
            failed = true;
            try { IntegrityFault?.Invoke(error); } catch { }
        }
        // Native callback exceptions are handled only by the existing outer REST path;
        // an observation failure never retries a callback or invents an authentication failure.
        return allowed ? handler(args) : new RestObject("429") { Error = "Selected account operation temporarily exceeds its resource budget." };
    }
    public void Dispose()
    {
        var permission = permissionHook; var callback = callbackHook;
        permissionHook = null; callbackHook = null;
        try { permission?.Dispose(); } finally { callback?.Dispose(); selected = ImmutableHashSet<RestCommandD>.Empty; }
    }
}
