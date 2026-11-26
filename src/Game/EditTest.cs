using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Scene;
using BrutalGame.Physics;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame;

/// <summary>
/// Scripted check for --edit-test: once terrain has loaded, carve craters where the camera looks and drop spheres
/// onto the terrain, then log where the spheres settled relative to the collision surface (physics bubble coordinates).
/// </summary>
public sealed class EditTest(SimWorld sim, TerrainWorld world, TerrainBrush brush, FlyCamera camera)
{
    private static readonly LogCategory Log = LogCategory.Make("EditTest");
    private const float DigRadius = 7f;
    private const float SphereRadius = 1f;
    private readonly List<(BodyHandle Body, float Floor)> _spheres = [];
    private double _start = -1.0;
    private int _stage;

    public void Update(double time)
    {
        if (_stage == 0 && world.Settled && world.CollisionBodies > 0)
        {
            _start = time;
            _stage = 1;
        }

        if (_stage == 1 && time - _start > 1.0)
        {
            for (var i = 0; i < 3; i++)
            {
                var hit = sim.Physics.CastRay(sim.Bubble.ToLocal(camera.Position), brush.RayThrough(-0.3f + 0.3f * i, 0.25f, 16f / 9f), 800f);

                if (!hit.Hit)
                    continue;

                var dig = brush.At(hit.Point, DigRadius, BrushMode.Dig, VoxelMaterial.Air);

                if (sim.Generator.IsFullyProtected(dig))
                {
                    Log.Info($"refused at {hit.Point}: shore or seabed");
                    continue;
                }

                sim.QueueEdit(dig);
                sim.QueueEdit(brush.At(hit.Point + new Vector3(0f, 0f, 14f), 4f, BrushMode.Place, VoxelMaterial.Rock));
                _spheres.Add((sim.Physics.AddDynamicSphere(hit.Point + new Vector3(0f, 25f, 0f), SphereRadius), hit.Point.Y - DigRadius));
                Log.Info($"carved at {hit.Point}");
            }

            _stage = 2;
        }

        if (_stage == 2 && time - _start > 8.0)
        {
            // A sphere dropped into a crater rests one radius above the dig sphere's bottom, unless the protected band clamped it.
            foreach (var (sphere, floor) in _spheres)
            {
                var p = sim.Physics.GetPosition(sphere);
                var error = p.Y - SphereRadius - floor;
                Log.Info($"sphere at {p} speed {sim.Physics.GetLinearVelocity(sphere).Length():F3} m/s, height above crater floor {error:F3} m");
            }

            _stage = 3;
        }
    }
}
