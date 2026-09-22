using System.Diagnostics;
using System.Text.Json.Nodes;

// Optional consumer only. The production plugin and independent MCP have no reference to this runner.
internal static class McpIdentityScenario
{
    public static async Task RunAsync(string run, string report, Process server,
        Func<string, Task<LabClient>> connect, Func<string, string?, long> scalar,
        Func<long, string> inventory, Func<string[]> logs, Action<bool, string> assert,
        Action<string, long, string> proved)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(run, "mcp-consumer.json")))!;
        var bridge = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(run, "server-bridge.json")))!;
        string root = input["root"]!.GetValue<string>();
        string sessionPath = Path.Combine(run, "mcp-session.json");
        await File.WriteAllTextAsync(sessionPath, new JsonObject
        {
            ["schemaVersion"] = 1, ["sessionId"] = "consumer-" + Guid.NewGuid().ToString("N"),
            ["clientKind"] = "synthetic", ["artifactDirectory"] = Path.Combine(report, "mcp-artifacts"),
            ["server"] = new JsonObject { ["pipeName"] = bridge["pipeName"]!.DeepClone(),
                ["token"] = bridge["token"]!.DeepClone(), ["processId"] = server.Id,
                ["processStartedUtc"] = server.StartTime.ToUniversalTime().ToString("O") }
        }.ToJsonString());
        await using var mcp = await Connection.Open(root, sessionPath, Path.Combine(report, "mcp-stdio.json"));
        var capabilities = await mcp.Call("session_capabilities", new());
        assert(capabilities["data"]?["sources"]?["server"]?["status"]?.ToString() == "ok", "mcp:actual-stdio-server-bridge-connected");
        var control = await connect("McpControl");
        await control.Join(); await control.RegisterAndLogin();
        var actor = await connect("McpActor");
        await actor.Join(); await actor.RegisterAndLogin();
        assert(control.Authenticated && actor.Authenticated && actor.SscSlots.Count >= 350, "mcp:real-authentication-and-ssc-complete");
        var before = await ReadPlayers(mcp);
        var player = Players(before).Single(x => x?["slot"]?.GetValue<int>() == actor.Slot)!;
        int inventoryBaseline = actor.InventoryUpdates.Count;
        var setup = await mcp.Call("fixture_action", new JsonObject
        {
            ["idempotency_key"] = "mcp-identity-initial-state", ["player_slot"] = (int)actor.Slot,
            ["expected_player_generation"] = player["generation"]!.DeepClone(),
            ["inventory"] = new JsonArray(new JsonObject { ["slot"] = 8, ["type"] = 8, ["stack"] = 5, ["prefix"] = 0 })
        });
        string actionId = setup["data"]?["actionId"]?.ToString() ?? throw new InvalidOperationException("MCP fixture action was not admitted: " + setup);
        var deadline = Stopwatch.StartNew();
        JsonNode final = setup;
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            final = await mcp.Call("action_status", new JsonObject { ["action_id"] = actionId });
            if (final["data"]?["status"]?.ToString() is "completed" or "failed" or "canceled" or "timed_out") break;
            await Task.Delay(50);
        }
        assert(final["data"]?["status"]?.ToString() == "completed", "mcp:fixture-prepared-on-server-thread");
        await actor.WaitUntil(() => actor.InventoryUpdates.Skip(inventoryBaseline).Any(x =>
            x.Player == actor.Slot && x.Slot == 8 && x.Item == 8 && x.Stack == 5 && x.Prefix == 0), TimeSpan.FromSeconds(5));
        assert(actor.InventoryUpdates.Skip(inventoryBaseline).Any(x =>
            x.Player == actor.Slot && x.Slot == 8 && x.Item == 8 && x.Stack == 5 && x.Prefix == 0),
            "mcp:new-exact-fixture-slot-type-stack-prefix-consumed-by-network-client");
        JsonNode prepared;
        bool itemObserved;
        var observationWait = Stopwatch.StartNew();
        do
        {
            prepared = await ReadPlayers(mcp);
            var preparedActor = Players(prepared).SingleOrDefault(x => x?["slot"]?.GetValue<int>() == actor.Slot);
            itemObserved = prepared["data"]?["sourceTick"]?.GetValue<long>() >= final["data"]?["fixture"]?["sourceTick"]?.GetValue<long>() &&
                JsonNode.DeepEquals(preparedActor?["generation"], player["generation"]) &&
                preparedActor?["inventory"]?.AsArray().Any(x => x?["slot"]?.GetValue<int>() == 8 && x?["type"]?.GetValue<int>() == 8 &&
                    x?["stack"]?.GetValue<int>() == 5 && x?["prefix"]?.GetValue<int>() == 0) == true;
            if (!itemObserved) await Task.Delay(50);
        } while (!itemObserved && observationWait.Elapsed < TimeSpan.FromSeconds(5));
        assert(itemObserved, "mcp:prepared-item-observed-on-server-and-consumed-by-network-client");
        long account = scalar("SELECT ID FROM Users WHERE Username=$name", actor.Name);
        var eventBefore = await mcp.Call("events_read", new JsonObject { ["source"] = "Server", ["limit"] = 128 });
        await actor.Send(120, w => { w.Write(actor.Slot); w.Write((byte)0); });
        await control.WaitUntil(() => control.Bubbles.Any(x => x.Player == actor.Slot && x.Emote == 0), TimeSpan.FromSeconds(5));
        assert(scalar("SELECT COUNT(*) FROM PlayerBans", null) == 0, "mcp:legal-own-emoji-reaches-recipient-without-ban");
        int bubbleCount = control.Bubbles.Count, itemCount = control.InventoryUpdates.Count;
        byte[] forged = LabClient.Packet(120, w => { w.Write(control.Slot); w.Write((byte)1); });
        byte[] later = LabClient.Packet(5, w => { w.Write(actor.Slot); w.Write((short)10); w.Write((short)1); w.Write((byte)0); w.Write((short)9); w.Write((byte)0); });
        await actor.SendBatch(forged, later);
        await actor.WaitUntil(() => actor.DisconnectReason is not null || actor.Closed, TimeSpan.FromSeconds(8));
        await control.Drain(TimeSpan.FromSeconds(2));
        assert(actor.DisconnectReason == "AntiCheat proven violation.", "mcp:first-forged-emoji-causes-real-plugin-disconnect");
        var persistence = Stopwatch.StartNew();
        while (scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + actor.Name) != 1 && persistence.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        assert(scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name AND Expiration=3155378975999999999", "acc:" + actor.Name) == 1, "mcp:real-permanent-account-ban-persisted");
        assert(scalar("SELECT COUNT(*) FROM PlayerBans WHERE Identifier=$name", "acc:" + control.Name) == 0, "mcp:innocent-account-remains-unbanned");
        assert(control.Bubbles.Skip(bubbleCount).All(x => x.Emote != 1), "mcp:forged-emoji-not-broadcast");
        assert(control.InventoryUpdates.Skip(itemCount).All(x => x.Player != actor.Slot || x.Slot != 10 || x.Item != 9) && inventory(account).Split('~')[10].StartsWith("0,"), "mcp:coalesced-after-proof-write-neither-broadcast-nor-persisted");
        assert(logs().Any(x => x.Contains("ANTICHEAT_INCIDENT") && x.Contains("A01.EmojiSenderMismatch") && x.Contains("accountId=" + account) && x.Contains("canceled=true", StringComparison.OrdinalIgnoreCase) && x.Contains("revoked=true", StringComparison.OrdinalIgnoreCase)), "mcp:existing-Production-rule-actually-canceled-and-revoked");
        proved(actor.Name, account, "A01.EmojiSenderMismatch");
        var after = await ReadPlayers(mcp);
        var eventAfter = await mcp.Call("events_read", new JsonObject { ["source"] = "Server", ["limit"] = 128,
            ["after_cursor"] = eventBefore["data"]?["nextCursor"]?.DeepClone() ?? JsonValue.Create(0) });
        await actor.DisposeAsync();
        var reconnect = await connect(actor.Name);
        await reconnect.Join(); await reconnect.LoginAndExpectRejected();
        assert(reconnect.LoginAttempts == 1 && reconnect.IsBanRejection, "mcp:normal-password-relogin-rejected-by-persisted-ban");
        await reconnect.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(report, "mcp-scenario.json"), new JsonObject
        {
            ["status"] = "passed", ["rule"] = "A01.EmojiSenderMismatch/m2.1", ["scope"] = "Production",
            ["stimulus"] = "existing-NetworkLab-real-TCP-parser-hooks-sanction", ["observation"] = "independent-MCP-server-state-plus-recipient-and-SQLite",
            ["screenshots"] = 0, ["before"] = before, ["preparation"] = final, ["prepared"] = prepared,
            ["preparationReceiverUpdates"] = new JsonArray(actor.InventoryUpdates.Skip(inventoryBaseline)
                .Where(x => x.Player == actor.Slot && x.Slot == 8).Take(16)
                .Select(x => (JsonNode)new JsonObject { ["player"] = x.Player, ["slot"] = x.Slot,
                    ["item"] = x.Item, ["stack"] = x.Stack, ["prefix"] = x.Prefix }).ToArray()),
            ["after"] = after, ["events"] = eventAfter, ["stockGuiClaim"] = false,
            ["otherRuleCompatibility"] = "not_claimed; original host eligibility remains unchanged"
        }.ToJsonString(new() { WriteIndented = true }));
    }

    private static Task<JsonNode> ReadPlayers(Connection mcp) => mcp.Call("state_read", new JsonObject { ["source"] = "Server", ["fields"] = new JsonArray("world", "players"), ["limit"] = 8 });
    private static JsonArray Players(JsonNode result) => result["data"]!["fields"]!["players"]!["items"]!.AsArray();

    private sealed class Connection(Process process, Task<string> stderr) : IAsyncDisposable
    {
        public static async Task<Connection> Open(string root, string session, string evidence)
        {
            var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-u", Path.Combine(root, "scripts/mcp_session.py"), "--session", session, "--evidence", evidence }) start.ArgumentList.Add(arg);
            var process = Process.Start(start)!;
            var result = new Connection(process, process.StandardError.ReadToEndAsync());
            string line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("MCP helper exited.");
            if (JsonNode.Parse(line)?["connected"]?.GetValue<bool>() != true) throw new IOException("MCP helper did not connect.");
            return result;
        }
        public async Task<JsonNode> Call(string tool, JsonObject arguments)
        {
            await process.StandardInput.WriteLineAsync(new JsonObject { ["tool"] = tool, ["arguments"] = arguments }.ToJsonString());
            await process.StandardInput.FlushAsync();
            string line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)) ?? throw new IOException("MCP connection exited: " + await stderr);
            return JsonNode.Parse(line)!["result"]!.DeepClone();
        }
        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            process.Dispose();
        }
    }
}
