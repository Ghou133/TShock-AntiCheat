using System.Reflection;
using System.Text.Json;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Mono.Cecil.Cil;
using TShockAPI;
using TShockAPI.Hooks;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private ILHook? applicationCommandWitness;
    private readonly Dictionary<int, (TSPlayer Player, long Entries, long Parsed)> applicationActors = new(4);

    private void PrepareApplicationWitness(string[] names)
    {
        Require(names.Length is > 0 and <= 4, "Use qa_m9_application <one to four exact ordinary actors>.");
        Require(applicationActors.Count == 0, "Application witness is prepared once per owned run.");
        foreach (var name in names)
        {
            var player = ResolvePlayer(name);
            Require(player.IsLoggedIn && !player.HasPermission("anticheat.bypass") &&
                !player.HasPermission("compatibility.qa.console"), "Ordinary authenticated actor required.");
            applicationActors.Add(player.Index, (player, 0, 0));
        }
        // Method BODY entry, before Remove/Substring/ParseParameters/alias scan. This witness does not cancel.
        applicationCommandWitness = new(typeof(Commands).GetMethod(nameof(Commands.HandleCommand))!, il =>
        {
            var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<TSPlayer>>(player =>
            {
                if (applicationActors.TryGetValue(player.Index, out var state) && ReferenceEquals(state.Player, player))
                    applicationActors[player.Index] = state with { Entries = state.Entries + 1 };
            });
        });
        PlayerHooks.PlayerCommand += ObserveApplicationParsed;
        WriteApplicationState();
    }

    private void ObserveApplicationParsed(PlayerCommandEventArgs args)
    {
        if (applicationActors.TryGetValue(args.Player.Index, out var state) && ReferenceEquals(state.Player, args.Player))
            applicationActors[args.Player.Index] = state with { Parsed = state.Parsed + 1 };
    }

    private void DisposeApplicationWitness()
    {
        PlayerHooks.PlayerCommand -= ObserveApplicationParsed;
        applicationCommandWitness?.Dispose(); applicationCommandWitness = null; applicationActors.Clear();
    }

    private void WriteApplicationState()
    {
        var plugin = M5Plugin();
        var budget = plugin.GetType().GetField("_applicationRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!;
        object Value(string name) => budget.GetType().GetProperty(name)!.GetValue(budget)!;
        var payload = new { utc = DateTimeOffset.UtcNow, witnessInstalled = applicationCommandWitness is not null,
            admitted = Value("Admitted"), rejected = Value("Rejected"), accountBuckets = Value("AccountBucketCount"),
            actors = applicationActors.Values.Select(x => new { x.Player.Name, slot = x.Player.Index,
                account = x.Player.Account.ID, x.Entries, x.Parsed, group = x.Player.Group.Name,
                samePlayer = ReferenceEquals(TShock.Players[x.Player.Index], x.Player),
                bypass = x.Player.HasPermission("anticheat.bypass"), consolePermission = x.Player.HasPermission("compatibility.qa.console") }).ToArray(),
            source = "real Commands.HandleCommand body and post-parse PlayerCommand event; no command content retained" };
        File.WriteAllText(Path.Combine(output!, "m9-application-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }
}
