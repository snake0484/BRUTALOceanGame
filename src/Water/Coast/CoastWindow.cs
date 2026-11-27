using Brutal.Numerics;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Coast;

/// <summary>
/// A coast region's frame: a square of <see cref="Cells"/> × <see cref="Cells"/> cells of <see cref="CellSize"/> in a
/// sea tile's tangent plane, local x along the tile's east and z along its south from the window's corner. Windows sit
/// on a lattice of pitch <see cref="Pitch"/> cells, so neighbours share <see cref="Blend"/> cells, over which their
/// weights (<see cref="Weight"/>) cross-fade and sum to 1. Local coordinates are float metres; the only double step is
/// the corner's place in the tile plane.
/// </summary>
public readonly struct CoastWindow
{
    public const int Cells = 256;
    public const float CellSize = 4f;
    public const int Pitch = 224;
    public const int Blend = Cells - Pitch;
    public const float Size = Cells * CellSize;
    public const double PitchMetres = Pitch * (double)CellSize;

    public readonly int Tile;
    public readonly double OriginU;
    public readonly double OriginV;
    public readonly double SeaRadius;
    private readonly double3 _east;
    private readonly double3 _up;
    private readonly double3 _south;

    public CoastWindow(SeaTiles tiles, int tile, int x, int z)
    {
        Tile = tile;
        OriginU = x * PitchMetres;
        OriginV = z * PitchMetres;
        SeaRadius = tiles.SeaRadius;
        tiles.Axes(tile, out _east, out _up, out _south);
    }

    public CoastWindow(SeaTiles tiles, in CoastRegionKey key) : this(tiles, key.Tile, key.X, key.Z)
    {
    }

    /// <summary>The lattice index of the window whose own (unshared) span holds tile-plane coordinate <paramref name="u"/>.</summary>
    public static int Index(double u) => (int)Math.Floor((u - 0.5 * Blend * CellSize) / PitchMetres);

    /// <summary>The unit direction of the sea sphere's point at local (x, z).</summary>
    public double3 Direction(double x, double z)
    {
        var u = OriginU + x;
        var v = OriginV + z;
        var w = Math.Sqrt(Math.Max(SeaRadius * SeaRadius - u * u - v * v, 0.0));
        return double3.Normalize(_east * u + _south * v + _up * w);
    }

    /// <summary>Local (x, z) of the sea sphere's point at unit direction <paramref name="dir"/>.</summary>
    public void ToLocal(in double3 dir, out double x, out double z)
    {
        x = SeaRadius * double3.Dot(dir, _east) - OriginU;
        z = SeaRadius * double3.Dot(dir, _south) - OriginV;
    }

    /// <summary>
    /// Fills rows [z0, z1) of an n × n grid of depths at cell centres, cell (0, 0)'s corner at local
    /// (<paramref name="originX"/>, <paramref name="originZ"/>). Called from jobs.
    /// </summary>
    public void FillDepth(ICoastDepth source, Span<float> depth, double originX, double originZ, double cell, int n, int z0, int z1)
    {
        for (var z = z0; z < z1; z++)
        {
            var pz = originZ + (z + 0.5) * cell;

            for (var x = 0; x < n; x++)
                depth[x + z * n] = source.GeneratedDepth(Direction(originX + (x + 0.5) * cell, pz));
        }
    }

    /// <summary>
    /// A window's weight at cell coordinates (cx, cz) (0..<see cref="Cells"/>): 1 inside, falling smoothly to 0 across
    /// the <see cref="Blend"/> cells it shares with each neighbour, whose weight is the complement.
    /// </summary>
    public static float Weight(float cx, float cz) => Ramp(cx) * Ramp(cz);

    private static float Ramp(float c)
    {
        var t = Math.Clamp(MathF.Min(c, Cells - c) / Blend, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
