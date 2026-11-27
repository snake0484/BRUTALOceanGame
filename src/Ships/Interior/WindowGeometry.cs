using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships.Interior;

/// <summary>
/// Windows from the plan, deterministically (every peer derives the same solids). Each window sticks to the hull along its
/// normal (cast back from <see cref="Reach"/> out, like strokes). Its opening is a straight prism of the outline from
/// outside the hull to <see cref="HullWindow.DepthMm"/> in (Room role on a layered hull, so it reaches the interior with
/// its own wall stack; Subtract otherwise). Its pane is a closed mesh: rings of the outline, each point cast onto the hull,
/// so the pane's outer face is the hull's own shape, thickened inward. Panes come after every cut (Panel role on a
/// layered hull).
/// </summary>
public static class WindowGeometry
{
    public const double Reach = 2.0;
    public const double Outside = 0.5;
    public const uint CutIdBase = 0xC0000000;
    public const uint PaneIdBase = 0xC8000000;
    private const double EdgeStep = 0.1;
    private const int MaxOutline = 64;
    private const double RingStep = 0.2;
    private const int MaxRings = 10;
    private const double MinRingEdge = 0.02;

    public static readonly string[] ShapeNames = ["Rectangle", "Rounded", "Circle", "Arch"];

    /// <summary>The window's frame on the hull: across and up in its plane (rolled), and the outward normal.</summary>
    public static (Vector3d Across, Vector3d Up, Vector3d Normal) Frame(HullWindow window)
    {
        var n = window.Normal;
        var reference = Math.Abs(n.Y) < 0.95 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, 1);
        var up = Normalise(Sub(reference, Scale(n, Dot(reference, n))), new Vector3d(0, 1, 0));
        var across = Cross(up, n);
        var a = window.Roll * SolidRotation.DegreesPerStep * Math.PI / 180.0;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        return (Add(Scale(across, c), Scale(up, s)), Add(Scale(up, c), Scale(across, -s)), n);
    }

    /// <summary>The outline in its own plane (metres, centred), counter-clockwise, densified to about 0.1 m per edge.</summary>
    public static List<(double X, double Y)> Outline(WindowShape shape, double width, double height)
    {
        double w = width * 0.5, h = height * 0.5;
        var corners = new List<(double X, double Y)>();

        switch (shape)
        {
            case WindowShape.Circle:
                for (var i = 0; i < 48; i++)
                {
                    var a = i * Math.Tau / 48;
                    corners.Add((w * Math.Cos(a), h * Math.Sin(a)));
                }

                return corners;
            case WindowShape.Rounded:
            {
                var r = Math.Min(w, h) * 0.5;
                Corner(corners, w - r, -h + r, r, -90);
                Corner(corners, w - r, h - r, r, 0);
                Corner(corners, -w + r, h - r, r, 90);
                Corner(corners, -w + r, -h + r, r, 180);
                return Densify(corners);
            }
            case WindowShape.Arch:
            {
                var spring = Math.Max(h - w, -h + height * 0.5);
                corners.Add((-w, -h));
                corners.Add((w, -h));

                for (var i = 0; i <= 16; i++)
                {
                    var a = i * Math.PI / 16;
                    corners.Add((w * Math.Cos(a), spring + (h - spring) * Math.Sin(a)));
                }

                return Densify(corners);
            }
            default:
                corners.AddRange([(-w, -h), (w, -h), (w, h), (-w, h)]);
                return Densify(corners);
        }
    }

    private static void Corner(List<(double X, double Y)> points, double cx, double cy, double r, double from)
    {
        for (var i = 0; i <= 6; i++)
        {
            var a = (from + 90.0 * i / 6) * Math.PI / 180.0;
            points.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
    }

    private static List<(double X, double Y)> Densify(List<(double X, double Y)> corners)
    {
        var result = new List<(double X, double Y)>();

        for (var i = 0; i < corners.Count; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Count];
            var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

            if (length < 1e-4)
                continue;

            var steps = Math.Max(1, (int)Math.Ceiling(length / EdgeStep));

            for (var k = 0; k < steps; k++)
                result.Add((a.X + (b.X - a.X) * k / steps, a.Y + (b.Y - a.Y) * k / steps));
        }

        // Too many points for a cheap mesh: keep every n-th.
        if (result.Count > MaxOutline)
        {
            var keep = new List<(double X, double Y)>();
            var stride = (double)result.Count / MaxOutline;

            for (var i = 0; i < MaxOutline; i++)
                keep.Add(result[(int)(i * stride)]);

            result = keep;
        }

        return result;
    }

    /// <summary>Where the window sits on the hull: its centre cast onto the surface along its normal (where it is when nothing is in reach).</summary>
    public static Vector3d Centre(HullWindow window, HullSurface surface)
    {
        var n = window.Normal;
        return surface.Project(window.Position, n, Reach, out var p, out _) ? p : window.Position;
    }

    public sealed class Expansion
    {
        public readonly List<HullSolid> Cuts = new();
        public readonly List<HullSolid> Panes = new();
        public readonly List<HullMesh> Meshes = new();

        /// <summary>Outline points that found no hull and kept the window's plane.</summary>
        public int Unstuck;
    }

    /// <summary>
    /// Every window's opening and pane. Meshes are indexed after <paramref name="meshBase"/> meshes already in the table;
    /// append <see cref="Expansion.Meshes"/> after them, the cuts with the other cuts and the panes after all of them.
    /// </summary>
    public static Expansion Expand(HullWindowSet? windows, HullSurface surface, bool layered, int meshBase)
    {
        var result = new Expansion();

        if (windows is null)
            return result;

        for (var i = 0; i < windows.Windows.Count; i++)
        {
            var window = windows.Windows[i];
            var (across, up, n) = Frame(window);
            var centre = Centre(window, surface);
            // One outline for the opening and the pane, thinned so the pane's smallest ring keeps edges of 2 cm or more.
            var outline = Thin(Outline(window.Shape, window.WidthMm * 0.001, window.HeightMm * 0.001), MinRingEdge * Rings(window));

            if (outline.Count < 3)
                continue;

            // The opening: straight through, from outside the hull to the depth asked for.
            var outer = new Vector3d[outline.Count];
            var inner = new Vector3d[outline.Count];
            var depth = window.DepthMm * 0.001;

            for (var j = 0; j < outline.Count; j++)
            {
                var p = Add(centre, Add(Scale(across, outline[j].X), Scale(up, outline[j].Y)));
                outer[j] = Add(p, Scale(n, Outside));
                inner[j] = Sub(p, Scale(n, depth));
            }

            if (HallwayGeometry.Prism(outer, inner, Scale(n, -1), out var cutAnchor) is { } cutMesh)
            {
                var cut = new HullSolid
                {
                    Id = CutIdBase | (uint)i,
                    Shape = SolidShape.Mesh,
                    Op = layered ? SolidOp.Add : SolidOp.Subtract,
                    Flags = layered ? SolidRoles.With(SolidFlags.None, SolidRole.Room) : SolidFlags.None,
                    Material = window.Material,
                    Rotation = SolidRotation.Identity,
                    Param = (ushort)(meshBase + result.Meshes.Count),
                };
                (cut.PosX, cut.PosY, cut.PosZ) = cutAnchor;
                (cut.HalfX, cut.HalfY, cut.HalfZ) = cutMesh.HalfUnits();
                result.Meshes.Add(cutMesh);
                result.Cuts.Add(cut);
            }

            // The pane: the outline's rings cast onto the hull, thickened inward.
            if (Pane(window, surface, centre, across, up, n, outline, ref result.Unstuck) is { } pane)
            {
                var (mesh, anchor) = pane;
                var solid = new HullSolid
                {
                    Id = PaneIdBase | (uint)i,
                    Shape = SolidShape.Mesh,
                    Op = SolidOp.Add,
                    Flags = layered ? SolidRoles.With(SolidFlags.None, SolidRole.Panel) : SolidFlags.None,
                    Material = window.Material,
                    Rotation = SolidRotation.Identity,
                    Param = (ushort)(meshBase + result.Meshes.Count),
                };
                (solid.PosX, solid.PosY, solid.PosZ) = anchor;
                (solid.HalfX, solid.HalfY, solid.HalfZ) = mesh.HalfUnits();
                result.Meshes.Add(mesh);
                result.Panes.Add(solid);
            }
        }

        return result;
    }

    /// <summary>The pane's points on the hull: ring 0 is the centre, ring <c>r</c> the outline scaled by r/rings.</summary>
    public static List<Vector3d[]> PaneRings(HullWindow window, HullSurface surface, Vector3d centre, Vector3d across, Vector3d up, Vector3d n,
        List<(double X, double Y)> outline, ref int unstuck)
    {
        var rings = Rings(window);
        var result = new List<Vector3d[]> { new[] { centre } };

        for (var r = 1; r <= rings; r++)
        {
            var s = r / (double)rings;
            var ring = new Vector3d[outline.Count];

            for (var j = 0; j < outline.Count; j++)
            {
                var q = Add(centre, Add(Scale(across, outline[j].X * s), Scale(up, outline[j].Y * s)));

                if (surface.Project(q, n, Reach, out var hit, out _))
                    ring[j] = hit;
                else
                {
                    ring[j] = q;
                    unstuck++;
                }
            }

            result.Add(ring);
        }

        return result;
    }

    /// <summary>How many rings the pane has from its centre out: one per 0.2 m of its larger half size.</summary>
    public static int Rings(HullWindow window) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(window.WidthMm, window.HeightMm) * 0.0005 / RingStep), 1, MaxRings);

    /// <summary>Drops outline points closer than <paramref name="minimum"/> to the last kept one (and to the first, closing the loop).</summary>
    public static List<(double X, double Y)> Thin(List<(double X, double Y)> outline, double minimum)
    {
        var kept = new List<(double X, double Y)>(outline.Count);

        foreach (var p in outline)
        {
            if (kept.Count == 0 || Distance(kept[^1], p) >= minimum)
                kept.Add(p);
        }

        while (kept.Count > 3 && Distance(kept[^1], kept[0]) < minimum)
            kept.RemoveAt(kept.Count - 1);

        return kept;

        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    private static (HullMesh Mesh, (int X, int Y, int Z) Anchor)? Pane(HullWindow window, HullSurface surface, Vector3d centre, Vector3d across, Vector3d up, Vector3d n,
        List<(double X, double Y)> outline, ref int unstuck)
    {
        var rings = PaneRings(window, surface, centre, across, up, n, outline, ref unstuck);
        var thickness = Math.Max((int)window.ThicknessMm, HullWindow.MinThicknessMm) * 0.001;
        var count = outline.Count;
        var anchor = Quantise(centre);
        var origin = new Vector3d(anchor.X * HullSolid.MetresPerPositionUnit, anchor.Y * HullSolid.MetresPerPositionUnit, anchor.Z * HullSolid.MetresPerPositionUnit);
        var builder = new MeshBuilder();

        // Outer then inner: centre, then each ring's points.
        for (var side = 0; side < 2; side++)
        {
            var offset = side == 0 ? new Vector3d(0, 0, 0) : Scale(n, -thickness);

            foreach (var ring in rings)
            {
                foreach (var p in ring)
                {
                    var q = Quantise(Sub(Add(p, offset), origin));

                    if (Math.Abs(q.X) >= HullMesh.MaxCoordinate || Math.Abs(q.Y) >= HullMesh.MaxCoordinate || Math.Abs(q.Z) >= HullMesh.MaxCoordinate)
                        return null;

                    builder.AddVertex(new MeshVertex(q.X, q.Y, q.Z));
                }
            }
        }

        var perSide = 1 + (rings.Count - 1) * count;
        int Index(int side, int ring, int j) => side * perSide + (ring == 0 ? 0 : 1 + (ring - 1) * count + (j % count));

        // Triangles only: the cast points are not coplanar.
        var faces = new List<int[]>();

        for (var side = 0; side < 2; side++)
        {
            for (var j = 0; j < count; j++)
                faces.Add(Wind(side, [Index(side, 0, 0), Index(side, 1, j), Index(side, 1, j + 1)]));

            for (var r = 1; r + 1 < rings.Count; r++)
            {
                for (var j = 0; j < count; j++)
                {
                    faces.Add(Wind(side, [Index(side, r, j), Index(side, r + 1, j), Index(side, r + 1, j + 1)]));
                    faces.Add(Wind(side, [Index(side, r, j), Index(side, r + 1, j + 1), Index(side, r, j + 1)]));
                }
            }
        }

        // The rim joins the outer and inner outlines.
        var last = rings.Count - 1;

        for (var j = 0; j < count; j++)
        {
            faces.Add([Index(0, last, j), Index(1, last, j), Index(1, last, j + 1)]);
            faces.Add([Index(0, last, j), Index(1, last, j + 1), Index(0, last, j + 1)]);
        }

        // Wound outward: if the closed surface's signed volume comes out negative, every face turns over.
        var volume = 0.0;

        foreach (var f in faces)
        {
            var a = builder.Vertices[f[0]];
            var b = builder.Vertices[f[1]];
            var c = builder.Vertices[f[2]];
            volume += (double)a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y) - (double)a.Y * ((double)b.X * c.Z - (double)b.Z * c.X) + (double)a.Z * ((double)b.X * c.Y - (double)b.Y * c.X);
        }

        foreach (var f in faces)
            builder.AddFace(volume < 0 ? [f[0], f[2], f[1]] : f, 0);

        return (builder.Build(), anchor);
    }

    /// <summary>The outer side keeps the triangle as listed, the inner side reversed, so the two faces of the pane face apart.</summary>
    private static int[] Wind(int side, int[] triangle) => side == 0 ? triangle : [triangle[0], triangle[2], triangle[1]];

    private static (int X, int Y, int Z) Quantise(Vector3d v) => (
        (int)Math.Round(v.X * HullSolid.PositionUnitsPerMetre),
        (int)Math.Round(v.Y * HullSolid.PositionUnitsPerMetre),
        (int)Math.Round(v.Z * HullSolid.PositionUnitsPerMetre));
}
