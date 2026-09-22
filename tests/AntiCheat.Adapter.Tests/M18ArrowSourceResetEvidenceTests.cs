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

public sealed partial class M18ArrowSourceResetIntervalTests
{
    [Test]
    public void FormalEnvelopeFitsCoreWhileIndependentC7StillSanctions()
    {
        using var language = new M18NativeLanguageScope();
        var oldActor = TShockAPI.TShock.Players[Actor];
        Main.netMode = 2; Main.myPlayer = 255;
        var actor = new TSPlayer(Actor) { IsLoggedIn = true, HasSentInventory = true, Group = new Group("m18-budget"),
            Account = new UserAccount { ID = 1807, Name = "m18-budget" } };
        actor.TPlayer.inventory[0].SetDefaults(ItemID.WoodenBow);
        actor.TPlayer.inventory[54].SetDefaults(ItemID.WoodenArrow); actor.TPlayer.inventory[54].stack = 100;
        TShockAPI.TShock.Players[Actor] = actor;
        var store = new M18BudgetStore();
        var engine = new AntiCheatEngine(TimeProvider.System, new() { Scope = ExecutionScope.TestLab }, RulePolicy.Disabled,
            store, store, M2RuleRegistry.Create(TargetRuntime.Fingerprint, ExecutionScope.TestLab));
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        var session = engine.OpenSession(Actor)!.Value; Assert.That(engine.Authenticate(session, 1807), Is.EqualTo(AuthenticationResult.Authenticated));
        using var candidates = new M6ArrowCandidateContexts(TargetRuntime.Fingerprint);
        using var lifecycle = new M5CombatContexts(TargetRuntime.Fingerprint);
        using var plugin = new AntiCheatPlugin(null!);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string field, object? value) => typeof(AntiCheatPlugin).GetField(field, flags)!.SetValue(plugin, value);
        void Sink(object? _, HookEvents.Terraria.NetMessage.SendDataEventArgs args) => args.ContinueExecution = false;
        static byte[] Body(short damage)
        {
            byte[] body = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Actor, 3, 1).bits);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 1); body[22] = 16;
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(23), damage); return body;
        }
        GetDataEventArgs Root(short damage)
        {
            var body = Body(damage); var args = new GetDataEventArgs { MsgID = PacketTypes.ProjectileNew, Index = 0, Length = body.Length + 1 };
            typeof(GetDataEventArgs).GetProperty(nameof(GetDataEventArgs.Msg))!.SetValue(args, new MessageBuffer { whoAmI = Actor, readBuffer = body });
            ServerApi.Hooks.NetGetData.Invoke(args); return args;
        }
        var handler = typeof(AntiCheatPlugin).GetMethod("OnGetData", flags)!.CreateDelegate<HookHandler<GetDataEventArgs>>(plugin);
        try
        {
            candidates.Install(); candidates.Connected(session);
            candidates.Tick(session.WorldEpoch, slot => slot == Actor ? (engine.GetSession(session), actor) : (null, null), true);
            lifecycle.Install(); lifecycle.Tick(session.WorldEpoch, slot => slot == Actor ? (engine.GetSession(session), actor) : (null, null));
            HookEvents.Terraria.NetMessage.SendData += Sink;
            Set("_engine", engine); Set("_scope", ExecutionScope.TestLab); Set("_arrowCandidates", candidates); Set("_arrowLifecycle", lifecycle);
            Set("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", TargetRuntime.Fingerprint, "m18-native-budget"));
            var binding = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
            ((Array)typeof(AntiCheatPlugin).GetField("_bindings", flags)!.GetValue(plugin)!).SetValue(Activator.CreateInstance(binding, session, actor), Actor);
            ServerApi.Hooks.NetGetData.Register(plugin, handler, 1000);
            Assert.That(Root(9).Handled, Is.False); Receive(new byte[] { 27 }.Concat(Body(9)).ToArray(), Actor);
            var input = new M2Packet(M2PacketKind.ProjectileNew, Body(1005));
            var envelope = candidates.Observe(input, session, actor)!;
            var evolution = lifecycle.Evaluate(input, session, actor, false).Single();
            Assert.That(evolution.Facts.Count, Is.EqualTo(16));
            var oldAnnotation = envelope.AddEvidence(evolution);
            var rejectedDraft = oldAnnotation with { Facts = oldAnnotation.Facts.Add("sourceResetBoundary", "unavailable").Add("alreadyCancelledBeforeRule", "False") };
            var proof = new ProofContext(TargetRuntime.Fingerprint, M2RuleRegistry.ContextVersion, true, true, true, true);
            Assert.That(rejectedDraft.Facts.Count, Is.EqualTo(25));
            Assert.That(engine.ObserveBusiness(new(session, 27, rejectedDraft, proof)).Reason, Is.EqualTo("business-evidence-out-of-bounds"));
            var reset = new M18ResetIntervalSnapshot(session, 1807, [], [], 0, 1, 2, true, true, true, true, false, true, true, 0, 0, 0, "none");
            var exactPatchedEnvelope = envelope with { SourceResetInterval = reset };
            var fixedAnnotation = exactPatchedEnvelope.AddEvidence(evolution);
            fixedAnnotation = fixedAnnotation with { Facts = fixedAnnotation.Facts.Add("alreadyCancelledBeforeRule", "False") };
            Assert.That(fixedAnnotation.Facts.Count, Is.EqualTo(24));
            Assert.That(fixedAnnotation.Facts.Values.Max(value => value.Length), Is.LessThanOrEqualTo(256));
            Assert.That(fixedAnnotation.Facts.Keys, Does.Contain("sourceCandidateGaps"));
            Assert.That(fixedAnnotation.Facts["candidateProvenance"], Does.StartWith(ArrowCandidateEnvelope.Provenance));
            var candidateDecision = engine.ObserveBusiness(new(session, 27, fixedAnnotation, proof));
            Assert.That(candidateDecision.Reason, Is.EqualTo("arrow-native-internal-int16-range-unproved"));
            Assert.That(engine.CanWrite(session), Is.True);
            var projection = lifecycle.EvaluateProjection(input, session, actor, false)!;
            Assert.That(projection.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(projection.Facts.Count, Is.EqualTo(14));
            Assert.That(Root(1005).Handled, Is.True); Assert.That(engine.CanWrite(session), Is.False);
            Assert.That(Main.projectile.Single(value => value.active).damage, Is.EqualTo(9));
            engine.PumpAsync().AsTask().GetAwaiter().GetResult(); Assert.That(store.Banned, Is.EquivalentTo(new long[] { 1807 }));
            Save("m18-evidence-budget.json", new { nativeSource = "actual native27 receive/relay -> M5 lifecycle; formal integrated ArrowCandidateEnvelope.AddEvidence",
                rejectedDraftFacts = rejectedDraft.Facts.Count, fixedC6Facts = fixedAnnotation.Facts.Count,
                maxValueLength = fixedAnnotation.Facts.Values.Max(value => value.Length), candidateDecision,
                c7AdapterFacts = projection.Facts.Count, c7RootFacts = projection.Facts.Count + 1, banned = store.Banned.ToArray(),
                formalIntegratedPlugin = true, newReset = "explicit metadata model only, no damage authorization" });
        }
        finally
        {
            ServerApi.Hooks.NetGetData.Deregister(plugin, handler); HookEvents.Terraria.NetMessage.SendData -= Sink;
            Set("_arrowCandidates", null); Set("_arrowLifecycle", null); TShockAPI.TShock.Players[Actor] = oldActor;
        }
    }

    private sealed class M18BudgetStore : IEnforcementJournal, IAccountBanStore, IRunSafetyGuard
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
