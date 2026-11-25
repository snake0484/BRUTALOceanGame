using Region = BrutalGame.Terrain.Runtime.BathymetryGrid.Region;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// The signed depth the ocean's depth map stores per <see cref="BathymetryGrid"/> cell: sea level - ground, negative on
/// dry land, <see cref="Pit"/> where ground lies below sea level cut off from the sea. Kept off the GPU upload so the
/// pit rule is testable against the CPU's (TerrainShoreDepth).
/// </summary>
public sealed class ShoreDepthMap
{
    /// <summary>
    /// Depth marking pits cut off from the sea (ground below sea level, no sea water); the ocean never draws there
    /// (the shader discards below -500). Dry ground instead stores its signed depth, sea level − ground (negative).
    /// </summary>
    public const float Pit = -1000f;

    /// <summary>
    /// Cells (2 m) a below-sea-level column may lie from open sea, through other below-sea-level columns, and still count
    /// as sea. The sea flag reads the sea-level voxel layer, which is mostly solid where the ground sits just under the
    /// surface, so the shoreline band reads "dry"; without this it was discarded as a pit and left a hole at the beach.
    /// </summary>
    public const int ShoreReach = 4;

    /// <summary>Lowest signed depth stored for dry ground; anything higher above the sea never meets the surface.</summary>
    private const float MinLandDepth = -20f;

    private const int Cells = BathymetryGrid.Resolution * BathymetryGrid.Resolution;

    private readonly byte[] _reach = new byte[Cells];
    private readonly byte[] _reachScratch = new byte[Cells];
    private readonly int[] _queue = new int[Cells];

    /// <summary>
    /// Open-sea cells (1) and the below-sea-level dry cells within <see cref="ShoreReach"/> steps of them (2..), found
    /// breadth-first from the sea's edge. <paramref name="all"/> floods the whole grid; otherwise the reach moves with the
    /// grid's data and is recomputed only around the regions that changed.
    /// </summary>
    public void Update(BathymetryGrid grid, bool all)
    {
        if (all)
        {
            Flood(grid, Region.All, Region.All);
            return;
        }

        if (grid.Shift != default)
            BathymetryGrid.ShiftCells(_reach, grid.Shift.X, grid.Shift.Z, (byte)0);

        // A cell's reach depends only on cells within ShoreReach steps, so flooding the region grown by twice that and
        // keeping the result grown by once that is exact.
        foreach (var region in grid.ChangedRegions)
            Flood(grid, region.Expand(ShoreReach), region.Expand(2 * ShoreReach));
    }

    /// <summary>Signed depth of cell <paramref name="i"/>.</summary>
    public float Depth(BathymetryGrid grid, int i) => Depth(grid.Tops[i], grid.Beds[i], grid.Sea[i], grid.SeaLods[i], _reach[i]);

    /// <summary>Writes the signed depth of every cell in <paramref name="region"/> into <paramref name="depths"/> (indexed by cell).</summary>
    public void Fill(BathymetryGrid grid, in Region region, Span<Half> depths)
    {
        const int n = BathymetryGrid.Resolution;
        var tops = grid.Tops;
        var beds = grid.Beds;
        var sea = grid.Sea;
        var seaLods = grid.SeaLods;

        for (var z = region.Z0; z < region.Z1; z++)
        {
            for (var x = region.X0; x < region.X1; x++)
            {
                var i = x + z * n;
                depths[i] = (Half)Depth(tops[i], beds[i], sea[i], seaLods[i], _reach[i]);
            }
        }
    }

    private static float Depth(float top, float bed, BathymetryGrid.SeaState sea, byte seaLod, byte reach)
    {
        var ground = BathymetryGrid.Ground(top, bed, sea);

        if (float.IsNaN(ground))
            return BathymetryGrid.UnknownDepth;

        if (sea == BathymetryGrid.SeaState.Dry && ground < BathymetryGrid.SeaLevel - SeaVoxel(seaLod) && reach == 0)
            return Pit;

        return MathF.Max(MinLandDepth, BathymetryGrid.SeaLevel - ground);
    }

    /// <summary>
    /// Height of the voxel layer the sea flag was read from (2^lod m). Ground inside it can't hold a sea voxel, so a
    /// column only counts as a cut-off pit when its ground lies deeper than that.
    /// </summary>
    private static float SeaVoxel(byte lod) => lod == byte.MaxValue ? 1f : 1 << Math.Min((int)lod, 8);

    /// <summary>Breadth-first reach within <paramref name="area"/>; the result is kept inside <paramref name="keep"/>.</summary>
    private void Flood(BathymetryGrid grid, Region keep, Region area)
    {
        const int n = BathymetryGrid.Resolution;
        var tops = grid.Tops;
        var beds = grid.Beds;
        var sea = grid.Sea;
        var head = 0;
        var tail = 0;

        for (var z = area.Z0; z < area.Z1; z++)
        {
            var row = z * n;
            _reachScratch.AsSpan(row + area.X0, area.X1 - area.X0).Clear();

            for (var x = area.X0; x < area.X1; x++)
            {
                if (sea[row + x] == BathymetryGrid.SeaState.Dry)
                    continue;

                _reachScratch[row + x] = 1;
                _queue[tail++] = row + x;
            }
        }

        while (head < tail)
        {
            var i = _queue[head++];
            var steps = _reachScratch[i];
            if (steps > ShoreReach)
                continue;

            var x = i % n;
            var z = i / n;
            for (var k = 0; k < 4; k++)
            {
                var (nx, nz) = k switch
                {
                    0 => (x - 1, z),
                    1 => (x + 1, z),
                    2 => (x, z - 1),
                    _ => (x, z + 1),
                };

                if (nx < area.X0 || nx >= area.X1 || nz < area.Z0 || nz >= area.Z1)
                    continue;

                var j = nx + nz * n;
                if (_reachScratch[j] != 0 || sea[j] != BathymetryGrid.SeaState.Dry || !(BathymetryGrid.Ground(tops[j], beds[j], sea[j]) < BathymetryGrid.SeaLevel))
                    continue;

                _reachScratch[j] = (byte)(steps + 1);
                _queue[tail++] = j;
            }
        }

        for (var z = keep.Z0; z < keep.Z1; z++)
            _reachScratch.AsSpan(z * n + keep.X0, keep.X1 - keep.X0).CopyTo(_reach.AsSpan(z * n + keep.X0));
    }
}
