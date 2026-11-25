namespace BrutalGame.Ships;

/// <summary>
/// One material's occupancy sums over a brick's owned samples. A sample owns the 0.25 m cube around it; its occupancy
/// is 0..32 thirty-seconds of that cube, from the quantised SDF as a linear ramp over one cell (exact for faces normal to
/// an axis). Coordinates are global sample indices (ship metres × 4).
/// </summary>
public struct MaterialOccupancy
{
    public byte Material;
    public long Occupancy;
    public long Sx, Sy, Sz;
    public long Sxx, Syy, Szz, Sxy, Sxz, Syz;

    public void Add(long occupancy, long x, long y, long z)
    {
        Occupancy += occupancy;
        Sx += occupancy * x;
        Sy += occupancy * y;
        Sz += occupancy * z;
        Sxx += occupancy * x * x;
        Syy += occupancy * y * y;
        Szz += occupancy * z * z;
        Sxy += occupancy * x * y;
        Sxz += occupancy * x * z;
        Syz += occupancy * y * z;
    }
}

/// <summary>
/// Hull mass moments, exact integers: density-weighted occupancy sums (kg/m³ × occupancy units × sample coordinates),
/// identical whatever order bricks are summed in. One occupancy unit is 1/2048 m³ (1/32 of a 0.25 m cube).
/// </summary>
public struct HullMass
{
    public const double OccupancyUnitM3 = 1.0 / 2048.0;
    public const double SampleMetres = 0.25;

    public long Occupancy;
    public Int128 W;
    public Int128 Wx, Wy, Wz;
    public Int128 Wxx, Wyy, Wzz, Wxy, Wxz, Wyz;

    public void Add(in MaterialOccupancy m, int densityKgM3)
    {
        Occupancy += m.Occupancy;
        Int128 d = densityKgM3;
        W += d * m.Occupancy;
        Wx += d * m.Sx;
        Wy += d * m.Sy;
        Wz += d * m.Sz;
        Wxx += d * m.Sxx;
        Wyy += d * m.Syy;
        Wzz += d * m.Szz;
        Wxy += d * m.Sxy;
        Wxz += d * m.Sxz;
        Wyz += d * m.Syz;
    }

    /// <summary>Solid volume in m³, all materials.</summary>
    public readonly double VolumeM3 => Occupancy * OccupancyUnitM3;

    public readonly double MassKg => (double)W * OccupancyUnitM3;

    /// <summary>Mass in grams, rounded once from the exact sum (ship-hulls MassMoments.MassGrams).</summary>
    public readonly long MassGrams => (long)((W * 1000 + 1024) / 2048);

    /// <summary>Centre of mass in ship metres; zero when massless.</summary>
    public readonly Vector3d CentreOfMass
    {
        get
        {
            if (W == 0)
                return default;

            var w = (double)W;
            return new Vector3d((double)Wx / w * SampleMetres, (double)Wy / w * SampleMetres, (double)Wz / w * SampleMetres);
        }
    }

    /// <summary>
    /// Inertia tensor about the centre of mass in kg·m² (xx, yy, zz, xy, xz, yz; products as +Σ m x y). Each sample cube
    /// adds its own m·a²/6 to the diagonal.
    /// </summary>
    public readonly (double Xx, double Yy, double Zz, double Xy, double Xz, double Yz) Inertia
    {
        get
        {
            if (W == 0)
                return default;

            const double s2 = SampleMetres * SampleMetres;
            var mass = MassKg;
            var unit = OccupancyUnitM3 * s2;
            var c = CentreOfMass;
            var own = mass * s2 / 6.0;
            var xx = (double)Wxx * unit - mass * c.X * c.X;
            var yy = (double)Wyy * unit - mass * c.Y * c.Y;
            var zz = (double)Wzz * unit - mass * c.Z * c.Z;
            var xy = (double)Wxy * unit - mass * c.X * c.Y;
            var xz = (double)Wxz * unit - mass * c.X * c.Z;
            var yz = (double)Wyz * unit - mass * c.Y * c.Z;
            return (yy + zz + own, xx + zz + own, xx + yy + own, xy, xz, yz);
        }
    }
}
