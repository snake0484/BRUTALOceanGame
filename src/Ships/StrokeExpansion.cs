using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships;

/// <summary>The bake's input once strokes are expanded: the plan's solids plus generated mesh solids, and the mesh table plus their meshes.</summary>
public sealed class ExpandedHull
{
    public HullSolid[] Solids = [];
    public List<HullMesh> Meshes = new();

    /// <summary>Generated solids (stamps and sweep pieces).</summary>
    public int Generated;

    /// <summary>Stroke points that found no hull within reach and stayed where they were.</summary>
    public int Unstuck;

    /// <summary>The stuck path of each stroke (ship metres), for drawing the line; parallel to the set's strokes.</summary>
    public List<StrokePath> Paths = new();
}

/// <summary>A stroke's points after sticking to the hull, densified along the surface.</summary>
public sealed class StrokePath
{
    public readonly List<Vector3d> Points = new();
    public readonly List<Vector3d> Normals = new();

    /// <summary>Index in <see cref="Points"/> of each control point.</summary>
    public readonly List<int> Controls = new();
}

/// <summary>
/// Expands brush strokes into ordinary mesh solids for the bake, deterministically from the plan (every peer gets the
/// same solids). Each control point is stuck to the hull (cast back along its normal from <see cref="Reach"/> out), the
/// segments between are resampled every <see cref="SampleMetres"/> and stuck too, so the line hugs curved hulls. A stamp
/// brush places a copy of its mesh every spacing, +Y on the normal and +Z along the line; a sweep brush builds a tube of
/// its profile along the path (split into pieces within the mesh limits), capped at both ends. Adding strokes become
/// panels on a layered hull (outside the skin); cutting strokes cut the skin, so the layers follow the groove.
/// </summary>
public static class StrokeExpansion
{
    public const double Reach = 2.0;
    public const double SampleMetres = 0.25;
    public const int MaxGenerated = 1024;
    public const int MaxPathPoints = 4096;
    public const uint GeneratedIdBase = 0xF0000000;
    private const double MinStep = 1e-3;

    public static ExpandedHull Expand(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, HullBrushSet? set, bool layered)
    {
        var result = new ExpandedHull();
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

            if (stroke.Brush >= set.Brushes.Count || path.Points.Count < 2 || stroke.Op is not (SolidOp.Add or SolidOp.Subtract))
                continue;

            var brush = set.Brushes[stroke.Brush];
            var template = new HullSolid
            {
                Shape = SolidShape.Mesh,
                Op = stroke.Op,
                Material = stroke.Material,
                Flags = layered && stroke.Op == SolidOp.Add ? SolidRoles.With(SolidFlags.None, SolidRole.Panel) : SolidFlags.None,
                Rotation = SolidRotation.Identity,
            };
            var scale = stroke.Scale / (double)HullStroke.UnitScale;
            var offset = stroke.Offset * HullSolid.MetresPerPositionUnit;

            if (brush.Kind == BrushKind.Stamp && brush.Mesh < meshes.Count && brush.Spacing >= HullBrush.MinSpacing)
                Stamps(path, meshes[brush.Mesh], brush.Spacing * HullSolid.MetresPerPositionUnit, scale, offset, s, template, result, generated);
            else if (brush.Kind == BrushKind.Sweep && brush.Profile.Count >= HullBrush.MinProfilePoints)
                Sweep(path, brush.Profile, scale, offset, s, template, result, generated);
        }

        result.Generated = generated.Count;
        var all = new HullSolid[solids.Length + generated.Count];
        solids.CopyTo(all);
        generated.CopyTo(all, solids.Length);
        result.Solids = all;
        return result;
    }

    /// <summary>The stroke stuck to the hull and resampled along it.</summary>
    public static StrokePath Path(HullSurface surface, HullStroke stroke, ref int unstuck)
    {
        var path = new StrokePath();
        Vector3d prevP = default, prevN = default;

        for (var i = 0; i < stroke.Points.Count; i++)
        {
            var point = stroke.Points[i];

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
                    Append(path, q, qn);
                }
            }

            Append(path, p, n);
            path.Controls.Add(path.Points.Count - 1);
            prevP = p;
            prevN = n;
        }

        return path;
    }

    private static void Append(StrokePath path, Vector3d p, Vector3d n)
    {
        if (path.Points.Count > 0 && Sub(p, path.Points[^1]).Length() < MinStep)
            return;

        path.Points.Add(p);
        path.Normals.Add(n);
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
                var placed = Transform(mesh, Add(p, Scale(ny, offset)), bx, ny, tz, scale, out var anchor);

                if (placed is not null)
                    Emit(template, anchor, placed, strokeIndex, copy++, result, output);

                next += spacing;
            }

            walked += length;
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

    private static void Sweep(StrokePath path, List<ProfilePoint> profile, double scale, double offset, int strokeIndex, HullSolid template, ExpandedHull result, List<HullSolid> output)
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
            corners[j] = (p.X * HullSolid.MetresPerPositionUnit * scale, p.Y * HullSolid.MetresPerPositionUnit * scale + offset);
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

                foreach (var (x, y) in corners)
                {
                    var q = Quantise(Add(centre, Add(Scale(bx, x), Scale(ny, y))));

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
