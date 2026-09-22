using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M7CombatNativeEvidenceTests
{
    [Test]
    public void M10_DamageCacheReusesOnlyTheUnionAndCountsEveryDeclaration()
    {
        using var fixture = new M10ArrowCacheFixture();
        var first = fixture.Observe(9);
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(1));
        Assert.That(fixture.Context.DamageUnionSupportedMatches, Is.EqualTo(1));
        var unsupported = fixture.Observe(short.MaxValue);
        Assert.That(unsupported, Is.Not.SameAs(first));
        Assert.That(unsupported.DeclaredDamage, Is.EqualTo(short.MaxValue));
        Assert.That(unsupported.DamageResults, Is.SameAs(first.DamageResults));
        Assert.That(fixture.Context.DamageUnionSupportedMatches, Is.EqualTo(1));
        fixture.Tick(); fixture.Tick();
        var later = fixture.Observe(9);
        Assert.That(later.Tick, Is.GreaterThan(first.Tick), "The envelope remains fresh even when its immutable union is reusable.");
        Assert.That(later.DamageResults, Is.SameAs(first.DamageResults));
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(1));
        Assert.That(fixture.Context.DamageUnionSupportedMatches, Is.EqualTo(2));
        M10AssertIncomplete(later);
    }

    [Test]
    public void M10_DamageCacheStillCapturesChangedNativeEffectsDuringObserveWithoutATick()
    {
        using var fixture = new M10ArrowCacheFixture();
        var first = fixture.Observe(25000);
        Assert.That(first.DamageResults!.SupportedResults.Contains(25000), Is.False);
        fixture.Player.TPlayer.rangedDamage = 250f; // Explicit native-state fixture input, not proof of a legitimate source.
        var changed = fixture.Observe(25000);
        Assert.That(changed.Tick, Is.EqualTo(first.Tick));
        Assert.That(changed.RecentSnapshots, Has.Length.EqualTo(2));
        Assert.That(changed.RecentSnapshots.Last().ObservedBowMultiplier, Is.EqualTo(250f));
        Assert.That(changed.DamageResults, Is.Not.SameAs(first.DamageResults));
        Assert.That(changed.DamageResults!.SupportedResults.Contains(25000), Is.True);
        Assert.That(first.RecentSnapshots.Single().ObservedBowMultiplier, Is.EqualTo(1f));
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(2));
        M10AssertIncomplete(changed);
    }

    [Test]
    public void M10_DamageCacheInvalidatesForEachSameTickNativeItemExport()
    {
        using var fixture = new M10ArrowCacheFixture();
        var first = fixture.Observe(1005);
        Assert.That(first.DamageResults!.SupportedResults.Contains(1005), Is.False);
        fixture.ExportItem(1000);
        var after = fixture.Observe(1005);
        Assert.That(after.Tick, Is.EqualTo(first.Tick));
        Assert.That(after.RecentSnapshots.Length, Is.EqualTo(first.RecentSnapshots.Length));
        Assert.That(after.CombatExports.Single().WireDamage, Is.EqualTo(1000));
        Assert.That(after.DamageResults!.SupportedResults.Contains(1005), Is.True);
        Assert.That(after.DamageResults.SupportedResults.Contains(3005), Is.False);
        fixture.ExportItem(3000);
        var next = fixture.Observe(3005);
        Assert.That(next.Tick, Is.EqualTo(first.Tick));
        Assert.That(next.CombatExports, Has.Length.EqualTo(2));
        Assert.That(next.DamageResults!.SupportedResults.Contains(3005), Is.True);
        Assert.That(first.CombatExports, Is.Empty);
        Assert.That(first.DamageResults.SupportedResults.Contains(1005), Is.False);
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(3));
        M10AssertIncomplete(next);
    }

    [Test]
    public void M10_DamageCacheUsesActualSsc50DuplicatePayloadEvenAfterPlayerArraysAreRestored()
    {
        using var fixture = new M10ArrowCacheFixture();
        var first = fixture.Observe(120);
        Assert.That(first.DamageResults!.SupportedResults.Contains(120), Is.False);
        int[] types = (int[])fixture.Player.TPlayer.buffType.Clone();
        int[] times = (int[])fixture.Player.TPlayer.buffTime.Clone();
        try
        {
            for (int i = 0; i < 3; i++)
            { fixture.Player.TPlayer.buffType[i] = BuffID.Archery; fixture.Player.TPlayer.buffTime[i] = 100; }
            NetMessage.SendData(50, Actor, -1, number: Actor);
        }
        finally { types.CopyTo(fixture.Player.TPlayer.buffType, 0); times.CopyTo(fixture.Player.TPlayer.buffTime, 0); }
        var after = fixture.Observe(120);
        Assert.That(after.RecentSnapshots, Has.Length.EqualTo(1), "Only the serialized export changed; current player state is identical.");
        Assert.That(after.CombatExports.Single().BuffTypes, Is.EqualTo(new[] { BuffID.Archery, BuffID.Archery, BuffID.Archery }));
        Assert.That(after.DamageResults!.SupportedResults.Contains(120), Is.True);
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(2));
        M10AssertIncomplete(after);
    }

    [Test]
    public void M10_DamageCacheInvalidatesOnNewGapsAndKeepsStickyPremisesAfterRecovery()
    {
        using var fixture = new M10ArrowCacheFixture();
        var first = fixture.Observe(9);
        fixture.Tick(verified: false);
        var unknownPlugins = fixture.Observe(9);
        Assert.That(unknownPlugins.RecentSnapshots.Length, Is.EqualTo(first.RecentSnapshots.Length));
        Assert.That(unknownPlugins.DamageResults, Is.Not.SameAs(first.DamageResults));
        Assert.That(unknownPlugins.DamageResults!.MissingPremises.Any(x => x.Contains(nameof(ArrowCandidateGap.UnverifiedPluginComposition))), Is.True);
        fixture.Tick();
        Assert.That(fixture.Observe(9).DamageResults, Is.SameAs(unknownPlugins.DamageResults));
        fixture.Player.HasSentInventory = false;
        var noInventory = fixture.Observe(9);
        Assert.That(noInventory.DamageResults, Is.Not.SameAs(unknownPlugins.DamageResults));
        Assert.That(noInventory.Gaps.HasFlag(ArrowCandidateGap.InventoryUnavailable), Is.True);
        fixture.Player.HasSentInventory = true;
        Assert.That(fixture.Observe(9).DamageResults, Is.SameAs(noInventory.DamageResults));
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(3));
        M10AssertIncomplete(noInventory);
    }

    [Test]
    public void M10_DamageCacheCannotRetainExpiredOrOverflowedExportHistoryAsComplete()
    {
        using var fixture = new M10ArrowCacheFixture();
        for (int i = 0; i < 8; i++) fixture.ExportItem(1000 + i);
        var full = fixture.Observe(1005);
        Assert.That(full.CombatExports, Has.Length.EqualTo(8));
        Assert.That(full.DamageResults!.MissingPremises, Does.Not.Contain("bounded-export-payload-history-lost-influence-not-cleared"));
        fixture.ExportItem(1008);
        var overflow = fixture.Observe(1005);
        Assert.That(overflow.DamageResults, Is.Not.SameAs(full.DamageResults));
        Assert.That(overflow.DamageResults!.MissingPremises, Does.Contain("bounded-export-payload-history-lost-influence-not-cleared"));
        Assert.That(fixture.Context.DroppedExports, Is.EqualTo(1));
        for (int i = 0; i <= M6ArrowCandidateContexts.SnapshotTtlTicks; i++) fixture.Tick();
        var expired = fixture.Observe(1005);
        Assert.That(expired.DamageResults, Is.Not.SameAs(overflow.DamageResults));
        Assert.That(expired.CombatExports, Is.Empty);
        Assert.That(expired.Gaps.HasFlag(ArrowCandidateGap.SnapshotHistoryExpired), Is.True);
        Assert.That(expired.Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.True);
        Assert.That(expired.DamageResults!.SupportedResults.Contains(1005), Is.False);
        Assert.That(expired.DamageResults.MissingPremises, Does.Contain("bounded-export-payload-history-lost-influence-not-cleared"));
        M10AssertIncomplete(expired);
    }

    [Test]
    public void M10_DamageCacheInvalidatesWhenSnapshotCapacityDiscardsAnEarlierObservation()
    {
        using var fixture = new M10ArrowCacheFixture();
        for (int i = 1; i < 8; i++) { fixture.Player.TPlayer.inventory[54].stack = 100 - i; fixture.Tick(); }
        var full = fixture.Observe(9);
        Assert.That(full.RecentSnapshots, Has.Length.EqualTo(8));
        Assert.That(full.Gaps.HasFlag(ArrowCandidateGap.SnapshotHistoryOverflow), Is.False);
        fixture.Player.TPlayer.inventory[54].stack = 92; fixture.Tick();
        var overflow = fixture.Observe(9);
        Assert.That(overflow.RecentSnapshots, Has.Length.EqualTo(8));
        Assert.That(overflow.RecentSnapshots.First().Tick, Is.GreaterThan(full.RecentSnapshots.First().Tick));
        Assert.That(overflow.Gaps.HasFlag(ArrowCandidateGap.SnapshotHistoryOverflow), Is.True);
        Assert.That(overflow.DamageResults, Is.Not.SameAs(full.DamageResults));
        Assert.That(overflow.DamageResults!.MissingPremises.Any(x => x.Contains(nameof(ArrowCandidateGap.SnapshotHistoryOverflow))), Is.True);
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(2));
        M10AssertIncomplete(overflow);
    }

    [Test]
    public void M10_DamageCacheSeesRealServerSourceAndOwnerExportGapsWithoutSnapshotChanges()
    {
        using var fixture = new M10ArrowCacheFixture();
        var first = fixture.Observe(9);
        var arrow = Main.projectile[0]; arrow.SetDefaults(ProjectileID.WoodenArrowFriendly);
        arrow.active = true; arrow.owner = Actor; arrow.key = new ProjectileKey(Actor, 3, 1);
        arrow.ApplyStatsFromSource(new EntitySource_Parent(new NPC()));
        var source = fixture.Observe(9);
        Assert.That(source.RecentSnapshots.Length, Is.EqualTo(first.RecentSnapshots.Length));
        Assert.That(source.Gaps.HasFlag(ArrowCandidateGap.ServerArrowSource), Is.True);
        Assert.That(source.DamageResults, Is.Not.SameAs(first.DamageResults));
        NetMessage.SendData(27, Actor, -1, number: 0);
        var export = fixture.Observe(9);
        Assert.That(export.Gaps.HasFlag(ArrowCandidateGap.ArrowExportToOwner), Is.True);
        Assert.That(export.DamageResults, Is.Not.SameAs(source.DamageResults));
        Assert.That(fixture.Context.DamageUnionEvaluations, Is.EqualTo(3));
        M10AssertIncomplete(export);
    }

    [TestCase("generation")]
    [TestCase("world")]
    [TestCase("server-run")]
    public void M10_DamageCacheCannotCrossSessionOrWorldIdentity(string change)
    {
        using var fixture = new M10ArrowCacheFixture();
        fixture.ExportItem(1000);
        var previous = fixture.Observe(1005); var previousSession = fixture.Session;
        Assert.That(previous.DamageResults!.SupportedResults.Contains(1005), Is.True);
        if (change == "world") fixture.Context.ResetWorld();
        else fixture.Context.Left(previousSession);
        Assert.That(fixture.Context.Observe(M10ArrowBenchmarkShot(1005), previousSession, fixture.Player), Is.Null);
        fixture.Session = change switch
        {
            "world" => previousSession with { WorldEpoch = previousSession.WorldEpoch + 1 },
            "server-run" => previousSession with { ServerRunId = Guid.NewGuid() },
            _ => previousSession with { Generation = previousSession.Generation + 1 }
        };
        fixture.Context.Connected(fixture.Session); fixture.Tick();
        var next = fixture.Observe(1005);
        Assert.That(next.DamageResults, Is.Not.SameAs(previous.DamageResults));
        Assert.That(next.CombatExports, Is.Empty);
        Assert.That(next.DamageResults!.SupportedResults.Contains(1005), Is.False);
        Assert.That(fixture.Context.Observe(M10ArrowBenchmarkShot(1005), previousSession, fixture.Player), Is.Null);
        M10AssertIncomplete(next);
    }

    private static void M10AssertIncomplete(ArrowCandidateEnvelope envelope)
    {
        Assert.That(envelope.DamageResults!.Complete, Is.False);
        Assert.That(envelope.DamageResults.AllowedResults.IsFull, Is.True);
        Assert.That(envelope.DamageResults.CanExclude(short.MaxValue), Is.False);
        Assert.That(envelope.CompleteFirstDamageUpperBound, Is.Null);
    }
    private sealed class M10ArrowCacheFixture : IDisposable
    {
        public SessionKey Session = new(Guid.NewGuid(), 1, Actor, 1);
        public readonly TSPlayer Player;
        public readonly M6ArrowCandidateContexts Context;
        private readonly List<Exception> faults = [];
        public M10ArrowCacheFixture()
        {
            Main.netMode = 2; Main.myPlayer = 255;
            Player = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37 } };
            Player.TPlayer.inventory[0].SetDefaults(ItemID.WoodenBow);
            Player.TPlayer.inventory[54].SetDefaults(ItemID.WoodenArrow); Player.TPlayer.inventory[54].stack = 100;
            Context = new(TargetRuntime.Fingerprint) { IntegrityFault = faults.Add };
            Context.Install(); Context.Connected(Session); Tick();
            Assert.That(Context.Healthy, Is.True, string.Join('\n', faults));
        }
        public void Tick(bool verified = true)
        {
            (SessionSnapshot?, TSPlayer?) Targets(int slot) => slot == Actor
                ? (new(Session, 37, false, DateTimeOffset.UtcNow), Player) : (null, null);
            Context.Tick(Session.WorldEpoch, Targets, verified);
        }
        public ArrowCandidateEnvelope Observe(short damage) => Context.Observe(M10ArrowBenchmarkShot(damage), Session, Player)
            ?? throw new InvalidOperationException("Actual candidate observation failed: " + string.Join('\n', faults));
        public void ExportItem(int damage)
        {
            Main.item[7].inner.SetDefaults(ItemID.WoodenBow); Main.item[7].inner.damage = damage;
            NetMessage.SendData(88, Actor, -1, number: 7, number2: 2);
        }
        public void Dispose()
        {
            Context.Dispose();
            Assert.That(faults, Is.Empty);
        }
    }
}
