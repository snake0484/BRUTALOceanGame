namespace BrutalGame.Crafting;

/// <summary>
/// Purity bands, the global cap, and the auto-merge bucket edges (exotic-matter.md, Stacks, blending, assay).
/// <para>Edges sit on every band edge and every gate, derived by the loader, so a merge (a mass-weighted mean of
/// nominal purities that all lie in one bucket) can never push content across a gate in either direction.</para>
/// <para>The Ore band merges whole, except that gates below its top (0.10 today) still split it.</para>
/// </summary>
public sealed class PurityRules
{
    public const int NeverMerge = -1;

    private const int AboveOre = 1 << 30;

    public int QMaxPpm { get; }
    public int BucketPpm { get; }
    public int PotencyBucketPpm { get; }
    public IReadOnlyList<string> BandNames => _bandNames;
    public IReadOnlyList<int> BandEdgesPpm => _bandEdges;
    public IReadOnlyList<int> GatesPpm => _gates;
    public int OreTopPpm => _bandEdges.Length > 1 ? _bandEdges[1] : int.MaxValue;

    private readonly string[] _bandNames;
    private readonly int[] _bandEdges;
    private readonly int[] _gates;

    public PurityRules(int qMaxPpm, int bucketPpm, int potencyBucketPpm, string[] bandNames, int[] bandEdges, int[] gates)
    {
        QMaxPpm = qMaxPpm;
        BucketPpm = bucketPpm;
        PotencyBucketPpm = potencyBucketPpm;
        _bandNames = bandNames;
        _bandEdges = bandEdges;
        _gates = gates;
    }

    /// <summary>Index of the band holding <paramref name="ppm"/>.</summary>
    public int Band(int ppm)
    {
        var band = 0;

        while (band + 1 < _bandEdges.Length && ppm >= _bandEdges[band + 1])
            band++;

        return band;
    }

    /// <summary>Number of gates at or below <paramref name="ppm"/>: equal segments mean no gate lies between.</summary>
    public int Segment(int ppm)
    {
        int lo = 0, hi = _gates.Length;

        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;

            if (_gates[mid] <= ppm)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Merge key of a stack: stacks of one item merge automatically only when their keys are equal and not
    /// <see cref="NeverMerge"/>. Keys use NOMINAL purity, what gates check.
    /// </summary>
    public int BucketKey(ContentKind kind, long nominalMg, long massMg, bool assayed, bool metastable)
    {
        switch (kind)
        {
            case ContentKind.None:
                return 0;

            case ContentKind.Antimatter:
                return NeverMerge;

            case ContentKind.Active:
                return (Fixed.Fraction(nominalMg, massMg) / PotencyBucketPpm << 1) | (metastable ? 1 : 0);
        }

        var q = Fixed.Fraction(nominalMg, massMg);
        var segment = Segment(q);

        if (q < OreTopPpm)
            return segment;

        return AboveOre | (segment << 18) | ((q / BucketPpm) & 0xFFFF) << 2 | (assayed ? 2 : 0) | (metastable ? 1 : 0);
    }
}
