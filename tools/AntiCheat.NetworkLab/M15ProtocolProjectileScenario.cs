using System.Text.Json;

internal delegate Task M15AdditionalRule(string name, string rule, LabClient observer,
    Func<LabClient, Task> legal, Func<LabClient, byte[]> request, Func<bool> noSideEffect);

internal sealed record M15LowContextHost(LabClient Observer, M15AdditionalRule AdditionalRule,
    Func<long, string, string, string, Task> RuleOutcome, Func<string, string?, long> Scalar,
    Action<bool, string> Assert, string ReportDirectory);

internal static class M15ProtocolProjectileScenario
{
    public static async Task RunAsync(M15LowContextHost host)
    {
        const string creditsRule = "WORLD02.CreditsRollStateAuthority", cannonRule = "PROJ01.CannonFiringAuthority";
        var witness = host.Observer; int beforeCredits = 0, beforeCannon = 0, beforeProjectiles = 0;
        var frames = new List<object>();
        await host.AdditionalRule("M15Credits", creditsRule, witness, async actor =>
        {
            long account = host.Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            // Original callers NPC.TransformCopperSlime/TransformElderSlime emit140/1 and140/2.
            // Out-of-world target -1 safely exercises both actual dispatch branches with no NPC transform;
            // the complete legitimate producer and owning-client paths are separate native tests.
            foreach (byte operation in new byte[] { 1, 2 })
            {
                var frame = LabClient.Packet(140, writer => { writer.Write(operation); writer.Write(-1); });
                frames.Add(new { source = "synthetic-loopback-TCP", kind = "allowed-sibling-operation", actor = actor.Name, packet = 140,
                    operation, hex = Convert.ToHexString(frame) });
                await actor.SendBatch(frame);
                await host.RuleOutcome(account, creditsRule, "Pass", operation == 1
                    ? "credits-roll-sibling-copper-slime-request" : "credits-roll-sibling-elder-slime-request");
            }
            await actor.PingAsync(); await witness.PingAsync(); beforeCredits = witness.PacketCount(140);
        }, actor =>
        {
            var frame = LabClient.Packet(140, writer => { writer.Write((byte)0); writer.Write(28800); });
            frames.Add(new { source = "synthetic-loopback-TCP", kind = "server-role-publication", actor = actor.Name, packet = 140,
                operation = 0, hex = Convert.ToHexString(frame) }); return frame;
        }, () => witness.PacketCount(140) == beforeCredits);

        await host.AdditionalRule("M15Cannon", cannonRule, witness, async actor =>
        {
            // Exact legal108 delegation/actual owning-client creation is covered at native method level.
            // This network baseline checks ordinary authenticated playability, not a fabricated108 client echo.
            await actor.PingAsync(); await witness.PingAsync();
            beforeCannon = witness.PacketCount(108); beforeProjectiles = witness.PacketCount(27);
        }, actor =>
        {
            var frame = LabClient.Packet(108, writer =>
            {
                writer.Write((short)50); writer.Write(3f); writer.Write((short)20); writer.Write((short)30);
                writer.Write((short)4); writer.Write((short)4); writer.Write((byte)witness.Slot);
            });
            frames.Add(new { source = "synthetic-loopback-TCP", kind = "server-cannon-delegation", actor = actor.Name, packet = 108,
                target = witness.Slot, hex = Convert.ToHexString(frame) }); return frame;
        }, () => witness.PacketCount(108) == beforeCannon && witness.PacketCount(27) == beforeProjectiles);

        host.Assert(!witness.Closed && witness.Authenticated && host.Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + witness.Name) == 0,
            "M15-A-C:passive-cannon-target-is-not-punished");
        await File.WriteAllTextAsync(Path.Combine(host.ReportDirectory, "m15-protocol-projectile-frames.json"),
            JsonSerializer.Serialize(new { frames, normalClientUi = "not-executed", actualTargetCannonCreationOnServer = false,
                legalCounterparts = "140 real sibling dispatch;108 exact native server serialization and client creation tested independently",
                enforcement = "AdditionalRule owns real first-proof cancel/revocation/persistence/reconnect and subsequent inventory protection; slice owns restart" },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
