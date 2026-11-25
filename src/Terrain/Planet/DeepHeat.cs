namespace BrutalGame.Terrain;

/// <summary>
/// Deep-rock heat (D29, planets.md "Deep heat"): the hook only. Rock temperature by depth below the unedited surface;
/// component wear and the 1 Hz ambient check come with entities.
/// </summary>
public static class DeepHeat
{
    /// <summary>Geothermal gradient (K/m).</summary>
    public const float Gradient = 0.025f;

    /// <summary>Rock temperature (K) at <paramref name="depth"/> m below the generated surface.</summary>
    public static float RockTemperature(in PlanetParams p, float depth, float surfaceK = 288f)
    {
        var t = surfaceK + Gradient * MathF.Max(depth, 0f);

        if (depth <= p.HeatOnsetDepth)
            return t;

        var x = (depth - p.HeatOnsetDepth) / (p.DigDepth - p.HeatOnsetDepth);
        return t + p.HeatExcessK * x * x;
    }

    /// <summary>Depth (m) of planet-frame point p below the generated surface; negative above it.</summary>
    public static float Depth(PlanetSurface surface, double x, double y, double z)
    {
        var r = Math.Sqrt(x * x + y * y + z * z);
        return (float)(surface.Radius + surface.Height(x / r, y / r, z / r) - r);
    }
}
