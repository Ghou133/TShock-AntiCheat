using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Real TCP5/147 effects using the existing optional TestLab calculation evidence.
/// No direct native invocation or scaffold fixture is accepted as the server execution.</summary>
internal static class M11EquipmentScenario
{
    private const string Marker = "ANTICHEAT_M11_EQUIPMENT_EFFECT ";
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m11-equipment"); Directory.CreateDirectory(directory);
        var results = new List<object>(12); var clients = new List<LabClient>(2);
        string status = "failed", failure = ""; int start = host.ConsoleLines().Length;
        try
        {
            var actor = await Actor("M11Equipment"); var peer = await Actor("M11EquipPeer");
            await Slot(actor, 3, 935, "single-native-avenger", 1, 0, .12f);
            await Slot(actor, 4, 935, "legal-move-transient-two-stored-slots", 2, 1, .12f);
            await Slot(actor, 3, 0, "legal-move-original-slot-removed", 1, 0, .12f);
            await Slot(actor, 3, 935, "normal-return-move-transient", 2, 1, .12f);
            await Slot(actor, 4, 0, "normal-return-move-complete", 1, 0, .12f);
            await Slot(actor, 4, 490, "different-legal-emblem-still-stacks", 1, 0, .27f);
            await Slot(actor, 4, 0, "different-emblem-removed", 1, 0, .12f);
            await Slot(actor, 8, 935, "locked-storage-does-not-become-effect", 2, 0, .12f);
            await Loadout(1, "empty-other-loadout", 0, 0f);
            await Loadout(0, "return-original-loadout", 2, .12f);
            await Slot(peer, 3, 935, "second-account-independent-single", 1, 0, .12f);
            await Slot(peer, 4, 935, "second-account-own-duplicate-effect", 2, 1, .12f);
            await Slot(peer, 3, 0, "second-account-normal-recovery", 1, 0, .12f);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(start).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)),
                "no-equipment-cheat-proof-or-account-sanction");
            status = "passed";

            async Task Slot(LabClient owner, int slot, short type, string label, int storedCount, int blocked, float melee)
            {
                int mark = host.ConsoleLines().Length, received = peer.InventoryUpdates.Count;
                byte[] frame = LabClient.Packet(5, writer =>
                { writer.Write(owner.Slot); writer.Write((short)(59 + slot)); writer.Write((short)(type == 0 ? 0 : 1)); writer.Write((byte)0); writer.Write(type); writer.Write((byte)0); });
                await owner.SendBatch(frame); await owner.PingAsync();
                var result = await Effect(owner, mark, sample =>
                    sample.GetProperty("After").GetProperty("Slots").EnumerateArray().Any(item =>
                        item.GetProperty("Slot").GetInt32() == slot && item.GetProperty("ItemId").GetInt32() == type), label);
                Validate(result, storedCount, blocked, melee, label);
                if (!ReferenceEquals(owner, peer))
                {
                    await peer.WaitUntil(() => peer.InventoryUpdates.Skip(received).Any(x =>
                        x.Player == owner.Slot && x.Slot == 59 + slot && x.Item == type), TimeSpan.FromSeconds(5));
                    Check(true, label + "-native-slot-forward-keeps-ordinary-storage-update");
                }
                Healthy(owner); results.Add(new { label, frameHex = Convert.ToHexString(frame), result });
            }
            async Task Loadout(byte page, string label, int stored, float melee)
            {
                int mark = host.ConsoleLines().Length;
                byte[] frame = LabClient.Packet(147, writer => { writer.Write(actor.Slot); writer.Write(page); writer.Write((ushort)0); });
                await actor.SendBatch(frame); await actor.PingAsync();
                var result = await Effect(actor, mark, sample => sample.GetProperty("After").GetProperty("ActiveLoadout").GetInt32() == page, label);
                Validate(result, stored, 0, melee, label); Healthy(actor);
                results.Add(new { label, frameHex = Convert.ToHexString(frame), result });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, results, syntheticLoopbackTcp = true, nativeServerUpdateEquips = true, stockClientGui = false,
                scope = "server four additive type935 functional fields only; prefix methods and storage retained",
                clientCompletionClaimed = false, acquisitionHistoryClaimed = false,
                diagnosticContract = "existing M8 call scope, shape changes only, at most32 per session; no enforcement input"
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }

        void Check(bool value, string label) => host.Assert(value, "m11-equipment:" + label);
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
            long account = Scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                DevRunLifecycle.Check();
                foreach (var line in host.ConsoleLines().Skip(mark))
                {
                    int index = line.IndexOf(Marker, StringComparison.Ordinal); if (index < 0) continue;
                    using var document = JsonDocument.Parse(line[(index + Marker.Length)..]);
                    var value = document.RootElement;
                    if (value.GetProperty("AccountId").GetInt64() == account &&
                        value.GetProperty("Session").GetProperty("Slot").GetInt32() == actor.Slot && predicate(value)) return value.Clone();
                }
                await DevRunLifecycle.WaitAsync(Task.Delay(25), TimeSpan.FromSeconds(1));
            }
            throw new TimeoutException("Missing actual native equipment-effect observation: " + label);
        }
        void Validate(JsonElement sample, int stored, int blocked, float melee, string label)
        {
            Check(sample.GetProperty("NativeBodyReturned").GetBoolean() && sample.GetProperty("InputsStable").GetBoolean() &&
                sample.GetProperty("GuardHealthy").GetBoolean(), label + "-same-session-complete-native-body-and-inputs");
            Check(sample.GetProperty("After").GetProperty("Slots").EnumerateArray().Count(slot => slot.GetProperty("ItemId").GetInt32() == 935) == stored,
                label + "-stored-slots-are-retained");
            Check(sample.GetProperty("AvengerBonusesBlocked").GetInt32() == blocked &&
                Math.Abs(sample.GetProperty("MeleeDelta").GetSingle() - melee) < .0001f &&
                Math.Abs(sample.GetProperty("RangedDelta").GetSingle() - (stored == 0 ? 0 : .12f)) < .0001f &&
                Math.Abs(sample.GetProperty("MagicDelta").GetSingle() - (stored == 0 ? 0 : .12f)) < .0001f &&
                Math.Abs(sample.GetProperty("MinionDelta").GetSingle() - (stored == 0 ? 0 : .12f)) < .0001f,
                label + "-actual-native-four-field-effect");
        }
    }
}
