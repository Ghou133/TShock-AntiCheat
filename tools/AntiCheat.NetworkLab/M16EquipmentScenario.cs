using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Authenticated TCP5/147 reaches ordinary server UpdateEquips; its existing bounded
/// diagnostic reports the actual native fields after the guard. No test flag grants eligibility.</summary>
internal static class M16EquipmentScenario
{
    private const string Marker = "ANTICHEAT_M11_EQUIPMENT_EFFECT ";
    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string directory = Path.Combine(host.ReportDirectory, "m16-equipment"); Directory.CreateDirectory(directory);
        var results = new List<object>(32); var clients = new List<LabClient>(2);
        string status = "failed", failure = ""; int start = host.ConsoleLines().Length;
        try
        {
            var actor = await Actor("M16ManaEquip"); var peer = await Actor("M16ManaPeer");
            foreach (short type in new short[] { 111, 1595, 2221, 982, 6188, 6189 })
            {
                int regen = Regeneration(type);
                await Slot(actor, 3, type, $"legal-single-{type}", 0, 0, 40, regen);
                await Slot(actor, 4, type, $"move-transient-{type}", 1, regen == 0 ? 0 : 1, 40, regen);
                await Slot(actor, 3, 0, $"move-complete-{type}", 0, 0, 40, regen);
                await Slot(actor, 4, 0, $"ordinary-removal-{type}", 0, 0, 0, 0);
            }
            await Slot(actor, 3, 111, "different-mana-type-first", 0, 0, 40, 0);
            await Slot(actor, 4, 982, "different-mana-types-stack", 0, 0, 80, 60);
            await Slot(actor, 5, 2221, "different-mana-types-plus-cuffs", 0, 0, 120, 60);
            await Slot(actor, 8, 982, "locked-mana-storage-stays", 0, 0, 120, 60);
            await Loadout(1, "ordinary-empty-loadout", 0, 0);
            await Loadout(0, "ordinary-return-loadout", 120, 60);
            await Slot(peer, 3, 982, "independent-account-single", 0, 0, 40, 60);
            await Slot(peer, 4, 982, "independent-account-repeated-effect", 1, 1, 40, 60);
            await Slot(peer, 3, 0, "independent-account-recovery", 0, 0, 40, 60);
            Healthy(actor); Healthy(peer);
            Check(!host.ConsoleLines().Skip(start).Any(line => line.Contains("ANTICHEAT_INCIDENT ", StringComparison.Ordinal)),
                "no-cheat-proof-or-account-sanction");
            status = "passed";

            async Task Slot(LabClient owner, int slot, short type, string label, int capacityBlocked, int regenerationBlocked,
                int capacity, int regeneration)
            {
                int mark = host.ConsoleLines().Length, received = peer.InventoryUpdates.Count;
                byte[] frame = LabClient.Packet(5, writer =>
                { writer.Write(owner.Slot); writer.Write((short)(59 + slot)); writer.Write((short)(type == 0 ? 0 : 1)); writer.Write((byte)0); writer.Write(type); writer.Write((byte)0); });
                await owner.SendBatch(frame); await owner.PingAsync();
                var sample = await Effect(owner, mark, value => value.GetProperty("After").GetProperty("Slots").EnumerateArray().Any(item =>
                    item.GetProperty("Slot").GetInt32() == slot && item.GetProperty("ItemId").GetInt32() == type), label);
                Validate(sample, capacityBlocked, regenerationBlocked, capacity, regeneration, label);
                if (!ReferenceEquals(owner, peer))
                {
                    await peer.WaitUntil(() => peer.InventoryUpdates.Skip(received).Any(x => x.Player == owner.Slot && x.Slot == 59 + slot && x.Item == type), TimeSpan.FromSeconds(5));
                    Check(true, label + "-native-slot-forward-keeps-storage");
                }
                Healthy(owner); results.Add(new { label, frameHex = Convert.ToHexString(frame), sample });
            }
            async Task Loadout(byte page, string label, int capacity, int regeneration)
            {
                int mark = host.ConsoleLines().Length;
                byte[] frame = LabClient.Packet(147, writer => { writer.Write(actor.Slot); writer.Write(page); writer.Write((ushort)0); });
                await actor.SendBatch(frame); await actor.PingAsync();
                var sample = await Effect(actor, mark, value => value.GetProperty("After").GetProperty("ActiveLoadout").GetInt32() == page, label);
                Validate(sample, 0, 0, capacity, regeneration, label); Healthy(actor);
                results.Add(new { label, frameHex = Convert.ToHexString(frame), sample });
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
            {
                status, failure, results, syntheticLoopbackTcp = true, nativeServerUpdateEquips = true, stockClientGui = false,
                ruleId = "B2.NativeManaAccessoryFunctionalMultiplicity", ruleVersion = "1.0.0", originalId = "v2:C04",
                wholeOriginalTaskComplete = false, finiteManaMechanismBreakthroughClaimed = false,
                scope = "six per-type native server accessory mana capacity/regeneration additions; item slots, prefixes and other effects retained",
                clientManaBenefitsEliminated = false, clientCompletionClaimed = false, acquisitionHistoryClaimed = false,
                diagnosticContract = "existing M8 calculation scope; at most32 shape changes per session; no enforcement input"
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var client in clients) await client.DisposeAsync();
        }
        void Check(bool value, string label) => host.Assert(value, "m16-equipment:" + label);
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
            throw new TimeoutException("Missing actual native mana accessory effect observation: " + label);
        }
        void Validate(JsonElement sample, int capacityBlocked, int regenerationBlocked, int capacity, int regeneration, string label)
        {
            Check(sample.GetProperty("NativeBodyReturned").GetBoolean() && sample.GetProperty("InputsStable").GetBoolean() &&
                sample.GetProperty("ManaAccessoryGuardHealthy").GetBoolean(), label + "-healthy-complete-native-calculation");
            Check(sample.GetProperty("ManaCapacityBonusesBlocked").GetInt32() == capacityBlocked &&
                sample.GetProperty("ManaRegenerationBonusesBlocked").GetInt32() == regenerationBlocked, label + "-exact-base-effects-suppressed");
            Check(sample.GetProperty("ManaCapacityDelta").GetInt32() == capacity &&
                sample.GetProperty("ManaRegenerationDelta").GetInt32() == regeneration, label + "-actual-native-mana-fields");
        }
    }
    private static int Regeneration(int type) => type is 982 or 6188 or 6189 ? 60 : 0;
}
