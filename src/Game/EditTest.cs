using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame;

/// <summary>
/// Scripted check for --edit-test: once terrain has loaded, carve craters where the camera looks and drop spheres
/// onto the terrain, then log where the spheres settled relative to the collision surface.
/// </summary>
public sealed class EditTest(TerrainWorld world, PhysicsWorld physics, TerrainBrush brush, FlyCamera camera)
{
    private static readonly LogCategory Log = LogCategory.Make("EditTest");
    private const float DigRadius = 7f;
    private const float SphereRadius = 1f;
    private readonly List<(BodyHandle Body, float Floor)> _spheres = [];
    private double _start = -1.0;
    private int _stage;

    public void Update(double time)
    {
        if (_stage == 0 && world.LoadedChunks > 0 && world.PendingJobs == 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 1.0)
        {
            for (var i = 0; i < 3; i++)
            {
                var hit = physics.CastRay(camera.Position, brush.RayThrough(-0.3f + 0.3f * i, 0.25f, 16f / 9f), 800f);
                if (!hit.Hit)
                    continue;

                world.ApplyBrush(new BrushOp(hit.Point, DigRadius, BrushMode.Dig, VoxelMaterial.Air));
                world.ApplyBrush(new BrushOp(hit.Point + new Vector3(0f, 0f, 14f), 4f, BrushMode.Place, VoxelMaterial.Rock));
                _spheres.Add((physics.AddDynamicSphere(hit.Point + new Vector3(0f, 25f, 0f), SphereRadius), hit.Point.Y - DigRadius));
                Log.Info($"carved at {hit.Point}");
            }

            _stage = 2;
        }

        if (_stage == 2 && time - _start > 8.0)
        {
            // A sphere dropped into a crater should rest one radius above the dig sphere's bottom.
            foreach (var (sphere, floor) in _spheres)
            {
                var p = physics.GetPosition(sphere);
                var error = p.Y - SphereRadius - floor;
                Log.Info($"sphere at {p} speed {physics.GetLinearVelocity(sphere).Length():F3} m/s, height above crater floor {error:F3} m");
            }

            _stage = 3;
        }
    }
}
