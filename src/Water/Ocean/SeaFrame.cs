using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// A tangent frame on the sea sphere near the camera: x east, y up, z south at its origin. The ocean is drawn in it
/// (the clipmap, the depth window, the FFT tiles), bent onto the sphere by y −= (x² + z²) / 2R; the client moves it when
/// the camera strays, so everything in it stays small. A GPU cache only: the waves themselves are defined by
/// <see cref="SeaTiles"/>.
/// </summary>
public readonly struct SeaFrame
{
    public readonly double3 Origin;
    public readonly double3 X;
    public readonly double3 Up;
    public readonly double3 Z;
    public readonly double SeaRadius;

    public SeaFrame(double3 origin, double3 x, double3 up, double3 z, double seaRadius)
    {
        Origin = origin;
        X = x;
        Up = up;
        Z = z;
        SeaRadius = seaRadius;
    }

    /// <summary>The frame on the sea sphere under <paramref name="position"/> (planet frame).</summary>
    public static SeaFrame At(in double3 position, in double3 spinAxis, double seaRadius)
    {
        var up = double3.Normalize(position);
        SeaTiles.EastSouth(up, spinAxis, out var east, out var south);
        return new SeaFrame(up * seaRadius, east, up, south, seaRadius);
    }

    /// <summary>Planet frame to frame coordinates: (x, height above the tangent plane, z).</summary>
    public double3 ToLocal(in double3 planet)
    {
        var d = planet - Origin;
        return new double3(double3.Dot(d, X), double3.Dot(d, Up), double3.Dot(d, Z));
    }

    public double3 ToPlanet(in double3 local) => Origin + X * local.X + Up * local.Y + Z * local.Z;

    /// <summary>How far the sea lies below the tangent plane at (x, z).</summary>
    public double Drop(double x, double z) => (x * x + z * z) / (2.0 * SeaRadius);

    /// <summary>The sea's rest point at (x, z), in the planet frame.</summary>
    public double3 SeaPoint(double x, double z) => Origin + X * x + Z * z - Up * Drop(x, z);

    /// <summary>Frame axes to planet axes as a row-vector rotation.</summary>
    public Matrix4x4 Rotation => new(
        (float)X.X, (float)X.Y, (float)X.Z, 0f,
        (float)Up.X, (float)Up.Y, (float)Up.Z, 0f,
        (float)Z.X, (float)Z.Y, (float)Z.Z, 0f,
        0f, 0f, 0f, 1f);

    public Vector3 ToLocalDirection(in double3 planet) => new((float)double3.Dot(planet, X), (float)double3.Dot(planet, Up), (float)double3.Dot(planet, Z));
}

/// <summary>
/// What the GPU needs to draw the <see cref="SeaTiles"/> sea round the camera, so its low band is the CPU's
/// (<see cref="OceanSurface.Sample"/>): up to four tile slots, each with the map from the sea frame's plane to its tile
/// plane linearised at the camera (exact there; the error grows with the square of the distance, 0.15 m at 2.4 km), and
/// the blend coordinates as planes in x, z: one across the row edge, one across each row's cell edge. The squared weight
/// of a slot is smooth(0.5 ± 0.5 row band) by its row side times smooth(0.5 ± 0.5 its row's cell band) by its cell side,
/// a side of 0 counting 1 (that edge is out of reach).
/// </summary>
public unsafe struct SeaSlots
{
    public int Count;

    /// <summary>The slot the camera is in: the surf carrier is taken from its tile.</summary>
    public int Primary;

    public fixed int Tile[SeaTiles.MaxBlend];

    /// <summary>Tile-plane coordinates of the sea point under the camera.</summary>
    public fixed double U[SeaTiles.MaxBlend];
    public fixed double V[SeaTiles.MaxBlend];

    /// <summary>Per slot, d(u, v) / d(x, z): (u by x, u by z, v by x, v by z).</summary>
    public fixed float ToTile[4 * SeaTiles.MaxBlend];

    /// <summary>Per slot, the inverse: (x by u, x by v, z by u, z by v).</summary>
    public fixed float FromTile[4 * SeaTiles.MaxBlend];

    /// <summary>Per slot: its side of the row edge and of its row's cell edge (−1, +1, or 0 where that edge is out of reach), its row (0 south, 1 north), 0.</summary>
    public fixed float Side[4 * SeaTiles.MaxBlend];

    /// <summary>Blend coordinate across the row edge = X x + Y z + Z, x and z relative to the camera.</summary>
    public Vector4 BandRow;

    /// <summary>The same across the cell edge of the row south of the row edge, and of the row north of it.</summary>
    public Vector4 BandSouth;
    public Vector4 BandNorth;

    private const double GradientStep = 64.0;

    /// <param name="cameraX">The camera in the frame's plane.</param>
    /// <param name="reach">Distance (m) from the camera within which waves are drawn.</param>
    public static void Build(SeaTiles tiles, in SeaFrame frame, double cameraX, double cameraZ, double reach, out SeaSlots slots)
    {
        slots = default;
        var point = frame.SeaPoint(cameraX, cameraZ);
        var dir = double3.Normalize(point);
        tiles.Window(dir, out var window);
        Span<double> here = stackalloc double[3];
        Span<double> east = stackalloc double[3];
        Span<double> west = stackalloc double[3];
        Span<double> south = stackalloc double[3];
        Span<double> north = stackalloc double[3];
        Band(tiles, window, frame, cameraX, cameraZ, here);
        Band(tiles, window, frame, cameraX + GradientStep, cameraZ, east);
        Band(tiles, window, frame, cameraX - GradientStep, cameraZ, west);
        Band(tiles, window, frame, cameraX, cameraZ + GradientStep, south);
        Band(tiles, window, frame, cameraX, cameraZ - GradientStep, north);
        Span<Vector4> bands = stackalloc Vector4[3];
        Span<bool> active = stackalloc bool[3];
        const double scale = 0.5 / GradientStep;

        // A band reaches the drawn sea if its edge lies within the blend (1) plus the reach in band units.
        for (var b = 0; b < 3; b++)
        {
            if (!double.IsFinite(here[b]))
                continue;

            bands[b] = new Vector4((float)((east[b] - west[b]) * scale), (float)((south[b] - north[b]) * scale), (float)here[b], 0f);
            active[b] = Math.Abs(here[b]) < 1.0 + reach * new Vector2(bands[b].X, bands[b].Y).Length();
        }

        slots.BandRow = bands[0];
        slots.BandSouth = bands[1];
        slots.BandNorth = bands[2];
        var ownRow = here[0] >= 0.0 ? 1 : 0;
        var dPdx = frame.X - frame.Up * (cameraX / frame.SeaRadius);
        var dPdz = frame.Z - frame.Up * (cameraZ / frame.SeaRadius);

        for (var slot = 0; slot < SeaTiles.MaxBlend; slot++)
        {
            var row = slot & 1;
            var cell = slot >> 1;
            var ownCell = here[1 + row] >= 0.0 ? 1 : 0;

            if ((!active[0] && row != ownRow) || (!active[1 + row] && cell != ownCell))
                continue;

            var i = slots.Count++;
            var tile = window.Tile[slot];
            slots.Tile[i] = tile;
            slots.Side[4 * i] = active[0] ? 2 * row - 1 : 0;
            slots.Side[4 * i + 1] = active[1 + row] ? 2 * cell - 1 : 0;
            slots.Side[4 * i + 2] = row;

            if (row == ownRow && cell == ownCell)
                slots.Primary = i;

            tiles.Plane(tile, dir, out slots.U[i], out slots.V[i]);
            tiles.Axes(tile, out var tileEast, out _, out var tileSouth);
            var ux = double3.Dot(dPdx, tileEast);
            var uz = double3.Dot(dPdz, tileEast);
            var vx = double3.Dot(dPdx, tileSouth);
            var vz = double3.Dot(dPdz, tileSouth);
            var det = 1.0 / (ux * vz - uz * vx);
            slots.ToTile[4 * i] = (float)ux;
            slots.ToTile[4 * i + 1] = (float)uz;
            slots.ToTile[4 * i + 2] = (float)vx;
            slots.ToTile[4 * i + 3] = (float)vz;
            slots.FromTile[4 * i] = (float)(vz * det);
            slots.FromTile[4 * i + 1] = (float)(-uz * det);
            slots.FromTile[4 * i + 2] = (float)(-vx * det);
            slots.FromTile[4 * i + 3] = (float)(ux * det);
        }
    }

    private static void Band(SeaTiles tiles, in SeaWindow window, in SeaFrame frame, double x, double z, Span<double> bands) =>
        tiles.Band(window, double3.Normalize(frame.SeaPoint(x, z)), out bands[0], out bands[1], out bands[2]);
}
