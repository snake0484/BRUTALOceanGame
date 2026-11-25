namespace BrutalGame.Terrain;

/// <summary>
/// Job-owned scratch for <see cref="PlanetGenerator"/>: the per-face lattice windows, and after a masked generate the
/// protected voxels (D30 shores and the dig floor) with the generated densities they clamp removal to. Grows during
/// warm-up only.
/// </summary>
public sealed class PlanetChunkScratch
{
    public const int MaxFaces = 3;
    private const int InitialPoints = 72 * 72;

    /// <summary>Protected voxels over the padded volume, one bit each (<see cref="ChunkConstants.Index"/> order).</summary>
    public readonly ulong[] Protected = new ulong[(ChunkConstants.PaddedVolume + 63) / 64];

    /// <summary>Generated density, copied only by a masked generate (edits to replay).</summary>
    public readonly sbyte[] Generated = new sbyte[ChunkConstants.PaddedVolume];

    /// <summary>Protected voxels in the last masked generate; 0 means edits replay unclamped.</summary>
    public int ProtectedCount;

    internal readonly Window[] Windows = new Window[MaxFaces];
    internal int WindowCount;
    internal float[] Heights = new float[MaxFaces * InitialPoints];
    internal float[] Inv = new float[MaxFaces * InitialPoints];
    internal float[] Coarse = new float[MaxFaces * 3 * 256];
    internal double[] AxisG = new double[MaxFaces * 2 * 128];
    internal double[] AxisSlope = new double[MaxFaces * 2 * 128];

    internal struct Window
    {
        public int Face;

        /// <summary>Lattice points voxels interpolate between (inclusive).</summary>
        public int I0, J0, I1, J1;

        /// <summary>Heights cover the points one wider for central differences, clipped to the face.</summary>
        public int HI0, HJ0, HW, HH, HBase;

        public int InvBase;

        /// <summary>Coarse lattice (every 8th point) under the heights.</summary>
        public int CI0, CJ0, CW, CH, CBase;

        /// <summary>Per-axis gnomonic coordinate and dg/dt over the height range: u at AxisBase, v at AxisBase + HW.</summary>
        public int AxisBase;
    }

    public bool IsProtected(int index) => (Protected[index >> 6] & (1UL << index)) != 0;

    internal static void Ensure<T>(ref T[] array, int length)
    {
        if (array.Length < length)
            array = new T[Math.Max(length, array.Length * 2)];
    }
}
