using System.Reflection;
using AntiCheat.Core;
using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using NUnit.Framework;
using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;
using ServerTShock = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [Test]
    public async Task RegisteredRootCancelsLifeSyncAndDisconnectsOnlyTheCurrentSessionWithoutSanction()
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(AntiCheatPlugin).GetField(name, privateInstance)!.SetValue(plugin, value);
        var fixtureActor = ServerTShock.Players[Slot]!;
        var actor = new M18RootCapturingPlayer(Slot)
        {
            IsLoggedIn = fixtureActor.IsLoggedIn,
            HasSentInventory = fixtureActor.HasSentInventory,
            ReceivedInfo = fixtureActor.ReceivedInfo,
            Account = fixtureActor.Account,
            Group = fixtureActor.Group,
        };
        ServerTShock.Players[Slot] = actor;
        var bindingType = typeof(AntiCheatPlugin).GetNestedType("Binding", BindingFlags.NonPublic)!;
        ((Array)typeof(AntiCheatPlugin).GetField("_bindings", privateInstance)!
            .GetValue(plugin)!).SetValue(Activator.CreateInstance(bindingType, session, actor), Slot);
        bool oldSsc = Main.ServerSideCharacter;
        Main.ServerSideCharacter = true;
        Main.player[Slot].statLife = 100;
        Main.player[Slot].statLifeMax = 100;
        Main.player[Slot].statLifeMax2 = 100;

        using var lockHealth = new M18LockHealthContext(TimeProvider.System, TargetRuntime.Fingerprint, new()
        {
            Enabled = true,
            WindowTicks = 120,
            ResponseWindowTicks = 12,
            RequiredPairs = 3,
            PendingCapacity = 4,
        });
        lockHealth.Install(slot => slot == Slot
            ? (engine.GetSession(session), actor)
            : (null, null));
        lockHealth.Tick(session.WorldEpoch);
        Set("_lockHealth", lockHealth);

        void HurtSink(object? sender, HookEvents.Terraria.NetMessage.SendPlayerHurtEventArgs args)
            => args.ContinueExecution = false;
        HookEvents.Terraria.NetMessage.SendPlayerHurt += HurtSink;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                actor.TPlayer.statLife = 99;
                NetMessage.SendPlayerHurt(Slot, PlayerDeathReason.ByOther(0), 1, -1, false, false, 0);
                var sync = M2ContractsTests.Packet(PacketTypes.PlayerHp,
                    new byte[] { Slot, 100, 0, 100, 0 }, Slot);
                ServerApi.Hooks.NetGetData.Invoke(sync);
                if (i < 2) Assert.That(sync.Handled, Is.False);
                else Assert.That(sync.Handled, Is.True);
            }

            await engine.PumpAsync();

            Assert.Multiple(() =>
            {
                Assert.That(actor.Disconnects, Is.EqualTo(1));
                Assert.That(engine.CanWrite(session), Is.True,
                    "A service-rule kick must not revoke the account or session.");
                Assert.That(engine.SanctionCount, Is.Zero);
                Assert.That(store.Bans, Is.Zero);
            });
            TestContext.Out.WriteLine("Registered root: three server SendPlayerHurt(1) -> full-life SSC sync pairs cancelled the third packet and disconnected once; account remained writable, sanctions=0.");
        }
        finally
        {
            HookEvents.Terraria.NetMessage.SendPlayerHurt -= HurtSink;
            Set("_lockHealth", null!);
            Main.ServerSideCharacter = oldSsc;
        }
    }

    private sealed class M18RootCapturingPlayer(int slot) : TSPlayer(slot)
    {
        public int Disconnects;
        public override void Disconnect(string reason) => Disconnects++;
    }
}
