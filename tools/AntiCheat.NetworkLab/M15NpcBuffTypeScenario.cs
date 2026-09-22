using System.Text.Json;

internal static class M15NpcBuffTypeScenario
{
    public static async Task RunAsync(M15LowContextHost host)
    {
        const string rule = "G03.NpcBuffTypeContract";
        var observer = host.Observer;
        int before = 0;
        await host.AdditionalRule("M15BuffType", rule, observer, async actor =>
        {
            long account = host.Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            host.Assert(host.Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", actor.Name) == 1,
                rule + ":ordinary-account-no-npcbuff-bypass");
            await actor.Send(53, writer => { writer.Write((short)199); writer.Write((ushort)24); writer.Write((short)60); });
            await host.RuleOutcome(account, rule, "Pass", "native-npc-buff-producer-type");
            await actor.Send(120, writer => { writer.Write(actor.Slot); writer.Write((byte)7); });
            await observer.WaitUntil(() => observer.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 7), TimeSpan.FromSeconds(5));
            await actor.PingAsync(); await observer.PingAsync(); before = observer.NpcBuffUpdates.Count;
        }, actor => LabClient.Packet(53, writer => { writer.Write((short)199); writer.Write((ushort)5); writer.Write((short)60); }),
            () => observer.NpcBuffUpdates.Skip(before).All(update => update.Npc != 199));
        await File.WriteAllTextAsync(Path.Combine(host.ReportDirectory, "m15-npc-buff-type.json"),
            JsonSerializer.Serialize(new
            {
                rule, version = "1.0.0", source = "synthetic-loopback-TCP", clientGui = false,
                legalRequest = new { packet = 53, npc = 199, buff = 24, time = 60 },
                firstProof = new { packet = 53, npc = 199, buff = 5, time = 60 },
                targetActivityAsserted = false,
                effectScope = "No forbidden packet54 after first proof; native AddBuff state and producer branches have separate method tests.",
                enforcement = "Existing AdditionalRule checks first cancellation/revocation, unique permanent account row, coalesced successor absence, innocent observer and reconnect rejection. Shared slice checks restart."
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
