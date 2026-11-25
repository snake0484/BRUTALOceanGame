using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>
/// Every celestial body of every star system, on Kepler rails and spinning, as frames in one <see cref="FrameTree"/>.
/// <see cref="Update"/> writes each body's Inertial (rail) and Surface (spin) pose for a tick: closed form, no
/// history, zero allocations. Several Solar roots are supported: each <see cref="AddStar"/> starts a new system.
/// </summary>
public sealed class CelestialSystem
{
    public const int DefaultCapacity = 256;

    /// <summary>1 g, m/s^2: rocky worlds are 1 g.</summary>
    public const double StandardGravity = 9.80665;

    public readonly FrameTree Frames;
    public readonly double TickSeconds;

    private readonly CelestialBody[] _bodies;

    public CelestialSystem(FrameTree frames, double tickSeconds, int capacity = DefaultCapacity)
    {
        Frames = frames;
        TickSeconds = tickSeconds;
        _bodies = new CelestialBody[capacity];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<CelestialBody> Bodies => _bodies.AsSpan(0, Count);

    public ref readonly CelestialBody this[int index] => ref _bodies[index];

    /// <summary>Tick the frames were last updated to.</summary>
    public long Tick { get; private set; } = -1;

    /// <summary>A star at the Solar root of a new system; its SOI is unbounded.</summary>
    public int AddStar(double mu, double radius, long dayTicks = 0)
    {
        var index = Reserve();
        var root = Frames.AddRoot(FrameKind.Solar, index);

        _bodies[index] = new CelestialBody
        {
            Kind = CelestialKind.Star,
            Parent = -1,
            Inertial = root,
            Surface = Frames.Add(root, FrameKind.BodySurface, index),
            Mu = mu,
            Radius = radius,
            SoiRadius = double.PositiveInfinity,
            RotatingTop = 0.05 * radius,
            DayTicks = dayTicks,
            SpinAxis = double3.UnitY,
        };

        return index;
    }

    /// <summary>A body on rails about <paramref name="parent"/>, SOI from Laplace <c>a (m / M)^0.4</c>.</summary>
    public int AddBody(int parent, CelestialKind kind, double mu, double radius, double rotatingTop, long dayTicks, double3 spinAxis, in KeplerOrbit orbit)
    {
        if ((uint)parent >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(parent));

        var index = Reserve();
        var inertial = Frames.Add(_bodies[parent].Inertial, FrameKind.BodyInertial, index);

        _bodies[index] = new CelestialBody
        {
            Kind = kind,
            Parent = parent,
            Inertial = inertial,
            Surface = Frames.Add(inertial, FrameKind.BodySurface, index),
            Mu = mu,
            Radius = radius,
            SoiRadius = orbit.SemiMajor * Math.Pow(mu / _bodies[parent].Mu, 0.4),
            RotatingTop = Math.Max(rotatingTop, 0.05 * radius),
            DayTicks = dayTicks,
            SpinAxis = double3.Normalize(spinAxis),
            Orbit = orbit,
        };

        return index;
    }

    /// <summary>Writes every body's rail and spin pose for <paramref name="tick"/>.</summary>
    public void Update(long tick)
    {
        var local = Frames.Local;

        for (var i = 0; i < Count; i++)
        {
            ref readonly var body = ref _bodies[i];

            if (body.Orbit.IsValid)
            {
                body.Orbit.State(tick, TickSeconds, out var position, out var velocity);
                ref var inertial = ref local[body.Inertial.Value];
                inertial.Position = position;
                inertial.Velocity = velocity;
            }

            ref var surface = ref local[body.Surface.Value];
            surface.Rotation = doubleQuat.CreateFromAxisAngle(body.SpinAxis, body.SpinAngle(tick));
            surface.AngularVelocity = body.DayTicks > 0 ? body.SpinAxis * (2.0 * Math.PI / (body.DayTicks * TickSeconds)) : double3.Zero;
        }

        Tick = tick;
    }

    /// <summary>
    /// The M1 test system: one star, a 1000 km 1 g planet with a 6 h day whose SOI is ~5e7 m, and one moon on rails
    /// inside it, tidally locked. The seed varies epochs, angles, the moon's inclination and both eccentricities.
    /// </summary>
    public static CelestialSystem CreateTest(FrameTree frames, ulong seed, double tickSeconds)
    {
        const double sunMu = 1.32712440018e20;
        const double planetRadius = 1.0e6;
        const double planetOrbit = 3.6e10;
        const double moonRadius = 4.0e5;
        const double moonOrbit = 2.0e7;
        const double moonGravity = 1.6;

        var system = new CelestialSystem(frames, tickSeconds);
        var random = seed;
        var star = system.AddStar(sunMu, 7.0e8);

        var planetMu = StandardGravity * planetRadius * planetRadius;
        var dayTicks = (long)Math.Round(6.0 * 3600.0 / tickSeconds);
        var planetRail = KeplerOrbit.Create(sunMu, planetOrbit, 0.02 * Next(ref random), 0, 2.0 * Math.PI * Next(ref random), 2.0 * Math.PI * Next(ref random), -(long)(Next(ref random) * 1e8), tickSeconds);
        var planet = system.AddBody(star, CelestialKind.Planet, planetMu, planetRadius, 1.0e5, dayTicks, double3.UnitY, planetRail);

        var moonMu = moonGravity * moonRadius * moonRadius;
        var moonRail = KeplerOrbit.Create(planetMu, moonOrbit, 0.05 * Next(ref random), 0.1 * Next(ref random), 2.0 * Math.PI * Next(ref random), 2.0 * Math.PI * Next(ref random), -(long)(Next(ref random) * 1e7), tickSeconds);
        system.AddBody(planet, CelestialKind.Moon, moonMu, moonRadius, 0, moonRail.PeriodTicks, double3.UnitY, moonRail);

        system.Update(0);
        return system;
    }

    private int Reserve()
    {
        if (Count == _bodies.Length)
            throw new InvalidOperationException($"Celestial store full ({_bodies.Length})");

        return Count++;
    }

    /// <summary>SplitMix64 to [0, 1).</summary>
    private static double Next(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
