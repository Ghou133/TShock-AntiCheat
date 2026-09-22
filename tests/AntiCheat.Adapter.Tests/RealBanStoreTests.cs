using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Persistence;
using AntiCheat.Plugin.TShock;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using TShockAPI;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

/// <summary>Real TShock managers plus an isolated SQLite file. No server socket or client is involved.</summary>
[TestFixture, NonParallelizable]
public sealed class RealBanStoreTests
{
    private SqliteConnection _database = null!;
    private ServerThreadDispatcher _dispatcher = null!;
    private string _directory = null!;
    private readonly Dictionary<FieldInfo, object?> _savedEvents = [];
    private const int AccountId = 123;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = new SqliteConnection($"Data Source={Path.Combine(_directory, "lab.sqlite")};Pooling=False");
        _dispatcher = new ServerThreadDispatcher();
        foreach (string eventName in new[] { "OnBanValidate", "OnBanPreAdd", "OnBanPostAdd" })
        {
            var field = typeof(BanManager).GetField(eventName, BindingFlags.NonPublic | BindingFlags.Static)!;
            _savedEvents[field] = field.GetValue(null);
        }
        ServerTShock.DB = _database;
        ServerTShock.Bans = new BanManager(_database);
        ServerTShock.UserAccounts = new UserAccountManager(_database);
        _database.Query("INSERT INTO Users (ID, Username, Password, UUID, Usergroup, Registered, LastAccessed, KnownIPs) VALUES (@0,@1,@2,@3,@4,@5,@6,@7)",
            AccountId, "verified-account", "", "", "default", "", "", "[]");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var pair in _savedEvents) pair.Key.SetValue(null, pair.Value);
        _savedEvents.Clear();
        _dispatcher?.Dispose();
        _database?.Dispose();
        // Every test creates a fresh isolated database; none are opened from server configuration.
    }

    [Test]
    public void FirstCompleteProofPersistsRealAccountBanAndLoginCheckRejectsIt()
    {
        using var journal = new FileEnforcementJournal(Path.Combine(_directory, "journal"));
        var policy = new RulePolicy("isolated-test-runtime", RuleQualification.TestLab, "adapter-first-event-test", [5], "lab-proof-v1");
        var engine = new AntiCheatEngine(TimeProvider.System, new EngineOptions { Scope = ExecutionScope.TestLab },
            policy, journal, new TShockAccountBanStore(_dispatcher));
        Assert.That(engine.RecoverAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        var session = engine.OpenSession(7)!.Value;
        Assert.That(engine.Authenticate(session, AccountId), Is.EqualTo(AuthenticationResult.Authenticated));
        var complete = new ProofContext("isolated-test-runtime", "lab-proof-v1", true, true, true, true);

        var decision = engine.Observe(new(session, 5, 8, complete));
        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(Verdict.ProvenCheat));
            Assert.That(decision.Behavior, Is.EqualTo(ControlAction.Block));
            Assert.That(engine.CanWrite(session), Is.False);
            Assert.That(ServerTShock.Bans.Bans, Is.Empty, "Storage is separate from synchronous revocation.");
        });

        var pump = engine.PumpAsync(1).AsTask();
        Assert.That(SpinWait.SpinUntil(() => _dispatcher.PendingCount == 1, TimeSpan.FromSeconds(5)), Is.True);
        _dispatcher.Drain(1);
        Assert.That(pump.GetAwaiter().GetResult().Applied, Is.EqualTo(1));
        var ban = ServerTShock.Bans.Bans.Values.Single();
        Assert.That(ban.Identifier, Is.EqualTo("acc:verified-account"));
        Assert.That(ban.ExpirationDateTime, Is.EqualTo(DateTime.MaxValue));
        Assert.That(ServerTShock.Bans.RetrieveBansByIdentifier(ban.Identifier).Single().ExpirationDateTime, Is.EqualTo(DateTime.MaxValue));

        engine.Observe(new(session, 5, 8, complete));
        Assert.That(engine.PumpAsync(1).AsTask().GetAwaiter().GetResult().Applied, Is.Zero);
        engine.Disconnect(session);
        var reconnect = engine.OpenSession(7)!.Value;
        Assert.That(engine.Authenticate(reconnect, AccountId), Is.EqualTo(AuthenticationResult.AccountBlocked));

        // Real TShock CheckBan is invoked with a transport capture subclass; this is not a real disconnect/client test.
        ServerTShock.Bans.UpdateBans();
        var player = new CapturingPlayer { Account = ServerTShock.UserAccounts.GetUserAccountByID(AccountId) };
        bool rejected = (bool)typeof(BanManager).GetMethod("CheckBan", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(ServerTShock.Bans, [player])!;
        Assert.That(rejected, Is.True);
        Assert.That(player.DisconnectReason, Does.Contain("banned"));
    }

    [Test]
    public void SameIntentReplaysIdempotentlyAndTemporaryBanIsUpgradedWithoutDeletingIt()
    {
        var temporary = ServerTShock.Bans.InsertBan("acc:verified-account", "original-reason", "original-admin",
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30)).Ban;
        Assert.That(temporary, Is.Not.Null);
        var store = new TShockAccountBanStore(_dispatcher);
        var intent = Intent(AccountId);
        var first = store.EnsurePermanentlyBannedAsync(intent).AsTask();
        _dispatcher.Drain(); first.GetAwaiter().GetResult();
        var replay = store.EnsurePermanentlyBannedAsync(intent).AsTask();
        _dispatcher.Drain(); replay.GetAwaiter().GetResult();
        var persisted = ServerTShock.Bans.RetrieveAllBans().Single();
        Assert.Multiple(() =>
        {
            Assert.That(persisted.TicketNumber, Is.EqualTo(temporary.TicketNumber));
            Assert.That(persisted.Reason, Is.EqualTo("original-reason"));
            Assert.That(persisted.BanningUser, Is.EqualTo("original-admin"));
            Assert.That(persisted.ExpirationDateTime, Is.EqualTo(DateTime.MaxValue));
        });
    }

    [Test]
    public void UnknownAccountMappingDoesNotBanClaimedNameOrIp()
    {
        var pending = new TShockAccountBanStore(_dispatcher).EnsurePermanentlyBannedAsync(Intent(999)).AsTask();
        _dispatcher.Drain();
        Assert.Throws<InvalidOperationException>(() => pending.GetAwaiter().GetResult());
        Assert.That(ServerTShock.Bans.Bans, Is.Empty);
        Assert.Throws<InvalidOperationException>(() => TShockAccountBanStore.ResolveAccountIdentifier(123,
            new UserAccount { ID = 999, Name = "victim" }));
    }

    [Test]
    public void ScheduledBanBecomesActiveOnFirstProofInsteadOfReportingInactiveSuccess()
    {
        ServerTShock.Bans.InsertBan("acc:verified-account", "scheduled", "admin",
            DateTime.UtcNow.AddDays(1), DateTime.MaxValue);
        var pending = new TShockAccountBanStore(_dispatcher).EnsurePermanentlyBannedAsync(Intent(AccountId)).AsTask();
        _dispatcher.Drain(); pending.GetAwaiter().GetResult();
        var persisted = ServerTShock.Bans.RetrieveAllBans().Single();
        Assert.That(persisted.BanDateTime, Is.LessThan(DateTime.UtcNow));
        Assert.That(persisted.ExpirationDateTime, Is.EqualTo(DateTime.MaxValue));
    }

    [Test]
    public void OtherPluginDeferringBanStartDoesNotReportImmediateSuccessAndRetryActivatesIt()
    {
        BanManager.OnBanPreAdd += (_, args) => args.BanDateTime = DateTime.UtcNow.AddDays(1);
        var store = new TShockAccountBanStore(_dispatcher);
        var intent = Intent(AccountId);
        var first = store.EnsurePermanentlyBannedAsync(intent).AsTask();
        _dispatcher.Drain();
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        Assert.That(ServerTShock.Bans.Bans.Values.Single().BanDateTime, Is.GreaterThan(DateTime.UtcNow));
        var retry = store.EnsurePermanentlyBannedAsync(intent).AsTask();
        _dispatcher.Drain(); retry.GetAwaiter().GetResult();
        Assert.That(ServerTShock.Bans.RetrieveAllBans().Single().BanDateTime, Is.LessThan(DateTime.UtcNow));
    }

    [Test]
    public void RenamedAccountResolvesNumericIdentityAtCommitTime()
    {
        var intent = Intent(AccountId);
        _database.Query("UPDATE Users SET Username=@0 WHERE ID=@1", "new-verified-name", AccountId);
        var pending = new TShockAccountBanStore(_dispatcher).EnsurePermanentlyBannedAsync(intent).AsTask();
        _dispatcher.Drain(); pending.GetAwaiter().GetResult();
        Assert.That(ServerTShock.Bans.Bans.Values.Single().Identifier, Is.EqualTo("acc:new-verified-name"));
    }

    [Test]
    public void DatabaseFailureSurfacesAndDoesNotReportSuccess()
    {
        _database.Query("DROP TABLE PlayerBans");
        var pending = new TShockAccountBanStore(_dispatcher).EnsurePermanentlyBannedAsync(Intent(AccountId)).AsTask();
        _dispatcher.Drain();
        Assert.That(pending.IsFaulted, Is.True);
        Assert.That(ServerTShock.Bans.Bans, Is.Empty);
    }

    private static BanIntent Intent(long account)
    {
        var id = Guid.NewGuid();
        var evidence = new Evidence(id, "A02", "lab-v1", new(Guid.NewGuid(), 1, 7, 1), account,
            DateTimeOffset.UtcNow, "isolated-test-runtime", "lab-v1", "adapter-test", RuleQualification.TestLab,
            new(true, true, true, true, true, true, true, true, true), 5, 7, 8);
        return new(id, account, evidence, evidence.ObservedUtc);
    }

    private sealed class CapturingPlayer() : TSPlayer("different-character-name")
    {
        public string? DisconnectReason { get; private set; }
        public override void Disconnect(string reason) => DisconnectReason = reason;
    }
}
