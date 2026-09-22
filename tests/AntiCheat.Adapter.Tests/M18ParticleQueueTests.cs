using AntiCheat.Plugin.TShock;
using AntiCheat.Rules;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.GameContent.Drawing;
using Terraria.GameContent.NetModules;
using Terraria.Net;
using Terraria.UI;
using TerrariaApi.Server;
using Microsoft.Xna.Framework.Graphics;

namespace AntiCheat.Adapter.Tests;

public sealed partial class M6NpcStrikeTests
{
    [Test]
    public void TestLabLightningQueueStopsStormPacketsBeforeNetModuleReceiverWithoutSanction()
    {
        var queueOptions = new M18ParticleQueueOptions
        {
            Enabled = true,
            WindowTicks = 10,
            PerSessionEvents = 2,
            EventCapacity = 4,
        };
        var dataDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "data", "progression");
        var business = new M2BusinessAdapter(TargetRuntime.Fingerprint, dataDirectory,
            particleQueueOptions: queueOptions);
        typeof(AntiCheatPlugin).GetField("_business", Private)!.SetValue(plugin, business);
        var manager = NetManager.Instance;
        var oldModules = manager._modules.ToArray();
        var moduleCountField = typeof(NetManager).GetField("_moduleCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)!;
        var storage = typeof(NetManager).GetNestedType("PacketTypeStorage`1",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericType(typeof(NetParticlesModule));
        var storageId = storage.GetField("Id", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        var storageModule = storage.GetField("Module", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        var oldModuleCount = moduleCountField.GetValue(manager);
        var oldStorageId = storageId.GetValue(null);
        var oldStorageModule = storageModule.GetValue(null);
        bool registeredHere = manager.GetModule<NetParticlesModule>() is null;
        try
        {
            if (registeredHere) manager.Register<NetParticlesModule>();
            byte[] body = NativeParticleBody(ParticleOrchestraType.StormLightning, Slot);
            byte[] payloadMismatch = NativeParticleBody(ParticleOrchestraType.StormLightning, (byte)(Slot + 1));
            Assert.That(M18ParticlePacketReader.ReadPayload(body,
                NetManager.Instance.GetId<NetParticlesModule>()).Kind, Is.EqualTo(PacketReadKind.Parsed));
            Assert.That(RootParticle(payloadMismatch).Handled, Is.False,
                "A payload slot mismatch is recorded against the authenticated sender and is not an automatic ban/block.");
            Assert.That(RootParticle(body).Handled, Is.False);

            // With no connected socket, the actual MessageBuffer -> NetManager ->
            // NetParticlesModule path still consumes the complete native frame.
            // The mismatch plus one matching frame already fill the same
            // authenticated session budget; the next matching frame stops.
            ReceiveParticle(body);
            Assert.That(RootParticle(body).Handled, Is.True,
                "The third attributed StormLightning request exceeds the finite lab queue.");
            Assert.That(engine.CanWrite(session), Is.True, "A resource stop-loss cannot revoke the session.");
            Assert.That(engine.SanctionCount, Is.Zero, "The candidate is not cheat proof or sanction evidence.");

            // Re-entering through the native boundary must preserve the raw-hook cancellation.
            ReceiveParticle(body);

            business.Forget(session);
            Assert.That(RootParticle(body).Handled, Is.False,
                "Exact-session cleanup opens a new queue window.");
            Assert.That(engine.CanWrite(session), Is.True);
            Assert.That(engine.SanctionCount, Is.Zero);
            TestContext.Out.WriteLine("TestLab StormLightning queue: one payload-mismatch and one matching frame were owned by the authenticated sender, the next frame cancelled before NetParticlesModule, exact-session forget reopened, sanction=0.");
        }
        finally
        {
            typeof(AntiCheatPlugin).GetField("_business", Private)!.SetValue(plugin, null);
            if (registeredHere)
            {
                manager._modules.Clear();
                foreach (var (id, module) in oldModules) manager._modules[id] = module;
                moduleCountField.SetValue(manager, oldModuleCount);
                storageId.SetValue(null, oldStorageId);
                storageModule.SetValue(null, oldStorageModule);
            }
        }
    }

    private GetDataEventArgs RootParticle(byte[] body, bool cancelled = false)
    {
        var args = M2ContractsTests.Packet((PacketTypes)M18ParticlePacketReader.MessageId, body, Slot);
        args.Handled = cancelled;
        ServerApi.Hooks.NetGetData.Invoke(args);
        return args;
    }

    private void ReceiveParticle(byte[] body)
    {
        // NetParticlesModule broadcasts through all 256 client slots. The shared
        // strike fixture owns one slot only, so fill the remaining native array
        // with disconnected clients for this bounded receiver exercise.
        var oldClients = Netplay.Clients.ToArray();
        var diagnosticsField = typeof(Main).GetField("_activeNetDiagnosticsUI",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)!;
        var oldDiagnostics = diagnosticsField.GetValue(null);
        try
        {
            diagnosticsField.SetValue(null, new NullDiagnostics());
            for (int i = 0; i < Netplay.Clients.Length; i++)
                // State 10 bypasses the MessageBuffer pre-hello boot branch while
                // the null Socket keeps every broadcast recipient disconnected.
                Netplay.Clients[i] = new RemoteClient { State = 10 };
            var buffer = new MessageBuffer { whoAmI = Slot };
            buffer.readBuffer[0] = (byte)M18ParticlePacketReader.MessageId;
            body.CopyTo(buffer.readBuffer, 1);
            buffer.ResetReader();
            buffer.GetData(0, body.Length + 1, out int parsedType);
            Assert.That(parsedType, Is.EqualTo(M18ParticlePacketReader.MessageId));
        }
        finally
        {
            oldClients.CopyTo(Netplay.Clients, 0);
            diagnosticsField.SetValue(null, oldDiagnostics);
        }
    }

    private static byte[] NativeParticleBody(ParticleOrchestraType type, byte invokingPlayer)
    {
        var settings = new ParticleOrchestraSettings
        {
            PositionInWorld = new Vector2(320.5f, 640.25f),
            MovementVector = new Vector2(14.5f, -3.25f),
            UniqueInfoPiece = 0x10203040,
            IndexOfPlayerWhoInvokedThis = invokingPlayer,
        };
        var packet = NetParticlesModule.Serialize(type, settings);
        try
        {
            packet.ShrinkToFit();
            packet.Reader.BaseStream.Position = 3;
            return packet.Reader.ReadBytes(packet.Length - 3);
        }
        finally { packet.Recycle(); }
    }

    private sealed class NullDiagnostics : INetDiagnosticsUI
    {
        public void Reset() { }
        public void Draw(SpriteBatch spriteBatch) { }
        public void CountReadMessage(int messageId, int messageLength) { }
        public void CountSentMessage(int messageId, int messageLength) { }
        public void CountReadModuleMessage(int moduleMessageId, int messageLength) { }
        public void CountSentModuleMessage(int moduleMessageId, int messageLength) { }
        public void RotateSendRecvCounters() { }
        public void GetLastSentRecvBytes(out int sent, out int recv) { sent = 0; recv = 0; }
    }
}
