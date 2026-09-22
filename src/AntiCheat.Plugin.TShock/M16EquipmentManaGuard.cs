using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;

namespace AntiCheat.Plugin.TShock;

public sealed partial class M8EquipmentExecutionObserver
{
    public const string ManaAccessoryEffectRuleId = "B2.NativeManaAccessoryFunctionalMultiplicity";
    public const string ManaAccessoryEffectVersion = "1.0.0";
    private ILHook? manaAccessoryEffectHook;
    private bool manaAccessoryEffectFailed;
    public bool ManaAccessoryEffectGuardHealthy => Healthy && manaAccessoryEffectHook is not null && !manaAccessoryEffectFailed;
    public string? ManaAccessoryEffectFailureType { get; private set; }
    public string? ManaAccessoryEffectFailureReason { get; private set; }
    public long ManaAccessoryEffectBlocks { get; private set; }

    // Native ItemSlot.CanEquipBothAccessories rejects equal types. Accepted inventory slots
    // may temporarily contain both halves of a legal move, so this guards only each repeated
    // native base addition. No inventory, client-completion, acquisition or account verdict.
    private static int ManaAccessoryIndex(int type) => type switch
    { 111 => 0, 1595 => 1, 2221 => 2, 982 => 3, 6188 => 4, 6189 => 5, _ => -1 };

    private void InstallManaAccessoryEffectGuard()
    {
        try
        {
            var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])
                ?? throw new MissingMethodException(nameof(Player.ApplyEquipFunctional));
            manaAccessoryEffectHook = new(method, il =>
            {
                InstrumentManaAccessoryAdditions(il, nameof(Player.statManaMax2), 40, 2, false);
                InstrumentManaAccessoryAdditions(il, nameof(Player.manaRegenBonus), 60, 1, true);
            });
        }
        catch (Exception error) { FailManaAccessoryEffect(error); }
    }

    private void InstrumentManaAccessoryAdditions(ILContext il, string fieldName, int amount, int expectedWrites, bool regeneration)
    {
        // Audit pins every write to the selected field in this method, including the native
        // local initialization. No other statement, prefix, cuff flag or visual is skipped.
        var instructions = il.Body.Instructions;
        var starts = new List<Instruction>(expectedWrites);
        int totalWrites = 0;
        for (int index = 0; index < instructions.Count; index++)
        {
            if (!instructions[index].MatchStfld<Player>(fieldName)) continue;
            totalWrites++;
            if (index < 7 || index + 1 >= instructions.Count ||
                !instructions[index - 7].MatchLdcI4(amount) ||
                !instructions[index - 6].MatchStloc(out int variable) ||
                !instructions[index - 5].MatchLdarg(0) || !instructions[index - 4].MatchLdarg(0) ||
                !instructions[index - 3].MatchLdfld<Player>(fieldName) ||
                !instructions[index - 2].MatchLdloc(variable) || instructions[index - 1].OpCode != OpCodes.Add ||
                instructions[index].Operand is not FieldReference { FieldType.FullName: "System.Int32" })
                throw new InvalidOperationException($"Mana accessory field operation changed: {fieldName}.");
            starts.Add(instructions[index - 5]);
        }
        if (totalWrites != expectedWrites || starts.Count != expectedWrites)
            throw new InvalidOperationException($"Mana accessory field write count changed: {fieldName}.");
        foreach (var start in starts)
        {
            int index = instructions.IndexOf(start);
            var after = instructions[index + 6];
            var cursor = new ILCursor(il) { Index = index };
            cursor.MoveAfterLabels();
            cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1); cursor.Emit(OpCodes.Ldarg_2);
            cursor.EmitDelegate<Func<Player, int, Item, bool>>((player, slot, item) =>
                SuppressAdditionalManaAccessoryBonus(player, slot, item, regeneration));
            cursor.Emit(OpCodes.Brtrue, after);
        }
    }

    private bool SuppressAdditionalManaAccessoryBonus(Player player, int slot, Item item, bool regeneration)
    {
        try
        {
            int index = ManaAccessoryIndex(item.type);
            if (!ManaAccessoryEffectGuardHealthy || index < 0 || regeneration && index < 3 ||
                !TryGetEquipmentEffectScope(player, slot, item, out var active)) return false;
            byte bit = (byte)(1 << index);
            byte applied = regeneration ? active.ManaRegenerationMask : active.ManaCapacityMask;
            if ((applied & bit) == 0)
            {
                if (regeneration) active.ManaRegenerationMask |= bit;
                else active.ManaCapacityMask |= bit;
                return false;
            }
            if (regeneration)
            { if (active.ManaRegenerationBonusesBlocked < int.MaxValue) active.ManaRegenerationBonusesBlocked++; }
            else if (active.ManaCapacityBonusesBlocked < int.MaxValue) active.ManaCapacityBonusesBlocked++;
            if (ManaAccessoryEffectBlocks < long.MaxValue) ManaAccessoryEffectBlocks++;
            return true;
        }
        catch (Exception error) { FailManaAccessoryEffect(error); return false; }
    }

    private void FailManaAccessoryEffect(Exception error)
    {
        manaAccessoryEffectFailed = true;
        ManaAccessoryEffectFailureType = error.GetType().FullName;
        ManaAccessoryEffectFailureReason = error.Message.Length <= 256 ? error.Message : error.Message[..256];
    }
}
