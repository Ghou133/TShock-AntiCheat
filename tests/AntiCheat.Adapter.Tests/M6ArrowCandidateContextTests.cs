using System.Buffers.Binary;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M6ArrowCandidateContextTests
{
    private const int Slot = 7;
    private M6ArrowCandidateContexts contexts = null!;
    private TSPlayer actor = null!, other = null!;
    private SessionKey session, otherSession;
    private Player oldPlayer = null!, oldOther = null!, oldServerPlayer = null!;
    private Projectile[] oldProjectiles = null!;
    private int[,] oldMap = null!;
    private int oldMode, oldMyPlayer;
    private bool oldDedicated;
    private RemoteClient oldClient = null!;

    [SetUp]
    public void SetUp()
    {
        oldPlayer = Main.player[Slot]; oldOther = Main.player[8]; oldServerPlayer = Main.player[255]; oldProjectiles = Main.projectile;
        oldMap = Projectile.keyToIndex; oldMode = Main.netMode; oldMyPlayer = Main.myPlayer; oldClient = Netplay.Clients[Slot];
        oldDedicated = Main.dedServ; Main.dedServ = true;
        Main.netMode = 2; Main.myPlayer = 255;
        Main.player[Slot] = new Player { whoAmI = Slot, active = true };
        Main.player[8] = new Player { whoAmI = 8, active = true };
        Main.player[255] = new Player { whoAmI = 255 };
        Main.projectile = Enumerable.Range(0, 1000).Select(_ => new Projectile()).ToArray();
        Projectile.keyToIndex = new int[256, 1001]; Netplay.Clients[Slot] = new RemoteClient { State = 10 };
        actor = new TSPlayer(Slot) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 37, Name = "candidate7" } };
        other = new TSPlayer(8) { IsLoggedIn = true, HasSentInventory = true, Account = new UserAccount { ID = 38, Name = "candidate8" } };
        session = new(Guid.NewGuid(), 5, Slot, 1); otherSession = session with { Slot = 8 };
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WoodenBow);
        actor.TPlayer.inventory[54].SetDefaults(ItemID.WoodenArrow); actor.TPlayer.inventory[54].stack = 100;
        var faults = new List<Exception>();
        contexts = new(TargetRuntime.Fingerprint) { IntegrityFault = faults.Add }; contexts.Install();
        contexts.Connected(session); contexts.Connected(otherSession); Tick();
        HookEvents.Terraria.NetMessage.SendData += Sink;
        Assert.That(contexts.Healthy, Is.True, string.Join("\n", faults));
    }
    [TearDown]
    public void TearDown()
    {
        HookEvents.Terraria.NetMessage.SendData -= Sink; contexts.Dispose();
        Main.player[Slot] = oldPlayer; Main.player[8] = oldOther; Main.player[255] = oldServerPlayer; Main.projectile = oldProjectiles;
        Projectile.keyToIndex = oldMap; Main.netMode = oldMode; Main.myPlayer = oldMyPlayer; Main.dedServ = oldDedicated; Netplay.Clients[Slot] = oldClient;
    }
    private void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
    private (SessionSnapshot?, TSPlayer?) Lookup(int slot) => slot == Slot
        ? (new(session, 37, false, DateTimeOffset.UtcNow), actor)
        : slot == 8 ? (new(otherSession, 38, false, DateTimeOffset.UtcNow), other) : (null, null);
    private void Tick(bool knownPlugins = true) => contexts.Tick(session.WorldEpoch, Lookup, knownPlugins);
    private static M2Packet Shot(int slot = Slot, short damage = 1005)
    {
        byte[] body = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(slot, 3, 1).bits);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 1); body[22] = 16;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(23), damage); return new(M2PacketKind.ProjectileNew, body);
    }
    private ArrowCandidateEnvelope Envelope() => contexts.Observe(Shot(), session, actor)!;

    [Test]
    public void ActualAcceptedSnapshotsAreImmutableAndNeverPruneUnseenCanonicalSources()
    {
        var first = Envelope();
        Assert.That(first.CanonicalCandidates, Has.Length.EqualTo(43));
        Assert.That(first.MaximumCanonicalWeaponDamage, Is.EqualTo(94));
        Assert.That(first.MaximumCanonicalAmmoDamage, Is.EqualTo(5));
        Assert.That(first.UnscaledItemComponentUpperBound, Is.EqualTo(100));
        Assert.That(first.CompleteFirstDamageUpperBound, Is.Null);
        Assert.That(first.Gaps, Is.EqualTo(ArrowCandidateGap.NativeCreationBranchesNotClosed));
        Assert.That(first.CanonicalCandidates.Any(x => x.Type == ItemID.PulseBow), Is.True, "Absent current inventory does not exclude a retained holdout source.");
        actor.TPlayer.inventory[0].SetDefaults(ItemID.CopperShortsword);
        actor.TPlayer.armor[3].SetDefaults(ItemID.RangerEmblem); actor.TPlayer.armor[3].Prefix(PrefixID.Menacing);
        actor.TPlayer.buffType[0] = BuffID.Archery; actor.TPlayer.buffTime[0] = 100;
        Tick(); var second = Envelope();
        Assert.That(first.RecentSnapshots.Single().Inventory[0].Type, Is.EqualTo(ItemID.WoodenBow));
        Assert.That(second.RecentSnapshots.Last().Inventory[0].Type, Is.EqualTo(ItemID.CopperShortsword));
        Assert.That(second.RecentSnapshots.Last().ActiveBuffs, Does.Contain(BuffID.Archery));
        Assert.That(second.RecentSnapshots.Last().EffectiveArmor[3].Prefix, Is.EqualTo(PrefixID.Menacing));
        Assert.That(second.CanonicalCandidates, Is.EqualTo(first.CanonicalCandidates));
        Assert.That(contexts.Observe(Shot(), session, other), Is.Null);
    }

    [Test]
    public void NativeItem88HooksRespectRecipientAndFieldMasksWithSessionLifetimeExceptions()
    {
        NetMessage.SendData(88, Slot, -1, null, 7, 1); // Color only.
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.False);
        NetMessage.SendData(88, 8, -1, null, 7, 2); // Damage, different recipient.
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.False);
        Assert.That(contexts.Observe(Shot(8), otherSession, other)!.Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.True);
        NetMessage.SendData(88, -1, 8, null, 7, 128, 8); // Ammo customization, broadcast excluding8.
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.True);
        for (int i = 0; i <= M6ArrowCandidateContexts.SnapshotTtlTicks; i++) Tick();
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.True);
        session = session with { Generation = 2 }; contexts.Connected(session); Tick();
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ServerItemCustomization), Is.False);
    }

    [Test]
    public void BoundedHistoryExpiryOverflowAndMissingConnectionHaveExplicitGaps()
    {
        for (int i = 0; i < 20; i++) { actor.TPlayer.inventory[54].stack = 100 - i; Tick(); }
        Assert.That(Envelope().RecentSnapshots, Has.Length.EqualTo(M6ArrowCandidateContexts.SnapshotCapacity));
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.SnapshotHistoryOverflow), Is.True);
        Assert.That(contexts.RetainedSnapshotCount, Is.LessThanOrEqualTo(2 * M6ArrowCandidateContexts.SnapshotCapacity));
        for (int i = 0; i <= M6ArrowCandidateContexts.SnapshotTtlTicks; i++) Tick();
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.SnapshotHistoryExpired), Is.True);
        session = session with { Generation = 2 }; Tick();
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ConnectionStartNotObserved), Is.True);
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.SnapshotHistoryOverflow), Is.False);
        contexts.Left(session); Assert.That(contexts.Observe(Shot(), session, actor), Is.Null);
        session = session with { WorldEpoch = 6 }; contexts.ResetWorld(); Tick();
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ConnectionStartNotObserved), Is.True);
    }

    [Test]
    public void UnknownPluginCompositionCannotBecomeCompleteAfterPluginRemoval()
    {
        Tick(false); Tick(true);
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.UnverifiedPluginComposition), Is.True);
        session = session with { Generation = 2 }; contexts.Connected(session); Tick(true);
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.UnverifiedPluginComposition), Is.False);
    }

    [Test]
    public void ActualSourceAndOwnerExportHooksPreserveBroadExceptionsWithoutConfusingRelays()
    {
        var arrow = Main.projectile[0]; arrow.SetDefaults(1); arrow.key = new(Slot, 3, 1); arrow.owner = Slot; arrow.active = true;
        NetMessage.SendData(27, -1, Slot, null, 0);
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ArrowExportToOwner), Is.False);
        arrow.ApplyStatsFromSource(new EntitySource_Parent(new NPC()));
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ServerArrowSource), Is.True);
        NetMessage.SendData(27, Slot, -1, null, 0);
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ArrowExportToOwner), Is.True);
        Tick(); arrow.active = false;
        Assert.That(Envelope().Gaps.HasFlag(ArrowCandidateGap.ArrowExportToOwner), Is.True);
    }

    [Test]
    public void BadTargetCatalogFaultOnlyDisposesTheAffectedCollector()
    {
        int faults = 0;
        using var bad = new M6ArrowCandidateContexts("not-locked") { IntegrityFault = _ => faults++ };
        bad.Install(); bad.Connected(session); bad.Tick(session.WorldEpoch, Lookup, true);
        Assert.That(bad.Healthy, Is.False); Assert.That(faults, Is.EqualTo(1));
        Assert.That(contexts.Healthy, Is.True); Assert.That(Envelope(), Is.Not.Null);
    }

    [TestCase((short)9, (short)1005, true)]
    [TestCase((short)4, (short)16385, false)]
    public void ActualRoot27PathKeepsWithdrawnC6SeparateFromIndependentProjection(short initial, short candidate, bool projectionBlocks)
    {
        var oldActor = TShockAPI.TShock.Players[Slot]; var store = new Store();
        using var plugin = new AntiCheatPlugin(null!);
        using var lifecycle = new M5CombatContexts(TargetRuntime.Fingerprint);
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        session = engine.OpenSession(Slot)!.Value; Assert.That(engine.Authenticate(session, 37), Is.EqualTo(AuthenticationResult.Authenticated));
        contexts.Connected(session); Tick();
        HookEvents.Terraria.NetMessage.SendData -= Sink;
        lifecycle.Install(); lifecycle.Tick(session.WorldEpoch, Lookup);
        HookEvents.Terraria.NetMessage.SendData += Sink;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string field, object value) => typeof(AntiCheatPlugin).GetField(field, flags)!.SetValue(plugin, value);
        Set("_engine", engine); Set("_scope", ExecutionScope.TestLab);
        Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m6-candidate-native-root"));
        Set("_arrowCandidates", contexts); Set("_arrowLifecycle", lifecycle);
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", flags)!.GetValue(plugin)!).SetValue(Activator.CreateInstance(bindingType, session, actor), Slot);
        var handler = typeof(AntiCheatPlugin).GetMethod("OnGetData", flags)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        TShockAPI.TShock.Players[Slot] = actor; ServerApi.Hooks.NetGetData.Register(plugin, handler, 1000);
        try
        {
            void Receive(short damage, bool expectedHandled = false)
            {
                var input = Shot(damage: damage); var buffer = new MessageBuffer { whoAmI = Slot };
                var args = M2ContractsTests.Packet(PacketTypes.ProjectileNew, input.Payload, Slot);
                ServerApi.Hooks.NetGetData.Invoke(args); Assert.That(args.Handled, Is.EqualTo(expectedHandled));
                if (args.Handled) return;
                buffer.readBuffer[0] = 27; input.Payload.CopyTo(buffer.readBuffer, 1); buffer.ResetReader(); buffer.GetData(0, input.Payload.Length + 1, out _);
            }
            Receive(initial);
            var result = lifecycle.Evaluate(Shot(damage: candidate), session, actor, false).Single();
            Assert.That(result.RuleId, Is.EqualTo(M5CombatRules.ArrowEvolutionRuleId));
            Assert.That(result.Verdict, Is.EqualTo(Verdict.Unknown), "C6 remains withdrawn for both the original9->1005 and native4->16385 input.");
            actor.TPlayer.inventory[54].stack = 99;
            var annotated = Envelope().AddEvidence(result);
            annotated = annotated with { Facts = annotated.Facts.Add("alreadyCancelledBeforeRule", "False") };
            Assert.That(annotated.Facts, Has.Count.LessThanOrEqualTo(24)); Assert.That(annotated.Facts.Values.All(x => x.Length <= 256), Is.True);
            var decision = engine.ObserveBusiness(new(session, 27, annotated, new(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true)));
            Assert.That(decision.Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"), "Useful candidate metadata must fit the real evidence bounds.");
            Receive(candidate, projectionBlocks);
            Assert.That(Main.projectile.Single(x => x.active).damage, Is.EqualTo(projectionBlocks ? initial : candidate));
            Assert.That(Envelope().RecentSnapshots.Last().Inventory[54].Stack, Is.EqualTo(99));
            Assert.That(engine.CanWrite(session), Is.EqualTo(!projectionBlocks));
            Assert.That(engine.PumpAsync().AsTask().GetAwaiter().GetResult().Applied, Is.EqualTo(projectionBlocks ? 1 : 0));
            Assert.That(store.Banned, Is.EqualTo(projectionBlocks ? new long[] { 37 } : Array.Empty<long>()));
        }
        finally
        {
            ServerApi.Hooks.NetGetData.Deregister(plugin, handler); TShockAPI.TShock.Players[Slot] = oldActor;
            Set("_arrowCandidates", null!); Set("_arrowLifecycle", null!);
        }
    }

    private sealed class Store : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
    {
        public readonly HashSet<long> Banned = [];
        public ValueTask<IReadOnlyList<BanIntent>> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BanIntent>>([]);
        public ValueTask AppendAsync(BanIntent intent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MarkAppliedAsync(Guid incidentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnsurePermanentlyBannedAsync(BanIntent intent, CancellationToken cancellationToken = default) { Banned.Add(intent.AccountId); return ValueTask.CompletedTask; }
        public ValueTask<RunSafetyStatus> BeginRunAsync(RunSafetyRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(RunSafetyStatus.Ready);
        public ValueTask CompleteRunAsync(Guid serverRunId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
