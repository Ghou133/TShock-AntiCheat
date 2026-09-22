using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using HttpServer;
using Microsoft.Data.Sqlite;
using MonoMod.RuntimeDetour;
using NUnit.Framework;
using Rests;
using TShockAPI;
using TShockAPI.DB;
using Native = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M17RestWorkTests
{
    [Test]
    public void RealFileBackedPinnedHttpDependencyIsAcceptedAndByteLoadedOrWrongIdentityIsUnsupported()
    {
        var validate = typeof(M17RestWorkGuard).GetMethod("ValidateHttpServerDependency", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = typeof(IHttpContext).Assembly;
        Assert.That(validate.Invoke(null, [actual]), Is.EqualTo(M17RestWorkGuard.HttpServerSha256));
        var byteLoaded = Assembly.Load(File.ReadAllBytes(actual.Location));
        Assert.That(byteLoaded.Location, Is.Empty);
        Assert.That(Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [byteLoaded]))!.InnerException, Is.TypeOf<NotSupportedException>());
        Assert.That(Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [typeof(TSPlayer).Assembly]))!.InnerException, Is.TypeOf<NotSupportedException>());
    }
    [Test]
    public void SameVersionOwnedDependencyWithDifferentDiskHashIsRejectedWithoutExecutingItsCode()
    {
        string folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "rest-dependency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, "HttpServer.dll");
        byte[] bytes = File.ReadAllBytes(typeof(IHttpContext).Assembly.Location);
        File.WriteAllBytes(path, [.. bytes, 0]); // Controlled PE overlay; inspect metadata only, never invoke types.
        var context = new AssemblyLoadContext("owned-rest-dependency-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var changed = context.LoadFromAssemblyPath(path);
            Assert.That(changed.GetName().Version, Is.EqualTo(new Version(2, 0, 0, 0)));
            var validate = typeof(M17RestWorkGuard).GetMethod("ValidateHttpServerDependency", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [changed]))!.InnerException, Is.TypeOf<NotSupportedException>());
        }
        finally { context.Unload(); }
    }
    [TestCase("/v2/users/create")]
    [TestCase("/v2/users/update")]
    public void NativeTokenAndPermissionPrecedeExactHashDatabaseBudgetAndNaturalRecovery(string route)
    {
        using var f = new Fixture();
        var command = f.Command(route);
        var fields = route.EndsWith("create") ? new[] { ("user", "RestTarget"), ("password", Fixture.Password) } :
            new[] { ("user", "RestTarget"), ("type", "name"), ("password", Fixture.Password) };
        if (route.EndsWith("update")) { f.Seed("RestTarget", "default"); f.Hashes = 0; }
        int initialCount = Native.UserAccounts.GetUserAccounts().Count;
        Assert.That(f.Execute(command, null, fields).Status, Is.EqualTo("401"));
        Assert.That(f.Execute(command, "invalid-token", fields).Status, Is.EqualTo("403"));
        string low = f.Token("RestReader");
        Assert.That(f.Execute(command, low, fields).Status, Is.EqualTo("403"));
        Assert.That(f.Guard.Admitted, Is.Zero); Assert.That(f.Hashes, Is.Zero);
        string admin = f.Token("RestAdmin");
        Assert.That(f.Execute(command, admin, fields).Status, Is.EqualTo("200"));
        string persisted = Native.UserAccounts.GetUserAccountByName("RestTarget").Password;
        for (int i = 0; i < 7; i++) f.Execute(command, admin, fields);
        Assert.That(f.Hashes, Is.EqualTo(8)); Assert.That(f.Guard.Admitted, Is.EqualTo(8));
        persisted = Native.UserAccounts.GetUserAccountByName("RestTarget").Password;
        Assert.That(f.Execute(command, admin, fields).Status, Is.EqualTo("429"));
        Assert.That(f.Hashes, Is.EqualTo(8)); Assert.That(f.Guard.Blocked, Is.EqualTo(1));
        Assert.That(Native.UserAccounts.GetUserAccountByName("RestTarget").Password == persisted, Is.True);
        Assert.That(Native.UserAccounts.GetUserAccounts().Count, Is.EqualTo(initialCount + (route.EndsWith("create") ? 1 : 0)));
        f.Clock.Advance(5); f.Execute(command, admin, fields);
        Assert.That(f.Hashes, Is.EqualTo(9)); Assert.That(f.Guard.Admitted, Is.EqualTo(9));
        Assert.That(f.Guard.SourceCount, Is.EqualTo(1));
        Assert.That(f.TokenVerifies, Is.EqualTo(2), "Both native authentication attempts remained outside selected account-work billing.");
        f.CheckCompletedLog();
    }
    [Test]
    public void ConfiguredAppTokenNeverNeedsAnInventedGameplaySessionOrDatabaseAccount()
    {
        using var f = new Fixture();
        string token = Guid.NewGuid().ToString("N");
        f.Api.AppTokens.Add(token, new() { Username = "no-database-account", UserGroupName = "rest-admin" });
        Assert.That(f.Execute(f.Command("/v2/users/create"), token, [("user", "FromApp"), ("password", Fixture.Password)]).Status, Is.EqualTo("200"));
        Assert.That(f.Hashes, Is.EqualTo(1)); Assert.That(f.Guard.SourceCount, Is.EqualTo(1));
        Assert.That(Native.UserAccounts.GetUserAccountByName("no-database-account"), Is.Null);
    }
    [Test]
    public void RenamedNativeCallbackIsCoveredButThirdPartyAndOutOfPermissionScopeAreNotReclassified()
    {
        using var f = new Fixture(); string token = f.Token("RestAdmin"); int calls = 0;
        var native = (RestCommandD)typeof(RestManager).GetMethod("UserCreateV2", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(RestCommandD), f.Manager);
        var alias = new SecureRestCommand("/owned-alias", native, RestPermissions.restmanageusers) { DoLog = false };
        f.Execute(alias, token, [("user", "AliasTarget"), ("password", Fixture.Password)]);
        Assert.That(f.Hashes, Is.EqualTo(1)); Assert.That(f.Guard.Admitted, Is.EqualTo(1));
        var thirdParty = new SecureRestCommand("/v2/users/create", _ => { calls++; return new RestObject(); }, RestPermissions.restmanageusers) { DoLog = false };
        for (int i = 0; i < 9; i++) f.Execute(thirdParty, token, []);
        Assert.That(calls, Is.EqualTo(9)); Assert.That(f.Guard.Admitted, Is.EqualTo(1));
        var parameters = Fixture.Parameters(null, [("user", "DirectTarget"), ("password", Fixture.Password)]);
        alias.Execute(new(), parameters, new SecureRest.TokenData { Username = "direct-only", UserGroupName = "rest-admin" }, f.Request, f.Context);
        Assert.That(f.Hashes, Is.EqualTo(2)); Assert.That(f.Guard.Admitted, Is.EqualTo(1), "Direct method token data cannot manufacture observed native permission success.");
    }
    [TestCase("clock")]
    [TestCase("endpoint")]
    [TestCase("dispose")]
    public void ObservationFailureWithdrawsOnlyThisGuardAndNativeHandlerRunsOnce(string fault)
    {
        using var f = new Fixture(); string token = f.Token("RestAdmin");
        if (fault == "clock") f.Clock.Fail = true;
        if (fault == "endpoint") ((ContextProxy)(object)f.Context).Fail = true;
        if (fault == "dispose") f.Guard.Dispose();
        Assert.That(f.Execute(f.Command("/v2/users/create"), token, [("user", "Once"), ("password", Fixture.Password)]).Status, Is.EqualTo("200"));
        Assert.That(f.Hashes, Is.EqualTo(1)); Assert.That(f.Guard.Healthy, Is.False);
        Assert.That(Native.UserAccounts.GetUserAccounts().Count(a => a.Name == "Once"), Is.EqualTo(1));
    }
    [Test]
    public void NativePermissionScopeRestoresAfterThirdPartyExceptionAndDoesNotFlowToAnotherThread()
    {
        using var f = new Fixture(); string token = f.Token("RestAdmin"); int entries = 0;
        var throwing = new SecureRestCommand("/owned-throw", _ => { entries++; throw new InvalidOperationException("owned-handler-failure"); }, RestPermissions.restmanageusers);
        var error = Assert.Throws<TargetInvocationException>(() => f.Execute(throwing, token, []));
        Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>()); Assert.That(entries, Is.EqualTo(1));
        Assert.That(typeof(M17RestWorkGuard).GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null), Is.Null);
        var create = (SecureRestCommand)f.Command("/v2/users/create");
        var nested = new SecureRestCommand("/owned-async", _ =>
        {
            var thread = new Thread(() => create.Execute(new(), Fixture.Parameters(null, [("user", "NoBorrow"), ("password", Fixture.Password)]),
                new SecureRest.TokenData { Username = "method-only", UserGroupName = "rest-admin" }, f.Request, f.Context));
            thread.Start(); Assert.That(thread.Join(2000), Is.True); return new RestObject();
        }, RestPermissions.restmanageusers) { DoLog = false };
        f.Execute(nested, token, []);
        Assert.That(f.Hashes, Is.EqualTo(1)); Assert.That(f.Guard.Admitted, Is.Zero);
        f.Execute(create, token, [("user", "ActualNext"), ("password", Fixture.Password)]);
        Assert.That(f.Hashes, Is.EqualTo(2)); Assert.That(f.Guard.Admitted, Is.EqualTo(1));
    }
    [Test]
    public void ParallelSourceRequestsCannotExceedGlobalAdmissionBound()
    {
        var budget = new M17RestWorkBudget(new Clock(), new() { SourceCapacity = 64, SourceBurst = 8, GlobalBurst = 5 });
        int allowed = 0;
        Parallel.For(1, 65, i => { if (budget.Consume(IPAddress.Parse("127.0.0." + i))) Interlocked.Increment(ref allowed); });
        Assert.That(allowed, Is.EqualTo(5)); Assert.That(budget.Admitted, Is.EqualTo(5)); Assert.That(budget.Blocked, Is.EqualTo(59));
        Assert.That(budget.SourceCount, Is.EqualTo(64));
    }
    [Test]
    public void SourceCapacityIpv4MappingGlobalBoundAndIdleTtlRecoverWithoutTokensOrAccountKeys()
    {
        var clock = new Clock(); var budget = new M17RestWorkBudget(clock, new() { SourceCapacity = 2, SourceBurst = 2, GlobalBurst = 3, IdleTtl = TimeSpan.FromSeconds(10) });
        Assert.That(budget.Consume(IPAddress.Parse("127.0.0.1")), Is.True);
        Assert.That(budget.Consume(IPAddress.Parse("::ffff:127.0.0.1")), Is.True);
        Assert.That(budget.Consume(IPAddress.Parse("127.0.0.1")), Is.False);
        Assert.That(budget.Consume(IPAddress.Parse("127.0.0.2")), Is.True);
        Assert.That(budget.Consume(IPAddress.Parse("127.0.0.2")), Is.False);
        Assert.That(budget.Consume(IPAddress.Parse("127.0.0.3")), Is.False);
        Assert.That(budget.SourceCount, Is.EqualTo(2)); clock.Advance(11);
        Assert.That(budget.Consume(IPAddress.Parse("127.0.0.3")), Is.True);
        Assert.That(budget.SourceCount, Is.EqualTo(1));
    }

    public sealed class Clock : TimeProvider
    {
        private long ticks; public bool Fail;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Fail ? throw new IOException("controlled-clock-failure") : Interlocked.Read(ref ticks);
        public void Advance(double seconds) => Interlocked.Add(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
    public class ContextProxy : DispatchProxy
    {
        public bool Fail; public IPAddress Address = IPAddress.Loopback;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_RemoteEndPoint"
            ? Fail ? throw new IOException("controlled-endpoint-failure") : new IPEndPoint(Address, 18000) : null;
    }
    public sealed class Fixture : IDisposable
    {
        public const string Password = "owned-rest-native-secret";
        public readonly Clock Clock = new(); public readonly SecureRest Api; public readonly RestManager Manager; public readonly M17RestWorkGuard Guard;
        public readonly IHttpContext Context = DispatchProxy.Create<IHttpContext, ContextProxy>();
        public readonly IRequest Request = DispatchProxy.Create<IRequest, ContextProxy>();
        public int Hashes, TokenVerifies;
        public readonly string DirectoryPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "rest-owned-" + Guid.NewGuid().ToString("N"));
        private readonly TShockAPI.Configuration.TShockConfig oldConfig = Native.Config;
        private readonly UserAccountManager oldUsers = Native.UserAccounts; private readonly GroupManager oldGroups = Native.Groups;
        private readonly ILog oldLog = Native.Log; private readonly Utils oldUtils = Native.Utils;
        private readonly SqliteConnection database; private readonly TextLog log; private readonly Hook hashHook, verifyHook;
        private string? issuedToken;
        public Fixture(TimeProvider? clock = null)
        {
            Directory.CreateDirectory(DirectoryPath); Native.Config = new(); Native.Utils = Utils.Instance;
            database = new("Data Source=" + Path.Combine(DirectoryPath, "owned.sqlite") + ";Pooling=False");
            log = new(Path.Combine(DirectoryPath, "native.log"), true); Native.Log = log;
            Native.UserAccounts = new(database); Native.Groups = new(database);
            var admin = new Group("rest-admin"); admin.AddPermission(RestPermissions.restapi); admin.AddPermission(RestPermissions.restmanageusers);
            var reader = new Group("rest-reader"); reader.AddPermission(RestPermissions.restapi);
            Native.Groups.groups.Add(admin); Native.Groups.groups.Add(reader);
            Seed("RestAdmin", "rest-admin"); Seed("RestReader", "rest-reader");
            Api = new(IPAddress.Loopback, 0); Manager = new(Api); Manager.RegisterRestfulCommands();
            Guard = new(clock ?? Clock, Api, Manager) { IntegrityFault = _ => { } }; Guard.Install();
            Assert.That(Guard.VerifiedHttpServerSha256, Is.EqualTo(M17RestWorkGuard.HttpServerSha256));
            hashHook = new(typeof(UserAccount).GetMethod(nameof(UserAccount.CreateBCryptHash), [typeof(string)])!,
                (Action<Action<UserAccount, string>, UserAccount, string>)((original, account, password) => { Hashes++; original(account, password); }));
            verifyHook = new(typeof(UserAccount).GetMethod(nameof(UserAccount.VerifyPassword), [typeof(string)])!,
                (Func<Func<UserAccount, string, bool>, UserAccount, string, bool>)((original, account, password) => { TokenVerifies++; return original(account, password); }));
        }
        public void Seed(string name, string group)
        {
            var user = new UserAccount { Name = name, Group = group }; user.CreateBCryptHash(Password); Native.UserAccounts.AddUserAccount(user);
        }
        public RestCommand Command(string uri) => ((List<RestCommand>)typeof(Rest).GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Api)!).Single(c => c.UriTemplate == uri);
        public string Token(string name)
        {
            var result = (RestObject)typeof(SecureRest).GetMethod("NewTokenInternal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Api, [name, Password, Context])!;
            Assert.That(result.Status, Is.EqualTo("200")); issuedToken = (string)result["token"]; return issuedToken;
        }
        public static ParameterCollection Parameters(string? token, (string Key, string Value)[] fields)
        {
            var parameters = new ParameterCollection(); if (token is not null) parameters.Add("token", token);
            foreach (var (key, value) in fields) parameters.Add(key, value); return parameters;
        }
        public RestObject Execute(RestCommand command, string? token, (string Key, string Value)[] fields) =>
            (RestObject)typeof(SecureRest).GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Api, [command, new RestVerbs(), Parameters(token, fields), Request, Context])!;
        public void CheckCompletedLog()
        {
            log.Info("Owned REST fixture completion marker"); log.Dispose(); string text = File.ReadAllText(Path.Combine(DirectoryPath, "native.log"));
            Assert.That(text.Contains(Password) || (issuedToken is not null && text.Contains(issuedToken)), Is.False, "Completed native log contains no credentials/token.");
        }
        public void Dispose()
        {
            hashHook.Dispose(); verifyHook.Dispose(); Guard.Dispose(); Api.Dispose(); log.Dispose(); database.Dispose();
            Native.Config = oldConfig; Native.UserAccounts = oldUsers; Native.Groups = oldGroups; Native.Log = oldLog; Native.Utils = oldUtils;
        }
    }
}


