using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Debug;

/// <summary>
/// Debug view of breaker crests drawn over the scene (no depth test): each crest segment coloured by stage, break points
/// as circles with a tick showing the peel direction. Drawn just above sea level (sampling the surface per vertex would
/// cost milliseconds). No allocation per frame.
/// </summary>
public sealed class BreakerOverlay
{
    private const float Thickness = 2f;
    private const float PeelTick = 12f;
    private const float Height = 0.5f;

    public bool Enabled;

    public void Draw(BreakerSet breakers, in Matrix4x4 viewProj)
    {
        if (!Enabled)
            return;

        var viewport = ImGui.GetMainViewport();
        var size = viewport.Size;
        var origin = viewport.Pos;
        var list = ImGui.GetForegroundDrawList(viewport);

        for (var i = 0; i < breakers.Count; i++)
        {
            var b = breakers[i];
            var previous = default(float2);
            var hasPrevious = false;

            for (var v = 0; v < b.Count; v++)
            {
                var vertex = b.Vertices[v];
                var visible = Project(vertex.Position, viewProj, size, origin, out var screen);

                if (visible && hasPrevious)
                    list.AddLine(previous, screen, StageColour(vertex.Stage), Thickness);

                previous = screen;
                hasPrevious = visible;
            }

            if (b.BreakIndex < 0 || !Project(b.BreakPoint, viewProj, size, origin, out var point))
                continue;

            list.AddCircleFilled(point, 6f, new ImColor8(255, 255, 255, 255), 12);

            var along = b.Vertices[Math.Clamp(b.BreakIndex + b.PeelDirection, 0, b.Count - 1)].Position;
            if (Project(along, viewProj, size, origin, out var ahead))
            {
                var direction = ahead - point;
                var length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
                if (length > 1e-3f)
                    list.AddLine(point, point + direction * (PeelTick / length), new ImColor8(255, 255, 255, 255), Thickness);
            }
        }
    }

    private static bool Project(Vector2 xz, in Matrix4x4 viewProj, float2 size, float2 origin, out float2 screen)
    {
        var world = new Vector4(xz.X, Height, xz.Y, 1f);
        var clip = Vector4.Transform(world, viewProj);
        screen = default;

        if (clip.W <= 1e-4f)
            return false;

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        if (MathF.Abs(ndc.X) > 1.2f || MathF.Abs(ndc.Y) > 1.2f)
            return false;

        screen = new float2(origin.X + (ndc.X * 0.5f + 0.5f) * size.X, origin.Y + (ndc.Y * 0.5f + 0.5f) * size.Y);
        return true;
    }

    private static ImColor8 StageColour(BreakerStage stage) => stage switch
    {
        BreakerStage.Shoaling => new ImColor8(80, 160, 255, 220),
        BreakerStage.Curling => new ImColor8(80, 255, 140, 255),
        BreakerStage.Plunging => new ImColor8(255, 220, 60, 255),
        BreakerStage.Bore => new ImColor8(255, 110, 60, 255),
        _ => new ImColor8(150, 150, 150, 160),
    };
}
