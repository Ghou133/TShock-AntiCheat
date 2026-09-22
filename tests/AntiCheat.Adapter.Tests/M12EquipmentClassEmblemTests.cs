using System.Reflection;
using AntiCheat.Plugin.TShock;
using MonoMod.RuntimeDetour;
using NUnit.Framework;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7LoadoutTransactionTests
{
    private static float ClassDamage(Player player, int type) => type switch
    { 489 => player.magicDamage, 490 => player.meleeDamage, 491 => player.rangedDamage, 2998 => player.minionDamage, _ => throw new ArgumentOutOfRangeException(nameof(type)) };

    [TestCase(489)] [TestCase(490)] [TestCase(491)] [TestCase(2998)]
    public void M12SingleClassEmblemPrefixesVanityLockedStorageAndMoveRemainNative(int type)
    {
        Assert.That(execution.ClassEmblemEffectGuardHealthy, Is.True, execution.ClassEmblemEffectFailureReason);
        var player = actor.TPlayer;
        player.armor[3].SetDefaults(type); player.armor[3].Prefix(PrefixID.Menacing);
        player.armor[8].SetDefaults(type); player.armor[13].SetDefaults(type);
        float before = ClassDamage(player, type); player.UpdateEquips(Slot);
        Assert.That(ClassDamage(player, type) - before, Is.EqualTo(.19f).Within(.0001));
        Assert.That(execution.ClassEmblemEffectBlocks, Is.Zero);
        var weapon = new Item(); weapon.SetDefaults(type == 489 ? ItemID.WaterBolt : type == 490 ? ItemID.CopperBroadsword : type == 491 ? ItemID.WoodenBow : ItemID.SlimeStaff);
        int singleDamage = player.GetWeaponDamage(weapon);
        // A second functional slot can be the transient first half of a legitimate move. Protect
        // only the redundant base effect and retain both slots and their independently applied prefixes.
        player.armor[4].SetDefaults(type); player.armor[4].Prefix(PrefixID.Warding);
        before = ClassDamage(player, type); int defense = player.statDefense; player.UpdateEquips(Slot);
        Assert.That(ClassDamage(player, type) - before, Is.EqualTo(.19f).Within(.0001));
        Assert.That(player.statDefense - defense, Is.EqualTo(player.armor[0].defense + 4));
        Assert.That(execution.ClassEmblemEffectBlocks, Is.EqualTo(1));
        Assert.That(player.armor[3].type, Is.EqualTo(type)); Assert.That(player.armor[4].type, Is.EqualTo(type));
        Assert.That(player.GetWeaponDamage(weapon), Is.GreaterThanOrEqualTo(singleDamage), "The server scalar feeds native GetWeaponDamage; client-owned projectile declarations remain outside this guarantee.");
        player.armor[3].TurnToAir(); before = ClassDamage(player, type); player.UpdateEquips(Slot);
        Assert.That(ClassDamage(player, type) - before, Is.EqualTo(.15f).Within(.0001));
        Assert.That(execution.ClassEmblemEffectBlocks, Is.EqualTo(1));
        Assert.That(player.armor[8].type, Is.EqualTo(type)); Assert.That(player.armor[13].type, Is.EqualTo(type));
    }

    [Test]
    public void M12AllDistinctClassEmblemsAndAvengerStackAsOneSharedMechanism()
    {
        var player = actor.TPlayer; int[] types = [489, 490, 491, 2998, 935];
        for (int i = 0; i < types.Length; i++) player.armor[3 + i].SetDefaults(types[i]);
        float[] before = [player.magicDamage, player.meleeDamage, player.rangedDamage, player.minionDamage];
        player.UpdateEquips(Slot);
        float[] after = [player.magicDamage, player.meleeDamage, player.rangedDamage, player.minionDamage];
        for (int i = 0; i < 4; i++) Assert.That(after[i] - before[i], Is.EqualTo(.27f).Within(.0001));
        Assert.That(execution.ClassEmblemEffectBlocks, Is.Zero); Assert.That(execution.AvengerEffectBlocks, Is.Zero);
    }

    [TestCase(489)] [TestCase(490)] [TestCase(491)] [TestCase(2998)]
    public void M12WholeNativeControlAndGuardShowExactRedundantClassBenefitAndDownstreamWeaponDamage(int type)
    {
        var player = actor.TPlayer; player.armor[3].SetDefaults(type); player.armor[4].SetDefaults(type);
        var weapon = new Item(); weapon.SetDefaults(type == 489 ? ItemID.WaterBolt : type == 490 ? ItemID.CopperBroadsword : type == 491 ? ItemID.WoodenBow : ItemID.SlimeStaff);
        execution.Dispose();
        player.magicDamage = player.meleeDamage = player.rangedDamage = player.minionDamage = 1;
        player.UpdateEquips(Slot); int controlDamage = player.GetWeaponDamage(weapon);
        Assert.That(ClassDamage(player, type), Is.EqualTo(1.30f).Within(.0001));
        execution = new(TargetRuntime.Fingerprint, slot => slot == Slot ? (session, actor, true) : (null, null, false), observer);
        execution.Install(); execution.Update();
        player.magicDamage = player.meleeDamage = player.rangedDamage = player.minionDamage = 1;
        player.UpdateEquips(Slot); int protectedDamage = player.GetWeaponDamage(weapon);
        Assert.That(ClassDamage(player, type), Is.EqualTo(1.15f).Within(.0001));
        Assert.That(protectedDamage, Is.LessThan(controlDamage));
        Assert.That(execution.ClassEmblemEffectBlocks, Is.EqualTo(1));
        TestContext.Out.WriteLine($"type={type}; full native scalar control=1.30 protected=1.15; native server GetWeaponDamage {controlDamage}->{protectedDamage}; no assertion about client local projectile damage.");
    }

    [Test]
    public void M12ClassEffectsKeepAuthorizedHostAndDirectMethodSemanticsAndRecheckGeneration()
    {
        var player = actor.TPlayer; player.armor[3].SetDefaults(490); player.armor[4].SetDefaults(490);
        var plugins = (List<TerrariaApi.Server.PluginContainer>)typeof(TerrariaApi.Server.ServerApi)
            .GetField("plugins", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var host = new M11UnsupportedEquipmentHost(); var container = new TerrariaApi.Server.PluginContainer(host);
        plugins.Add(container);
        try { float before = player.meleeDamage; player.UpdateEquips(Slot); Assert.That(player.meleeDamage - before, Is.EqualTo(.30f).Within(.0001)); }
        finally { plugins.Remove(container); }
        float direct = player.meleeDamage; player.ApplyEquipFunctional(3, player.armor[3]); player.ApplyEquipFunctional(4, player.armor[4]);
        Assert.That(player.meleeDamage - direct, Is.EqualTo(.30f).Within(.0001));
        var method = typeof(Player).GetMethod(nameof(Player.ApplyEquipFunctional), [typeof(int), typeof(Item)])!;
        using (var replace = new Hook(method, (Action<Action<Player, int, Item>, Player, int, Item>)((original, p, slot, item) =>
        { if (slot == 4) session = session with { Generation = session.Generation + 1 }; original(p, slot, item); })))
        { float before = player.meleeDamage; player.UpdateEquips(Slot); Assert.That(player.meleeDamage - before, Is.EqualTo(.30f).Within(.0001)); }
        Assert.That(execution.ClassEmblemEffectBlocks, Is.Zero);
        float fresh = player.meleeDamage; player.UpdateEquips(Slot);
        Assert.That(player.meleeDamage - fresh, Is.EqualTo(.15f).Within(.0001));
    }

    [Test]
    public void M12ClassEmblemEffectEvidenceUsesExistingBoundAndSurvivesLoadoutTransitions()
    {
        var reports = new List<M11EquipmentEffectObservation>(); execution.EffectObservation = reports.Add;
        var player = actor.TPlayer;
        for (int index = 0; index < 40; index++)
        {
            player.armor[3].SetDefaults(490); if ((index & 1) == 0) player.armor[4].SetDefaults(490); else player.armor[4].TurnToAir();
            player.UpdateEquips(Slot);
        }
        Assert.That(reports.Count, Is.EqualTo(32));
        Assert.That(reports[0].ClassEmblemBonusesBlocked, Is.EqualTo(1));
        Assert.That(reports.All(x => x.ClassEmblemGuardHealthy && x.NativeBodyReturned && x.InputsStable), Is.True);
        Assert.That(execution.ClassEmblemEffectBlocks, Is.EqualTo(20));
        Assert.That(Dispatch([(byte)Slot, 1, 0, 0]), Is.True); player.UpdateEquips(Slot);
        Assert.That(player.CurrentLoadoutIndex, Is.EqualTo(1));
        Assert.That(execution.ClassEmblemEffectBlocks, Is.EqualTo(20));
    }
}
