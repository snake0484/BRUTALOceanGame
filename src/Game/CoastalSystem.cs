using System.Numerics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Water.Ocean;

namespace BrutalGame;

/// <summary>
/// Feeds layer B. Rebuilds the coastal maps (on jobs, from the analytic seabed plus edits) when the map re-centres, the
/// terrain is edited, or the wind direction turns; never because of LOD streaming. Each finished field is crossfaded in
/// over <see cref="FadeSeconds"/>, so a rebuild never makes the waves jump.
/// Two levels: a far field (4 km of 16 m cells, re-centred in <see cref="FarStep"/> steps) carries the islands' shadows,
/// and every near field (the bathymetry grid's 1 km of 4 m cells) is solved inside it, so near and far water agree. The
/// far field builds first; the near field built inside it brings it in, the pair crossfading as one.
/// </summary>
public sealed class CoastalSystem : IDisposable
{
    private const float DirectionThreshold = 5f * MathF.PI / 180f;

    /// <summary>The far field's centre snaps to this grid (m), so it re-centres once the camera has gone 256–768 m.</summary>
    private const float FarStep = 512f;

    public const double FadeSeconds = 1.5;

    private readonly CoastalBuilder _builder = new();
    private readonly CoastalBuilder _far = new(CoastalField.FarCellSize);
    private readonly TerrainCoastalDepth _depth = new();
    private Vector2 _builtOrigin = new(float.NaN);
    private Vector2 _farOrigin = new(float.NaN);
    private int _builtOps = -1;
    private int _farOps = -1;
    private double _fadeStart = double.NegativeInfinity;

    public float LastBuildMs => _builder.LastBuildMs;

    public float LastFarBuildMs => _far.LastBuildMs;

    public int Builds { get; private set; }

    public int FarBuilds { get; private set; }

    /// <summary>Main thread, once per frame (between ticks).</summary>
    public void Update(TerrainWorld terrain, OceanSwell swell, double time, bool enabled)
    {
        if (_far.Poll())
            FarBuilds++;

        if (_builder.Poll())
        {
            Builds++;
            _fadeStart = time;
        }

        var fade = (float)Math.Clamp((time - _fadeStart) / FadeSeconds, 0.0, 1.0);
        swell.Coastal = enabled ? _builder.Current : null;
        swell.CoastalPrevious = fade < 1f ? _builder.Previous : null;
        swell.CoastalFade = fade;

        // The retired field becomes the next build's target, so nothing starts until the fade is over; the far field's
        // retired one is the previous near field's, so it waits as well.
        if (_builder.Busy || _far.Busy || fade < 1f)
            return;

        var origin = terrain.Bathymetry.Origin;
        var direction = swell.DominantDirection;
        var far = _far.Current;
        var farOrigin = FarOrigin(origin);

        if (far is null || farOrigin != _farOrigin || terrain.Ops.Count != _farOps || Turned(far, direction))
        {
            _depth.Snapshot(terrain.Params, terrain.Ops);
            _farOrigin = farOrigin;
            _farOps = terrain.Ops.Count;
            _far.TryStart(_depth, farOrigin, direction);
            return;
        }

        var current = _builder.Current;

        if (current is not null && origin == _builtOrigin && terrain.Ops.Count == _builtOps && current.Far == far && !Turned(current, direction))
            return;

        _depth.Snapshot(terrain.Params, terrain.Ops);
        _builtOrigin = origin;
        _builtOps = terrain.Ops.Count;
        _builder.TryStart(_depth, origin, direction, far);
    }

    private static bool Turned(CoastalField field, float direction) =>
        MathF.Abs(MathF.IEEERemainder(direction - field.Direction, MathF.Tau)) > DirectionThreshold;

    /// <summary>The far field's corner for a near field at <paramref name="origin"/>: centred on the near field's centre, snapped to <see cref="FarStep"/>.</summary>
    private static Vector2 FarOrigin(Vector2 origin)
    {
        var centre = origin + new Vector2(0.5f * CoastalField.Resolution * CoastalField.CellSize);
        var snapped = new Vector2(MathF.Round(centre.X / FarStep), MathF.Round(centre.Y / FarStep)) * FarStep;
        return snapped - new Vector2(0.5f * CoastalField.Resolution * CoastalField.FarCellSize);
    }

    public void Dispose()
    {
        _builder.Dispose();
        _far.Dispose();
    }
}
