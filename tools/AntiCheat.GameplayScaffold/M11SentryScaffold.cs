using System.Text.Json;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace CompatibilityAudit;

public sealed partial class GameplayScaffold
{
    // Reuses the existing M10 ordinary-account raw27/29 and native SendData witnesses.
    private void WriteM11SentryState()
    {
        var plugin = M5Plugin();
        var guard = plugin.GetType().GetField("_summonBudget", PrivateM5)?.GetValue(plugin);
        var sentry = guard?.GetType().GetProperty("SentryBudget")?.GetValue(guard);
        var capacities = guard?.GetType().GetField("capacities", PrivateM5)?.GetValue(guard) as Array;
        var pending = guard?.GetType().GetField("equipmentGaps", PrivateM5)?.GetValue(guard) as Array;
        object? Value(object? source, string name) => source?.GetType().GetProperty(name)?.GetValue(source);
        var payload = new { utc = DateTimeOffset.UtcNow, guardPresent = sentry is not null,
            healthy = Value(guard, "Healthy"), targetValidated = Value(sentry, "TargetValidated"),
            failureReason = Value(sentry, "FailureReason"), nativeCalculations = Value(guard, "NativeCalculations"),
            nativeCommits = Value(sentry, "NativeCommits"),
            actors = m10SummonActors.Values.Select(witness =>
            {
                var actor = witness.Actor; var player = actor.TPlayer; var capacity = capacities?.GetValue(actor.Index);
                var state = guard?.GetType().GetMethod("CaptureSentry")?.Invoke(guard, [actor.Index]);
                var decision = Value(state, "Decision");
                return new { slot = actor.Index, actor.Name, account = actor.Account.ID,
                    samePlayer = ReferenceEquals(TShock.Players[actor.Index], actor), actor.IsLoggedIn, actor.HasSentInventory,
                    bypass = actor.HasPermission("anticheat.bypass"), sscBypass = actor.HasPermission(Permissions.bypassssc),
                    x = player.position.X, y = player.position.Y, player.dead, player.maxTurrets,
                    nativeCapacityReady = capacity is not null, nativeCapacity = Value(capacity, "TurretMaximum"),
                    upperBound = Value(capacity, "TurretUpperBound"), pendingWarTableAllowance = Value(capacity, "PendingTurretBuffAllowance"),
                    nativeCapacitySession = Value(capacity, "Session"),
                    pendingEquipmentSlots = (pending?.GetValue(actor.Index) as IEnumerable<int>)?.Order().ToArray() ?? [],
                    knownActiveEntities = Value(state, "KnownActiveEntities"), witness.RawRequests, witness.Send27, witness.Send29, witness.LastRaw,
                    latestDecision = decision is null ? null : new { session = Value(decision, "Session"),
                        sequence = Value(decision, "Sequence"), key = Value(decision, "ProjectileKey"),
                        action = Value(decision, "Action")?.ToString(), verdict = Value(decision, "Verdict")?.ToString(),
                        reason = Value(decision, "Reason"), passes = Value(decision, "Passes"),
                        blocks = Value(decision, "Blocks"), unknowns = Value(decision, "Unknowns") },
                    activeSentries = Main.projectile.Where(entity => entity.active && entity.owner == actor.Index && entity.sentry)
                        .Take(1000).Select(entity => new { key = entity.key.bits, entity.type, entity.timeLeft, entity.whoAmI }).ToArray(),
                    activeProjectiles = Main.projectile.Where(entity => entity.active && entity.owner == actor.Index)
                        .Take(1000).Select(entity => new { key = entity.key.bits, entity.type, entity.whoAmI }).ToArray(),
                    source = "read-only existing-guard capacity/entity/decision and native server snapshot" };
            }).ToArray() };
        File.WriteAllText(Path.Combine(output!, "m11-sentry-state-latest.json"), JsonSerializer.Serialize(payload, jsonOptions));
    }

    private void SupplyM11SentryGuiMaterials(string[] names)
    {
        Require(names.Length == 1, "Use qa_m11_sentry_materials <actor already prepared by qa_m10_summon>.");
        var actor = ResolvePlayer(names[0]);
        Require(m10SummonActors.TryGetValue(actor.Index, out var witness) && ReferenceEquals(witness.Actor, actor) &&
            actor.IsLoggedIn && actor.Account is not null && !actor.HasPermission("anticheat.bypass") && !actor.HasPermission(Permissions.bypassssc),
            "Prepared ordinary SSC account required.");
        string marker = Path.Combine(root!, "qa-m11-sentry-materials-" + actor.Index + ".json");
        Require(!File.Exists(marker), "This isolated supply batch was already issued.");
        var materials = new[] { (Type: ItemID.StaffoftheFrostHydra, Stack: 1), (Type: ItemID.SlimeStaff, Stack: 1),
            (Type: ItemID.ManaCrystal, Stack: 9), (Type: ItemID.LesserManaPotion, Stack: 30) };
        File.WriteAllText(marker, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, actor = actor.Name,
            account = actor.Account.ID, source = "one-time isolated fixture supply; client performs native consumption and use",
            materials = materials.Select(item => new { item.Type, item.Stack }) }, jsonOptions));
        foreach (var item in materials) actor.GiveItem(item.Type, item.Stack);
        Record("m11-sentry-gui-materials", new { marker, actor = actor.Name, noBypass = true });
        WriteM11SentryState();
    }
}
