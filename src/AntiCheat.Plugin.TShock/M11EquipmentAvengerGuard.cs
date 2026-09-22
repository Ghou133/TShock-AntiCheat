using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using TerrariaApi.Server;

namespace AntiCheat.Plugin.TShock;

public sealed partial class M8EquipmentExecutionObserver
{
    public const string AvengerEffectRuleId = "B2.NativeAvengerFunctionalMultiplicity";
    public const string AvengerEffectVersion = "1.0.0";
    private ILHook? avengerEffectHook;
    private bool avengerEffectFailed;
    public bool AvengerEffectGuardHealthy => Healthy && avengerEffectHook is not null && !avengerEffectFailed;
    public string? AvengerEffectFailureType { get; private set; }
    public string? AvengerEffectFailureReason { get; private set; }
    public long AvengerEffectBlocks { get; private set; }

    private void InstallAvengerEffectGuard()
    {
        try
        {
            var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])
                ?? throw new MissingMethodException(nameof(Player.ApplyEquipFunctional));
            avengerEffectHook = new(method, il => InstrumentAdditiveEquipmentBonus(il, 935, .12f,
                [nameof(Player.magicDamage), nameof(Player.meleeDamage), nameof(Player.rangedDamage), nameof(Player.minionDamage)],
                SuppressAdditionalAvengerBonus));
        }
        catch (Exception error)
        { FailAvengerEffect(error); }
    }

    private static void InstrumentAdditiveEquipmentBonus(ILContext il, int itemType, float amount,
        string[] fields, Func<Player, int, Item, bool> suppress)
    {
        // Match one exact type branch and its complete selected additive field writes. Generic
        // visuals, mount effects and all prefix/armor benefit methods retain their original execution.
        var instructions = il.Body.Instructions;
        int found = -1;
        Instruction? end = null;
        for (int i = 2; i + 2 + fields.Length * 6 < instructions.Count; i++)
        {
            if (!instructions[i].MatchLdcI4(itemType) || !instructions[i - 2].MatchLdarg(2) ||
                !instructions[i - 1].MatchLdfld<Item>(nameof(Item.type)) ||
                (instructions[i + 1].OpCode != OpCodes.Bne_Un && instructions[i + 1].OpCode != OpCodes.Bne_Un_S)) continue;
            var after = instructions[i + 1].Operand switch { Instruction target => target, ILLabel label => label.Target, _ => null };
            if (after is null) continue;
            int start = i + 2;
            bool matches = instructions[start + fields.Length * 6] == after;
            for (int field = 0; field < fields.Length && matches; field++)
            {
                int p = start + field * 6;
                matches = instructions[p].MatchLdarg(0) && instructions[p + 1].MatchLdarg(0) &&
                    instructions[p + 2].Operand is FieldReference read && read.DeclaringType.FullName == typeof(Player).FullName && read.Name == fields[field] &&
                    instructions[p + 2].OpCode == OpCodes.Ldfld && instructions[p + 3].MatchLdcR4(amount) &&
                    instructions[p + 4].OpCode == OpCodes.Add && instructions[p + 5].OpCode == OpCodes.Stfld &&
                    instructions[p + 5].Operand is FieldReference write && write.FullName == read.FullName;
            }
            if (!matches) continue;
            if (found >= 0) throw new InvalidOperationException($"Ambiguous equipment effect block for {itemType}.");
            found = start; end = after;
        }
        if (found < 0 || end is null) throw new InvalidOperationException($"Audited equipment effect block not found for {itemType}.");
        var cursor = new ILCursor(il) { Index = found };
        cursor.MoveAfterLabels();
        cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1); cursor.Emit(OpCodes.Ldarg_2);
        cursor.EmitDelegate(suppress);
        cursor.Emit(OpCodes.Brtrue, end);
    }

    private bool SuppressAdditionalAvengerBonus(Player player, int itemSlot, Item item)
    {
        try { return TrySuppressAdditionalAvengerBonus(player, itemSlot, item); }
        catch (Exception error)
        {
            // A context/host failure withdraws only this safety contract. Return to the original
            // additive block; native exceptions from its own execution are never caught here.
            FailAvengerEffect(error); return false;
        }
    }
    private void FailAvengerEffect(Exception error)
    {
        avengerEffectFailed = true;
        AvengerEffectFailureType = error.GetType().FullName;
        AvengerEffectFailureReason = error.Message.Length <= 256 ? error.Message : error.Message[..256];
    }

    private bool TrySuppressAdditionalAvengerBonus(Player player, int itemSlot, Item item)
    {
        if (!AvengerEffectGuardHealthy || item.type != 935 || !TryGetEquipmentEffectScope(player, itemSlot, item, out var active)) return false;
        if (!active.AvengerBonusApplied) { active.AvengerBonusApplied = true; return false; }
        if (active.AvengerBonusesBlocked < int.MaxValue) active.AvengerBonusesBlocked++;
        if (AvengerEffectBlocks < long.MaxValue) AvengerEffectBlocks++;
        return true;
    }

    private bool TryGetEquipmentEffectScope(Player player, int itemSlot, Item item, out ExecutionScope active)
    {
        active = scope!;
        if (active is null || active.Owner != this ||
            active.Player != player || active.Entries != 1 || active.Returns != 0 || active.EffectSession is not { } session ||
            Main.netMode != 2 || Environment.CurrentManagedThreadId != Volatile.Read(ref updateThread) ||
            itemSlot is < 3 or > 9 || item.IsAir ||
            !player.IsItemSlotUnlockedAndUsable(itemSlot) || !ReferenceEquals(player.GetEffectiveArmor(itemSlot), item)) return false;
        // Host extensions can intentionally change equipment semantics. No claim or filtering
        // is made while that contract is unknown, and the loaded host never becomes an offender.
        if (!ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(TShockAPI.TShock) || x.Plugin.GetType() == typeof(AntiCheatPlugin))) return false;
        var binding = current(active.Index);
        if (binding.Session != session || !binding.CanWrite || binding.Player is not { IsLoggedIn: true, Account: not null } actor ||
            !ReferenceEquals(actor.TPlayer, player) || !ReferenceEquals(Main.player[active.Index], player)) return false;
        return true;
    }
}
