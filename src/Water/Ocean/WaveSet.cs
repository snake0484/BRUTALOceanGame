namespace BrutalGame.Water.Ocean;

/// <summary>
/// One discretised spectrum: fixed wave components drawn from JONSWAP with cos-2s spreading for the local wind sea, and,
/// independent of the wind, a narrow Gaussian band of long-period groundswell (docs/surf-research.md).
/// Storage is preallocated so rebuilding on wind change does not allocate.
/// </summary>
internal sealed class WaveSet
{
    public const int Capacity = 256;
    private const int SpreadTableSize = 256;

    /// <summary>
    /// Relative bandwidth taken for the wind sea's peak (about JONSWAP's σ around it): a set every 1 / (2 × this) = 8
    /// waves.
    /// </summary>
    public const double WindSeaBandwidth = 0.0625;

    /// <summary>The groundswell is the band the surf carrier follows once its variance is this share of the wind sea's (height half).</summary>
    private const double SwellDominance = 0.25;

    public readonly float[] Kx = new float[Capacity];
    public readonly float[] Kz = new float[Capacity];
    public readonly float[] DirX = new float[Capacity];
    public readonly float[] DirZ = new float[Capacity];
    public readonly float[] Amplitude = new float[Capacity];
    public readonly float[] Omega = new float[Capacity];
    public readonly float[] Coth = new float[Capacity];
    public readonly double[] Phase0 = new double[Capacity];
    public int Count;

    /// <summary>The wind sea's peak frequency (0 when calm).</summary>
    public double PeakOmega;
    public SwellParams Params;

    /// <summary>Variance (m²) of the wind sea and of the groundswell, and the groundswell's peak frequency.</summary>
    public double WindSeaEnergy;
    public double SwellEnergy;
    public double SwellOmega;

    /// <summary>
    /// The groundswell dominates once its height is at least half the wind sea's: long-period swell shoals more and
    /// carries the surf, so it is the band the surf carrier, the coastal maps and the sets follow.
    /// </summary>
    public bool SwellDominant => SwellEnergy > 0.0 && SwellEnergy >= SwellDominance * WindSeaEnergy;

    /// <summary>Peak frequency, direction (radians, travelling toward) and relative bandwidth of the dominant band.</summary>
    public double CarrierOmega => SwellDominant ? SwellOmega : PeakOmega;
    public float CarrierDirection => SwellDominant ? Params.SwellDirection : Params.WindDirection;
    public double CarrierBandwidth => SwellDominant ? Params.SwellBandwidth : WindSeaBandwidth;

    public void Build(in SwellParams p, ulong seed)
    {
        Count = 0;
        Params = p;
        PeakOmega = 0.0;
        WindSeaEnergy = 0.0;
        SwellEnergy = 0.0;
        SwellOmega = 0.0;

        var rng = new SplitMix64(seed);
        BuildWindSea(p, ref rng);
        BuildGroundswell(p, ref rng);
    }

    private void BuildWindSea(in SwellParams p, ref SplitMix64 rng)
    {
        if (p.WindSpeed < 0.1f || p.FrequencyBands <= 0 || p.DirectionsPerBand <= 0)
            return;

        double windSpeed = p.WindSpeed;
        var fetch = Math.Max(p.Fetch, Jonswap.MinFetch);
        double depth = p.Depth;
        var alpha = Jonswap.Alpha(windSpeed, fetch);
        PeakOmega = Jonswap.PeakOmega(windSpeed, fetch);

        var omegaMin = 0.5 * PeakOmega;
        var omegaMax = Jonswap.Omega(2.0 * Math.PI / Math.Max(p.MinWavelength, 0.5f), depth);
        if (omegaMax <= omegaMin)
            return;

        var bands = Math.Min(p.FrequencyBands, Capacity / p.DirectionsPerBand);
        var dirs = p.DirectionsPerBand;

        Span<double> spreadCdf = stackalloc double[SpreadTableSize + 1];
        BuildSpreadCdf(spreadCdf, p.Spread);

        var lnMin = Math.Log(omegaMin);
        var lnStep = (Math.Log(omegaMax) - lnMin) / bands;

        for (var b = 0; b < bands; b++)
        {
            var lo = Math.Exp(lnMin + b * lnStep);
            var hi = Math.Exp(lnMin + (b + 1) * lnStep);
            var dOmega = hi - lo;

            for (var d = 0; d < dirs; d++)
            {
                var omega = lo + dOmega * rng.NextDouble();
                var energy = Jonswap.Spectrum(omega, alpha, PeakOmega, p.Gamma);
                var amplitude = Math.Sqrt(2.0 * energy * dOmega / dirs);
                var theta = p.WindDirection + SampleSpread(spreadCdf, (d + rng.NextDouble()) / dirs);
                Add(omega, amplitude, theta, depth, rng.NextDouble() * Math.Tau);
                WindSeaEnergy += 0.5 * amplitude * amplitude;
            }
        }
    }

    /// <summary>
    /// Groundswell: a Gaussian band of SwellBandwidth around the swell's peak frequency, sampled out to ±3 σ, with narrow
    /// cos-2s spreading about its own direction; amplitudes scaled so its significant height is exactly SwellHeight.
    /// </summary>
    private void BuildGroundswell(in SwellParams p, ref SplitMix64 rng)
    {
        if (p.SwellHeight <= 0f || p.SwellPeriod <= 0f || p.SwellBands <= 0 || p.SwellDirections <= 0)
            return;

        var dirs = p.SwellDirections;
        var bands = Math.Min(p.SwellBands, (Capacity - Count) / dirs);
        if (bands <= 0)
            return;

        double depth = p.Depth;
        var peak = Math.Tau / p.SwellPeriod;
        var sigma = Math.Max(p.SwellBandwidth, 0.005) * peak;
        var lo = Math.Max(peak - 3.0 * sigma, 0.2 * peak);
        var dOmega = (peak + 3.0 * sigma - lo) / bands;

        Span<double> spreadCdf = stackalloc double[SpreadTableSize + 1];
        BuildSpreadCdf(spreadCdf, p.SwellSpread);

        var first = Count;
        var variance = 0.0;

        for (var b = 0; b < bands; b++)
        {
            for (var d = 0; d < dirs; d++)
            {
                var omega = lo + dOmega * (b + rng.NextDouble());
                var offset = (omega - peak) / sigma;
                var amplitude = Math.Sqrt(2.0 * Math.Exp(-0.5 * offset * offset) * dOmega / dirs);
                var theta = p.SwellDirection + SampleSpread(spreadCdf, (d + rng.NextDouble()) / dirs);
                Add(omega, amplitude, theta, depth, rng.NextDouble() * Math.Tau);
                variance += 0.5 * amplitude * amplitude;
            }
        }

        // Hs = 4 √variance, exactly as asked.
        SwellEnergy = p.SwellHeight * p.SwellHeight / 16.0;
        SwellOmega = peak;
        var scale = variance > 0.0 ? (float)Math.Sqrt(SwellEnergy / variance) : 0f;
        for (var i = first; i < Count; i++)
            Amplitude[i] *= scale;
    }

    private void Add(double omega, double amplitude, double theta, double depth, double phase)
    {
        var k = Jonswap.WaveNumber(omega, depth);
        var (sin, cos) = Math.SinCos(theta);

        var i = Count++;
        Kx[i] = (float)(k * cos);
        Kz[i] = (float)(k * sin);
        DirX[i] = (float)cos;
        DirZ[i] = (float)sin;
        Amplitude[i] = (float)amplitude;
        Omega[i] = (float)omega;
        Coth[i] = double.IsFinite(depth) ? (float)(1.0 / Math.Tanh(k * depth)) : 1f;
        Phase0[i] = phase;
    }

    /// <summary>
    /// Cumulative distribution of D(θ) ∝ cos^(2s)(θ/2) over θ ∈ [-π, π].
    /// </summary>
    private static void BuildSpreadCdf(Span<double> cdf, double s)
    {
        var step = Math.Tau / SpreadTableSize;
        var prev = Spreading(-Math.PI, s);
        cdf[0] = 0.0;

        for (var i = 1; i <= SpreadTableSize; i++)
        {
            var next = Spreading(-Math.PI + i * step, s);
            cdf[i] = cdf[i - 1] + 0.5 * (prev + next) * step;
            prev = next;
        }

        var total = cdf[SpreadTableSize];
        for (var i = 1; i <= SpreadTableSize; i++)
            cdf[i] /= total;
    }

    private static double Spreading(double theta, double s) => Math.Pow(Math.Max(Math.Cos(0.5 * theta), 0.0), 2.0 * s);

    private static double SampleSpread(ReadOnlySpan<double> cdf, double u)
    {
        var lo = 0;
        var hi = cdf.Length - 1;

        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (cdf[mid] < u)
                lo = mid;
            else
                hi = mid;
        }

        var span = cdf[hi] - cdf[lo];
        var t = span > 0.0 ? (u - cdf[lo]) / span : 0.0;
        return -Math.PI + (lo + t) * (Math.Tau / SpreadTableSize);
    }
}
