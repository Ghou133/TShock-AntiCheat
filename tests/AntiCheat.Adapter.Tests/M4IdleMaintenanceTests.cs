using System.Reflection;
using AntiCheat.Plugin.TShock;
using NUnit.Framework;
using Terraria;

namespace AntiCheat.Adapter.Tests;

// Tests the actual root callback against its real bounded dispatcher. No game loop or TCP is claimed here.
[TestFixture, NonParallelizable]
public sealed class M4IdleMaintenanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private AntiCheatPlugin plugin = null!;
    private ServerThreadDispatcher dispatcher = null!;
    private Action idle = null!;
    private Action<EventArgs> update = null!;
    private int oldMode;
    private bool oldConnected;

    [SetUp]
    public void SetUp()
    {
        oldMode = Main.netMode; oldConnected = Netplay.HasFullyConnectedClients;
        Main.netMode = 2; Netplay.HasFullyConnectedClients = false;
        plugin = new AntiCheatPlugin(null!);
        dispatcher = (ServerThreadDispatcher)typeof(AntiCheatPlugin).GetField("_dispatcher", Private)!.GetValue(plugin)!;
        idle = typeof(AntiCheatPlugin).GetMethod("OnIdleMaintenance", Private)!.CreateDelegate<Action>(plugin);
        update = typeof(AntiCheatPlugin).GetMethod("OnUpdate", Private)!.CreateDelegate<Action<EventArgs>>(plugin);
    }

    [TearDown]
    public void TearDown()
    {
        plugin.Dispose();
        Main.netMode = oldMode; Netplay.HasFullyConnectedClients = oldConnected;
    }

    [Test]
    public void RepeatedIdleCallbacksEachDrainOneAndActiveEventsDoNotDoubleDrain()
    {
        int calls = 0;
        var work = Enumerable.Range(0, 4).Select(_ => dispatcher.InvokeAsync(() => calls++).AsTask()).ToArray();
        idle();
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(dispatcher.PendingCount, Is.EqualTo(3));
        idle();
        Assert.That(calls, Is.EqualTo(2));
        Netplay.HasFullyConnectedClients = true;
        update(EventArgs.Empty);
        Assert.That(calls, Is.EqualTo(3));
        idle(); idle(); // The target emits the third-party event twice inside active Update.
        Assert.That(calls, Is.EqualTo(3));
        Assert.That(dispatcher.PendingCount, Is.EqualTo(1));
        Netplay.HasFullyConnectedClients = false;
        idle();
        Assert.That(calls, Is.EqualTo(4));
        Assert.That(work.All(task => task.IsCompletedSuccessfully), Is.True);
    }

    [TestCase(0)]
    [TestCase(1)]
    public void NonServerThirdPartyEventDoesNotDrain(int mode)
    {
        int calls = 0;
        var work = dispatcher.InvokeAsync(() => calls++).AsTask();
        Main.netMode = mode;
        idle(); idle();
        Assert.That(calls, Is.Zero);
        Assert.That(work.IsCompleted, Is.False);
        Assert.That(dispatcher.PendingCount, Is.EqualTo(1));
        Main.netMode = 2;
        idle();
        Assert.That(work.IsCompletedSuccessfully, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DispatcherThreadFaultIsContainedAndStopsLaterActiveAndIdleMaintenance(bool faultReporterFails)
    {
        update(EventArgs.Empty); // Binds the actual dispatcher to this update thread.
        int calls = 0;
        var work = dispatcher.InvokeAsync(() => calls++).AsTask();
        if (faultReporterFails)
            typeof(AntiCheatPlugin).GetField("_reportedContextFaults", Private)!.SetValue(plugin, null);
        Exception? wrongThread = null;
        var thread = new Thread(() =>
        {
            try { idle(); }
            catch (Exception exception) { wrongThread = exception; }
        }) { IsBackground = true };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(3)), Is.True);
        Assert.That(wrongThread, Is.Null, "Even a fault reporter failure must not escape the raw idle Action.");
        Assert.That(typeof(AntiCheatPlugin).GetField("_infrastructureFailed", Private)!.GetValue(plugin), Is.True);
        Assert.That(typeof(AntiCheatPlugin).GetField("_maintenanceCallbackFailed", Private)!.GetValue(plugin), Is.EqualTo(1));
        Assert.That(calls, Is.Zero);
        Assert.That(work.IsCompleted, Is.False);
        idle(); idle(); update(EventArgs.Empty); // Returning to the correct thread cannot silently recover the fault.
        Assert.That(calls, Is.Zero);
        Assert.That(work.IsCompleted, Is.False);
        Assert.That(dispatcher.PendingCount, Is.EqualTo(1));
        if (!faultReporterFails)
        {
            var reports = (HashSet<string>)typeof(AntiCheatPlugin).GetField("_reportedContextFaults", Private)!.GetValue(plugin)!;
            Assert.That(reports, Is.EquivalentTo(new[] { "InfrastructureMaintenance" }));
        }
    }
}
