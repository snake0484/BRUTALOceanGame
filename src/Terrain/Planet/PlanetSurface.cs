using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// The generated surface height H(dir) about the datum radius: a pure function of seed + direction (planets.md
/// "Generation"). Three octave groups of double-lattice 3D noise sampled at dir × Radius: a continent mask, ridges and
/// detail. Each octave is clamped to [−1, 1], so its amplitude bounds it exactly. Band-limited per LOD: octaves shorter
/// than 2 voxels are skipped. <see cref="Bounds"/> gives rigorous height bounds over a cap of directions by interval
/// arithmetic (the noise's Lipschitz bound per octave), for the shell classification.
/// </summary>
public sealed class PlanetSurface
{
    public const int ContinentOctaves = 5;
    public const int RidgeOctaves = 3;
    public const int DetailOctaves = 15;
    public const int Octaves = ContinentOctaves + RidgeOctaves + DetailOctaves;
    public const int RidgeStart = ContinentOctaves;
    public const int DetailStart = ContinentOctaves + RidgeOctaves;

    /// <summary>Longest detail wavelength (m); detail octave i has 2^(15−i) m, down to 2 m.</summary>
    public const double DetailWavelength = 32768.0;
    public const float DetailAmplitude = 450f;
    public const float DetailGain = 0.574f;

    /// <summary>Continent wavelength over radius; ridges start at a fifth of it.</summary>
    private const double ContinentScale = 1.25;
    private const double RidgeScale = 0.25;

    private const float ShelfDepth = 150f;
    private const float ShelfWidth = 0.03f;
    private const float AbyssWidth = 0.3f;
    private const float AbyssClearance = 600f;
    private const float LandRise = 3000f;
    private const float MountainStart = 0.1f;
    private const float MountainRamp = 0.3f;
    private const float CoastDetail = 0.15f;
    private const float DetailRamp = 0.15f;

    /// <summary>Continent value past which the inland clamp holds (the coastal ramp's end), and how fast the floor drops before it.</summary>
    private const float InlandStart = 0.06f;
    private const float FloorDrop = 1e5f;

    /// <summary>Float rounding allowance on bounds (m).</summary>
    private const float BoundsSlack = 0.05f;

    public readonly PlanetParams Params;
    public readonly double Radius;
    public readonly float SeaHeight;
    public readonly bool HasSea;

    /// <summary>Ground at or below this (m about Radius) is protected (D30); −∞ with no sea.</summary>
    public readonly float ProtectHeight;

    private readonly double[] _frequency = new double[Octaves];
    private readonly float[] _wavelength = new float[Octaves];
    private readonly float[] _amplitude = new float[Octaves];
    private readonly uint[] _seed = new uint[Octaves];
    private readonly double[] _offsetX = new double[Octaves];
    private readonly double[] _offsetY = new double[Octaves];
    private readonly double[] _offsetZ = new double[Octaves];
    private readonly float _abyss;
    private readonly float _floor;

    public PlanetSurface(in PlanetParams p)
    {
        Params = p;
        Radius = p.Radius;
        HasSea = p.HasSea;
        SeaHeight = p.SeaHeight;
        ProtectHeight = HasSea ? SeaHeight + p.ShoreProtectHeight : float.NegativeInfinity;
        _abyss = p.MinHeight + AbyssClearance;
        _floor = HasSea ? ProtectHeight + 1f : float.NegativeInfinity;

        var baseSeed = Noise.SeedOf(p.Seed);
        var continentNorm = 0f;
        var ridgeNorm = 0f;
        var detailAmplitude = DetailAmplitude;

        for (var i = 0; i < Octaves; i++)
        {
            double wavelength;
            float amplitude;

            if (i < RidgeStart)
            {
                wavelength = ContinentScale * p.Radius / (1 << i);
                amplitude = 1f / (1 << i);
                continentNorm += amplitude;
            }
            else if (i < DetailStart)
            {
                wavelength = RidgeScale * p.Radius / (1 << (i - RidgeStart));
                amplitude = 1f / (1 << (i - RidgeStart));
                ridgeNorm += amplitude;
            }
            else
            {
                wavelength = DetailWavelength / (1 << (i - DetailStart));
                amplitude = detailAmplitude;
                detailAmplitude *= DetailGain;
            }

            var h = Noise.Mix(baseSeed + (uint)i * 0x9E3779B9u);
            _frequency[i] = 1.0 / wavelength;
            _wavelength[i] = (float)wavelength;
            _amplitude[i] = amplitude;
            _seed[i] = h;
            _offsetX[i] = (Noise.Mix(h ^ 0x68E31DA4u) & 0xFFFF) * (1.0 / 64.0);
            _offsetY[i] = (Noise.Mix(h ^ 0xB5297A4Du) & 0xFFFF) * (1.0 / 64.0);
            _offsetZ[i] = (Noise.Mix(h ^ 0x1B56C4E9u) & 0xFFFF) * (1.0 / 64.0);
        }

        for (var i = 0; i < RidgeStart; i++)
            _amplitude[i] /= continentNorm;

        for (var i = RidgeStart; i < DetailStart; i++)
            _amplitude[i] /= ridgeNorm;
    }

    public ReadOnlySpan<float> Wavelengths => _wavelength;

    public ReadOnlySpan<float> Amplitudes => _amplitude;

    /// <summary>Octaves with a wavelength under 2 voxels are skipped at this LOD (planets.md "Detail ladder").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MinWavelength(int lod) => 2f * (1 << lod);

    /// <summary>How many octaves LOD <paramref name="lod"/> evaluates.</summary>
    public int OctaveCount(int lod)
    {
        var min = MinWavelength(lod);
        var count = 0;

        for (var i = 0; i < Octaves; i++)
        {
            if (_wavelength[i] >= min)
                count++;
        }

        return count;
    }

    /// <summary>H at a unit direction with the octaves LOD <paramref name="lod"/> evaluates. Allocation-free, deterministic.</summary>
    public float Height(double dx, double dy, double dz, int lod = 0)
    {
        float c = 0f, r = 0f, d = 0f;
        Accumulate(dx * Radius, dy * Radius, dz * Radius, MinWavelength(lod), float.PositiveInfinity, ref c, ref r, ref d);
        return Compose(c, r, d);
    }

    /// <summary>
    /// Adds the octaves with wavelength in [<paramref name="minWavelength"/>, <paramref name="maxWavelength"/>) at datum
    /// point q into the three group sums (continent, ridge, detail).
    /// </summary>
    public void Accumulate(double qx, double qy, double qz, float minWavelength, float maxWavelength, ref float continent, ref float ridge, ref float detail)
    {
        for (var i = 0; i < Octaves; i++)
        {
            var wavelength = _wavelength[i];

            if (wavelength < minWavelength || wavelength >= maxWavelength)
                continue;

            var f = _frequency[i];
            var n = Math.Clamp(Noise.Gradient3(qx * f + _offsetX[i], qy * f + _offsetY[i], qz * f + _offsetZ[i], _seed[i]), -1f, 1f);
            var a = _amplitude[i];

            if (i < RidgeStart)
            {
                continent += a * n;
            }
            else if (i < DetailStart)
            {
                var k = 1f - MathF.Abs(n);
                ridge += a * k * k;
            }
            else
            {
                detail += a * n;
            }
        }
    }

    /// <summary>The height from the three group sums: continent shape, ridges, detail, the inland clamp and the relief bounds.</summary>
    public float Compose(float continent, float ridge, float detail)
    {
        var c = continent + Params.LandBias;
        var h = Shape(c) + Mountains(c) * ridge + DetailScale(c) * detail;
        h = MathF.Max(h, Floor(c));
        return Math.Clamp(h, Params.MinHeight, Params.MaxHeight);
    }

    /// <summary>
    /// Rigorous bounds of H (every LOD's band limit included) over the cap of directions within <paramref name="angle"/>
    /// (rad) of unit direction d. Each octave's interval is its value at the centre ± Lipschitz × frequency × arc, clamped to
    /// [−1, 1]; octaves too fine to tell are not evaluated. The composition is monotone per term.
    /// </summary>
    public void Bounds(double dx, double dy, double dz, double angle, out float lo, out float hi)
    {
        var rho = angle * Radius;
        var qx = dx * Radius;
        var qy = dy * Radius;
        var qz = dz * Radius;
        float cLo = 0f, cHi = 0f, rLo = 0f, rHi = 0f, dLo = 0f, dHi = 0f;

        for (var i = 0; i < Octaves; i++)
        {
            var f = _frequency[i];
            var spread = (float)(Noise.GradientLipschitz * f * rho);
            float nLo = -1f, nHi = 1f;

            if (spread < 1f)
            {
                var n = Math.Clamp(Noise.Gradient3(qx * f + _offsetX[i], qy * f + _offsetY[i], qz * f + _offsetZ[i], _seed[i]), -1f, 1f);
                nLo = MathF.Max(n - spread, -1f);
                nHi = MathF.Min(n + spread, 1f);
            }

            var a = _amplitude[i];

            if (i < RidgeStart)
            {
                cLo += a * nLo;
                cHi += a * nHi;
            }
            else if (i < DetailStart)
            {
                var absLo = nLo <= 0f && nHi >= 0f ? 0f : MathF.Min(MathF.Abs(nLo), MathF.Abs(nHi));
                var absHi = MathF.Max(MathF.Abs(nLo), MathF.Abs(nHi));
                var kLo = 1f - absHi;
                var kHi = 1f - absLo;
                rLo += a * kLo * kLo;
                rHi += a * kHi * kHi;
            }
            else
            {
                dLo += a * nLo;
                dHi += a * nHi;
            }
        }

        cLo += Params.LandBias;
        cHi += Params.LandBias;

        var mLo = Mountains(cLo);
        var mHi = Mountains(cHi);
        var sLo = DetailScale(cLo);
        var sHi = DetailScale(cHi);
        var detailLo = MathF.Min(sLo * dLo, sHi * dLo);
        var detailHi = MathF.Max(sLo * dHi, sHi * dHi);
        lo = Shape(cLo) + mLo * rLo + detailLo;
        hi = Shape(cHi) + mHi * rHi + detailHi;
        lo = MathF.Max(lo, Floor(cLo));
        hi = MathF.Max(hi, Floor(cHi));
        lo = Math.Clamp(lo - BoundsSlack, Params.MinHeight, Params.MaxHeight);
        hi = Math.Clamp(hi + BoundsSlack, Params.MinHeight, Params.MaxHeight);
    }

    /// <summary>Sea mask: generated ground below sea level. False with no sea.</summary>
    public bool IsSea(double dx, double dy, double dz) => HasSea && Height(dx, dy, dz) < SeaHeight;

    /// <summary>Base shape by continent value, monotone: shelf and abyss below 0, a plain rising inland above it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Shape(float c)
    {
        if (c >= 0f)
            return SeaHeight + LandRise * c;

        var x = -c;
        return SeaHeight - ShelfDepth * Smooth01(x / ShelfWidth) - (SeaHeight - ShelfDepth - _abyss) * Smooth01((x - ShelfWidth) / AbyssWidth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Mountains(float c) => Params.MountainHeight * Smooth01((c - MountainStart) / MountainRamp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DetailScale(float c) => CoastDetail + (1f - CoastDetail) * Smooth01(c / DetailRamp);

    /// <summary>
    /// Inland clamp: past the coastal ramp the ground stands at least 1 m above the protected band, so the only ground at
    /// or below it is seabed or coast joined to the sea. Before the ramp's end the floor drops away steeply (monotone).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Floor(float c) => _floor - MathF.Max(0f, InlandStart - c) * FloorDrop;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Smooth01(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v * v * (3f - 2f * v);
    }
}
