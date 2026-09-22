using AntiCheat.Core;
using Terraria;

namespace AntiCheat.Plugin.TShock;

public sealed record M11EquipmentEffectObservation(SessionKey Session, long AccountId, int Sequence,
    bool NativeBodyReturned, bool InputsStable, bool GuardHealthy, int AvengerBonusesApplied, int AvengerBonusesBlocked,
    float MeleeDelta, float RangedDelta, float MagicDelta, float MinionDelta, int DefenseDelta,
    M5EquipmentEffectSnapshot Before, M5EquipmentEffectSnapshot After)
{
    public bool ClassEmblemGuardHealthy { get; init; }
    public int ClassEmblemBonusesBlocked { get; init; }
    public byte ClassEmblemAppliedMask { get; init; }
    public bool ManaAccessoryGuardHealthy { get; init; }
    public int ManaCapacityBonusesBlocked { get; init; }
    public int ManaRegenerationBonusesBlocked { get; init; }
    public byte ManaCapacityAppliedMask { get; init; }
    public byte ManaRegenerationAppliedMask { get; init; }
    public int ManaCapacityDelta { get; init; }
    public int ManaRegenerationDelta { get; init; }
}

public sealed partial class M8EquipmentExecutionObserver
{
    // Existing per-call evidence, optionally exported by the host's TestLab logger. This is not
    // another equipment model or an input to enforcement. At most32 shape changes per session;
    // overflow stops logging, never changes eligibility or creates client-completion evidence.
    private sealed record EffectReportState(SessionKey Session, M5EquipmentEffectSnapshot Shape, int Count);
    private readonly EffectReportState?[] effectReportStates = new EffectReportState?[256];
    private sealed record EffectReportBefore(SessionKey Session, long AccountId, M5EquipmentEffectSnapshot Shape,
        float Melee, float Ranged, float Magic, float Minion, int Defense, int Sequence, int ManaCapacity, int ManaRegeneration);
    public Action<M11EquipmentEffectObservation>? EffectObservation { get; set; }
    public bool EffectObservationFailed { get; private set; }

    private EffectReportBefore? BeginEffectObservation(SessionKey session, Player player)
    {
        if (EffectObservation is null || EffectObservationFailed || (uint)session.Slot >= effectReportStates.Length) return null;
        try
        {
            var previous = effectReportStates[session.Slot];
            if (previous?.Session != session) previous = null;
            if (previous?.Count >= 32) return null;
            bool contains = false, changed = previous is null || previous.Shape.ActiveLoadout != player.CurrentLoadoutIndex;
            for (int slot = 0; slot < 10; slot++)
            {
                var item = player.GetEffectiveArmor(slot); contains |= IsGuardedEmblem(item.type) || ManaAccessoryIndex(item.type) >= 0;
                if (previous is not null)
                {
                    var last = previous.Shape.Slots[slot];
                    changed |= last.ItemId != item.type || last.Prefix != item.prefix || last.Stack != item.stack ||
                        last.Usable != player.IsItemSlotUnlockedAndUsable(slot) ||
                        last.SharedFromOtherLoadout != !ReferenceEquals(item, player.armor[slot]);
                }
            }
            if (!changed || !contains && previous?.Shape.Slots.Any(slot => IsGuardedEmblem(slot.ItemId) || ManaAccessoryIndex(slot.ItemId) >= 0) != true) return null;
            var binding = current(session.Slot);
            if (binding.Session != session || !binding.CanWrite || binding.Player?.Account is not { } account ||
                !ReferenceEquals(binding.Player.TPlayer, player)) return null;
            var shape = M5EquipmentContexts.Capture(session, player);
            return shape.Complete ? new(session, account.ID, shape, player.meleeDamage, player.rangedDamage,
                player.magicDamage, player.minionDamage, player.statDefense, (previous?.Count ?? 0) + 1,
                player.statManaMax2, player.manaRegenBonus) : null;
        }
        catch { EffectObservationFailed = true; return null; }
    }

    private void CompleteEffectObservation(ExecutionScope execution, EffectReportBefore before)
    {
        if (EffectObservation is not { } report || EffectObservationFailed) return;
        try
        {
            var binding = current(before.Session.Slot);
            if (binding.Session != before.Session || !binding.CanWrite || binding.Player?.Account?.ID != before.AccountId ||
                !ReferenceEquals(binding.Player.TPlayer, execution.Player) || !ReferenceEquals(Main.player[before.Session.Slot], execution.Player)) return;
            var player = execution.Player; var after = M5EquipmentContexts.Capture(before.Session, player);
            var observation = new M11EquipmentEffectObservation(before.Session, before.AccountId, before.Sequence,
                execution is { Entries: 1, Returns: 1 }, SameInputs(before.Shape, after), AvengerEffectGuardHealthy,
                execution.AvengerBonusApplied ? 1 : 0, execution.AvengerBonusesBlocked,
                player.meleeDamage - before.Melee, player.rangedDamage - before.Ranged,
                player.magicDamage - before.Magic, player.minionDamage - before.Minion,
                player.statDefense - before.Defense, before.Shape, after)
            {
                ClassEmblemGuardHealthy = ClassEmblemEffectGuardHealthy,
                ClassEmblemBonusesBlocked = execution.ClassEmblemBonusesBlocked,
                ClassEmblemAppliedMask = execution.ClassEmblemMask,
                ManaAccessoryGuardHealthy = ManaAccessoryEffectGuardHealthy,
                ManaCapacityBonusesBlocked = execution.ManaCapacityBonusesBlocked,
                ManaRegenerationBonusesBlocked = execution.ManaRegenerationBonusesBlocked,
                ManaCapacityAppliedMask = execution.ManaCapacityMask,
                ManaRegenerationAppliedMask = execution.ManaRegenerationMask,
                ManaCapacityDelta = player.statManaMax2 - before.ManaCapacity,
                ManaRegenerationDelta = player.manaRegenBonus - before.ManaRegeneration
            };
            effectReportStates[before.Session.Slot] = new(before.Session, after, before.Sequence);
            report(observation);
        }
        catch { EffectObservationFailed = true; }
    }
}
