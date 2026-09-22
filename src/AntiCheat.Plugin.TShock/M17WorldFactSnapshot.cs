using System.Collections.Immutable;
using System.Text.Json;
using Terraria;

namespace AntiCheat.Plugin.TShock;

/// <summary>The fixed native facts already used by the candidate progression adapter.
/// Capturing twenty booleans is bounded and does not scan NPCs, players or assets.</summary>
public readonly record struct M17WorldFactSnapshot(int WorldId, uint Values)
{
    private static readonly (string Name, Func<bool> Read)[] Fields =
    [
        ("Main.hardMode", () => Main.hardMode), ("Main.drunkWorld", () => Main.drunkWorld),
        ("Main.getGoodWorld", () => Main.getGoodWorld), ("Main.zenithWorld", () => Main.zenithWorld),
        ("Main.tenthAnniversaryWorld", () => Main.tenthAnniversaryWorld), ("Main.remixWorld", () => Main.remixWorld),
        ("NPC.downedSlimeKing", () => NPC.downedSlimeKing), ("NPC.downedBoss1", () => NPC.downedBoss1),
        ("NPC.downedBoss3", () => NPC.downedBoss3), ("NPC.downedDeerclops", () => NPC.downedDeerclops),
        ("NPC.downedQueenSlime", () => NPC.downedQueenSlime), ("NPC.downedMechBoss1", () => NPC.downedMechBoss1),
        ("NPC.downedMechBoss2", () => NPC.downedMechBoss2), ("NPC.downedMechBoss3", () => NPC.downedMechBoss3),
        ("NPC.downedPlantBoss", () => NPC.downedPlantBoss), ("NPC.downedGolemBoss", () => NPC.downedGolemBoss),
        ("NPC.downedAncientCultist", () => NPC.downedAncientCultist), ("NPC.downedMoonlord", () => NPC.downedMoonlord),
        ("NPC.downedFishron", () => NPC.downedFishron), ("NPC.downedEmpressOfLight", () => NPC.downedEmpressOfLight)
    ];

    public static M17WorldFactSnapshot Read()
    {
        uint values = 0;
        for (int index = 0; index < Fields.Length; index++)
            if (Fields[index].Read()) values |= 1u << index;
        return new(Main.worldID, values);
    }

    public ImmutableDictionary<string, JsonElement> ToFacts()
    {
        var facts = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        for (int index = 0; index < Fields.Length; index++)
            facts[Fields[index].Name] = JsonSerializer.SerializeToElement((Values & (1u << index)) != 0);
        facts["currentbossdefeated"] = JsonSerializer.SerializeToElement("BossDType.NA");
        // This changes only snapshot validity, not source policy or acquisition eligibility.
        facts["policy.mklpSubsetEnabled"] = JsonSerializer.SerializeToElement(false);
        return facts.ToImmutable();
    }
}
