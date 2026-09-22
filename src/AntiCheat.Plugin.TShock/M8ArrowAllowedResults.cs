using System.Buffers.Binary;
using System.Collections.Immutable;
using AntiCheat.Core;
using AntiCheat.Rules;
using Terraria;
using Terraria.ID;

namespace AntiCheat.Plugin.TShock;

/// <summary>A serialized server payload observed for this recipient, not a receipt acknowledgement.
/// The raw wire fields are immutable and intentionally survive disappearance of the world item.</summary>
public sealed record M8CombatExport(long Tick, int PacketId, int EntitySlot, int ItemType,
    int? WireDamage, int? WireShoot, int? WireAmmo, int? WireUseAmmo, ImmutableArray<int> BuffTypes);

public sealed record M8ArrowBuffProjection(float BowMultiplier, bool AllBuffsSupported, int PreservedSlots)
{
    public static M8ArrowBuffProjection FromExport(M8CombatExport export)
    {
        float ranged = 1f, arrow = 1f;
        bool complete = export.PacketId == 50;
        foreach (int buff in export.BuffTypes)
        {
            // Reproduce the two independent float accumulators and preserve direct-array duplicates.
            // Do not pass SSC50 through AddBuff, which would de-duplicate a legitimate host export.
            if (buff == BuffID.Archery) arrow *= 1.1f;
            else if (buff == BuffID.Wrath) ranged += 0.1f;
            else complete = false;
        }
        return new(ranged * arrow, complete, export.BuffTypes.Length);
    }
}

public sealed partial class M6ArrowCandidateContexts
{
    public const int ExportCapacity = 8;
    private sealed record ExportDispatch(int Packet, int EntitySlot, int ItemType, int Remote, int Ignore, long Tick);
    private ExportDispatch? exportDispatch;
    public long SerializedExports { get; private set; }
    public long DroppedExports { get; private set; }
    // Counts actual immutable union builds; unchanged state may serve many declarations.
    public long DamageUnionEvaluations { get; private set; }
    public long DamageUnionSupportedMatches { get; private set; }

    private void PrepareCombatExport(HookEvents.Terraria.NetMessage.SendDataEventArgs args)
    {
        exportDispatch = null;
        if (!installed || failed || !args.ContinueExecution || Main.netMode != 2 ||
            Environment.CurrentManagedThreadId != updateThread || worldEpoch <= 0) return;
        if (args.msgType == 88 && (uint)args.number < Main.item.Length && Main.item[args.number]?.inner is { } item)
            exportDispatch = new(88, args.number, item.type, args.remoteClient, args.ignoreClient, tick);
        else if (args.msgType == 50 && (uint)args.number < 255)
            exportDispatch = new(50, args.number, 0, args.remoteClient, args.ignoreClient, tick);
    }

    private void OnCombatPacketWrite(object? sender, HookEvents.Terraria.NetMessage.OnPacketWriteEventArgs args)
    {
        if (!installed || failed || Main.netMode != 2 || Environment.CurrentManagedThreadId != updateThread ||
            exportDispatch is not { } dispatch || dispatch.Tick != tick) return;
        try
        {
            // Native OnPacketWrite supplies the actual framed bytes after Write(Int16/UInt16).
            // No packet is copied unless it is one of the two bounded export subjects.
            var stream = args.ms;
            if (stream.Length < 4 || stream.Length > 256) return;
            var frame = stream.ToArray();
            if (frame[2] != dispatch.Packet) return;
            exportDispatch = null;
            var payload = frame.AsSpan(3);
            M8CombatExport? export = null;
            if (dispatch.Packet == 88 && payload.Length >= 3 && BinaryPrimitives.ReadInt16LittleEndian(payload) == dispatch.EntitySlot)
                export = ReadItemExport(payload, dispatch);
            if (dispatch.Packet == 50 && payload.Length >= 3 && payload.Length <= 3 + Player.maxBuffs * 2 &&
                (payload.Length & 1) == 1 && payload[0] == dispatch.EntitySlot)
            {
                var buffs = ImmutableArray.CreateBuilder<int>();
                for (int offset = 1; offset < payload.Length; offset += 2)
                {
                    int value = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
                    if (value == 0)
                    {
                        if (offset + 2 == payload.Length)
                            export = new(tick, 50, dispatch.EntitySlot, 0, null, null, null, null, buffs.ToImmutable());
                        break;
                    }
                    buffs.Add(value); // Direct SSC50 preserves duplicate IDs; never normalize through AddBuff.
                }
            }
            if (export is null) return;
            for (int slot = 0; slot < states.Length; slot++)
            {
                if (states[slot] is not { } state || state.Session.WorldEpoch != worldEpoch ||
                    !(dispatch.Remote == slot || dispatch.Remote == -1 && dispatch.Ignore != slot) ||
                    export.PacketId == 50 && export.EntitySlot != slot) continue;
                if (state.Exports.Count == ExportCapacity)
                {
                    state.Exports.Dequeue(); state.ExportHistoryLost = true;
                    DroppedExports = Increment(DroppedExports);
                }
                state.Exports.Enqueue(export); SerializedExports = Increment(SerializedExports);
                InvalidateDamageResults(state);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private static M8CombatExport? ReadItemExport(ReadOnlySpan<byte> payload, ExportDispatch dispatch)
    {
        byte flags = payload[2]; int offset = 3;
        int? damage = null, shoot = null, ammo = null, useAmmo = null;
        // Validate every variable field, including fields irrelevant to damage, before retaining it.
        for (int bit = 0; bit < 7; bit++)
        {
            if ((flags & (1 << bit)) == 0) continue;
            int width = bit is 0 or 2 or 6 ? 4 : 2;
            if (offset > payload.Length - width) return null;
            if (bit == 1) damage = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            if (bit == 5) shoot = BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]);
            offset += width;
        }
        if ((flags & 128) != 0)
        {
            if (offset >= payload.Length) return null;
            byte second = payload[offset++];
            for (int bit = 0; bit < 6; bit++)
            {
                if ((second & (1 << bit)) == 0) continue;
                int width = bit == 2 ? 4 : bit == 5 ? 1 : 2;
                if (offset > payload.Length - width) return null;
                if (bit == 3) ammo = BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]);
                if (bit == 4) useAmmo = BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]);
                offset += width;
            }
        }
        return offset == payload.Length ? new(dispatch.Tick, 88, dispatch.EntitySlot, dispatch.ItemType, damage, shoot, ammo, useAmmo, []) : null;
    }

    private M8DamageAllowedResults BuildDamageResults(ArrowCandidateEnvelope envelope, bool exportHistoryLost)
    {
        var ranges = new List<M8DamageSourceRange>(32);
        var gaps = new List<string>
        {
            "pre-observation-item-and-effect-history-not-established",
            "native-effect-snapshots-are-not-all-client-possibilities",
            "unobserved-parent-and-delayed-client-creation-results"
        };
        // Damage-only88 need not repeat ammo/useAmmo fields. The canonical type supplies
        // positive classification; explicit changed fields supply additional possibilities.
        // Combine both observed host-custom components: launcher+canonical ammo and
        // canonical launcher+ammo alone miss simultaneous exports. A single maxima pair
        // bounds all eight retained payloads without a per-snapshot Cartesian product.
        var itemExports = envelope.CombatExports.Where(x => x.PacketId == 88 && x.WireDamage.HasValue).ToArray();
        int exportedWeapon = itemExports.Where(x => envelope.CanonicalCandidates.Any(c => c.Type == x.ItemType) ||
                x.WireUseAmmo == AmmoID.Arrow).Select(x => x.WireDamage!.Value).DefaultIfEmpty(0).Max();
        int exportedAmmo = itemExports.Where(x => canonicalArrowAmmunition.Contains(x.ItemType) || x.WireAmmo == AmmoID.Arrow)
            .Select(x => x.WireDamage!.Value).DefaultIfEmpty(0).Max();
        int combinedWeapon = Math.Max(envelope.MaximumCanonicalWeaponDamage, exportedWeapon);
        int combinedAmmo = Math.Max(envelope.MaximumCanonicalAmmoDamage, exportedAmmo);
        foreach (var snapshot in envelope.RecentSnapshots)
        {
            // Includes every canonical launcher, even if absent from the held slot or inventory.
            // AI075's audited615/714/630 type1 branches recompute the same damage at release.
            // A current multiplier is one possible branch, not an asserted global maximum.
            ranges.Add(M8DamageAllowedResults.ArrowComponents("canonical-launcher-ammo-or-AI075-release",
                envelope.MaximumCanonicalWeaponDamage, envelope.MaximumCanonicalAmmoDamage,
                snapshot.ObservedBowMultiplier, snapshot.ObservedBowMultiplier, true));
            foreach (var export in envelope.CombatExports)
            {
                if (export.PacketId != 88 || export.WireDamage is not { } damage) continue;
                bool launcher = envelope.CanonicalCandidates.Any(c => c.Type == export.ItemType) || export.WireUseAmmo == AmmoID.Arrow;
                bool ammunition = canonicalArrowAmmunition.Contains(export.ItemType) || export.WireAmmo == AmmoID.Arrow;
                if (launcher)
                    ranges.Add(M8DamageAllowedResults.ArrowComponents("server88-launcher-damage",
                        damage, envelope.MaximumCanonicalAmmoDamage, snapshot.ObservedBowMultiplier, snapshot.ObservedBowMultiplier, true));
                if (ammunition)
                    ranges.Add(M8DamageAllowedResults.ArrowComponents("server88-ammunition-damage",
                        envelope.MaximumCanonicalWeaponDamage, damage, snapshot.ObservedBowMultiplier, snapshot.ObservedBowMultiplier, true));
            }
            if (exportedWeapon > 0 && exportedAmmo > 0)
                ranges.Add(M8DamageAllowedResults.ArrowComponents("server88-combined-launcher-and-ammunition",
                    combinedWeapon, combinedAmmo, snapshot.ObservedBowMultiplier, snapshot.ObservedBowMultiplier, true));
        }
        foreach (var export in envelope.CombatExports.Where(x => x.PacketId == 50))
        {
            var buffs = M8ArrowBuffProjection.FromExport(export);
            if (!buffs.AllBuffsSupported) gaps.Add("SSC50-includes-buff-outside-archery-wrath-component");
            // This is a normalized component branch, not a claim that the client has consumed50
            // or that armor and other effects are absent. Those alternatives remain in the union.
            ranges.Add(M8DamageAllowedResults.ArrowComponents("SSC50-direct-array-archery-wrath-component",
                envelope.MaximumCanonicalWeaponDamage, envelope.MaximumCanonicalAmmoDamage,
                buffs.BowMultiplier, buffs.BowMultiplier, true));
            if (exportedAmmo > 0)
                ranges.Add(M8DamageAllowedResults.ArrowComponents("SSC50-effects-with-server88-combined-components",
                    combinedWeapon, combinedAmmo, buffs.BowMultiplier, buffs.BowMultiplier, true));
            foreach (var itemExport in envelope.CombatExports.Where(x => x.PacketId == 88 && x.WireDamage.HasValue &&
                (envelope.CanonicalCandidates.Any(c => c.Type == x.ItemType) || x.WireUseAmmo == AmmoID.Arrow)))
                ranges.Add(M8DamageAllowedResults.ArrowComponents("SSC50-effects-with-server88-launcher",
                    itemExport.WireDamage!.Value, envelope.MaximumCanonicalAmmoDamage,
                    buffs.BowMultiplier, buffs.BowMultiplier, true));
        }
        if (envelope.Gaps != ArrowCandidateGap.NativeCreationBranchesNotClosed) gaps.Add(envelope.Gaps.ToString());
        if (exportHistoryLost) gaps.Add("bounded-export-payload-history-lost-influence-not-cleared");
        var result = M8DamageAllowedResults.Build(ranges, gaps);
        DamageUnionEvaluations = Increment(DamageUnionEvaluations);
        return result;
    }

    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
}
