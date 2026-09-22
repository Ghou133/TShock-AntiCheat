using System.Net;
using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.NetModules;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M16RequestEgressGuardTests
{
    [Test]
    public void ActualShrinkAndTwoRecipientTransportCountsExactBytesAndBlocksBeforeSocket()
    {
        Run(s =>
        {
            s.Handler = _ => s.Broadcast(50);
            s.Chat();
            Assert.That(s.First.Bytes, Is.EqualTo(55));
            Assert.That(s.Second.Bytes, Is.EqualTo(55));
            Assert.That(s.Guard.AdmittedBytes, Is.EqualTo(110), "Charged after shrink, not reserved 30005 bytes.");
            s.Chat();
            Assert.That(s.First.Bytes, Is.EqualTo(110));
            Assert.That(s.Second.Bytes, Is.EqualTo(55), "Second recipient exceeds the exact remaining sender budget.");
            Assert.That(s.Guard.BlockedBytes, Is.EqualTo(55));
            Assert.That(s.Guard.BlockedSends, Is.EqualTo(1));
        });
    }
    [Test]
    public void NonChatModuleAndOutOfScopeOutputStillSendAfterChatBudgetExhaustion()
    {
        Run(s =>
        {
            s.Handler = _ => s.Broadcast(95); s.Chat(); s.Chat();
            Assert.That(s.Guard.BlockedSends, Is.EqualTo(2));
            long before = s.First.Bytes + s.Second.Bytes;
            s.Handler = _ => s.Broadcast(95, 2); s.Chat();
            s.Broadcast(95);
            Assert.That(s.First.Bytes + s.Second.Bytes - before, Is.EqualTo(400));
            Assert.That(s.Guard.BlockedSends, Is.EqualTo(2));
        });
    }
    [Test]
    public void NestedChatScopeAndFinallyRestoreCorrectAccountResponsibility()
    {
        Run(s =>
        {
            s.Handler = e =>
            {
                if (e.Who == 7) { s.Chat(8); s.Broadcast(95); }
                else s.Broadcast(95);
            };
            s.Chat();
            Assert.That(s.First.Bytes, Is.EqualTo(200));
            Assert.That(s.Second.Bytes, Is.EqualTo(200));
            Assert.That(s.Guard.AccountBucketCount, Is.EqualTo(2));
            s.Handler = _ => { s.Broadcast(95); throw new InvalidOperationException("bounded handler fixture failure"); };
            try { s.Chat(); } catch (TargetInvocationException) { }
            long before = s.First.Bytes; s.Broadcast(95);
            Assert.That(s.First.Bytes - before, Is.EqualTo(100), "Unowned output after dispatch never inherits its ended scope.");
        });
    }
    [Test]
    public void SessionReplacementDuringDispatchAndUnauthenticatedRequestHaveNoBorrowedAttribution()
    {
        Run(s =>
        {
            s.Handler = _ => { s.Session = s.Session with { Key = s.Session.Key with { Generation = 2 } }; s.Broadcast(95); };
            s.Chat();
            Assert.That(s.Guard.AdmittedBytes, Is.Zero);
            s.Session = s.Session with { AccountId = null }; s.Handler = _ => s.Broadcast(95); s.Chat();
            Assert.That(s.Guard.AdmittedBytes, Is.Zero);
            Assert.That(s.First.Bytes, Is.EqualTo(200));
        });
    }
    [Test]
    public void AsynchronousOutputDoesNotInheritThreadStaticScope()
    {
        Run(s =>
        {
            s.Handler = _ => { var t = new Thread(() => s.Broadcast(95)); t.Start(); Assert.That(t.Join(3000), Is.True); };
            s.Chat(); Assert.That(s.Guard.AdmittedBytes, Is.Zero); Assert.That(s.First.Bytes, Is.EqualTo(100));
        });
    }
    [TestCase("before-dispatch")]
    [TestCase("during-send")]
    [TestCase("budget-clock")]
    [TestCase("dispose-during-dispatch")]
    public void ObservationFailureWithdrawsOnlyBudgetAndNativeHandlerAndEachSendRunOnce(string fault)
    {
        Run(s =>
        {
            int entries = 0;
            if (fault == "before-dispatch") s.FailLookup = true;
            s.Handler = _ =>
            {
                entries++;
                if (fault == "during-send") s.FailLookup = true;
                if (fault == "budget-clock") s.Clock.Fail = true;
                if (fault == "dispose-during-dispatch") s.Guard.Dispose();
                s.Broadcast(95);
            };
            s.Chat();
            Assert.That(entries, Is.EqualTo(1));
            Assert.That(s.Guard.Healthy, Is.False);
            Assert.That(s.First.Bytes, Is.EqualTo(100));
            Assert.That(s.Second.Bytes, Is.EqualTo(100));
            Assert.That(s.Guard.BlockedBytes, Is.Zero);
            s.FailLookup = false; s.Clock.Fail = false; s.Handler = _ => { entries++; s.Broadcast(95); }; s.Chat();
            Assert.That(entries, Is.EqualTo(2));
            Assert.That(s.First.Bytes, Is.EqualTo(200));
            Assert.That(s.Guard.Healthy, Is.False, "Time passing or a later valid request cannot silently repair lost observation.");
        });
    }
    private sealed class Scenario
    {
        public required M16RequestEgressGuard Guard;
        public required SessionSnapshot Session;
        public required SocketSink First;
        public required SocketSink Second;
        public required FrozenClock Clock;
        public bool FailLookup;
        public Action<ServerChatEventArgs>? Handler;
        private static readonly MethodInfo Dispatch = typeof(HookManager).GetMethod("InvokeServerChat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Chat(int who = 7) => Dispatch.Invoke(ServerApi.Hooks,
            [new MessageBuffer { whoAmI = who }, who, "fixture no retained content", default(ChatCommandId)]);
        public void Broadcast(int payload, ushort module = 1)
        {
            var packet = new NetPacket(module, 30000); packet.Writer.Write(new byte[payload]);
            NetManager.Instance.Broadcast(packet);
        }
    }
    private void Run(Action<Scenario> action)
    {
        var oldClients = Netplay.Clients; int oldMode = Main.netMode; bool oldDed = Main.dedServ;
        Type storage = typeof(NetManager).GetNestedType("PacketTypeStorage`1", BindingFlags.Public | BindingFlags.NonPublic)!.MakeGenericType(typeof(NetTextModule));
        var id = storage.GetField("Id")!; var module = storage.GetField("Module")!;
        object? oldId = id.GetValue(null), oldModule = module.GetValue(null);
        using var plugin = new StubPlugin(); Scenario? scenario = null;
        void Handle(ServerChatEventArgs e) => scenario?.Handler?.Invoke(e);
        try
        {
            Main.netMode = 2; Main.dedServ = true; id.SetValue(null, (ushort)1); module.SetValue(null, new NetTextModule());
            var first = new SocketSink(); var second = new SocketSink();
            Netplay.Clients = Enumerable.Range(0, 256).Select(i => new RemoteClient { Id = i, Socket = new SocketSink(false) }).ToArray();
            Netplay.Clients[7].Socket = first; Netplay.Clients[8].Socket = second;
            var actor = new TSPlayer(7) { IsLoggedIn = true, Account = new UserAccount { ID = 140 } };
            var other = new TSPlayer(8) { IsLoggedIn = true, Account = new UserAccount { ID = 141 } };
            var session = new SessionSnapshot(new(Guid.NewGuid(), 1, 7, 1), 140, false, DateTimeOffset.UtcNow);
            var otherSession = session with { Key = session.Key with { Slot = 8 }, AccountId = 141 };
            var clock = new FrozenClock();
            using var guard = new M16RequestEgressGuard(clock, who =>
                scenario?.FailLookup == true ? throw new InvalidOperationException("fixture lookup unavailable") :
                who == 7 ? (scenario?.Session ?? session, actor) : (otherSession, other),
                new() { ActorBurstBytes = 200, ActorBytesPerSecond = 50, GlobalBurstBytes = 10000, GlobalBytesPerSecond = 1000 });
            guard.Install();
            scenario = new() { Guard = guard, Session = session, First = first, Second = second, Clock = clock };
            ServerApi.Hooks.ServerChat.Register(plugin, Handle, 0);
            action(scenario);
        }
        finally
        {
            ServerApi.Hooks.ServerChat.Deregister(plugin, Handle);
            id.SetValue(null, oldId); module.SetValue(null, oldModule);
            Netplay.Clients = oldClients; Main.netMode = oldMode; Main.dedServ = oldDed;
        }
    }
    private sealed class StubPlugin() : TerrariaPlugin(null!) { public override void Initialize() { } }
    private sealed class FrozenClock : TimeProvider
    { public bool Fail; public override long TimestampFrequency => TimeSpan.TicksPerSecond; public override long GetTimestamp() => Fail ? throw new InvalidOperationException("fixture clock fault") : 0; }
    private sealed class SocketSink(bool connected = true) : ISocket
    {
        public long Bytes;
        public void Close() { }
        public bool IsConnected() => connected;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(IPAddress.Loopback, 17401);
        public void Connect(RemoteAddress address) { }
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object state = null!) => Bytes += size;
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object state = null!) { }
        public bool IsDataAvailable() => false;
        public bool StartListening(SocketConnectionAccepted callback) => false;
        public void StopListening() { }
    }
}
