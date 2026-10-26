using System.Diagnostics;
using System.Numerics;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Seabed height and sea state per 2 m column around the camera, rebuilt from the ready terrain leaves of any LOD.
/// Depth = max(0, sea level - ground), the ground being the bed (the top at or below the sea-level voxel layer) where it
/// lies under sea level and the top otherwise (<see cref="Ground"/>). Tops and beds come from the finest leaf with a
/// surface in the column (max within that LOD); sea state comes from the finest leaf containing the sea-level voxel layer
/// (docs/voxel-water.md, docs/terrain-lod.md). Unknown cells are deep sea.
/// </summary>
public sealed class BathymetryGrid
{
    public const float CellSize = 2f;
    public const int Resolution = 512;
    public const float SeaLevel = 0f;
    public const float UnknownDepth = 50f;
    private const byte NoLod = byte.MaxValue;
    private const int MaxChanged = 32;

    public enum SeaState : byte
    {
        Unknown,
        Sea,
        Dry,
    }

    private readonly float[] _top = new float[Resolution * Resolution];
    private readonly float[] _bed = new float[Resolution * Resolution];
    private readonly SeaState[] _sea = new SeaState[Resolution * Resolution];
    private readonly byte[] _seaLod = new byte[Resolution * Resolution];

    // LOD of the leaf each top came from: the finest leaf with a surface in the column wins, so a coarse leaf stacked
    // above (whose quantised voxels can report a spurious surface near its bottom) never overrides the real seabed.
    private readonly byte[] _topLod = new byte[Resolution * Resolution];
    private readonly byte[] _bedLod = new byte[Resolution * Resolution];
    private readonly bool[] _simulated = new bool[Resolution * Resolution];
    private readonly float[] _zoneWeight = new float[Resolution * Resolution];

    /// <summary>Width (m) inside a disc edge over which the sim surface blends into the open-sea swell.</summary>
    public float ZoneBlendWidth = 5f;
    private readonly bool[] _zoneWet = new bool[Resolution * Resolution];
    private readonly float[] _zoneOffset = new float[Resolution * Resolution];
    private readonly float[] _zoneDepth = new float[Resolution * Resolution];

    private readonly float[] _zoneWhite = new float[Resolution * Resolution];
    private readonly List<Vector3> _discs = new();
    private readonly Region[] _changed = new Region[MaxChanged];

    // The cells of the region being re-merged as they were before (BeginRegion), so EndRegion reports only real changes.
    private readonly float[] _oldTop = new float[Resolution * Resolution];
    private readonly float[] _oldBed = new float[Resolution * Resolution];
    private readonly SeaState[] _oldSea = new SeaState[Resolution * Resolution];
    private readonly byte[] _oldSeaLod = new byte[Resolution * Resolution];
    private int _changedCount;
    private bool _centred;

    public BathymetryGrid()
    {
        Array.Fill(_top, float.NaN);
        Array.Fill(_bed, float.NaN);
    }

    /// <summary>World xz of cell (0, 0)'s corner.</summary>
    public Vector2 Origin { get; private set; }

    public float WorldSize => Resolution * CellSize;

    public bool Dirty { get; set; } = true;

    /// <summary>
    /// What changed since <see cref="ClearChanged"/>: the regions re-merged from terrain or zone discs (always tracked),
    /// whether everything must be re-uploaded anyway (a re-centre moved the data), and the cells the data moved by
    /// (new cell x holds what old cell x + Shift.X held).
    /// </summary>
    public bool ChangedAll { get; private set; } = true;

    public ReadOnlySpan<Region> ChangedRegions => _changed.AsSpan(0, _changedCount);

    public (int X, int Z) Shift { get; private set; }

    /// <summary>Topmost terrain surface per cell, overhangs and roofs included (what rain stops on).</summary>
    public ReadOnlySpan<float> Tops => _top;

    /// <summary>Topmost terrain surface per cell at or below the sea-level voxel layer; see <see cref="Ground"/>.</summary>
    public ReadOnlySpan<float> Beds => _bed;

    /// <summary>
    /// The ground the sea sees in a cell: the bed where it lies under sea level, so land overhead (an overhang, an arch, a
    /// placed rock) never hides the water beneath it, and otherwise the top (land rising through sea level). A sea cell
    /// whose bed hasn't arrived yet has an unknown seabed: NaN, read as deep.
    /// </summary>
    public static float Ground(float top, float bed, SeaState sea) =>
        bed < SeaLevel ? bed : float.IsNaN(bed) && sea == SeaState.Sea ? float.NaN : top;

    public ReadOnlySpan<SeaState> Sea => _sea;

    /// <summary>LOD of the leaf each cell's sea state came from (byte.MaxValue = none); its voxels are 2^lod m.</summary>
    public ReadOnlySpan<byte> SeaLods => _seaLod;

    /// <summary>Cells whose sea is run by the voxel water sim (docs/voxel-water.md). The ocean draws them at the sim's level.</summary>
    public ReadOnlySpan<bool> Simulated => _simulated;

    /// <summary>0..1 blend from swell to sim surface: 1 deep inside a zone, ramping to 0 at its edge.</summary>
    public ReadOnlySpan<float> ZoneWeight => _zoneWeight;

    public ReadOnlySpan<bool> ZoneWet => _zoneWet;

    /// <summary>Absolute water surface height (m) of sea-zone cells, from the zone flow sim.</summary>
    public ReadOnlySpan<float> ZoneOffset => _zoneOffset;

    public ReadOnlySpan<float> ZoneDepth => _zoneDepth;

    /// <summary>Foam 0..1 for sea-zone cells, from the zone flow sim (pours, fast flow), advected and settling.</summary>
    public ReadOnlySpan<float> ZoneWhite => _zoneWhite;

    /// <summary>Zone levels changed inside <see cref="TakeLevelRect"/>; cheaper to upload than full <see cref="Dirty"/> changes.</summary>
    public bool LevelsDirty { get; private set; }

    private int _levelMinX = Resolution;
    private int _levelMinZ = Resolution;
    private int _levelMaxX = -1;
    private int _levelMaxZ = -1;

    public float LastRebuildMs { get; private set; }

    /// <summary>
    /// Re-centres on the camera when it drifts. The seabed data moves with the grid by whole chunks, so only the strips
    /// that moved in from outside (unknown until the caller rebuilds them) are new; (dx, dz) is the shift in cells, and
    /// Resolution or more on the first call. Zone state is re-stamped from the discs. Returns false if nothing moved.
    /// </summary>
    public bool Recenter(Vector3 cameraPos, out int dx, out int dz)
    {
        var half = WorldSize * 0.5f;
        var centre = Origin + new Vector2(half);
        (dx, dz) = (0, 0);

        if (MathF.Abs(cameraPos.X - centre.X) < WorldSize * 0.125f && MathF.Abs(cameraPos.Z - centre.Y) < WorldSize * 0.125f && _centred)
            return false;

        const float snap = ChunkConstants.Size;
        var origin = new Vector2(MathF.Floor((cameraPos.X - half) / snap) * snap, MathF.Floor((cameraPos.Z - half) / snap) * snap);
        dx = _centred ? (int)MathF.Round((origin.X - Origin.X) / CellSize) : Resolution;
        dz = _centred ? (int)MathF.Round((origin.Y - Origin.Y) / CellSize) : Resolution;
        Origin = origin;
        _centred = true;

        // Everything here is anchored to the world, so it all moves with the grid; discs reaching into the strips that
        // came in are stamped again.
        ShiftCells(_top, dx, dz, float.NaN);
        ShiftCells(_bed, dx, dz, float.NaN);
        ShiftCells(_sea, dx, dz, SeaState.Unknown);
        ShiftCells(_seaLod, dx, dz, NoLod);
        ShiftCells(_topLod, dx, dz, NoLod);
        ShiftCells(_bedLod, dx, dz, NoLod);
        ShiftCells(_simulated, dx, dz, false);
        ShiftCells(_zoneWeight, dx, dz, 0f);
        ShiftCells(_zoneWet, dx, dz, false);
        ShiftCells(_zoneOffset, dx, dz, 0f);
        ShiftCells(_zoneDepth, dx, dz, 0f);
        ShiftCells(_zoneWhite, dx, dz, 0f);
        Shift = (Shift.X + dx, Shift.Z + dz);
        Dirty = true;

        if (Math.Abs(Shift.X) >= Resolution || Math.Abs(Shift.Z) >= Resolution)
            ChangedAll = true;

        foreach (var disc in _discs)
        {
            StampDisc(disc);
            MarkChanged(DiscRegion(disc));
        }

        return true;
    }

    /// <summary>
    /// Moves a per-cell array (<paramref name="stride"/> values per cell) by (dx, dz) cells: new [x, z] = old
    /// [x + dx, z + dz]; cells from outside get fill.
    /// </summary>
    public static void ShiftCells<T>(T[] cells, int dx, int dz, T fill, int stride = 1)
    {
        const int n = Resolution;
        if (Math.Abs(dx) >= n || Math.Abs(dz) >= n)
        {
            Array.Fill(cells, fill);
            return;
        }

        var width = n * stride;
        var keep = (n - Math.Abs(dx)) * stride;
        for (var k = 0; k < n; k++)
        {
            // Rows move toward lower z when dz > 0, so walk in that direction to read each source row before it is overwritten.
            var z = dz >= 0 ? k : n - 1 - k;
            var row = cells.AsSpan(z * width, width);
            var from = z + dz;

            if ((uint)from >= n)
            {
                row.Fill(fill);
                continue;
            }

            cells.AsSpan(from * width + Math.Max(dx, 0) * stride, keep).CopyTo(row.Slice(Math.Max(-dx, 0) * stride, keep));
            row.Slice(dx >= 0 ? keep : 0, width - keep).Fill(fill);
        }
    }

    /// <summary>The strips a shift of (dx, dz) cells brought in from outside (up to two; the rest of the span is unused).</summary>
    public static int Exposed(int dx, int dz, Span<Region> strips)
    {
        const int n = Resolution;
        var count = 0;

        if (dx > 0)
            strips[count++] = new Region(n - dx, 0, n, n);
        else if (dx < 0)
            strips[count++] = new Region(0, 0, -dx, n);

        if (dz > 0)
            strips[count++] = new Region(0, n - dz, n, n);
        else if (dz < 0)
            strips[count++] = new Region(0, 0, n, -dz);

        return count;
    }

    /// <summary>The cells the texture must upload are now on the GPU.</summary>
    public void ClearChanged()
    {
        ChangedAll = false;
        _changedCount = 0;
        Shift = default;
        Dirty = false;
    }

    /// <summary>Adds a changed region, merging it into one it touches when that doesn't grow the area much.</summary>
    private void MarkChanged(in Region region)
    {
        Dirty = true;
        if (region.Empty)
            return;

        for (var i = 0; i < _changedCount; i++)
        {
            var other = _changed[i];
            var union = other.Union(region);
            if (other.Touches(region) && union.Area <= 5 * (other.Area + region.Area) / 4)
            {
                _changed[i] = union;
                return;
            }
        }

        if (_changedCount < MaxChanged)
        {
            _changed[_changedCount++] = region;
            return;
        }

        var bounds = region;
        for (var i = 0; i < _changedCount; i++)
            bounds = bounds.Union(_changed[i]);

        _changed[0] = bounds;
        _changedCount = 1;
    }

    public void BeginRebuild()
    {
        ChangedAll = true;
        Dirty = true;
        Array.Fill(_top, float.NaN);
        Array.Fill(_bed, float.NaN);
        Array.Clear(_sea);
        Array.Fill(_seaLod, NoLod);
        Array.Fill(_topLod, NoLod);
        Array.Fill(_bedLod, NoLod);
    }

    /// <summary>Cell rectangle [x0, x1) × [z0, z1).</summary>
    public readonly record struct Region(int X0, int Z0, int X1, int Z1)
    {
        public static readonly Region All = new(0, 0, Resolution, Resolution);

        public bool Empty => X1 <= X0 || Z1 <= Z0;

        public bool Intersects(in Region o) => X0 < o.X1 && o.X0 < X1 && Z0 < o.Z1 && o.Z0 < Z1;

        /// <summary>Overlaps or shares an edge.</summary>
        public bool Touches(in Region o) => X0 <= o.X1 && o.X0 <= X1 && Z0 <= o.Z1 && o.Z0 <= Z1;

        public int Area => Empty ? 0 : (X1 - X0) * (Z1 - Z0);

        /// <summary>Grown by margin cells on every side, clamped to the grid.</summary>
        public Region Expand(int margin) =>
            new(Math.Max(0, X0 - margin), Math.Max(0, Z0 - margin), Math.Min(Resolution, X1 + margin), Math.Min(Resolution, Z1 + margin));

        /// <summary>Moved with the data by a re-centre shift (see <see cref="ShiftCells"/>), clamped to the grid.</summary>
        public Region Shifted(int dx, int dz) =>
            new(Math.Max(0, X0 - dx), Math.Max(0, Z0 - dz), Math.Min(Resolution, X1 - dx), Math.Min(Resolution, Z1 - dz));

        public Region Union(in Region o) => new(Math.Min(X0, o.X0), Math.Min(Z0, o.Z0), Math.Max(X1, o.X1), Math.Max(Z1, o.Z1));
    }

    /// <summary>The cells a leaf covers, clamped to the grid.</summary>
    public Region CellsOf(ChunkCoord coord)
    {
        var extent = ChunkConstants.Size * coord.VoxelScale;
        var origin = coord.WorldOrigin;
        return new Region(
            Math.Max(0, (int)MathF.Floor((origin.X - Origin.X) / CellSize)),
            Math.Max(0, (int)MathF.Floor((origin.Z - Origin.Y) / CellSize)),
            Math.Min(Resolution, (int)MathF.Ceiling((origin.X + extent - Origin.X) / CellSize)),
            Math.Min(Resolution, (int)MathF.Ceiling((origin.Z + extent - Origin.Y) / CellSize)));
    }

    /// <summary>
    /// Starts a partial rebuild: clears only the cells in <paramref name="region"/>; merge the leaves over it next, then
    /// call <see cref="EndRegion"/>.
    /// </summary>
    public void BeginRegion(in Region region)
    {
        for (var z = region.Z0; z < region.Z1; z++)
        {
            var row = region.X0 + z * Resolution;
            var count = region.X1 - region.X0;
            _top.AsSpan(row, count).CopyTo(_oldTop.AsSpan(row, count));
            _bed.AsSpan(row, count).CopyTo(_oldBed.AsSpan(row, count));
            _sea.AsSpan(row, count).CopyTo(_oldSea.AsSpan(row, count));
            _seaLod.AsSpan(row, count).CopyTo(_oldSeaLod.AsSpan(row, count));
            _top.AsSpan(row, count).Fill(float.NaN);
            _bed.AsSpan(row, count).Fill(float.NaN);
            _sea.AsSpan(row, count).Clear();
            _seaLod.AsSpan(row, count).Fill(NoLod);
            _topLod.AsSpan(row, count).Fill(NoLod);
            _bedLod.AsSpan(row, count).Fill(NoLod);
        }
    }

    /// <summary>
    /// Ends a partial rebuild: marks changed only the cells whose top, bed, sea state or sea LOD differ from before. A
    /// coarse leaf swapped for its children far away usually changes nothing, and then nothing is uploaded.
    /// </summary>
    public void EndRegion(in Region region)
    {
        var (x0, z0, x1, z1) = (Resolution, Resolution, 0, 0);

        for (var z = region.Z0; z < region.Z1; z++)
        {
            for (var x = region.X0; x < region.X1; x++)
            {
                var i = x + z * Resolution;
                if (Same(_top[i], _oldTop[i]) && Same(_bed[i], _oldBed[i]) && _sea[i] == _oldSea[i] && _seaLod[i] == _oldSeaLod[i])
                    continue;

                (x0, z0, x1, z1) = (Math.Min(x0, x), Math.Min(z0, z), Math.Max(x1, x + 1), Math.Max(z1, z + 1));
            }
        }

        MarkChanged(new Region(x0, z0, x1, z1));
    }

    private static bool Same(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

    /// <summary>Folds one leaf's per-voxel-column tops, beds and sea flags into the cells it covers (within <paramref name="region"/>).</summary>
    public void MergeLeaf(ChunkCoord coord, ReadOnlySpan<float> tops, ReadOnlySpan<float> beds, ReadOnlySpan<bool> sea, bool hasSeaLayer, in Region region)
    {
        const int size = ChunkConstants.Size;
        var scale = coord.VoxelScale;
        var origin = coord.WorldOrigin;
        var cells = CellsOf(coord);

        var x0 = Math.Max(cells.X0, region.X0);
        var z0 = Math.Max(cells.Z0, region.Z0);
        var x1 = Math.Min(cells.X1, region.X1);
        var z1 = Math.Min(cells.Z1, region.Z1);

        for (var gz = z0; gz < z1; gz++)
        {
            var vz = Math.Clamp((int)((Origin.Y + (gz + 0.5f) * CellSize - origin.Z) / scale), 0, size - 1);

            for (var gx = x0; gx < x1; gx++)
            {
                var vx = Math.Clamp((int)((Origin.X + (gx + 0.5f) * CellSize - origin.X) / scale), 0, size - 1);
                var column = vx + vz * size;
                var cell = gx + gz * Resolution;

                var top = tops[column];
                if (!float.IsNaN(top) && (coord.Lod < _topLod[cell] || (coord.Lod == _topLod[cell] && top > _top[cell])))
                {
                    _top[cell] = top;
                    _topLod[cell] = (byte)coord.Lod;
                }

                var bed = beds[column];
                if (!float.IsNaN(bed) && (coord.Lod < _bedLod[cell] || (coord.Lod == _bedLod[cell] && bed > _bed[cell])))
                {
                    _bed[cell] = bed;
                    _bedLod[cell] = (byte)coord.Lod;
                }

                if (hasSeaLayer && coord.Lod <= _seaLod[cell])
                {
                    _seaLod[cell] = (byte)coord.Lod;
                    _sea[cell] = sea[column] ? SeaState.Sea : SeaState.Dry;
                }
            }
        }
    }

    /// <param name="elapsedMs">Total time of the (possibly multi-region) rebuild, measured by the caller.</param>
    public void EndRebuild(double elapsedMs)
    {
        Version++;
        LastRebuildMs = (float)elapsedMs;
    }

    /// <summary>Marks a disc (world x, world z, radius) as simulated sea.</summary>
    public void AddSimulatedDisc(float x, float z, float radius)
    {
        var disc = new Vector3(x, z, radius);
        _discs.Add(disc);
        StampDisc(disc);
        MarkChanged(DiscRegion(disc));
    }

    private Region DiscRegion(Vector3 disc)
    {
        var cells = CellsInDisc(disc.X, disc.Y, disc.Z);
        return new Region(cells.MinX, cells.MinZ, cells.MaxX + 1, cells.MaxZ + 1);
    }

    /// <summary>Hands a disc back to the open sea: clears the zone state under it and re-stamps the remaining discs.</summary>
    public void RemoveSimulatedDisc(Vector3 disc)
    {
        if (!_discs.Remove(disc))
            return;

        var cells = CellsInDisc(disc.X, disc.Y, disc.Z);
        for (var gz = cells.MinZ; gz <= cells.MaxZ; gz++)
        {
            var row = cells.MinX + gz * Resolution;
            var count = cells.MaxX - cells.MinX + 1;
            _simulated.AsSpan(row, count).Clear();
            _zoneWeight.AsSpan(row, count).Clear();
            _zoneWet.AsSpan(row, count).Clear();
            _zoneWhite.AsSpan(row, count).Clear();
        }

        foreach (var other in _discs)
            StampDisc(other);

        MarkChanged(DiscRegion(disc));
    }

    private void StampDisc(Vector3 disc)
    {
        var cells = CellsInDisc(disc.X, disc.Y, disc.Z);
        for (var gz = cells.MinZ; gz <= cells.MaxZ; gz++)
            for (var gx = cells.MinX; gx <= cells.MaxX; gx++)
            {
                var distance = Vector2.Distance(CellCentre(gx, gz), new Vector2(disc.X, disc.Y));
                if (distance > disc.Z)
                    continue;

                var i = gx + gz * Resolution;
                _simulated[i] = true;
                _zoneWeight[i] = MathF.Max(_zoneWeight[i], Math.Clamp((disc.Z - distance) / ZoneBlendWidth, 0f, 1f));
            }
    }

    public (int MinX, int MinZ, int MaxX, int MaxZ) CellsInDisc(float x, float z, float radius) =>
    (
        Math.Max(0, (int)MathF.Floor((x - radius - Origin.X) / CellSize)),
        Math.Max(0, (int)MathF.Floor((z - radius - Origin.Y) / CellSize)),
        Math.Min(Resolution - 1, (int)MathF.Floor((x + radius - Origin.X) / CellSize)),
        Math.Min(Resolution - 1, (int)MathF.Floor((z + radius - Origin.Y) / CellSize))
    );

    public Vector2 CellCentre(int gx, int gz) => Origin + new Vector2((gx + 0.5f) * CellSize, (gz + 0.5f) * CellSize);

    /// <summary>Cells whose zone level, depth or foam changed since the last call (empty when Max &lt; Min); clears it.</summary>
    public (int MinX, int MinZ, int MaxX, int MaxZ) TakeLevelRect()
    {
        var rect = (_levelMinX, _levelMinZ, _levelMaxX, _levelMaxZ);
        (_levelMinX, _levelMinZ, _levelMaxX, _levelMaxZ) = (Resolution, Resolution, -1, -1);
        LevelsDirty = false;
        return rect;
    }

    private void MarkLevel(int gx, int gz)
    {
        _levelMinX = Math.Min(_levelMinX, gx);
        _levelMinZ = Math.Min(_levelMinZ, gz);
        _levelMaxX = Math.Max(_levelMaxX, gx);
        _levelMaxZ = Math.Max(_levelMaxZ, gz);
        LevelsDirty = true;
    }

    /// <param name="surface">Absolute water surface height from the zone sim (m).</param>
    public void SetZoneLevel(int gx, int gz, float surface, float depth, float foam)
    {
        var i = gx + gz * Resolution;
        _zoneWet[i] = true;
        _zoneOffset[i] = surface;
        _zoneDepth[i] = depth;
        _zoneWhite[i] = foam;
        MarkLevel(gx, gz);
    }

    /// <param name="surface">Where to place the (discarded) surface: below the ground and no higher than the water beside it, so interpolation dives under the ground rather than climbing a wall.</param>
    public void SetZoneDry(int gx, int gz, float surface)
    {
        var i = gx + gz * Resolution;
        if (!_zoneWet[i] && _zoneOffset[i] == surface)
            return;

        _zoneWet[i] = false;
        _zoneOffset[i] = surface;
        _zoneWhite[i] = 0f;
        MarkLevel(gx, gz);
    }

    /// <summary>Foam that has drifted out of a zone onto the open sea around it (drawn on the ocean's own surface).</summary>
    public void SetSpillFoam(int gx, int gz, float foam)
    {
        var i = gx + gz * Resolution;
        if (_simulated[i] || MathF.Abs(_zoneWhite[i] - foam) < 1e-3f)
            return;

        _zoneWhite[i] = foam;
        MarkLevel(gx, gz);
    }

    /// <summary>Terrain top at world xz, bilinear over the 2 m cells. Unknown cells read as deep seabed.</summary>
    public float SampleTop(float x, float z)
    {
        var fx = (x - Origin.X) / CellSize - 0.5f;
        var fz = (z - Origin.Y) / CellSize - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var z0 = (int)MathF.Floor(fz);
        var tx = fx - x0;
        var tz = fz - z0;
        return float.Lerp(float.Lerp(TopAt(x0, z0), TopAt(x0 + 1, z0), tx), float.Lerp(TopAt(x0, z0 + 1), TopAt(x0 + 1, z0 + 1), tx), tz);
    }

    private float TopAt(int gx, int gz)
    {
        gx = Math.Clamp(gx, 0, Resolution - 1);
        gz = Math.Clamp(gz, 0, Resolution - 1);
        var top = _top[gx + gz * Resolution];
        return float.IsNaN(top) ? SeaLevel - UnknownDepth : top;
    }

    /// <summary>The ground the sea sees (<see cref="Ground"/>) at world xz, bilinear over the 2 m cells. Unknown cells read as deep seabed.</summary>
    public float SampleGround(float x, float z)
    {
        var fx = (x - Origin.X) / CellSize - 0.5f;
        var fz = (z - Origin.Y) / CellSize - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var z0 = (int)MathF.Floor(fz);
        var tx = fx - x0;
        var tz = fz - z0;
        return float.Lerp(float.Lerp(GroundAt(x0, z0), GroundAt(x0 + 1, z0), tx), float.Lerp(GroundAt(x0, z0 + 1), GroundAt(x0 + 1, z0 + 1), tx), tz);
    }

    private float GroundAt(int gx, int gz)
    {
        var i = Math.Clamp(gx, 0, Resolution - 1) + Math.Clamp(gz, 0, Resolution - 1) * Resolution;
        var ground = Ground(_top[i], _bed[i], _sea[i]);
        return float.IsNaN(ground) ? SeaLevel - UnknownDepth : ground;
    }

    /// <summary>Bumped on every rebuild, so the zone sim knows when to re-read the bed.</summary>
    public int Version { get; private set; }

    public bool IsSea(float x, float z)
    {
        var gx = (int)MathF.Floor((x - Origin.X) / CellSize);
        var gz = (int)MathF.Floor((z - Origin.Y) / CellSize);

        if ((uint)gx >= Resolution || (uint)gz >= Resolution)
            return true;

        return _sea[gx + gz * Resolution] != SeaState.Dry;
    }

    public float SampleDepth(float x, float z)
    {
        var gx = (int)MathF.Floor((x - Origin.X) / CellSize);
        var gz = (int)MathF.Floor((z - Origin.Y) / CellSize);

        if ((uint)gx >= Resolution || (uint)gz >= Resolution)
            return UnknownDepth;

        var i = gx + gz * Resolution;
        var ground = Ground(_top[i], _bed[i], _sea[i]);
        return float.IsNaN(ground) ? UnknownDepth : MathF.Max(0f, SeaLevel - ground);
    }
}
