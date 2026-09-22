using MonoMod.RuntimeDetour;
using Terraria;

namespace AntiCheat.Plugin.TShock;

public sealed partial class M8EquipmentExecutionObserver
{
    public const string ClassEmblemEffectRuleId = "B2.NativeClassEmblemFunctionalMultiplicity";
    public const string ClassEmblemEffectVersion = "1.0.0";
    private ILHook? classEmblemEffectHook;
    private bool classEmblemEffectFailed;
    public bool ClassEmblemEffectGuardHealthy => Healthy && classEmblemEffectHook is not null && !classEmblemEffectFailed;
    public string? ClassEmblemEffectFailureType { get; private set; }
    public string? ClassEmblemEffectFailureReason { get; private set; }
    public long ClassEmblemEffectBlocks { get; private set; }

    // The four adjacent native branches share the same type-specific single-field +.15 operation.
    // Distinct types are independent and may stack with each other and Avenger. Prefixes are outside
    // these blocks. A four-bit per-calculation set gives a fixed bound; it never records item history.
    private static int ClassEmblemIndex(int type) => type switch { 489 => 0, 490 => 1, 491 => 2, 2998 => 3, _ => -1 };
    private static bool IsGuardedEmblem(int type) => type == 935 || ClassEmblemIndex(type) >= 0;

    private void InstallClassEmblemEffectGuard()
    {
        try
        {
            var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])
                ?? throw new MissingMethodException(nameof(Player.ApplyEquipFunctional));
            classEmblemEffectHook = new(method, il =>
            {
                int[] types = [489, 490, 491, 2998];
                string[] fields = [nameof(Player.magicDamage), nameof(Player.meleeDamage), nameof(Player.rangedDamage), nameof(Player.minionDamage)];
                for (int index = 0; index < types.Length; index++)
                    InstrumentAdditiveEquipmentBonus(il, types[index], .15f, [fields[index]], SuppressAdditionalClassEmblemBonus);
            });
        }
        catch (Exception error) { FailClassEmblemEffect(error); }
    }

    private bool SuppressAdditionalClassEmblemBonus(Player player, int itemSlot, Item item)
    {
        try
        {
            int index = ClassEmblemIndex(item.type);
            if (!ClassEmblemEffectGuardHealthy || index < 0 || !TryGetEquipmentEffectScope(player, itemSlot, item, out var active)) return false;
            byte mask = (byte)(1 << index);
            if ((active.ClassEmblemMask & mask) == 0) { active.ClassEmblemMask |= mask; return false; }
            if (active.ClassEmblemBonusesBlocked < int.MaxValue) active.ClassEmblemBonusesBlocked++;
            if (ClassEmblemEffectBlocks < long.MaxValue) ClassEmblemEffectBlocks++;
            return true;
        }
        catch (Exception error) { FailClassEmblemEffect(error); return false; }
    }

    private void FailClassEmblemEffect(Exception error)
    {
        classEmblemEffectFailed = true;
        ClassEmblemEffectFailureType = error.GetType().FullName;
        ClassEmblemEffectFailureReason = error.Message.Length <= 256 ? error.Message : error.Message[..256];
    }
}
