using System.Numerics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Water.Ocean;

namespace BrutalGame;

/// <summary>
/// Feeds layer B. Rebuilds the coastal maps (on jobs, from the analytic seabed plus edits) when the map re-centres, the
/// terrain is edited, or the wind direction turns; never because of LOD streaming. Each finished field is crossfaded in
/// over <see cref="FadeSeconds"/>, so a rebuild never makes the waves jump.
/// </summary>
public sealed class CoastalSystem : IDisposable
{
    private const float DirectionThreshold = 5f * MathF.PI / 180f;

    public const double FadeSeconds = 1.5;

    private readonly CoastalBuilder _builder = new();
    private readonly TerrainCoastalDepth _depth = new();
    private Vector2 _builtOrigin = new(float.NaN);
    private int _builtOps = -1;
    private double _fadeStart = double.NegativeInfinity;

    public float LastBuildMs => _builder.LastBuildMs;

    public int Builds { get; private set; }

    /// <summary>Main thread, once per frame (between ticks).</summary>
    public void Update(TerrainWorld terrain, OceanSwell swell, double time, bool enabled)
    {
        if (_builder.Poll())
        {
            Builds++;
            _fadeStart = time;
        }

        var fade = (float)Math.Clamp((time - _fadeStart) / FadeSeconds, 0.0, 1.0);
        swell.Coastal = enabled ? _builder.Current : null;
        swell.CoastalPrevious = fade < 1f ? _builder.Previous : null;
        swell.CoastalFade = fade;

        // The retired field becomes the next build's target, so nothing starts until the fade is over.
        if (_builder.Busy || fade < 1f)
            return;

        var origin = terrain.Bathymetry.Origin;
        var direction = swell.DominantDirection;
        var current = _builder.Current;
        var turned = current is not null && MathF.Abs(MathF.IEEERemainder(direction - current.Direction, MathF.Tau)) > DirectionThreshold;

        if (current is not null && origin == _builtOrigin && terrain.Ops.Count == _builtOps && !turned)
            return;

        _depth.Snapshot(terrain.Params, terrain.Ops);
        _builtOrigin = origin;
        _builtOps = terrain.Ops.Count;
        _builder.TryStart(_depth, origin, direction);
    }

    public void Dispose() => _builder.Dispose();
}
