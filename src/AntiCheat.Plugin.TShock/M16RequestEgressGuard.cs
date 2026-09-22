using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Rules;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.NetModules;
using Terraria.Net;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>Exact synchronous ServerChat dispatch scope and the actual native NetModule transport.
/// Only chat output is limited. Gameplay state, unowned asynchronous output and authentication are untouched.</summary>
public sealed class M16RequestEgressGuard(TimeProvider clock,
    Func<int, (SessionSnapshot? Session, TSPlayer? Actor)> resolve,
    M16RequestEgressOptions? options = null) : IDisposable
{
    private readonly M16RequestEgressBudget budget = new(clock, options);
    private Hook? chatHook;
    private ILHook? transportHook;
    private volatile bool failed;
    [ThreadStatic] private static Request? active;
    private sealed record Request(M16RequestEgressGuard Owner, SessionKey Session, long? Account, TSPlayer Actor);
    private delegate bool ChatOriginal(HookManager manager, MessageBuffer buffer, int who, string text, ChatCommandId command);
    private delegate bool ChatHook(ChatOriginal original, HookManager manager, MessageBuffer buffer, int who, string text, ChatCommandId command);
    public bool Healthy => !failed && chatHook is not null && transportHook is not null;
    public string LastFault { get; private set; } = "none";
    public Action<Exception>? IntegrityFault { get; init; }
    public long AdmittedBytes => budget.AdmittedBytes;
    public long BlockedBytes => budget.BlockedBytes;
    public long AdmittedSends => budget.AdmittedSends;
    public long BlockedSends => budget.BlockedSends;
    public int AccountBucketCount => budget.AccountBucketCount;
    public void Install()
    {
        if (failed || chatHook is not null || transportHook is not null) return;
        if (typeof(NetManager).Assembly.GetName().Version != new Version(1, 4, 5, 8))
            throw new NotSupportedException("Chat egress requires audited Terraria1.4.5.8 native transport.");
        try
        {
            chatHook = new Hook(typeof(HookManager).GetMethod("InvokeServerChat", BindingFlags.Instance | BindingFlags.NonPublic)!, (ChatHook)WithinChat);
            transportHook = new ILHook(typeof(NetManager).GetMethod("mfwh_SendData")!, InstrumentTransport);
        }
        catch { Dispose(); throw; }
    }
    private bool WithinChat(ChatOriginal original, HookManager manager, MessageBuffer buffer, int who, string text, ChatCommandId command)
    {
        var previous = active; active = null;
        try
        {
            try
            {
                if (Healthy && who is >= 0 and < 256 && buffer.whoAmI == who)
                {
                    var (session, actor) = resolve(who);
                    if (session is { Revoked: false } && session.Key.Slot == who && actor is not null && actor.Index == who &&
                        ((session.AccountId is null && !actor.IsLoggedIn) || (session.AccountId is > 0 &&
                         actor is { IsLoggedIn: true, Account: not null } && actor.Account.ID == session.AccountId)))
                        active = new(this, session.Key, session.AccountId, actor);
                }
            }
            catch (Exception error) { Fault(error); }
            // Original executes exactly once, outside the observer exception handler.
            return original(manager, buffer, who, text, command);
        }
        finally { active = previous; }
    }
    /// <summary>Exact synchronous sender only. Unauthenticated callers carry Account=null;
    /// no requested name or credentials become account attribution. Egress still requires an account.</summary>
    public (SessionKey Session, long? Account)? CurrentSynchronousActor(TSPlayer commandActor)
    {
        try
        {
            if (!Healthy || active is not { } request || request.Owner != this || !ReferenceEquals(request.Actor, commandActor)) return null;
            var (session, actor) = resolve(request.Session.Slot);
            if (session is not { Revoked: false } || session.Key != request.Session || session.AccountId != request.Account ||
                !ReferenceEquals(actor, request.Actor)) return null;
            bool known = request.Account is null ? !commandActor.IsLoggedIn : request.Account > 0 &&
                commandActor is { IsLoggedIn: true, Account: not null } && commandActor.Account.ID == request.Account;
            return known ? (request.Session, request.Account) : null;
        }
        catch (Exception error) { Fault(error); return null; }
    }
    private void InstrumentTransport(ILContext il)
    {
        bool IsCall(Instruction i, string type, string method) => i.Operand is MethodReference m && m.DeclaringType.FullName == type && m.Name == method;
        if (il.Body.Instructions.Count(i => IsCall(i, "Terraria.Net.NetPacket", "ShrinkToFit")) != 1 ||
            il.Body.Instructions.Count(i => IsCall(i, "Terraria.Net.Sockets.ISocket", "AsyncSend")) != 1 ||
            il.Body.Instructions.Last().OpCode != OpCodes.Ret)
            throw new NotSupportedException("Chat egress native serialization/send landmarks changed.");
        var cursor = new ILCursor(il);
        cursor.GotoNext(MoveType.After, i => IsCall(i, "Terraria.Net.NetPacket", "ShrinkToFit"));
        // This point lies before the native try/catch. Branch to its existing outer return;
        // callers still recycle the shared packet once, and no recipient socket is closed.
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.EmitDelegate<Func<NetPacket, bool>>(AdmitSerializedChat);
        cursor.Emit(OpCodes.Brfalse, il.Body.Instructions.Last());
    }
    private bool AdmitSerializedChat(NetPacket packet)
    {
        try { return AdmitSerializedChatCore(packet); }
        catch (Exception error) { Fault(error); return true; }
    }
    private bool AdmitSerializedChatCore(NetPacket packet)
    {
        if (!Healthy || Main.netMode != 2 || active is not { Account: > 0 } request || request.Owner != this ||
            NetManager.Instance.GetModule<NetTextModule>() is null || packet.Id != NetManager.Instance.GetId<NetTextModule>()) return true;
        var (session, actor) = resolve(request.Session.Slot);
        if (session is not { Revoked: false } || session.Key != request.Session || session.AccountId != request.Account ||
            !ReferenceEquals(actor, request.Actor) || actor is not { IsLoggedIn: true, Account: not null } || actor.Account.ID != request.Account) return true;
        // Inspect only the already-serialized fixed envelope. Never retain or decode chat text.
        var data = packet.Buffer?.Data;
        if (data is null || packet.Length is < 5 or > 65535 || packet.Length > data.Length || data[2] != 82 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data) != packet.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3)) != packet.Id) return true;
        return budget.Consume(request.Session, request.Account!.Value, packet.Length).Allowed;
    }
    private void Fault(Exception error)
    {
        failed = true; LastFault = error.GetType().Name;
        try { IntegrityFault?.Invoke(error); } catch { /* The diagnostic sink cannot block native chat/transport. */ }
    }
    public void Forget(SessionKey session) => budget.Forget(session);
    public void Dispose()
    {
        try { transportHook?.Dispose(); }
        finally { transportHook = null; chatHook?.Dispose(); chatHook = null; }
    }
}
