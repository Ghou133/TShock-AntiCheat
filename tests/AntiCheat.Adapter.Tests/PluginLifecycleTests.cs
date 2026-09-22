using System.Reflection;
using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Rules;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Persistence;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class PluginLifecycleTests
{
    private AntiCheatPlugin _plugin = null!;
    private AntiCheatEngine _engine = null!;
    private string _oldSavePath = null!;
    private TSPlayer? _oldPlayer;
    private const int Slot = 7;

    [SetUp]
    public void SetUp()
    {
        _oldSavePath = ServerTShock.SavePath;
        _oldPlayer = ServerTShock.Players[Slot];
        ServerTShock.SavePath = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", "plugin-" + Guid.NewGuid().ToString("N"));
        _plugin = new AntiCheatPlugin(null!);
        _plugin.Initialize();
        _engine = (AntiCheatEngine)typeof(AntiCheatPlugin).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        Assert.That(SpinWait.SpinUntil(() => !_engine.IsMaintenanceMode, TimeSpan.FromSeconds(5)), Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        _plugin?.Dispose();
        _plugin?.ShutdownCompletion.GetAwaiter().GetResult();
        ServerTShock.Players[Slot] = _oldPlayer!;
        ServerTShock.SavePath = _oldSavePath;
    }

    [Test]
    public void ThrowingShutdownCallbackCannotSkipDispatcherOrDurableJournalCompletion()
    {
        var cancellation = (CancellationTokenSource)typeof(AntiCheatPlugin)
            .GetField("_shutdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        var dispatcher = (ServerThreadDispatcher)typeof(AntiCheatPlugin)
            .GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        using var callback = cancellation.Token.Register(() => throw new InvalidOperationException("owned-shutdown-fault"));
        var pending = dispatcher.InvokeAsync(() => Assert.Fail("Shutdown must never execute queued server writes.")).AsTask();
        Assert.DoesNotThrow(() => _plugin.Dispose());
        Assert.That(_plugin.ShutdownCompletion.GetAwaiter().GetResult(), Is.True);
        Assert.That(dispatcher.PendingCount, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => pending.GetAwaiter().GetResult());
        Assert.That(_engine.SanctionCount, Is.Zero);
        // The actual journal lock was released even though a registered cancellation callback threw.
        using var reopened = new FileEnforcementJournal(Path.Combine(ServerTShock.SavePath, "anticheat", "enforcement"));
        Assert.That(reopened.ReadAsync().AsTask().GetAwaiter().GetResult(), Is.Empty);
    }

    [Test]
    public void NormalAndUnqualifiedForeignSlotPacketsRemainPlayableAndCoreCancellationRemains()
    {
        var player = ConnectAndLogin(101);
        var legal = Packet(Slot);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False);
        var unknown = Packet(Slot + 1);
        ServerApi.Hooks.NetGetData.Invoke(unknown);
        Assert.That(unknown.Handled, Is.False, "A02 is unqualified and cannot turn mismatch into an automatic ban.");
        Assert.That(_engine.SanctionCount, Is.Zero);
        Assert.That(player.Disconnects, Is.Zero);
        var cancelled = Packet(Slot + 1);
        cancelled.Handled = true;
        ServerApi.Hooks.NetGetData.Invoke(cancelled);
        Assert.That(cancelled.Handled, Is.True);
    }

    [Test]
    public void MalformedPacketIsBlockedWithoutAccountPunishment()
    {
        var player = ConnectAndLogin(101);
        var malformed = Packet(Slot);
        malformed.Length = 2;
        ServerApi.Hooks.NetGetData.Invoke(malformed);
        Assert.That(malformed.Handled, Is.True);
        Assert.That(_engine.SanctionCount, Is.Zero);
        Assert.That(player.Disconnects, Is.Zero);
    }

    [Test]
    public void LeaveSlotReuseLogoutAndWorldChangesDoNotReuseIdentity()
    {
        var original = ConnectAndLogin(101);
        var before = Key();
        var leave = new LeaveEventArgs();
        typeof(LeaveEventArgs).GetProperty(nameof(LeaveEventArgs.Who))!.SetValue(leave, Slot);
        // Exercise the genuine hook collection from another thread, as TSAPI may do during socket reset.
        var thread = new Thread(() => ServerApi.Hooks.ServerLeave.Invoke(leave));
        thread.Start(); thread.Join();
        var next = ConnectAndLogin(202);
        var after = Key();
        Assert.That(after.Generation, Is.GreaterThan(before.Generation));
        Assert.That(_engine.GetSession(before), Is.Null);
        Assert.That(_engine.GetSession(after)!.AccountId, Is.EqualTo(202));

        PlayerHooks.OnPlayerPostLogin(original); // Delayed prior-session callback must not bind its account.
        Assert.That(_engine.GetSession(after)!.AccountId, Is.EqualTo(202));
        PlayerHooks.OnPlayerLogout(next);
        Assert.That(_engine.GetSession(Key())!.AccountId, Is.Null);
        ServerApi.Hooks.GameWorldDisconnect.Invoke(EventArgs.Empty);
        Assert.That(_engine.GetSession(after), Is.Null);
        Assert.That(_engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void UnknownRuntimeLeavesVersionSpecificFilteringToCore()
    {
        ConnectAndLogin(101);
        typeof(AntiCheatPlugin).GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_plugin, new RuntimeStatus(false, "unrecognized", "test-version-mismatch"));
        typeof(AntiCheatPlugin).GetField("_targetRuntime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_plugin, new TargetRuntimeStatus(false, "unrecognized", "unknown", "test-version-mismatch"));
        var malformedInBaseline = Packet(Slot + 1);
        malformedInBaseline.Length = 2;
        ServerApi.Hooks.NetGetData.Invoke(malformedInBaseline);
        Assert.That(malformedInBaseline.Handled, Is.False);
        Assert.That(_engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void RevocationBlocksRawAndPreRawChatOnceWithoutPunishingReusedSlot()
    {
        // Explicit test-only Core qualification; production plugin has no configuration switch for it.
        using var journal = new FileEnforcementJournal(Path.Combine(ServerTShock.SavePath, "lab-proof"));
        using var dispatch = new ServerThreadDispatcher();
        _engine = new AntiCheatEngine(TimeProvider.System, new EngineOptions { Scope = ExecutionScope.TestLab },
            new RulePolicy("lab", RuleQualification.TestLab, "test-only", [5]), journal, new TShockAccountBanStore(dispatch));
        Assert.That(_engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        typeof(AntiCheatPlugin).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_plugin, _engine);
        var player = ConnectAndLogin(101);
        var first = _engine.Observe(new(Key(), 5, Slot + 1, new("lab", "1", true, true, true, true)));
        Assert.That(first.Verdict, Is.EqualTo(Verdict.ProvenCheat));
        var packet = Packet(Slot);
        ServerApi.Hooks.NetGetData.Invoke(packet);
        Assert.That(packet.Handled, Is.True);
        ServerApi.Hooks.NetGetData.Invoke(Packet(Slot));
        Assert.That(player.Disconnects, Is.EqualTo(1));
        var chat = new ServerChatEventArgs();
        typeof(ServerChatEventArgs).GetProperty(nameof(ServerChatEventArgs.Who))!.SetValue(chat, Slot);
        ServerApi.Hooks.ServerChat.Invoke(chat);
        Assert.That(chat.Handled, Is.True);
        var replacement = ConnectAndLogin(202);
        var replacementPacket = Packet(Slot);
        ServerApi.Hooks.NetGetData.Invoke(replacementPacket);
        Assert.That(replacementPacket.Handled, Is.False);
        Assert.That(replacement.Disconnects, Is.Zero);
        Assert.That(_engine.SanctionCount, Is.EqualTo(1));
    }

    [Test]
    public void TransientJournalOwnershipFaultEntersMaintenanceAndRecoversWithoutReload()
    {
        _plugin.Dispose();
        Assert.That(_plugin.ShutdownCompletion.GetAwaiter().GetResult(), Is.True);
        var directory = Path.Combine(ServerTShock.SavePath, "anticheat", "enforcement");
        using (var held = new FileStream(Path.Combine(directory, "journal.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _plugin = new AntiCheatPlugin(null!);
            _plugin.Initialize();
            var connect = new ConnectEventArgs();
            typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
            ServerApi.Hooks.ServerConnect.Invoke(connect);
            Assert.That(connect.Handled, Is.True, "Persistence ownership failure is maintenance, not a cheating finding.");
        }
        // Advance this adapter's retry deadline explicitly; do not wait on a real game client or wall-clock sleep.
        typeof(AntiCheatPlugin).GetField("_nextAttemptTimestamp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_plugin, 0L);
        ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty);
        _engine = (AntiCheatEngine)typeof(AntiCheatPlugin).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        Assert.That(SpinWait.SpinUntil(() => !_engine.IsMaintenanceMode, TimeSpan.FromSeconds(5)), Is.True);
        ConnectAndLogin(303);
        Assert.That(_engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void PreviousArmedRunWithEmptyJournalCannotBeBypassedByObservationMode()
    {
        _plugin.Dispose();
        Assert.That(_plugin.ShutdownCompletion.GetAwaiter().GetResult(), Is.True);
        string directory = Path.Combine(ServerTShock.SavePath, "anticheat", "enforcement");
        using (var journal = new FileEnforcementJournal(directory))
        {
            var result = ((IRunSafetyGuard)journal).BeginRunAsync(new(Guid.NewGuid(), ExecutionScope.Production, true))
                .AsTask().GetAwaiter().GetResult();
            Assert.That(result, Is.EqualTo(RunSafetyStatus.Ready));
            Assert.That(journal.ReadAsync().AsTask().GetAwaiter().GetResult(), Is.Empty);
            // Simulate abrupt stop after the durable armed marker but before any intent was appended.
        }
        _plugin = new AntiCheatPlugin(null!);
        _plugin.Initialize();
        _engine = (AntiCheatEngine)typeof(AntiCheatPlugin).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        Assert.That(SpinWait.SpinUntil(() => _engine.MaintenanceReason == "previous-enforcement-run-unclean", TimeSpan.FromSeconds(5)), Is.True);
        var connect = new ConnectEventArgs();
        typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
        ServerApi.Hooks.ServerConnect.Invoke(connect);
        Assert.That(connect.Handled, Is.True);
        Assert.That(_engine.SanctionCount, Is.Zero, "Crash uncertainty is maintenance, not an invented account ban.");
        _plugin.Dispose();
        Assert.That(_plugin.ShutdownCompletion.GetAwaiter().GetResult(), Is.False);
    }

    [Test]
    public void InterruptedObservationOnlyRunDoesNotForceNormalPlayersIntoMaintenance()
    {
        _plugin.Dispose();
        Assert.That(_plugin.ShutdownCompletion.GetAwaiter().GetResult(), Is.True);
        string directory = Path.Combine(ServerTShock.SavePath, "anticheat", "enforcement");
        using (var journal = new FileEnforcementJournal(directory))
        {
            var result = ((IRunSafetyGuard)journal).BeginRunAsync(new(Guid.NewGuid(), ExecutionScope.ObserveOnly, false))
                .AsTask().GetAwaiter().GetResult();
            Assert.That(result, Is.EqualTo(RunSafetyStatus.Ready));
        }
        _plugin = new AntiCheatPlugin(null!);
        _plugin.Initialize();
        _engine = (AntiCheatEngine)typeof(AntiCheatPlugin).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        Assert.That(SpinWait.SpinUntil(() => !_engine.IsMaintenanceMode, TimeSpan.FromSeconds(5)), Is.True);
        ConnectAndLogin(404);
        Assert.That(_engine.SanctionCount, Is.Zero);
    }

    [Test]
    public void ShutdownWithMemoryOnlyProofLeavesArmedMarkerAndCannotLoseTheAccountFenceSilently()
    {
        var journal = (FileEnforcementJournal)typeof(AntiCheatPlugin).GetField("_journal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_plugin)!;
        using var dispatcher = new ServerThreadDispatcher();
        // Test-only replacement; production plugin has no admission switch and remains ObserveOnly.
        _engine = new AntiCheatEngine(TimeProvider.System, new EngineOptions { Scope = ExecutionScope.TestLab },
            new RulePolicy("lab", RuleQualification.TestLab, "test-only", [5]), journal, new TShockAccountBanStore(dispatcher));
        Assert.That(_engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        typeof(AntiCheatPlugin).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_plugin, _engine);
        ConnectAndLogin(505);
        Assert.That(_engine.Observe(new(Key(), 5, Slot + 1, new("lab", "1", true, true, true, true))).Verdict,
            Is.EqualTo(Verdict.ProvenCheat));
        Assert.That(journal.ReadAsync().AsTask().GetAwaiter().GetResult(), Is.Empty, "Intent has not been pumped yet.");
        _plugin.Dispose();
        Assert.That(_plugin.ShutdownCompletion.GetAwaiter().GetResult(), Is.False, "A memory-only incident cannot be declared clean.");
        using var nextRunJournal = new FileEnforcementJournal(Path.Combine(ServerTShock.SavePath, "anticheat", "enforcement"));
        Assert.That(nextRunJournal.BeginRunAsync(new(Guid.NewGuid(), ExecutionScope.ObserveOnly, false)).AsTask().GetAwaiter().GetResult(),
            Is.EqualTo(RunSafetyStatus.PreviousEnforcementRunUnclean));
    }

    [TestCase(PacketTypes.Emoji, false)]
    [TestCase(PacketTypes.Emoji, true)]
    [TestCase(PacketTypes.PlayerSlot, false)]
    [TestCase(PacketTypes.PlayerSlot, true)]
    public void M2ActualRegisteredParserSanctionsFirstQualifiedSelfMismatchAndRevokesLaterPackets(PacketTypes type, bool previouslyCancelled)
    {
        EnableM2Lab();
        var player = ConnectAndLogin(707);
        byte[] legalBody = type == PacketTypes.Emoji ? [(byte)Slot, 0] : [(byte)Slot, 0, 0, 1, 0, 0, 1, 0, 0];
        var legal = M2ContractsTests.Packet(type, legalBody);
        ServerApi.Hooks.NetGetData.Invoke(legal);
        Assert.That(legal.Handled, Is.False);
        Assert.That(_engine.SanctionCount, Is.Zero);
        legalBody[0] = Slot + 1;
        var violation = M2ContractsTests.Packet(type, legalBody);
        violation.Handled = previouslyCancelled;
        ServerApi.Hooks.NetGetData.Invoke(violation);
        Assert.Multiple(() =>
        {
            Assert.That(violation.Handled, Is.True);
            Assert.That(_engine.CanWrite(Key()), Is.False);
            Assert.That(_engine.SanctionCount, Is.EqualTo(1));
            Assert.That(_engine.GetSession(Key())!.AccountId, Is.EqualTo(707));
            Assert.That(player.Disconnects, Is.EqualTo(1));
        });
        var followup = Packet(Slot);
        ServerApi.Hooks.NetGetData.Invoke(followup);
        Assert.That(followup.Handled, Is.True);
        Assert.That(_engine.SanctionCount, Is.EqualTo(1));
    }

    [TestCase(999, false)]
    [TestCase(1000, false)]
    [TestCase(1001, true)]
    [TestCase(1023, true)]
    public void M2RegisteredProjectileBoundaryPreservesValidIdentityAndBlocksInvalidWithoutBan(int identity, bool blocked)
    {
        EnableM2Lab();
        ConnectAndLogin(808);
        var priorEntities = Main.projectile;
        var priorMap = Projectile.keyToIndex;
        int priorMode = Main.netMode, priorWidth = Main.maxTilesX;
        try
        {
            Main.netMode = 2; Main.maxTilesX = 4200;
            Main.projectile = [new Projectile()];
            Projectile.keyToIndex = new int[256, 1001];
            byte[] body = new byte[23];
            uint key = (uint)(Slot | identity << 8 | 1 << 18);
            BinaryPrimitives.WriteUInt32LittleEndian(body, key);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 1);
            var packet = M2ContractsTests.Packet(PacketTypes.ProjectileNew, body);
            Assert.DoesNotThrow(() => ServerApi.Hooks.NetGetData.Invoke(packet));
            Assert.That(packet.Handled, Is.EqualTo(blocked));
            Assert.That(_engine.SanctionCount, Is.Zero);
            Assert.That(_engine.CanWrite(Key()), Is.True);
            var business = GetPluginField<M2BusinessAdapter>("_business");
            var pending = (System.Collections.IDictionary)typeof(M2BusinessAdapter).GetField("pendingProjectiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(business)!;
            Assert.That(pending.Count, Is.EqualTo(blocked ? 0 : 1));
            ServerApi.Hooks.GameWorldDisconnect.Invoke(EventArgs.Empty);
            Assert.That(pending.Count, Is.Zero, "World transitions must discard even the valid pending creation.");
        }
        finally { Main.projectile = priorEntities; Projectile.keyToIndex = priorMap; Main.netMode = priorMode; Main.maxTilesX = priorWidth; }
    }

    [Test]
    public void M2ProjectileLookupRejectsCorruptMappedSlotAndOldGenerationWithoutThrowing()
    {
        var priorEntities = Main.projectile;
        var priorMap = Projectile.keyToIndex;
        int priorMode = Main.netMode, priorWidth = Main.maxTilesX;
        try
        {
            Main.netMode = 2; Main.maxTilesX = 4200;
            var key = new ProjectileKey(7, 1000, 9);
            Main.projectile = [new Projectile { key = key, active = true }];
            Projectile.keyToIndex = new int[256, 1001];
            Assert.That(M2ProjectileLookup.TryGet(key, out var found, out var healthy), Is.True);
            Assert.That(found, Is.SameAs(Main.projectile[0])); Assert.That(healthy, Is.True);
            Assert.That(M2ProjectileLookup.TryGet(new ProjectileKey(7, 1000, 8), out _, out healthy), Is.False);
            Assert.That(healthy, Is.True, "A stale key differs from a damaged table.");
            Projectile.keyToIndex[7, 1000] = Main.projectile.Length;
            Assert.That(M2ProjectileLookup.TryGet(key, out _, out healthy), Is.False);
            Assert.That(healthy, Is.False);
        }
        finally { Main.projectile = priorEntities; Projectile.keyToIndex = priorMap; Main.netMode = priorMode; Main.maxTilesX = priorWidth; }
    }

    [Test]
    public void M2HostileCategoryBlocksWithoutBanAndTableMutationRemovesOnlyItsEvidence()
    {
        EnableM2Lab(); ConnectAndLogin(909);
        var priorEntities = Main.projectile; var priorMap = Projectile.keyToIndex; var priorHostile = Main.projHostile;
        int priorMode = Main.netMode, priorWidth = Main.maxTilesX;
        try
        {
            Main.netMode = 2; Main.maxTilesX = 4200;
            Main.projectile = [new Projectile()]; Projectile.keyToIndex = new int[256, 1001];
            Main.projHostile = new bool[Terraria.ID.ProjectileID.Count]; Main.projHostile[1] = true;
            var business = GetPluginField<M2BusinessAdapter>("_business");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(M2BusinessAdapter).GetField("projectileSource", flags)!.SetValue(business, Main.projHostile);
            typeof(M2BusinessAdapter).GetField("projectileCatalog", flags)!.SetValue(business,
                new ProjectileCatalog("m2-hook-fixture", "fixture-core-hostile-flag", new Dictionary<int, ProjectileCategory>
                { [1] = new(ProjectileSourceConstraint.ServerOnly, [], []) }.ToImmutableDictionary(), true));
            byte[] body = new byte[23];
            BinaryPrimitives.WriteUInt32LittleEndian(body, new ProjectileKey(Slot, 0, 7).bits);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(20), 1);
            var hostile = M2ContractsTests.Packet(PacketTypes.ProjectileNew, body);
            ServerApi.Hooks.NetGetData.Invoke(hostile);
            Assert.That(hostile.Handled, Is.True);
            Assert.That(_engine.SanctionCount, Is.Zero, "An enemy category flag alone is not a creation-mechanism proof.");
            Main.projHostile[1] = false;
            var changed = M2ContractsTests.Packet(PacketTypes.ProjectileNew, body);
            ServerApi.Hooks.NetGetData.Invoke(changed);
            Assert.That(changed.Handled, Is.False);
            Assert.That(_engine.CanWrite(Key()), Is.True);
        }
        finally { Main.projectile = priorEntities; Projectile.keyToIndex = priorMap; Main.projHostile = priorHostile; Main.netMode = priorMode; Main.maxTilesX = priorWidth; }
    }

    [Test]
    public void M2BusinessContextFaultStillDrainsSanctionDispatcherAndPreservesIndependentIdentityRules()
    {
        EnableM2Lab(); ConnectAndLogin(1001);
        var business = GetPluginField<M2BusinessAdapter>("_business");
        // Inject a broken table builder, as distinct from any client cheating evidence.
        typeof(M2BusinessAdapter).GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(business, null);
        int priorMode = Main.netMode, priorWidth = Main.maxTilesX;
        try
        {
            Main.netMode = 2; Main.maxTilesX = 4200;
            int calls = 0;
            var queued = GetPluginField<ServerThreadDispatcher>("_dispatcher").InvokeAsync(() => calls++).AsTask();
            ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(queued.IsCompletedSuccessfully, Is.True);
            Assert.That(GetPluginField<M2BusinessAdapter?>("_business"), Is.SameAs(business));
            Assert.That(business.ItemTableReady, Is.False);
            Assert.That(_engine.SanctionCount, Is.Zero);
            var proof = M2ContractsTests.Packet(PacketTypes.Emoji, [(byte)(Slot + 1), 0]);
            ServerApi.Hooks.NetGetData.Invoke(proof);
            Assert.That(proof.Handled, Is.True); Assert.That(_engine.SanctionCount, Is.EqualTo(1));
        }
        finally { Main.netMode = priorMode; Main.maxTilesX = priorWidth; }
    }

    private T GetPluginField<T>(string name) => (T)typeof(AntiCheatPlugin).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;

    [TestCase("ItemTable", "C1")]
    [TestCase("ItemTable", "C5")]
    [TestCase("CombatTick", "C1")]
    [TestCase("CombatTick", "C5")]
    [TestCase("CombatPacket", "C1")]
    [TestCase("CombatPacket", "C5")]
    [TestCase("InventoryTick", "C1")]
    [TestCase("InventoryPacket", "C5")]
    [TestCase("ProgressionBoundary", "C1")]
    [TestCase("ProgressionBoundary", "C5")]
    [TestCase("ProgressionSnapshot", "C1")]
    [TestCase("ProgressionSnapshot", "C5")]
    public void ProducerFaultPreservesOtherContextsLegalPacketsAndFirstIndependentProof(string fault, string proof)
    {
        EnableM2Lab();
        var oldPlayer = Main.player[Slot]; var oldEntities = Main.projectile; var oldMap = Projectile.keyToIndex;
        var oldChests = Main.chest; var oldChestIndex = Chest._chestsByCoords; var oldConfig = ServerTShock.Config;
        int oldMode = Main.netMode, oldWidth = Main.maxTilesX, oldHeight = Main.maxTilesY;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var buffField = typeof(TSPlayer).Assembly.GetType("TShockAPI.Bouncer")!
            .GetField("PlayerAddBuffWhitelist", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldBuffs = buffField.GetValue(null);
        try
        {
            Main.netMode = 2; Main.maxTilesX = 4200; Main.maxTilesY = 1200;
            Main.player[Slot] = new Player { whoAmI = Slot, active = true, position = new Microsoft.Xna.Framework.Vector2(320, 320), chest = -1 };
            Main.projectile = [new Projectile()]; Projectile.keyToIndex = new int[256, 1001];
            Main.chest = new Chest[8000]; Chest._chestsByCoords = [];
            ServerTShock.Config = new TShockAPI.Configuration.TShockConfig();
            ServerTShock.Config.Settings.RegionProtectChests = false;
            ServerTShock.Config.Settings.RangeChecks = false;
            // A controlled target-core table exercises the real reflection producer and its per-entry
            // revalidation. It is a fault-isolation fixture, not a client or runtime qualification.
            var rowType = buffField.FieldType.GetElementType()!;
            var table = Array.CreateInstance(rowType, Terraria.ID.BuffID.Count);
            var row = Activator.CreateInstance(rowType)!;
            rowType.GetProperty("MaxTicks")!.SetValue(row, 1800);
            rowType.GetProperty("CanBeAddedWithoutHostile")!.SetValue(row, true);
            table.SetValue(row, Terraria.ID.BuffID.Wet); buffField.SetValue(null, table);
            var actor = ConnectAndLogin(1201); actor.HasSentInventory = true; actor.ActiveChest = -1;
            var business = GetPluginField<M2BusinessAdapter>("_business");
            var inventory = new M3InventoryContexts("m2-hook-fixture", slot => slot == Slot ? (Key(), actor, _engine.CanWrite(Key())) : (null, null, false), (_, _, _) => false);
            SetPluginField("_inventory", inventory); business.InventoryContexts = inventory;
            var combat = new M3CombatContexts("m2-hook-fixture"); SetPluginField("_combat", combat);
            ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty);
            Assert.That(business.BuffTableReady, Is.True);
            byte[] projectile = new byte[23];
            BinaryPrimitives.WriteUInt32LittleEndian(projectile, new ProjectileKey(Slot, 5, 1).bits);
            BinaryPrimitives.WriteInt16LittleEndian(projectile.AsSpan(20), 1);
            byte[] buff = new byte[7]; buff[0] = Slot;
            BinaryPrimitives.WriteInt16LittleEndian(buff.AsSpan(1), Terraria.ID.BuffID.Wet);
            BinaryPrimitives.WriteInt32LittleEndian(buff.AsSpan(3), 1);
            if (fault == "ItemTable") typeof(M2BusinessAdapter).GetField("definitions", flags)!.SetValue(business, null);
            else if (fault.StartsWith("Combat", StringComparison.Ordinal)) typeof(M3CombatContexts).GetField("definitions", flags)!.SetValue(combat, null);
            else if (fault == "InventoryTick")
            {
                var leases = typeof(M3InventoryContexts).GetField("leases", flags)!;
                leases.SetValue(inventory, Array.CreateInstance(leases.FieldType.GetElementType()!, 0));
            }
            else if (fault == "InventoryPacket") typeof(M3InventoryContexts).GetField("accessories", flags)!.SetValue(inventory, null);
            else if (fault == "ProgressionSnapshot") typeof(M2BusinessAdapter).GetField("worldFacts", flags)!.SetValue(business, null);
            else
            {
                // Update currently has no deterministic throwable runtime getter. Inject at the exact
                // shared policy boundary used by Update/Evaluate, without fabricating a proof snapshot.
                SetPluginField("_progressionPolicy", System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(M3ProgressionPolicy)));
                typeof(AntiCheatPlugin).GetMethod("RunProgression", flags)!.Invoke(_plugin,
                    [new Action<M3ProgressionPolicy>(_ => throw new InvalidDataException("fixture policy producer fault"))]);
            }
            if (fault == "CombatPacket") ServerApi.Hooks.NetGetData.Invoke(M2ContractsTests.Packet(PacketTypes.ProjectileNew, projectile));
            else if (fault == "InventoryPacket")
            {
                byte[] equipment = new byte[9]; equipment[0] = Slot;
                BinaryPrimitives.WriteInt16LittleEndian(equipment.AsSpan(1), (short)(Terraria.ID.PlayerItemSlotID.Armor0 + 3));
                ServerApi.Hooks.NetGetData.Invoke(M2ContractsTests.Packet(PacketTypes.PlayerSlot, equipment));
            }
            int calls = 0;
            var queued = GetPluginField<ServerThreadDispatcher>("_dispatcher").InvokeAsync(() => calls++).AsTask();
            ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(queued.IsCompletedSuccessfully, Is.True);
            Assert.That(GetPluginField<M2BusinessAdapter>("_business"), Is.SameAs(business));
            Assert.That(GetPluginField<M3CombatContexts?>("_combat"), fault.StartsWith("Combat", StringComparison.Ordinal) ? Is.Null : Is.SameAs(combat));
            Assert.That(GetPluginField<M3InventoryContexts?>("_inventory"), fault.StartsWith("Inventory", StringComparison.Ordinal) ? Is.Null : Is.SameAs(inventory));
            Assert.That(business.BuffTableReady, Is.True);
            var legalShot = M2ContractsTests.Packet(PacketTypes.ProjectileNew, projectile);
            var legalBuff = M2ContractsTests.Packet(PacketTypes.PlayerAddBuff, buff);
            ServerApi.Hooks.NetGetData.Invoke(legalShot); ServerApi.Hooks.NetGetData.Invoke(legalBuff);
            Assert.That(legalShot.Handled, Is.False); Assert.That(legalBuff.Handled, Is.False);
            if (!fault.StartsWith("Inventory", StringComparison.Ordinal))
            {
                Chest.CreateWorldChest(0, 20, 20);
                byte[] open = [20, 0, 20, 0];
                var openPacket = M2ContractsTests.Packet(PacketTypes.ChestGetContents, open);
                ServerApi.Hooks.NetGetData.Invoke(openPacket);
                Assert.That(openPacket.Handled, Is.False);
                actor.TPlayer.chest = 0; actor.ActiveChest = 0; // The separate core acceptance step.
                ServerApi.Hooks.GameUpdate.Invoke(EventArgs.Empty);
                var write = M2ContractsTests.Packet(PacketTypes.ChestItem, new byte[8]);
                ServerApi.Hooks.NetGetData.Invoke(write);
                Assert.That(write.Handled, Is.False);
                Assert.That(inventory.ConfirmedContainerGrants, Is.EqualTo(1));
            }
            Assert.That(_engine.SanctionCount, Is.Zero, "A context fault and legal actions are never cheating evidence.");
            GetDataEventArgs illegal;
            if (proof == "C1")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(projectile, new ProjectileKey(Slot + 1, 6, 1).bits);
                illegal = M2ContractsTests.Packet(PacketTypes.ProjectileNew, projectile);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(buff.AsSpan(3), 1801);
                illegal = M2ContractsTests.Packet(PacketTypes.PlayerAddBuff, buff);
            }
            ServerApi.Hooks.NetGetData.Invoke(illegal);
            Assert.That(illegal.Handled, Is.True); Assert.That(_engine.CanWrite(Key()), Is.False);
            Assert.That(_engine.SanctionCount, Is.EqualTo(1));
            ServerApi.Hooks.NetGetData.Invoke(illegal);
            Assert.That(_engine.SanctionCount, Is.EqualTo(1));
        }
        finally
        {
            buffField.SetValue(null, oldBuffs); Main.player[Slot] = oldPlayer;
            Main.projectile = oldEntities; Projectile.keyToIndex = oldMap; Main.chest = oldChests;
            Chest._chestsByCoords = oldChestIndex; ServerTShock.Config = oldConfig;
            Main.netMode = oldMode; Main.maxTilesX = oldWidth; Main.maxTilesY = oldHeight;
        }
    }
    private void SetPluginField(string name, object value) => typeof(AntiCheatPlugin).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_plugin, value);
    private void EnableM2Lab()
    {
        Assert.That(_engine.CompleteShutdownAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        var journal = GetPluginField<FileEnforcementJournal>("_journal");
        var dispatcher = GetPluginField<ServerThreadDispatcher>("_dispatcher");
        _engine = new AntiCheatEngine(TimeProvider.System, new EngineOptions { Scope = ExecutionScope.TestLab },
            RulePolicy.Disabled, journal, new TShockAccountBanStore(dispatcher), M2RuleRegistry.Create("m2-hook-fixture", ExecutionScope.TestLab));
        Assert.That(_engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        SetPluginField("_engine", _engine);
        SetPluginField("_scope", ExecutionScope.TestLab);
        SetPluginField("_targetRuntime", new TargetRuntimeStatus(true, "v1.4.5.8", "m2-hook-fixture", "explicit-test-fixture"));
        SetPluginField("_business", new M2BusinessAdapter("m2-hook-fixture", Path.Combine(ServerTShock.SavePath, "absent")));
        GetPluginField<M2BusinessAdapter>("_business").IntegrityFault = (producer, exception) =>
            typeof(AntiCheatPlugin).GetMethod("OnBusinessIntegrityFault", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_plugin, [producer, exception]);
    }

    private CapturingPlayer ConnectAndLogin(int accountId)
    {
        var player = new CapturingPlayer(Slot) { Account = new UserAccount { ID = accountId, Name = "server-verified-" + accountId }, IsLoggedIn = true };
        ServerTShock.Players[Slot] = player;
        var connect = new ConnectEventArgs();
        typeof(ConnectEventArgs).GetProperty(nameof(ConnectEventArgs.Who))!.SetValue(connect, Slot);
        ServerApi.Hooks.ServerConnect.Invoke(connect);
        Assert.That(connect.Handled, Is.False);
        PlayerHooks.OnPlayerPostLogin(player);
        return player;
    }

    private SessionKey Key()
    {
        var array = (Array)typeof(AntiCheatPlugin).GetField("_bindings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_plugin)!;
        var binding = array.GetValue(Slot)!;
        return (SessionKey)binding.GetType().GetProperty("Key")!.GetValue(binding)!;
    }

    private static GetDataEventArgs Packet(int owner)
    {
        var args = new GetDataEventArgs { MsgID = PacketTypes.PlayerSlot, Index = 0, Length = 10 };
        var message = new MessageBuffer { whoAmI = Slot, readBuffer = [(byte)owner, 0, 0, 1, 0, 0, 1, 0, 0] };
        typeof(GetDataEventArgs).GetProperty(nameof(GetDataEventArgs.Msg))!.SetValue(args, message);
        return args;
    }

    private sealed class CapturingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects { get; private set; }
        public override void Disconnect(string reason) => Disconnects++;
    }
}
