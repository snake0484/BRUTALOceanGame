namespace BrutalGame.Chemistry;

/// <summary>One-dimensional ideal-gas flow: isentropic relations, area–Mach, and mass flow through an orifice.</summary>
public static class GasDynamics
{
    public const double StandardGravity = 9.80665;

    /// <summary>Downstream / upstream pressure below which the flow chokes.</summary>
    public static double CriticalRatio(double gamma) => Math.Pow(2.0 / (gamma + 1.0), gamma / (gamma - 1.0));

    /// <summary>p / p₀ at Mach <paramref name="mach"/>.</summary>
    public static double PressureRatio(double mach, double gamma) => Math.Pow(1.0 + 0.5 * (gamma - 1.0) * mach * mach, -gamma / (gamma - 1.0));

    /// <summary>T / T₀ at Mach <paramref name="mach"/>.</summary>
    public static double TemperatureRatio(double mach, double gamma) => 1.0 / (1.0 + 0.5 * (gamma - 1.0) * mach * mach);

    /// <summary>A / A* at Mach <paramref name="mach"/>.</summary>
    public static double AreaRatio(double mach, double gamma)
    {
        var g = gamma;
        return 1.0 / mach * Math.Pow(2.0 / (g + 1.0) * (1.0 + 0.5 * (g - 1.0) * mach * mach), (g + 1.0) / (2.0 * (g - 1.0)));
    }

    /// <summary>The Mach number with A / A* = <paramref name="ratio"/> (≥ 1), supersonic or subsonic branch (bisection, exact to 1e-10).</summary>
    public static double MachFromArea(double ratio, double gamma, bool supersonic)
    {
        if (ratio <= 1.0)
            return 1.0;

        double lo = supersonic ? 1.0 : 1e-9, hi = supersonic ? 50.0 : 1.0;

        for (var i = 0; i < 100; i++)
        {
            var mid = 0.5 * (lo + hi);
            var f = AreaRatio(mid, gamma) - ratio;

            // Area ratio falls with Mach on the subsonic branch and rises on the supersonic one.
            if ((f > 0) == supersonic)
                hi = mid;
            else
                lo = mid;

            if (hi - lo < 1e-10)
                break;
        }

        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// Mass flow (kg/s) of gas at stagnation p₀, T₀ through an orifice of area <paramref name="area"/> and discharge
    /// coefficient <paramref name="cd"/> into <paramref name="downstream"/> pressure: the compressible orifice equation,
    /// choked below the critical ratio. Zero when the downstream pressure is not lower.
    /// </summary>
    public static double MassFlow(double cd, double area, double p0, double t0, double gamma, double specificR, double downstream)
    {
        if (p0 <= downstream || p0 <= 0 || t0 <= 0)
            return 0.0;

        var r = Math.Max(downstream / p0, CriticalRatio(gamma));
        var term = 2.0 * gamma / ((gamma - 1.0) * specificR * t0) * (Math.Pow(r, 2.0 / gamma) - Math.Pow(r, (gamma + 1.0) / gamma));
        return cd * area * p0 * Math.Sqrt(Math.Max(term, 0.0));
    }
}
