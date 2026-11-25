using System.Diagnostics;
using System.Numerics;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Fills the water fog volume (docs/water-fog.md) from the bathymetry: sea water from the bed up to sea level, solid under
/// the ground, air above.
/// </summary>
public sealed class BathymetryFog(BathymetryGrid grid)
{
    /// <summary>Open sea, packed RGBA8: clear turquoise, 1/e over 2 m along the eye's ray (alpha 32 of the volume's 4/m full extinction).</summary>
    private const uint Sea = 0x20_38_3D_0A;
    private const uint Air = 0;
    private const uint Solid = 0xFF000000u;

    /// <summary>Bathymetry cells (2 m) around a fog column within which a sea cell makes it sea.</summary>
    private const int SeaReach = 3;

    /// <summary>Grid cells a side a fog block's footprint covers: its 16 (plus one for the bilinear) and the reach on both sides.</summary>
    private const int FogCells = ChunkConstants.Size / 2 + 1 + 2 * SeaReach;

    /// <summary>Ground samples a side per block: its 16 cells and one more for the bilinear.</summary>
    private const int FogGroundSide = ChunkConstants.Size / 2 + 2;

    /// <summary>Main-thread time (ms) a frame's fill may spend before the rest waits for the next frame: a hitch is worse than a block of fog a few frames late.</summary>
    private const float FillBudgetMs = 0.5f;

    /// <summary>World y of the sea's top voxel layer.</summary>
    private static readonly int SeaTop = (int)MathF.Ceiling(BathymetryGrid.SeaLevel) - 1;

    private readonly float[] _ground = new float[FogGroundSide * FogGroundSide];
    private readonly int[] _solidTop = new int[ChunkConstants.Size * ChunkConstants.Size];
    private readonly int[] _waterTop = new int[ChunkConstants.Size * ChunkConstants.Size];
    private readonly uint[] _scratch = new uint[WaterFogVolume.BlockCells];
    private readonly bool[] _seaRaw = new bool[FogCells * FogCells];
    private readonly bool[] _seaDilated = new bool[FogCells * FogCells];
    private readonly int[] _keys = new int[WaterFogVolume.Slots];
    private int _keyVersion = -1;
    private (int X, int Y, int Z) _keyWindow;

    /// <summary>Where the fill left off round the window, so every stale block gets its turn under the per-frame budget.</summary>
    private int _cursor;

    /// <summary>The sea's colour and density as the fog volume's uniform takes it.</summary>
    public static Vector4 SeaColour => new Vector4(Sea & 0xFF, (Sea >> 8) & 0xFF, (Sea >> 16) & 0xFF, Sea >> 24) / 255f;

    /// <summary>Per frame, before the volume is recorded: the volume's stale blocks in its window, up to the budget.</summary>
    public void Fill(WaterFogVolume fog)
    {
        var start = Stopwatch.GetTimestamp();
        var budget = (long)(FillBudgetMs * Stopwatch.Frequency / 1000.0);

        // Each block's key: a hash of the grid cells under it (with the sea mask's reach), so a block is filled again only
        // when its own ground changed, not on every rebuild of the kilometre grid. Hashed once per rebuild.
        if (_keyVersion != grid.Version || _keyWindow != fog.WindowMin)
        {
            for (var index = 0; index < WaterFogVolume.Slots; index++)
                _keys[index] = Key(Block(fog.WindowMin, index));

            _keyVersion = grid.Version;
            _keyWindow = fog.WindowMin;
            fog.KeysPeakMs = MathF.Max(fog.KeysPeakMs, (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        for (var k = 0; k < WaterFogVolume.Slots; k++)
        {
            var index = (_cursor + k) % WaterFogVolume.Slots;
            var coord = Block(fog.WindowMin, index);
            var revision = _keys[index];

            if (!fog.Stale(coord.X, coord.Y, coord.Z, revision))
                continue;

            if (!fog.CanUpload || Stopwatch.GetTimestamp() - start > budget)
            {
                _cursor = index;
                break;
            }

            // Written to a managed scratch and copied once: the volume's staging is write-combined memory.
            var blockStart = Stopwatch.GetTimestamp();
            var cells = fog.Reserve(coord.X, coord.Y, coord.Z, revision);
            FillBlock(coord, _scratch);
            _scratch.AsSpan().CopyTo(cells);
            fog.BlockPeakMs = MathF.Max(fog.BlockPeakMs, (float)Stopwatch.GetElapsedTime(blockStart).TotalMilliseconds);
        }

        var ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        fog.Fills += fog.UploadsLastFrame;

        // Frames that filled something, once the code is warm (the first calls are the JIT's).
        if (fog.UploadsLastFrame > 0 && fog.Fills > 24)
        {
            fog.FillMs.Record(ms);

            if (ms > fog.FillPeakMs)
                (fog.FillPeakMs, fog.FillPeakUploads) = (ms, fog.UploadsLastFrame);
        }
    }

    /// <summary>The window's block at a slot index: x fastest, then y, then z.</summary>
    private static ChunkCoord Block((int X, int Y, int Z) min, int index) =>
        new(min.X + index % WaterFogVolume.BlocksX, min.Y + index / WaterFogVolume.BlocksX % WaterFogVolume.BlocksY, min.Z + index / (WaterFogVolume.BlocksX * WaterFogVolume.BlocksY));

    /// <summary>The grid cells a block reads: its columns' bilinear cells and the sea mask's reach around them, FogCells a side from (x, z).</summary>
    private (int X, int Z) Footprint(ChunkCoord coord)
    {
        var origin = coord.WorldOrigin;
        return ((int)MathF.Floor((origin.X - grid.Origin.X) / BathymetryGrid.CellSize - 0.5f) - SeaReach + 1,
            (int)MathF.Floor((origin.Z - grid.Origin.Y) / BathymetryGrid.CellSize - 0.5f) - SeaReach + 1);
    }

    /// <summary>A hash of the ground and sea state of the grid cells under a block.</summary>
    private int Key(ChunkCoord coord)
    {
        var (bx, bz) = Footprint(coord);
        var tops = grid.Tops;
        var beds = grid.Beds;
        var sea = grid.Sea;
        var h = 2166136261u;

        for (var j = 0; j < FogCells; j++)
        {
            var gz = Math.Clamp(bz + j, 0, BathymetryGrid.Resolution - 1) * BathymetryGrid.Resolution;

            for (var i = 0; i < FogCells; i++)
            {
                // The ground the fill reads (the bed under the sea, the top on the beach: a beach refined by streaming moves
                // its waterline) and the sea state.
                var g = Math.Clamp(bx + i, 0, BathymetryGrid.Resolution - 1) + gz;
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(BathymetryGrid.Ground(tops[g], beds[g], sea[g]))) * 16777619u;
                h = (h ^ (byte)sea[g]) * 16777619u;
            }
        }

        return (int)h;
    }

    /// <summary>
    /// A block from the bathymetry: per voxel column the ground (bilinear over the 2 m cells, so a beach slopes instead of
    /// stepping), solid under it, sea water from there up to the sea-level layer where the column is sea (an unknown bed
    /// is deep sea), the cell the ground crosses full sea water like the rest; air elsewhere. A roof over a beach counts as
    /// ground down to the sand.
    /// </summary>
    private void FillBlock(ChunkCoord coord, Span<uint> cells)
    {
        var origin = coord.WorldOrigin;
        var sea = grid.Sea;
        var raw = _seaRaw;
        var dilated = _seaDilated;
        var (bx, bz) = Footprint(coord);

        // A column is sea when any cell within SeaReach of it is: the mask marks a cell sea only where its y = -1 voxel is
        // open, so the last half metre of the beach's slope under sea level, and the run-up over it, several metres on a
        // gentle beach, read as dry and stepped along the cells. The ground still decides where the water is. Dilated in
        // two passes over the block's footprint of cells.
        for (var j = 0; j < FogCells; j++)
        {
            var gz = Math.Clamp(bz + j, 0, BathymetryGrid.Resolution - 1) * BathymetryGrid.Resolution;

            for (var i = 0; i < FogCells; i++)
                raw[i + j * FogCells] = sea[Math.Clamp(bx + i, 0, BathymetryGrid.Resolution - 1) + gz] == BathymetryGrid.SeaState.Sea;
        }

        for (var j = 0; j < FogCells; j++)
            for (var i = 0; i < FogCells; i++)
            {
                var any = false;
                for (var k = Math.Max(i - SeaReach + 1, 0); k <= Math.Min(i + SeaReach, FogCells - 1) && !any; k++)
                    any = raw[k + j * FogCells];

                dilated[i + j * FogCells] = any;
            }

        for (var i = 0; i < FogCells; i++)
            for (var j = 0; j < FogCells; j++)
            {
                var any = false;
                for (var k = Math.Max(j - SeaReach + 1, 0); k <= Math.Min(j + SeaReach, FogCells - 1) && !any; k++)
                    any = dilated[i + k * FogCells];

                raw[i + j * FogCells] = any;
            }

        // The ground at the footprint's cell centres once (SampleGround at a centre is that cell's ground), then bilinear
        // per column by hand: 289 grid reads for a block instead of 4 per column.
        var corners = _ground;
        var cx0 = bx + SeaReach - 1;
        var cz0 = bz + SeaReach - 1;

        for (var j = 0; j < FogGroundSide; j++)
            for (var i = 0; i < FogGroundSide; i++)
                corners[i + j * FogGroundSide] = grid.SampleGround(grid.Origin.X + (cx0 + i + 0.5f) * BathymetryGrid.CellSize, grid.Origin.Y + (cz0 + j + 0.5f) * BathymetryGrid.CellSize);

        // Per column: the last solid cell (its centre at or under the ground) and the top water cell (none off the sea);
        // the cells are then two integer compares each.
        var solidTop = _solidTop;
        var waterTop = _waterTop;
        var seaTopCell = SeaTop + 1;

        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            var fz = (origin.Z + z - grid.Origin.Y) / BathymetryGrid.CellSize - 0.5f - cz0;
            var iz = (int)MathF.Floor(fz);
            var tz = fz - iz;
            var rowSea = (int)MathF.Floor((origin.Z + z - grid.Origin.Y) / BathymetryGrid.CellSize - 0.5f) - bz;

            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var fx = (origin.X + x - grid.Origin.X) / BathymetryGrid.CellSize - 0.5f - cx0;
                var ix = (int)MathF.Floor(fx);
                var tx = fx - ix;
                var g = ix + iz * FogGroundSide;
                var ground = float.Lerp(float.Lerp(corners[g], corners[g + 1], tx), float.Lerp(corners[g + FogGroundSide], corners[g + FogGroundSide + 1], tx), tz);
                var isSea = raw[(int)MathF.Floor((origin.X + x - grid.Origin.X) / BathymetryGrid.CellSize - 0.5f) - bx + rowSea * FogCells];
                var c = x + z * ChunkConstants.Size;
                solidTop[c] = (int)MathF.Floor(ground - 0.5f);
                waterTop[c] = isSea ? seaTopCell : int.MinValue;
            }
        }

        var o = 0;
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            var row = z * ChunkConstants.Size;

            for (var y = 0; y < ChunkConstants.Size; y++)
            {
                var worldY = (int)origin.Y + y;

                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    var c = row + x;
                    cells[o++] = worldY <= solidTop[c] ? Solid : worldY <= waterTop[c] ? Sea : Air;
                }
            }
        }
    }
}
