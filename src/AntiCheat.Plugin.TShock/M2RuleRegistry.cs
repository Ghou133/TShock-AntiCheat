using AntiCheat.Core;
using AntiCheat.Rules;

namespace AntiCheat.Plugin.TShock;

public static class M2RuleRegistry
{
    public const string ContextVersion = "m2.1";
    public const string ProductionAuditReference = "docs/m14-production-qualification.md";
    public static IReadOnlyDictionary<string, string> ProductionRules { get; } =
        System.Collections.Immutable.ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string>
        {
            ["A01.EmojiSenderMismatch"] = "m2.1",
            ["A02.InventorySenderMismatch"] = "m2.1",
            [M3CheatAttemptRules.DebuffRuleId] = "1.0.0",
            [M3CheatAttemptRules.PortalRuleId] = "1.0.0",
            [M4CombatRules.RitualRuleId] = "1.0.0",
            [M4CombatRules.PortalDamageRuleId] = "1.0.0",
            [M4VitalRules.LifeRuleId] = "1.0.0",
            [M4VitalRules.ManaRuleId] = "1.0.0",
            [M3CheatAttemptRules.ChestResizeRuleId] = "1.0.0",
            [ContainerRules.RuleId] = "1.1.0",
            [M5VitalsRules.HurtRuleId] = M5VitalsRules.Version,
            [M5ProgressionRules.RuleId] = M5ProgressionRules.Version,
            [M7ArrowProjectionRules.RuleId] = M7ArrowProjectionRules.Version,
            [M7ProgressionRules.SigilRuleId] = M7ProgressionRules.Version,
            [M13NpcAuthorityRules.RuleId] = M13NpcAuthorityRules.Version,
            [M13NaturalGolemRules.RuleId] = M13NaturalGolemRules.Version,
            [M13NpcBuffRules.RuleId] = M13NpcBuffRules.Version,
            [M14NpcBuffStateRules.RuleId] = M14NpcBuffStateRules.Version,
            [M14NaturalRodRules.RuleId] = M14NaturalRodRules.Version,
            [M14LProtocolRules.RuleId] = M14LProtocolRules.Version,
            [M14LBuffAddRules.PlayerRuleId] = M14LBuffAddRules.Version,
            [M14LBuffAddRules.NpcRuleId] = M14LBuffAddRules.Version,
            [M14RNpcShimmerRules.RuleId] = M14RNpcShimmerRules.Version,
            [M14RWorldAlignmentRules.RuleId] = M14RWorldAlignmentRules.Version,
            [M14RCavernMonsterRules.RuleId] = M14RCavernMonsterRules.Version,
            [M15ProtocolRules.RuleId] = M15ProtocolRules.Version,
            [M15ProjectileRules.RuleId] = M15ProjectileRules.Version,
            [M15NpcBuffTypeRules.RuleId] = M15NpcBuffTypeRules.Version
        });
    // Independently pinned to the reviewed images, so changing TargetRuntime cannot silently
    // carry production admission to a new build. These values come from its audited target lock.
    private const string ProductionFingerprint = "tshock1458:" +
        "9704798C992623E09C847EA3DE1D2BC50B74F2F584A58CB405557C5B0DA7219F:" +
        "78C5A8B18D36C6C3CE6B8FFD016FB87FE407686845D0EEC363EC5ECAEF85C739:" +
        "C7FF9B092B296C86E1EA91B9BA8426E9F4955B4FA62DB7EA7FCFAE2EE3D1D511";

    public static IReadOnlyList<BusinessRulePolicy> Create(TargetRuntimeStatus runtime, ExecutionScope scope,
        IEnumerable<string>? progressionRules = null) => Create(runtime.Fingerprint,
            runtime.Verified && runtime.GameVersion.TrimStart('v') == "1.4.5.8" ? scope : ExecutionScope.ObserveOnly,
            progressionRules);

    // Admission is compiled and versioned. A JSON setting can select an isolated lab; it cannot add production rules.
    public static IReadOnlyList<BusinessRulePolicy> Create(string fingerprint, ExecutionScope scope, IEnumerable<string>? progressionRules = null)
    {
        var qualification = scope == ExecutionScope.TestLab ? RuleQualification.TestLab : RuleQualification.Unqualified;
        bool productionRuntime = scope == ExecutionScope.Production &&
            string.Equals(fingerprint, ProductionFingerprint, StringComparison.Ordinal) &&
            string.Equals(fingerprint, TargetRuntime.Fingerprint, StringComparison.Ordinal) &&
            TargetRuntime.TargetVersion == "1.4.5.8" && TargetRuntime.ProtocolVersion == 326 &&
            TargetRuntime.AcceptedClrVersion == "9.0.7";
        var rules = new List<BusinessRulePolicy>();
        void Add(string id, string version = "m2.1", string? auditReference = null)
        {
            bool productionAdmitted = ProductionRules.TryGetValue(id, out var reviewedVersion) && reviewedVersion == version;
            bool qualified = productionRuntime && productionAdmitted;
            rules.Add(new(id, version, fingerprint, ContextVersion,
                qualified ? RuleQualification.ProductionQualified : qualification,
                productionAdmitted ? ProductionAuditReference : auditReference ?? "docs/m2-rule-matrix.md"));
        }
        foreach (var kind in Enum.GetValues<SelfIdentityMessage>())
            Add(ProtocolRules.RuleId(kind));
        Add("A04.MessageDirection");
        Add(M3ProgressionPolicy.RuleId);
        Add(M3CheatAttemptRules.DebuffRuleId, "1.0.0");
        Add(M3CheatAttemptRules.PortalRuleId, "1.0.0");
        Add(M13NpcAuthorityRules.RuleId, M13NpcAuthorityRules.Version, "docs/m13-protocol-authority.md");
        Add(M13NaturalGolemRules.RuleId, M13NaturalGolemRules.Version, "docs/m13-natural-items.md");
        Add(M13NpcBuffRules.RuleId, M13NpcBuffRules.Version, "docs/m13-projectile-contract.md");
        Add(M14NpcBuffStateRules.RuleId, M14NpcBuffStateRules.Version, "docs/m14-npc-contract.md");
        Add(M14NaturalRodRules.RuleId, M14NaturalRodRules.Version, "docs/m14-natural-nonboss.md");
        Add(M14LProtocolRules.RuleId, M14LProtocolRules.Version, "docs/m14-low-context-protocol-projectile.md");
        Add(M14LBuffAddRules.PlayerRuleId, M14LBuffAddRules.Version, "docs/m14-low-context-buffs.md");
        Add(M14LBuffAddRules.NpcRuleId, M14LBuffAddRules.Version, "docs/m14-low-context-buffs.md");
        Add(M14RNpcShimmerRules.RuleId, M14RNpcShimmerRules.Version, "docs/m14-resume-buffs.md");
        Add(M14RWorldAlignmentRules.RuleId, M14RWorldAlignmentRules.Version, "docs/m14-resume-protocol.md");
        Add(M14RCavernMonsterRules.RuleId, M14RCavernMonsterRules.Version, "docs/m14-resume-protocol.md");
        Add(M15ProtocolRules.RuleId, M15ProtocolRules.Version, "docs/m15-protocol-projectile.md");
        Add(M15ProjectileRules.RuleId, M15ProjectileRules.Version, "docs/m15-protocol-projectile.md");
        Add(M15NpcBuffTypeRules.RuleId, M15NpcBuffTypeRules.Version, "docs/m15-buffs.md");
        Add(M3CheatAttemptRules.ChestResizeRuleId, "1.0.0");
        Add(M3BuffListReader.RuleId, M3BuffListReader.Version);
        Add(M4CombatRules.RitualRuleId, "1.0.0");
        Add("C6.PortalPlacementDamage", "1.0.0");
        Add(M4CombatContexts.NumericRuleId, "1.0.0");
        Add(M4VitalRules.LifeRuleId, "1.0.0");
        Add(M4VitalRules.ManaRuleId, "1.0.0");
        Add(M4VitalRules.UnlockRuleId, "1.0.0");
        Add(M5VitalsRules.HurtRuleId, M5VitalsRules.Version);
        Add(M5ProgressionRules.RuleId, M5ProgressionRules.Version);
        Add(M7ProgressionRules.SigilRuleId, M7ProgressionRules.Version, "docs/m5-progression-inventory.md");
        Add(M11NaturalItemRules.SolarTabletRuleId, M11NaturalItemRules.Version, "docs/m11-natural-equipment.md");
        Add(M12NaturalMechanicalRules.RuleId, M12NaturalMechanicalRules.Version, "docs/m12-gameplay.md");
        Add(M5CombatRules.ArrowEvolutionRuleId, M5CombatRules.Version);
        Add(M7ArrowProjectionRules.RuleId, M7ArrowProjectionRules.Version, "docs/m5-combat.md");
        Add(M6CombatRules.StrikeRuleId, M6CombatRules.Version);
        Add(M16CombatImmunityRules.RuleId, M16CombatImmunityRules.Version, "docs/m16-combat.md");
        Add(M9PlayerTeleportGuard.RuleId, M9PlayerTeleportGuard.Version);
        Add("C3.SentryBudget", "1.0.0");
        Add(M10SummonBudgetRules.RuleId, M10SummonBudgetRules.Version, "docs/m10-summon-budget.md");
        Add(M11SentryBudgetRules.RuleId, M11SentryBudgetRules.Version, "docs/m11-sentry-budget.md");
        Add("A05.PlayerNumeric"); Add("C0.ProjectileNumeric");
        foreach (var id in new[] { "CONTEXT", "CORE", "BOUNDS", "TILE-DOMAIN", "OBJECT-DOMAIN", "LIQUID-DOMAIN",
            "SIGN-DOMAIN", "BUDGET", "AUTHORIZATION", "TARGET", "PASS" }) Add("WORLD-" + id);
        Add(ContainerRules.RuleId, ContainerRules.Version);
        Add(ProjectileRules.AuthorityRuleId, ProjectileRules.AuthorityVersion);
        foreach (var id in new[] { "B1.ItemStructure", "B2.ActiveAccessoryConflict", "B3.AccessoryUnlock",
            "C2.ProjectileSource", "C3.SummonBudget", "C4.FishingMechanism", "C5.BuffProtocol", "C5.HealSource", "C6.WeaponProjectileCausality" })
            Add(id, "1.0.0");
        foreach (var id in (progressionRules ?? []).Distinct(StringComparer.Ordinal)) Add(id);
        return rules;
    }
}
