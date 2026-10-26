namespace BrutalGame.Water.Ocean;

/// <summary>
/// Fetch-limited JONSWAP spectrum and linear dispersion.
/// </summary>
public static class Jonswap
{
    public const double Gravity = 9.81;
    public const double MinFetch = 1000.0;

    /// <summary>Peak angular frequency ωp = 22 (g² / (U F))^(1/3).</summary>
    public static double PeakOmega(double windSpeed, double fetch) =>
        22.0 * Math.Cbrt(Gravity * Gravity / (windSpeed * fetch));

    /// <summary>Phillips constant α = 0.076 (U² / (F g))^0.22.</summary>
    public static double Alpha(double windSpeed, double fetch) =>
        0.076 * Math.Pow(windSpeed * windSpeed / (fetch * Gravity), 0.22);

    /// <summary>Spectral energy density S(ω) in m²·s.</summary>
    public static double Spectrum(double omega, double alpha, double peakOmega, double gamma)
    {
        if (omega <= 0.0)
            return 0.0;

        var sigma = omega <= peakOmega ? 0.07 : 0.09;
        var d = (omega - peakOmega) / (sigma * peakOmega);
        var r = Math.Exp(-0.5 * d * d);
        var ratio = peakOmega / omega;
        var ratio2 = ratio * ratio;
        var omega2 = omega * omega;

        return alpha * Gravity * Gravity / (omega2 * omega2 * omega) * Math.Exp(-1.25 * ratio2 * ratio2) * Math.Pow(gamma, r);
    }

    /// <summary>ω = sqrt(g k tanh(k h)).</summary>
    public static double Omega(double k, double depth) =>
        double.IsFinite(depth) ? Math.Sqrt(Gravity * k * Math.Tanh(k * depth)) : Math.Sqrt(Gravity * k);

    /// <summary>Solves ω² = g k tanh(k h) for k with Newton iterations.</summary>
    public static double WaveNumber(double omega, double depth)
    {
        var deep = omega * omega / Gravity;
        if (!double.IsFinite(depth) || deep * depth > 10.0)
            return deep;

        var k = deep / Math.Sqrt(Math.Tanh(deep * depth));
        for (var i = 0; i < 6; i++)
        {
            var t = Math.Tanh(k * depth);
            var f = Gravity * k * t - omega * omega;
            var df = Gravity * t + Gravity * k * depth * (1.0 - t * t);
            k -= f / df;
        }

        return k;
    }

    /// <summary>
    /// Explicit wavenumber (Fenton and McKee 1990): k0 / tanh((k0 h)^(3/4))^(2/3), within about 1.5% of
    /// <see cref="WaveNumber"/> and far cheaper; for per-texel and per-segment work each tick.
    /// </summary>
    public static float WaveNumberApprox(float omega, float depth)
    {
        var deep = omega * omega / (float)Gravity;
        var kh = deep * MathF.Max(depth, 1e-3f);
        return kh > 10f ? deep : deep / MathF.Pow(MathF.Tanh(MathF.Pow(kh, 0.75f)), 2f / 3f);
    }

    /// <summary>Normalisation Ns so that Ns · cos^(2s)(θ/2) integrates to 1 over [-π, π].</summary>
    public static double SpreadingNormalisation(double s)
    {
        const int steps = 512;
        var step = Math.Tau / steps;
        var sum = 0.0;

        for (var i = 0; i < steps; i++)
            sum += Math.Pow(Math.Max(Math.Cos(0.5 * (-Math.PI + (i + 0.5) * step)), 0.0), 2.0 * s) * step;

        return sum > 0.0 ? 1.0 / sum : 0.0;
    }
}
