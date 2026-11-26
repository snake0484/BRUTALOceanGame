using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// The generated surface height H(dir) about the datum radius: a pure function of the body's compiled planet kind and
/// the direction (planet-kinds.md §1.1). The kind's layers run in <see cref="LayerOps"/>; this is the point query (every
/// octave where it stands, no coarse grid) and the rigorous bounds over a cap of directions for the shell classification.
/// Band-limited per LOD. Allocation-free, thread-safe.
/// </summary>
public sealed class PlanetSurface
{
    public readonly PlanetParams Params;
    public readonly PlanetKindProgram Program;
    public readonly double Radius;
    public readonly float SeaHeight;
    public readonly bool HasSea;

    /// <summary>Ground at or below this (m about Radius) is protected (D30); −∞ with no sea.</summary>
    public readonly float ProtectHeight;

    public PlanetSurface(in PlanetParams p)
    {
        Params = p;
        Radius = p.Radius;
        HasSea = p.HasSea;
        SeaHeight = p.SeaHeight;
        ProtectHeight = HasSea ? SeaHeight + p.ShoreProtectHeight : float.NegativeInfinity;
        Program = PlanetKindCompiler.Compile(PlanetKinds.Get(p.KindId ?? PlanetKinds.Legacy), p);
    }

    public ReadOnlySpan<float> Wavelengths => Program.Wavelength;

    public ReadOnlySpan<float> Amplitudes => Program.Amplitude;

    /// <summary>Octaves with a wavelength under 2 voxels are skipped at this LOD (planets.md "Detail ladder").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MinWavelength(int lod) => 2f * (1 << lod);

    /// <summary>How many octaves LOD <paramref name="lod"/> evaluates.</summary>
    public int OctaveCount(int lod)
    {
        var count = 0;

        for (var layer = 0; layer < Program.LayerCount; layer++)
            count += Program.OctaveEnd[Program.At(lod, layer)] * Program.Fields(layer);

        return count;
    }

    /// <summary>H at a unit direction with the band limit of LOD <paramref name="lod"/>.</summary>
    [SkipLocalsInit]
    public float Height(double dx, double dy, double dz, int lod = 0)
    {
        Span<float> rows = stackalloc float[Program.Rows];
        var x = dx * Radius;
        var y = dy * Radius;
        var z = dz * Radius;
        rows[PlanetKindProgram.Height] = 0f;

        var set = new RowSet
        {
            X = new ReadOnlySpan<double>(in x),
            Y = new ReadOnlySpan<double>(in y),
            Z = new ReadOnlySpan<double>(in z),
            Ch = rows,
            Count = 1,
            Stride = 1,
            Channels = Program.ChannelCount,
            W = 1,
            H = 1,
            Direct = true,
        };

        LayerOps.Evaluate(Program, lod, ref set);
        return rows[PlanetKindProgram.Height];
    }

    /// <summary>Rigorous bounds of H over the cap within <paramref name="angle"/> (rad) of unit direction d, at every LOD.</summary>
    public void Bounds(double dx, double dy, double dz, double angle, out float lo, out float hi) =>
        Bounds(dx, dy, dz, angle, 0, PlanetGenerator.MaxLod, out lo, out hi);

    /// <summary>
    /// Rigorous bounds of H over the cap, for every LOD in [<paramref name="minLod"/>, <paramref name="maxLod"/>]: each
    /// octave's interval is its value at the centre ± Lipschitz × frequency × arc, and every layer maps intervals to an
    /// interval (planet-kinds.md §3).
    /// </summary>
    [SkipLocalsInit]
    public void Bounds(double dx, double dy, double dz, double angle, int minLod, int maxLod, out float lo, out float hi)
    {
        Span<float> los = stackalloc float[Program.ChannelCount];
        Span<float> his = stackalloc float[Program.ChannelCount];
        LayerOps.Bounds(Program, dx * Radius, dy * Radius, dz * Radius, angle * Radius, minLod, maxLod, los, his, Program.LayerCount);
        lo = los[PlanetKindProgram.Height];
        hi = his[PlanetKindProgram.Height];
        LayerOps.Widen(ref lo, ref hi);
        lo = Math.Clamp(lo, Params.MinHeight, Params.MaxHeight);
        hi = Math.Clamp(hi, Params.MinHeight, Params.MaxHeight);
    }

    /// <summary>Sea mask: generated ground below sea level. False with no sea.</summary>
    public bool IsSea(double dx, double dy, double dz) => HasSea && Height(dx, dy, dz) < SeaHeight;
}
