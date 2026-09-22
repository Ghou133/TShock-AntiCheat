using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real TCP5/147 reaches the running server's native UpdateEquips. Evidence is the bounded
/// existing calculation callback; there is no direct host call, native fixture, or GUI claim.</summary>
internal static class M12EquipmentScenario
{
    private const string Marker = "ANTICHEAT_M11_EQUIPMENT_EFFECT ";
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m12-equipment"); Directory.CreateDirectory(directory);
        var results = new List<object>(32); var clients = new List<LabClient>(2);
        string status = "failed", failure = ""; int start = host.ConsoleLines().Length;
        try
        {
            var actor = await Actor("M12Emblems"); var peer = await Actor("M12EmblemPeer");
            foreach (short type in new short[] { 489, 490, 491, 2998 })
            {
                await Slot(actor, 3, type, $"single-{type}", 0, expectedType: type, expectedBonus: .15f);
                await Slot(actor, 4, type, $"move-transient-{type}", 1, expectedType: type, expectedBonus: .15f);
                await Slot(actor, 3, 0, $"move-complete-{type}", 0, expectedType: type, expectedBonus: .15f);
                await Slot(actor, 4, 0, $"removed-{type}", 0, expectedType: 0, expectedBonus: 0);
            }
            // One instance of each type is legal. Equal display categories never create conflicts.
            int mask = 0;
            foreach (short type in new short[] { 489, 490, 491, 2998 })
            {
                int index = Index(type); mask |= 1 << index;
                await Slot(actor, 3 + index, type, $"different-types-{type}", 0, expectedType: -1, expectedBonus: .15f, mask: mask);
            }
            await Slot(actor, 7, 935, "different-types-plus-avenger", 0, expectedType: -1, expectedBonus: .27f, mask: 15);
            await Slot(actor, 8, 490, "locked-slot-storage-remains", 0, expectedType: -1, expectedBonus: .27f, mask: 15);
            await Loadout(1, "ordinary-empty-loadout", 0, 0);
            await Loadout(0, "ordinary-return-loadout", 15, .27f);
            await Slot(peer, 3, 490, "independent-account-single", 0, 490, .15f);
            await Slot(peer, 4, 490, "independent-account-own-duplicate", 1, 490, .15f);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(start).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)),
                "no-cheat-proof-or-account-sanction");
            status = "passed";

            async Task Slot(LabClient owner, int slot, short type, string label, int blocked, int expectedType, float expectedBonus, int mask = -1)
            {
                int mark = host.ConsoleLines().Length, received = peer.InventoryUpdates.Count;
                byte[] frame = LabClient.Packet(5, writer =>
                { writer.Write(owner.Slot); writer.Write((short)(59 + slot)); writer.Write((short)(type == 0 ? 0 : 1)); writer.Write((byte)0); writer.Write(type); writer.Write((byte)0); });
                await owner.SendBatch(frame); await owner.PingAsync();
                var sample = await Effect(owner, mark, value => value.GetProperty("After").GetProperty("Slots").EnumerateArray().Any(item =>
                    item.GetProperty("Slot").GetInt32() == slot && item.GetProperty("ItemId").GetInt32() == type), label);
                Validate(sample, blocked, expectedType, expectedBonus, mask, label);
                if (!ReferenceEquals(owner, peer))
                {
                    await peer.WaitUntil(() => peer.InventoryUpdates.Skip(received).Any(x => x.Player == owner.Slot && x.Slot == 59 + slot && x.Item == type), TimeSpan.FromSeconds(5));
                    Check(true, label + "-native-slot-forward-keeps-storage");
                }
                Healthy(owner); results.Add(new { label, frameHex = Convert.ToHexString(frame), sample });
            }
            async Task Loadout(byte page, string label, int mask, float bonus)
            {
                int mark = host.ConsoleLines().Length;
                byte[] frame = LabClient.Packet(147, writer => { writer.Write(actor.Slot); writer.Write(page); writer.Write((ushort)0); });
                await actor.SendBatch(frame); await actor.PingAsync();
                var sample = await Effect(actor, mark, value => value.GetProperty("After").GetProperty("ActiveLoadout").GetInt32() == page, label);
                Validate(sample, 0, -1, bonus, mask, label); Healthy(actor);
                results.Add(new { label, frameHex = Convert.ToHexString(frame), sample });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, results, syntheticLoopbackTcp = true, nativeServerUpdateEquips = true, stockClientGui = false,
                scope = "per-type class-emblem base additions in native server ApplyEquipFunctional; separate prefixes and stored items retained",
                clientCombatBenefitsEliminated = false, clientCompletionClaimed = false, acquisitionHistoryClaimed = false,
                diagnosticContract = "existing M8 call scope; at most32 shape changes per session; no enforcement input"
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m12-equipment:" + label);
        long Scalar(string sql, string name)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(host.RunDirectory, "tshock", "tshock.sqlite") + ";Mode=ReadOnly");
            db.Open(); using var query = db.CreateCommand(); query.CommandText = sql; query.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(query.ExecuteScalar() ?? 0L);
        }
        async Task<LabClient> Actor(string name)
        {
            var actor = await host.Connect(name); clients.Add(actor); await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350 && Scalar("SELECT COUNT(*) FROM Users WHERE Username=$name AND UserGroup='default'", name) == 1,
                name + "-ordinary-authenticated-ssc-default-account"); return actor;
        }
        void Healthy(LabClient actor) => Check(!actor.Closed && actor.DisconnectReason is null &&
            Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0, actor.Name + "-connected-unbanned");
        async Task<JsonElement> Effect(LabClient actor, int mark, Func<JsonElement, bool> predicate, string label)
        {
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name); var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                foreach (var line in host.ConsoleLines().Skip(mark))
                {
                    int index = line.IndexOf(Marker, StringComparison.Ordinal); if (index < 0) continue;
                    using var document = JsonDocument.Parse(line[(index + Marker.Length)..]); var value = document.RootElement;
                    if (value.GetProperty("AccountId").GetInt64() == account && value.GetProperty("Session").GetProperty("Slot").GetInt32() == actor.Slot && predicate(value)) return value.Clone();
                }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Missing native class-emblem effect observation: " + label);
        }
        void Validate(JsonElement sample, int blocked, int expectedType, float bonus, int mask, string label)
        {
            Check(sample.GetProperty("NativeBodyReturned").GetBoolean() && sample.GetProperty("InputsStable").GetBoolean() &&
                sample.GetProperty("ClassEmblemGuardHealthy").GetBoolean() && sample.GetProperty("GuardHealthy").GetBoolean(), label + "-complete-native-calculation");
            Check(sample.GetProperty("ClassEmblemBonusesBlocked").GetInt32() == blocked, label + "-exact-base-effects-suppressed");
            string[] fields = ["MagicDelta", "MeleeDelta", "RangedDelta", "MinionDelta"];
            for (int index = 0; index < fields.Length; index++)
            {
                float expected = expectedType == -1 ? ((mask & (1 << index)) != 0 ? bonus : 0) : Index(expectedType) == index ? bonus : 0;
                Check(Math.Abs(sample.GetProperty(fields[index]).GetSingle() - expected) < .0001f, label + "-" + fields[index]);
            }
            if (mask >= 0) Check(sample.GetProperty("ClassEmblemAppliedMask").GetInt32() == mask, label + "-independent-native-type-mask");
        }
    }
    private static int Index(int type) => type switch { 489 => 0, 490 => 1, 491 => 2, 2998 => 3, _ => -1 };
}
