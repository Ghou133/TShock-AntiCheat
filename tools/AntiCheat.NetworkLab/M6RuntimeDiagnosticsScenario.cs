using System.Diagnostics;
using System.Text.Json;

/// <summary>Bounded, identical input templates with and without AntiCheat. The original core protections remain loaded.</summary>
internal static class M6RuntimeDiagnosticsScenario
{
    public static async Task RunAsync(M4ObservedReplayHarness host, bool withAntiCheat)
    {
        string directory = Path.Combine(host.ReportDirectory, "m6-runtime-diagnostics");
        Directory.CreateDirectory(directory);
        var sequence = new List<object>(24);
        var timer = Stopwatch.StartNew();
        await host.FixtureSnapshot(); // Existing console/isolation validation enables the bounded observer.
        var control = await Actor("M6RuntimeControl");
        // Finite same-runtime plugin present/absent comparison; both arms retain the exact TShock core.
        await M7ProjectileCleanupScenario.RunAsync(host, control, withAntiCheat);
        var subject = await Actor("M6RuntimeSubject");
        await host.ConsoleCommand("qa_m5_target " + subject.Name);
        var before = await State("target-before");
        byte slot = before.GetProperty("target").GetProperty("index").GetByte();
        byte generation = before.GetProperty("target").GetProperty("generation").GetByte();
        Check(before.GetProperty("target").GetProperty("life").GetInt32() == 3000, "same-owned-live-target-baseline");
        await subject.Send(28, writer => { writer.Write(slot); writer.Write(generation); writer.Write((short)20); writer.Write(0f); writer.Write((byte)1); writer.Write((byte)0); });
        sequence.Add(new { phase = "authenticated-legal-hit", packet = 28, target = slot, generation, damage = 20 });
        await Task.Delay(250);
        var legal = await State("target-after-legal-hit");
        Check(legal.GetProperty("target").GetProperty("life").GetInt32() == 2980, "real-legal-strike-before-disconnection-input");
        await control.Drain(TimeSpan.FromMilliseconds(100));
        Check(control.NpcStrikes.Any(s => s.Target == slot && s.Generation == generation && s.Damage == 20),
            "real-legal28-independent-peer-delivery-without27");
        if (withAntiCheat)
        {
            var completion = legal.GetProperty("clientStrike");
            Check(completion.ValueKind == JsonValueKind.Object && completion.GetProperty("LifeBefore").GetInt32() == 3000 &&
                completion.GetProperty("LifeAfter").GetInt32() == 2980 && completion.GetProperty("TargetGeneration").GetInt32() == generation &&
                completion.GetProperty("NativeStrikeEntryObserved").GetBoolean() && completion.GetProperty("RelayAttemptObserved").GetBoolean(),
                "m8-product-live-same-generation-client28-consumption-observed");
            Check(!completion.GetProperty("ClientAttackAuthorized").GetBoolean() && !completion.GetProperty("LootItemsVerified").GetBoolean(),
                "m8-consumption-and-peer-delivery-do-not-authorize-client28");
        }
        uint firstArrowKey = LabClient.ProjectileKey(subject.Slot, 930, 38);
        int beforeArrow = control.ProjectileClaims.Count;
        await subject.Send(27, w => subject.WriteProjectileDamage(w, firstArrowKey, 1, 1005));
        await control.WaitUntil(() => control.ProjectileClaims.Skip(beforeArrow).Any(p => p.Key == firstArrowKey && p.Damage == 1005),
            TimeSpan.FromSeconds(5));
        var arrow = await State("target-after-first1005");
        Check(!subject.Closed && subject.DisconnectReason is null, "first1005-incomplete-model-remains-unpunished");
        if (withAntiCheat)
            Check(arrow.GetProperty("arrowUnionEvaluations").GetInt64() > legal.GetProperty("arrowUnionEvaluations").GetInt64(),
                "m8-real-raw27-actually-computed-allowed-union");
        sequence.Add(new { phase = "first1005-still-uncovered", packet = 27, key = firstArrowKey, damage = 1005,
            legalityProven = false, actualPeerReceipt = true, productUnionEvaluations = arrow.GetProperty("arrowUnionEvaluations") });
        // Same packet shape as the prior NPC153 window; NPC slot/generation belongs to this isolated run.
        await subject.SendBatch(LabClient.Packet(153, writer => { writer.Write(slot); writer.Write((short)20); }),
            LabClient.Packet(154, _ => { }));
        sequence.Add(new { phase = "npc153-and-coalesced-followup", packet = 153, target = slot, damage = 20 });
        await subject.Drain(TimeSpan.FromMilliseconds(500));
        sequence.Add(new { phase = "subject-after-frame", subject.Closed, subject.DisconnectReason });
        if (withAntiCheat)
            Check(subject.DisconnectReason == "AntiCheat proven violation.", "plugin-first-proof-disconnect-observed");
        await subject.DisposeAsync();
        sequence.Add(new { phase = "subject-client-closed", elapsedMs = timer.Elapsed.TotalMilliseconds });
        await Task.Delay(250);
        var after = await State("target-after-disconnect");
        await control.Send(154, _ => { });
        await control.Drain(TimeSpan.FromMilliseconds(150));
        Check(!control.Closed && control.DisconnectReason is null, "innocent-control-survives-target-input");
        await control.DisposeAsync();
        sequence.Add(new { phase = "control-client-closed", elapsedMs = timer.Elapsed.TotalMilliseconds });
        await Task.Delay(300);
        await File.WriteAllTextAsync(Path.Combine(directory, "sequence.json"), JsonSerializer.Serialize(new
        {
            withAntiCheat, sameInputTemplate = true, replayOfExactHistoricalSocketScheduling = false,
            coreProtectionsRetained = true, maximumConcurrentSubjects = 2, distinctSubjectsIncludingCleanupActor = 3, sequence,
            before = before.GetProperty("target"), legal = legal.GetProperty("target"), after = after.GetProperty("target"),
            clientStrike = legal.GetProperty("clientStrike"),
            diagnosticsWrittenSoFar = Directory.GetFiles(host.ReportDirectory, "m6-first-*-error-*.json").Select(Path.GetFileName).ToArray(),
            note = "Normal runner shutdown follows; first-error files and stdout/stderr must also be inspected after exit. No recurrence is not a root-cause fix."
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task<LabClient> Actor(string name)
        {
            var actor = await host.Connect(name); await actor.Join(); await actor.RegisterAndLogin();
            Check(actor.Authenticated && actor.SscSlots.Count >= 350, name + "-authenticated-with-real-ssc");
            sequence.Add(new { phase = "authenticated", actor.Name, actor.Slot });
            return actor;
        }
        async Task<JsonElement> State(string label)
        {
            var requested = DateTimeOffset.UtcNow;
            await host.ConsoleCommand("qa_m5_state");
            string path = Path.Combine(host.ReportDirectory, "m5-state-latest.json");
            JsonElement value = default;
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        if (document.RootElement.GetProperty("utc").GetDateTimeOffset() >= requested)
                        { value = document.RootElement.Clone(); break; }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
                await Task.Delay(25);
            }
            if (value.ValueKind == JsonValueKind.Undefined) throw new TimeoutException("No fresh M6 runtime target state: " + label);
            await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), value.GetRawText());
            return value;
        }
        void Check(bool condition, string label) => host.Assert(condition, "m6-runtime:" + label);
    }
}
