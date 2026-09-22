using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Narrow TestLab replay for the M18 P1-A candidate. The server fixture creates
// a native SendPlayerHurt output, while the connected client supplies the later
// packet16 SSC state. No GUI, client injection, permanent sanction, or formal
// production qualification is inferred.
internal static class M18LockHealthScenario
{
    private const byte PlayerLifePacket = 16;
    private const byte PlayerHurtPacket = 117;
    private const string Rule = "G06.SustainedOneDamageFullLifeSync";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p1-a");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string status = "failed", failure = "";
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            await host.ConsoleCommand("qa_capture 16 117");
            await Until(() => host.ConsoleLines().Any(line =>
                line.Contains("QA raw packet capture: 16,117", StringComparison.Ordinal)),
                "lock-health-raw-capture-enabled");

            var control = await host.Connect("M18P1AControl");
            await control.Join(); await control.RegisterAndLogin();
            control.PauseHeartbeat = true;
            Check(control.Authenticated && control.SscSlots.Count >= 350,
                "ordinary-control-real-authentication-and-ssc");

            var subject = await host.Connect("M18P1ASubject");
            await subject.Join(); await subject.RegisterAndLogin();
            subject.PauseHeartbeat = true;
            Check(subject.Authenticated && subject.SscSlots.Count >= 350,
                "subject-real-authentication-and-ssc");
            await WaitForLoggedInSnapshot(subject.Name);

            // First provide a legal shape control. A normal two-point output
            // followed by a full-life packet must not enter the one-point queue.
            int normalHurtBefore = HurtFrameCount(subject);
            int normalLifeFrameBefore = CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot);
            int normalLogStart = host.ConsoleLines().Length;
            await host.ConsoleCommand("qa_m18_lock_health " + subject.Name + " normal");
            await Until(() => host.ConsoleLines().Skip(normalLogStart).Any(line =>
                line.Contains("QA_M18_LOCK_HEALTH_OUTPUT", StringComparison.Ordinal) &&
                line.Contains("mode=normal", StringComparison.Ordinal)), "normal-lock-health-output");
            await subject.WaitUntil(() => MatchingHurtFrameCount(subject, normalHurtBefore,
                subject.Slot, 2, false) > 0, TimeSpan.FromSeconds(5));
            byte[] normalLifeFrame = LifeFrame(subject.Slot, 100, 100);
            await subject.SendBatch(normalLifeFrame);
            await subject.PingAsync();
            await Until(() => CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot) > normalLifeFrameBefore,
                "normal-full-life-frame-captured");
            Check(MatchingHurtFrameCount(subject, normalHurtBefore, subject.Slot, 2, false) > 0,
                "normal-two-point-output-reaches-subject");
            Check(CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot) > normalLifeFrameBefore,
                "normal-full-life-packet-reaches-server");
            Check(!subject.Closed && subject.DisconnectReason is null,
                "normal-two-point-control-is-not-kicked");
            Check(!host.ConsoleLines().Skip(normalLogStart).Any(line =>
                line.Contains("rule=" + Rule + " ", StringComparison.Ordinal)),
                "normal-two-point-control-does-not-create-lock-health-rule");
            observations.Add(new
            {
                kind = "normal-shape-control",
                output = new { mode = "normal", damage = 2, target = subject.Slot },
                outputPacket = PlayerHurtPacket,
                lifePacket = PlayerLifePacket,
                lifeFrameHex = Convert.ToHexString(normalLifeFrame),
                hurtDeclarations = HurtFrames(subject).Skip(normalHurtBefore).Select(DescribeHurtFrame).ToArray(),
                rawFullLifeFrames = CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot) - normalLifeFrameBefore,
                candidateRuleObserved = false,
                sanction = "none"
            });

            // Now exercise exactly three one-point/full-life pairs. The first
            // two remain service-allowed; the third is cancelled and disconnects
            // only the current session. The fixture resets server life before
            // each native output and never writes the account database.
            var pairResults = new List<object>();
            int pairHurtBefore = HurtFrameCount(subject);
            int outputBefore = HurtFrameCount(subject);
            int rawLifeBefore = CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot);
            int pairLogStart = host.ConsoleLines().Length;
            for (int pair = 1; pair <= 3; pair++)
            {
                await host.ConsoleCommand("qa_m18_lock_health " + subject.Name + " one");
                await Until(() => host.ConsoleLines().Skip(pairLogStart).Count(line =>
                    line.Contains("QA_M18_LOCK_HEALTH_OUTPUT", StringComparison.Ordinal) &&
                    line.Contains("mode=one", StringComparison.Ordinal)) >= pair,
                    "one-point-output-" + pair);
                await subject.WaitUntil(() => MatchingHurtFrameCount(subject, pairHurtBefore,
                    subject.Slot, 1, false) >= pair,
                    TimeSpan.FromSeconds(5));
                byte[] lifeFrame = LifeFrame(subject.Slot, 100, 100);
                await subject.SendBatch(lifeFrame);
                if (pair < 3)
                {
                    await subject.PingAsync();
                    Check(!subject.Closed && subject.DisconnectReason is null,
                        "one-point-pair-" + pair + "-remains-service-allowed");
                }
                else
                {
                    await subject.WaitUntil(() => subject.Closed || subject.DisconnectReason is not null,
                        TimeSpan.FromSeconds(5));
                    Check(subject.DisconnectReason == "AntiCheat service rule: sustained one-point full-life sync.",
                        "third-one-point-pair-uses-service-kick-boundary");
                }
                await Until(() => CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot) >= rawLifeBefore + pair,
                    "one-point-full-life-frame-" + pair + "-captured");
                pairResults.Add(new
                {
                    pair,
                    outputPacket = PlayerHurtPacket,
                    outputDamage = 1,
                    fullLifePacket = PlayerLifePacket,
                    lifeFrameHex = Convert.ToHexString(lifeFrame),
                    subjectClosedAfterPair = subject.Closed,
                    subjectDisconnectReasonAfterPair = subject.DisconnectReason
                });
            }

            int rawAfter = HurtFrameCount(subject);
            int rawLifeAfter = CountRawFrames(host.ReportDirectory, PlayerLifePacket, subject.Slot);
            string? candidateLog = host.ConsoleLines().Skip(pairLogStart).FirstOrDefault(line =>
                line.Contains("rule=" + Rule + " ", StringComparison.Ordinal));
            Check(rawAfter - outputBefore == 3, "three-native-one-point-outputs-are-captured");
            Check(rawLifeAfter - rawLifeBefore == 3, "three-full-life-packets-are-captured");
            Check(candidateLog is not null && candidateLog.Contains("verdict=UnsafeInput", StringComparison.Ordinal) &&
                candidateLog.Contains("action=Block", StringComparison.Ordinal) &&
                candidateLog.Contains("canceled=True", StringComparison.Ordinal),
                "third-pair-logs-bounded-unsafe-input-and-cancel");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "service-kick-writes-no-permanent-ban");
            Check(!control.Closed && control.DisconnectReason is null &&
                Scalar(database, "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + control.Name) == 0,
                "independent-control-remains-healthy");
            observations.Add(new
            {
                kind = "sustained-one-point-full-life-service-kick",
                rule = Rule,
                outputPacket = PlayerHurtPacket,
                lifePacket = PlayerLifePacket,
                subjectSlot = subject.Slot,
                pairResults,
                rawNativeOutputFrames = rawAfter - outputBefore,
                rawFullLifeFrames = rawLifeAfter - rawLifeBefore,
                candidateLog,
                subjectDisconnectReason = subject.DisconnectReason,
                banRows = Scalar(database, "SELECT COUNT(*) FROM PlayerBans"),
                boundary = "TestLab service kick only; no permanent sanction predicate, GUI AntiHurt/QTR proof or production qualification",
                rawHurtDeclarations = HurtFrames(subject).Skip(pairHurtBefore).Select(DescribeHurtFrame).ToArray()
            });

            await WriteEvidence(report, run, observations, "passed");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await WriteEvidence(report, run, observations, "failed", failure);
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p1-a-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
            string[] lines = Directory.Exists(host.ReportDirectory)
                ? Directory.EnumerateFiles(host.ReportDirectory, "gameplay-events-*.jsonl")
                    .SelectMany(File.ReadLines)
                    .Where(line => line.Contains("raw-client-experiment-frame", StringComparison.Ordinal) ||
                        line.Contains("m18-lock-health-output", StringComparison.Ordinal) ||
                        line.Contains("player-slot-packet", StringComparison.Ordinal))
                    .Take(2048).ToArray()
                : [];
            await File.WriteAllLinesAsync(Path.Combine(report, "m18-p1-a-events.jsonl"), lines);
        }

        void Check(bool condition, string label) => host.Assert(condition, "m18-p1-a:" + label);

        async Task Until(Func<bool> predicate, string label)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (predicate()) return;
                await Task.Delay(100);
            }
            throw new TimeoutException(label);
        }

        async Task WaitForLoggedInSnapshot(string playerName)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var snapshot = await host.FixtureSnapshot();
                if (snapshot.GetProperty("players").EnumerateArray().Any(player =>
                    player.GetProperty("Name").GetString() == playerName &&
                    player.GetProperty("IsLoggedIn").GetBoolean()))
                    return;
                await Task.Delay(250);
            }
            throw new TimeoutException("subject-login-stability");
        }
    }

    private static byte[] LifeFrame(byte player, short current, short maximum)
        => LabClient.Packet(PlayerLifePacket, writer =>
        {
            writer.Write(player); writer.Write(current); writer.Write(maximum);
        });

    private static int CountRawFrames(string report, byte packetId, byte playerIndex)
    {
        int count = 0;
        foreach (string line in Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(ReadJournalLines))
        {
            if (!line.Contains("\"kind\":\"raw-client-experiment-frame\"", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var payload = document.RootElement.GetProperty("payload");
                if (payload.GetProperty("packetId").GetByte() == packetId &&
                    payload.GetProperty("playerIndex").GetByte() == playerIndex) count++;
            }
            catch (JsonException)
            {
                // The scaffold appends one JSON object at a time. A shared read
                // can observe the final line before its newline; retry on the
                // next polling pass instead of stopping the recording thread.
            }
        }
        return count;
    }

    private static string[] ReadJournalLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
            return lines.ToArray();
        }
        catch (IOException)
        {
            // The writer may briefly hold its exclusive append handle. The
            // scenario's Until loop will sample again without changing play.
            return [];
        }
    }

    private static IEnumerable<(byte Packet, int PayloadBytes, string PayloadHex)> HurtFrames(LabClient client)
        => client.ServerVitalFrames.Where(frame => frame.Packet == PlayerHurtPacket);

    private static int HurtFrameCount(LabClient client) => HurtFrames(client).Count();

    private static int MatchingHurtFrameCount(LabClient client, int priorFrameCount, int target,
        int damage, bool pvp)
        => HurtFrames(client).Skip(priorFrameCount).Count(frame =>
            TryDecodeHurtFrame(frame.PayloadHex, out int actualTarget, out int actualDamage, out bool actualPvp) &&
            actualTarget == target && actualDamage == damage && actualPvp == pvp);

    private static object DescribeHurtFrame((byte Packet, int PayloadBytes, string PayloadHex) frame)
    {
        bool parsed = TryDecodeHurtFrame(frame.PayloadHex, out int target, out int damage, out bool pvp);
        return new { target, damage, pvp, parsed, payloadBytes = frame.PayloadBytes, payloadHex = frame.PayloadHex };
    }

    private static bool TryDecodeHurtFrame(string frameHex, out int target, out int damage, out bool pvp)
    {
        target = 0; damage = 0; pvp = false;
        byte[] frame;
        try { frame = Convert.FromHexString(frameHex); }
        catch (FormatException) { return false; }
        if (frame.Length < 8 || frame[0] != PlayerHurtPacket) return false;
        target = frame[1];
        byte fields = frame[2];
        int cursor = 3;
        for (int bit = 0; bit < 7; bit++)
        {
            if ((fields & (1 << bit)) == 0) continue;
            cursor += bit is 3 or 6 ? 1 : 2;
            if (cursor > frame.Length - 5) return false;
        }
        if ((fields & 128) != 0)
        {
            int length = 0, shift = 0;
            bool terminated = false;
            for (int index = 0; index < 5 && cursor < frame.Length - 5; index++)
            {
                byte part = frame[cursor++];
                if (index == 4 && (part & 0xf8) != 0) return false;
                length |= (part & 127) << shift;
                if ((part & 128) == 0) { terminated = true; break; }
                shift += 7;
            }
            if (!terminated || length < 0 || length > frame.Length - cursor - 5)
                return false;
            cursor += length;
        }
        if (frame.Length - cursor != 5) return false;
        damage = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(cursor, 2));
        pvp = (frame[cursor + 3] & 2) != 0;
        return true;
    }

    private static long Scalar(string database, string sql, params string[] values)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$name", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$name", values.ElementAtOrDefault(0));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task WriteEvidence(string report, string run, IReadOnlyList<object> observations,
        string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p1-a-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / packet16 PlayerLife, packet117 PlayerHurtV2",
                source = "native server SendPlayerHurt output fixture -> real client packet16 -> M18LockHealthContext before core handler",
                observations,
                boundary = "One-point/full-life pairs are a bounded TestLab service-kick candidate; they do not prove AntiHurt/QTR GUI use, permanent cheating, or Production qualification",
                excluded = "server Player.Hurt alone as an output source, stock GUI, client injection, account ban, deployment and migration"
            }, Json));
}
