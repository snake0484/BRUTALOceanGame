namespace BrutalGame.Terrain;

/// <summary>
/// One sphere edit on a planet: the edit, network and persistence unit. The centre is stored as int64 at 1/1024 m in
/// the planet frame (review C13), so replay is identical on every peer; double only at runtime. <see cref="Seq"/> is the
/// server's replay order, <see cref="Author"/> who made it.
/// </summary>
public readonly record struct EditOp(long X, long Y, long Z, float Radius, BrushMode Mode, VoxelMaterial Material, uint Seq = 0, ushort Author = 0)
{
    public const double UnitsPerMetre = 1024.0;
    public const double Unit = 1.0 / UnitsPerMetre;

    /// <summary>Bytes in the stored form.</summary>
    public const int StoredSize = 3 * sizeof(long) + sizeof(float) + 2 + sizeof(uint) + sizeof(ushort);

    public static long Quantise(double metres) => (long)Math.Round(metres * UnitsPerMetre, MidpointRounding.ToEven);

    public static EditOp At(double x, double y, double z, float radius, BrushMode mode, VoxelMaterial material, ushort author = 0) =>
        new(Quantise(x), Quantise(y), Quantise(z), radius, mode, material, 0, author);

    public void Centre(out double x, out double y, out double z)
    {
        x = X * Unit;
        y = Y * Unit;
        z = Z * Unit;
    }

    /// <summary>Reach in metres for chunks of the given voxel size.</summary>
    public float ReachAt(double voxelSize) => Radius + BrushOp.Falloff * (float)voxelSize;

    public void Write(BinaryWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
        writer.Write(Z);
        writer.Write(Radius);
        writer.Write((byte)Mode);
        writer.Write((byte)Material);
        writer.Write(Seq);
        writer.Write(Author);
    }

    public static EditOp Read(BinaryReader reader) => new(
        reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadSingle(),
        (BrushMode)reader.ReadByte(), (VoxelMaterial)reader.ReadByte(), reader.ReadUInt32(), reader.ReadUInt16());
}

/// <summary>Replays <see cref="EditOp"/>s over a generated planet chunk, clamping removal on protected voxels (D30).</summary>
public static class PlanetEditor
{
    /// <summary>True if the op's reach sphere touches the chunk's padded volume (apron included).</summary>
    public static bool Intersects(in EditOp op, in ChunkCoord coord)
    {
        var s = coord.VoxelSize;
        var size = ChunkConstants.Size * s;
        op.Centre(out var cx, out var cy, out var cz);
        var reach = op.ReachAt(s);
        var dx = Outside(cx, coord.X * size - s, coord.X * size + size);
        var dy = Outside(cy, coord.Y * size - s, coord.Y * size + size);
        var dz = Outside(cz, coord.Z * size - s, coord.Z * size + size);
        return dx * dx + dy * dy + dz * dz <= (double)reach * reach;
    }

    /// <summary>
    /// Replays ops in order. With <paramref name="protection"/> holding protected voxels (a masked generate), Dig can't
    /// take a protected voxel past its generated density: d = max(d_old, min(d_dig, d_gen)). Placed material there can
    /// still be removed back to the generated ground. Refreshes HasSurface.
    /// </summary>
    public static void Apply(VoxelChunk chunk, ReadOnlySpan<EditOp> ops, PlanetChunkScratch? protection = null)
    {
        if (ops.IsEmpty)
            return;

        var clamp = protection is { ProtectedCount: > 0 } ? protection : null;

        foreach (var op in ops)
            ApplyOne(chunk, op, clamp);

        chunk.RecomputeHasSurface();
        chunk.Version++;
    }

    private static double Outside(double v, double lo, double hi) => v < lo ? lo - v : v > hi ? v - hi : 0.0;

    private static void ApplyOne(VoxelChunk chunk, in EditOp op, PlanetChunkScratch? clamp)
    {
        var coord = chunk.Coord;
        var s = coord.VoxelSize;
        var size = ChunkConstants.Size * s;
        op.Centre(out var cx, out var cy, out var cz);
        var lx = (float)((cx - coord.X * size) / s);
        var ly = (float)((cy - coord.Y * size) / s);
        var lz = (float)((cz - coord.Z * size) / s);
        var radius = (float)(op.Radius / s);
        var reach = radius + BrushOp.Falloff;

        var x0 = Math.Max(-1, (int)MathF.Floor(lx - reach));
        var y0 = Math.Max(-1, (int)MathF.Floor(ly - reach));
        var z0 = Math.Max(-1, (int)MathF.Floor(lz - reach));
        var x1 = Math.Min(ChunkConstants.Size, (int)MathF.Ceiling(lx + reach));
        var y1 = Math.Min(ChunkConstants.Size, (int)MathF.Ceiling(ly + reach));
        var z1 = Math.Min(ChunkConstants.Size, (int)MathF.Ceiling(lz + reach));

        var density = chunk.Density;
        var material = chunk.Material;
        const float scale = ChunkConstants.DensityScale;
        var dig = op.Mode == BrushMode.Dig;

        for (var z = z0; z <= z1; z++)
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var dx = x - lx;
            var dy = y - ly;
            var dz = z - lz;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            var i = ChunkConstants.Index(x, y, z);
            var old = density[i];

            if (dig)
            {
                var value = Quantise((radius - distance) * scale);

                if (clamp != null && clamp.IsProtected(i))
                    value = Math.Min(value, clamp.Generated[i]);

                if (value > old)
                    density[i] = value;

                continue;
            }

            var solid = Quantise((distance - radius) * scale);

            if (solid >= old)
                continue;

            density[i] = solid;

            if (solid < 0 && (old >= 0 || distance < radius))
                material[i] = (byte)op.Material;
        }
    }

    private static sbyte Quantise(float value) => (sbyte)Math.Clamp(MathF.Round(value), -127f, 127f);
}
