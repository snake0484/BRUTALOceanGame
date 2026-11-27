using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim;

/// <summary>
/// Each sea tile's wind from the planet's weather (<see cref="ISeaWind"/>): at an epoch's first tick, the weather over a
/// <see cref="Grid"/> × <see cref="Grid"/> lattice across the tile, its mean speed along its mean direction in the tile's
/// plane. Waves at a point were raised over the water around it, so the mean also hides a storm's calm eye. A pure function
/// of the field, the tile and the epoch: camera-independent, so a headless host reproduces it.
/// </summary>
public sealed class SeaWindSchedule(SphereWeatherField field, SeaTiles tiles) : ISeaWind
{
    public const int Grid = 5;

    /// <summary>Lattice spacing (m): the grid spans the tile.</summary>
    public const double Spacing = SeaTiles.TileSize / Grid;

    private const int MaxSystems = 256;

    public readonly SphereWeatherField Field = field;
    public readonly SeaTiles Tiles = tiles;

    /// <summary>Debug offset (ticks) of weather time from sim time.</summary>
    public long TickOffset;

    public SeaWindKey At(int tile, long epoch) => Key(Mean(tile, epoch * SeaWind.EpochTicks + TickOffset));

    public static SeaWindKey Key(Vector2 wind) => SeaWindKey.Of(wind.Length(), MathF.Atan2(wind.Y, wind.X));

    /// <summary>The tile's mean wind (m/s, blowing toward) at a weather tick, in its plane (x east, y south).</summary>
    [SkipLocalsInit]
    public Vector2 Mean(int tile, long tick)
    {
        Tiles.Axes(tile, out var east, out var up, out var south);
        var radius = Tiles.SeaRadius;
        var half = 0.5 * (Grid - 1) * Spacing;
        var extra = Math.Sqrt(2.0) * half / radius;
        Span<SphereSystem> systems = stackalloc SphereSystem[MaxSystems];
        var count = Field.Gather(up, extra, tick * Field.TickSeconds, systems);
        var gathered = systems[..count];
        var sum = Vector2.Zero;
        var speed = 0f;

        for (var j = 0; j < Grid; j++)
        {
            for (var i = 0; i < Grid; i++)
            {
                var unit = double3.Normalize(up * radius + east * (i * Spacing - half) + south * (j * Spacing - half));
                var sample = Field.Evaluate(unit, gathered);
                var wind = Field.ToPlanet(unit, sample.Wind);
                var plane = new Vector2((float)double3.Dot(wind, east), (float)double3.Dot(wind, south));
                sum += plane;
                speed += plane.Length();
            }
        }

        var length = sum.Length();
        return length > 1e-3f ? sum * (speed / (Grid * Grid) / length) : Vector2.Zero;
    }
}
