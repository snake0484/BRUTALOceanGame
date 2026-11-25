namespace BrutalGame.Terrain;

/// <summary>
/// Shores and seabeds are unminable (D30). A dig is refused when its footprint comes within <see cref="Band"/> of any
/// column whose generated ground lies below sea level + <see cref="Margin"/>, so a dug pit below sea level is always
/// cut off from the sea by undug land and holds no sea water on the CPU or the GPU. Placing is always allowed.
/// </summary>
public static class ShoreGuard
{
    /// <summary>Generated ground below sea level plus this (m) is seabed or beach.</summary>
    public const float Margin = 2f;

    /// <summary>
    /// Protected land inshore of that line (m). Twice the GPU's 8 m shore reach (ShoreDepthMap), so a pit never joins the
    /// sea through the below-sea-level cells the GPU still counts as sea.
    /// </summary>
    public const float Band = 16f;

    /// <summary>Ground sample spacing (m), the bathymetry cell size.</summary>
    private const float Step = 2f;

    public static bool Allows(in BrushOp op, in TerrainParams p)
    {
        if (op.Mode != BrushMode.Dig)
            return true;

        var reach = op.Radius + Band;
        var reach2 = reach * reach;
        var floor = p.SeaLevel + Margin;
        var steps = (int)MathF.Ceiling(reach / Step);

        for (var j = -steps; j <= steps; j++)
        {
            for (var i = -steps; i <= steps; i++)
            {
                var dx = i * Step;
                var dz = j * Step;

                if (dx * dx + dz * dz > reach2)
                    continue;

                if (TerrainGenerator.SurfaceHeight(op.Centre.X + dx, op.Centre.Z + dz, p) < floor)
                    return false;
            }
        }

        return true;
    }
}
