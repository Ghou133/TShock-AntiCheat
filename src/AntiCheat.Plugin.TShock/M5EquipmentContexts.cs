using System.Collections.Immutable;
using AntiCheat.Core;
using Terraria;

namespace AntiCheat.Plugin.TShock;

public sealed record M5EquipmentEffectSlot(int Slot, int ItemId, int Prefix, int Stack,
    bool Usable, bool GrantsStatistics, bool AppliesFunctionalMethod, bool SharedFromOtherLoadout,
    bool AppliesPrefixMethod = false);
public sealed record M5EquipmentEffectSnapshot(SessionKey Session, int ActiveLoadout,
    ImmutableArray<M5EquipmentEffectSlot> Slots, bool Complete, bool AtomicClientTransitionComplete);

/// <summary>
/// Target UpdateEquips projection, without claiming that accepted armor came from a legitimate
/// acquisition or that a multi-packet client switch has finished. No timing heuristic grants either.
/// </summary>
public static class M5EquipmentContexts
{
    public static M5EquipmentEffectSnapshot Capture(SessionKey session, Player player)
    {
        if (player.armor is not { Length: >= 20 } || player.Loadouts is not { Length: 3 } ||
            player.CurrentLoadoutIndex is < 0 or >= 3)
            return new(session, player.CurrentLoadoutIndex, [], false, false);
        var result = ImmutableArray.CreateBuilder<M5EquipmentEffectSlot>(10);
        for (int slot = 0; slot < 10; slot++)
        {
            var item = player.GetEffectiveArmor(slot);
            bool usable = player.IsItemSlotUnlockedAndUsable(slot);
            bool difficultyAllowed = !item.expertOnly || Main.expertMode;
            bool statistics = !item.IsAir && usable && difficultyAllowed && player.UpdateEquips_CanItemGrantBenefits(slot, item);
            // Native UpdateEquips calls ApplyEquipFunctional for all unlocked 3..9; that method
            // excludes expertOnly in a non-expert world. Keep this separate from prefix/armor benefits.
            bool functional = slot >= 3 && usable && difficultyAllowed && !item.IsAir;
            result.Add(new(slot, item.type, item.prefix, item.stack, usable, statistics, functional,
                !ReferenceEquals(item, player.armor[slot]), statistics && item.accessory));
        }
        return new(session, player.CurrentLoadoutIndex, result.ToImmutable(), true, false);
    }
}
