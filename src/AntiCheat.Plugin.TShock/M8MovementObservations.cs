using System.Collections.Immutable;
using AntiCheat.Core;
using Terraria;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record M8MovementNotice(string Source, DateTimeOffset AttemptUtc, bool AddressedToSubject,
    int RemoteClient, int IgnoreClient, double? DestinationX, double? DestinationY, bool ExplicitDestination,
    int? Style, int? PortalIndex, int? HurtDamageArgument, int? HurtDirectionArgument, bool? HurtPvp);

public sealed record M8MovementRuntimeState(string Source, double? PositionX, double? PositionY,
    double? VelocityX, double? VelocityY, bool MountActive, int MountType, double? GravityDirection,
    int GrapplingCount, bool NoKnockback, bool Immune, int ImmuneTime, int UnacknowledgedTeleports,
    bool Dead, bool Ghost);

public sealed record M8MovementSample(long Sequence, DateTimeOffset ReceivedUtc, string InputSource,
    bool CancelledAtObservation, bool? PreviousCancelledAtObservation, double? ClientPositionX, double? ClientPositionY,
    bool ClientVelocityPresent, double? ClientVelocityX, double? ClientVelocityY, int? ClientMountType,
    int ClientGravityDirection, bool ClientValuesFinite, double? ReceiptIntervalMilliseconds,
    double? ClientDisplacementPixels, double? ApparentPixelsPerReceiptSecond,
    M8MovementRuntimeState RuntimeBeforeCandidate, M8MovementNotice? RecentTeleportAttempt,
    double? TeleportAttemptAgeMilliseconds, M8MovementNotice? RecentHurtAttempt, double? HurtAttemptAgeMilliseconds);

public sealed record M8MovementSessionSnapshot(SessionKey Session, int AccountId, int WorldId,
    string Capability, bool CanSupplyHardProof, long Received, long SamplesWritten, long SampledOut,
    long OverwrittenUnexpired, long NonFiniteDeclarations, DateTimeOffset? FirstReceiptUtc,
    DateTimeOffset? LastReceiptUtc, double? MaximumApparentPixelsPerReceiptSecond,
    ImmutableArray<M8MovementSample> Samples);

/// <summary>
/// Original72 G04 SOFT: bounded diagnostics of client declarations and independently labelled
/// pre-candidate runtime state. There is deliberately no verdict, cancellation, score or sanction API.
/// Positions accepted by Main.player and outgoing message attempts never prove legitimate movement.
/// </summary>
public sealed class M8MovementObservations(TimeProvider clock, string fingerprint) : IDisposable
{
    public const int SessionCapacity = 256;
    public const int SamplesPerSession = 32;
    public static readonly TimeSpan SampleSpacing = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NoticeRetention = TimeSpan.FromSeconds(5);
    private sealed record Retained(M8MovementSample Sample, long Timestamp);
    private sealed record Notice(M8MovementNotice Value, long Timestamp);
    private sealed class State(SessionKey session, int account, TSPlayer actor)
    {
        public readonly SessionKey Session = session;
        public readonly int AccountId = account;
        public readonly TSPlayer Actor = actor;
        public readonly Player RuntimePlayer = actor.TPlayer;
        public readonly Retained?[] Samples = new Retained?[SamplesPerSession];
        public int Cursor;
        public long Received, Written, SampledOut, Overwritten, NonFinite;
        public long? LastReceiptTimestamp, LastSampleTimestamp;
        public double? LastX, LastY, MaximumRate;
        public bool LastCancelled;
        public DateTimeOffset? FirstUtc, LastUtc;
        public Notice? Teleport, Hurt;
    }
    private readonly State?[] states = new State?[SessionCapacity];
    private Func<int, (SessionSnapshot? Session, TSPlayer? Player)>? lookup;
    private bool installed, failed;
    private long epoch, received, sampled, skipped, malformed, invalidBinding, wrongContext;
    private int thread, worldId;
    public bool Healthy => installed && !failed;
    public long TotalReceived => Interlocked.Read(ref received);
    public long TotalSampled => Interlocked.Read(ref sampled);
    public long TotalSampledOut => Interlocked.Read(ref skipped);
    public long MalformedNotObserved => Interlocked.Read(ref malformed);
    public long InvalidBindingNotObserved => Interlocked.Read(ref invalidBinding);
    public long WrongContextNotObserved => Interlocked.Read(ref wrongContext);
    public Action<Exception>? IntegrityFault { get; set; }
    public bool FaultObserverFailed { get; private set; }

    public void Install(Func<int, (SessionSnapshot? Session, TSPlayer? Player)> resolve)
    {
        if (installed || failed) return;
        Safe(() =>
        {
            if (fingerprint != TargetRuntime.Fingerprint)
                throw new InvalidOperationException("Movement observations require the audited protocol326 runtime.");
            lookup = resolve ?? throw new ArgumentNullException(nameof(resolve));
            HookEvents.Terraria.NetMessage.SendData += OnSendData;
            HookEvents.Terraria.NetMessage.SendPlayerHurt += OnSendPlayerHurt;
            installed = true;
        });
    }

    public void Tick(long worldEpoch) => Safe(() =>
    {
        if (!Healthy || Main.netMode != 2 || worldEpoch <= 0) return;
        if (thread != 0 && thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Movement observation game-update context changed.");
        thread = Environment.CurrentManagedThreadId;
        if (epoch != worldEpoch || worldId != Main.worldID)
        { Array.Clear(states); epoch = worldEpoch; worldId = Main.worldID; }
        // Bounded housekeeping, never a per-packet world/entity scan.
        for (int slot = 0; slot < states.Length; slot++)
            if (states[slot] is { } state && !CurrentRetainedBinding(state)) states[slot] = null;
    });

    /// <summary>Call with the existing M2 parser result at the raw hook. Does not mean core accepted it.</summary>
    public void Observe(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled) =>
        Safe(() => ObserveCore(packet, session, actor, alreadyCancelled));

    private void ObserveCore(M2Packet packet, SessionKey session, TSPlayer actor, bool cancelled)
    {
        if (packet.Kind != M2PacketKind.PlayerUpdate) return;
        if (!CurrentContext(session)) { Increment(ref wrongContext); return; }
        var body = packet.Payload;
        if (body is null || body.Length < 14 || body.Length != 14 + ((body[2] & 4) != 0 ? 8 : 0) +
            ((body[2] & 128) != 0 ? 2 : 0) + ((body[3] & 64) != 0 ? 16 : 0) + ((body[4] & 32) != 0 ? 8 : 0))
        { Increment(ref malformed); return; }
        if (body[0] != session.Slot || !CurrentBinding(session, actor)) { Increment(ref invalidBinding); return; }
        var state = GetState(session, actor);
        long now = clock.GetTimestamp();
        DateTimeOffset utc = clock.GetUtcNow();
        double? x = Finite(M2PacketReader.Single(body, 6)), y = Finite(M2PacketReader.Single(body, 10));
        bool velocityPresent = (body[2] & 4) != 0;
        double? vx = velocityPresent ? Finite(M2PacketReader.Single(body, 14)) : null;
        double? vy = velocityPresent ? Finite(M2PacketReader.Single(body, 18)) : null;
        int? mount = (body[2] & 128) != 0 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(velocityPresent ? 22 : 14, 2)) : null;
        bool finite = x.HasValue && y.HasValue && (!velocityPresent || vx.HasValue && vy.HasValue);
        double? elapsed = state.LastReceiptTimestamp is { } previous && IsFresh(previous, now, Retention)
            ? clock.GetElapsedTime(previous, now).TotalMilliseconds : null;
        double? distance = elapsed.HasValue && x.HasValue && y.HasValue && state.LastX.HasValue && state.LastY.HasValue
            ? Math.Sqrt(Math.Pow(x.Value - state.LastX.Value, 2) + Math.Pow(y.Value - state.LastY.Value, 2)) : null;
        double? rate = elapsed > 0 && distance.HasValue ? distance.Value * 1000 / elapsed.Value : null;
        bool? previousCancelled = elapsed.HasValue ? state.LastCancelled : null;
        state.Received = Saturated(state.Received); Increment(ref received);
        if (!finite) state.NonFinite = Saturated(state.NonFinite);
        state.FirstUtc ??= utc; state.LastUtc = utc;
        state.LastReceiptTimestamp = now; state.LastX = x; state.LastY = y; state.LastCancelled = cancelled;
        if (rate.HasValue) state.MaximumRate = Math.Max(state.MaximumRate ?? 0, rate.Value);
        if (state.LastSampleTimestamp is { } prior && prior <= now && clock.GetElapsedTime(prior, now) < SampleSpacing)
        { state.SampledOut = Saturated(state.SampledOut); Increment(ref skipped); return; }

        var player = actor.TPlayer;
        var runtime = new M8MovementRuntimeState("Main.player-before-current-candidate; accepted-state-not-legality",
            Finite(player.position.X), Finite(player.position.Y), Finite(player.velocity.X), Finite(player.velocity.Y),
            player.mount.Active, player.mount.Type, Finite(player.gravDir), player.grapCount, player.noKnockback,
            player.immune, player.immuneTime, player.unacknowledgedTeleports, player.dead, player.ghost);
        var teleport = FreshNotice(state.Teleport, now);
        var hurt = FreshNotice(state.Hurt, now);
        state.Written = Saturated(state.Written); Increment(ref sampled);
        var sample = new M8MovementSample(state.Received, utc, "packet13-client-declarations; receipt-clock-not-simulation-time",
            cancelled, previousCancelled, x, y, velocityPresent, vx, vy, mount, (body[2] & 16) != 0 ? 1 : -1,
            finite, elapsed, distance, rate, runtime, teleport?.Value,
            teleport is null ? null : clock.GetElapsedTime(teleport.Timestamp, now).TotalMilliseconds,
            hurt?.Value, hurt is null ? null : clock.GetElapsedTime(hurt.Timestamp, now).TotalMilliseconds);
        if (state.Samples[state.Cursor] is { } overwritten && IsFresh(overwritten.Timestamp, now, Retention))
            state.Overwritten = Saturated(state.Overwritten);
        state.Samples[state.Cursor] = new(sample, now); state.Cursor = (state.Cursor + 1) % SamplesPerSession;
        state.LastSampleTimestamp = now;
    }

    private void OnSendData(object? sender, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (!args.ContinueExecution || args.msgType is not (65 or 96)) return;
        Safe(() =>
        {
            if (args.msgType == 65 && (args.number != 0 || !float.IsFinite(args.number2) || args.number2 != (int)args.number2)) return;
            int slot = args.msgType == 65 ? (int)args.number2 : args.number;
            if (!TryNoticeState(slot, out var state)) return;
            state!.Teleport = new(new(args.msgType == 65 ? "NetMessage.SendData65-attempt; not-consumption-or-authorization" :
                    "NetMessage.SendData96-attempt; not-consumption-or-authorization", clock.GetUtcNow(),
                AddressedTo(slot, args.remoteClient, args.ignoreClient), args.remoteClient, args.ignoreClient,
                Finite(args.msgType == 65 ? args.number3 : args.number2), Finite(args.msgType == 65 ? args.number4 : args.number3),
                args.msgType != 65 || args.number6 != 1, args.msgType == 65 ? args.number5 : 4,
                //96 carries a signed16 portal index; its native consumer always uses teleport style4.
                args.msgType == 96 && float.IsFinite(args.number4) ? unchecked((short)args.number4) : null,
                null, null, null), clock.GetTimestamp());
        });
    }

    private void OnSendPlayerHurt(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
    {
        Safe(() =>
        {
            if (!TryNoticeState(args.playerTargetIndex, out var state)) return;
            state!.Hurt = new(new("NetMessage.SendPlayerHurt-arguments; possible-knockback-context-not-applied-effect",
                clock.GetUtcNow(), AddressedTo(args.playerTargetIndex, args.remoteClient, args.ignoreClient),
                args.remoteClient, args.ignoreClient, null, null, false, null, null, args.damage, args.direction, args.pvp),
                clock.GetTimestamp());
        });
    }

    private bool TryNoticeState(int slot, out State? state)
    {
        state = null;
        if (slot is < 0 or >= SessionCapacity || lookup is null) return false;
        if (thread != Environment.CurrentManagedThreadId) { Increment(ref wrongContext); return false; }
        var target = lookup(slot);
        if (target.Session is not { } session || target.Player is not { } actor ||
            !CurrentContext(session.Key) || !CurrentBinding(session.Key, actor)) return false;
        state = GetState(session.Key, actor); return true;
    }

    private State GetState(SessionKey session, TSPlayer actor)
    {
        if (states[session.Slot] is not { } state || state.Session != session ||
            state.AccountId != actor.Account.ID || !ReferenceEquals(state.Actor, actor) || !ReferenceEquals(state.RuntimePlayer, actor.TPlayer))
            states[session.Slot] = state = new(session, actor.Account.ID, actor);
        return state;
    }

    private bool CurrentContext(SessionKey session) => Healthy && Main.netMode == 2 &&
        thread == Environment.CurrentManagedThreadId && epoch > 0 && epoch == session.WorldEpoch && worldId == Main.worldID;

    private bool CurrentBinding(SessionKey session, TSPlayer actor)
    {
        if (lookup is null || session.Slot is < 0 or >= SessionCapacity || session.ServerRunId == Guid.Empty || session.Generation <= 0)
            return false;
        var current = lookup(session.Slot);
        return current.Session is { Revoked: false } observed && observed.Key == session &&
            ReferenceEquals(current.Player, actor) && actor.Index == session.Slot && actor.IsLoggedIn &&
            actor.Account is not null && observed.AccountId == actor.Account.ID && ReferenceEquals(Main.player[session.Slot], actor.TPlayer);
    }

    private bool CurrentRetainedBinding(State state) => CurrentBinding(state.Session, state.Actor) &&
        state.AccountId == state.Actor.Account.ID && ReferenceEquals(state.RuntimePlayer, state.Actor.TPlayer);

    public M8MovementSessionSnapshot? Capture(SessionKey session)
    {
        M8MovementSessionSnapshot? result = null;
        Safe(() =>
        {
            if (!CurrentContext(session) || session.Slot is < 0 or >= SessionCapacity || states[session.Slot] is not { } state ||
                state.Session != session) return;
            // Invalidate before a new packet arrives, even if the TSPlayer object still resolves
            // its TPlayer property through a replaced Main.player slot or a changed account.
            if (!CurrentRetainedBinding(state)) { states[session.Slot] = null; return; }
            long now = clock.GetTimestamp();
            var values = state.Samples.Where(x => x is not null && IsFresh(x.Timestamp, now, Retention))
                .Select(x => x!.Sample).OrderBy(x => x.Sequence).ToImmutableArray();
            result = new(session, state.AccountId, worldId, "Original72.G04.SOFT", false, state.Received, state.Written,
                state.SampledOut, state.Overwritten, state.NonFinite, state.FirstUtc, state.LastUtc, state.MaximumRate, values);
        });
        return result;
    }

    private Notice? FreshNotice(Notice? notice, long now) => notice is not null && IsFresh(notice.Timestamp, now, NoticeRetention) ? notice : null;
    private bool IsFresh(long timestamp, long now, TimeSpan limit) => timestamp <= now && clock.GetElapsedTime(timestamp, now) <= limit;
    private static bool AddressedTo(int subject, int remote, int ignored) => remote == subject || remote == -1 && ignored != subject;
    private static double? Finite(float value) => float.IsFinite(value) ? value : null;
    private static long Saturated(long value) => value == long.MaxValue ? value : value + 1;
    private static void Increment(ref long value)
    {
        long current;
        do { current = Interlocked.Read(ref value); if (current == long.MaxValue) return; }
        while (Interlocked.CompareExchange(ref value, current + 1, current) != current);
    }
    private void Safe(Action action)
    {
        if (failed) return;
        try { action(); }
        catch (Exception error)
        {
            failed = true;
            try { IntegrityFault?.Invoke(error); } catch (Exception) { FaultObserverFailed = true; }
        }
    }
    public void Dispose()
    {
        if (installed)
        {
            HookEvents.Terraria.NetMessage.SendData -= OnSendData;
            HookEvents.Terraria.NetMessage.SendPlayerHurt -= OnSendPlayerHurt;
        }
        installed = false; lookup = null; Array.Clear(states); thread = 0; epoch = 0;
    }
}
