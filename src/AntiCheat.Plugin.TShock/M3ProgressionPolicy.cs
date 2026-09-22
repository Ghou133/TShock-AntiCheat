using System.Collections.Immutable;
using System.Text.Json;
using AntiCheat.Core;
using AntiCheat.Progression;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record M3ProgressionPolicyOptions
{
    public bool Enabled { get; init; }
    public string PolicyContract { get; init; } = "";
    public int[] WhitelistedProjectileTypes { get; init; } = [];
}

/// <summary>
/// Explicit isolated action policy, derived from the preserved MKLP King Slime condition. This is not
/// natural-impossibility evidence and does not promote the original candidate's acquisition contract.
/// </summary>
public sealed class M3ProgressionPolicy
{
    public const string RuleId = "PG-PRJ-POL-001.ActiveCreation";
    public const string PolicyContract = "m3.king-slime.fresh-own-projectile.regardless-item-history.v1";
    public const string SourceCatalogSha256 = "EF63A03FBC6F603F12EF6A62ACB5B5DA4F44B4903ABCF85106371F09BD75C4BE";
    private static readonly ImmutableHashSet<int> ExactTargets = [406, 881, 1042, 1103, 1104];
    private readonly string fingerprint;
    private readonly bool enabled;
    private readonly ImmutableHashSet<int> whitelist;
    private readonly ProgressionEntityRule rule;
    private long epoch;
    private int worldId;
    private bool downedKingSlime;
    private string seed = "";
    private DateTimeOffset captured;
    private long revision;
    private int stableTicks;
    private bool kingSlimeActive;

    public M3ProgressionPolicy(string fingerprint, ExecutionScope scope, M3ProgressionPolicyOptions options,
        ProgressionEntityRule source)
    {
        this.fingerprint = fingerprint;
        if (source.Rule.Id != "PG-PRJ-POL-001" || source.SubjectKind != ProgressionSubjectKind.Projectile
            || !source.Rule.Items.ToImmutableHashSet().SetEquals(ExactTargets)
            || source.Rule.Classification != "server_policy"
            || !ConditionMatches(source.Rule.Condition, ExpectedSourceCondition()))
            throw new InvalidDataException("The active-creation policy requires the preserved King Slime source group.");
        var entries = options.WhitelistedProjectileTypes ?? [];
        if (entries.Length > 64 || entries.Any(x => !ExactTargets.Contains(x)))
            throw new InvalidDataException("Only bounded exact policy target exemptions are supported.");
        whitelist = entries.ToImmutableHashSet();
        enabled = scope == ExecutionScope.TestLab && options.Enabled && options.PolicyContract == PolicyContract;
        // Preserve the full source condition (including its original AND/OR ordering). This separate
        // action policy explicitly applies regardless of item history; original candidate data is unchanged.
        rule = source with { Rule = source.Rule with
        {
            Id = RuleId,
            Exceptions = new Condition { Op = "any", Args =
            [
                Fact("exception.sourceWhitelistMatchesSubject"),
                Fact("exception.serverOrEnvironmentalCreation"),
                Fact("exception.synchronization")
            ] }
        } };
    }

    public static M3ProgressionPolicy? Load(string savePath, string dataDirectory, string fingerprint, ExecutionScope scope)
    {
        var options = new M3ProgressionPolicyOptions();
        string path = Path.Combine(savePath, "anticheat.json");
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 65536) throw new InvalidDataException("AntiCheat configuration exceeds 64 KiB.");
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("ProgressionActiveCreationPolicy", out var value))
                options = value.Deserialize<M3ProgressionPolicyOptions>() ?? options;
        }
        if (scope != ExecutionScope.TestLab || !options.Enabled || options.PolicyContract != PolicyContract) return null;
        string sourcePath = Path.Combine(dataDirectory, "entity-candidates.json");
        using (var sourceStream = File.OpenRead(sourcePath))
        {
            if (sourceStream.Length > 1024 * 1024
                || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceStream)) != SourceCatalogSha256)
                throw new InvalidDataException("The active-creation policy source catalog has changed; this rule is unavailable.");
        }
        var source = ProgressionEntityCatalog.Load(sourcePath)
            .Rules.Single(x => x.Rule.Id == "PG-PRJ-POL-001");
        return new(fingerprint, scope, options, source);
    }

    public bool Enabled => enabled;

    public void ResetWorld() { epoch = 0; stableTicks = 0; captured = default; revision++; }

    public void Update(long worldEpoch)
    {
        if (!enabled) return;
        bool boss = IsKingSlimeActive();
        string nextSeed = SeedSignature();
        bool changed = epoch != worldEpoch || worldId != Main.worldID || downedKingSlime != NPC.downedSlimeKing
            || seed != nextSeed || kingSlimeActive != boss;
        stableTicks = changed ? 0 : Math.Min(3, stableTicks + 1);
        if (changed) revision++;
        epoch = worldEpoch; worldId = Main.worldID; downedKingSlime = NPC.downedSlimeKing;
        seed = nextSeed; kingSlimeActive = boss; captured = DateTimeOffset.UtcNow;
    }

    public BusinessRuleResult? Evaluate(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled)
    {
        if (packet.Kind != M2PacketKind.ProjectileNew) return null;
        int type = M2PacketReader.Int16(packet.Payload, 20);
        if (!ExactTargets.Contains(type)) return null;
        if (!enabled) return Result(ControlAction.Pass, Verdict.Pass, "explicit-active-creation-policy-disabled");
        if (alreadyCancelled) return Result(ControlAction.Block, Verdict.Unknown, "preserve-existing-core-cancellation");
        if (!actor.IsLoggedIn || actor.Account is null || !actor.HasSentInventory || actor.IgnoreSSCPackets)
            return Result(ControlAction.Unknown, Verdict.Unknown, "policy-authentication-or-initial-sync-incomplete");
        // An extra runtime plugin needs a scoped grant/source adapter. Known core and this plugin are
        // checked from actual loaded types, never from a client field or a configuration 'trusted' flag.
        if (ServerApi.Plugins.Any(x => x.Plugin.GetType() != typeof(TShockAPI.TShock)
            && x.Plugin.GetType() != typeof(AntiCheatPlugin)))
            return Result(ControlAction.Unknown, Verdict.Unknown, "policy-plugin-source-contract-unavailable");
        var key = (ProjectileKey)M2PacketReader.Int32(packet.Payload, 0);
        bool found = M2ProjectileLookup.TryGet(key, out var existing, out bool lookupComplete) && existing!.active;
        if (!lookupComplete) return Result(ControlAction.Unknown, Verdict.Unknown, "policy-projectile-key-lookup-incomplete");
        if (found) return Result(ControlAction.Pass, Verdict.Pass, "existing-projectile-synchronization-exempt");
        if (key.Spawner != session.Slot) return Result(ControlAction.Unknown, Verdict.Unknown, "fresh-projectile-not-attributed-to-current-session");
        if (whitelist.Contains(type)) return Result(ControlAction.Pass, Verdict.Pass, "explicit-projectile-type-whitelist");
        var now = DateTimeOffset.UtcNow;
        bool current = epoch == session.WorldEpoch && worldId == Main.worldID && downedKingSlime == NPC.downedSlimeKing
            && seed == SeedSignature() && stableTicks >= 2 && !kingSlimeActive && !IsKingSlimeActive();
        var facts = ImmutableDictionary<string, JsonElement>.Empty
            .Add("policy.mklpEntitySubsetEnabled", JsonSerializer.SerializeToElement(enabled))
            .Add("NPC.downedSlimeKing", JsonSerializer.SerializeToElement(downedKingSlime))
            .Add("currentbossdefeated", JsonSerializer.SerializeToElement("BossDType.NA"))
            .Add("exception.sourceWhitelistMatchesSubject", JsonSerializer.SerializeToElement(false))
            .Add("exception.serverOrEnvironmentalCreation", JsonSerializer.SerializeToElement(false))
            .Add("exception.synchronization", JsonSerializer.SerializeToElement(false));
        var observation = new ProgressionActionObservation(session, type, ProgressionActionKind.CreateProjectile)
        {
            SubjectKind = ProgressionSubjectKind.Projectile, CurrentSession = session, RuntimeFingerprint = fingerprint,
            TerrariaVersion = "1.4.5.8", NowUtc = now, ParseComplete = true, Authenticated = true, ClientOrigin = true,
            ActiveActionAttributed = true, BeforeSideEffects = true,
            World = new(epoch, fingerprint, revision.ToString(), seed, captured, captured.AddSeconds(2), true, current, facts),
            Mechanism = new(RuleId, ProgressionBusinessRules.Version, "1.4.5.8", fingerprint, seed,
                "docs/m3-world-inputs.md#explicit-active-creation-policy", ExactTargets,
                [ProgressionActionKind.CreateProjectile], false, true, true)
                { SubjectKind = ProgressionSubjectKind.Projectile }
        };
        return ProgressionBusinessRules.Evaluate(rule, observation);
    }

    private static Condition Fact(string name) => new() { Op = "fact", Name = name, Expected = JsonSerializer.SerializeToElement(true) };
    private static bool ConditionMatches(Condition? actual, Condition? expected)
    {
        if (actual is null || expected is null) return actual is null && expected is null;
        if (actual.Op != expected.Op || actual.Name != expected.Name || actual.Value != expected.Value
            || actual.Expected.ValueKind != expected.Expected.ValueKind
            || (actual.Expected.ValueKind != JsonValueKind.Undefined && actual.Expected.GetRawText() != expected.Expected.GetRawText())
            || actual.Args.Length != expected.Args.Length || !ConditionMatches(actual.Arg, expected.Arg)) return false;
        for (int i = 0; i < actual.Args.Length; i++) if (!ConditionMatches(actual.Args[i], expected.Args[i])) return false;
        return true;
    }
    private static Condition ExpectedSourceCondition() => new() { Op = "all", Args =
    [
        Fact("policy.mklpEntitySubsetEnabled"),
        new Condition { Op = "all", Args =
        [
            new Condition { Op = "not", Arg = Fact("NPC.downedSlimeKing") },
            new Condition { Op = "not", Arg = new Condition { Op = "fact", Name = "currentbossdefeated",
                Expected = JsonSerializer.SerializeToElement("BossDType.KingSlime") } }
        ] }
    ] };
    // Fixed target NPC capacity, and only called for the five explicitly configured projectile types
    // during evaluation. A null/changed entity collection cannot establish a settled world.
    private static bool IsKingSlimeActive() => Main.npc is null || Main.npc.Length > 1000
        || Main.npc.Any(x => x is { active: true, type: NPCID.KingSlime });
    private static string SeedSignature() => $"world:{Main.worldID};drunk:{Main.drunkWorld};worthy:{Main.getGoodWorld};" +
        $"zenith:{Main.zenithWorld};anniversary:{Main.tenthAnniversaryWorld};remix:{Main.remixWorld}";
    private static BusinessRuleResult Result(ControlAction action, Verdict verdict, string reason) =>
        new(RuleId, ProgressionBusinessRules.Version, action, verdict, reason, false, false,
            ImmutableDictionary<string, string>.Empty.Add("policyContract", PolicyContract));
}
