using Terraria;
using Terraria.DataStructures;
using TerrariaApi.Server;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    private void M18LockHealthCommand(string[] arguments)
    {
        Require(arguments.Length == 2,
            "Use qa_m18_lock_health <exact player name or tsi:index> <one|normal>.");
        var player = ResolvePlayer(arguments[0]);
        Require(arguments[1] is "one" or "normal",
            "The lock-health fixture mode must be one or normal.");
        Require(player.TPlayer.statLifeMax2 is > 1 and <= short.MaxValue,
            "The target must have a bounded effective life maximum.");

        int damage = arguments[1] == "one" ? 1 : 2;
        int maximum = player.TPlayer.statLifeMax2;
        player.TPlayer.statLife = maximum - 1;
        int ignoreClient = TShock.Players
            .Where(candidate => candidate is not null && candidate.Active && candidate.Index != player.Index)
            .Select(candidate => candidate.Index)
            .FirstOrDefault(255);

        // This is a server-side native output fixture. It deliberately does not
        // call Player.Hurt: the audited server-mode Player.Hurt path may reduce
        // life without producing a SendPlayerHurt output. The output call and
        // the later client packet16 remain separate evidence stages.
        NetMessage.SendPlayerHurt(player.Index, PlayerDeathReason.ByOther(0), damage, -1,
            false, false, ignoreClient);
        Record("m18-lock-health-output", new
        {
            playerIndex = player.Index,
            accountId = player.Account?.ID,
            mode = arguments[1],
            damage,
            serverLifeAfterFixture = player.TPlayer.statLife,
            serverLifeMaximum = maximum,
            remoteClient = -1,
            ignoreClient,
            targetIncluded = ignoreClient != player.Index,
            note = "Disposable TestLab native SendPlayerHurt output; no account sanction or permanent state is written by the fixture."
        });
        TSPlayer.Server.SendInfoMessage(
            $"QA_M18_LOCK_HEALTH_OUTPUT player={player.Index} mode={arguments[1]} damage={damage} life={player.TPlayer.statLife}/{maximum} ignore={ignoreClient}");
    }
}
