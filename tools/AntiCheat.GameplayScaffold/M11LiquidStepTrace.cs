using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent.NetModules;
using Terraria.Utilities;

namespace CompatibilityAudit;

/// <summary>Lab-only observers. Native calls and RNG results are neither replaced nor replayed.
/// Full state hashing is limited to the first 64 actual scheduler attempts.</summary>
public sealed class M11LiquidStepTrace : IDisposable
{
    public const int MaximumSteps = 64, MaximumRandomSamples = 128;
    private readonly int left, top, width, height;
    private readonly Action? initialize;
    private readonly Func<bool>? initializationReady;
    private readonly Func<long> allowed, paused;
    private readonly List<Step> steps = new(MaximumSteps);
    private readonly List<object> randomSamples = new(MaximumRandomSamples);
    private Hook? outer;
    private ILHook? body, liquidUpdate;
    private bool initialized, insideBody;
    private long bodyEntries, bodyReturns, randomCalls, attempts;
    private string? randomBefore;
    private int randomObjectOrdinal;
    private int randomMaximum;
    private UnifiedRandom? currentRandom;
    private readonly List<UnifiedRandom> randomObjects = new(16);
    public IReadOnlyList<Step> Steps => steps;
    public IReadOnlyList<object> RandomSamples => randomSamples;
    public long Attempts => attempts;
    public long NativeBodyEntries => bodyEntries;
    public long NativeBodyReturns => bodyReturns;
    public long RandomCalls => randomCalls;
    public long UnhashedAttempts => Math.Max(0, attempts - MaximumSteps);
    public object? FirstBodyEntry { get; private set; }
    public object? FirstBodyExit { get; private set; }
    public string? FirstNativeException { get; private set; }
    public long NativeExceptions { get; private set; }
    public sealed record State(string Sha256, long Water, long Lava, long Honey, long Shimmer,
        long HaloLiquid, int Checking, int Queue, int Buffer, int SkipCount, double WorldTime,
        bool DayTime, string RngSha256);
    public sealed record Step(long Attempt, State Before, State After, long BodyEntries, long BodyReturns,
        long Allowed, long Paused, long RandomCalls, double ElapsedMilliseconds, bool Completed);

    public M11LiquidStepTrace(int left, int top, int width, int height, Func<long> allowed,
        Func<long> paused, Action? initialize = null, Func<bool>? initializationReady = null)
    {
        this.left = left; this.top = top; this.width = width; this.height = height;
        this.allowed = allowed; this.paused = paused; this.initialize = initialize;
        this.initializationReady = initializationReady;
    }

    public void Install()
    {
        var update = typeof(Liquid).GetMethod(nameof(Liquid.UpdateLiquid), [])!;
        try
        {
            body = new ILHook(update, il =>
            {
                var cursor = new ILCursor(il);
                cursor.EmitDelegate<Action>(() =>
                {
                    if (!initialized) return;
                    insideBody = true; bodyEntries++;
                    if (FirstBodyEntry is null) FirstBodyEntry = Entry();
                });
                int returns = 0;
                while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret))
                {
                    cursor.MoveAfterLabels();
                    cursor.EmitDelegate<Action>(() =>
                    {
                        if (!initialized) return;
                        bodyReturns++;
                        if (FirstBodyExit is null) FirstBodyExit = Entry();
                        insideBody = false;
                    });
                    cursor.Index++; returns++;
                }
                if (returns != 2) throw new NotSupportedException("Audited native liquid body must have two return sites.");
            });
            liquidUpdate = new ILHook(typeof(Liquid).GetMethod(nameof(Liquid.Update), [])!, il =>
            {
                var cursor = new ILCursor(il); int sites = 0;
                while (cursor.TryGotoNext(MoveType.Before, i => i.Operand is MethodReference m &&
                    m.DeclaringType.FullName == typeof(UnifiedRandom).FullName && m.Name == nameof(UnifiedRandom.Next) &&
                    m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int32"))
                {
                    var max = new VariableDefinition(il.Import(typeof(int))); il.Body.Variables.Add(max);
                    // Stack remains [random, max] for the unchanged original callvirt.
                    cursor.MoveAfterLabels(); cursor.Emit(OpCodes.Stloc, max); cursor.Emit(OpCodes.Dup);
                    cursor.Emit(OpCodes.Ldloc, max);
                    cursor.EmitDelegate<Action<UnifiedRandom, int>>((random, bound) =>
                    {
                        if (!insideBody) return;
                        currentRandom = random; randomMaximum = bound;
                        randomBefore = RngHash(random); randomObjectOrdinal = RandomOrdinal(random);
                    });
                    cursor.Emit(OpCodes.Ldloc, max); cursor.Index++;
                    cursor.Emit(OpCodes.Dup);
                    cursor.EmitDelegate<Action<int>>(value =>
                    {
                        if (!insideBody) return;
                        randomCalls++;
                        if (randomSamples.Count < MaximumRandomSamples) randomSamples.Add(new
                        {
                            attempt = attempts, bodyEntry = bodyEntries, ordinal = randomCalls,
                            randomObjectOrdinal, maximum = randomMaximum, value, before = randomBefore,
                            after = RngHash(currentRandom!), sameReceiverAfter = ReferenceEquals(currentRandom, WorldGen.genRand), actualCallUnmodified = true
                        });
                    });
                    sites++;
                }
                if (sites != 1) throw new NotSupportedException("Audited native liquid RNG call site changed.");
            });
            outer = new Hook(update, (Action<Action>)(original =>
            {
                // An earlier UI/console sample is not a lock on native queues. Leave this
                // ordinary call intact until the actual entry satisfies the same deadline.
                if (!initialized && initializationReady is not null && !initializationReady())
                { original(); return; }
                if (!initialized) { initialized = true; initialize?.Invoke(); }
                attempts++;
                State? before = steps.Count < MaximumSteps ? Snapshot() : null;
                long entered = bodyEntries, returned = bodyReturns, admitted = allowed(), rejected = paused(), rng = randomCalls;
                long start = Stopwatch.GetTimestamp(); bool complete = false;
                try { original(); complete = true; }
                catch (Exception error)
                {
                    NativeExceptions++;
                    if (FirstNativeException is null)
                    {
                        string detail = error.ToString();
                        FirstNativeException = detail.Length <= 8192 ? detail : detail[..8192];
                    }
                    throw;
                }
                finally
                {
                    insideBody = false;
                    if (before is not null) steps.Add(new(attempts, before, Snapshot(), bodyEntries - entered,
                        bodyReturns - returned, allowed() - admitted, paused() - rejected, randomCalls - rng,
                        Stopwatch.GetElapsedTime(start).TotalMilliseconds, complete));
                }
            }));
        }
        catch { Dispose(); throw; }
    }

    private int RandomOrdinal(UnifiedRandom random)
    {
        int index = randomObjects.FindIndex(value => ReferenceEquals(value, random));
        if (index >= 0) return index + 1;
        if (randomObjects.Count == 16) return -1;
        randomObjects.Add(random); return randomObjects.Count;
    }

    private object Entry() => new
    {
        attempts, bodyEntries, bodyReturns, Liquid.skipCount, Main.time, Main.dayTime,
        randomObjectOrdinal = RandomOrdinal(WorldGen.genRand), rng = RngState(WorldGen.genRand),
        rngSha256 = RngHash(WorldGen.genRand), state = Snapshot()
    };

    public static object RngState(UnifiedRandom random)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        for (Type? type = random.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(random);
                if (value is not null && value is not (int or uint or long or ulong or bool or int[]))
                    throw new NotSupportedException("Unsupported actual RNG state field: " + field.Name);
                fields[type.FullName + "." + field.Name] = value is int[] values ? values.ToArray() : value;
            }
        return new { type = random.GetType().AssemblyQualifiedName, complete = true, fields };
    }

    public static string RngHash(UnifiedRandom random) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(RngState(random))));

    public State Snapshot()
    {
        long[] amount = new long[4]; long halo = 0; int checking = 0;
        using var bytes = new MemoryStream(1800000); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        for (int x = left - 4; x < left + width + 4; x++) for (int y = top - 4; y < top + height + 4; y++)
        {
            var tile = Main.tile[x, y];
            writer.Write(tile.type); writer.Write(tile.wall); writer.Write(tile.liquid); writer.Write(tile.sTileHeader);
            writer.Write(tile.bTileHeader); writer.Write(tile.bTileHeader2); writer.Write(tile.bTileHeader3);
            writer.Write(tile.frameX); writer.Write(tile.frameY);
            if (x < left || x >= left + width || y < top || y >= top + height) halo += tile.liquid;
            else { amount[tile.liquidType()] += tile.liquid; if (tile.checkingLiquid()) checking++; }
        }
        foreach (var entry in Main.liquid)
        { writer.Write(entry.x); writer.Write(entry.y); writer.Write(entry.kill); writer.Write(entry.delay); }
        foreach (var entry in Main.liquidBuffer)
        { writer.Write(entry.x); writer.Write(entry.y); }
        foreach (var field in typeof(Liquid).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(int) || field.FieldType == typeof(bool)).OrderBy(field => field.Name))
        { writer.Write(field.Name); writer.Write(field.GetValue(null)!.ToString()!); }
        writer.Write(LiquidBuffer.numLiquidBuffer);
        foreach (var table in new[] { Main.tileSolid, Main.tileSolidTop, Main.tileWaterDeath, Main.tileLavaDeath })
            foreach (bool value in table) writer.Write(value);
        lock (Liquid._netChangeSet) { writer.Write(Liquid._netChangeSet.Count); foreach (int value in Liquid._netChangeSet) writer.Write(value); }
        lock (Liquid._swapNetChangeSet) { writer.Write(Liquid._swapNetChangeSet.Count); foreach (int value in Liquid._swapNetChangeSet) writer.Write(value); }
        writer.Write(JsonSerializer.Serialize(NetLiquidModule._changesByChunkCoords.Select(p => new { key = p.Key.ToString(), changes = p.Value.DirtiedPackedTileCoords.ToArray() })));
        writer.Write(Main.time); writer.Write(Main.dayTime); writer.Write(Main.raining);
        writer.Write(RngHash(WorldGen.genRand)); writer.Flush();
        return new(Convert.ToHexString(SHA256.HashData(bytes.ToArray())), amount[0], amount[1], amount[2], amount[3], halo,
            checking, Liquid.numLiquid, LiquidBuffer.numLiquidBuffer, Liquid.skipCount, Main.time, Main.dayTime, RngHash(WorldGen.genRand));
    }

    public void Dispose()
    {
        try { outer?.Dispose(); }
        finally { outer = null; body?.Dispose(); body = null; liquidUpdate?.Dispose(); liquidUpdate = null; }
    }
}
