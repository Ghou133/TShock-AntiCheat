using AntiCheat.Core;

namespace AntiCheat.Rules;

public sealed record M15CannonFiringObservation(short Damage, float Knockback, short X, short Y,
    short Angle, short Ammo, byte TargetPlayer);

/// <summary>Packet108 delegates server-activated portal-gun cannon creation to its owning client.</summary>
public static class M15ProjectileRules
{
    public const string RuleId = "PROJ01.CannonFiringAuthority";
    public const string Version = "1.0.0";
    public const string ContractVersion = "terraria1.4.5.8-326-cannon-firing-publisher-v1";

    public static BusinessRuleResult Evaluate(M15CannonFiringObservation shot, RuleInputContext input,
        bool nativePublisherContractComplete, string contractVersion = ContractVersion,
        bool? scopedClientExtensionAuthorized = false)
    {
        var facts = RuleResults.Facts(("messageId", 108), ("damage", shot.Damage), ("knockback", shot.Knockback),
            ("x", shot.X), ("y", shot.Y), ("angle", shot.Angle), ("ammo", shot.Ammo),
            ("targetPlayer", shot.TargetPlayer), ("contractVersion", contractVersion),
            ("nativePublisherContractComplete", nativePublisherContractComplete),
            ("scopedClientExtensionAuthorized", scopedClientExtensionAuthorized),
            ("proofKind", "client-attempted-server-cannon-firing-delegation"),
            ("projectileOrEquipmentHistoryRequired", false), ("targetPlayerIsResponsibleAccount", false));
        if (!input.ParserComplete || !input.VersionMatched || contractVersion != ContractVersion)
            return RuleResults.Unknown(RuleId, "cannon-firing-parser-or-contract-unverified", facts);
        if (!float.IsFinite(shot.Knockback))
            return RuleResults.Block(RuleId, "cannon-firing-nonfinite-knockback", facts);
        if (!input.ClientOrigin)
            return RuleResults.Pass(RuleId, "native-server-cannon-firing-delegation", facts);
        if (scopedClientExtensionAuthorized == true)
            return RuleResults.Pass(RuleId, "scoped-client-cannon-firing-extension", facts);
        if (!nativePublisherContractComplete || scopedClientExtensionAuthorized is null)
            return RuleResults.Unknown(RuleId, "cannon-firing-client-extension-contract-unavailable", facts);
        // The server emits108 only from ShootFromCannon's netMode2 branch. The receiving
        // owning client creates a projectile through the native path, never echoes108.
        // The requested target, damage, angle and ammo are not independent cheat proofs.
        return RuleResults.Candidate(RuleId, "original-client-cannot-delegate-cannon-firing", input, facts);
    }
}
