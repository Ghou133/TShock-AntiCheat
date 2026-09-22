using System.Reflection;
using AntiCheat.Plugin.TShock;
using MonoMod.RuntimeDetour;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using Terraria.UI;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7LoadoutTransactionTests
{
    [TestCase(111)] [TestCase(1595)] [TestCase(2221)]
    [TestCase(982)] [TestCase(6188)] [TestCase(6189)]
    public void M16LegalManaAccessoriesUseTheNativeCompatibilityAndSingleEffectWithPrefixes(int type)
    {
        Assert.That(execution.ManaAccessoryEffectGuardHealthy, Is.True, execution.ManaAccessoryEffectFailureReason);
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(type); player.armor[3].Prefix(PrefixID.Arcane);
        player.armor[8].SetDefaults(type); player.armor[13].SetDefaults(type);
        Assert.That(ItemSlot.CanEquipBothAccessories(player.armor[3], player.armor[13], false), Is.False,
            "The target native UI rejects identical functional accessories; the server accepted slots are not a legality certificate.");
        int capacity = player.statManaMax2, regeneration = player.manaRegenBonus;
        player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(60), "The one native +40 and separate Arcane +20 remain.");
        Assert.That(player.manaRegenBonus - regeneration, Is.EqualTo(type is 982 or 6188 or 6189 ? 60 : 0));
        Assert.That(execution.ManaAccessoryEffectBlocks, Is.Zero);
        Assert.That(player.armor[8].type, Is.EqualTo(type)); Assert.That(player.armor[13].type, Is.EqualTo(type));
    }

    [TestCase(111)] [TestCase(1595)] [TestCase(2221)]
    [TestCase(982)] [TestCase(6188)] [TestCase(6189)]
    public void M16TransientManaAccessoryMoveKeepsSlotsAndPrefixesWhileBlockingOnlyRepeatedBaseEffects(int type)
    {
        var reports = new List<M11EquipmentEffectObservation>(); execution.EffectObservation = reports.Add;
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(type); player.armor[3].Prefix(PrefixID.Arcane);
        player.armor[4].SetDefaults(type); player.armor[4].Prefix(PrefixID.Warding);
        int capacity = player.statManaMax2, regeneration = player.manaRegenBonus, defense = player.statDefense;
        player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(60));
        Assert.That(player.statDefense - defense, Is.EqualTo(player.armor[0].defense + player.armor[3].defense + player.armor[4].defense + 4),
            "Both native GrantArmorBenefits and the independent Warding prefix remain; this contract only suppresses repeated mana additions.");
        Assert.That(player.manaRegenBonus - regeneration, Is.EqualTo(type is 982 or 6188 or 6189 ? 60 : 0));
        var report = reports.Single();
        Assert.That(report.ManaAccessoryGuardHealthy && report.NativeBodyReturned && report.InputsStable, Is.True);
        Assert.That(report.ManaCapacityBonusesBlocked, Is.EqualTo(1));
        Assert.That(report.ManaRegenerationBonusesBlocked, Is.EqualTo(type is 982 or 6188 or 6189 ? 1 : 0));
        Assert.That(player.armor[3].type, Is.EqualTo(type)); Assert.That(player.armor[4].type, Is.EqualTo(type));
        Assert.That(player.armor[3].prefix, Is.EqualTo(PrefixID.Arcane)); Assert.That(player.armor[4].prefix, Is.EqualTo(PrefixID.Warding));
        if (type is 1595 or 2221) Assert.That(player.magicCuffs, Is.True, "Separate cuff behavior survives.");
        if (type == 2221) Assert.That(player.manaMagnet, Is.True, "Separate magnet behavior survives.");
        player.armor[3].TurnToAir(); capacity = player.statManaMax2; regeneration = player.manaRegenBonus;
        player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40));
        Assert.That(player.manaRegenBonus - regeneration, Is.EqualTo(type is 982 or 6188 or 6189 ? 60 : 0));
        Assert.That(reports.Last().ManaCapacityBonusesBlocked, Is.Zero);
    }

    [Test]
    public void M16DistinctManaAccessoryTypesKeepNativeIndependentBenefits()
    {
        // Preserve every distinct type's native result; separate incompatibility groups are
        // outside this identical-type contract. The nearest two-type legal mix is checked too.
        var player = actor.TPlayer; int[] types = [111, 1595, 2221, 982, 6188];
        for (int index = 0; index < types.Length; index++) player.armor[3 + index].SetDefaults(types[index]);
        Assert.That(ItemSlot.CanEquipBothAccessories(player.armor[3], player.armor[6], false), Is.True,
            "Band of Starpower and Mana Regeneration Band provide a target-native legal different-type control.");
        int capacity = player.statManaMax2, regeneration = player.manaRegenBonus;
        player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(200));
        Assert.That(player.manaRegenBonus - regeneration, Is.EqualTo(120));
        Assert.That(execution.ManaAccessoryEffectBlocks, Is.Zero);
        Assert.That(player.magicCuffs && player.manaMagnet, Is.True);
    }

    [TestCase(111, 0)] [TestCase(982, 60)]
    public void M16NativeControlDemonstratesCapacityAndRegenerationSideEffectsAreActuallyRemoved(int type, int regen)
    {
        var player = actor.TPlayer; player.armor[3].SetDefaults(type); player.armor[4].SetDefaults(type);
        execution.Dispose();
        int capacity = player.statManaMax2, regeneration = player.manaRegenBonus;
        player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(80));
        Assert.That(player.manaRegenBonus - regeneration, Is.EqualTo(2 * regen));
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
        execution.Install(); execution.Update();
        capacity = player.statManaMax2; regeneration = player.manaRegenBonus;
        player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40));
        Assert.That(player.manaRegenBonus - regeneration, Is.EqualTo(regen));
        TestContext.Out.WriteLine($"Native UpdateEquips type={type}: capacity control80/protected40; regeneration control{2 * regen}/protected{regen}; stored items retained; no client mana claim.");
    }

    [Test]
    public void M16ManaGuardKeepsDirectNativeCallsUnsupportedHostAndGenerationReplacementNative()
    {
        var player = actor.TPlayer; player.armor[3].SetDefaults(982); player.armor[4].SetDefaults(982);
        int before = player.statManaMax2;
        player.ApplyEquipFunctional(3, player.armor[3]); player.ApplyEquipFunctional(4, player.armor[4]);
        Assert.That(player.statManaMax2 - before, Is.EqualTo(80));
        var plugins = (List<TerrariaApi.Server.PluginContainer>)typeof(TerrariaApi.Server.ServerApi)
            .GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var host = new M11UnsupportedEquipmentHost(); var container = new TerrariaApi.Server.PluginContainer(host);
        plugins.Add(container);
        try { before = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - before, Is.EqualTo(80)); }
        finally { plugins.Remove(container); }
        var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])!;
        using (var change = new Hook(method, (Action<Action<Player, int, Item>, Player, int, Item>)((original, p, slot, item) =>
        { if (slot == 4) session = session with { Generation = session.Generation + 1 }; original(p, slot, item); })))
        { before = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - before, Is.EqualTo(80)); }
        Assert.That(execution.ManaAccessoryEffectBlocks, Is.Zero);
        before = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - before, Is.EqualTo(40));
    }

    [Test]
    public void M16ManaNativeExceptionCannotLeakPriorCallScopeAndObservationFailureCannotDisableProtection()
    {
        var player = actor.TPlayer; player.armor[3].SetDefaults(982); player.armor[4].SetDefaults(982);
        var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])!;
        using (var throwing = new Hook(method, (Action<Action<Player, int, Item>, Player, int, Item>)((original, p, slot, item) =>
        { if (slot == 4) throw new InvalidOperationException("owned test interruption"); original(p, slot, item); })))
            Assert.Throws<InvalidOperationException>(() => player.UpdateEquips(Slot));
        int capacity = player.statManaMax2; player.ApplyEquipFunctional(4, player.armor[4]);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40), "No abandoned UpdateEquips scope can reach a later direct call.");
        execution.EffectObservation = _ => throw new InvalidOperationException("owned diagnostic failure");
        capacity = player.statManaMax2; player.UpdateEquips(Slot);
        Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40));
        Assert.That(execution.EffectObservationFailed, Is.True);
        Assert.That(execution.ManaAccessoryEffectGuardHealthy, Is.True);
        capacity = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40));
    }

    [Test]
    public void M16ManaScopeResetsEveryCalculationAndLoadoutAndRetainsTheBoundedObservationLimit()
    {
        var reports = new List<M11EquipmentEffectObservation>(); execution.EffectObservation = reports.Add;
        var player = actor.TPlayer;
        for (int i = 0; i < 40; i++)
        {
            player.armor[3].SetDefaults(982); if ((i & 1) == 0) player.armor[4].SetDefaults(982); else player.armor[4].TurnToAir();
            int before = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - before, Is.EqualTo(40));
        }
        Assert.That(reports.Count, Is.EqualTo(32)); Assert.That(execution.ManaAccessoryEffectBlocks, Is.EqualTo(40));
        Assert.That(Dispatch([Slot, 1, 0, 0]), Is.True);
        int capacity = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - capacity, Is.Zero);
        Assert.That(Dispatch([Slot, 0, 0, 0]), Is.True);
        capacity = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40));
    }

    [Test]
    public void M16ManaGuardWithoutCurrentWriteAuthorityOrAuthenticatedBindingKeepsTheNativeOperation()
    {
        execution.Dispose();
        bool writable = false;
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, writable) : (null, null, false), observer);
        execution.Install(); execution.Update();
        var player = actor.TPlayer; player.armor[3].SetDefaults(982); player.armor[4].SetDefaults(982);
        int capacity = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - capacity, Is.EqualTo(80));
        writable = true; actor.IsLoggedIn = false;
        capacity = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - capacity, Is.EqualTo(80));
        actor.IsLoggedIn = true;
        capacity = player.statManaMax2; player.UpdateEquips(Slot); Assert.That(player.statManaMax2 - capacity, Is.EqualTo(40));
        Assert.That(execution.ManaAccessoryEffectBlocks, Is.EqualTo(2));
    }
}
