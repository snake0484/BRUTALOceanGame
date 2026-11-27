using System.Numerics;

namespace BrutalGame.Ships;

/// <summary>
/// Mass of small things (a tool, D91) by sampling the plan's ordered CSG at cell centres: the 0.25 m grid that gives a
/// ship its mass is far too coarse for something a few centimetres across. Each sample is inside or not and takes the
/// material of the last solid that added or painted it. Deterministic; boxes, wedges and cylinders (other shapes are
/// skipped). The cell grows so a sample count stays under <see cref="MaxSamples"/>.
/// </summary>
public static class SolidMass
{
    public const double CellMetres = 0.002;
    public const long MaxSamples = 4_000_000;

    public static (double MassKg, double VolumeM3) Sample(ReadOnlySpan<HullSolid> solids, IReadOnlyList<int> densities)
    {
        var min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vector3d(double.MinValue, double.MinValue, double.MinValue);
        var any = false;

        foreach (ref readonly var s in solids)
        {
            if (s.Op != SolidOp.Add || !SolidGeometry.IsSupported(s.Shape))
                continue;

            SolidGeometry.Bounds(s, out var lo, out var hi);
            min = new Vector3d(Math.Min(min.X, lo.X), Math.Min(min.Y, lo.Y), Math.Min(min.Z, lo.Z));
            max = new Vector3d(Math.Max(max.X, hi.X), Math.Max(max.Y, hi.Y), Math.Max(max.Z, hi.Z));
            any = true;
        }

        if (!any)
            return (0, 0);

        var size = new Vector3d(max.X - min.X, max.Y - min.Y, max.Z - min.Z);
        var cell = CellMetres;

        while (Math.Ceiling(size.X / cell) * Math.Ceiling(size.Y / cell) * Math.Ceiling(size.Z / cell) > MaxSamples)
            cell *= 1.25;

        int nx = (int)Math.Ceiling(size.X / cell), ny = (int)Math.Ceiling(size.Y / cell), nz = (int)Math.Ceiling(size.Z / cell);

        // Each solid's inverse turn (its matrix's transpose), centre and half extents, worked out once.
        var frames = new (double[] M, Vector3d C, Vector3 Half)[solids.Length];

        for (var i = 0; i < solids.Length; i++)
        {
            var m = new double[9];
            SolidRotation.Matrix(solids[i].Rotation, m);
            frames[i] = (m, new Vector3d(solids[i].PositionX, solids[i].PositionY, solids[i].PositionZ), SolidGeometry.Half(solids[i]));
        }

        var mass = 0.0;
        var volume = 0.0;
        var cellVolume = cell * cell * cell;

        for (var z = 0; z < nz; z++)
        {
            for (var y = 0; y < ny; y++)
            {
                for (var x = 0; x < nx; x++)
                {
                    var p = new Vector3d(min.X + (x + 0.5) * cell, min.Y + (y + 0.5) * cell, min.Z + (z + 0.5) * cell);
                    var inside = false;
                    var material = 0;

                    for (var i = 0; i < solids.Length; i++)
                    {
                        ref readonly var s = ref solids[i];

                        if (!SolidGeometry.IsSupported(s.Shape))
                            continue;

                        var (m, c, half) = frames[i];
                        double dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z;
                        var local = new Vector3((float)(m[0] * dx + m[3] * dy + m[6] * dz), (float)(m[1] * dx + m[4] * dy + m[7] * dz), (float)(m[2] * dx + m[5] * dy + m[8] * dz));
                        var contains = SolidGeometry.Distance(s.Shape, local, half) <= 0f;

                        switch (s.Op)
                        {
                            case SolidOp.Add when contains:
                                (inside, material) = (true, s.Material);
                                break;
                            case SolidOp.Subtract when contains:
                                inside = false;
                                break;
                            case SolidOp.Paint when contains && inside:
                                material = s.Material;
                                break;
                            case SolidOp.Intersect:
                                inside &= contains;
                                break;
                        }
                    }

                    if (!inside)
                        continue;

                    volume += cellVolume;
                    mass += cellVolume * (material < densities.Count ? densities[material] : 0);
                }
            }
        }

        return (mass, volume);
    }
}
