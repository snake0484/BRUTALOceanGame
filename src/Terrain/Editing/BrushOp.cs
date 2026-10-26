using System.Numerics;

namespace BrutalGame.Terrain;

public enum BrushMode : byte
{
    Dig,
    Place,
}

/// <summary>
/// One sphere edit in metres. Edits are stored as ops and replayed after procedural generation at any LOD,
/// so chunks need no persistent voxel storage and aprons stay consistent across chunk borders.
/// </summary>
public readonly record struct BrushOp(Vector3 Centre, float Radius, BrushMode Mode, VoxelMaterial Material)
{
    /// <summary>Distance in voxels beyond the radius that still changes the clamped SDF (±127 / DensityScale).</summary>
    public const float Falloff = 127f / ChunkConstants.DensityScale;

    /// <summary>Reach in metres at LOD 0.</summary>
    public float Reach => Radius + Falloff;

    /// <summary>Reach in metres for chunks of the given voxel size.</summary>
    public float ReachAt(float voxelScale) => Radius + Falloff * voxelScale;
}

public static class VoxelEditor
{
    /// <summary>Writes LOD-0 chunk coords whose padded volume the op touches. Returns how many were written.</summary>
    public static int AffectedChunks(in BrushOp op, Span<ChunkCoord> output) => AffectedChunks(op, 0, output);

    /// <summary>Writes chunk coords at <paramref name="lod"/> whose padded volume the op touches. Returns how many were written.</summary>
    public static int AffectedChunks(in BrushOp op, int lod, Span<ChunkCoord> output)
    {
        var s = ChunkConstants.VoxelSize * (1 << lod);
        var size = ChunkConstants.Size * s;
        var pad = new Vector3(op.ReachAt(s) + s);
        var min = (op.Centre - pad) / size;
        var max = (op.Centre + pad) / size;
        var yMin = Math.Max((int)MathF.Floor(min.Y), ChunkConstants.MinChunkY >> lod);
        var yMax = Math.Min((int)MathF.Floor(max.Y), ChunkConstants.MaxChunkY >> lod);
        var count = 0;

        for (var z = (int)MathF.Floor(min.Z); z <= (int)MathF.Floor(max.Z); z++)
        for (var y = yMin; y <= yMax; y++)
        for (var x = (int)MathF.Floor(min.X); x <= (int)MathF.Floor(max.X); x++)
        {
            if (count == output.Length)
                return count;

            output[count++] = new ChunkCoord(x, y, z, lod);
        }

        return count;
    }

    /// <summary>True if the op's reach sphere touches the chunk's padded volume (apron included).</summary>
    public static bool Intersects(in BrushOp op, ChunkCoord coord)
    {
        var s = coord.VoxelScale;
        var min = coord.WorldOrigin - new Vector3(s);
        var max = min + new Vector3((ChunkConstants.Padded - 1) * s);
        var reach = op.ReachAt(s);
        var d = op.Centre - Vector3.Clamp(op.Centre, min, max);
        return d.LengthSquared() <= reach * reach;
    }

    /// <summary>Replays ops over a freshly generated chunk of any LOD (padded volume included) and refreshes HasSurface.</summary>
    public static void Apply(VoxelChunk chunk, ReadOnlySpan<BrushOp> ops)
    {
        if (ops.IsEmpty)
            return;

        foreach (var op in ops)
            ApplyOne(chunk, op);

        chunk.RecomputeHasSurface();
        chunk.Version++;
    }

    private static void ApplyOne(VoxelChunk chunk, in BrushOp op)
    {
        var s = chunk.Coord.VoxelScale;
        var local = (op.Centre - chunk.Coord.WorldOrigin) / s;
        var radius = op.Radius / s;
        var reach = radius + BrushOp.Falloff;

        var x0 = Math.Max(-1, (int)MathF.Floor(local.X - reach));
        var y0 = Math.Max(-1, (int)MathF.Floor(local.Y - reach));
        var z0 = Math.Max(-1, (int)MathF.Floor(local.Z - reach));
        var x1 = Math.Min(ChunkConstants.Size, (int)MathF.Ceiling(local.X + reach));
        var y1 = Math.Min(ChunkConstants.Size, (int)MathF.Ceiling(local.Y + reach));
        var z1 = Math.Min(ChunkConstants.Size, (int)MathF.Ceiling(local.Z + reach));

        var density = chunk.Density;
        var material = chunk.Material;
        var water = chunk.Water;
        const float scale = ChunkConstants.DensityScale;
        var dig = op.Mode == BrushMode.Dig;

        for (var z = z0; z <= z1; z++)
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var d = new Vector3(x, y, z) - local;
            var distance = d.Length();
            var i = ChunkConstants.Index(x, y, z);
            var old = density[i];

            if (dig)
            {
                var value = Quantise((radius - distance) * scale);

                if (value > old)
                    density[i] = value;

                // Water is left alone: carved former solid already holds none (so it starts dry until the flow sim
                // refills it), and existing open water the brush overlaps must not be cut away.

                continue;
            }

            var solid = Quantise((distance - radius) * scale);

            if (solid >= old)
                continue;

            density[i] = solid;

            if (solid < 0)
                water[i] = 0;

            if (solid < 0 && (old >= 0 || distance < radius))
                material[i] = (byte)op.Material;
        }
    }

    private static sbyte Quantise(float value) => (sbyte)Math.Clamp(MathF.Round(value), -127f, 127f);
}
