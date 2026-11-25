using System.Numerics;
using Brutal.GlfwApi;
using BrutalGame.Engine.Scene;
using BrutalGame.Physics;
using BrutalGame.Sim;
using BrutalGame.Terrain;

namespace BrutalGame;

/// <summary>
/// Mouse terrain editing: LMB digs, Ctrl+LMB places, [ and ] change radius. Rays hit terrain collision. Edits go to the
/// sim as commands; digs into the shore or seabed are refused there (ShoreGuard).
/// </summary>
public sealed class TerrainBrush(SimWorld sim, FlyCamera camera, IslandFrame island)
{
    private const float MaxDistance = 600f;
    private const float Interval = 0.08f;

    public float Radius = 3f;
    public VoxelMaterial PlaceMaterial = VoxelMaterial.Rock;
    public RayHit LastHit;
    private float _cooldown;

    public void Update(GlfwWindow window, float dt, bool uiWantsMouse)
    {
        _cooldown -= dt;

        if (window.GetKey(GlfwKey.LeftBracket) == GlfwKeyAction.Press)
            Radius = MathF.Max(0.5f, Radius - 4f * dt);
        else if (window.GetKey(GlfwKey.RightBracket) == GlfwKeyAction.Press)
            Radius = MathF.Min(16f, Radius + 4f * dt);

        LastHit = sim.Physics.CastRay(island.ToIsland(camera.Position), CursorRay(window), MaxDistance);

        if (uiWantsMouse || window.GetMouseButton(GlfwMouseButton.Left) != GlfwButtonAction.Press || _cooldown > 0f || !LastHit.Hit)
            return;

        var place = window.GetKey(GlfwKey.LeftControl) == GlfwKeyAction.Press;
        sim.QueueEdit(new BrushOp(LastHit.Point, Radius, place ? BrushMode.Place : BrushMode.Dig, PlaceMaterial));
        _cooldown = Interval;
    }

    public Vector3 CursorRay(GlfwWindow window)
    {
        var size = window.Size;
        var cursor = window.CursorPos;
        var ndcX = (float)(2.0 * cursor.X / Math.Max(size.X, 1) - 1.0);
        var ndcY = (float)(2.0 * cursor.Y / Math.Max(size.Y, 1) - 1.0);
        return RayThrough(ndcX, ndcY, size.X / (float)Math.Max(size.Y, 1));
    }

    /// <summary>Island ray direction through normalised device coords (y down).</summary>
    public Vector3 RayThrough(float ndcX, float ndcY, float aspect)
    {
        var forward = IslandFrame.ToIslandDirection(camera.Forward);
        var right = IslandFrame.ToIslandDirection(camera.Right);
        var up = Vector3.Cross(right, forward);
        var tan = MathF.Tan(camera.FovY * 0.5f);
        return Vector3.Normalize(forward + right * (ndcX * tan * aspect) - up * (ndcY * tan));
    }
}
