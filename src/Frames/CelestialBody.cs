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

    /// <summary>Spin angle comes from <c>tick % DayTicks</c>, so it is exact; 0 means no spin.</summary>
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
