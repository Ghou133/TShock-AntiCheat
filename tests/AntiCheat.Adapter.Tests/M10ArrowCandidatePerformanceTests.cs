using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

// Reuses the existing isolated native-engine setup, actual serializer and transport sink.
public sealed partial class M7CombatNativeEvidenceTests
{
    [Test, Explicit("Opt-in measurement of the actual Observe path; no timing threshold or enforcement qualification.")]
    [Category("M10DamageBenchmark")]
    public void M10_ArrowObserveBenchmark_1000Declarations_EightSnapshots_EightSerializedExports()
    {
        Main.netMode = 2; Main.myPlayer = 255;
        var session = new SessionKey(Guid.NewGuid(), 1, Actor, 1);
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WoodenBow);
        actor.TPlayer.inventory[54].SetDefaults(ItemID.WoodenArrow);
        actor.TPlayer.buffType[0] = BuffID.Archery; actor.TPlayer.buffTime[0] = 100;
        actor.TPlayer.ResetEffects(); actor.TPlayer.UpdateBuffs(Actor);
        var faults = new List<Exception>();
        using var context = new M6ArrowCandidateContexts(TargetRuntime.Fingerprint) { IntegrityFault = faults.Add };
        context.Install(); context.Connected(session);
        var target = new SessionSnapshot(session, 37, false, DateTimeOffset.UtcNow);
        (SessionSnapshot?, TSPlayer?) Targets(int slot) => slot == Actor ? (target, actor) : (null, null);
        for (int i = 0; i < M6ArrowCandidateContexts.SnapshotCapacity; i++)
        {
            actor.TPlayer.inventory[54].stack = 100 - i;
            context.Tick(1, Targets, true);
        }
        Assert.That(context.Healthy, Is.True, string.Join('\n', faults));
        // Both actual server serialization paths contribute. No private cache/state injection.
        for (int i = 0; i < 4; i++)
        {
            Main.item[7].inner.damage = 1000 + i * 100;
            NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
        }
        for (int i = 0; i < 4; i++) NetMessage.SendData(50, Actor, -1, number: Actor);
        var packets = new[] { M10ArrowBenchmarkShot(9), M10ArrowBenchmarkShot(short.MaxValue) };
        var first = context.Observe(packets[0], session, actor)!;
        Assert.That(first.RecentSnapshots, Has.Length.EqualTo(8));
        Assert.That(first.CombatExports, Has.Length.EqualTo(8));
        Assert.That(first.CombatExports.Count(x => x.PacketId == 88), Is.EqualTo(4));
        Assert.That(first.CombatExports.Count(x => x.PacketId == 50), Is.EqualTo(4));
        Assert.That(first.DamageResults!.AllowedResults.IsFull, Is.True);
        Assert.That(first.DamageResults.Complete, Is.False);
        Assert.That(first.DamageResults.SupportedResults.Contains(9), Is.True);
        Assert.That(first.DamageResults.SupportedResults.Contains(short.MaxValue), Is.False,
            "The mixed benchmark preserves a supported/unsupported declaration comparison without an enforcement threshold.");

        const int warmupIterations = 128, measuredIterations = 1000;
        for (int i = 0; i < warmupIterations; i++)
            if (context.Observe(packets[i & 1], session, actor) is null)
                throw new InvalidOperationException("Actual Observe failed during benchmark warmup.");
        long evaluationsBefore = context.DamageUnionEvaluations;
        long matchesBefore = context.DamageUnionSupportedMatches;
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long timestampBefore = Stopwatch.GetTimestamp();
        ArrowCandidateEnvelope? last = null;
        bool allAllowedFull = true, allIncomplete = true;
        for (int i = 0; i < measuredIterations; i++)
        {
            last = context.Observe(packets[i & 1], session, actor)
                ?? throw new InvalidOperationException("Actual Observe failed during measured benchmark.");
            allAllowedFull &= last.DamageResults!.AllowedResults.IsFull;
            allIncomplete &= !last.DamageResults.Complete;
        }
        long elapsedTimestamp = Stopwatch.GetTimestamp() - timestampBefore;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        long evaluations = context.DamageUnionEvaluations - evaluationsBefore;
        long supportedMatches = context.DamageUnionSupportedMatches - matchesBefore;
        double elapsedMilliseconds = elapsedTimestamp * 1000d / Stopwatch.Frequency;
        Assert.That(last, Is.Not.Null);
        Assert.That(last!.RecentSnapshots, Has.Length.EqualTo(8));
        Assert.That(last.CombatExports, Has.Length.EqualTo(8));
        Assert.That(allAllowedFull, Is.True, "Missing premises continue to admit the full Int16 domain.");
        Assert.That(allIncomplete, Is.True);
        Assert.That(supportedMatches, Is.EqualTo(measuredIterations / 2),
            "SupportedMatches is a per-declaration statistic even if a future union implementation reuses unchanged state.");
        Assert.That(faults, Is.Empty);

        var result = new
        {
            schema = 1, utc = DateTimeOffset.UtcNow,
            scenario = nameof(M10_ArrowObserveBenchmark_1000Declarations_EightSnapshots_EightSerializedExports),
            scope = "isolated-native-fixture-actual-Observe; no-GUI-no-TCP-throughput-or-production-capacity-claim",
            runtime = M10ArrowBenchmarkAssembly(typeof(Main).Assembly),
            adapter = M10ArrowBenchmarkAssembly(typeof(M6ArrowCandidateContexts).Assembly),
            rules = M10ArrowBenchmarkAssembly(typeof(M8DamageAllowedResults).Assembly),
            tests = M10ArrowBenchmarkAssembly(GetType().Assembly),
            framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            warmupIterations, measuredIterations, snapshotCount = last.RecentSnapshots.Length,
            exportCount = last.CombatExports.Length, export88Count = 4, export50Count = 4,
            nativeSerializedExports = context.SerializedExports, unchangedStateDuringMeasurement = true,
            declaredDamageValues = new[] { 9, (int)short.MaxValue }, elapsedTimestamp,
            stopwatchFrequency = Stopwatch.Frequency, elapsedMilliseconds,
            microsecondsPerObserve = elapsedMilliseconds * 1000d / measuredIterations,
            allocatedBytes, allocatedBytesPerObserve = allocatedBytes / (double)measuredIterations,
            damageUnionEvaluations = evaluations, supportedMatches,
            supportedIntervals = last.DamageResults!.SupportedResults.Intervals,
            sourceBranches = last.DamageResults.SourceBranches, missingPremises = last.DamageResults.MissingPremises,
            allAllowedFullInt16 = allAllowedFull, allIncomplete,
            performanceThreshold = (double?)null, newHardRuleQualified = false
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Progress.WriteLine("M10_ARROW_OBSERVE_BENCHMARK " + json);
        string? evidence = Environment.GetEnvironmentVariable("ANTICHEAT_M10_DAMAGE_BENCHMARK_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "m10-arrow-observe-benchmark.json"), json);
        }
    }

    private static M2Packet M10ArrowBenchmarkShot(short damage)
    {
        byte[] payload = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, new ProjectileKey(Actor, 99, 1).bits);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(20), ProjectileID.WoodenArrowFriendly); payload[22] = 16;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(23), damage);
        return new(M2PacketKind.ProjectileNew, payload);
    }
    private static object M10ArrowBenchmarkAssembly(Assembly assembly) => new
    {
        path = assembly.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
    };
}
