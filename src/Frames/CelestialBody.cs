using Brutal.Numerics;

namespace BrutalGame.Frames;

public struct CelestialBody
{
    public CelestialKind Kind;

    /// <summary>Celestial index of the body this one orbits; -1 for a star (its Inertial frame is the Solar root).</summary>
    public int Parent;

    public FrameId Inertial, Surface;

    public double Mu, Radius, SoiRadius;

    /// <summary>max(atmosphere top, 0.05 R): below it things live in the rotating Surface frame.</summary>
    public double RotatingTop;

    /// <summary>Spin angle comes from <c>tick % DayTicks</c>, so it is exact; 0 means no spin. World data, fixed at generation.</summary>
    public long DayTicks;

    /// <summary>Unit spin axis in the Inertial frame.</summary>
    public double3 SpinAxis;

    /// <summary>Rail about <see cref="Parent"/>; invalid for a star.</summary>
    public KeplerOrbit Orbit;

    public readonly SystemId System(FrameTree frames) => frames.System[Inertial.Value];

    /// <summary>Spin angle (rad, in [0, 2 pi)) at <paramref name="tick"/> plus <paramref name="subTick"/>.</summary>
    public readonly double SpinAngle(long tick, double subTick = 0)
    {
        if (DayTicks <= 0)
            return 0;

        var ticks = tick % DayTicks;

        if (ticks < 0)
            ticks += DayTicks;

        return 2.0 * Math.PI * ((ticks + subTick) / DayTicks);
    }
}

/// <summary>
/// One body as world data (see <see cref="RailElements"/>): everything generation rounded, fitted or derived (period, axis,
/// day, SOI) is stored and replicated as it is, so a load never re-derives it from doubles.
/// </summary>
public struct CelestialRecord
{
    public CelestialKind Kind;

    /// <summary>-1 for a star, which has no rail.</summary>
    public int Parent;

    public double Mu, Radius, SoiRadius, RotatingTop;
    public long DayTicks;
    public double3 SpinAxis;
    public RailElements Rail;

    public readonly void Write(BinaryWriter writer)
    {
        writer.Write((byte)Kind);
        writer.Write(Parent);
        writer.Write(Mu);
        writer.Write(Radius);
        writer.Write(SoiRadius);
        writer.Write(RotatingTop);
        writer.Write(DayTicks);
        writer.Write(SpinAxis.X);
        writer.Write(SpinAxis.Y);
        writer.Write(SpinAxis.Z);
        writer.Write(Rail.SemiMajor);
        writer.Write(Rail.Eccentricity);
        writer.Write(Rail.Inclination);
        writer.Write(Rail.Ascending);
        writer.Write(Rail.ArgPeriapsis);
        writer.Write(Rail.ParentMu);
        writer.Write(Rail.EpochTick);
        writer.Write(Rail.PeriodTicks);
    }

    public static CelestialRecord Read(BinaryReader reader) => new()
    {
        Kind = (CelestialKind)reader.ReadByte(),
        Parent = reader.ReadInt32(),
        Mu = reader.ReadDouble(),
        Radius = reader.ReadDouble(),
        SoiRadius = reader.ReadDouble(),
        RotatingTop = reader.ReadDouble(),
        DayTicks = reader.ReadInt64(),
        SpinAxis = new double3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        Rail = new RailElements
        {
            SemiMajor = reader.ReadDouble(),
            Eccentricity = reader.ReadDouble(),
            Inclination = reader.ReadDouble(),
            Ascending = reader.ReadDouble(),
            ArgPeriapsis = reader.ReadDouble(),
            ParentMu = reader.ReadDouble(),
            EpochTick = reader.ReadInt64(),
            PeriodTicks = reader.ReadInt64(),
        },
    };
}
