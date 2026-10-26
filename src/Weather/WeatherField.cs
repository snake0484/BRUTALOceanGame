using System.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// Analytic weather, deterministic from the climate's seed and time, so it can be evaluated anywhere and needs no
/// networking. Pressure systems spawn in cells of a frame that drifts with the steering wind. Each cell has
/// <see cref="Slots"/> slots that run back-to-back lives (grow, mature, decay, a gap), each life a new kind, place and
/// size. Profiles have compact support, so a point only sees the systems of its own and its eight neighbouring cells.
/// Read-only apart from <see cref="Climate"/>; safe to call from jobs.
/// </summary>
public sealed class WeatherField(WeatherClimate climate)
{
    public const float CellSize = 24_000f;
    public const int Slots = 2;
    public const float MaxRadius = 16_000f;

    /// <summary>Most systems a point sample can see: 3 × 3 cells.</summary>
    public const int MaxPointSystems = 9 * Slots;

    private const double MinPeriod = 1800.0;
    private const double MaxPeriod = 3600.0;
    private const double MinLifeShare = 0.6;
    private const double MaxLifeShare = 0.95;
    private const float GrowUntil = 0.3f;
    private const float DecayFrom = 0.65f;

    /// <summary>Systems move with this share of the prevailing wind, plus a drift of their own of at most MaxOwnSpeed.</summary>
    private const float SteeringShare = 0.8f;
    private const float MaxOwnSpeed = 1.5f;

    /// <summary>Spawn points keep this share of the cell from its edges.</summary>
    private const float SpawnMargin = 0.1f;

    /// <summary>
    /// Furthest a system's edge gets from its spawn cell: its drift over the longest life plus its radius. Below CellSize,
    /// so a point needs at most 3 × 3 cells.
    /// </summary>
    private const float MaxReach = MaxRadius + MaxOwnSpeed * (float)(MaxPeriod * MaxLifeShare);

    private const float StormShare = 0.1f;
    private const float StormShareByStorminess = 0.4f;
    private const float LowShare = 0.3f;

    private const float RainCore = 0.6f;
    private const float ConvectionCore = 0.45f;
    private const float BaseGust = 0.2f;
    private const float StormGust = 0.5f;

    /// <summary>Surface wind crosses the isobars into lows (out of highs) by this angle over the sea.</summary>
    private const float Inflow = 20f * MathF.PI / 180f;

    /// <summary>1 / max of q (1 − q²)², reached at q = 1/√5: the circulation peaks at PeakWind there.</summary>
    private const float RingNorm = 3.4938562f;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    private static readonly float SinInflow = MathF.Sin(Inflow);
    private static readonly float CosInflow = MathF.Cos(Inflow);

    public WeatherClimate Climate = climate;

    public Vector2 Prevailing => Climate.PrevailingSpeed * new Vector2(MathF.Cos(Climate.PrevailingDirection), MathF.Sin(Climate.PrevailingDirection));

    public Vector2 Steering => SteeringShare * Prevailing;

    public WeatherSample Sample(float x, float z, double time)
    {
        var p = new Vector2(x, z);
        Span<PressureSystem> systems = stackalloc PressureSystem[MaxPointSystems];
        var count = Gather(p, p, time, systems);
        return Evaluate(p, systems[..count]);
    }

    /// <summary>
    /// Writes the systems alive at <paramref name="time"/> whose extent overlaps [min, max] and returns how many, at most
    /// the span's length. Ordered by cell row, cell, then slot.
    /// </summary>
    public int Gather(Vector2 min, Vector2 max, double time, Span<PressureSystem> systems)
    {
        var steering = Steering;
        var shiftX = steering.X * time;
        var shiftZ = steering.Y * time;
        var cx0 = (long)Math.Floor((min.X - shiftX - MaxReach) / CellSize);
        var cx1 = (long)Math.Floor((max.X - shiftX + MaxReach) / CellSize);
        var cz0 = (long)Math.Floor((min.Y - shiftZ - MaxReach) / CellSize);
        var cz1 = (long)Math.Floor((max.Y - shiftZ + MaxReach) / CellSize);
        var count = 0;

        for (var cz = cz0; cz <= cz1; cz++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                for (var slot = 0; slot < Slots; slot++)
                {
                    if (!TryLife(cx, cz, slot, time, shiftX, shiftZ, out var s))
                        continue;

                    if (s.Centre.X + s.Radius <= min.X || s.Centre.X - s.Radius >= max.X || s.Centre.Y + s.Radius <= min.Y || s.Centre.Y - s.Radius >= max.Y)
                        continue;

                    if (count == systems.Length)
                        return count;

                    systems[count++] = s;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Weather at <paramref name="p"/> from the given systems; those that don't reach it add nothing. Lows and storms
    /// union their cloud, rain and convection; highs scale all three down and still the prevailing wind. Each system's
    /// circulation is the gradient wind of its pressure profile: cyclonic (counter-clockwise from above) round lows,
    /// anticyclonic round highs, crossing the isobars by <see cref="Inflow"/>.
    /// </summary>
    public WeatherSample Evaluate(Vector2 p, ReadOnlySpan<PressureSystem> systems)
    {
        var dry = 1f - Climate.BaseCloud;
        var dryRain = 1f;
        var still = 1f;
        var clear = 1f;
        var calm = 1f;
        var pressure = 0f;
        var wind = Vector2.Zero;

        foreach (ref readonly var s in systems)
        {
            var d = p - s.Centre;
            var q2 = d.LengthSquared() / (s.Radius * s.Radius);

            if (q2 >= 1f)
                continue;

            var w = 1f - q2;
            var g = w * w * w * s.Strength;
            var spin = s.Kind == PressureKind.High ? -1f : 1f;
            var circulation = spin * s.PeakWind * s.Strength * RingNorm * w * w / s.Radius;
            wind += circulation * (CosInflow * new Vector2(d.Y, -d.X) - SinInflow * d);
            pressure += s.Pressure * g;

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

        var storm = (1f - still) * clear;
        return new WeatherSample
        {
            Wind = calm * Prevailing + wind,
            Cloud = (1f - dry) * clear,
            Rain = (1f - dryRain) * clear,
            Storm = storm,
            Pressure = pressure,
            Gust = BaseGust + StormGust * storm,
        };
    }

    private bool TryLife(long cx, long cz, int slot, double time, double shiftX, double shiftZ, out PressureSystem system)
    {
        system = default;
        var key = Key(Climate.Seed, cx, cz, slot);
        var rng = new Rng(key);
        var period = MinPeriod + (MaxPeriod - MinPeriod) * rng.NextDouble();
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

        var spawnX = (cx + (double)(SpawnMargin + (1f - 2f * SpawnMargin) * rng.NextFloat())) * CellSize;
        var spawnZ = (cz + (double)(SpawnMargin + (1f - 2f * SpawnMargin) * rng.NextFloat())) * CellSize;
        var heading = MathF.Tau * rng.NextFloat();
        var drift = MaxOwnSpeed * rng.NextFloat() * age;
        system.Centre = new Vector2((float)(spawnX + MathF.Cos(heading) * drift + shiftX), (float)(spawnZ + MathF.Sin(heading) * drift + shiftZ));

        var stormShare = StormShare + StormShareByStorminess * Climate.Storminess;
        var roll = rng.NextFloat();
        system.Kind = roll < stormShare ? PressureKind.Storm : roll < stormShare + LowShare ? PressureKind.Low : PressureKind.High;
        Shape(ref system, rng.NextFloat(), rng.NextFloat(), Climate.Storminess);
        return true;
    }

    /// <summary>Size and peaks by kind; one power roll drives depth, wind and rain together, so deep storms are wild ones.</summary>
    private static void Shape(ref PressureSystem s, float size, float power, float storminess)
    {
        switch (s.Kind)
        {
            case PressureKind.Storm:
                s.Radius = float.Lerp(8_000f, 12_000f, size);
                s.Pressure = -float.Lerp(12f, 25f, power);
                s.PeakWind = float.Lerp(14f, 26f, power) * (0.6f + 0.6f * storminess);
                s.Cloud = 1f;
                s.Rain = float.Lerp(0.8f, 1f, power);
                s.Convection = float.Lerp(0.6f, 1f, power);
                break;
            case PressureKind.Low:
                s.Radius = float.Lerp(10_000f, MaxRadius, size);
                s.Pressure = -float.Lerp(5f, 10f, power);
                s.PeakWind = float.Lerp(5f, 10f, power);
                s.Cloud = float.Lerp(0.85f, 1f, power);
                s.Rain = float.Lerp(0.2f, 0.5f, power);
                s.Convection = 0.2f * power;
                break;
            default:
                s.Radius = float.Lerp(12_000f, MaxRadius, size);
                s.Pressure = float.Lerp(5f, 10f, power);
                s.PeakWind = float.Lerp(2f, 4f, power);
                s.Cloud = float.Lerp(0.6f, 0.9f, power);
                s.Calm = float.Lerp(0.5f, 0.8f, power);
                break;
        }
    }

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

    private static ulong Key(ulong seed, long cx, long cz, int slot) =>
        Mix(seed + Golden * Mix((ulong)cx + Golden * Mix((ulong)cz + Golden * (ulong)slot)));

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
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
