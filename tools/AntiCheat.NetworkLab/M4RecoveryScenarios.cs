using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// This is a scenario in the existing NetworkLab, not a new ban store or client.
// The caller owns server startup/capture/final shutdown and registers every LabClient.
internal sealed record M4RecoveryHarness(
    string RunDirectory,
    string ReportDirectory,
    Process Server,
    Func<bool, Task> StartServer,
    Func<string, Task<LabClient>> Connect,
    Func<Task> FinishCapture,
    Func<Func<string, bool>, bool> AnyConsoleLine,
    Func<bool> RuntimeVerified,
    Action<bool, string> Assert,
    Action<int> RecordForcedExit);

internal static class M4RecoveryScenarios
{
    private sealed record IntentSnapshot(string Path, Guid IncidentId, long AccountId, bool Applied,
        string RuleId, string Sha256);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task RunAsync(M4RecoveryHarness host)
    {
        string run = Path.GetFullPath(host.RunDirectory);
        if (!File.Exists(Path.Combine(run, ".anticheat-lab")))
            throw new InvalidOperationException("M4 crash recovery requires an owned isolated NetworkLab run.");
        if (Path.GetFullPath(host.Server.StartInfo.FileName) != Path.Combine(run, "app", "TShock.Server.exe"))
            throw new InvalidOperationException("M4 crash recovery may kill only this run's TShock child process.");
        string report = Path.Combine(Path.GetFullPath(host.ReportDirectory), "recovery");
        Directory.CreateDirectory(report);
        string database = Path.Combine(run, "tshock", "tshock.sqlite");
        string journal = Path.Combine(run, "tshock", "anticheat", "enforcement");
        string marker = Path.Combine(journal, "run-safety.state");
        var timer = Stopwatch.StartNew();
        var stages = new List<object>(16);
        int? killedPid = null, forcedExitCode = null;
        int? sqliteBusyCode = null;
        long appliedAccount = 0, pendingAccount = 0;
        string status = "failed", failure = "";
        IntentSnapshot? durablePending = null;
        Guid? initialRunId = null;
        int inventoryBefore = 0;
        LabClient? control = null;
        try
        {
            Check(host.RuntimeVerified(), "initial-target-runtime-verified");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 0, "fresh-isolated-ban-table");
            Check(ReadIntents().Count == 0, "fresh-isolated-intent-directory");
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "tshock", "anticheat.json"))))
                Check(config.RootElement.GetProperty("ExecutionScope").GetString() == "TestLab", "actual-enforcing-testlab-config");

            // Normal counterexample first. No fault/lock is active during its real login and SSC flow.
            control = await host.Connect("M4RecoveryControl");
            await control.Join();
            await control.RegisterAndLogin();
            await control.Send(120, writer => { writer.Write(control.Slot); writer.Write((byte)2); });
            await control.WaitUntil(() => control.Bubbles.Any(x => x.Player == control.Slot && x.Emote == 2), TimeSpan.FromSeconds(5));
            Check(control.Authenticated && control.SscSlots.Count >= 350 && !control.Closed, "normal-authenticated-ssc-and-action-before-fault");

            var applied = await host.Connect("M4RecoveryApplied");
            await applied.Join();
            await applied.RegisterAndLogin();
            appliedAccount = AccountId(applied.Name);
            await SendFirstProof(applied);
            await applied.WaitUntil(() => applied.Closed || applied.DisconnectReason is not null, TimeSpan.FromSeconds(8));
            await Until(() => Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value AND Expiration=3155378975999999999", "acc:" + applied.Name) == 1,
                TimeSpan.FromSeconds(8), "first-account-permanent-ban");
            await Until(() => ReadIntents().Any(x => x.AccountId == appliedAccount && x.Applied), TimeSpan.FromSeconds(5), "first-intent-acknowledged");
            Check(applied.ProofBatches == 1, "existing-permanent-ban-came-from-one-proven-attempt");
            await applied.DisposeAsync();
            // Match the existing NetworkLab's bounded post-disconnect cleanup interval.
            await Task.Delay(300);
            await Snapshot("before-sqlite-lock");

            var pending = await host.Connect("M4RecoveryPending");
            await pending.Join();
            await pending.RegisterAndLogin();
            pendingAccount = AccountId(pending.Name);
            inventoryBefore = control.InventoryUpdates.Count;

            // A real SQLite reserved writer lock permits reads but excludes another writer.
            // No trigger, replacement DB, fake adapter, or TShock protection change is used.
            await using (var blocker = OpenDatabase(SqliteOpenMode.ReadWrite))
            {
                using (var begin = blocker.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE";
                    begin.CommandTimeout = 1;
                    begin.ExecuteNonQuery();
                }
                try
                {
                    using (var probe = OpenDatabase(SqliteOpenMode.ReadWrite))
                    using (var update = probe.CreateCommand())
                    {
                        update.CommandText = "UPDATE PlayerBans SET Expiration=Expiration WHERE 1=0";
                        update.CommandTimeout = 1;
                        try { update.ExecuteNonQuery(); }
                        catch (SqliteException error) when (error.SqliteErrorCode == 5) { sqliteBusyCode = error.SqliteErrorCode; }
                    }
                    Check(sqliteBusyCode == 5, "real-sqlite-writer-lock-observed-as-busy");
                    stages.Add(new { stage = "sqlite-writer-lock-active", elapsedMs = timer.ElapsedMilliseconds, sqliteBusyCode });
                    await SendFirstProof(pending);
                    await Until(() => ReadIntents().Any(x => x.AccountId == pendingAccount && !x.Applied),
                        TimeSpan.FromSeconds(8), "pending-intent-durable-before-kill");
                    durablePending = ReadIntents().Single(x => x.AccountId == pendingAccount);
                    Check(durablePending.RuleId == "NPC01.ServerDebuffDamage" && pending.ProofBatches == 1,
                        "locked-database-subject-has-one-versioned-first-proof");
                    Check(host.AnyConsoleLine(line => line.Contains("ANTICHEAT_INCIDENT") &&
                        line.Contains("accountId=" + pendingAccount + " ") &&
                        line.Contains("rule=NPC01.ServerDebuffDamage ") &&
                        line.Contains("canceled=true", StringComparison.OrdinalIgnoreCase) &&
                        line.Contains("revoked=true", StringComparison.OrdinalIgnoreCase)),
                        "real-hook-incident-attributed-canceled-and-revoked-before-kill");
                    Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:" + pending.Name) == 0,
                        "locked-database-has-not-committed-pending-ban");
                    await Snapshot("durable-pending-before-kill");
                    using (var guard = JsonDocument.Parse(await File.ReadAllTextAsync(marker)))
                    {
                        var state = guard.RootElement.GetProperty("State");
                        initialRunId = state.GetProperty("ServerRunId").GetGuid();
                        Check(!state.GetProperty("Clean").GetBoolean() && state.GetProperty("CanProduceProofs").GetBoolean(),
                            "enforcing-run-marker-armed-before-actual-kill");
                    }

                    // Kill only the exact process handle started by the current run. There is no
                    // exit/exit-nosave/Dispose/CompleteShutdown call before this abrupt boundary.
                    killedPid = host.Server.Id;
                    Check(!host.Server.HasExited, "owned-server-alive-at-kill-checkpoint");
                    host.Server.Kill(entireProcessTree: true);
                    await host.Server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    forcedExitCode = host.Server.ExitCode;
                    host.RecordForcedExit(forcedExitCode.Value);
                    await host.FinishCapture().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(host.Server.HasExited && forcedExitCode != 0, "actual-owned-process-force-kill-completed");
                    stages.Add(new { stage = "process-killed", pid = killedPid, exitCode = forcedExitCode,
                        elapsedMs = timer.ElapsedMilliseconds, gracefulShutdownRequested = false });
                }
                finally
                {
                    using var rollback = blocker.CreateCommand();
                    rollback.CommandText = "ROLLBACK";
                    rollback.CommandTimeout = 1;
                    rollback.ExecuteNonQuery();
                }
            }

            await control.Drain(TimeSpan.FromMilliseconds(200));
            Check(control.InventoryUpdates.Skip(inventoryBefore).All(x => x.Player != pending.Slot || x.Slot != 10 || x.Item != 9),
                "coalesced-post-proof-inventory-not-broadcast-before-kill");
            Check(CharacterInventory(pendingAccount).Split('~')[10].StartsWith("0,", StringComparison.Ordinal),
                "coalesced-post-proof-inventory-not-in-real-ssc-database");
            await control.DisposeAsync();
            await pending.DisposeAsync();
            await Snapshot("after-kill-lock-released");
            Check(IntegrityCheck() == "ok", "real-sqlite-integrity-after-process-kill");
            Check(ReadIntents().Single(x => x.AccountId == pendingAccount).Sha256 == durablePending.Sha256,
                "pending-intent-bytes-survive-force-kill");

            host.Server.Close();
            await host.StartServer(true);
            Check(host.RuntimeVerified(), "restarted-target-runtime-verified");
            // The prior armed run cannot prove that all in-memory decisions were persisted.
            // Retaining maintenance is the existing recovery contract, not a failed ban or a
            // permission to erase the marker. This harness never calls ConfirmRecoveryAsync.
            await Task.Delay(1300);
            using (var guard = JsonDocument.Parse(await File.ReadAllTextAsync(marker)))
            {
                var state = guard.RootElement.GetProperty("State");
                Check(!state.GetProperty("Clean").GetBoolean() && state.GetProperty("ServerRunId").GetGuid() == initialRunId,
                    "restart-retains-original-unclean-enforcement-marker");
            }
            Check(ReadIntents().Single(x => x.AccountId == pendingAccount).Sha256 == durablePending.Sha256,
                "maintenance-restart-does-not-discard-or-rewrite-pending-evidence");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value AND Expiration=3155378975999999999", "acc:M4RecoveryApplied") == 1,
                "prior-permanent-ban-survives-kill-and-restart-exactly-once");
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$value", "acc:M4RecoveryPending") == 0,
                "unverified-crash-window-not-silently-marked-fully-recovered");

            foreach (string name in new[] { "M4RecoveryApplied", "M4RecoveryPending", "M4RecoveryNew" })
            {
                var rejected = await host.Connect(name);
                string? joinError = null;
                try { await rejected.Join(); }
                catch (IOException error) { joinError = error.Message; }
                // A mere timeout does not prove a safe refusal and is intentionally not accepted.
                Check(!rejected.Authenticated && rejected.Slot == 255 && (rejected.Closed || rejected.DisconnectReason is not null),
                    "maintenance-explicitly-refuses-admission:" + name);
                stages.Add(new { stage = "maintenance-admission", name, rejected.Slot, rejected.Closed,
                    rejected.DisconnectReason, joinError });
                await rejected.DisposeAsync();
            }
            Check(Scalar("SELECT COUNT(*) FROM PlayerBans") == 1, "no-duplicate-ban-and-no-innocent-or-source-ban");
            Check(Scalar("SELECT COUNT(*) FROM Users WHERE Username=$value", "M4RecoveryNew") == 0,
                "maintenance-did-not-register-a-new-account");
            await Snapshot("after-maintenance-restart");
            status = "passed";
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(report, "recovery-summary.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, failure, actualProcessKill = forcedExitCode is not null,
                killedPid, forcedExitCode, sqliteBusyCode, appliedAccount, pendingAccount, initialRunId,
                durablePending, stages, elapsedMs = timer.ElapsedMilliseconds,
                scenario = "real-tshock-sqlite-lock-durable-intent-process-kill-restricted-restart",
                automaticReturnToNormalEnforcement = false, confirmsPowerLossDurability = false,
                firewallOrIpcExercised = false, noRunSafetyReset = true
            }, Json));
        }

        void Check(bool condition, string name) => host.Assert(condition, "recovery:" + name);
        SqliteConnection OpenDatabase(SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database, Mode = mode, Pooling = false, DefaultTimeout = 1
            }.ToString());
            connection.Open();
            return connection;
        }
        long Scalar(string sql, string? value = null)
        {
            using var connection = OpenDatabase(SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 1;
            if (value is not null) command.Parameters.AddWithValue("$value", value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        long AccountId(string name) => Scalar("SELECT ID FROM Users WHERE Username=$value", name);
        string CharacterInventory(long account)
        {
            using var connection = OpenDatabase(SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Inventory FROM tsCharacter WHERE Account=$account";
            command.CommandTimeout = 1;
            command.Parameters.AddWithValue("$account", account);
            return (string?)command.ExecuteScalar() ?? throw new InvalidDataException("Missing SSC record.");
        }
        string IntegrityCheck()
        {
            using var connection = OpenDatabase(SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            command.CommandTimeout = 1;
            return (string?)command.ExecuteScalar() ?? "missing";
        }
        List<IntentSnapshot> ReadIntents()
        {
            var paths = Directory.EnumerateFiles(journal, "*.json").Take(17).ToArray();
            if (paths.Length > 16) throw new InvalidDataException("Unexpected recovery scenario intent count.");
            var result = new List<IntentSnapshot>(paths.Length);
            foreach (string path in paths)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 16384) throw new InvalidDataException("Oversized scenario intent.");
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                using var record = JsonDocument.Parse(bytes.ToArray());
                var intent = record.RootElement.GetProperty("Intent");
                result.Add(new(path, intent.GetProperty("IncidentId").GetGuid(), intent.GetProperty("AccountId").GetInt64(),
                    record.RootElement.GetProperty("Applied").GetBoolean(), intent.GetProperty("Evidence").GetProperty("RuleId").GetString()!,
                    Convert.ToHexString(SHA256.HashData(bytes.ToArray()))));
            }
            return result;
        }
        async Task Snapshot(string stage)
        {
            string target = Path.Combine(report, stage);
            Directory.CreateDirectory(target);
            var intents = ReadIntents();
            foreach (var intent in intents) File.Copy(intent.Path, Path.Combine(target, Path.GetFileName(intent.Path)), true);
            File.Copy(marker, Path.Combine(target, "run-safety.state"), true);
            await File.WriteAllTextAsync(Path.Combine(target, "state.json"), JsonSerializer.Serialize(new
            {
                stage, intents, bans = Scalar("SELECT COUNT(*) FROM PlayerBans"),
                permanentBans = Scalar("SELECT COUNT(*) FROM PlayerBans WHERE Expiration=3155378975999999999"),
                elapsedMs = timer.ElapsedMilliseconds
            }, Json));
            stages.Add(new { stage, elapsedMs = timer.ElapsedMilliseconds, intentCount = intents.Count });
        }
    }

    private static Task SendFirstProof(LabClient actor) => actor.SendBatch(
        LabClient.Packet(153, writer => { writer.Write((byte)0); writer.Write((short)1); }),
        LabClient.Packet(5, writer => LabClient.WriteInventory(writer, actor.Slot, 1, 9)));

    private static async Task Until(Func<bool> condition, TimeSpan timeout, string checkpoint)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException("M4 recovery checkpoint: " + checkpoint);
            await Task.Delay(25);
        }
    }
}
