using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using MonoMod.RuntimeDetour;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Terraria;
using Terraria.Chat;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using ServerTShock = TShockAPI.TShock;
using NativeUtils = TShockAPI.Utils;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M17CommandWorkGuardTests
{
    [Test]
    public void RealTakenAccountRegistrationHashesOnlyAdmittedAttemptsAndMakesNoDatabaseMutation()
    {
        using var f = new Scenario(methodName: "RegisterUser", nativeBody: true);
        var oldAccounts = ServerTShock.UserAccounts; var oldLog = ServerTShock.Log;
        var directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "credentials.sqlite") + ";Pooling=False");
        using var log = new TextLog(Path.Combine(directory, "native.log"), true);
        ServerTShock.UserAccounts = new UserAccountManager(db); ServerTShock.Log = log;
        db.Query("INSERT INTO Users (ID, Username, Password, UUID, Usergroup, Registered, LastAccessed, KnownIPs) VALUES (@0,@1,@2,@3,@4,@5,@6,@7)",
            71, f.Actor.Name, "preexisting-placeholder-hash-never-verified", "", "default", "", "", "[]");
        int hashes = 0;
        using var hashEntry = new Hook(typeof(UserAccount).GetMethod(nameof(UserAccount.CreateBCryptHash), [typeof(string)])!,
            (Action<Action<UserAccount, string>, UserAccount, string>)((original, account, password) => { hashes++; original(account, password); }));
        try
        {
            f.Handler = actor => f.Command.Run(f.Args(actor));
            for (int i = 0; i < 9; i++) f.Chat();
            Assert.That(hashes, Is.EqualTo(8), "Eight actual native BCrypt hashes completed; ninth handler never entered.");
            Assert.That(f.Work.Admitted, Is.EqualTo(8)); Assert.That(f.Work.Blocked, Is.EqualTo(1));
            Assert.That(ServerTShock.UserAccounts.GetUserAccountByName(f.Actor.Name).Password, Is.EqualTo("preexisting-placeholder-hash-never-verified"));
            Assert.That(ServerTShock.UserAccounts.GetUserAccounts().Count, Is.EqualTo(1));
            Assert.That(f.Work.AccountBucketCount, Is.Zero); Assert.That(f.Actor.IsLoggedIn, Is.False);
        }
        finally { ServerTShock.UserAccounts = oldAccounts; ServerTShock.Log = oldLog; }
    }

    [Test]
    public void ExistingNativeFailedLoginLimitRemainsReachableWhenWorkAllowanceIsEmpty()
    {
        using var f = new Scenario(); f.Handler = actor => f.Command.Run(f.Args(actor));
        for (int i = 0; i < 5; i++) f.Chat();
        Assert.That(f.NativeEntries, Is.EqualTo(4)); Assert.That(f.Work.Blocked, Is.EqualTo(1));
        f.Actor.LoginAttempts = ServerTShock.Config.Settings.MaximumLoginAttempts + 1;
        f.Chat(); Assert.That(f.NativeEntries, Is.EqualTo(5), "Native policy's pre-hash kick branch is not suppressed by this resource budget.");
        Assert.That(f.Work.Blocked, Is.EqualTo(1));
    }
    [Test]
    public void RealNativeFailedVerificationsReachCoreKickAfterFourFailuresWithoutBudgetInventingFailures()
    {
        using var f = new Scenario(nativeBody: true);
        var oldAccounts = ServerTShock.UserAccounts; var oldLog = ServerTShock.Log; var oldUtils = ServerTShock.Utils;
        var directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string logPath = Path.Combine(directory, "native-login.log");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "credentials.sqlite") + ";Pooling=False");
        using var log = new TextLog(logPath, true);
        ServerTShock.UserAccounts = new UserAccountManager(db); ServerTShock.Log = log; ServerTShock.Utils = NativeUtils.Instance;
        const string correct = "isolated-known-native-password", wrong = "fixture-placeholder";
        var seed = new UserAccount(); seed.CreateBCryptHash(correct); string stored = seed.Password;
        db.Query("INSERT INTO Users (ID, Username, Password, UUID, Usergroup, Registered, LastAccessed, KnownIPs) VALUES (@0,@1,@2,@3,@4,@5,@6,@7)",
            71, f.Actor.Name, stored, "", "default", "", "", "[]");
        typeof(TSPlayer).GetField("CacheIP", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Actor, "127.0.0.1");
        Netplay.Clients[7].IsActive = true;
        int entries = 0, verifies = 0, broadcasts = 0;
        using var entry = new Hook(typeof(Commands).GetMethod("AttemptLogin", BindingFlags.Static | BindingFlags.NonPublic)!,
            (Action<Action<CommandArgs>, CommandArgs>)((original, args) => { entries++; original(args); }));
        using var verify = new Hook(typeof(UserAccount).GetMethod(nameof(UserAccount.VerifyPassword), [typeof(string)])!,
            (Func<Func<UserAccount, string, bool>, UserAccount, string, bool>)((original, account, password) => { verifies++; return original(account, password); }));
        // Native AttemptLogin and Kick execute. Only their outbound endpoints are recorded
        // in this method-level fixture; real peer termination belongs to the TCP scenario.
        using var broadcast = new Hook(typeof(NativeUtils).GetMethod(nameof(NativeUtils.Broadcast), [typeof(string), typeof(Microsoft.Xna.Framework.Color)])!,
            (Action<Action<NativeUtils, string, Microsoft.Xna.Framework.Color>, NativeUtils, string, Microsoft.Xna.Framework.Color>)((_, _, _, _) => broadcasts++));
        try
        {
            Assert.That(ServerTShock.Config.Settings.MaximumLoginAttempts, Is.EqualTo(3));
            f.Handler = actor => f.Command.Run(f.Args(actor));
            for (int i = 1; i <= 4; i++) { f.Chat(); Assert.That(f.Actor.LoginAttempts, Is.EqualTo(i)); }
            Assert.That(entries, Is.EqualTo(4)); Assert.That(verifies, Is.EqualTo(4)); Assert.That(f.Work.Admitted, Is.EqualTo(4));
            f.Chat();
            Assert.That(entries, Is.EqualTo(5)); Assert.That(verifies, Is.EqualTo(4));
            Assert.That(((RecordingPlayer)f.Actor).Disconnects, Is.EqualTo(1)); Assert.That(broadcasts, Is.EqualTo(1));
            Assert.That(f.Work.Admitted, Is.EqualTo(4)); Assert.That(f.Work.Blocked, Is.Zero); Assert.That(f.Actor.LoginAttempts, Is.EqualTo(4));
            Assert.That(ServerTShock.UserAccounts.GetUserAccountByName(f.Actor.Name).Password == stored, Is.True);
            log.Dispose(); // This owned native log is complete before its credential-leak check.
            string contents = File.ReadAllText(logPath);
            Assert.That(contents.Contains(correct) || contents.Contains(wrong) || contents.Contains(stored), Is.False, "No credential values in the native log.");
        }
        finally { ServerTShock.UserAccounts = oldAccounts; ServerTShock.Log = oldLog; ServerTShock.Utils = oldUtils; }
    }
    [Test]
    public void ActualSelectedNativeDelegateIsChargedAfterPermissionAndBeforeHandler()
    {
        using var f = new Scenario();
        var denied = new Command("fixture.absent.permission", f.Native, "login");
        f.Handler = actor => Assert.That(denied.Run(f.Args(actor)), Is.False); f.Chat();
        Assert.That(f.NativeEntries, Is.Zero); Assert.That(f.Work.Admitted, Is.Zero);
        f.Handler = actor => Assert.That(f.Command.Run(f.Args(actor)), Is.True);
        for (int i = 0; i < 5; i++) f.Chat();
        Assert.That(f.NativeEntries, Is.EqualTo(4)); Assert.That(f.Work.Admitted, Is.EqualTo(4)); Assert.That(f.Work.Blocked, Is.EqualTo(1));
        Assert.That(f.Work.AccountBucketCount, Is.Zero, "Unauthenticated login does not charge its requested account.");
    }
    [Test]
    public void PlayerControlledNameAndChangedOrMulticastPluginDelegateCannotBorrowNativeIdentity()
    {
        using var f = new Scenario(); int pluginEntries = 0;
        f.Command.CommandDelegate = _ => pluginEntries++;
        f.Command.Names.Clear(); f.Command.Names.Add("login");
        f.Handler = actor => f.Command.Run(f.Args(actor));
        for (int i = 0; i < 10; i++) f.Chat();
        Assert.That(pluginEntries, Is.EqualTo(10)); Assert.That(f.Work.Admitted, Is.Zero);
        f.Command.CommandDelegate = f.Native + (_ => pluginEntries++); f.Chat();
        Assert.That(f.NativeEntries, Is.EqualTo(1)); Assert.That(pluginEntries, Is.EqualTo(11)); Assert.That(f.Work.Admitted, Is.Zero);
        f.Command.CommandDelegate = f.Native; f.Command.Names[0] = "server-chosen-alias"; f.Chat();
        Assert.That(f.Work.Admitted, Is.EqualTo(1)); Assert.That(f.NativeEntries, Is.EqualTo(2));
    }
    [Test]
    public void NestedActorsHaveSeparateBudgetsAndOutOfScopeOrAsyncCallsAreNotAttributed()
    {
        using var f = new Scenario(authenticated: true);
        f.Handler = actor => { if (actor.Index == 7) f.Chat(8); f.Command.Run(f.Args(actor)); };
        for (int i = 0; i < 4; i++) f.Chat();
        Assert.That(f.NativeEntries, Is.EqualTo(8)); Assert.That(f.Work.AccountBucketCount, Is.EqualTo(2));
        f.Chat(); Assert.That(f.NativeEntries, Is.EqualTo(8)); Assert.That(f.Work.Blocked, Is.EqualTo(2));
        f.Command.Run(f.Args(f.Actor)); Assert.That(f.NativeEntries, Is.EqualTo(9));
        f.Handler = actor => { var thread = new Thread(() => f.Command.Run(f.Args(actor))); thread.Start(); Assert.That(thread.Join(2000), Is.True); };
        f.Chat(); Assert.That(f.NativeEntries, Is.EqualTo(10)); Assert.That(f.Work.Admitted, Is.EqualTo(8));
    }
    [Test]
    public async Task QueuedThirdPartyHandlerAfterChatReturnsExecutesOnceWithoutBorrowingRequestScope()
    {
        using var f = new Scenario(); int pluginEntries = 0;
        var plugin = new Command(_ => Interlocked.Increment(ref pluginEntries), "login");
        using var release = new ManualResetEventSlim(false); Task pending = Task.CompletedTask;
        f.Handler = actor => pending = Task.Run(() => { Assert.That(release.Wait(2000), Is.True); plugin.Run(f.Args(actor)); });
        f.Chat(); release.Set(); await pending;
        Assert.That(pluginEntries, Is.EqualTo(1)); Assert.That(f.Work.Admitted, Is.Zero); Assert.That(f.Work.Blocked, Is.Zero);
    }
    [TestCase("PasswordUser", 2)]
    [TestCase("ManageUsers", 8)]
    public void RealPasswordAndPermissionedAccountChangesAreStoppedBeforeHashAndRecoverWithoutTargetAccountBilling(string method, int allowed)
    {
        using var f = new Scenario(authenticated: true, methodName: method, nativeBody: true);
        var oldAccounts = ServerTShock.UserAccounts; var oldLog = ServerTShock.Log;
        string directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string logPath = Path.Combine(directory, "native-account-work.log");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "credentials.sqlite") + ";Pooling=False");
        using var log = new TextLog(logPath, true);
        ServerTShock.UserAccounts = new UserAccountManager(db); ServerTShock.Log = log;
        string current = "isolated-initial-password", proposed = "isolated-next-password";
        var account = new UserAccount { ID = method == "PasswordUser" ? 140 : 71, Name = "OwnedCredentialTarget", Group = "default" };
        account.CreateBCryptHash(current);
        db.Query("INSERT INTO Users (ID, Username, Password, UUID, Usergroup, Registered, LastAccessed, KnownIPs) VALUES (@0,@1,@2,@3,@4,@5,@6,@7)",
            account.ID, account.Name, account.Password, "", "default", "", "", "[]");
        if (method == "PasswordUser") f.Actor.Account = account;
        typeof(TSPlayer).GetField("CacheIP", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Actor, "127.0.0.1");
        int hashes = 0, verifies = 0;
        using var hash = new Hook(typeof(UserAccount).GetMethod(nameof(UserAccount.CreateBCryptHash), [typeof(string)])!,
            (Action<Action<UserAccount, string>, UserAccount, string>)((original, subject, password) => { hashes++; original(subject, password); }));
        using var verify = new Hook(typeof(UserAccount).GetMethod(nameof(UserAccount.VerifyPassword), [typeof(string)])!,
            (Func<Func<UserAccount, string, bool>, UserAccount, string, bool>)((original, subject, password) => { verifies++; return original(subject, password); }));
        string permission = method == "PasswordUser" ? Permissions.canchangepassword : Permissions.user;
        var command = new Command(permission, f.Native, "owned-native-alias");
        try
        {
            f.Handler = actor => command.Run(new CommandArgs("fixture-no-credential-text", false, actor,
                method == "PasswordUser" ? [current, proposed] : ["password", account.Name, proposed]));
            f.Chat(); Assert.That(hashes, Is.Zero); Assert.That(verifies, Is.Zero); Assert.That(f.Work.Admitted, Is.Zero);
            f.Actor.Group.AddPermission(permission);
            for (int i = 0; i < allowed; i++) { f.Chat(); current = proposed; proposed = "isolated-next-password-" + i; }
            Assert.That(hashes, Is.EqualTo(allowed)); Assert.That(verifies, Is.EqualTo(method == "PasswordUser" ? allowed : 0));
            string before = ServerTShock.UserAccounts.GetUserAccountByName(account.Name).Password;
            f.Chat();
            Assert.That(hashes, Is.EqualTo(allowed)); Assert.That(verifies, Is.EqualTo(method == "PasswordUser" ? allowed : 0));
            Assert.That(ServerTShock.UserAccounts.GetUserAccountByName(account.Name).Password == before, Is.True);
            Assert.That(f.Work.Blocked, Is.EqualTo(1)); Assert.That(f.Work.AccountBucketCount, Is.EqualTo(1), "Only sender140 is charged, not the target71 account.");
            f.Clock.Advance(5); f.Chat();
            Assert.That(hashes, Is.EqualTo(allowed + 1)); Assert.That(f.Work.Admitted, Is.EqualTo(allowed + 1));
            Assert.That(ServerTShock.UserAccounts.GetUserAccountByName(account.Name).Password != before, Is.True);
            Assert.That(f.Actor.LoginAttempts, Is.Zero); Assert.That(((RecordingPlayer)f.Actor).Disconnects, Is.Zero);
            log.Dispose(); string contents = File.ReadAllText(logPath);
            Assert.That(contents.Contains("isolated-initial-password") || contents.Contains("isolated-next-password") || contents.Contains(before), Is.False);
        }
        finally { ServerTShock.UserAccounts = oldAccounts; ServerTShock.Log = oldLog; }
    }
    [TestCase("generation")]
    [TestCase("account")]
    [TestCase("actor")]
    public void ReplacedSessionOrActorDuringRequestHasNoBorrowedAuthority(string change)
    {
        using var f = new Scenario(authenticated: true);
        f.Handler = actor =>
        {
            if (change == "generation") f.Session = f.Session with { Key = f.Session.Key with { Generation = 2 } };
            else if (change == "account") f.Session = f.Session with { AccountId = 999 };
            else f.Actor = new TSPlayer(7) { IsLoggedIn = true, Account = new UserAccount { ID = 140 } };
            f.Command.Run(f.Args(actor));
        };
        f.Chat(); Assert.That(f.NativeEntries, Is.EqualTo(1)); Assert.That(f.Work.Admitted, Is.Zero);
    }
    [TestCase("lookup")]
    [TestCase("clock")]
    [TestCase("dispose")]
    public void AdmissionFaultOrDisposeLeavesNativeHandlerExactlyOnce(string failure)
    {
        using var f = new Scenario();
        f.Handler = actor =>
        {
            if (failure == "lookup") f.ThrowWorkLookup = true;
            else if (failure == "clock") f.Clock.Throw = true;
            else f.Work.Dispose();
            f.Command.Run(f.Args(actor));
        };
        f.Chat(); Assert.That(f.NativeEntries, Is.EqualTo(1)); Assert.That(f.Work.Healthy, Is.False);
        Assert.That(f.Work.Blocked, Is.Zero);
        f.ThrowWorkLookup = false; f.Clock.Throw = false; f.Command.Run(f.Args(f.Actor));
        Assert.That(f.NativeEntries, Is.EqualTo(2));
    }

    private sealed class Clock : TimeProvider
    { public bool Throw; private long ticks; public void Advance(int seconds) => ticks += seconds * TimeSpan.TicksPerSecond; public override long TimestampFrequency => TimeSpan.TicksPerSecond; public override long GetTimestamp() => Throw ? throw new IOException("clock-fixture") : ticks; }
    private sealed class StubPlugin() : TerrariaPlugin(null!) { public override void Initialize() { } }
    private sealed class Scenario : IDisposable
    {
        public TSPlayer Actor;
        private readonly TSPlayer other;
        public SessionSnapshot Session;
        private readonly SessionSnapshot otherSession;
        public readonly Clock Clock = new();
        public readonly M16RequestEgressGuard Scope;
        public readonly M17CommandWorkGuard Work;
        public readonly CommandDelegate Native;
        public readonly Command Command;
        public Action<TSPlayer>? Handler;
        public bool ThrowWorkLookup;
        public int NativeEntries;
        private readonly Hook? nativeEntry;
        private readonly StubPlugin owner = new();
        private readonly TShockAPI.Configuration.TShockConfig? oldConfig = ServerTShock.Config;
        private readonly int oldMode = Main.netMode;
        private readonly bool oldDed = Main.dedServ;
        private readonly Player oldPlayer = Main.player[7];
        private readonly RemoteClient oldClient = Netplay.Clients[7];
        private readonly MethodInfo chat = typeof(HookManager).GetMethod("InvokeServerChat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public Scenario(bool authenticated = false, string methodName = "AttemptLogin", bool nativeBody = false)
        {
            ServerTShock.Config ??= new TShockAPI.Configuration.TShockConfig(); Main.netMode = 2; Main.dedServ = true;
            Main.player[7] = new Player { whoAmI = 7, name = "I04-owned-native-account" }; Netplay.Clients[7] = new RemoteClient { Id = 7 };
            Actor = new RecordingPlayer(7) { Group = new Group("ordinary"), IsLoggedIn = authenticated,
                Account = authenticated ? new UserAccount { ID = 140 } : null };
            other = new TSPlayer(8) { Group = new Group("ordinary"), IsLoggedIn = authenticated,
                Account = authenticated ? new UserAccount { ID = 141 } : null };
            Session = new(new(Guid.NewGuid(), 1, 7, 1), authenticated ? 140 : null, false, DateTimeOffset.UtcNow);
            otherSession = Session with { Key = Session.Key with { Slot = 8 }, AccountId = authenticated ? 141 : null };
            Scope = new(Clock, who => who == 7 ? (Session, Actor) : (otherSession, other)); Scope.Install();
            Work = new(Clock, actor => ThrowWorkLookup ? throw new IOException("work-attribution-fixture") : Scope.CurrentSynchronousActor(actor));
            Work.Install();
            var method = typeof(Commands).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
            Native = (CommandDelegate)method.CreateDelegate(typeof(CommandDelegate));
            if (!nativeBody) nativeEntry = new Hook(method, (Action<Action<CommandArgs>, CommandArgs>)((_, _) => NativeEntries++));
            Command = new Command(Native, "server-native-alias");
            ServerApi.Hooks.ServerChat.Register(owner, Handle);
        }
        private void Handle(ServerChatEventArgs args) => Handler?.Invoke(args.Who == 7 ? Actor : other);
        public void Chat(int who = 7) => chat.Invoke(ServerApi.Hooks, [new MessageBuffer { whoAmI = who }, who, "fixture-content-not-retained", default(ChatCommandId)]);
        public CommandArgs Args(TSPlayer actor) => new("fixture", false, actor, ["fixture-placeholder"]);
        public void Dispose()
        {
            ServerApi.Hooks.ServerChat.Deregister(owner, Handle); nativeEntry?.Dispose(); Work.Dispose(); Scope.Dispose(); owner.Dispose();
            ServerTShock.Config = oldConfig; Main.netMode = oldMode; Main.dedServ = oldDed;
            Main.player[7] = oldPlayer; Netplay.Clients[7] = oldClient;
        }
    }
    private sealed class RecordingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public override void SendMessage(string message, byte red, byte green, byte blue) { }
        public override void Disconnect(string reason) => Disconnects++;
    }
}
