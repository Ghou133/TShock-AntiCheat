using System.Text.Json;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private Command? m16EgressCommand;
    private readonly Dictionary<int, TSPlayer> m16EgressActors = new(2);
    private long m16EgressEntries;
    private void PrepareM16Egress(string[] args)
    {
        Require(args.Length == 2 && m16EgressCommand is null, "Use qa_m16_egress <actor> <peer>, once per owned run.");
        foreach (var name in args)
        {
            var actor = ResolvePlayer(name);
            Require(actor is { IsLoggedIn: true, Account: not null } && !actor.HasPermission("anticheat.bypass") &&
                !actor.HasPermission("compatibility.qa.console"), "Ordinary authenticated test account required.");
            m16EgressActors.Add(actor.Index, actor);
        }
        // A fixed, bounded response producer. It is available only to these exact isolated
        // actor objects and executes synchronously through real Commands.HandleCommand.
        m16EgressCommand = new Command(a =>
        {
            ValidateIsolation();
            if (a.Parameters.Count != 0 || !m16EgressActors.TryGetValue(a.Player.Index, out var actor) ||
                !ReferenceEquals(actor, a.Player) || !actor.IsLoggedIn) return;
            m16EgressEntries++;
            for (int i = 0; i < 128; i++) TSPlayer.All.SendMessage("M16-EGRESS-FIXTURE-" + i.ToString("D3") + ":" + new string('x', 8192), 255, 255, 255);
        }, "m16_egress") { AllowServer = false, DoLog = false };
        Commands.ChatCommands.Add(m16EgressCommand);
        WriteM16EgressState();
    }
    private void WriteM16EgressState()
    {
        var plugin = M5Plugin(); var guard = plugin.GetType().GetField("_requestEgress", PrivateM5)?.GetValue(plugin);
        object? Value(string name) => guard?.GetType().GetProperty(name)?.GetValue(guard);
        var payload = new { utc = DateTimeOffset.UtcNow, fixtureArtificial = true, guardPresent = guard is not null,
            healthy = Value("Healthy"), lastFault = Value("LastFault"), admittedBytes = Value("AdmittedBytes"), blockedBytes = Value("BlockedBytes"),
            admittedSends = Value("AdmittedSends"), blockedSends = Value("BlockedSends"), accountBuckets = Value("AccountBucketCount"),
            commandEntries = m16EgressEntries, attemptsPerCommand = 128, charactersPerLine = "M16-EGRESS-FIXTURE-000:".Length + 8192,
            actors = m16EgressActors.Values.Select(a => new { a.Index, account = a.Account.ID, sameObject = ReferenceEquals(TShock.Players[a.Index], a),
                authenticated = a.IsLoggedIn, bypass = a.HasPermission("anticheat.bypass"), console = a.HasPermission("compatibility.qa.console") }).ToArray(),
            scope = "fixed isolated command response, not command CPU protection; real TShock chat serialize and native transport" };
        File.WriteAllText(Path.Combine(output!, "m16-egress-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
    private void DisposeM16Egress()
    {
        if (m16EgressCommand is not null) Commands.ChatCommands.Remove(m16EgressCommand);
        m16EgressCommand = null; m16EgressActors.Clear();
    }
}
