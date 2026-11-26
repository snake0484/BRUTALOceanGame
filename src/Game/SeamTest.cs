using System.Numerics;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Sim;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame;

/// <summary>
/// --seam-test (M2 exit check, docs/pivot/README.md): the pixel probe for cracks in the octree. Static views from above the
/// spawn looking inland at −35° pitch from 100, 50, 20 and 5 km, the octree drawn alone (no ocean surface, no impostor
/// behind it) over a key clear colour nothing else writes. Once a view has settled, one frame's scene is copied back
/// (<see cref="Renderer.RequestCapture"/>: the only readback, and only in this test) and the key-coloured pixels whose
/// ray meets the ball under all of the planet's ground are counted: there the ground must cover the ball, so each one is
/// a crack or a hole. Saves each capture as seam_&lt;km&gt;km.bmp beside the executable. Closes the window when done.
/// </summary>
public sealed class SeamTest
{
    private static readonly LogCategory Log = LogCategory.Make("SeamTest");

    /// <summary>The clear colour while the test runs: the terrain palette's unused air entry.</summary>
    public static readonly float4 Key = new(1f, 0f, 1f, 1f);

    private static readonly double[] Altitudes = [100_000.0, 50_000.0, 20_000.0, 5_000.0];

    private const double Yaw = Math.PI;
    private const double Pitch = -35.0 * Math.PI / 180.0;

    /// <summary>A view is captured once the world has stayed settled this long (the octree's cover fade has ended).</summary>
    private const double SettledSeconds = 1.0;
    private const double SettleTimeout = 180.0;
    private const int BmpHeader = 54;

    private readonly FlyCamera _camera;
    private readonly SimWorld _sim;
    private readonly TerrainWorld _world;
    private readonly Renderer _renderer;
    private readonly double _ball;
    private readonly Vector4[] _planes = new Vector4[5];
    private int _view;
    private double _viewStart = -1.0;
    private double _readySince = -1.0;
    private bool _capturing;

    private readonly bool _steep;
    private readonly double3 _start;
    private readonly double3 _heading;

    /// <param name="steep">Look at the planet's steepest ground (<see cref="SteepGround"/>) instead of inland from the spawn.</param>
    public SeamTest(FlyCamera camera, SimWorld sim, TerrainWorld world, Renderer renderer, bool steep = false)
    {
        _camera = camera;
        _sim = sim;
        _world = world;
        _renderer = renderer;
        _ball = sim.Planet.Radius + sim.Planet.MinHeight;
        _steep = steep;

        if (steep)
            SteepGround.Find(sim.Generator.Surface, out _start, out _heading);
    }

    public bool Done => _view == Altitudes.Length;

    /// <summary>The octree with nothing of its impostor behind or round it.</summary>
    public static FocusBlend OctreeAlone(FocusBlend blend) => new(blend.Overlay, 0f, 0f, blend.OverlayDepthScale);

    /// <summary>Before the frame's terrain update: holds the camera at the current view and asks for its capture once settled.</summary>
    public void Fly(double time, bool ready)
    {
        if (Done)
            return;

        if (_steep)
        {
            var ground = _sim.Generator.Surface.Height(_start.X, _start.Y, _start.Z);
            _camera.Position = _start * (_sim.Planet.Radius + ground + Altitudes[_view]);
            _camera.LookAlong(_heading * Math.Cos(Pitch) + _start * Math.Sin(Pitch));
        }
        else
        {
            _camera.Position = _sim.Bubble.ToPlanet(new Vector3(0f, (float)Altitudes[_view], 0f));
            _camera.LookAlong(_sim.Bubble.ToPlanetDirection(FlyCamera.Direction(Yaw, Pitch)));
        }

        if (_capturing)
            return;

        if (_viewStart < 0.0)
            _viewStart = time;

        _readySince = !ready ? -1.0 : _readySince < 0.0 ? time : _readySince;
        var timedOut = time - _viewStart > SettleTimeout;

        if (!timedOut && (_readySince < 0.0 || time - _readySince < SettledSeconds))
            return;

        if (timedOut)
            Log.Info($"{Altitudes[_view] / 1000.0:F0} km: NOT SETTLED after {SettleTimeout:F0} s, {_world.PendingJobs} jobs pending");

        _renderer.RequestCapture();
        _capturing = true;
    }

    /// <summary>After the frame: counts the capture's cracks against the view it drew.</summary>
    public void Record(in Matrix4x4 viewProj)
    {
        if (Done || !_capturing)
            return;

        var pixels = _renderer.ReadCapture(out var extent);

        if (pixels.IsEmpty || !Matrix4x4.Invert(viewProj, out var inverse))
            return;

        var camera = _camera.Position;
        var distance2 = double3.Dot(camera, camera);
        var ball2 = _ball * _ball;
        int ground = 0, cracks = 0, key = 0;

        for (var y = 0; y < extent.Height; y++)
        {
            var row = pixels.Slice(y * extent.Width * 4, extent.Width * 4);
            var ndcY = (y + 0.5f) * 2f / extent.Height - 1f;

            for (var x = 0; x < extent.Width; x++)
            {
                var p = Vector4.Transform(new Vector4((x + 0.5f) * 2f / extent.Width - 1f, ndcY, 0.5f, 1f), inverse);
                var dir = double3.Normalize(new double3(p.X, p.Y, p.Z) / p.W);
                var along = double3.Dot(camera, dir);
                var open = row[x * 4] == 255 && row[x * 4 + 1] == 0 && row[x * 4 + 2] == 255;
                key += open ? 1 : 0;

                if (along >= 0.0 || distance2 - along * along >= ball2)
                    continue;

                ground++;
                cracks += open ? 1 : 0;
            }
        }

        PlanetFlyTest.ExtractPlanes(viewProj, _planes);
        var km = Altitudes[_view] / 1000.0;
        Log.Info($"{km:F0} km: {cracks} crack px of {ground} ground px ({extent.Width}x{extent.Height}, {key} key px in all); {_world.CountHoles(_planes)} leaf holes, {_world.DesiredLeaves} leaves desired, per LOD [{string.Join(' ', _world.LeavesPerLod.ToArray())}], settled in {_readySince - _viewStart:F1} s");
        Save(Path.Combine(AppContext.BaseDirectory, $"seam_{km:F0}km.bmp"), pixels, extent.Width, extent.Height);

        (_view, _viewStart, _readySince, _capturing) = (_view + 1, -1.0, -1.0, false);
    }

    /// <summary>A top-down 32-bit BMP of the capture as it is (BGRA swapchains come out true; RGBA ones with red and blue swapped).</summary>
    private static void Save(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(BmpHeader + pixels.Length);
        writer.Write(0);
        writer.Write(BmpHeader);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write(pixels);
    }
}
