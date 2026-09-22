using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Net.Sockets;
using AntiCheat.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.Sockets;

namespace AntiCheat.Plugin.TShock;

public sealed record M18ResetSlot(int Slot, int Type, int Stack, int Prefix, long SendSequence, bool WriteCompleted);
public sealed record M18ResetIntervalSnapshot(SessionKey Session, long? Account, ImmutableArray<M18ResetSlot> Slots,
    ImmutableArray<int> Buffs, int? Loadout, long WorldInfoSequence, long ReadySequence,
    bool SectionAccepted, bool SpawnAccepted, bool RequiredWritesCompleted, bool HistoricalResetSequenceObserved,
    bool SscRestoreInProgress, bool NativeTransportIdentityVerified, bool InitialResetInputsAvailable,
    int LaterItemExports, int LaterBuffExports, int ServerProjectileExports, string Gap)
{
    // This is only a finite input boundary. All ordinary producer/effect possibilities are separate.
    public bool CompleteOrdinaryDamageModel => false;
    public bool ClientStateDirectlyObserved => false;
}

/// <summary>Companion for existing M6/M8 contexts. Observes final native sends only;
/// never authorizes damage, edits gameplay, cancels transport or certifies arbitrary client state.</summary>
public sealed class M18ArrowSourceResetIntervals : IDisposable
{
    private sealed class State(SessionKey session)
    {
        public readonly SessionKey Session = session;
        public readonly object Sync = new();
        public readonly Dictionary<int, M18ResetSlot> Slots = new(350);
        public TSPlayer? Player; public Player? NativePlayer; public RemoteClient? Client; public ISocket? Socket;
        public LinuxTcpSocket? Provider; public TcpClient? Connection; public System.Net.Sockets.Socket? AcceptedSocket;
        public long? Account; public long Sequence, WorldInfo, LoadoutSend, BuffSend, ReadySend;
        public bool WorldInfoDone, LoadoutDone, BuffDone, ReadyDone, Section, Spawn, Retired, HistoricalBoundary;
        public int? Loadout; public ImmutableArray<int> Buffs = [];
        public int Outstanding, Later88, Later50, Server27;
        public long FirstObservedTick;
        public M18ResetIntervalSnapshot? Cached;
        public string Gap = "none";
    }
    private sealed class Send(State state, long sequence, byte packet, int slot)
    {
        public readonly State State = state;
        public readonly long Sequence = sequence;
        public readonly byte Packet = packet;
        public readonly int Slot = slot;
        public int Completion;
    }
    private readonly State?[] states = new State?[255];
    private readonly Func<int, (SessionSnapshot? Session, TSPlayer? Actor, bool CanWrite)> resolve;
    private readonly string fingerprint;
    private readonly int[] initialSlots;
    private ILHook? hook;
    private Hook? receiveHook;
    private delegate void GetDataOriginal(MessageBuffer buffer, int start, int length, out int messageId);
    private delegate void GetDataHook(GetDataOriginal original, MessageBuffer buffer, int start, int length, out int messageId);
    private int updateThread;
    private long epoch, tick;
    [ThreadStatic] private static State? activeSectionReceive;
    private bool hostSupported, failed;
    public const int MaximumOutstanding = 768;
    public const int IntervalTtlTicks = M6ArrowCandidateContexts.SnapshotTtlTicks;
    public bool Healthy => hook is not null && receiveHook is not null && !failed;
    public string LastFault { get; private set; } = "none";

    public M18ArrowSourceResetIntervals(string fingerprint,
        Func<int, (SessionSnapshot? Session, TSPlayer? Actor, bool CanWrite)> resolve)
    {
        this.fingerprint = fingerprint; this.resolve = resolve;
        var layout = new Player();
        initialSlots = Enumerable.Range(0, PlayerItemSlotID.Count).Where(slot =>
        {
            if (slot == PlayerItemSlotID.TrashItem) return true;
            var reference = new PlayerItemSlotID.SlotReference(layout, slot);
            return reference.TryGetArraySlot(out var array, out int index) && array is not null && (uint)index < array.Length;
        }).ToArray();
        if (initialSlots.Length != 350) throw new NotSupportedException("Native initial slot layout changed.");
    }
    public void Install()
    {
        if (hook is not null || failed) return;
        if (fingerprint != TargetRuntime.Fingerprint || typeof(Main).Assembly.GetName().Version != new Version(1, 4, 5, 8))
            throw new NotSupportedException("SSC source boundary requires the audited target.");
        try
        {
            var method = typeof(OTAPI.Hooks.NetMessage).GetMethod("InvokeSendBytes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            hook = new ILHook(method, Instrument);
            receiveHook = new Hook(typeof(MessageBuffer).GetMethod(nameof(MessageBuffer.GetData))!, (GetDataHook)WithinGetData);
        }
        catch { Dispose(); throw; }
    }
    private void Instrument(ILContext il)
    {
        bool SendCall(Instruction instruction) => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == "Terraria.Net.Sockets.ISocket" && method.Name == "AsyncSend";
        if (il.Body.Instructions.Count(SendCall) != 1 || !il.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference method && method.Name == "get_Result"))
            throw new NotSupportedException("Final SendBytes cancellation/send landmarks changed.");
        var cursor = new ILCursor(il); cursor.GotoNext(MoveType.Before, SendCall);
        cursor.Remove(); cursor.Emit(OpCodes.Ldarg, 6);
        cursor.EmitDelegate<Action<ISocket, byte[], int, int, SocketSendCallback, object, int>>(SendAfterCancellation);
    }
    public void Connected(SessionKey session)
    {
        if ((uint)session.Slot >= states.Length) return;
        Retire(Interlocked.Exchange(ref states[session.Slot], new(session)));
    }
    public void Forget(SessionKey session)
    {
        if ((uint)session.Slot < states.Length && states[session.Slot] is { } state && state.Session == session &&
            ReferenceEquals(Interlocked.CompareExchange(ref states[session.Slot], null, state), state)) Retire(state);
    }
    public void ResetWorld()
    {
        for (int slot = 0; slot < states.Length; slot++) Retire(Interlocked.Exchange(ref states[slot], null));
        epoch = 0;
    }
    public void Tick(long worldEpoch, bool verifiedNativeHost)
    {
        int thread = Environment.CurrentManagedThreadId;
        if (updateThread != 0 && updateThread != thread) { failed = true; LastFault = "update-thread-changed"; return; }
        updateThread = thread; hostSupported = verifiedNativeHost; tick++;
        epoch = worldEpoch;
        for (int slot = 0; slot < states.Length; slot++)
            if (states[slot] is { } state && state.Session.WorldEpoch != epoch)
            { Retire(Interlocked.Exchange(ref states[slot], null)); }
            else if (states[slot] is { } current)
                lock (current.Sync)
                {
                    if (!verifiedNativeHost) Gap(current, "unsupported-host-interval");
                    if (current.FirstObservedTick != 0 && tick - current.FirstObservedTick > IntervalTtlTicks)
                    { Gap(current, "bounded-reset-input-expired"); current.Slots.Clear(); current.Buffs = []; current.Cached = null; }
                }
    }
    public void Authenticated(SessionKey session, long account)
    {
        try { AuthenticatedCore(session, account); }
        catch (Exception error) { failed = true; LastFault = error.GetType().Name; }
    }
    private void AuthenticatedCore(SessionKey session, long account)
    {
        if (!TryCurrent(session, out var state)) return;
        lock (state.Sync)
        {
            if (account <= 0 || state.Player!.Account?.ID != account || state.Account is { } old && old != account)
                Gap(state, "authentication-binding-changed");
            else { state.Account = account; state.Cached = null; }
        }
    }
    /// <summary>Caller supplies the actual native before/after states of an uncanceled request.
    /// Seeing an unaccepted8/12 or merely a client packet is intentionally insufficient.</summary>
    public void AcceptedHandshake(SessionKey session, int packet, int before, int after)
    {
        if (!TryCurrent(session, out var state)) return;
        lock (state.Sync)
        {
            state.Cached = null;
            if (packet == 8 && before == 2 && after == 3 && state.WorldInfo != 0) state.Section = true;
            else if (packet == 12 && before == 3 && after == 10 && state.Section && state.ReadySend != 0)
                state.Spawn = true;
        }
    }
    private void WithinGetData(GetDataOriginal original, MessageBuffer buffer, int start, int length, out int messageId)
    {
        State? state = null; int before = -100;
        try
        {
            int slot = buffer.whoAmI;
            if ((uint)slot < states.Length && states[slot] is { } candidate &&
                length > 0 && (uint)start < buffer.readBuffer.Length && buffer.readBuffer[start] is 8 or 12 &&
                TryCurrent(candidate.Session, out var current)) { state = current; before = current.Client!.State; }
        }
        catch (Exception error) { failed = true; LastFault = error.GetType().Name; }
        var previousSection = activeSectionReceive;
        if (state is not null && before == 2 && buffer.readBuffer[start] == 8) activeSectionReceive = state;
        try { original(buffer, start, length, out messageId); }
        catch { if (state is not null) lock (state.Sync) Gap(state, "native-receive-threw"); throw; }
        finally { activeSectionReceive = previousSection; }
        if (state is null) return;
        try
        {
            if (TryCurrent(state.Session, out var current) && ReferenceEquals(current, state))
                AcceptedHandshake(state.Session, messageId, before, state.Client!.State);
        }
        catch (Exception error) { failed = true; LastFault = error.GetType().Name; }
    }
    private bool TryCurrent(SessionKey session, out State state)
    {
        state = null!;
        if (!Healthy || !hostSupported || Main.netMode != 2 || Main.myPlayer != 255 || epoch != session.WorldEpoch ||
            Environment.CurrentManagedThreadId != updateThread || (uint)session.Slot >= states.Length ||
            states[session.Slot] is not { } found || found.Session != session) return false;
        var (snapshot, actor, canWrite) = resolve(session.Slot);
        if (snapshot?.Key != session || !canWrite || snapshot.Revoked || actor is null || actor.Index != session.Slot ||
            !ReferenceEquals(TShockAPI.TShock.Players[session.Slot], actor) ||
            !ReferenceEquals(Main.player[session.Slot], actor.TPlayer) || actor.Client is not { Socket: { } socket } client)
        { lock (found.Sync) Gap(found, "current-binding-unavailable"); return false; }
        lock (found.Sync)
        {
            if (found.Retired) return false;
            if (found.FirstObservedTick == 0) found.FirstObservedTick = tick;
            if (found.Player is null)
            {
                found.Player = actor; found.NativePlayer = actor.TPlayer; found.Client = client; found.Socket = socket;
                // This is the provider installed by the audited TShock native listener. The
                // mutable wrapper alone does not identify an accepted TCP connection.
                if (socket.GetType() == typeof(LinuxTcpSocket) && socket is LinuxTcpSocket provider &&
                    provider._connection is { } connection && connection.Client is { } acceptedSocket)
                { found.Provider = provider; found.Connection = connection; found.AcceptedSocket = acceptedSocket; }
            }
            else if (!ReferenceEquals(found.Player, actor) || !ReferenceEquals(found.NativePlayer, actor.TPlayer) ||
                !ReferenceEquals(found.Client, client) || !ReferenceEquals(found.Socket, socket))
            { Gap(found, "transport-or-native-player-changed"); return false; }
            if (!CapturedTransportStillMatches(found)) { Gap(found, "native-transport-identity-changed"); return false; }
            if (found.Account is { } account && (!actor.IsLoggedIn || actor.Account?.ID != account || snapshot.AccountId != account))
            { Gap(found, "account-or-session-authentication-changed"); return false; }
        }
        state = found; return true;
    }
    private void SendAfterCancellation(ISocket socket, byte[] data, int offset, int size,
        SocketSendCallback callback, object callbackState, int remote)
    {
        Send? token = null;
        try { token = Prepare(socket, data, offset, size, remote); }
        catch (Exception error) { failed = true; LastFault = error.GetType().Name; }
        // Preserve the actual call and exception propagation. Native SendPacket owns its catch.
        if (token is null) { socket.AsyncSend(data, offset, size, callback, callbackState); return; }
        try
        {
            socket.AsyncSend(data, offset, size, state =>
            {
                try { Complete(token); }
                catch (Exception error) { failed = true; LastFault = error.GetType().Name; }
                callback(state); // The original callback is invoked with its original state.
            }, callbackState);
        }
        catch { lock (token.State.Sync) Gap(token.State, "native-send-threw"); throw; }
    }
    private Send? Prepare(ISocket socket, byte[] data, int offset, int size, int remote)
    {
        if ((uint)remote >= states.Length || states[remote] is not { } state || size < 3 || offset < 0 || offset > data.Length - size) return null;
        var frame = data.AsSpan(offset, size);
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame) != size || frame[2] is not (5 or 7 or 27 or 49 or 50 or 88 or 147)) return null;
        if (size > 4096 || Environment.CurrentManagedThreadId != updateThread)
        { lock (state.Sync) Gap(state, size > 4096 ? "bounded-wire-capture-exceeded" : "non-update-source-send"); return null; }
        if (!TryCurrent(state.Session, out state) || !ReferenceEquals(socket, state.Socket)) return null;
        byte packet = frame[2]; int slot = -1;
        lock (state.Sync)
        {
            state.Cached = null;
            if (++state.Outstanding > MaximumOutstanding) { Gap(state, "send-observation-capacity"); state.Outstanding--; return null; }
            long sequence = ++state.Sequence;
            if (packet == 88) state.Later88 = Math.Min(int.MaxValue - 1, state.Later88) + 1;
            else if (packet == 27) state.Server27 = Math.Min(int.MaxValue - 1, state.Server27) + 1;
            else if (packet == 7)
            {
                if (!ReadSsc(frame) || state.Spawn) Gap(state, "world-info-not-initial-ssc");
                else if (state.WorldInfo == 0) state.WorldInfo = sequence;
            }
            else if (packet == 5 && frame.Length == 12 && frame[3] == remote)
            {
                slot = BinaryPrimitives.ReadInt16LittleEndian(frame[4..]);
                if (state.WorldInfo == 0) { state.Outstanding--; return null; }
                if (state.Player is not { IsLoggedIn: true, Account.ID: > 0 }) Gap(state, "ssc-reset-not-authenticated");
                else if (Array.BinarySearch(initialSlots, slot) < 0) Gap(state, "initial-slot-layout-unsupported");
                else if (state.ReadySend != 0) Gap(state, "slot-write-after-ready-boundary");
                else state.Slots[slot] = new(slot, BinaryPrimitives.ReadInt16LittleEndian(frame[9..]),
                    BinaryPrimitives.ReadInt16LittleEndian(frame[6..]), frame[8], sequence, false);
            }
            else if (packet == 50 && frame.Length >= 6 && frame[3] == remote)
            {
                if (state.ReadySend != 0) state.Later50 = Math.Min(int.MaxValue - 1, state.Later50) + 1;
                else if (TryBuffs(frame, out var buffs)) { state.Buffs = buffs; state.BuffSend = sequence; state.BuffDone = false; }
                else Gap(state, "buff-frame-unsupported");
            }
            else if (packet == 147 && frame.Length == 7 && frame[3] == remote)
            {
                if (state.ReadySend != 0) Gap(state, "loadout-write-after-ready-boundary");
                else { state.Loadout = frame[4]; state.LoadoutSend = sequence; state.LoadoutDone = false; }
            }
            else if (packet == 49)
            {
                if (frame.Length != 3 || (!state.Section && !ReferenceEquals(activeSectionReceive, state)) ||
                    state.Slots.Count != 350 || state.BuffSend == 0 || state.LoadoutSend == 0)
                    Gap(state, "ready-before-required-initial-output");
                state.ReadySend = sequence; state.ReadyDone = false;
            }
            return new(state, sequence, packet, slot);
        }
    }
    private static void Complete(Send token)
    {
        var state = token.State;
        lock (state.Sync)
        {
            if (state.Retired) return;
            state.Cached = null;
            if (Interlocked.Exchange(ref token.Completion, 1) != 0) { Gap(state, "duplicate-native-write-callback"); return; }
            state.Outstanding--;
            // These are only the captured provider's objects, not Main/TShock live slot
            // state. A late completion cannot certify a replacement underlying socket.
            if (!CapturedTransportStillMatches(state)) { Gap(state, "native-transport-identity-changed"); return; }
            if (token.Packet == 5 && state.Slots.TryGetValue(token.Slot, out var slot) && slot.SendSequence == token.Sequence)
                state.Slots[token.Slot] = slot with { WriteCompleted = true };
            if (token.Packet == 7 && state.WorldInfo == token.Sequence) state.WorldInfoDone = true;
            if (token.Packet == 50 && state.BuffSend == token.Sequence) state.BuffDone = true;
            if (token.Packet == 147 && state.LoadoutSend == token.Sequence) state.LoadoutDone = true;
            if (token.Packet == 49 && state.ReadySend == token.Sequence) state.ReadyDone = true;
        }
    }
    public M18ResetIntervalSnapshot? Capture(SessionKey session)
    {
        try { return CaptureCore(session); }
        catch (Exception error) { failed = true; LastFault = error.GetType().Name; return null; }
    }
    private M18ResetIntervalSnapshot? CaptureCore(SessionKey session)
    {
        if (!TryCurrent(session, out var state)) return null;
        lock (state.Sync)
        {
            bool restoring = state.Player!.IgnoreSSCPackets;
            if (state.Cached is { } cached && cached.SscRestoreInProgress == restoring) return cached;
            bool writes = state.WorldInfoDone && state.LoadoutDone && state.BuffDone && state.ReadyDone &&
                state.Slots.Count == 350 && state.Slots.Values.All(slot => slot.WriteCompleted);
            // RestoreCharacter clears IgnoreSSCPackets in finally after its last send.
            // Re-read it even on an otherwise unchanged cached interval. Historical
            // evidence is latched; it is never a claim that later client state is fixed.
            bool boundary = writes && state.Section && state.Spawn && state.Account is > 0 && !restoring && state.Gap == "none";
            state.HistoricalBoundary |= boundary;
            bool nativeTransport = state.Provider is not null;
            return state.Cached = new(session, state.Account, state.Slots.Values.OrderBy(slot => slot.Slot).ToImmutableArray(),
                state.Buffs, state.Loadout, state.WorldInfo, state.ReadySend, state.Section, state.Spawn, writes, state.HistoricalBoundary,
                restoring, nativeTransport, boundary && nativeTransport,
                state.Later88, state.Later50, state.Server27, state.Gap);
        }
    }
    private static bool CapturedTransportStillMatches(State state) => state.Provider is null ||
        ReferenceEquals(state.Provider._connection, state.Connection) && state.Connection is not null &&
        ReferenceEquals(state.Connection.Client, state.AcceptedSocket);
    private static bool TryBuffs(ReadOnlySpan<byte> frame, out ImmutableArray<int> buffs)
    {
        buffs = []; if ((frame.Length & 1) != 0 || frame.Length > 6 + Player.maxBuffs * 2) return false;
        var values = ImmutableArray.CreateBuilder<int>();
        for (int offset = 4; offset + 1 < frame.Length; offset += 2)
        {
            int value = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            if (value == 0) { buffs = values.ToImmutable(); return offset + 2 == frame.Length; }
            values.Add(value);
        }
        return false;
    }
    private static bool ReadSsc(ReadOnlySpan<byte> frame)
    {
        // Locked7 serializer: fixed22 bytes before world-name, then bounded7-bit UTF8 length.
        int offset = 25, count = 0, shift = 0;
        while (true)
        {
            if (offset >= frame.Length || shift >= 35) return false;
            byte next = frame[offset++]; count |= (next & 127) << shift;
            if ((next & 128) == 0) break; shift += 7;
        }
        if (count < 0 || count > frame.Length - offset) return false;
        offset += count + 79 + TreeTopsInfo.AreaId.Count + 4;
        return offset < frame.Length && (frame[offset] & 64) != 0;
    }
    private static void Gap(State state, string reason) { if (state.Gap == "none") { state.Gap = reason; state.Cached = null; } }
    private static void Retire(State? state) { if (state is not null) lock (state.Sync) state.Retired = true; }
    public void Dispose()
    {
        try { receiveHook?.Dispose(); }
        finally
        {
            receiveHook = null;
            try { hook?.Dispose(); }
            finally { hook = null; foreach (var state in states) Retire(state); Array.Clear(states); }
        }
    }
}

