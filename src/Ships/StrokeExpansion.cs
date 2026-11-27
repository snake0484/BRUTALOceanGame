using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships;

/// <summary>Strokes expanded: the plan's solids plus generated flair mesh solids after them, and the mesh table plus their meshes.</summary>
public sealed class ExpandedHull
{
    public HullSolid[] Solids = [];
    public List<HullMesh> Meshes = new();

    /// <summary>How many of <see cref="Solids"/> are the plan's own; the generated ones follow.</summary>
    public int PlanSolids;

    /// <summary>Generated solids (stamps and sweep pieces).</summary>
    public int Generated;

    /// <summary>Stroke points that found no hull within reach and stayed where they were.</summary>
    public int Unstuck;

    /// <summary>The stuck path of each stroke (ship metres), for drawing the line; parallel to the set's strokes.</summary>
    public List<StrokePath> Paths = new();

    /// <summary>Paint strokes' bands, as projector boxes (drawn on the hull and flair, nothing baked).</summary>
    public List<HullProjector> Projectors = new();
}

/// <summary>A stroke's points after sticking to the hull, densified along the surface.</summary>
public sealed class StrokePath
{
    public readonly List<Vector3d> Points = new();
    public readonly List<Vector3d> Normals = new();

    /// <summary>The design's size at each point (1 = as modelled), blended between the stroke's points.</summary>
    public readonly List<double> Scales = new();

    /// <summary>Index in <see cref="Points"/> of each control point.</summary>
    public readonly List<int> Controls = new();
}

/// <summary>
/// Expands brush strokes into flair mesh solids, deterministically from the plan (every peer gets the same ones). They are
/// drawn as they are and counted for mass, never baked into the hull grid. Each control point is stuck to the hull (cast back along its normal from <see cref="Reach"/> out), the
/// segments between are resampled every <see cref="SampleMetres"/> and stuck too, so the line hugs curved hulls. A stamp
/// brush places a copy of its mesh every spacing, +Y on the normal and +Z along the line; a sweep brush builds a tube of
/// its plan profile along the path, the line through the brush's line point (split into pieces within the mesh limits),
/// capped at both ends. Sizes multiply: the brush's, the stroke's, and the points' (blended along the line).
/// </summary>
public static class StrokeExpansion
{
    public const double Reach = 2.0;
    public const double SampleMetres = 0.25;
    public const int MaxGenerated = 1024;
    public const int MaxPathPoints = 4096;
    public const uint GeneratedIdBase = 0xF0000000;

    /// <summary>Paint reaches this far out from the hull (over raised flair) and this far in (metres).</summary>
    public const double PaintAbove = 1.0;
    public const double PaintBelow = 0.1;
    public const int MaxProjectors = 4096;
    private const double MergeCos = 0.9995;
    private const double MinStep = 1e-3;

    /// <summary>
    /// <paramref name="profiles"/>: the plan's profiles, which sweep brushes index. <paramref name="strokePaint"/>: a stroke's
    /// own paint slot by id (-1: it carries the hull's), laid over its flair as fill boxes.
    /// </summary>
    public static ExpandedHull Expand(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, HullBrushSet? set, IReadOnlyList<HullProfile>? profiles,
        Func<uint, int>? strokePaint = null)
    {
        var result = new ExpandedHull { PlanSolids = solids.Length };
        result.Meshes.AddRange(meshes);

        if (set is null || set.Strokes.Count == 0)
        {
            result.Solids = solids.ToArray();
            return result;
        }

        var surface = new HullSurface(solids, meshes);
        var generated = new List<HullSolid>();

        for (var s = 0; s < set.Strokes.Count; s++)
        {
            var stroke = set.Strokes[s];
            var path = Path(surface, stroke, ref result.Unstuck);
            result.Paths.Add(path);

            if (stroke.Brush >= set.Brushes.Count || path.Points.Count < 2)
                continue;

            var brush = set.Brushes[stroke.Brush];
            var template = new HullSolid
            {
                Shape = SolidShape.Mesh,
                Op = SolidOp.Add,
                Material = stroke.Material,
                Rotation = SolidRotation.Identity,
            };
            var scale = brush.Scale / (double)HullBrush.UnitScale * (stroke.Scale / (double)HullBrush.UnitScale);
            var offset = stroke.Offset * HullSolid.MetresPerPositionUnit;

            var slot = strokePaint?.Invoke(stroke.Id) ?? -1;

            if (slot >= 0 && Extent(brush, meshes, profiles) is { } extent)
                Fill(path, extent.HalfWidth * scale, extent.Above * scale, offset, (byte)slot, result.Projectors);

            if (brush.Kind == BrushKind.Stamp && brush.Mesh < meshes.Count && brush.Spacing >= HullBrush.MinSpacing)
                Stamps(path, meshes[brush.Mesh], brush.Spacing * HullSolid.MetresPerPositionUnit, scale, offset, s, template, result, generated);
            else if (brush.Kind == BrushKind.Paint)
                Paint(path, brush, scale, offset, result.Projectors);
            else if (brush.Kind == BrushKind.Sweep && profiles is not null && brush.Outline < profiles.Count
                && profiles[brush.Outline] is { Kind: ProfileKind.Brush, Points.Count: >= HullBrush.MinProfilePoints and <= HullBrush.MaxProfilePoints } profile)
                Sweep(path, profile.Points, brush.Anchor, scale, offset, s, template, result, generated);
        }

        result.Generated = generated.Count;
        var all = new HullSolid[solids.Length + generated.Count];
        solids.CopyTo(all);
        generated.CopyTo(all, solids.Length);
        result.Solids = all;
        return result;
    }

    /// <summary>A model brush's flair at size 1 (metres): how far it reaches either side of the line and out of the hull; null for paint.</summary>
    private static (double HalfWidth, double Above)? Extent(HullBrush brush, IReadOnlyList<HullMesh> meshes, IReadOnlyList<HullProfile>? profiles)
    {
        const double k = HullSolid.MetresPerPositionUnit;

        if (brush.Kind == BrushKind.Stamp && brush.Mesh < meshes.Count && meshes[brush.Mesh].Vertices.Length > 0)
        {
            var vertices = meshes[brush.Mesh].Vertices;
            return (vertices.Max(v => Math.Max(Math.Abs(v.X), Math.Abs(v.Z))) * k, Math.Max(vertices.Max(v => v.Y), 0) * k);
        }

        if (brush.Kind == BrushKind.Sweep && profiles is not null && brush.Outline < profiles.Count && profiles[brush.Outline].Points.Count > 0)
        {
            var points = profiles[brush.Outline].Points;
            return (points.Max(p => Math.Abs(p.X - brush.Anchor.X)) * k, Math.Max(points.Max(p => p.Y - brush.Anchor.Y), 0) * k);
        }

        return null;
    }

    /// <summary>The stroke stuck to the hull and resampled along it.</summary>
    public static StrokePath Path(HullSurface surface, HullStroke stroke, ref int unstuck)
    {
        var path = new StrokePath();
        Vector3d prevP = default, prevN = default;
        var prevS = 1.0;

        for (var i = 0; i < stroke.Points.Count; i++)
        {
            var point = stroke.Points[i];
            var size = point.Scale / (double)HullBrush.UnitScale;

            if (!surface.Project(point.Position, point.Normal, Reach, out var p, out var n))
                unstuck++;

            if (i > 0)
            {
                var length = Sub(p, prevP).Length();
                var steps = Math.Min((int)Math.Ceiling(length / SampleMetres), MaxPathPoints);

                for (var k = 1; k < steps && path.Points.Count < MaxPathPoints; k++)
                {
                    var f = k / (double)steps;
                    var q = Lerp(prevP, p, f);
                    var qn = Normalise(Lerp(prevN, n, f), n);
                    surface.Project(q, qn, Reach, out q, out qn);
                    Append(path, q, qn, prevS + (size - prevS) * f);
                }
            }

            Append(path, p, n, size);
            path.Controls.Add(path.Points.Count - 1);
            prevP = p;
            prevN = n;
            prevS = size;
        }

        return path;
    }

    private static void Append(StrokePath path, Vector3d p, Vector3d n, double scale)
    {
        if (path.Points.Count > 0 && Sub(p, path.Points[^1]).Length() < MinStep)
            return;

        path.Points.Add(p);
        path.Normals.Add(n);
        path.Scales.Add(scale);
    }

    /// <summary>Frame at a path point: normal up, tangent along the line made square to it, binormal = normal × tangent.</summary>
    private static (Vector3d B, Vector3d N, Vector3d T) Frame(Vector3d normal, Vector3d along)
    {
        var n = Normalise(normal, new Vector3d(0, 1, 0));
        var t = Normalise(Sub(along, Scale(n, Dot(along, n))), Math.Abs(n.X) < 0.9 ? new Vector3d(1, 0, 0) : new Vector3d(0, 0, 1));
        var b = Cross(n, t);
        return (b, n, t);
    }

    private static Vector3d Along(StrokePath path, int i)
    {
        var a = path.Points[Math.Max(i - 1, 0)];
        var b = path.Points[Math.Min(i + 1, path.Points.Count - 1)];
        return Sub(b, a);
    }

    private static void Stamps(StrokePath path, HullMesh mesh, double spacing, double scale, double offset, int strokeIndex, HullSolid template, ExpandedHull result, List<HullSolid> output)
    {
        var next = 0.0;
        var walked = 0.0;
        var copy = 0;

        for (var i = 0; i + 1 < path.Points.Count; i++)
        {
            var a = path.Points[i];
            var b = path.Points[i + 1];
            var length = Sub(b, a).Length();

            while (next <= walked + length + 1e-9)
            {
                if (output.Count >= MaxGenerated)
                    return;

                var f = length > 0 ? (next - walked) / length : 0.0;
                var p = Lerp(a, b, f);
                var n = Normalise(Lerp(path.Normals[i], path.Normals[i + 1], f), path.Normals[i]);
                var (bx, ny, tz) = Frame(n, Sub(b, a));
                var size = scale * (path.Scales[i] + (path.Scales[i + 1] - path.Scales[i]) * f);
                var placed = Transform(mesh, Add(p, Scale(ny, offset)), bx, ny, tz, size, out var anchor);

                if (placed is not null)
                    Emit(template, anchor, placed, strokeIndex, copy++, result, output);

                next += spacing;
            }

            walked += length;
        }
    }

    /// <summary>A paint stroke's pattern band: <see cref="Band"/> of the brush's width, reaching <see cref="PaintAbove"/> out and <see cref="PaintBelow"/> in.</summary>
    private static void Paint(StrokePath path, HullBrush brush, double scale, double offset, List<HullProjector> output)
    {
        const double k = HullSolid.MetresPerPositionUnit;
        var template = new HullProjector
        {
            Kind = ProjectorKind.Paint,
            Colour = brush.Colour,
            Spacing = (float)Math.Max(brush.Spacing * k * scale, 0.01),
            LineWidth = (float)(brush.LineWidth * k * scale),
            Pattern = brush.Pattern,
        };
        Band(path, brush.Width * k * scale * 0.5, PaintAbove, PaintBelow, false, offset, template, output);
    }

    /// <summary>
    /// A stroke's own paint over its flair: <see cref="Band"/> as wide and tall as the flair (sizes following the points),
    /// a little over, painting whatever way the surface faces.
    /// </summary>
    private static void Fill(StrokePath path, double halfWidth, double above, double offset, byte slot, List<HullProjector> output)
    {
        const double margin = 0.02;
        Band(path, halfWidth + margin, above + margin, PaintBelow, true, offset, new HullProjector { Kind = ProjectorKind.Fill, Paint = slot, Colour = 0xFFFFFFFF }, output);
    }

    /// <summary>
    /// Boxes along the path, copies of <paramref name="template"/>: one per run of the path that stays straight and level
    /// (pieces are merged while direction, normal and size hold), <paramref name="halfWidth"/> either side (times the point
    /// sizes), from <paramref name="below"/> in to <paramref name="above"/> out (times the point sizes when
    /// <paramref name="sizedDepth"/>), lifted by <paramref name="offset"/>. Each carries the distance along the stroke at
    /// its centre, so a pattern runs on through the pieces.
    /// </summary>
    private static void Band(StrokePath path, double halfWidth, double above, double below, bool sizedDepth, double offset, HullProjector template, List<HullProjector> output)
    {
        var walked = 0.0;
        var start = 0;

        while (start + 1 < path.Points.Count && output.Count < MaxProjectors)
        {
            var a = path.Points[start];
            var along = Sub(path.Points[start + 1], a);
            var end = start + 1;

            if (along.Length() < 1e-6)
            {
                start++;
                continue;
            }

            along = Normalise(along, new Vector3d(1, 0, 0));

            // Grow the piece while the path runs on straight with the same normal and size.
            while (end + 1 < path.Points.Count)
            {
                var next = Normalise(Sub(path.Points[end + 1], path.Points[end]), along);

                if (Dot(next, along) < MergeCos || Dot(path.Normals[end + 1], path.Normals[start]) < MergeCos
                    || Math.Abs(path.Scales[end + 1] - path.Scales[start]) > 1e-3)
                    break;

                end++;
            }

            var b = path.Points[end];
            var length = Sub(b, a).Length();
            var n = Normalise(Add(path.Normals[start], path.Normals[end]), path.Normals[start]);
            var (across, normal, tangent) = Frame(n, Sub(b, a));
            var size = (path.Scales[start] + path.Scales[end]) * 0.5;
            var half = halfWidth * size;
            var top = sizedDepth ? above * size : above;
            var mid = Scale(Add(a, b), 0.5);
            var box = template;
            box.Centre = Add(mid, Scale(normal, offset + (top - below) * 0.5));
            (box.U, box.V, box.N) = (tangent, across, normal);
            box.HalfU = length * 0.5 + Math.Min(half, length) * 0.5;
            box.HalfV = half;
            box.HalfN = (top + below) * 0.5;
            box.Along = walked + length * 0.5;
            output.Add(box);

            walked += length;
            start = end;
        }
    }

    /// <summary>The stamp's vertices in ship space around a quantised anchor; null when it would exceed the mesh limits.</summary>
    private static HullMesh? Transform(HullMesh mesh, Vector3d origin, Vector3d bx, Vector3d ny, Vector3d tz, double scale, out (int X, int Y, int Z) anchor)
    {
        anchor = Quantise(origin);
        var exact = new Vector3d(anchor.X * HullSolid.MetresPerPositionUnit, anchor.Y * HullSolid.MetresPerPositionUnit, anchor.Z * HullSolid.MetresPerPositionUnit);
        var shift = Sub(origin, exact);
        var vertices = new MeshVertex[mesh.Vertices.Length];
        var k = scale * HullSolid.MetresPerPositionUnit;

        for (var v = 0; v < vertices.Length; v++)
        {
            var m = mesh.Vertices[v];
            var w = Add(shift, Add(Scale(bx, m.X * k), Add(Scale(ny, m.Y * k), Scale(tz, m.Z * k))));
            var q = Quantise(w);

            if (Math.Abs(q.X) >= HullMesh.MaxCoordinate || Math.Abs(q.Y) >= HullMesh.MaxCoordinate || Math.Abs(q.Z) >= HullMesh.MaxCoordinate)
                return null;

            vertices[v] = new MeshVertex(q.X, q.Y, q.Z);
        }

        return new HullMesh(vertices, mesh.FaceSizes, mesh.Corners, mesh.Panels, mesh.Creases, mesh.SubdivLevels);
    }

    private static void Sweep(StrokePath path, List<ProfilePoint> profile, ProfilePoint lineAt, double scale, double offset, int strokeIndex, HullSolid template, ExpandedHull result, List<HullSolid> output)
    {
        // Orient the profile counter-clockwise in (across, out), so faces wind outward.
        var count = profile.Count;
        var area = 0L;

        for (var j = 0; j < count; j++)
        {
            var a = profile[j];
            var b = profile[(j + 1) % count];
            area += (long)a.X * b.Y - (long)b.X * a.Y;
        }

        if (area == 0)
            return;

        var corners = new (double X, double Y)[count];

        for (var j = 0; j < count; j++)
        {
            var p = profile[area > 0 ? j : count - 1 - j];
            corners[j] = ((p.X - lineAt.X) * HullSolid.MetresPerPositionUnit * scale, (p.Y - lineAt.Y) * HullSolid.MetresPerPositionUnit * scale);
        }

        // Pieces share their boundary ring; each stays within the vertex, face and coordinate limits.
        var ringsPerPiece = Math.Max(2, Math.Min(256, (HullMesh.MaxFaces - 2) / count));
        var piece = 0;

        for (var first = 0; first + 1 < path.Points.Count; first += ringsPerPiece - 1)
        {
            if (output.Count >= MaxGenerated)
                return;

            var last = Math.Min(first + ringsPerPiece - 1, path.Points.Count - 1);
            var anchor = Quantise(path.Points[first]);
            var exact = new Vector3d(anchor.X * HullSolid.MetresPerPositionUnit, anchor.Y * HullSolid.MetresPerPositionUnit, anchor.Z * HullSolid.MetresPerPositionUnit);
            var builder = new MeshBuilder();
            var ok = true;

            for (var i = first; i <= last && ok; i++)
            {
                var (bx, ny, _) = Frame(path.Normals[i], Along(path, i));
                var centre = Sub(path.Points[i], exact);
                var size = path.Scales[i];

                foreach (var (x, y) in corners)
                {
                    var q = Quantise(Add(centre, Add(Scale(bx, x * size), Scale(ny, y * size + offset))));

                    if (Math.Abs(q.X) >= HullMesh.MaxCoordinate || Math.Abs(q.Y) >= HullMesh.MaxCoordinate || Math.Abs(q.Z) >= HullMesh.MaxCoordinate)
                    {
                        ok = false;
                        break;
                    }

                    builder.AddVertex(new MeshVertex(q.X, q.Y, q.Z));
                }
            }

            if (!ok)
                return;

            var rings = last - first + 1;

            // MeshBuilder keeps the corner arrays, so each face gets its own.
            for (var r = 0; r + 1 < rings; r++)
            {
                for (var j = 0; j < count; j++)
                {
                    var jn = (j + 1) % count;
                    builder.AddFace([r * count + j, r * count + jn, (r + 1) * count + jn, (r + 1) * count + j], 0);
                }
            }

            var start = new int[count];
            var end = new int[count];

            for (var j = 0; j < count; j++)
            {
                start[j] = count - 1 - j;
                end[j] = (rings - 1) * count + j;
            }

            builder.AddFace(start, 0);
            builder.AddFace(end, 0);
            Emit(template, anchor, builder.Build(), strokeIndex, piece++, result, output);
        }
    }

    private static void Emit(HullSolid template, (int X, int Y, int Z) anchor, HullMesh mesh, int strokeIndex, int copy, ExpandedHull result, List<HullSolid> output)
    {
        var solid = template;
        solid.Id = GeneratedIdBase | ((uint)strokeIndex << 16) | (uint)(copy & 0xFFFF);
        solid.PosX = anchor.X;
        solid.PosY = anchor.Y;
        solid.PosZ = anchor.Z;
        solid.Param = (ushort)result.Meshes.Count;
        (solid.HalfX, solid.HalfY, solid.HalfZ) = mesh.HalfUnits();
        result.Meshes.Add(mesh);
        output.Add(solid);
    }

    private static (int X, int Y, int Z) Quantise(Vector3d v) => (
        (int)Math.Round(v.X * HullSolid.PositionUnitsPerMetre),
        (int)Math.Round(v.Y * HullSolid.PositionUnitsPerMetre),
        (int)Math.Round(v.Z * HullSolid.PositionUnitsPerMetre));
}
