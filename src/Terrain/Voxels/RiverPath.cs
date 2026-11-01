using System.Numerics;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain;

/// <summary>
/// A river the generator carves (docs/voxel-water.md "Flows"): its centreline sampled every metre downstream, from inside
/// a lake's shore to past the coast, with the channel's bed and the water's surface as generated at each station. The bed
/// never rises downstream, so the channel holds no pits. Its first stations are the sill: the lake's level stands over it
/// by the head that passes <see cref="Discharge"/> over a broad-crested weir <see cref="Width"/> wide.
/// </summary>
public sealed class RiverPath
{
    /// <summary>Broad-crested weir coefficient (SI): Q = C · width · head^1.5.</summary>
    public const float WeirCoefficient = 1.7f;

    /// <summary>The channel's banks rise this much per metre out from its bed's edge.</summary>
    public const float BankSlope = 1f;

    /// <summary>Generated water stands this far past the bed's edge (m), up the banks' foot.</summary>
    public const float WaterMargin = 1.5f;

    /// <summary>
    /// The share of Manning's uniform flow the pipe model carries down a voxel channel at the same depth and slope: its
    /// stairs and, off the grid's axes, its zigzag faces cost conveyance. Measured on straight 8 m channels
    /// (WaterFlowTests.ChannelCarriesItsUniformFlow): 0.96 along an axis and 0.81 at 30–60° on a 5 % slope, 0.65 at 45° on 10 %.
    /// </summary>
    public const float Conveyance = 0.8f;

    private const float CellSize = 16f;
    private const int Stride = 4;

    public readonly Vector2[] Points;

    /// <summary>Unit downstream direction at each station.</summary>
    public readonly Vector2[] Directions;
    public readonly float[] Beds;
    public readonly float[] Surfaces;
    public readonly float Width;
    public readonly float Discharge;

    /// <summary>How far from the centreline the carve can reach (m): the bed, then banks up the deepest cut.</summary>
    public readonly float Reach;

    /// <summary>World y of the lowest bed and the highest generated surface.</summary>
    public readonly float Lowest;
    public readonly float Highest;

    private readonly Vector2 _min;
    private readonly int _cellsX;
    private readonly int _cellsZ;

    /// <summary>Per grid cell, every <see cref="Stride"/>th station within <see cref="Reach"/> of it.</summary>
    private readonly int[][] _cells;

    public RiverPath(Vector2[] points, float[] beds, float[] surfaces, float width, float discharge, float reach)
    {
        Points = points;
        Beds = beds;
        Surfaces = surfaces;
        Width = width;
        Discharge = discharge;
        Reach = reach;
        Directions = new Vector2[points.Length];
        Lowest = float.MaxValue;
        Highest = float.MinValue;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);

        for (var k = 0; k < points.Length; k++)
        {
            var ahead = points[Math.Min(k + 1, points.Length - 1)] - points[Math.Max(k - 1, 0)];
            Directions[k] = ahead == Vector2.Zero ? Vector2.UnitX : Vector2.Normalize(ahead);
            min = Vector2.Min(min, points[k]);
            max = Vector2.Max(max, points[k]);
            Lowest = MathF.Min(Lowest, beds[k]);
            Highest = MathF.Max(Highest, surfaces[k]);
        }

        var margin = reach + Stride;
        _min = min - new Vector2(margin);
        _cellsX = (int)MathF.Ceiling((max.X - min.X + 2f * margin) / CellSize) + 1;
        _cellsZ = (int)MathF.Ceiling((max.Y - min.Y + 2f * margin) / CellSize) + 1;
        var lists = new List<int>[_cellsX * _cellsZ];

        for (var k = 0; ; k = Math.Min(k + Stride, points.Length - 1))
        {
            var lo = (points[k] - new Vector2(margin) - _min) / CellSize;
            var hi = (points[k] + new Vector2(margin) - _min) / CellSize;

            for (var cz = Math.Max(0, (int)lo.Y); cz <= Math.Min(_cellsZ - 1, (int)hi.Y); cz++)
                for (var cx = Math.Max(0, (int)lo.X); cx <= Math.Min(_cellsX - 1, (int)hi.X); cx++)
                    (lists[cx + cz * _cellsX] ??= new List<int>(8)).Add(k);

            if (k == points.Length - 1)
                break;
        }

        _cells = new int[lists.Length][];

        for (var c = 0; c < lists.Length; c++)
            _cells[c] = lists[c]?.ToArray() ?? [];
    }

    public int Count => Points.Length;

    public float HalfWidth => Width * 0.5f;

    /// <summary>The bed at the river's head, under the lake.</summary>
    public float Sill => Beds[0];

    public float Depth(int station) => Surfaces[station] - Beds[station];

    /// <summary>The generated water's mean speed at a station (m/s), downstream.</summary>
    public float Speed(int station) => Discharge / (Width * MathF.Max(Depth(station), 0.1f));

    /// <summary>The bed's fall per metre about a station, over 8 stations either side.</summary>
    public float Slope(int station)
    {
        var a = Math.Max(0, station - 8);
        var b = Math.Min(Count - 1, station + 8);
        return b > a ? MathF.Max(0f, (Beds[a] - Beds[b]) / (b - a)) : 0f;
    }

    /// <summary>
    /// The froth (0..1, <see cref="WaterBlock.Froth"/>) the river carries at a station once running steadily: each one-metre
    /// voxel stair aerates the water it drops to <see cref="WaterPipes.AerateSpeed"/>'s share of its fall speed, and the
    /// bubbles burst at <see cref="WaterPipes.FrothDecay"/> over its travel to the next stair, 1 / slope metres on: the mean
    /// over that run. About 0.6 on a 40 % pitch, 0.2 at the 6 % control slope.
    /// </summary>
    public float Froth(int station)
    {
        var slope = Slope(station);

        if (slope <= 0f)
            return 0f;

        var aerated = MathF.Min(MathF.Sqrt(2f * WaterPipes.Gravity) / WaterPipes.AerateSpeed, 1f);
        var decay = WaterPipes.FrothDecay / (slope * MathF.Max(Speed(station), 0.1f));
        return aerated * (1f - MathF.Exp(-decay)) / decay;
    }

    /// <summary>The bounds (world x, z) meet the river's reach.</summary>
    public bool Touches(float x0, float z0, float x1, float z1) =>
        x1 >= _min.X && z1 >= _min.Y && x0 <= _min.X + _cellsX * CellSize && z0 <= _min.Y + _cellsZ * CellSize;

    /// <summary>The station nearest (x, z) and its distance, if within <paramref name="within"/> (m). Allocation-free.</summary>
    public bool Nearest(float x, float z, float within, out int station, out float distance)
    {
        station = -1;
        distance = float.MaxValue;
        var cx = (int)MathF.Floor((x - _min.X) / CellSize);
        var cz = (int)MathF.Floor((z - _min.Y) / CellSize);

        if ((uint)cx >= (uint)_cellsX || (uint)cz >= (uint)_cellsZ)
            return false;

        var at = new Vector2(x, z);
        var best = float.MaxValue;

        foreach (var k in _cells[cx + cz * _cellsX])
        {
            var d = Vector2.DistanceSquared(at, Points[k]);

            if (d < best)
            {
                best = d;
                station = k;
            }
        }

        if (station < 0)
            return false;

        var coarse = station;

        for (var k = Math.Max(0, coarse - Stride); k <= Math.Min(Points.Length - 1, coarse + Stride); k++)
        {
            var d = Vector2.DistanceSquared(at, Points[k]);

            if (d < best)
            {
                best = d;
                station = k;
            }
        }

        distance = MathF.Sqrt(best);
        return distance <= within;
    }

    /// <summary>The carved ground at <paramref name="distance"/> from a station: the bed across its width, then the banks.</summary>
    public float Carve(int station, float distance) => Beds[station] + MathF.Max(0f, distance - HalfWidth) * BankSlope;

    /// <summary>
    /// Depth (m) of uniform flow of <paramref name="discharge"/> down a wide channel <paramref name="width"/> wide on
    /// <paramref name="slope"/>, by Manning's formula at the pipe model's bed roughness and <see cref="Conveyance"/>, so the
    /// generated river is the depth the sim runs it at.
    /// </summary>
    public static float NormalDepth(float discharge, float width, float slope) =>
        MathF.Pow(discharge * WaterPipes.BedRoughness / (Conveyance * width * MathF.Sqrt(slope)), 0.6f);

    /// <summary>Uniform flow (m³/s) at <paramref name="depth"/> in the same channel: the inverse of <see cref="NormalDepth"/>.</summary>
    public static float UniformFlow(float depth, float width, float slope) =>
        Conveyance * width * MathF.Pow(MathF.Max(depth, 0f), 5f / 3f) * MathF.Sqrt(slope) / WaterPipes.BedRoughness;

    /// <summary>
    /// The slope (m/m) down which <paramref name="discharge"/> runs as deep as the weir head that passes it
    /// (<see cref="WeirHead"/>). Flatter, the channel needs more head than the sill gives: it, not the sill, holds the lake
    /// back, and the water backs up behind it.
    /// </summary>
    public static float ControlSlope(float discharge, float width)
    {
        var head = WeirHead(discharge, width);
        var root = discharge * WaterPipes.BedRoughness / (Conveyance * width * MathF.Pow(head, 5f / 3f));
        return root * root;
    }

    /// <summary>The weir head (m) over a sill that passes <paramref name="discharge"/>.</summary>
    public static float WeirHead(float discharge, float width) => MathF.Pow(discharge / (WeirCoefficient * width), 2f / 3f);

    /// <summary>Flow (m³/s) over a sill under <paramref name="head"/>: the inverse of <see cref="WeirHead"/>.</summary>
    public static float WeirFlow(float head, float width) => WeirCoefficient * width * MathF.Pow(MathF.Max(head, 0f), 1.5f);
}
