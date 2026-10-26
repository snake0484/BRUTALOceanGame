using System.Numerics;
namespace BrutalGame.Terrain.Water;

[Flags]
public enum WaterFlags : byte
{
    None = 0,
    Solid = 1,
    Reservoir = 2,

    /// <summary>Simulated sea next to an edit: flows like any water, but is drawn by the ocean renderer (level offset).</summary>
    SeaZone = 4,
}

/// <summary>Block face, ordered so that <c>(Face)((int)f ^ 1)</c> is the opposite face.</summary>
public enum Face
{
    NegX,
    PosX,
    NegY,
    PosY,
    NegZ,
    PosZ,
}

/// <summary>
/// Flow sim state for one LOD-0 chunk (32³ interior + 1-cell halo, <see cref="ChunkConstants.Index"/> layout).
/// See docs/voxel-water.md "Flow simulation v1". Halo cells are written only through <see cref="WaterHalo"/>.
/// </summary>
public sealed class WaterBlock
{
    private const int S = ChunkConstants.Size;
    private const int P = ChunkConstants.Padded;
    private const int A = ChunkConstants.PaddedArea;
    private const byte ReservoirByte = 128;

    public ChunkCoord Coord;
    public float[] Water = new float[ChunkConstants.PaddedVolume];
    public float[] Next = new float[ChunkConstants.PaddedVolume];
    public readonly float[] OutScale = new float[ChunkConstants.PaddedVolume];
    public readonly byte[] Flags = new byte[ChunkConstants.PaddedVolume];
    public float MaxDelta;
    public bool BorderActive;
    public int QuietSteps;
    public bool Asleep;

    /// <summary>World y of the top sea voxel layer. Reservoir cells are held at their hydrostatic fill below it.</summary>
    public int ReservoirTopY = -1;

    /// <summary>Per padded row (y, z): any water in the row's 34 cells. Built by pass 1, reused by pass 2.</summary>
    internal readonly byte[] WetRows = new byte[ChunkConstants.PaddedArea];
    private byte _openFaces;

    public WaterBlock()
    {
        Reset(default);
    }

    /// <summary>Clears to an empty block whose halo is solid (no neighbours).</summary>
    public void Reset(ChunkCoord coord)
    {
        Coord = coord;
        Water.AsSpan().Clear();
        Next.AsSpan().Clear();
        OutScale.AsSpan().Fill(1f);
        Flags.AsSpan().Fill((byte)WaterFlags.Solid);
        WetRows.AsSpan().Clear();

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                Flags.AsSpan(ChunkConstants.Index(0, y, z), S).Clear();

        MaxDelta = 0f;
        BorderActive = false;
        QuietSteps = 0;
        Asleep = false;
        ReservoirTopY = -1;
        _openFaces = 0;
    }

    /// <summary>Chunk generated + ops replayed at LOD 0. Solid where density &lt; 0, Reservoir where byte water ≥ 128 (open, generated sea).</summary>
    public void InitFrom(VoxelChunk chunk, float seaLevel = 0f)
    {
        if (chunk.Coord.Lod != 0)
            throw new ArgumentException("Water blocks need a LOD-0 chunk.", nameof(chunk));

        Reset(chunk.Coord);
        ReservoirTopY = (int)MathF.Ceiling(seaLevel) - 1;
        ReadSolids(chunk, keepWater: false);
    }

    /// <summary>After an edit: recompute flags, keep water in open cells, zero solids, refill reservoirs. Keeps the InitFrom sea level.</summary>
    public void RefreshFrom(VoxelChunk chunk)
    {
        if (chunk.Coord.Lod != 0 || chunk.Coord != Coord)
            throw new ArgumentException("Refresh needs this block's LOD-0 chunk.", nameof(chunk));

        ReadSolids(chunk, keepWater: true);
        QuietSteps = 0;
        Asleep = false;
    }

    private void ReadSolids(VoxelChunk chunk, bool keepWater)
    {
        var density = chunk.Density;
        var source = chunk.Water;

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                var row = ChunkConstants.Index(0, y, z);
                var fill = ReservoirFill(y);

                for (var i = row; i < row + S; i++)
                {
                    var solid = density[i] < 0;
                    var reservoir = !solid && source[i] >= ReservoirByte;
                    Flags[i] = (byte)(solid ? WaterFlags.Solid : reservoir ? WaterFlags.Reservoir : WaterFlags.None);
                    Water[i] = reservoir ? fill : solid || !keepWater ? 0f : Water[i];
                }
            }
        }

        _openFaces = 0;

        for (var f = 0; f < 6; f++)
            if (ApronOpen(density, (Face)f))
                _openFaces |= (byte)(1 << f);
    }

    private bool ApronOpen(sbyte[] density, Face face)
    {
        Plane(face, halo: false, out var inner, out var su, out var sv);
        var outward = Outward(face);

        for (var v = 0; v < S; v++)
        {
            for (var u = 0; u < S; u++)
            {
                var i = inner + u * su + v * sv;

                if ((Flags[i] & (byte)WaterFlags.Solid) == 0 && density[i + outward] >= 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fill a reservoir cell in interior row y is held at: 1 at the top sea layer, +Compression per layer below.
    /// That is the vertical-pair equilibrium of WaterFlow, so sim water next to the sea levels with the sea surface
    /// instead of being pushed out by its own compression.
    /// </summary>
    public float ReservoirFill(int y) => 1f + WaterFlow.Compression * Math.Max(0, ReservoirTopY - (Coord.Y * S + y));

    /// <summary>Interior water volume (cells), summed in double.</summary>
    /// <summary>
    /// Inside the given discs (world x, world z, radius), hands open cells below sea level to the zone shallow-water sim:
    /// they become clamped sea for this automaton and are tagged <see cref="WaterFlags.SeaZone"/>.
    /// Must be re-applied after <see cref="RefreshFrom"/>, which re-derives flags from the voxel chunk.
    /// </summary>
    public void Unclamp(ReadOnlySpan<Vector3> discs)
    {
        const byte solid = (byte)WaterFlags.Solid;
        const byte reservoir = (byte)WaterFlags.Reservoir;
        const byte zone = (byte)WaterFlags.SeaZone;
        var origin = Coord.WorldOrigin;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var inside = false;
                foreach (var disc in discs)
                {
                    var dx = origin.X + x - disc.X;
                    var dz = origin.Z + z - disc.Y;
                    if (dx * dx + dz * dz <= disc.Z * disc.Z)
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                    continue;

                // Zone cells belong to the shallow-water zone sim; here they act as clamped sea (a source/sink for water
                // flowing out of the zone) and are never drawn by the voxel-water mesh.
                for (var y = 0; y < S; y++)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    if ((Flags[i] & solid) != 0)
                        continue;

                    var worldY = Coord.Y * S + y;
                    if (worldY >= ReservoirTopY + 1)
                    {
                        Flags[i] |= zone;
                        continue;
                    }

                    Flags[i] = (byte)(Flags[i] | reservoir | zone);
                    Water[i] = ReservoirFill(y);
                }
            }
        }
    }

    public float Mass()
    {
        var sum = 0.0;

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                var row = Water.AsSpan(ChunkConstants.Index(0, y, z), S);

                foreach (var w in row)
                    sum += w;
            }
        }

        return (float)sum;
    }

    /// <summary>Any interior cell on this face holds water.</summary>
    public bool AnyWaterOnFace(Face face)
    {
        Plane(face, halo: false, out var start, out var su, out var sv);

        for (var v = 0; v < S; v++)
            for (var u = 0; u < S; u++)
                if (Water[start + u * su + v * sv] > 0f)
                    return true;

        return false;
    }

    /// <summary>The chunk's terrain lets water leave through this face (an open border cell meets open apron). Set by InitFrom/RefreshFrom.</summary>
    public bool FaceOpen(Face face) => (_openFaces & (1 << (int)face)) != 0;

    /// <summary>Start index and the two in-plane strides of a face plane: the interior border layer, or the halo layer beyond it.</summary>
    internal static void Plane(Face face, bool halo, out int start, out int su, out int sv)
    {
        var positive = ((int)face & 1) != 0;
        var fixedCoord = positive ? (halo ? S : S - 1) : (halo ? -1 : 0);

        switch ((int)face >> 1)
        {
            case 0:
                start = ChunkConstants.Index(fixedCoord, 0, 0);
                su = P;
                sv = A;
                break;
            case 1:
                start = ChunkConstants.Index(0, fixedCoord, 0);
                su = 1;
                sv = A;
                break;
            default:
                start = ChunkConstants.Index(0, 0, fixedCoord);
                su = 1;
                sv = P;
                break;
        }
    }

    internal static int Outward(Face face)
    {
        var stride = ((int)face >> 1) switch { 0 => 1, 1 => P, _ => A };
        return ((int)face & 1) != 0 ? stride : -stride;
    }
}
