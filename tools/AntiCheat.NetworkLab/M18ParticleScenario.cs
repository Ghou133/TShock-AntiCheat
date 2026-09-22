using System.Text.Json;
using Microsoft.Data.Sqlite;

// Narrow live slice for the M18 sign-frame and NetParticles candidate. The
// client is synthetic loopback TCP, but the receiver, hook, module and
// observer are the isolated 1.4.5.8 server runtime. No public flood or GUI is
// implied by this scenario.
internal static class M18ParticleScenario
{
    private const byte SignPacket = 47;
    private const byte NetModulesPacket = 82;
    private const byte StormLightning = 61;
    private const int CandidateEventBudget = 128;
    private const string Rule = "F08.LightningParticleDensityBudget";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4ObservedReplayHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "m18-p0-c");
        Directory.CreateDirectory(report);
        string status = "failed", failure = "";
        var observations = new List<object>();
        try
        {
            Check(host.RuntimeVerified(), "actual-target-runtime-verified");
            Check(File.Exists(Path.Combine(run, ".anticheat-lab")) &&
                File.Exists(Path.Combine(run, ".compatibility-isolated-test")),
                "existing-isolated-networklab-and-qa-scaffold");
            string database = Path.Combine(run, "tshock", "tshock.sqlite");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");

            var observer = await host.Connect("M18P0CObserver");
            await observer.Join(); await observer.RegisterAndLogin();
            observer.PauseHeartbeat = true;
            Check(observer.Authenticated && observer.SscSlots.Count >= 350,
                "observer-real-authentication-and-ssc");

            var actor = await host.Connect("M18P0CActor");
            await actor.Join(); await actor.RegisterAndLogin();
            actor.PauseHeartbeat = true;
            Check(actor.Authenticated && actor.SscSlots.Count >= 350,
                "actor-real-authentication-and-ssc");

            // qa_mark turns on the existing bounded passive raw journal. It is
            // used only for packet47; packet82 credentials/text are not captured.
            await host.ConsoleCommand("qa_mark m18-p0-c-sign-and-lightning");
            await Until(() => host.ConsoleLines().Any(x => x.Contains("QA status:", StringComparison.Ordinal)),
                "qa-mark-enables-bounded-raw-observation");
            await actor.Drain(TimeSpan.FromMilliseconds(250));
            await observer.Drain(TimeSpan.FromMilliseconds(250));

            int signRawBefore = CountRawEvents(host.ReportDirectory, SignPacket);
            int signRelayBefore = observer.PacketCount(SignPacket);
            int signLogStart = host.ConsoleLines().Length;
            byte[] shortSign = ShortSignFrame();
            await actor.SendBatch(shortSign);
            await actor.PingAsync();
            await Task.Delay(250);
            await observer.Drain(TimeSpan.FromMilliseconds(150));
            await Until(() => CountRawEvents(host.ReportDirectory, SignPacket) > signRawBefore,
                "short-sign-frame-reaches-bounded-raw-observation");
            string[] signRaw = RawEvents(host.ReportDirectory, SignPacket).ToArray();
            string[] signConsole = host.ConsoleLines().Skip(signLogStart).ToArray();
            Check(signRaw.Any(x => x.Contains("\"handledObserved\":true", StringComparison.Ordinal)),
                "short-sign-frame-is-observed-handled-before-native-sign-path");
            Check(observer.PacketCount(SignPacket) == signRelayBefore,
                "short-sign-frame-has-no-sign-broadcast");
            Check(Healthy(actor, database), "short-sign-frame-keeps-actor-healthy");
            observations.Add(new
            {
                kind = "malformed-short-sign-frame",
                packet = SignPacket,
                frameHex = Convert.ToHexString(shortSign),
                claimedStringLength = 60000,
                rawEvents = signRaw.TakeLast(4).ToArray(),
                console = signConsole.Where(x => x.Contains("ANTICHEAT", StringComparison.Ordinal)).Take(8).ToArray(),
                nativeBroadcastDelta = observer.PacketCount(SignPacket) - signRelayBefore,
                sanction = "none; malformed input is UnsafeInput/BlockOnly"
            });

            // Probe the exact receiver before sending a larger batch. A missing native module
            // is a server-role/runtime boundary, not a reason to register one or to call a
            // client frame malicious. Keep this diagnostic to one complete frame.
            int particleBeforeDiagnostic = observer.ParticleModules.Count(x => x.Type == StormLightning);
            int modulePacketBeforeDiagnostic = observer.PacketCount(NetModulesPacket);
            int particleDiagnosticLogStart = host.ConsoleLines().Length;
            byte[] diagnosticFrame = StormFrame(actor.Slot, 0x10203040, moduleId: 0);
            await actor.SendBatch(diagnosticFrame);
            await actor.PingAsync();
            await Until(() => host.ConsoleLines().Skip(particleDiagnosticLogStart)
                .Any(x => x.Contains("ANTICHEAT_M18_PARTICLE_RUNTIME packet=82", StringComparison.Ordinal)),
                "packet82-runtime-diagnostic");
            string[] particleDiagnosticConsole = host.ConsoleLines().Skip(particleDiagnosticLogStart).ToArray();
            string? particleDiagnostic = particleDiagnosticConsole.FirstOrDefault(x =>
                x.Contains("ANTICHEAT_M18_PARTICLE_RUNTIME packet=82", StringComparison.Ordinal));
            Check(particleDiagnostic is not null, "packet82-runtime-diagnostic-is-written");
            observations.Add(new
            {
                kind = "particle-runtime-entry-diagnostic",
                frameHex = Convert.ToHexString(diagnosticFrame),
                console = particleDiagnostic,
                nativeModuleBroadcastDelta = observer.PacketCount(NetModulesPacket) - modulePacketBeforeDiagnostic,
                exactStormLightningBroadcastDelta = observer.ParticleModules.Count(x => x.Type == StormLightning) - particleBeforeDiagnostic
            });

            if (particleDiagnostic!.Contains("readKind=UnknownRuntime", StringComparison.Ordinal) ||
                particleDiagnostic.Contains("moduleRegistered=False", StringComparison.Ordinal) ||
                particleDiagnostic.Contains("modulePresent=False", StringComparison.Ordinal))
            {
                Check(Healthy(actor, database), "unregistered-particle-route-keeps-actor-healthy");
                Check(observer.ParticleModules.Count(x => x.Type == StormLightning) == particleBeforeDiagnostic,
                    "unregistered-particle-route-has-no-native-broadcast");
                observations.Add(new
                {
                    kind = "particle-server-receiver-not-available",
                    candidateDisposition = "unqualified-no-live-server-receiver",
                    f08LiveResult = "not-reached",
                    sanction = "none; no module registration was added"
                });
                await WriteEvidence(report, run, observations, "partial",
                    "Live 1.4.5.8 TestLab packet82 was classified UnknownRuntime or lacked a registered NetParticlesModule; F08 live stop-loss was not exercised.");
                status = "partial";
                return;
            }

            ushort particleModuleId = RuntimeModuleId(particleDiagnostic);
            Check(particleModuleId != 0, "packet82-runtime-module-id-is-nonzero");
            int particleBefore = observer.ParticleModules.Count(x =>
                x.Module == particleModuleId && x.Type == StormLightning);
            int modulePacketBefore = observer.PacketCount(NetModulesPacket);

            // Only if the locked server proves that the module is registered do the next 128
            // complete frames test the finite TestLab queue stop. The 129th is never a sanction.
            byte[][] admitted = Enumerable.Range(0, CandidateEventBudget)
                .Select(i => StormFrame(actor.Slot, 0x10203040 + i, particleModuleId)).ToArray();
            await actor.SendBatch(admitted);
            await actor.PingAsync();
            await observer.WaitUntil(() => observer.ParticleModules.Count(x =>
                x.Module == particleModuleId && x.Type == StormLightning) >= particleBefore + CandidateEventBudget,
                TimeSpan.FromSeconds(8));
            int particleAtBudget = observer.ParticleModules.Count(x =>
                x.Module == particleModuleId && x.Type == StormLightning);
            Check(particleAtBudget == particleBefore + CandidateEventBudget,
                "128-complete-stormlightning-frames-reach-native-module-broadcast");
            Check(observer.PacketCount(NetModulesPacket) >= modulePacketBefore + CandidateEventBudget,
                "admitted-stormlightning-frames-use-real-packet82-route");

            int blockedLogStart = host.ConsoleLines().Length;
            byte[] blockedFrame = StormFrame(actor.Slot, 0x10203040 + CandidateEventBudget, particleModuleId);
            await actor.SendBatch(blockedFrame);
            await actor.PingAsync();
            await Task.Delay(300);
            await observer.Drain(TimeSpan.FromMilliseconds(150));
            string[] blockedConsole = host.ConsoleLines().Skip(blockedLogStart).ToArray();
            string? resourceLine = blockedConsole.FirstOrDefault(x =>
                x.Contains("rule=" + Rule, StringComparison.Ordinal) &&
                x.Contains("verdict=ResourceAbuse", StringComparison.Ordinal) &&
                x.Contains("reason=particle-window-", StringComparison.Ordinal) &&
                x.Contains("packet=82", StringComparison.Ordinal) &&
                x.Contains("action=Block", StringComparison.Ordinal) &&
                x.Contains("canceled=True", StringComparison.OrdinalIgnoreCase));
            int particleAfter = observer.ParticleModules.Count(x =>
                x.Module == particleModuleId && x.Type == StormLightning);
            Check(resourceLine is not null, "129th-stormlightning-frame-writes-f08-stop-before-native");
            Check(particleAfter == particleAtBudget,
                "blocked-stormlightning-frame-has-no-native-module-broadcast");
            Check(Healthy(actor, database), "particle-stop-loss-keeps-actor-healthy");
            Check(Scalar(database, "SELECT COUNT(*) FROM PlayerBans") == 0,
                "particle-density-stop-loss-does-not-ban");
            observations.Add(new
            {
                kind = "stormlightning-density-stop-loss",
                packet = NetModulesPacket,
                module = particleModuleId,
                particleType = StormLightning,
                candidateEventBudget = CandidateEventBudget,
                admittedFrames = particleAtBudget - particleBefore,
                blockedFrameHex = Convert.ToHexString(blockedFrame),
                nativeModuleBroadcastDelta = particleAfter - particleBefore,
                resourceReason = resourceLine,
                console = blockedConsole.Where(x => x.Contains("rule=" + Rule, StringComparison.Ordinal)).Take(4).ToArray(),
                sanction = "none; ResourceAbuse is a bounded stop-loss, not account-cheat proof"
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
            await CopyRelevantEvents(host.ReportDirectory, report);
            await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-c-run-summary.json"),
                JsonSerializer.Serialize(new { status, failure, observations }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "m18-p0-c:" + name);
    }

    private static byte[] ShortSignFrame()
        => LabClient.Packet(SignPacket, writer =>
        {
            writer.Write((short)3); writer.Write((short)10); writer.Write((short)20);
            writer.Write(new byte[] { 0xE0, 0xD4, 0x03, 7 });
        });

    private static byte[] StormFrame(byte invokingPlayer, int uniqueInfoPiece, ushort moduleId)
        => LabClient.Packet(NetModulesPacket, writer =>
        {
            writer.Write(moduleId); writer.Write(StormLightning);
            writer.Write(320.5f); writer.Write(640.25f);
            writer.Write(14.5f); writer.Write(-3.25f);
            writer.Write(uniqueInfoPiece); writer.Write(invokingPlayer);
        });

    private static ushort RuntimeModuleId(string diagnostic)
    {
        const string marker = "moduleId=";
        int start = diagnostic.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("packet82 runtime diagnostic omitted moduleId");
        start += marker.Length;
        int end = diagnostic.IndexOf(' ', start);
        string token = (end < 0 ? diagnostic[start..] : diagnostic[start..end]).Trim();
        if (!ushort.TryParse(token, out ushort id))
            throw new InvalidDataException("packet82 runtime diagnostic moduleId was not numeric");
        return id;
    }

    private static bool Healthy(LabClient actor, string database)
        => actor.Authenticated && !actor.Closed && actor.DisconnectReason is null &&
            Scalar(database, "SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + actor.Name) == 0;

    private static string[] RawEvents(string report, byte packet)
        => Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("\"kind\":\"raw-client-post-anticheat-frame\"", StringComparison.Ordinal) &&
                line.Contains("\"packetId\":" + packet, StringComparison.Ordinal))
            .ToArray();

    private static int CountRawEvents(string report, byte packet) => RawEvents(report, packet).Length;

    private static async Task WriteEvidence(string report, string run, IReadOnlyList<object> observations,
        string status, string failure = "")
        => await File.WriteAllTextAsync(Path.Combine(report, "m18-p0-c-evidence.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, runDirectory = run,
                protocol = "Terraria 1.4.5.8 / protocol 326 / packet47 SignNew and packet82 NetModules",
                signSource = "TerraAngel MsgUpdateSign.cs -> short 7-bit length frame",
                particleSource = "TerraAngel lightning route -> NetParticlesModule with runtime-resolved module id, StormLightning(61), 24-byte body",
                observations,
                maintenanceBoundary = "NPC message153 and chest-size message155 remain existing contracts; no Buff153 conflation",
                sanctionBoundary = "F08 resource stop-loss and malformed-input block only; no permanent-account proof or formal qualification"
            }, Json));

    private static async Task CopyRelevantEvents(string report, string destination)
    {
        string[] lines = Directory.EnumerateFiles(report, "gameplay-events-*.jsonl")
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("\"kind\":\"raw-client-post-anticheat-frame\"", StringComparison.Ordinal) &&
                line.Contains("\"packetId\":47", StringComparison.Ordinal))
            .Take(256)
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(destination, "qa-sign-events.jsonl"), lines);
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

    private static async Task Until(Func<bool> predicate, string label)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }
}
