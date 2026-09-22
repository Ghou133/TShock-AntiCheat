using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

/// <summary>One current NPC read on the verified update context; no history, per-packet scan,
/// delayed mutation, or damage provenance inferred from accepted player/SSC values.</summary>
public sealed class M16CombatNpcImmunityGuard(string fingerprint,
    Func<int, (SessionSnapshot? Session, TSPlayer? Player)> targets)
{
    private long worldEpoch;
    private int updateThread;

    public void Tick(long epoch)
    {
        worldEpoch = epoch;
        updateThread = Environment.CurrentManagedThreadId;
    }

    public BusinessRuleResult Evaluate(NpcStrikeObservation request, SessionKey session, TSPlayer actor)
    {
        bool verifiedThread = updateThread != 0 && Environment.CurrentManagedThreadId == updateThread;
        bool native = verifiedThread && fingerprint == TargetRuntime.Fingerprint && Main.netMode == 2 &&
            Main.myPlayer == 255 && worldEpoch > 0 && session.WorldEpoch == worldEpoch &&
            ServerApi.Plugins.All(x => x.Plugin.GetType() == typeof(TShockAPI.TShock) ||
                x.Plugin.GetType() == typeof(AntiCheatPlugin));
        var current = verifiedThread && (uint)session.Slot < 255 ? targets(session.Slot) : (null, null);
        bool attribution = verifiedThread && current.Item1 is { } bound && bound.Key == session && !bound.Revoked &&
            ReferenceEquals(current.Item2, actor) && actor.Index == session.Slot && actor.IsLoggedIn &&
            actor.Account is { } account && bound.AccountId == account.ID && account.ID > 0 &&
            Main.player is { } players && (uint)session.Slot < players.Length &&
            ReferenceEquals(players[session.Slot], actor.TPlayer) && actor.HasSentInventory && !actor.IgnoreSSCPackets;
        M16CombatTargetSnapshot? snapshot = null;
        if (native && Main.npc is { } npcs && (uint)request.TargetSlot < npcs.Length &&
            npcs[request.TargetSlot] is { } npc && npc.whoAmI == request.TargetSlot && npc.ai is { Length: >= 1 })
            snapshot = new(npc.whoAmI, npc.generation, npc.type, npc.netID, npc.aiStyle,
                npc.active, npc.life, npc.ai[0], npc.dontTakeDamage);
        var input = new RuleInputContext(session, fingerprint, TargetRuntime.Fingerprint,
            true, snapshot is not null, attribution, native);
        var result = M16CombatImmunityRules.Evaluate(request, input, snapshot, native);
        return result with
        {
            Facts = result.Facts
                .Add("producer", nameof(M16CombatNpcImmunityGuard))
                .Add("targetSource", "current-server-npc-native-phase-policy")
        };
    }
}
