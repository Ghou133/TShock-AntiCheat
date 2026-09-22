using System.Collections.Immutable;

namespace AntiCheat.Rules;

public readonly record struct SignedDamageInterval(int Minimum, int Maximum);

/// <summary>Exact signed Int16 image of integer intervals. The fixed interval budget widens on
/// overflow; it never drops a legal branch to retain a smaller damage limit.</summary>
public sealed class M8DamageWireSet
{
    public const int IntervalCapacity = 32;
    public static M8DamageWireSet Empty { get; } = new([]);
    public static M8DamageWireSet Full { get; } = new([new(short.MinValue, short.MaxValue)]);
    public ImmutableArray<SignedDamageInterval> Intervals { get; }
    public bool IsFull => Intervals.Length == 1 && Intervals[0] == new SignedDamageInterval(short.MinValue, short.MaxValue);
    private M8DamageWireSet(ImmutableArray<SignedDamageInterval> intervals) => Intervals = intervals;
    public bool Contains(int value) => value is >= short.MinValue and <= short.MaxValue &&
        Intervals.Any(interval => value >= interval.Minimum && value <= interval.Maximum);

    public static M8DamageWireSet Project(int minimum, int maximum, int divisor = 1)
    {
        if (minimum > maximum || divisor <= 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        long low = minimum / divisor, high = maximum / divisor;
        if (high - low >= ushort.MaxValue) return Full;
        int begin = unchecked((ushort)(short)low), end = unchecked((ushort)(short)high);
        var parts = new List<SignedDamageInterval>(4);
        void Unsigned(int a, int b)
        {
            if (a <= short.MaxValue) parts.Add(new(a, Math.Min(b, short.MaxValue)));
            if (b > short.MaxValue) parts.Add(new(Math.Max(a, 32768) - 65536, b - 65536));
        }
        if (begin <= end) Unsigned(begin, end);
        else { Unsigned(begin, ushort.MaxValue); Unsigned(0, end); }
        return Normalize(parts);
    }

    public M8DamageWireSet Union(M8DamageWireSet other) => IsFull || other.IsFull ? Full : Normalize(Intervals.Concat(other.Intervals));
    private static M8DamageWireSet Normalize(IEnumerable<SignedDamageInterval> values)
    {
        var merged = new List<SignedDamageInterval>(IntervalCapacity);
        foreach (var value in values.OrderBy(x => x.Minimum))
        {
            if (merged.Count > 0 && value.Minimum <= merged[^1].Maximum + 1)
                merged[^1] = new(merged[^1].Minimum, Math.Max(merged[^1].Maximum, value.Maximum));
            else merged.Add(value);
        }
        return merged.Count > IntervalCapacity ? Full : new(merged.ToImmutableArray());
    }

    public override string ToString() => IsFull ? "all-int16" : string.Join('|', Intervals.Select(x => $"{x.Minimum}..{x.Maximum}"));
}

public sealed record M8DamageSourceRange(string Source, int Minimum, int Maximum, bool IncludeSingleReflection = false);

/// <summary>A finite set of supported result ranges plus an explicit full-domain branch for every
/// missing premise. Observed effects are usable inputs to compatibility, never a completeness certificate.</summary>
public sealed record M8DamageAllowedResults(M8DamageWireSet SupportedResults, M8DamageWireSet AllowedResults,
    ImmutableArray<string> MissingPremises, int SourceBranches)
{
    public bool Complete => MissingPremises.IsEmpty;
    public bool CanExclude(int damage) => Complete && !AllowedResults.Contains(damage);

    public static M8DamageAllowedResults Build(IEnumerable<M8DamageSourceRange> sources, IEnumerable<string> missing)
    {
        M8DamageWireSet supported = M8DamageWireSet.Empty;
        var gaps = missing.Distinct(StringComparer.Ordinal).Take(16).ToImmutableArray();
        int count = 0;
        foreach (var source in sources)
        {
            if (++count > 64)
                return new(M8DamageWireSet.Full, M8DamageWireSet.Full, gaps.Add("source-range-capacity-widened"), count);
            supported = supported.Union(M8DamageWireSet.Project(source.Minimum, source.Maximum));
            if (source.IncludeSingleReflection)
                supported = supported.Union(M8DamageWireSet.Project(source.Minimum, source.Maximum, 4));
        }
        if (count == 0 && gaps.IsEmpty) gaps = ["no-supported-source-range"];
        return new(supported, gaps.IsEmpty ? supported : M8DamageWireSet.Full, gaps, count);
    }

    /// <summary>Target GetWeaponDamage then PickAmmo: independent float products/truncations,
    /// weapon epsilon before conversion, ammo Sharp Barb before scaling. Nonfinite or Int32
    /// overflow expands to the full internal range, rather than pretending a float cast is safe.</summary>
    public static M8DamageSourceRange ArrowComponents(string source, int weaponDamage, int ammoDamage,
        float weaponMultiplier, float ammoMultiplier, bool sharpBarb)
    {
        float weapon = weaponDamage * weaponMultiplier + 0.000005f;
        float ammo = (ammoDamage + (sharpBarb ? 1f : 0f)) * ammoMultiplier;
        if (weaponDamage < 0 || ammoDamage < 0 || !float.IsFinite(weapon) || !float.IsFinite(ammo) ||
            weapon < 0 || ammo < 0 || weapon >= 2147483648f || ammo >= 2147483648f)
            return new(source, int.MinValue, int.MaxValue, true);
        long combined = (long)(int)weapon + (int)ammo;
        return combined > int.MaxValue ? new(source, int.MinValue, int.MaxValue, true) : new(source, 0, (int)combined, true);
    }

    // Client28 writes float Damage as Int16; the dedicated receiver clamps negative values to
    // zero before StrikeNPC, whose defense/critical calculation is a separate transformation.
    public static int NpcReceiverDamage(short wireDamage) => Math.Max(0, (int)wireDamage);
}
