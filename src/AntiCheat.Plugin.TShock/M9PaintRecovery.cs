using System.Buffers.Binary;
using System.Reflection;
using DynamicMethod = System.Reflection.Emit.DynamicMethod;
using AntiCheat.Core;
using ModFramework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record M9PaintCompletion(long Operation, SessionKey Session, long AccountId, int WorldId,
    int X, int Y, byte Before, byte Submitted, byte Current, long Revision, string Outcome,
    Guid ObjectObservation, M9PaintTileState BeforeState, M9PaintTileState CommittedState, bool SameObjectAtRecord,
    int PacketType = 63);
public sealed record M9PaintTileState(ushort Type, ushort Wall, byte Liquid, ushort Header, byte Header1,
    byte Header2, byte Header3, short FrameX, short FrameY)
{
    public static M9PaintTileState Capture(ITile tile) => new(tile.type, tile.wall, tile.liquid, tile.sTileHeader,
        tile.bTileHeader, tile.bTileHeader2, tile.bTileHeader3, tile.frameX, tile.frameY);
}

/// <summary>Packet63/64 ordinary block/wall paint. Compensates authorization lost during that exact
/// native commit; never rolls back earlier lawful painting because the actor was later banned.
/// No items, drops, frames, adjacent tiles or unknown inventories are restored.</summary>
public sealed class M9PaintRecovery(TimeProvider clock,
    Func<int, (SessionSnapshot? Session, TSPlayer? Actor)> resolve,
    Func<bool>? writesAvailable = null, ExecutionScope scope = ExecutionScope.Production) : IDisposable
{
    public const int Capacity = 128;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    public const string ContractId = "WORLD.PaintCommitRecovery/1.0.0";
    public const string WallContractId = "WORLD.WallPaintCommitRecovery/1.0.0";
    private readonly Func<bool> writes = writesAvailable ?? (() => true);
    private readonly bool isolatedLabAdapterAllowed = IsIsolatedLab(scope);
    private readonly List<IDisposable> hooks = [];
    private readonly Queue<(long Timestamp, M9PaintCompletion Value)> journal = new();
    private static readonly Type NativeReferenceType = typeof(ServerApi).Assembly.GetType("TerrariaApi.Server.TileReference", true)!;
    private static readonly Func<Tile, nint> NativeAddress = CreateNativeAddressReader();
    private static readonly Func<ConstileationProvider, Array?> NativeStorage = CreateNativeStorageReader();
    private long epoch, operation;
    private int thread, worldId, initializingWrites;
    private volatile bool failed, hostContractLost;
    private Commit? observedCommit; // Visible to setters on other threads, not merely ThreadStatic.
    [ThreadStatic] private static Request? activeRequest;
    [ThreadStatic] private static Commit? activeCommit;
    private sealed class Request(M9PaintRecovery owner, SessionSnapshot session, TSPlayer actor, int x, int y, byte color, int packetType)
    {
        public M9PaintRecovery Owner = owner;
        public SessionSnapshot Session = session;
        public TSPlayer Actor = actor;
        public int X = x, Y = y;
        public byte Color = color;
        public int PacketType = packetType;
        public bool SuppressRelay, NativeEntered;
    }
    private sealed class Commit(Request request, ITile tile, ModFramework.ICollection<ITile> collection, byte before)
    {
        public Request Request = request;
        public ITile Tile = tile;
        public ModFramework.ICollection<ITile> Collection = collection;
        public byte Before = before;
        public M9PaintTileState BeforeState = null!;
        public M9PaintTileState? CommittedState;
        public int WorldId = Main.worldID;
        public bool JournalReserved;
        public readonly object Gate = new();
        public Guid ObjectObservation = Guid.NewGuid();
        public nint Address;
        public Array? Storage;
        public long Revision;
        public bool Invalid, Restoring;
    }
    private delegate void GetDataOriginal(MessageBuffer buffer, int start, int length, out int messageType);
    private delegate void GetDataHook(GetDataOriginal original, MessageBuffer buffer, int start, int length, out int messageType);
    private delegate bool PaintOriginal(int x, int y, byte color, bool broadcast, bool effects);
    private delegate bool PaintHook(PaintOriginal original, int x, int y, byte color, bool broadcast, bool effects);
    public bool Healthy => hooks.Count == 24 && !failed && !hostContractLost && thread != 0;
    public string InvalidReason { get; private set; } = "none";
    public long Allowed { get; private set; }
    public long BeforeWriteBlocked { get; private set; }
    public long Restored { get; private set; }
    public long RecoveryRefused { get; private set; }
    public long RelaySuppressed { get; private set; }
    public long Unknown { get; private set; }
    public long Dropped { get; private set; }
    public long NativeFailures { get; private set; }
    public long ObserverFailures { get; private set; }
    public string LastUnknown { get; private set; } = "none";

    public void Install()
    {
        if (hooks.Count != 0 || failed) return;
        if (typeof(Tile).Assembly.GetName().Version != new Version(1, 4, 5, 8))
            throw new NotSupportedException("Paint recovery requires the audited 1.4.5.8 Tile contract.");
        try
        {
            // Wrap all nine public setters through the actual backing write. The same monitor
            // covers final eligibility and compensation, excluding races with off-thread setters.
            string[] properties = ["type", "wall", "liquid", "sTileHeader", "bTileHeader", "bTileHeader2", "bTileHeader3", "frameX", "frameY"];
            foreach (Type storageType in new[] { typeof(Tile), NativeReferenceType }) foreach (string name in properties)
            {
                var setter = storageType.GetProperty(name)!.SetMethod!;
                Type valueType = setter.GetParameters()[0].ParameterType;
                Delegate detour = valueType == typeof(ushort) ? (Action<Action<Tile, ushort>, Tile, ushort>)WriteUShort :
                    valueType == typeof(short) ? (Action<Action<Tile, short>, Tile, short>)WriteShort :
                    (Action<Action<Tile, byte>, Tile, byte>)WriteByte;
                hooks.Add(new Hook(setter, detour));
            }
            hooks.Add(new Hook(NativeReferenceType.GetMethod(nameof(Tile.ClearEverything))!, (Action<Action<Tile>, Tile>)ClearNative));
            hooks.Add(new Hook(typeof(ConstileationProvider).GetProperty("Item")!.SetMethod!,
                (Action<Action<ConstileationProvider, int, int, ITile>, ConstileationProvider, int, int, ITile>)ReplaceNative));
            // RuntimeDetour cannot wrap generic source methods; the established IL adapter is
            // used for this collection setter. Any attempted replacement already forbids recovery,
            // so it does not need a compare-and-restore monitor around the array assignment.
            hooks.Add(new ILHook(typeof(DefaultCollection<ITile>).GetProperty("Item")!.SetMethod!, il =>
            {
                var tracking = new VariableDefinition(il.Import(typeof(bool))); il.Body.Variables.Add(tracking);
                var cursor = new ILCursor(il);
                cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1); cursor.Emit(OpCodes.Ldarg_2);
                cursor.EmitDelegate<Func<DefaultCollection<ITile>, int, int, bool>>(ObserveReplacement);
                cursor.Emit(OpCodes.Stloc, tracking);
                while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret))
                {
                    cursor.MoveAfterLabels(); cursor.Emit(OpCodes.Ldloc, tracking);
                    cursor.EmitDelegate<Action<bool>>(initializing => { if (initializing) Interlocked.Decrement(ref initializingWrites); });
                    cursor.Index++;
                }
                // An exceptional unbound setter intentionally leaves the incomplete-write signal
                // set: subsequent binding withdraws the contract instead of assuming observation.
            }));
            hooks.Add(new Hook(typeof(MessageBuffer).GetMethod(nameof(MessageBuffer.GetData))!, (GetDataHook)WithinRequest));
            // Wrap the verified native BODY after OTAPI's mutable/cancellable wrapper event.
            // A permission change in that event is caught before effects and color writes.
            hooks.Add(new Hook(typeof(WorldGen).GetMethod("mfwh_paintTile",
                [typeof(int), typeof(int), typeof(byte), typeof(bool), typeof(bool)])!, (PaintHook)WithinTilePaint));
            hooks.Add(new Hook(typeof(WorldGen).GetMethod("mfwh_paintWall",
                [typeof(int), typeof(int), typeof(byte), typeof(bool), typeof(bool)])!, (PaintHook)WithinWallPaint));
            HookEvents.Terraria.NetMessage.SendData += OnSend;
        }
        catch { Dispose(); throw; }
    }

    public void Tick(long worldEpoch)
    {
        if (thread != 0 && thread != Environment.CurrentManagedThreadId) { InvalidateObservation(); return; }
        Volatile.Write(ref thread, Environment.CurrentManagedThreadId);
        if (Volatile.Read(ref initializingWrites) != 0) InvalidateObservation();
        RefreshHost();
        if (epoch != worldEpoch || worldId != Main.worldID)
        { journal.Clear(); epoch = worldEpoch; worldId = Main.worldID; }
        Trim();
    }

    /// <summary>Hosts that bypass ordinary Tile setters must withdraw this local recovery contract.
    /// An observation gap is sticky until a new instance; elapsed TTL never repairs it.</summary>
    public void InvalidateObservation()
    {
        failed = true;
        InvalidReason = "observation-gap";
        if (Volatile.Read(ref observedCommit) is { } commit) lock (commit.Gate) commit.Invalid = true;
    }

    private void WithinRequest(GetDataOriginal original, MessageBuffer buffer, int start, int length, out int messageType)
    {
        var previous = activeRequest; activeRequest = null;
        try
        {
            bool ordinaryPaint = start >= 0 && length == 7 && start <= buffer.readBuffer.Length - length &&
                buffer.readBuffer[start] is 63 or 64 && buffer.readBuffer[start + 6] == 0;
            if (ordinaryPaint) RefreshHost();
            if (ordinaryPaint && Healthy && Main.netMode == 2 && epoch > 0 && !WorldGen.isGeneratingOrLoadingWorld &&
                thread == Environment.CurrentManagedThreadId && start >= 0 && length == 7 && start <= buffer.readBuffer.Length - length &&
                buffer.readBuffer[start] is 63 or 64 && buffer.readBuffer[start + 6] == 0)
            {
                var bytes = buffer.readBuffer.AsSpan(start + 1, 6);
                int x = BinaryPrimitives.ReadInt16LittleEndian(bytes), y = BinaryPrimitives.ReadInt16LittleEndian(bytes[2..]);
                var (session, actor) = resolve(buffer.whoAmI);
                if (session is { AccountId: not null } && actor is { IsLoggedIn: true, Account: not null } &&
                    session.Key.WorldEpoch == epoch && session.Key.Slot == buffer.whoAmI && actor.Index == buffer.whoAmI &&
                    session.AccountId == actor.Account.ID && x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY && bytes[4] <= Main.numTileColors)
                    activeRequest = new(this, session, actor, x, y, bytes[4], buffer.readBuffer[start]);
            }
            original(buffer, start, length, out messageType);
        }
        finally { activeRequest = previous; }
    }

    private bool WithinTilePaint(PaintOriginal original, int x, int y, byte color, bool broadcast, bool effects) =>
        WithinPaint(original, x, y, color, broadcast, effects, 63);
    private bool WithinWallPaint(PaintOriginal original, int x, int y, byte color, bool broadcast, bool effects) =>
        WithinPaint(original, x, y, color, broadcast, effects, 64);
    private bool WithinPaint(PaintOriginal original, int x, int y, byte color, bool broadcast, bool effects, int packetType)
    {
        if (activeRequest is not { } request || request.Owner != this || request.NativeEntered ||
            request.X != x || request.Y != y || request.Color != color || request.PacketType != packetType || broadcast)
            return original(x, y, color, broadcast, effects);
        request.NativeEntered = true; // Recursive/plugin paints cannot borrow the original client source.
        RefreshHost();
        if (!StillSameSession(request) || !Authorized(request))
        {
            request.SuppressRelay = true; BeforeWriteBlocked = Increment(BeforeWriteBlocked);
            return false;
        }
        var candidate = Main.tile[x, y];
        bool standard = Main.tile.GetType() == typeof(DefaultCollection<ITile>) && candidate?.GetType() == typeof(Tile);
        bool constileation = Main.tile.GetType() == typeof(ConstileationProvider) && candidate?.GetType() == NativeReferenceType;
        if (!Healthy || candidate is not Tile tile || !(standard || constileation) || !SurfaceExists(request, tile))
        {
            Unknown = Increment(Unknown);
            LastUnknown = !Healthy ? "unhealthy-observation-contract" : !standard && !constileation ? "unsupported-native-tile-provider" : "paint-surface-absent";
            return original(x, y, color, broadcast, effects);
        }
        Trim();
        // A journal slot is reserved BEFORE execution. No recovery is attempted if we cannot
        // retain the source, committed before/after states and result within the fixed capacity.
        bool journalReserved = journal.Count < Capacity;
        if (!journalReserved) { Dropped = Increment(Dropped); Unknown = Increment(Unknown); }
        var previous = activeCommit;
        if (previous is not null) previous.Invalid = true;
        var commit = new Commit(request, tile, Main.tile, 0) { JournalReserved = journalReserved,
            Address = constileation ? NativeAddress(tile) : 0,
            Storage = constileation ? NativeStorage((ConstileationProvider)Main.tile) : null }; activeCommit = commit;
        var previousObserved = Interlocked.Exchange(ref observedCommit, commit);
        bool result;
        try
        {
            lock (commit.Gate) { commit.Before = Color(request, tile); commit.BeforeState = M9PaintTileState.Capture(tile); }
            try { result = original(x, y, color, broadcast, effects); }
            catch { NativeFailures = Increment(NativeFailures); commit.Invalid = true; throw; }
            Allowed = Increment(Allowed);
            // Keep observation active through callbacks triggered by the permission recheck.
            // A postcheck permission hook doing A→B→A is still a conflicting later edit.
            lock (commit.Gate) commit.CommittedState = M9PaintTileState.Capture(tile);
            // Permission extensions execute without holding the tile monitor. Their synchronous
            // work may involve other threads; every such setter still invalidates this scope.
            bool authorizedAfter;
            try { authorizedAfter = StillSameSession(request) && Authorized(request); RefreshHost(); }
            catch
            {
                ObserverFailures = Increment(ObserverFailures); commit.Invalid = true;
                InvalidateObservation(); request.SuppressRelay = true;
                if (commit.JournalReserved) Record(commit, "postcommit-observer-failed-no-restore");
                throw; // Native write happened; never retry, invent recovery, or claim write-before protection.
            }
            lock (commit.Gate)
            {
                string outcome = result ? "authorized-commit" : "native-no-change";
                if (result && !authorizedAfter)
                {
                    request.SuppressRelay = true;
                    if (CanRestore(commit))
                    {
                        commit.Restoring = true;
                        if (request.PacketType == 64) tile.wallColor(commit.Before); else tile.color(commit.Before);
                        Restored = Increment(Restored); outcome = "authorization-lost-restored";
                    }
                    else { RecoveryRefused = Increment(RecoveryRefused); outcome = "authorization-lost-conflict-no-restore"; }
                }
                if (commit.JournalReserved) Record(commit, outcome);
            }
            return result;
        }
        finally { activeCommit = previous; Volatile.Write(ref observedCommit, previousObserved); }
    }

    private bool CanRestore(Commit commit) => Healthy && !commit.Invalid && !commit.Restoring && commit.JournalReserved && journal.Count < Capacity &&
        thread == Environment.CurrentManagedThreadId && epoch == commit.Request.Session.Key.WorldEpoch && worldId == Main.worldID &&
        !WorldGen.isGeneratingOrLoadingWorld && StillSameSession(commit.Request) && SameWorldObject(commit) && SurfaceExists(commit.Request, commit.Tile) &&
        commit.Revision == 1 && Color(commit.Request, commit.Tile) == commit.Request.Color;

    private static byte Color(Request request, ITile tile) => request.PacketType == 64 ? tile.wallColor() : tile.color();
    private static bool SurfaceExists(Request request, ITile tile) => request.PacketType == 64 ? tile.wall != 0 : tile.active();

    private bool StillSameSession(Request request)
    {
        var (session, actor) = resolve(request.Session.Key.Slot);
        return session is not null && session.Key == request.Session.Key && session.AccountId == request.Session.AccountId &&
            ReferenceEquals(actor, request.Actor) && actor!.IsLoggedIn && actor.Account?.ID == request.Session.AccountId &&
            session.Key.WorldEpoch == epoch && worldId == Main.worldID;
    }
    private bool Authorized(Request request)
    {
        var (session, _) = resolve(request.Session.Key.Slot);
        return session is { Revoked: false } && writes() && request.Actor.HasPaintPermission(request.X, request.Y);
    }
    private void MarkWrite(Commit commit)
    {
        if (thread != Environment.CurrentManagedThreadId) commit.Invalid = true;
        if (commit.Restoring) return;
        if (commit.Revision == long.MaxValue) commit.Invalid = true; else commit.Revision++;
    }
    private void WriteUShort(Action<Tile, ushort> original, Tile tile, ushort value)
    {
        bool initializing = EnterWrite();
        try
        {
            var commit = Volatile.Read(ref observedCommit);
            if (commit is null || !MatchesTile(commit, tile)) { original(tile, value); return; }
            lock (commit.Gate) { MarkWrite(commit); original(tile, value); }
        }
        finally { if (initializing) Interlocked.Decrement(ref initializingWrites); }
    }
    private void WriteShort(Action<Tile, short> original, Tile tile, short value)
    {
        bool initializing = EnterWrite();
        try
        {
            var commit = Volatile.Read(ref observedCommit);
            if (commit is null || !MatchesTile(commit, tile)) { original(tile, value); return; }
            lock (commit.Gate) { MarkWrite(commit); original(tile, value); }
        }
        finally { if (initializing) Interlocked.Decrement(ref initializingWrites); }
    }
    private void WriteByte(Action<Tile, byte> original, Tile tile, byte value)
    {
        bool initializing = EnterWrite();
        try
        {
            var commit = Volatile.Read(ref observedCommit);
            if (commit is null || !MatchesTile(commit, tile)) { original(tile, value); return; }
            lock (commit.Gate) { MarkWrite(commit); original(tile, value); }
        }
        finally { if (initializing) Interlocked.Decrement(ref initializingWrites); }
    }
    private bool ObserveReplacement(DefaultCollection<ITile> collection, int x, int y)
    {
        bool initializing = EnterWrite();
        var commit = Volatile.Read(ref observedCommit);
        if (commit is not null && ReferenceEquals(commit.Collection, collection) && x == commit.Request.X && y == commit.Request.Y)
            lock (commit.Gate) { commit.Invalid = true; MarkWrite(commit); }
        return initializing;
    }
    private void ClearNative(Action<Tile> original, Tile tile)
    {
        bool initializing = EnterWrite();
        try
        {
            var commit = Volatile.Read(ref observedCommit);
            if (commit is null || !MatchesTile(commit, tile)) { original(tile); return; }
            lock (commit.Gate) { commit.Invalid = true; MarkWrite(commit); original(tile); }
        }
        finally { if (initializing) Interlocked.Decrement(ref initializingWrites); }
    }
    private void ReplaceNative(Action<ConstileationProvider, int, int, ITile> original,
        ConstileationProvider collection, int x, int y, ITile value)
    {
        bool initializing = EnterWrite();
        try
        {
            var commit = Volatile.Read(ref observedCommit);
            if (commit is null || !ReferenceEquals(collection, commit.Collection) || x != commit.Request.X || y != commit.Request.Y)
            { original(collection, x, y, value); return; }
            lock (commit.Gate) { commit.Invalid = true; MarkWrite(commit); original(collection, x, y, value); }
        }
        finally { if (initializing) Interlocked.Decrement(ref initializingWrites); }
    }
    private static bool MatchesTile(Commit commit, Tile tile) => ReferenceEquals(commit.Tile, tile) ||
        (commit.Address != 0 && tile.GetType() == NativeReferenceType && NativeAddress(tile) == commit.Address);
    private static bool SameWorldObject(Commit commit)
    {
        if (!ReferenceEquals(Main.tile, commit.Collection) || commit.Request.X >= Main.maxTilesX || commit.Request.Y >= Main.maxTilesY) return false;
        if (commit.Storage is not null && (Main.tile is not ConstileationProvider provider || !ReferenceEquals(NativeStorage(provider), commit.Storage))) return false;
        return Main.tile[commit.Request.X, commit.Request.Y] is Tile tile && MatchesTile(commit, tile);
    }
    private static Func<Tile, nint> CreateNativeAddressReader()
    {
        var method = new DynamicMethod("M9TileStorageAddress", typeof(nint), [typeof(Tile)], typeof(M9PaintRecovery).Module, true);
        var il = method.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Castclass, NativeReferenceType);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, NativeReferenceType.GetField("_tile", BindingFlags.NonPublic | BindingFlags.Instance)!);
        il.Emit(System.Reflection.Emit.OpCodes.Conv_I); il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return method.CreateDelegate<Func<Tile, nint>>();
    }
    private static Func<ConstileationProvider, Array?> CreateNativeStorageReader()
    {
        var method = new DynamicMethod("M9TileStorageArray", typeof(Array), [typeof(ConstileationProvider)], typeof(M9PaintRecovery).Module, true);
        var il = method.GetILGenerator(); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, typeof(ConstileationProvider).GetField("data", BindingFlags.NonPublic | BindingFlags.Instance)!);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return method.CreateDelegate<Func<ConstileationProvider, Array?>>();
    }
    private bool EnterWrite()
    {
        int established = Volatile.Read(ref thread);
        bool initializing = established == 0;
        if (initializing)
        {
            Interlocked.Increment(ref initializingWrites);
            established = Volatile.Read(ref thread); // Closes publication between the first read and increment.
        }
        if (established != 0 && established != Environment.CurrentManagedThreadId)
        {
            // Even a setter that read observedCommit=null may subsequently write during a
            // published transaction. Withdraw at its entrance, before it can perform that write.
            InvalidateObservation(); InvalidReason = "off-update-thread-world-writer";
        }
        return initializing;
    }
    private void OnSend(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (activeRequest is not { SuppressRelay: true } request || request.Owner != this || !args.ContinueExecution ||
            args.msgType != request.PacketType || args.number != request.X || args.number2 != request.Y || args.number3 != request.Color ||
            args.number4 != 0 || args.remoteClient != -1 || args.ignoreClient != request.Session.Key.Slot) return;
        args.ContinueExecution = false; RelaySuppressed = Increment(RelaySuppressed);
        // Resync the authoritative value through the existing tile packet. This is not replaying
        // the rejected input. It also corrects the actor's locally predicted paint.
        if (request.Session.Key.WorldEpoch == epoch && worldId == Main.worldID &&
            request.X < Main.maxTilesX && request.Y < Main.maxTilesY)
            NetMessage.SendTileSquare(-1, request.X, request.Y, 1);
    }
    private void Record(Commit commit, string outcome)
    {
        Trim();
        if (journal.Count >= Capacity) { Dropped = Increment(Dropped); return; }
        if (operation == long.MaxValue) { InvalidateObservation(); return; }
        journal.Enqueue((clock.GetTimestamp(), new(++operation, commit.Request.Session.Key, commit.Request.Session.AccountId!.Value,
            commit.WorldId, commit.Request.X, commit.Request.Y, commit.Before, commit.Request.Color, Color(commit.Request, commit.Tile), commit.Revision, outcome,
            commit.ObjectObservation, commit.BeforeState, commit.CommittedState ?? commit.BeforeState,
            SameWorldObject(commit), commit.Request.PacketType)));
    }
    private void Trim()
    {
        long now = clock.GetTimestamp();
        while (journal.TryPeek(out var item) && clock.GetElapsedTime(item.Timestamp, now) > Retention) journal.Dequeue();
    }
    public M9PaintCompletion[] Snapshot()
    { if (thread != Environment.CurrentManagedThreadId) return []; Trim(); return journal.Select(x => x.Value).ToArray(); }
    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
    private void RefreshHost()
    {
        if (hostContractLost) return;
        foreach (var container in ServerApi.Plugins)
        {
            Type type = container.Plugin.GetType();
            if (type == typeof(TShockAPI.TShock) || type == typeof(AntiCheatPlugin)) continue;
            // The existing isolated QA host is an explicit TestLab-only adapter. Production
            // never trusts a plugin on name alone. It cannot promote a rule or enter the package.
            bool isolatedLabAdapter = isolatedLabAdapterAllowed &&
                type.FullName == "CompatibilityAudit.GameplayScaffold" && type.Assembly.GetName().Name == "GameplayScaffold" &&
                type.Assembly.GetName().Version == new Version(1, 0, 2, 0) &&
                type.GetMethod("M9PaintEffect", BindingFlags.NonPublic | BindingFlags.Instance) is not null &&
                type.GetMethod("PrepareM9Paint", BindingFlags.NonPublic | BindingFlags.Instance) is not null;
            if (isolatedLabAdapter) continue;
            hostContractLost = true; InvalidReason = "unsupported-host-observation-history"; break;
        }
    }
    private static bool IsIsolatedLab(ExecutionScope scope)
    {
        if (scope != ExecutionScope.TestLab) return false;
        string? root = Environment.GetEnvironmentVariable("ANTICHEAT_LAB_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        string expected = Path.GetFullPath(Path.Combine(root, "app")).TrimEnd(Path.DirectorySeparatorChar);
        string current = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(expected, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            File.Exists(Path.Combine(root, ".anticheat-lab")) && File.Exists(Path.Combine(root, ".compatibility-isolated-test"));
    }
    public void Dispose()
    {
        HookEvents.Terraria.NetMessage.SendData -= OnSend;
        List<Exception>? errors = null;
        try
        {
            for (int i = hooks.Count - 1; i >= 0; i--)
                try { hooks[i].Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
        }
        finally { hooks.Clear(); journal.Clear(); thread = 0; Volatile.Write(ref observedCommit, null); }
        if (errors is not null) throw new AggregateException("Paint hook cleanup failed; every remaining hook was still attempted.", errors);
    }
}
