using System.Numerics;
using BrutalGame.Engine.Scene;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame;

/// <summary>
/// Scripted check for --roof-test: once terrain has loaded, floats a roof of rock over the camera and the water ahead of
/// it, so rain can be seen to stop under it: no drops, crowns or rings below a roof (RainCover, docs/rain.md).
/// </summary>
public sealed class RoofTest(TerrainWorld world, FlyCamera camera)
{
    private const float Underside = 6f;
    private const float Radius = 3f;
    private const float Spacing = 4f;
    private const int Rocks = 5;
    private const int Rows = 3;

    private bool _placed;

    public void Update()
    {
        if (_placed || world.LoadedChunks == 0 || world.PendingJobs > 0)
            return;

        var forward = Vector3.Normalize(camera.Forward with { Y = 0f });
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var start = camera.Position with { Y = Underside + Radius };

        for (var row = 0; row < Rows; row++)
        {
            for (var i = 0; i < Rocks; i++)
                world.ApplyBrush(new BrushOp(start + forward * (row * Spacing) + right * ((i - (Rocks - 1) * 0.5f) * Spacing), Radius, BrushMode.Place, VoxelMaterial.Rock));
        }

        _placed = true;
    }
}
