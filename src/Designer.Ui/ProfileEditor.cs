using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The 2D editor for a profile: drag points (0.05 m snap), double-click an edge to add one, right-click a point to remove
/// it. A hallway shows the floor, a 0.5 m grid and one 0.8 × 1.9 m lane per person who fits abreast (red when not even one
/// does). A wall is an open line drawn as the right half of a hallway: the room's side on the right, the room to the left;
/// its points stay off the far side of the wall and never go down. A drag is a live copy, handed back once when the mouse
/// is released (one undo step).
/// </summary>
public sealed class ProfileEditor
{
    private const double Units = HullSolid.PositionUnitsPerMetre;
    private const double Snap = 0.05;
    private const float PointPixels = 8f;

    private static readonly ImColor8 Grid = new(255, 255, 255, 18);
    private static readonly ImColor8 FloorLine = new(255, 255, 255, 90);
    private static readonly ImColor8 OutlineColour = new(140, 255, 255, 255);
    private static readonly ImColor8 Point = new(235, 235, 235, 255);
    private static readonly ImColor8 PointHot = new(255, 230, 120, 255);
    private static readonly ImColor8 PersonFill = new(110, 220, 120, 40);
    private static readonly ImColor8 PersonLine = new(110, 220, 120, 200);
    private static readonly ImColor8 TooSmall = new(255, 90, 80, 220);
    private static readonly ImColor8 WallSide = new(255, 255, 255, 140);

    private List<ProfilePoint>? _draft;
    private int _dragPoint = -1;
    private int _owner = -1;

    /// <summary>The outline being shown: the live copy while dragging.</summary>
    public IReadOnlyList<ProfilePoint> Shown(int owner, IReadOnlyList<ProfilePoint> profile) => _owner == owner && _draft is not null ? _draft : profile;

    /// <summary>Draws the editor for profile <paramref name="owner"/>. Returns a finished edit's outline, or null.</summary>
    public List<ProfilePoint>? Draw(int owner, IReadOnlyList<ProfilePoint> stored, Clearance clear, ProfileKind kind = ProfileKind.Hallway)
    {
        var wall = kind == ProfileKind.Wall;
        var least = wall ? HullProfile.MinWallPoints : HullProfile.MinPoints;
        var edges = wall ? stored.Count - 1 : stored.Count;
        double Sx(ProfilePoint p) => (wall ? -p.X : p.X) / Units;

        if (_owner != owner)
            (_owner, _draft, _dragPoint) = (owner, null, -1);

        var profile = Shown(owner, stored);
        var size = new float2(Px(320f), Px(260f));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##profile"u8, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var list = ImGui.GetWindowDrawList();
        list.AddRectFilled(origin, new float2(origin.X + size.X, origin.Y + size.Y), new ImColor8(0, 0, 0, 90));

        // Fit the outline (and a person) with a margin, one scale on both axes.
        double x0 = wall ? -1.0 : -0.5, x1 = wall ? 0.0 : 0.5, y1 = HallwayGeometry.MinClearHeight;

        foreach (var p in profile)
        {
            (x0, x1) = (Math.Min(x0, Sx(p)), Math.Max(x1, Sx(p)));
            y1 = Math.Max(y1, p.Y / Units);
        }

        const double margin = 0.35;
        x0 -= margin;
        x1 += margin;
        var y0 = -margin;
        y1 += margin;
        var scale = Math.Min(size.X / (x1 - x0), size.Y / (y1 - y0));
        var cx = (x0 + x1) * 0.5;
        var cy = (y0 + y1) * 0.5;
        float2 ToScreen(double x, double y) => new((float)(origin.X + size.X * 0.5 + (x - cx) * scale), (float)(origin.Y + size.Y * 0.5 - (y - cy) * scale));
        (double X, double Y) ToWorld(float2 s) => (cx + (s.X - origin.X - size.X * 0.5) / scale, cy - (s.Y - origin.Y - size.Y * 0.5) / scale);

        for (var g = Math.Ceiling(x0 / 0.5) * 0.5; g <= x1; g += 0.5)
            list.AddLine(ToScreen(g, y0), ToScreen(g, y1), Grid);

        for (var g = Math.Ceiling(y0 / 0.5) * 0.5; g <= y1; g += 0.5)
            list.AddLine(ToScreen(x0, g), ToScreen(x1, g), Grid);

        list.AddLine(ToScreen(x0, 0), ToScreen(x1, 0), FloorLine, 1.5f);

        if (wall)
        {
            // The room's side (its box) on the right; the wall stands in from it.
            list.AddLine(ToScreen(0, 0), ToScreen(0, y1), WallSide, 2f);
            list.AddText(ToScreen(0.05, y1 - 0.05), WallSide, "side");
        }

        // People abreast: one 0.8 × 1.9 lane each, centred on the floor; a red one when not even one fits.
        var (lo, hi) = HallwayGeometry.FloorSpan(profile);
        var mid = (lo + hi) * 0.5;
        var people = wall ? 0 : Math.Max(clear.People, 1);

        for (var i = 0; i < people; i++)
        {
            var left = mid - people * HallwayGeometry.LaneWidth * 0.5 + i * HallwayGeometry.LaneWidth;
            var a = ToScreen(left + 0.04, HallwayGeometry.MinClearHeight);
            var b = ToScreen(left + HallwayGeometry.LaneWidth - 0.04, 0.0);

            if (clear.Fits)
            {
                list.AddRectFilled(a, b, PersonFill);
                list.AddRect(a, b, PersonLine, 0f, default, 1f);
            }
            else
            {
                list.AddRect(a, b, TooSmall, 0f, default, 1.5f);
            }
        }

        var mouse = ImGui.GetIO().MousePos;
        var hot = -1;
        var best = Px(PointPixels);

        for (var j = 0; j < profile.Count; j++)
        {
            var s = ToScreen(Sx(profile[j]), profile[j].Y / Units);
            var distance = MathF.Sqrt((s.X - mouse.X) * (s.X - mouse.X) + (s.Y - mouse.Y) * (s.Y - mouse.Y));

            if (hovered && distance < best)
            {
                best = distance;
                hot = j;
            }
        }

        if (_dragPoint >= 0)
            hot = _dragPoint;

        for (var j = 0; j < edges; j++)
        {
            var a = profile[j];
            var b = profile[(j + 1) % profile.Count];
            list.AddLine(ToScreen(Sx(a), a.Y / Units), ToScreen(Sx(b), b.Y / Units), OutlineColour, 2f);
        }

        for (var j = 0; j < profile.Count; j++)
        {
            var s = ToScreen(Sx(profile[j]), profile[j].Y / Units);
            var r = Px(j == hot ? 5f : 3.5f);
            list.AddRectFilled(new float2(s.X - r, s.Y - r), new float2(s.X + r, s.Y + r), j == hot ? PointHot : Point);
        }

        if (_dragPoint < 0 && hovered && hot >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
        {
            _dragPoint = hot;
            _draft = new List<ProfilePoint>(stored);
        }

        if (_dragPoint >= 0 && _draft is not null)
        {
            var (x, y) = ToWorld(mouse);
            _draft[_dragPoint] = wall ? WallPoint(_draft, _dragPoint, x, y) : new ProfilePoint(Snapped(x), Snapped(y));

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                return null;

            var done = _draft;
            (_draft, _dragPoint) = (null, -1);
            return done;
        }

        if (hovered && hot >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right, false))
        {
            if (profile.Count <= least)
                return null;

            var fewer = new List<ProfilePoint>(profile);
            fewer.RemoveAt(hot);
            return fewer;
        }

        if (hovered && hot < 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && profile.Count < HullProfile.MaxPoints)
        {
            // The nearest edge gets a new point under the mouse.
            var edge = -1;
            var near = Px(PointPixels);

            for (var j = 0; j < edges; j++)
            {
                var a = ToScreen(Sx(profile[j]), profile[j].Y / Units);
                var b = ToScreen(Sx(profile[(j + 1) % profile.Count]), profile[(j + 1) % profile.Count].Y / Units);
                var d = GizmoMath.SegmentDistance(new System.Numerics.Vector2(mouse.X, mouse.Y), new System.Numerics.Vector2(a.X, a.Y), new System.Numerics.Vector2(b.X, b.Y));

                if (d < near)
                {
                    near = d;
                    edge = j;
                }
            }

            if (edge >= 0)
            {
                var (x, y) = ToWorld(mouse);
                var more = new List<ProfilePoint>(profile);
                more.Insert(edge + 1, new ProfilePoint(Snapped(x), Snapped(y)));

                if (wall)
                    more[edge + 1] = WallPoint(more, edge + 1, x, y);

                return more;
            }
        }

        return null;
    }

    /// <summary>A wall point under the mouse: off the far side of the wall, the first on the floor, between its neighbours' heights.</summary>
    private static ProfilePoint WallPoint(List<ProfilePoint> points, int i, double x, double y)
    {
        var lo = i == 0 ? 0 : points[i - 1].Y;
        var hi = i == 0 ? 0 : i + 1 < points.Count ? points[i + 1].Y : HullProfile.MaxUnits;
        return new ProfilePoint(Math.Max(0, Snapped(-x)), Math.Clamp(Snapped(y), lo, hi));
    }

    private static int Snapped(double metres) => (int)Math.Round(Math.Round(metres / Snap) * Snap * Units);

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
