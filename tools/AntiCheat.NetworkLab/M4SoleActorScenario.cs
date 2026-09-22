using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Reuses the existing NetworkLab lifecycle and client. No observer connection is created.
internal sealed record M4SoleActorHarness(
    string RunDirectory,
    string ReportDirectory,
    Process Server,
    int LoopbackPort,
    Func<bool, Task> StartServer,
    Func<Task> StopServer,
    Func<string, Task<LabClient>> Connect,
    Func<Func<string, bool>, bool> AnyConsoleLine,
    Func<bool> RuntimeVerified,
    Func<int> RegisteredClientCount,
    Action<bool, string> Assert);

internal static class M4SoleActorScenario
{
    private sealed record IntentSnapshot(string Path, Guid IncidentId, long AccountId, bool Applied,
        string RuleId, string Sha256);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4SoleActorHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        if (!File.Exists(Path.Combine(run, ".anticheat-lab")) ||
            !string.Equals(Path.GetFullPath(host.Server.StartInfo.FileName),
                Path.Combine(run, "app", "TShock.Server.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sole-actor regression requires this run's owned isolated TShock child.");
        if (host.LoopbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(host.LoopbackPort));
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "sole-actor");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string journal = Path.Combine(run, "tshock", "anticheat", "enforcement");
        string marker = Path.Combine(journal, "run-safety.state");
        var timer = Stopwatch.StartNew();
        var emptySamples = new List<object>(400);
        string status = "failed", failure = "", stage = "initial";
        long accountId = 0, emptyStartedMs = 0;
        bool cleanBeforeRestart = false, restarted = false;
        int initialPid = host.Server.Id;
        LabClient? actor = null;
        string pluginSha256 = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(run, "app", "ServerPlugins", "AntiCheat.Plugin.TShock.dll"))));
        try
        {
            Check(host.RuntimeVerified(), "initial-target-runtime-verified");
            Check(host.RegisteredClientCount() == 0 && EstablishedConnections() == 0, "no-preexisting-lab-client-or-live-connection");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 0 && ReadIntents().Count == 0, "fresh-ban-table-and-intent-directory");
            using (var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "tshock", "anticheat.json"))))
                Check(configuration.RootElement.GetProperty("ExecutionScope").GetString() == "TestLab", "actual-enforcing-testlab-config");

            // The sole actor supplies the legal counterexample first; no second client keeps GameUpdate alive.
            actor = await host.Connect("M4SoleActor");
            await actor.Join();
            await actor.RegisterAndLogin();
            accountId = Scalar("SELECT ID FROM Users WHERE Username=$value", actor.Name);
            await actor.Send(120, writer => { writer.Write(actor.Slot); writer.Write((byte)2); });
            await actor.WaitUntil(() => actor.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 2), TimeSpan.FromSeconds(5));
            Check(actor.Authenticated && actor.SscSlots.Count >= 350 && !actor.Closed && actor.DisconnectReason is null,
                "sole-normal-account-authenticated-ssc-and-legal-action");
            Check(host.RegisteredClientCount() == 1 && EstablishedConnections() == 1,
                "exactly-one-client-and-server-side-established-connection-before-proof");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 0, "normal-actions-have-no-ban");
            await Snapshot("before-proof");

            // Exactly one versioned first-proof packet; no appended packet, observer, or console wake-up.
            stage = "single-proof";
            await actor.SendBatch(LabClient.Packet(153, writer => { writer.Write((byte)0); writer.Write((short)1); }));
            await actor.WaitUntil(() => actor.Closed || actor.DisconnectReason is not null, TimeSpan.FromSeconds(8));
            await actor.DisposeAsync(); // Stops the client's heartbeat and releases its only TCP connection.
            Check(actor.ProofBatches == 1 && actor.ProofBatchHex == "060099000100", "exactly-one-six-byte-packet153-first-proof");
            await Until(() => EstablishedConnections() == 0, TimeSpan.FromSeconds(5), "sole-connection-closed");
            Check(host.AnyConsoleLine(line => line.Contains("ANTICHEAT_INCIDENT ") &&
                line.Contains("accountId=" + accountId + " ") && line.Contains("rule=NPC01.ServerDebuffDamage ") &&
                line.Contains("canceled=true", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("revoked=true", StringComparison.OrdinalIgnoreCase)),
                "real-hook-canceled-and-revoked-the-only-actor");
            emptyStartedMs = timer.ElapsedMilliseconds;
            await Snapshot("empty-after-disconnect");

            stage = "empty-server-persistence";
            var persistenceWait = Stopwatch.StartNew();
            while (true)
            {
                SampleEmptyServer();
                bool ban = Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value AND Expiration=3155378975999999999",
                    "acc:" + actor.Name) == 1;
                bool applied = ReadIntents().Any(x => x.AccountId == accountId && x.Applied && x.RuleId == "NPC01.ServerDebuffDamage");
                if (ban && applied) break;
                if (persistenceWait.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Sole disconnected actor did not acquire a real permanent SQLite ban and Applied intent while the server stayed empty.");
                await Task.Delay(50);
            }
            Check(true, "sqlite-permanent-ban-and-applied-intent-completed-with-no-observer");
            await Snapshot("empty-ban-and-intent-applied");

            // Remain empty for a further two seconds after persistence, sampling real TCP state throughout.
            stage = "empty-server-two-second-hold";
            var hold = Stopwatch.StartNew();
            while (hold.Elapsed < TimeSpan.FromSeconds(2))
            {
                SampleEmptyServer();
                await Task.Delay(50);
            }
            Check(host.RegisteredClientCount() == 1 && EstablishedConnections() == 0,
                "two-more-empty-seconds-without-any-observer-or-reconnect");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 1 && ReadIntents().Count == 1,
                "one-permanent-account-record-and-one-intent-after-empty-hold");
            await Snapshot("empty-before-clean-shutdown");

            stage = "clean-shutdown";
            await host.StopServer();
            using (var guard = JsonDocument.Parse(await File.ReadAllTextAsync(marker)))
                cleanBeforeRestart = guard.RootElement.GetProperty("State").GetProperty("Clean").GetBoolean();
            Check(host.Server.HasExited && host.Server.ExitCode == 0 && cleanBeforeRestart,
                "empty-server-normal-exit-zero-and-clean-run-safety-marker");
            await Snapshot("after-clean-shutdown");
            host.Server.Close();
            await host.StartServer(true);
            restarted = true;
            Check(host.RuntimeVerified(), "same-database-restart-target-runtime-verified");

            stage = "restart-account-admission";
            var rejected = await host.Connect(actor.Name);
            await rejected.Join();
            await rejected.LoginAndExpectRejected();
            await Until(() => rejected.Closed, TimeSpan.FromSeconds(5), "banned-reconnect-socket-closed");
            // TShock emits the successful credential-authentication text before its account-ban
            // check. LabClient.Authenticated remembers that text, not permission to keep playing.
            Check(rejected.LoginAttempts == 1 && rejected.IsBanRejection && rejected.Closed,
                "same-permanently-banned-account-explicitly-rejected-after-restart");
            await rejected.DisposeAsync();
            await Until(() => EstablishedConnections() == 0, TimeSpan.FromSeconds(5), "rejected-reconnect-closed-before-new-actor");
            var innocent = await host.Connect("M4SoleInnocent");
            await innocent.Join();
            await innocent.RegisterAndLogin();
            await innocent.Send(120, writer => { writer.Write(innocent.Slot); writer.Write((byte)2); });
            await innocent.WaitUntil(() => innocent.Bubbles.Any(x => x.Player == innocent.Slot && x.Emote == 2), TimeSpan.FromSeconds(5));
            Check(innocent.Authenticated && innocent.SscSlots.Count >= 350 && !innocent.Closed && innocent.DisconnectReason is null,
                "new-innocent-account-login-ssc-and-action-after-restart");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + innocent.Name) == 0 &&
                Scalar("SELECT COUNT(*) FROM PlayerBans") == 1 && ReadIntents().Count == 1,
                "restart-no-duplicate-ban-no-innocent-ban-and-no-shared-loopback-ban");
            await innocent.DisposeAsync();
            await Snapshot("after-restart-admission");
            stage = "completed";
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            await Snapshot("failure-before-caller-shutdown");
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(report, "sole-actor-summary.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, stage, scenario = "one-client-first-packet153-empty-server-durable-ban-clean-restart",
                initialPid, pluginSha256, accountId, emptyStartedMs, emptySamples,
                clientsCreated = host.RegisteredClientCount(), firstProofBatches = actor?.ProofBatches,
                firstProofBatchHex = actor?.ProofBatchHex, cleanBeforeRestart, restarted,
                scenarioCreatesObserver = false, connectionEvidence = "OS TCP Established table sampled for the dedicated loopback server port; each empty sample requires the client registry to remain one",
                elapsedMs = timer.ElapsedMilliseconds
            }, Json));
        }

        void Check(bool value, string name) => host.Assert(value, "sole-actor:" + name);
        int EstablishedConnections() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Count(connection =>
            connection.LocalEndPoint.Port == host.LoopbackPort && IPAddress.IsLoopback(connection.LocalEndPoint.Address) &&
            connection.State == TcpState.Established);
        void SampleEmptyServer()
        {
            int connections = EstablishedConnections(), clients = host.RegisteredClientCount();
            if (emptySamples.Count < 400) emptySamples.Add(new { elapsedMs = timer.ElapsedMilliseconds, establishedConnections = connections, registeredClients = clients });
            if (connections != 0 || clients != 1 || host.Server.HasExited)
                throw new InvalidOperationException("Empty-server enforcement regression acquired another live connection/client or lost its server process.");
        }
        long Scalar(string sql, string? value = null)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; command.CommandTimeout = 1;
            if (value is not null) command.Parameters.AddWithValue("$value", value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        List<IntentSnapshot> ReadIntents()
        {
            var result = new List<IntentSnapshot>();
            foreach (string path in Directory.EnumerateFiles(journal, "*.json").Take(3))
            {
                if (result.Count == 2) throw new InvalidDataException("Unexpected sole-actor intent count.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 16384) throw new InvalidDataException("Oversized sole-actor intent.");
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                using var json = JsonDocument.Parse(bytes.ToArray());
                var intent = json.RootElement.GetProperty("Intent");
                result.Add(new(path, intent.GetProperty("IncidentId").GetGuid(), intent.GetProperty("AccountId").GetInt64(),
                    json.RootElement.GetProperty("Applied").GetBoolean(), intent.GetProperty("Evidence").GetProperty("RuleId").GetString()!,
                    Convert.ToHexString(SHA256.HashData(bytes.ToArray()))));
            }
            return result;
        }
        async Task Snapshot(string name)
        {
            string target = Path.Combine(report, name); Directory.CreateDirectory(target);
            var intents = ReadIntents();
            foreach (var intent in intents) File.Copy(intent.Path, Path.Combine(target, Path.GetFileName(intent.Path)), true);
            if (File.Exists(marker)) File.Copy(marker, Path.Combine(target, "run-safety.state"), true);
            await File.WriteAllTextAsync(Path.Combine(target, "state.json"), JsonSerializer.Serialize(new
            {
                name, elapsedMs = timer.ElapsedMilliseconds, accountId, intents,
                bans = Scalar("SELECT COUNT(*) FROM PlayerBans"),
                permanentBans = Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Expiration=3155378975999999999"),
                registeredClients = host.RegisteredClientCount(), establishedConnections = EstablishedConnections()
            }, Json));
        }
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout, string checkpoint)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException("M4 sole-actor checkpoint: " + checkpoint);
            await Task.Delay(25);
        }
    }
}
