using AntiCheat.Core;

namespace AntiCheat.Rules;

public enum M4VitalKind { Life, Mana, PermanentUnlocks, HurtDeclaration }
public sealed record M4VitalObservation(M4VitalKind Kind, int ClaimedSlot, int Current = 0,
    int RawMaximum = 0, byte UnlockFlags = 0, byte TorchFlags = 0, byte DifficultyFlags = 0,
    bool Pvp = false, int HurtDamage = 0, int HurtCooldown = -1);
public sealed record M4VitalContext(RuleInputContext Input, string ContractVersion,
    bool HistoryFromConnectionStart, bool Synchronizing, bool KnownPluginSet, bool ScopedPermission,
    int MaximumFromServerExports, bool ServerExportHistoryHealthy);

/// <summary>Only raw maximum declarations. No current/effective-vital, damage-response or acquisition inference.</summary>
public static class M4VitalRules
{
    public const string LifeRuleId = "VITAL01.RawLifeMaximum";
    public const string ManaRuleId = "VITAL02.RawManaMaximum";
    public const string UnlockRuleId = "VITAL03.PermanentUnlockDeclaration";
    public const string ContractVersion = "terraria326-raw-vitals-v1";
    public const string Version = "1.0.0";
    // Load clamps only upper maxima (500/200), not the residue of a lower imported value.
    // One final native increment can therefore reach 504/219 without proving the prior file's provenance.
    public const int BaselineLifeMaximum = 504;
    public const int BaselineManaMaximum = 219;

    public static string RuleId(M4VitalKind kind) => kind switch
    {
        M4VitalKind.Life => LifeRuleId, M4VitalKind.Mana => ManaRuleId, _ => UnlockRuleId
    };

    // A server-exported noncanonical maximum can be incremented by an unmodified client.
    // Life crystals (+20 below 400), life fruit (+5 below 500), and hardcore loss (-60)
    // preserve the residue modulo 5; mana crystals (+20 below 200) preserve modulo 20.
    // This is a finite upper bound, not proof of item possession or an entire acquisition model.
    public static int NativeUpperBoundAfterServerExport(M4VitalKind kind, int rawMaximum)
    {
        if (kind is not (M4VitalKind.Life or M4VitalKind.Mana) || rawMaximum < 0 || rawMaximum > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rawMaximum));
        int ceiling = kind == M4VitalKind.Life ? 500 : 200;
        int increment = kind == M4VitalKind.Life ? 5 : 20;
        return Math.Max(rawMaximum, ceiling + rawMaximum % increment);
    }

    public static BusinessRuleResult Evaluate(M4VitalObservation observation, M4VitalContext context)
    {
        string id = RuleId(observation.Kind);
        var facts = RuleResults.Facts(("claimedSlot", observation.ClaimedSlot), ("senderSlot", context.Input.Session.Slot),
            ("currentClaim", observation.Current), ("rawMaximumClaim", observation.RawMaximum),
            ("serverExportUpperBound", context.MaximumFromServerExports), ("contract", context.ContractVersion),
            ("unlockFlags", observation.UnlockFlags), ("torchFlags", observation.TorchFlags),
            ("difficultyFlags", observation.DifficultyFlags), ("currentIsNotEffectiveMaximumProof", true));
        if (!context.Input.ParserComplete || !context.Input.VersionMatched || context.ContractVersion != ContractVersion)
            return RuleResults.Unknown(id, "target-vital-contract-unavailable", facts);
        // Server handling rewrites the body playerId to whoAmI. This slice never punishes a target selected by that field.
        if (!context.Input.ClientOrigin || observation.ClaimedSlot != context.Input.Session.Slot)
            return RuleResults.Unknown(id, "vital-subject-not-self-client-declaration", facts);
        if (observation.Kind == M4VitalKind.PermanentUnlocks)
            return RuleResults.Unknown(id, "unlock-booleans-observed-acquisition-cause-not-on-wire", facts);
        if (observation.RawMaximum < 0 || observation.Current < 0)
            return RuleResults.Unknown(id, "negative-vitals-left-to-core-safety-not-cheat-proof", facts);
        int nativeCeiling = observation.Kind == M4VitalKind.Life ? BaselineLifeMaximum : BaselineManaMaximum;
        if (observation.RawMaximum <= nativeCeiling)
            return RuleResults.Pass(id, "raw-maximum-within-vanilla-envelope-current-not-adjudicated", facts);
        if (!context.HistoryFromConnectionStart || context.Synchronizing || !context.KnownPluginSet ||
            context.ScopedPermission || !context.ServerExportHistoryHealthy ||
            context.MaximumFromServerExports < nativeCeiling || context.MaximumFromServerExports > short.MaxValue)
            return RuleResults.Unknown(id, "server-export-or-scoped-vital-exceptions-incomplete", facts);
        if (observation.RawMaximum <= context.MaximumFromServerExports)
            return RuleResults.Pass(id, "raw-maximum-compatible-with-bounded-server-export", facts);
        return RuleResults.Candidate(id, "raw-maximum-exceeds-native-and-server-export-envelope", context.Input, facts);
    }
}
