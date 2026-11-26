namespace BrutalGame.Chemistry;

/// <summary>What a nozzle does at one chamber state and ambient pressure.</summary>
public readonly record struct NozzlePerformance(
    double MassFlow,
    double Thrust,
    double ExitMach,
    double ExitPressure,
    double ExitVelocity,
    double DivergenceEfficiency,
    bool Choked,
    bool Separated,
    double SeparationFraction)
{
    /// <summary>Specific impulse (s).</summary>
    public double Isp => MassFlow > 0 ? Thrust / (MassFlow * GasDynamics.StandardGravity) : 0.0;
}

/// <summary>
/// A converging–diverging nozzle shaped like a real bell. Converging: a cosine blend from the chamber radius down to the
/// throat. Diverging: a parabolic (Rao) bell, a quadratic Bézier leaving the throat at the initial angle θn and reaching
/// the exit radius at the exit angle θe; θn = θe gives a straight cone. Thrust comes from the shape: the flow is marched
/// quasi-one-dimensionally along the contour, an over-expanded bell separates where the wall pressure drops below
/// <see cref="SeparationRatio"/> × ambient (Summerfield), and the momentum leaving the effective exit is cut by the
/// divergence loss (1 + cos θ) / 2 of the wall angle there.
/// </summary>
public sealed class BellNozzle
{
    public const int Samples = 48;
    public const double SeparationRatio = 0.4;

    public double ChamberRadius;
    public double ThroatRadius;
    public double ExitRadius;
    public double ConvergingLength;
    public double BellLength;

    /// <summary>Wall angle leaving the throat (radians).</summary>
    public double InitialAngle;

    /// <summary>Wall angle at the exit lip (radians).</summary>
    public double ExitAngle;

    /// <summary>Discharge coefficient of the throat.</summary>
    public double ThroatCd = 0.98;

    public double ThroatArea => Math.PI * ThroatRadius * ThroatRadius;

    public double ExitArea => Math.PI * ExitRadius * ExitRadius;

    public double ExpansionRatio => ExitArea / ThroatArea;

    public double Length => ConvergingLength + BellLength;

    /// <summary>The Bézier's middle point; false when the two wall tangents do not meet in front of the throat and behind the exit.</summary>
    public bool Control(out double cx, out double cr)
    {
        // Throat (0, Rt) + s·(cos θn, sin θn) = exit (Lb, Re) − u·(cos θe, sin θe), solved for s and u.
        double ax = Math.Cos(InitialAngle), ar = Math.Sin(InitialAngle), bx = Math.Cos(ExitAngle), br = Math.Sin(ExitAngle);
        double dx = BellLength, dr = ExitRadius - ThroatRadius;
        var det = ax * br - ar * bx;
        cx = cr = 0;

        if (Math.Abs(det) < 1e-9)
        {
            // Parallel tangents: a straight cone if they are one line.
            cx = BellLength * 0.5;
            cr = ThroatRadius + dr * 0.5;
            return Math.Abs(dr * ax - dx * ar) < 1e-6;
        }

        var s = (dx * br - dr * bx) / det;
        var u = (ax * dr - ar * dx) / det;
        cx = s * ax;
        cr = ThroatRadius + s * ar;
        return s > 0 && u > 0;
    }

    public bool IsValid => ThroatRadius > 0 && ExitRadius >= ThroatRadius && ChamberRadius > ThroatRadius && BellLength > 0 && ConvergingLength > 0 && Control(out _, out _);

    /// <summary>The wall contour from the chamber end (x = −converging length) to the exit lip (x = bell length): radius at x, metres.</summary>
    public void Contour(List<(double X, double R)> points, int samples = Samples)
    {
        points.Clear();

        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)samples;
            var x = -ConvergingLength * (1.0 - t);
            var blend = 0.5 * (1.0 + Math.Cos(Math.PI * t));
            points.Add((x, ThroatRadius + (ChamberRadius - ThroatRadius) * blend));
        }

        Control(out var cx, out var cr);

        for (var i = 0; i <= samples; i++)
        {
            var t = i / (double)samples;
            var a = (1 - t) * (1 - t);
            var b = 2 * (1 - t) * t;
            var c = t * t;
            points.Add((b * cx + c * BellLength, a * ThroatRadius + b * cr + c * ExitRadius));
        }
    }

    /// <summary>Flow and thrust for a chamber at stagnation <paramref name="p0"/>, <paramref name="t0"/> of a gas with γ and R_s, into <paramref name="ambient"/> pressure.</summary>
    public NozzlePerformance Perform(double p0, double t0, double gamma, double specificR, double ambient)
    {
        if (!IsValid || p0 <= ambient || t0 <= 0)
            return default;

        var mdot = GasDynamics.MassFlow(ThroatCd, ThroatArea, p0, t0, gamma, specificR, ambient);
        var choked = ambient / p0 <= GasDynamics.CriticalRatio(gamma);

        if (!choked)
        {
            // Subsonic all the way: the throat is the exit.
            var pe = ambient;
            var te = t0 * Math.Pow(pe / p0, (gamma - 1.0) / gamma);
            var ve = Math.Sqrt(Math.Max(0.0, 2.0 * gamma / (gamma - 1.0) * specificR * (t0 - te)));
            var me = ve / Math.Sqrt(gamma * specificR * te);
            return new NozzlePerformance(mdot, mdot * ve, me, pe, ve, 1.0, false, false, 0.0);
        }

        Control(out var cx, out var cr);
        var steps = Samples * 2;
        double exitX = BellLength, exitR = ExitRadius, angle = ExitAngle;
        var separated = false;
        var fraction = 1.0;

        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = 2 * (1 - t) * t * cx + t * t * BellLength;
            var r = (1 - t) * (1 - t) * ThroatRadius + 2 * (1 - t) * t * cr + t * t * ExitRadius;
            var mach = GasDynamics.MachFromArea(r * r / (ThroatRadius * ThroatRadius), gamma, supersonic: true);
            var p = p0 * GasDynamics.PressureRatio(mach, gamma);

            if (p < SeparationRatio * ambient)
            {
                // Flow leaves the wall here; the rest of the bell sees ambient on both faces and does nothing.
                exitX = x;
                exitR = r;
                var dx = 2 * (1 - t) * cx + 2 * t * (BellLength - cx);
                var drr = 2 * (1 - t) * (cr - ThroatRadius) + 2 * t * (ExitRadius - cr);
                angle = Math.Atan2(drr, dx);
                separated = true;
                fraction = t;
                break;
            }
        }

        var area = Math.PI * exitR * exitR;
        var exitMach = GasDynamics.MachFromArea(area / ThroatArea, gamma, supersonic: true);
        var exitT = t0 * GasDynamics.TemperatureRatio(exitMach, gamma);
        var exitP = p0 * GasDynamics.PressureRatio(exitMach, gamma);
        var v = exitMach * Math.Sqrt(gamma * specificR * exitT);
        var lambda = 0.5 * (1.0 + Math.Cos(angle));
        var thrust = lambda * mdot * v + (exitP - ambient) * area;
        return new NozzlePerformance(mdot, Math.Max(0.0, thrust), exitMach, exitP, v, lambda, true, separated, fraction);
    }
}
