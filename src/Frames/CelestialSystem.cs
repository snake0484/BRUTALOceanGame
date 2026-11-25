using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>
/// Every celestial body of every star system, on Kepler rails and spinning, as frames in one <see cref="FrameTree"/>.
/// <see cref="Update"/> writes each body's Inertial (rail) and Surface (spin) pose for a tick, <see cref="Evaluate"/>
/// the same at render time between ticks: closed form, no history, zero allocations. Several Solar roots are supported:
/// each star starts a new system. The bodies are world data (<see cref="CelestialRecord"/>): generated once
/// (<see cref="AddStar"/>, <see cref="AddBody"/>), then stored and loaded as they are (<see cref="Write"/>, <see cref="Read"/>).
/// </summary>
public sealed class CelestialSystem
{
    public const int DefaultCapacity = 256;

    /// <summary>Bumped when the stored layout changes.</summary>
    public const int FormatVersion = 1;

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

    /// <summary>Render time of <see cref="FrameTree.Render"/>: <see cref="RenderTick"/> plus <see cref="RenderAlpha"/> of a tick.</summary>
    public long RenderTick { get; private set; } = -1;

    public double RenderAlpha { get; private set; }

    /// <summary>Generation: a star at the Solar root of a new system; its SOI is unbounded.</summary>
    public int AddStar(double mu, double radius, long dayTicks = 0)
    {
        return Add(new CelestialRecord
        {
            Kind = CelestialKind.Star,
            Parent = -1,
            Mu = mu,
            Radius = radius,
            SoiRadius = double.PositiveInfinity,
            RotatingTop = 0.05 * radius,
            DayTicks = dayTicks,
            SpinAxis = double3.UnitY,
        });
    }

    /// <summary>Generation: a body on rails about <paramref name="parent"/>, SOI from Laplace <c>a (m / M)^0.4</c>.</summary>
    public int AddBody(int parent, CelestialKind kind, double mu, double radius, double rotatingTop, long dayTicks, double3 spinAxis, in KeplerOrbit orbit)
    {
        if ((uint)parent >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(parent));

        return Add(new CelestialRecord
        {
            Kind = kind,
            Parent = parent,
            Mu = mu,
            Radius = radius,
            SoiRadius = orbit.SemiMajor * Math.Pow(mu / _bodies[parent].Mu, 0.4),
            RotatingTop = Math.Max(rotatingTop, 0.05 * radius),
            DayTicks = dayTicks,
            SpinAxis = double3.Normalize(spinAxis),
            Rail = orbit.Elements,
        });
    }

    /// <summary>A body exactly as stored: its frames are added and nothing is derived again.</summary>
    public int Add(in CelestialRecord record)
    {
        if (record.Parent >= Count || (record.Parent < 0) != (record.Rail.PeriodTicks == 0))
            throw new ArgumentException("A star has no parent and no rail; every other body has both", nameof(record));

        var index = Reserve();
        var inertial = record.Parent < 0
            ? Frames.AddRoot(FrameKind.Solar, index)
            : Frames.Add(_bodies[record.Parent].Inertial, FrameKind.BodyInertial, index);

        _bodies[index] = new CelestialBody
        {
            Kind = record.Kind,
            Parent = record.Parent,
            Inertial = inertial,
            Surface = Frames.Add(inertial, FrameKind.BodySurface, index),
            Mu = record.Mu,
            Radius = record.Radius,
            SoiRadius = record.SoiRadius,
            RotatingTop = record.RotatingTop,
            DayTicks = record.DayTicks,
            SpinAxis = record.SpinAxis,
            Orbit = record.Parent < 0 ? default : new KeplerOrbit(record.Rail),
        };

        return index;
    }

    /// <summary>The stored form of body <paramref name="index"/>.</summary>
    public CelestialRecord Record(int index)
    {
        ref readonly var body = ref _bodies[index];

        return new CelestialRecord
        {
            Kind = body.Kind,
            Parent = body.Parent,
            Mu = body.Mu,
            Radius = body.Radius,
            SoiRadius = body.SoiRadius,
            RotatingTop = body.RotatingTop,
            DayTicks = body.DayTicks,
            SpinAxis = body.SpinAxis,
            Rail = body.Orbit.Elements,
        };
    }

    /// <summary>Writes every body's pose for <paramref name="tick"/> into <see cref="FrameTree.Local"/>, the sim's poses.</summary>
    public void Update(long tick)
    {
        WritePoses(Frames.Local, tick, 0.0);
        Tick = tick;
    }

    /// <summary>
    /// Writes every body's pose at render time, <paramref name="alpha"/> (in [0, 1)) of a tick past <paramref name="tick"/>,
    /// into <see cref="FrameTree.Render"/>. Closed form in double like <see cref="Update"/>: at alpha 0 it is that tick's
    /// pose bit for bit, and between ticks rails and spin move at render rate instead of stepping at the tick.
    /// </summary>
    public void Evaluate(long tick, double alpha)
    {
        WritePoses(Frames.Render, tick, alpha);
        RenderTick = tick;
        RenderAlpha = alpha;
    }

    /// <summary>Stores the system: the tick length, then every <see cref="CelestialRecord"/> in index order, bit-exact.</summary>
    public void Write(BinaryWriter writer)
    {
        writer.Write(FormatVersion);
        writer.Write(TickSeconds);
        writer.Write(Count);

        for (var i = 0; i < Count; i++)
            Record(i).Write(writer);
    }

    /// <summary>A stored system into <paramref name="frames"/>; <see cref="Update"/> it before reading poses.</summary>
    public static CelestialSystem Read(BinaryReader reader, FrameTree frames, int capacity = DefaultCapacity)
    {
        var version = reader.ReadInt32();

        if (version != FormatVersion)
            throw new InvalidDataException($"Celestial format {version}, expected {FormatVersion}");

        var system = new CelestialSystem(frames, reader.ReadDouble(), capacity);
        var count = reader.ReadInt32();

        for (var i = 0; i < count; i++)
            system.Add(CelestialRecord.Read(reader));

        return system;
    }

    /// <summary>
    /// Generation of the M1 test system: one star, a 1000 km 1 g planet with a 6 h day whose SOI is ~5e7 m, and one moon
    /// on rails inside it, tidally locked. The seed varies epochs, angles, the moon's inclination and both eccentricities.
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
        system.Evaluate(0, 0.0);
        return system;
    }

    private void WritePoses(FramePose[] poses, long tick, double subTick)
    {
        for (var i = 0; i < Count; i++)
        {
            ref readonly var body = ref _bodies[i];

            if (body.Orbit.IsValid)
            {
                body.Orbit.State(tick, TickSeconds, out var position, out var velocity, subTick);
                ref var inertial = ref poses[body.Inertial.Value];
                inertial.Position = position;
                inertial.Velocity = velocity;
            }

            ref var surface = ref poses[body.Surface.Value];
            surface.Rotation = doubleQuat.CreateFromAxisAngle(body.SpinAxis, body.SpinAngle(tick, subTick));
            surface.AngularVelocity = body.DayTicks > 0 ? body.SpinAxis * (2.0 * Math.PI / (body.DayTicks * TickSeconds)) : double3.Zero;
        }
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
