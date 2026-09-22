using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.DataStructures;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed partial class M5CombatContexts
{
    private void OnProjectionDefaults(Projectile entity, HookEvents.Terraria.Projectile.SetDefaultsEventArgs args)
    {
        if (!installed || failed || !witnesses.TryGetValue(entity.key.bits, out var witness) ||
            !ReferenceEquals(entity, witness.Entity)) return;
        if (!VerifiedThread)
        {
            Fail(new InvalidOperationException("Arrow defaults changed outside the verified update context."));
            return;
        }
        // SetDefaults can reuse an existing object and a wrapped key without an observed29.
        // A new native lifetime cannot inherit a previous arrow's initial declaration.
        witnesses.Remove(entity.key.bits);
    }

    /// <summary>Consumes the existing bounded raw-create / actual post-write relay witness.
    /// Call after Evaluate in the same verified raw hook. The withdrawn C6 rule remains unchanged.</summary>
    public BusinessRuleResult? EvaluateProjection(M2Packet packet, SessionKey session, TSPlayer actor, bool alreadyCancelled)
    {
        if (!installed || failed || !VerifiedThread || !M4CombatProjectileReader.TryRead(packet, out var parsed) ||
            parsed!.Type != 1 || (uint)session.Slot >= 255) return null;
        var shot = parsed;
        var key = (ProjectileKey)shot.Key;
        bool knownPlugins = ObservePluginComposition();
        bool found = M2ProjectileLookup.TryGet(key, out var entity, out bool complete) && entity!.active;
        bool confirmed = witnesses.TryGetValue(shot.Key, out var witness) && witness.Session == session &&
            tick - witness.Tick <= WitnessTtlTicks && SameEntity(witness);
        bool actorVerified = actor.Index == session.Slot && actor.IsLoggedIn && actor.Account is not null &&
            targets?.Invoke(session.Slot).Session is { } current && current.Key == session && !current.Revoked &&
            current.AccountId == actor.Account.ID;
        bool available = ContractHealthy && knownPlugins && worldEpoch == session.WorldEpoch &&
            fingerprint == TargetRuntime.Fingerprint && Main.netMode == 2 && Main.myPlayer == 255 &&
            shot.AllNumbersFinite && actor.HasSentInventory && !actor.IgnoreSSCPackets;
        bool sourceException = sourceExceptions[session.Slot] == session || pluginExceptions[session.Slot] == session ||
            confirmed && !M7ArrowProjectionRules.IsReachable(witness!.InitialDamage, entity!.damage);
        var input = new RuleInputContext(session, fingerprint, fingerprint, true, available, actorVerified, available);
        var observation = new ProjectileObservation(new(key.Spawner, key.Index, key.Generation),
            found ? ProjectileOperation.Update : ProjectileOperation.Create, shot.Type);
        var result = M7ArrowProjectionRules.Evaluate(observation, shot.Damage,
            new(input, available, found && complete && confirmed, confirmed, witness?.InitialDamage ?? 0, sourceException));
        return result with { Facts = result.Facts.Add("producer", "M5CombatContexts.EvaluateProjection")
            .Add("initialWitness", "raw-fresh-key+post-write-vanilla-relay")
            .Add("snapshotTick", tick.ToString()).Add("upstreamCancelled", alreadyCancelled.ToString()) };
    }
}
