using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

/// <summary>Optional isolated TestLab observer. Its caller validates scope, loopback ownership,
/// marker and environment before installing. It never changes game state or supplies rule input.</summary>
public sealed class M16CombatLabDiagnostics(Func<string?> validatedOutput, TerrariaPlugin registrator) : IDisposable
{
    public const string CommandName = "qa_m16_combat_state";
    public const int EventCapacity = 256;
    private readonly List<object> events = new(EventCapacity);
    private Hook? receiver;
    private Command? command;
    private int updateThread, snapshotRequested, observerFailed;
    private long sequence, dropped, windowStart;
    private bool installed;
    [ThreadStatic] private static Scope? current;
    private sealed class Scope(M16CombatLabDiagnostics observer, NPC target, int sender)
    {
        public M16CombatLabDiagnostics Observer = observer;
        public NPC Target = target;
        public int Sender = sender, Strikes, Loot, Relays;
    }
    private delegate void NativeGetData(MessageBuffer buffer, int start, int length, out int messageType);
    private delegate void AroundGetData(NativeGetData original, MessageBuffer buffer, int start, int length, out int messageType);

    public void Install()
    {
        if (installed) return;
        if (validatedOutput() is null) throw new InvalidOperationException("M16 observation requires an already validated lab output.");
        installed = true; // Dispose must also unwind an interrupted installation.
        try
        {
        receiver = new Hook(typeof(MessageBuffer).GetMethod(nameof(MessageBuffer.GetData),
            [typeof(int), typeof(int), typeof(int).MakeByRefType()])!, (AroundGetData)AroundReceive);
        HookEvents.Terraria.NPC.StrikeNPC += OnStrike;
        HookEvents.Terraria.NPC.NPCLoot += OnLoot;
        HookEvents.Terraria.NetMessage.SendData += OnSend;
        ServerApi.Hooks.GameUpdate.Register(registrator, OnUpdate);
        command = new Command("anticheat.lab.console", args =>
        {
            if (!ReferenceEquals(args.Player, TSPlayer.Server) || args.Parameters.Count != 0) return;
            Interlocked.Exchange(ref snapshotRequested, 1); // Coalesce; never allocate a command queue.
        }, CommandName) { AllowServer = true, HelpText = "Read current isolated NPC state and bounded packet28 observations." };
        Commands.ChatCommands.Add(command);
        windowStart = Stopwatch.GetTimestamp();
        }
        catch
        {
            try { Dispose(); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        Exception? failure = null;
        void Cleanup(Action action)
        {
            try { action(); } catch (Exception error) { failure ??= error; }
        }
        var oldReceiver = receiver; receiver = null;
        if (oldReceiver is not null) Cleanup(oldReceiver.Dispose);
        var oldCommand = command; command = null;
        if (oldCommand is not null) Cleanup(() => Commands.ChatCommands.Remove(oldCommand));
        if (installed)
        {
            Cleanup(() => HookEvents.Terraria.NPC.StrikeNPC -= OnStrike);
            Cleanup(() => HookEvents.Terraria.NPC.NPCLoot -= OnLoot);
            Cleanup(() => HookEvents.Terraria.NetMessage.SendData -= OnSend);
            Cleanup(() => ServerApi.Hooks.GameUpdate.Deregister(registrator, OnUpdate));
        }
        installed = false;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void OnUpdate(EventArgs _)
    {
        updateThread = Environment.CurrentManagedThreadId;
        if (Interlocked.Exchange(ref snapshotRequested, 0) == 0 || observerFailed != 0) return;
        try
        {
            string? output = validatedOutput(); if (output is null) return;
            var state = new { utc = DateTimeOffset.UtcNow, source = "isolated-read-only-native-observer",
                processId = Environment.ProcessId, thread = updateThread, Main.netMode, Main.myPlayer,
                plugins = ServerApi.Plugins.Select(p => p.Plugin.GetType().FullName).ToArray(),
                eventCapacity = EventCapacity, dropped, windowSeconds = 300,
                windowExpired = Stopwatch.GetElapsedTime(windowStart) > TimeSpan.FromMinutes(5),
                npc = Main.npc.Take(200).Where(npc => npc is { active: true }).Select(npc => Snapshot(npc, -1)).ToArray(),
                events = events.ToArray() };
            string path = Path.Combine(output, "m16-combat-state.json");
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, state, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref observerFailed, 1);
            ServerApi.LogWriter.PluginWriteLine(registrator,
                "M16 passive combat diagnostics unavailable: " + error.GetType().Name, TraceLevel.Warning);
        }
    }

    private static object Snapshot(NPC npc, int sender) => new { slot = npc.whoAmI, generation = npc.generation,
        objectIdentity = RuntimeHelpers.GetHashCode(npc), type = npc.type, netId = npc.netID, aiStyle = npc.aiStyle,
        active = npc.active, life = npc.life, lifeMax = npc.lifeMax, phase = npc.ai[0],
        dontTakeDamage = npc.dontTakeDamage, justHit = npc.justHit, x = npc.Center.X, y = npc.Center.Y,
        defense = npc.defense, part0 = npc.localAI[0], part1 = npc.localAI[1], part2 = npc.localAI[2],
        senderInteraction = (uint)sender < npc.playerInteraction.Length && npc.playerInteraction[sender] };

    private void AroundReceive(NativeGetData original, MessageBuffer buffer, int start, int length, out int messageType)
    {
        bool observe = installed && observerFailed == 0 && Environment.CurrentManagedThreadId == updateThread &&
            Stopwatch.GetElapsedTime(windowStart) <= TimeSpan.FromMinutes(5) && Main.netMode == 2 &&
            (uint)buffer.whoAmI < 255 && start >= 0 && length == 11 && start <= buffer.readBuffer.Length - length &&
            buffer.readBuffer[start] == 28 && buffer.readBuffer[start + 1] < Main.npc.Length;
        NPC? target = observe ? Main.npc[buffer.readBuffer[start + 1]] : null;
        if (!observe || target is null || target.type is not (396 or 397 or 398))
        { original(buffer, start, length, out messageType); return; }
        if (events.Count >= EventCapacity) { if (dropped < long.MaxValue) dropped++; original(buffer, start, length, out messageType); return; }
        object before;
        try { before = Snapshot(target, buffer.whoAmI); }
        catch { Interlocked.Exchange(ref observerFailed, 1); original(buffer, start, length, out messageType); return; }
        string body = Convert.ToHexString(buffer.readBuffer.AsSpan(start + 1, 10));
        int damage = BinaryPrimitives.ReadInt16LittleEndian(buffer.readBuffer.AsSpan(start + 3, 2));
        var previous = current; var scope = new Scope(this, target, buffer.whoAmI); current = scope;
        string? exceptionType = null; bool returned = false;
        try { original(buffer, start, length, out messageType); returned = true; }
        catch (Exception error) { exceptionType = error.GetType().FullName; throw; }
        finally
        {
            current = previous;
            try
            {
            events.Add(new { sequence = ++sequence, utc = DateTimeOffset.UtcNow, sender = buffer.whoAmI,
                body, damage, before, after = Snapshot(target, buffer.whoAmI), returned, exceptionType,
                nativeStrikes = scope.Strikes, nativeLoot = scope.Loot, nativeRelays = scope.Relays,
                sameEntity = ReferenceEquals(Main.npc[target.whoAmI], target),
                meaning = "real GetData entry/return; cancellation cause comes from independent product decision log" });
            }
            catch { Interlocked.Exchange(ref observerFailed, 1); } // An observer cannot change the receiver's outcome.
        }
    }

    private void OnStrike(NPC target, HookEvents.Terraria.NPC.StrikeNPCEventArgs args)
    { if (current is { } scope && ReferenceEquals(scope.Observer, this) && ReferenceEquals(scope.Target, target)) scope.Strikes++; }
    private void OnLoot(NPC target, HookEvents.Terraria.NPC.NPCLootEventArgs args)
    { if (current is { } scope && ReferenceEquals(scope.Observer, this) && ReferenceEquals(scope.Target, target)) scope.Loot++; }
    private void OnSend(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        if (current is { } scope && ReferenceEquals(scope.Observer, this) && args.msgType == 28 &&
            args.number == scope.Target.whoAmI && args.ignoreClient == scope.Sender && args.ContinueExecution) scope.Relays++;
    }
}
