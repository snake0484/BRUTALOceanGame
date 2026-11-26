using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// <see cref="WeatherField"/> on a rotating sphere, deterministic from the climate's seed and the tick. Systems spawn in
/// latitude rows of ~24 km cells (fewer cells per row toward the poles); each row drifts east or west with its zonal
/// steering wind in closed form. Slots, lives, kinds and profiles are the island's, distances are great-circle, and
/// circulation turns cyclonically about local up in the north, the other way in the south, fading out at the equator.
/// Winds come back in the local tangent frame: X east, Y north. Read-only; safe to call from jobs.
/// </summary>
public sealed class SphereWeatherField
{
    public const double BaseCellSize = 24_000.0;
    public const int Slots = 2;

    /// <summary>Sample buffer: the 1000 km rocky worlds need ≤ 24, gas giants' coarse rows a few more.</summary>
    public const int MaxPointSystems = 64;

    private const double BaseMaxRadius = 16_000.0;
    private const double BaseMinPeriod = 1800.0;
    private const double BaseMaxPeriod = 3600.0;
    private const double BaseMaxOwnSpeed = 1.5;
    private const double MinLifeShare = 0.6;
    private const double MaxLifeShare = 0.95;
    private const float GrowUntil = 0.3f;
    private const float DecayFrom = 0.65f;
    private const double SpawnMargin = 0.1;

    /// <summary>Largest angle the short-angle series takes; beyond it (polar rows, gas giants) Math.SinCos does.</summary>
    private const double SeriesLimit = 0.02;
    private const double ReachSafety = 1.01;

    private const float StormShare = 0.1f;
    private const float StormShareByStorminess = 0.4f;
    private const float LowShare = 0.3f;

    private const float RainCore = 0.6f;
    private const float ConvectionCore = 0.45f;
    private const float BaseGust = 0.2f;
    private const float StormGust = 0.5f;

    private const double Inflow = 20.0 * Math.PI / 180.0;
    private const double RingNorm = 3.4938562;
    private const double Tau = 2.0 * Math.PI;
    private const double HalfPi = 0.5 * Math.PI;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const ulong JetSalt = 0x3C6EF372FE94F82BUL;
    private const ulong StormSalt = 0xA54FF53A5F1D36F1UL;
    private const ulong CloudSalt = 0x510E527FADE682D1UL;

    private static readonly double SinInflow = Math.Sin(Inflow);
    private static readonly double CosInflow = Math.Cos(Inflow);

    public readonly PlanetClimate Climate;
    public readonly double TickSeconds;

    /// <summary>North pole, and the planet-frame directions of longitude 0 and 90° east on the equator.</summary>
    public readonly double3 Pole;
    public readonly double3 Prime;
    public readonly double3 PrimeEast;

    public readonly int Rows;

    /// <summary>Row height (rad).</summary>
    public readonly double RowHeight;

    /// <summary>Furthest (rad) a system's edge gets from its spawn point: its radius plus its own drift over its longest life.</summary>
    public readonly double Reach;

    private readonly int _bands;
    private readonly double _minPeriod;
    private readonly double _maxPeriod;
    private readonly double _ownSpeedAxis;
    private readonly double _sinEquatorFade;
    private readonly double _sinReach;
    private readonly double _cosReach;
    private readonly float[] _jet;
    private readonly float[] _storminess;
    private readonly float[] _cloud;
    private readonly int[] _rowCells;
    private readonly double[] _rowOmega;
    private readonly float[] _rowStorminess;
    private readonly double[] _rowCosColatA;
    private readonly double[] _rowSinColatA;
    private readonly double[] _rowCosColatB;
    private readonly double[] _rowSinColatB;
    private readonly double[] _rowSinLatitude;
    private readonly double[] _rowCosLatitude;
    private readonly int[] _rowFirstCell;
    private readonly double[] _cellSinLongitude;
    private readonly double[] _cellCosLongitude;

    public SphereWeatherField(in PlanetClimate climate, double tickSeconds = 1.0 / 60.0)
    {
        Climate = climate;
        TickSeconds = tickSeconds;
        Pole = double3.Normalize(climate.SpinAxis);
        PrimeEast = double3.Normalize(double3.Cross(Pole, Math.Abs(Pole.X) < 0.9 ? double3.UnitX : double3.UnitZ));
        Prime = double3.Cross(PrimeEast, Pole);

        var radius = climate.Radius;
        var cellSize = BaseCellSize * climate.SizeScale;
        _minPeriod = BaseMinPeriod * climate.TimeScale;
        _maxPeriod = BaseMaxPeriod * climate.TimeScale;
        var ownSpeed = BaseMaxOwnSpeed * climate.SizeScale / climate.TimeScale;
        _ownSpeedAxis = ownSpeed / Math.Sqrt(2.0);
        Reach = Math.Min(Math.PI, (BaseMaxRadius * climate.SizeScale + ownSpeed * _maxPeriod * MaxLifeShare) / radius * ReachSafety);
        _sinEquatorFade = Math.Max(1e-9, Math.Sin(climate.EquatorFade));
        (_sinReach, _cosReach) = Math.SinCos(Reach);

        _bands = Math.Max(1, climate.ZonalCells);
        _jet = new float[2 * _bands];
        _storminess = new float[2 * _bands];
        _cloud = new float[2 * _bands];

        for (var k = 0; k < _jet.Length; k++)
        {
            var hemisphereBand = k >= _bands ? k - _bands : _bands - 1 - k;
            var speed = hemisphereBand % 2 == 0 ? climate.EasterlySpeed : climate.WesterlySpeed;
            _jet[k] = speed * Vary(climate.Seed, JetSalt, k, climate.JetVariation);
            _storminess[k] = Vary(climate.Seed, StormSalt, k, climate.StorminessVariation);
            _cloud[k] = Vary(climate.Seed, CloudSalt, k, climate.BaseCloudVariation);
        }

        Rows = Math.Max(1, (int)Math.Round(Math.PI * radius / cellSize));
        RowHeight = Math.PI / Rows;
        _rowCells = new int[Rows];
        _rowOmega = new double[Rows];
        _rowStorminess = new float[Rows];
        _rowCosColatA = new double[Rows];
        _rowSinColatA = new double[Rows];
        _rowCosColatB = new double[Rows];
        _rowSinColatB = new double[Rows];
        _rowSinLatitude = new double[Rows];
        _rowCosLatitude = new double[Rows];
        _rowFirstCell = new int[Rows + 1];

        for (var j = 0; j < Rows; j++)
        {
            var lat = -HalfPi + (j + 0.5) * RowHeight;
            var (sin, cos) = Math.SinCos(lat);
            _rowSinLatitude[j] = sin;
            _rowCosLatitude[j] = cos;
            _rowCells[j] = Math.Max(1, (int)Math.Round(Tau * radius * cos / cellSize));
            _rowFirstCell[j + 1] = _rowFirstCell[j] + _rowCells[j];
            _rowOmega[j] = climate.SteeringShare * Zonal(lat) / (radius * cos);
            _rowStorminess[j] = Math.Clamp(climate.Storminess * Band(_storminess, lat), 0f, 1f);

            var colatA = HalfPi - (-HalfPi + (j + 1 - SpawnMargin) * RowHeight);
            var colatB = HalfPi - (-HalfPi + (j + SpawnMargin) * RowHeight);
            (_rowSinColatA[j], _rowCosColatA[j]) = Math.SinCos(colatA);
            (_rowSinColatB[j], _rowCosColatB[j]) = Math.SinCos(colatB);
        }

        _cellSinLongitude = new double[_rowFirstCell[Rows]];
        _cellCosLongitude = new double[_rowFirstCell[Rows]];

        for (var j = 0; j < Rows; j++)
        {
            for (var i = 0; i < _rowCells[j]; i++)
                (_cellSinLongitude[_rowFirstCell[j] + i], _cellCosLongitude[_rowFirstCell[j] + i]) = Math.SinCos((i + 0.5) * Tau / _rowCells[j]);
        }
    }

    public int RowCells(int row) => _rowCells[row];

    public WeatherSample Sample(double3 planetLocal, long tick) => SampleTime(planetLocal, tick * TickSeconds);

    /// <summary>As <see cref="Sample(double3, long)"/> at a time in seconds. Altitude is ignored: this is the 10 m wind.</summary>
    [SkipLocalsInit]
    public WeatherSample SampleTime(double3 planetLocal, double time)
    {
        var unit = double3.Normalize(planetLocal);
        Span<SphereSystem> systems = stackalloc SphereSystem[MaxPointSystems];
        var count = Gather(unit, 0.0, time, systems);
        return Evaluate(unit, systems[..count]);
    }

    /// <summary>Unit direction at latitude and longitude (rad), longitude east from <see cref="Prime"/>.</summary>
    public double3 Direction(double latitude, double longitude)
    {
        var (sinLat, cosLat) = Math.SinCos(latitude);
        var (sinLon, cosLon) = Math.SinCos(longitude);
        return cosLat * (cosLon * Prime + sinLon * PrimeEast) + sinLat * Pole;
    }

    /// <summary>Local east and north at a unit direction. At the exact poles east falls back to <see cref="PrimeEast"/>.</summary>
    public void Basis(double3 unit, out double3 east, out double3 north)
    {
        east = double3.Cross(Pole, unit);
        var length2 = east.LengthSquared();
        east = length2 > 1e-24 ? east / Math.Sqrt(length2) : PrimeEast;
        north = double3.Cross(unit, east);
    }

    /// <summary>A tangent-frame wind (X east, Y north) at a unit direction as a planet-frame vector, e.g. for airspeed.</summary>
    public double3 ToPlanet(double3 unit, Vector2 wind)
    {
        Basis(unit, out var east, out var north);
        return wind.X * east + wind.Y * north;
    }

    /// <summary>
    /// Writes the systems alive at <paramref name="time"/> that reach within <paramref name="extra"/> rad of
    /// <paramref name="unit"/> and returns how many, at most the span's length. Ordered by row, cell, slot.
    /// </summary>
    public int Gather(double3 unit, double extra, double time, Span<SphereSystem> systems)
    {
        var window = new Window(this, unit, extra);
        var count = 0;

        for (var j = window.Row0; j <= window.Row1; j++)
        {
            if (!RowSpan(j, window, time, out var shift, out var from0, out var to0, out var from1, out var to1))
                continue;

            count = GatherCells(j, from0, to0, unit, extra, time, shift, systems, count);
            count = GatherCells(j, from1, to1, unit, extra, time, shift, systems, count);
        }

        return count;
    }

    /// <summary>Weather at a unit direction from the given systems; those that don't reach it add nothing.</summary>
    public WeatherSample Evaluate(double3 unit, ReadOnlySpan<SphereSystem> systems)
    {
        var sample = Accumulate(unit, systems, out var wind, out var east, out var north);
        sample.Wind = new Vector2((float)double3.Dot(wind, east), (float)double3.Dot(wind, north));
        return sample;
    }

    /// <summary>Zonal wind (m/s, positive toward east) of the steering profile at a latitude.</summary>
    public double Zonal(double latitude)
    {
        var x = Math.Abs(latitude) / HalfPi * _bands;
        var band = Math.Min((int)x, _bands - 1);
        return -_jet[latitude >= 0.0 ? _bands + band : _bands - 1 - band] * Math.Sin(Math.PI * x);
    }

    /// <summary>Candidate cells a point sample visits, in all and in its busiest row: the high-latitude cap.</summary>
    internal int CandidateCells(double3 unit, out int maxPerRow)
    {
        var window = new Window(this, unit, 0.0);
        var total = 0;
        maxPerRow = 0;

        for (var j = window.Row0; j <= window.Row1; j++)
        {
            if (!RowSpan(j, window, 0.0, out _, out var from0, out var to0, out var from1, out var to1))
                continue;

            var cells = Math.Max(0, to0 - from0 + 1) + Math.Max(0, to1 - from1 + 1);
            total += cells;
            maxPerRow = Math.Max(maxPerRow, cells);
        }

        return total;
    }

    /// <summary>The sample with scalars filled, plus the planet-frame wind and the local basis.</summary>
    internal WeatherSample Accumulate(double3 p, ReadOnlySpan<SphereSystem> systems, out double3 wind, out double3 east, out double3 north)
    {
        Basis(p, out east, out north);
        var latitude = Math.Asin(Math.Clamp(double3.Dot(p, Pole), -1.0, 1.0));
        var dry = 1f - Math.Clamp(Climate.BaseCloud * Band(_cloud, latitude), 0f, 1f);
        var dryRain = 1f;
        var still = 1f;
        var clear = 1f;
        var calm = 1f;
        var pressure = 0f;
        var circulation = double3.Zero;

        foreach (ref readonly var s in systems)
        {
            var d = p - s.Centre;
            var chord2 = d.LengthSquared();
            var q2 = (float)(chord2 * (1.0 + chord2 / 12.0) * s.InvAngularRadius * s.InvAngularRadius);

            if (q2 >= 1f)
                continue;

            var w = 1f - q2;
            var g = w * w * w * s.Strength;
            pressure += s.Pressure * g;

            if (s.Spin != 0f)
            {
                var k = s.PeakWind * s.Strength * RingNorm * w * w * s.InvAngularRadius;
                var tangent = d - p * double3.Dot(p, d);
                var inflow = s.Kind == PressureKind.High ? -MathF.Abs(s.Spin) : MathF.Abs(s.Spin);
                circulation += k * (s.Spin * CosInflow * double3.Cross(p, d) - inflow * SinInflow * tangent);
            }

            if (s.Kind == PressureKind.High)
            {
                clear *= 1f - s.Cloud * g;
                calm *= 1f - s.Calm * g;
                continue;
            }

            dry *= 1f - s.Cloud * g;
            dryRain *= 1f - s.Rain * s.Strength * Core(q2, RainCore);
            still *= 1f - s.Convection * s.Strength * Core(q2, ConvectionCore);
        }

        var zonal = Zonal(latitude);
        var poleward = Climate.MeridionalShare * zonal;
        wind = calm * (zonal * east + (latitude >= 0.0 ? poleward : -poleward) * north) + circulation;

        var storm = (1f - still) * clear;
        return new WeatherSample
        {
            Cloud = (1f - dry) * clear,
            Rain = (1f - dryRain) * clear,
            Storm = storm,
            Pressure = pressure,
            Gust = BaseGust + StormGust * storm,
        };
    }

    internal static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// The cells of row <paramref name="j"/> whose spawn points can lie within the window's reach of its point, as up to
    /// two ascending index ranges (two when the range wraps), so any window visits shared cells in the same order.
    /// </summary>
    private bool RowSpan(int j, in Window window, double time, out double shift, out int from0, out int to0, out int from1, out int to1)
    {
        shift = Wrap(_rowOmega[j] * time);
        from0 = 0;
        from1 = 0;
        to1 = -1;
        var cells = _rowCells[j];
        to0 = cells - 1;
        var reach = RowLongitudeReach(j, window);

        if (reach < 0.0)
            return false;

        if (reach >= Math.PI)
            return true;

        var width = Tau / cells;
        var relative = Wrap(window.Longitude - shift);
        var first = (long)Math.Floor((relative - reach) / width);
        var last = (long)Math.Floor((relative + reach) / width);

        if (last - first + 1 >= cells)
            return true;

        var a = (int)(first - cells * (long)Math.Floor((double)first / cells));
        var b = (int)(last - cells * (long)Math.Floor((double)last / cells));

        if (a <= b)
        {
            from0 = a;
            to0 = b;
            return true;
        }

        to0 = b;
        from1 = a;
        to1 = cells - 1;
        return true;
    }

    /// <summary>
    /// Widest longitude offset (rad) from the window's point of a spawn point in row j within its reach: π for the whole
    /// row, negative for none. Δλ(θ) is largest where the reach circle is tangent to a meridian, else at a row edge.
    /// </summary>
    private double RowLongitudeReach(int j, in Window window)
    {
        var tangentCos = window.CosColat / window.CosReach;
        var capHasPole = Math.Abs(window.CosColat) > window.CosReach;

        if (!capHasPole && _rowCosColatA[j] >= tangentCos && tangentCos >= _rowCosColatB[j])
            return Math.Asin(Math.Min(1.0, window.SinReach / window.SinColat));

        return Math.Max(
            LongitudeReach(window, _rowSinColatA[j], _rowCosColatA[j]),
            LongitudeReach(window, _rowSinColatB[j], _rowCosColatB[j]));
    }

    private static double LongitudeReach(in Window window, double sinColat, double cosColat)
    {
        var denominator = window.SinColat * sinColat;

        if (denominator < 1e-300)
            return window.CosColat * cosColat >= window.CosReach ? Math.PI : -1.0;

        var c = (window.CosReach - window.CosColat * cosColat) / denominator;

        if (c <= -1.0)
            return Math.PI;

        return c >= 1.0 ? -1.0 : Math.Acos(c);
    }

    private int GatherCells(int j, int from, int to, double3 p, double extra, double time, double shift, Span<SphereSystem> systems, int count)
    {
        if (from > to)
            return count;

        var (sinShift, cosShift) = Math.SinCos(shift);

        for (var i = from; i <= to; i++)
        {
            for (var slot = 0; slot < Slots; slot++)
            {
                if (!TryLife(j, i, slot, time, sinShift, cosShift, out var s))
                    continue;

                var limit = 1.0 / s.InvAngularRadius + extra;

                if ((p - s.Centre).LengthSquared() >= limit * limit)
                    continue;

                if (count == systems.Length)
                    return count;

                systems[count++] = s;
            }
        }

        return count;
    }

    private bool TryLife(int j, int i, int slot, double time, double sinShift, double cosShift, out SphereSystem system)
    {
        system = default;
        var key = Key(Climate.Seed, j, i, slot);
        var rng = new Rng(key);
        var period = _minPeriod + (_maxPeriod - _minPeriod) * rng.NextDouble();
        var phase = period * rng.NextDouble();
        var life = Math.Floor((time + phase) / period);
        var age = time - (life * period - phase);

        rng = new Rng(Mix(key + Golden * (ulong)(long)life));
        var span = period * (MinLifeShare + (MaxLifeShare - MinLifeShare) * rng.NextDouble());

        if (age >= span)
            return false;

        var u = (float)(age / span);
        system.Strength = Smooth(0f, GrowUntil, u) * (1f - Smooth(DecayFrom, 1f, u));

        if (system.Strength <= 0f)
            return false;

        var (sinDLat, cosDLat) = SinCos((SpawnMargin - 0.5 + (1.0 - 2.0 * SpawnMargin) * rng.NextDouble()) * RowHeight);
        var (sinDLon, cosDLon) = SinCos((SpawnMargin - 0.5 + (1.0 - 2.0 * SpawnMargin) * rng.NextDouble()) * (Tau / _rowCells[j]));
        var sinLat = _rowSinLatitude[j] * cosDLat + _rowCosLatitude[j] * sinDLat;
        var cosLat = _rowCosLatitude[j] * cosDLat - _rowSinLatitude[j] * sinDLat;
        var cell = _rowFirstCell[j] + i;
        var sinCell = _cellSinLongitude[cell] * cosShift + _cellCosLongitude[cell] * sinShift;
        var cosCell = _cellCosLongitude[cell] * cosShift - _cellSinLongitude[cell] * sinShift;
        var sinLon = sinCell * cosDLon + cosCell * sinDLon;
        var cosLon = cosCell * cosDLon - sinCell * sinDLon;
        var radial = cosLon * Prime + sinLon * PrimeEast;
        var spawn = cosLat * radial + sinLat * Pole;

        var velocityEast = _ownSpeedAxis * (2.0 * rng.NextDouble() - 1.0);
        var velocityNorth = _ownSpeedAxis * (2.0 * rng.NextDouble() - 1.0);
        var speed = Math.Sqrt(velocityEast * velocityEast + velocityNorth * velocityNorth);
        system.Centre = spawn;

        if (speed > 0.0)
        {
            var east = -sinLon * Prime + cosLon * PrimeEast;
            var north = -sinLat * radial + cosLat * Pole;
            var (sinArc, cosArc) = SinCos(speed * age / Climate.Radius);
            system.Centre = cosArc * spawn + sinArc / speed * (velocityEast * east + velocityNorth * north);
        }

        var stormShare = StormShare + StormShareByStorminess * _rowStorminess[j];
        var roll = rng.NextFloat();
        system.Kind = roll < stormShare ? PressureKind.Storm : roll < stormShare + LowShare ? PressureKind.Low : PressureKind.High;
        Shape(ref system, rng.NextFloat(), rng.NextFloat(), _rowStorminess[j], Climate.SizeScale, Climate.WindScale);
        system.InvAngularRadius = Climate.Radius / system.Radius;

        var sinCentreLat = double3.Dot(system.Centre, Pole);
        var sense = Math.Sign(sinCentreLat) * Smooth(0f, (float)_sinEquatorFade, (float)Math.Abs(sinCentreLat));
        system.Spin = system.Kind == PressureKind.High ? -sense : sense;
        return true;
    }

    /// <summary>Size and peaks by kind, as the island's table scaled by the climate.</summary>
    private static void Shape(ref SphereSystem s, float size, float power, float storminess, float sizeScale, float windScale)
    {
        switch (s.Kind)
        {
            case PressureKind.Storm:
                s.Radius = float.Lerp(8_000f, 12_000f, size) * sizeScale;
                s.Pressure = -float.Lerp(12f, 25f, power);
                s.PeakWind = float.Lerp(14f, 26f, power) * (0.6f + 0.6f * storminess) * windScale;
                s.Cloud = 1f;
                s.Rain = float.Lerp(0.8f, 1f, power);
                s.Convection = float.Lerp(0.6f, 1f, power);
                break;
            case PressureKind.Low:
                s.Radius = float.Lerp(10_000f, (float)BaseMaxRadius, size) * sizeScale;
                s.Pressure = -float.Lerp(5f, 10f, power);
                s.PeakWind = float.Lerp(5f, 10f, power) * windScale;
                s.Cloud = float.Lerp(0.85f, 1f, power);
                s.Rain = float.Lerp(0.2f, 0.5f, power);
                s.Convection = 0.2f * power;
                break;
            default:
                s.Radius = float.Lerp(12_000f, (float)BaseMaxRadius, size) * sizeScale;
                s.Pressure = float.Lerp(5f, 10f, power);
                s.PeakWind = float.Lerp(2f, 4f, power) * windScale;
                s.Cloud = float.Lerp(0.6f, 0.9f, power);
                s.Calm = float.Lerp(0.5f, 0.8f, power);
                break;
        }
    }

    /// <summary>A per-band table (from the south pole up) at a latitude, linear between band centres.</summary>
    private float Band(float[] table, double latitude)
    {
        var x = latitude / HalfPi * _bands + _bands - 0.5;
        var k = (int)Math.Floor(x);
        var t = (float)(x - k);
        var last = table.Length - 1;
        return float.Lerp(table[Math.Clamp(k, 0, last)], table[Math.Clamp(k + 1, 0, last)], t);
    }

    private static float Vary(ulong seed, ulong salt, int band, float variation) =>
        1f + variation * (2f * new Rng(Mix(seed ^ salt) + Golden * (ulong)band).NextFloat() - 1f);

    /// <summary>Sine and cosine; within ±<see cref="SeriesLimit"/> by Taylor series, exact to rounding (next term &lt; 3e-16).</summary>
    private static (double Sin, double Cos) SinCos(double a)
    {
        if (Math.Abs(a) > SeriesLimit)
            return Math.SinCos(a);

        var a2 = a * a;
        return (a * (1.0 - a2 / 6.0 * (1.0 - a2 / 20.0 * (1.0 - a2 / 42.0))), 1.0 - a2 / 2.0 * (1.0 - a2 / 12.0 * (1.0 - a2 / 30.0)));
    }

    private static double Wrap(double angle) => angle - Tau * Math.Floor(angle / Tau);

    /// <summary>(1 − (q / core)²)³ inside the core, 0 outside.</summary>
    private static float Core(float q2, float core)
    {
        var w = 1f - q2 / (core * core);
        return w > 0f ? w * w * w : 0f;
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static ulong Key(ulong seed, int row, int cell, int slot) =>
        Mix(seed + Golden * Mix((ulong)row + Golden * Mix((ulong)cell + Golden * (ulong)slot)));

    /// <summary>A point's latitude, longitude and the reach circle about it, shared by every row it visits.</summary>
    private readonly struct Window
    {
        public readonly double Longitude;
        public readonly double SinColat;
        public readonly double CosColat;
        public readonly double SinReach;
        public readonly double CosReach;
        public readonly int Row0;
        public readonly int Row1;

        public Window(SphereWeatherField field, double3 unit, double extra)
        {
            var reach = Math.Min(Math.PI, field.Reach + extra);
            CosColat = Math.Clamp(double3.Dot(unit, field.Pole), -1.0, 1.0);
            SinColat = Math.Sqrt(Math.Max(0.0, 1.0 - CosColat * CosColat));
            (SinReach, CosReach) = extra == 0.0 ? (field._sinReach, field._cosReach) : Math.SinCos(reach);
            Longitude = Math.Atan2(double3.Dot(unit, field.PrimeEast), double3.Dot(unit, field.Prime));
            var latitude = Math.Asin(CosColat);
            Row0 = Math.Clamp((int)Math.Floor((latitude - reach + HalfPi) / field.RowHeight), 0, field.Rows - 1);
            Row1 = Math.Clamp((int)Math.Floor((latitude + reach + HalfPi) / field.RowHeight), 0, field.Rows - 1);
        }
    }

    /// <summary>SplitMix64: stable across runtimes, unlike System.Random.</summary>
    private struct Rng(ulong state)
    {
        private ulong _state = state;

        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

        public float NextFloat() => (Next() >> 40) * (1f / (1 << 24));

        private ulong Next() => Mix(_state += Golden);
    }
}
