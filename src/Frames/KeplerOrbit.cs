using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>
/// A rail as world data. <see cref="PeriodTicks"/> and the fitted <see cref="SemiMajor"/> are decided once, at system
/// generation (<see cref="KeplerOrbit.Fit"/>), then stored and replicated as they are: a load never re-derives them from
/// doubles, so every build and platform agrees on the integer period and the phase never drifts between them. Positions
/// evaluated from a rail are server-authoritative and never hash-compared: SinCos and the Newton solve may differ in the
/// last bit between runtimes.
/// </summary>
public struct RailElements
{
    public double SemiMajor, Eccentricity, Inclination, Ascending, ArgPeriapsis, ParentMu;
    public long EpochTick, PeriodTicks;
}

/// <summary>
/// Elliptic Kepler rail relative to the parent BodyInertial (or Solar) frame. Phase comes from
/// <c>(tick - EpochTick) % PeriodTicks</c>, so it is exact forever; the body is at periapsis at <see cref="EpochTick"/>.
/// The semi-major axis is fitted to the whole-tick period (<see cref="Fit"/>) so position, velocity and
/// <see cref="ParentMu"/> agree. Reference plane is XZ with +Y north. Built from stored <see cref="RailElements"/>.
/// </summary>
public readonly struct KeplerOrbit
{
    public const int MaxNewtonIterations = 16;

    public readonly double SemiMajor, Eccentricity, Inclination, Ascending, ArgPeriapsis;
    public readonly long EpochTick, PeriodTicks;
    public readonly double ParentMu;

    /// <summary>Unit vectors toward periapsis and 90 degrees ahead of it in the orbit plane, parent axes.</summary>
    private readonly double3 _p, _q;

    private readonly double _minor;

    /// <summary>A rail from stored elements, taken as they are: nothing is rounded or refitted.</summary>
    public KeplerOrbit(in RailElements elements)
    {
        if (elements.Eccentricity is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(elements), "Rails are elliptic");

        if (elements.PeriodTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(elements), "A rail needs a whole-tick period");

        SemiMajor = elements.SemiMajor;
        Eccentricity = elements.Eccentricity;
        Inclination = elements.Inclination;
        Ascending = elements.Ascending;
        ArgPeriapsis = elements.ArgPeriapsis;
        EpochTick = elements.EpochTick;
        PeriodTicks = elements.PeriodTicks;
        ParentMu = elements.ParentMu;
        _minor = SemiMajor * Math.Sqrt(1.0 - Eccentricity * Eccentricity);

        var (sinO, cosO) = Math.SinCos(Ascending);
        var (sinW, cosW) = Math.SinCos(ArgPeriapsis);
        var (sinI, cosI) = Math.SinCos(Inclination);

        // Classical P, Q (Z north) mapped to Y north: (x, y, z) -> (x, z, -y).
        _p = new double3(cosO * cosW - sinO * sinW * cosI, sinW * sinI, -(sinO * cosW + cosO * sinW * cosI));
        _q = new double3(-cosO * sinW - sinO * cosW * cosI, cosW * sinI, -(-sinO * sinW + cosO * cosW * cosI));
    }

    /// <summary>
    /// Generation only: the period is rounded to whole ticks, then the semi-major axis refitted to it. The result is world
    /// data; loads build the rail from it (<see cref="KeplerOrbit(in RailElements)"/>) and never call this again.
    /// </summary>
    public static RailElements Fit(double parentMu, double semiMajor, double eccentricity, double inclination, double ascending, double argPeriapsis, long epochTick, double tickSeconds)
    {
        if (eccentricity is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(eccentricity), "Rails are elliptic");

        var periodSeconds = 2.0 * Math.PI * Math.Sqrt(semiMajor * semiMajor * semiMajor / parentMu);
        var periodTicks = Math.Max(1L, (long)Math.Round(periodSeconds / tickSeconds));

        return new RailElements
        {
            SemiMajor = Math.Cbrt(parentMu * Square(periodTicks * tickSeconds / (2.0 * Math.PI))),
            Eccentricity = eccentricity,
            Inclination = inclination,
            Ascending = ascending,
            ArgPeriapsis = argPeriapsis,
            ParentMu = parentMu,
            EpochTick = epochTick,
            PeriodTicks = periodTicks,
        };
    }

    /// <summary>Generation only: <see cref="Fit"/> then the rail.</summary>
    public static KeplerOrbit Create(double parentMu, double semiMajor, double eccentricity, double inclination, double ascending, double argPeriapsis, long epochTick, double tickSeconds)
    {
        return new KeplerOrbit(Fit(parentMu, semiMajor, eccentricity, inclination, ascending, argPeriapsis, epochTick, tickSeconds));
    }

    public bool IsValid => PeriodTicks > 0;

    /// <summary>The stored form; default for an invalid rail (a star).</summary>
    public RailElements Elements => !IsValid ? default : new RailElements
    {
        SemiMajor = SemiMajor,
        Eccentricity = Eccentricity,
        Inclination = Inclination,
        Ascending = Ascending,
        ArgPeriapsis = ArgPeriapsis,
        ParentMu = ParentMu,
        EpochTick = EpochTick,
        PeriodTicks = PeriodTicks,
    };

    /// <summary>Orbit phase in [0, 1) at <paramref name="tick"/> plus <paramref name="subTick"/> in [0, 1).</summary>
    public double Phase(long tick, double subTick = 0)
    {
        var ticks = (tick - EpochTick) % PeriodTicks;

        if (ticks < 0)
            ticks += PeriodTicks;

        return (ticks + subTick) / PeriodTicks;
    }

    /// <summary>Position (m) and velocity (m/s) in the parent frame.</summary>
    public void State(long tick, double tickSeconds, out double3 position, out double3 velocity, double subTick = 0)
    {
        var mean = 2.0 * Math.PI * Phase(tick, subTick);
        var anomaly = SolveEccentric(mean, Eccentricity);
        var (sinE, cosE) = Math.SinCos(anomaly);
        var rate = 2.0 * Math.PI / (PeriodTicks * tickSeconds) / (1.0 - Eccentricity * cosE);

        position = _p * (SemiMajor * (cosE - Eccentricity)) + _q * (_minor * sinE);
        velocity = _p * (-SemiMajor * sinE * rate) + _q * (_minor * cosE * rate);
    }

    /// <summary>Kepler's equation <c>M = E - e sin E</c> by Newton, to the last bit or <see cref="MaxNewtonIterations"/>.</summary>
    public static double SolveEccentric(double mean, double eccentricity)
    {
        var anomaly = eccentricity < 0.8 ? mean : Math.PI;

        for (var i = 0; i < MaxNewtonIterations; i++)
        {
            var (sinE, cosE) = Math.SinCos(anomaly);
            var step = (anomaly - eccentricity * sinE - mean) / (1.0 - eccentricity * cosE);
            anomaly -= step;

            if (Math.Abs(step) <= 1e-15 * (1.0 + Math.Abs(anomaly)))
                break;
        }

        return anomaly;
    }

    private static double Square(double value) => value * value;
}
