using System.Text.Json;
using Terraria;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // One-time setup for the disposable P1-C reward run. This changes only
    // Main.GameMode in the copied server process so native boss-bag logic is
    // reachable; it does not write the world, create an item, or claim reward
    // ownership. The before/after marker makes the setup auditable.
    private void M18ExpertModeCommand(string[] args)
    {
        Require(args.Length == 0, "Use qa_m18_expert_mode without arguments.");

        string marker = Path.Combine(root!, "m18-world-mode-preparation.json");
        Require(!File.Exists(marker), "The one-time M18 expert-mode preparation already ran; existing process state is retained.");

        int beforeGameMode = Main.GameMode;
        bool beforeExpert = Main.expertMode;
        bool beforeMaster = Main.masterMode;
        Require(!beforeExpert && !beforeMaster,
            "The M18 expert-mode preparation expects the copied seed to start in normal mode.");

        Main.GameMode = 1;
        Require(Main.expertMode && !Main.masterMode,
            "The copied server did not enter Expert mode through Main.GameMode.");

        var payload = new
        {
            utc = DateTimeOffset.UtcNow,
            command = "qa_m18_expert_mode",
            fixtureArtificial = true,
            readOnly = false,
            worldFileMutation = false,
            itemMutation = false,
            beforeGameMode,
            afterGameMode = Main.GameMode,
            beforeExpert,
            afterExpert = Main.expertMode,
            beforeMaster,
            afterMaster = Main.masterMode,
            note = "One-time in-memory difficulty preparation in the copied isolated server process; no original world write and no reward/item creation."
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(payload, jsonOptions));
        Record("m18-world-mode-preparation", payload);
        TSPlayer.Server.SendInfoMessage("M18 Expert-mode preparation recorded in copied process: " + marker + ".");
    }
}
