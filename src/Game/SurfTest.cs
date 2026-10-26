using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Water.Ocean;

namespace BrutalGame;

/// <summary>
/// Scripted scene for --surf-test: builds a sandbar 2–3 m under the surface off the west beach (overlapping place
/// brushes), so swell breaks over open water instead of at the waterline, and frames the camera low along it. The bar
/// runs at an angle to the incoming crests, like a point break: each crest starts breaking at one end and the break
/// peels along it. For looking at breaker faces and lips (docs/breaking-waves.md).
/// </summary>
public sealed class SurfTest(TerrainWorld world, FlyCamera camera, OceanSwell swell, bool keepCamera)
{
    private static readonly LogCategory Log = LogCategory.Make("SurfTest");
    private const float Spacing = 6f;
    private const float Radius = 14f;

    /// <summary>
    /// The bar is sized to the sea: the surf carrier breaks where the depth is its shoaled height / γ (BreakDepth), so the
    /// bar's crest sits at CrestShare of that and its seaward foot where the beach is FlankShare of it, and the set's waves
    /// (0.3–1.7 × the carrier) break on the flank, plunging (ξ well above 1), rather than on the 1:20 shelf
    /// (a fixed 1.6 m crest took the 20 m/s sea's 2–3 m waves 40 m seaward of it) or not at all.
    /// </summary>
    private const float Shoaling = 1.1f;
    private const float CrestShare = 0.55f;
    private const float FlankShare = 1.4f;
    private const float MinCrestDepth = 1.2f;

    /// <summary>--surf-cam: where the eye sits relative to the curl (m ahead of the face, along the peel, above the sea).</summary>
    private const float CamAhead = 16f;
    private const float CamAlong = 10f;
    private const float CamHeight = 2.2f;
    private const float CamFollow = 1.5f;

    /// <summary>Height (m) traded per metre a curl lies from the one followed, so the camera stays with one peel.</summary>
    private const float CamStick = 0.05f;
    private const float CamClearance = 1.5f;

    /// <summary>Metres the camera keeps above the drawn sea under it (OceanSurface: pitched walls and lifted crests included).</summary>
    private const float SeaClearance = 0.8f;
    private Vector2 Min = new(-340f, -90f);
    private Vector2 Max = new(-262f, 90f);
    private float _crestDepth = 1.6f;

    /// <summary>Bar width (m) and how far its seaward edge moves shoreward per metre along it (tan of its angle to the crests).</summary>
    private const float Width = 36f;
    private const float Skew = 0.35f;

    private int _stage;
    private double _start;
    private bool _placed;
    private Vector2 _target = new(-300f, 0f);
    private Vector2 _logged = new(float.NaN);
    private Vector3 _look;

    /// <summary>Follow the curl with the camera (--surf-cam), from in front of the face or (--surf-cam side) from the shoulder.</summary>
    public bool FollowCurl;
    public bool SideView;

    /// <summary>--surf-cam back: from the broken side, looking along the crest at the break point, so the lip in view is a mature one.</summary>
    public bool BackView;

    /// <summary>
    /// --surf-cam side: metres along the crest toward the unbroken shoulder and ahead of the face, like a photographer in
    /// the channel looking along the face into the curl; it sits this much above the crest so a passing crest never swallows it.
    /// </summary>
    private const float SideAlong = 8f;
    private const float SideAhead = 7f;
    private const float SideAbove = 0.8f;

    /// <summary>
    /// --surf-cam: puts the camera low in front of the face of the biggest crest that is breaking, a little along the
    /// direction its break point peels, looking back at the curl; eases toward it so a new crest doesn't make it jump.
    /// </summary>
    public void Follow(BreakerSet breakers, OceanSurface surface, float dt)
    {
        if (!FollowCurl || _stage < 2)
            return;

        // The tallest curl, discounted by its distance from the one followed (the bar at first), so it stays with a peel.
        Breaker? best = null;
        var bestScore = float.MinValue;
        for (var i = 0; i < breakers.Count; i++)
        {
            var b = breakers[i];
            if (b.BreakIndex < 0)
                continue;

            var score = b.Vertices[b.BreakIndex].Height - CamStick * Vector2.Distance(b.BreakPoint, _target);
            if (score <= bestScore)
                continue;

            best = b;
            bestScore = score;
        }

        if (best is null)
            return;

        var v = best.BreakIndex;
        var along = best.Vertices[Math.Min(v + 1, best.Count - 1)].Position - best.Vertices[Math.Max(v - 1, 0)].Position;
        if (along.LengthSquared() < 1e-6f)
            return;

        along = Vector2.Normalize(along);
        var ahead = new Vector2(-along.Y, along.X);
        if (Vector2.Dot(ahead, best.Vertices[v].Travel) < 0f)
            ahead = -ahead;

        var peel = best.PeelDirection != 0 ? best.PeelDirection : 1;
        var target = best.BreakPoint;
        if (float.IsNaN(_logged.X) || Vector2.Distance(target, _logged) > 10f)
        {
            _logged = target;
            Log.Info($"surf-cam: break point {target.X:F0}, {target.Y:F0}, crest height {best.Vertices[v].Height:F2} m, depth {best.Vertices[v].Depth:F1} m, type {best.Type}");
        }

        _target = target;
        var eye = SideView ? target + along * (SideAlong * peel) + ahead * SideAhead
            : BackView ? target - along * (SideAlong * peel) + ahead * SideAhead
            : target + ahead * CamAhead + along * (CamAlong * peel);
        var height = SideView || BackView ? MathF.Max(CamHeight, best.Vertices[v].Height + SideAbove) : CamHeight;
        height = MathF.Max(height, surface.SampleHeight(eye.X, eye.Y) + SeaClearance);
        var goal = new Vector3(eye.X, MathF.Max(height, world.Bathymetry.SampleTop(eye.X, eye.Y) + CamClearance), eye.Y);
        var ease = _placed ? 1f - MathF.Exp(-CamFollow * dt) : 1f;
        _placed = true;

        camera.Position = Vector3.Lerp(camera.Position, goal, ease);
        _look = Vector3.Lerp(_look, new Vector3(target.X, 0.5f, target.Y), ease);
        var look = _look - camera.Position;
        camera.Yaw = MathF.Atan2(look.Z, look.X);
        camera.Pitch = MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length());
    }

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage != 1 || time - _start < 1.0)
            return;

        var breakDepth = Shoaling * swell.CarrierHeight / OceanSwell.WaveBreakingGamma;
        _crestDepth = MathF.Max(CrestShare * breakDepth, MinCrestDepth);
        var flankDepth = FlankShare * breakDepth;
        var foot = Min.X;
        for (var x = -150f; x > -700f; x -= 2f)
        {
            foot = x;
            if (-world.Bathymetry.SampleTop(x, 0f) >= flankDepth)
                break;
        }

        var shift = foot - Min.X;
        Min.X = foot;
        Max.X += shift;

        var count = 0;
        for (var z = Min.Y; z <= Max.Y; z += Spacing)
        {
            var x0 = Min.X + Skew * (z - Min.Y);
            for (var x = x0; x <= x0 + Width; x += Spacing)
            {
                world.ApplyBrush(new BrushOp(new Vector3(x, -_crestDepth - Radius, z), Radius, BrushMode.Place, VoxelMaterial.Sand));
                count++;
            }
        }

        if (!keepCamera)
        {
            camera.Position = new Vector3(-318f + shift, 3f, -40f);
            camera.Yaw = 55f * MathF.PI / 180f;
            camera.Pitch = -4f * MathF.PI / 180f;
        }

        Log.Info($"sandbar: {count} brushes, {Width} m wide from x {Min.X} at z {Min.Y} to x {Min.X + Skew * (Max.Y - Min.Y)} at z {Max.Y}, crest {_crestDepth:F2} m deep for a {swell.CarrierHeight:F2} m carrier breaking in {breakDepth:F2} m, foot in {flankDepth:F2} m");
        _stage = 2;
    }
}
