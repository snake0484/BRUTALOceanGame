using System.Diagnostics;
using System.Numerics;
using BrutalGame.Ships.Components;
using BrutalGame.Terrain;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships;

/// <summary>The drawn hull: exact surface polygons binned into bricks for the renderer, plus counts and timing.</summary>
public sealed class HullCsgResult
{
    public readonly List<HullBrick> Bricks = new();
    public int Fragments;
    public int Triangles;

    /// <summary>Whole build, when run on one thread.</summary>
    public float Ms;

    /// <summary>Join, T-junctions, binning and flair (single-threaded tail of a job build).</summary>
    public float FinishMs;

    /// <summary>Volume (m³) of the flair meshes drawn with the hull, per plan material index.</summary>
    public readonly Dictionary<byte, double> FlairVolume = new();
}

/// <summary>
/// The hull's surface as exact polygons, straight from the solids (no grid): each solid's faces are split against the
/// other solids (a convex solid by its planes, a mesh solid by the planes of the triangles that cross the piece), and a
/// piece is kept where the plan's ordered CSG changes from inside to outside across it (probed just in front of and
/// behind its centre). Later solids win coplanar faces. T-junctions are closed by inserting the vertices that lie on
/// each edge, then the polygons are clipped into 8 m bricks for <c>HullRenderer</c>. Flat faces and edges are exact;
/// cylinders are prisms within <see cref="ExactSurface.CylinderSagMetres"/> and shade smooth; mesh solids use their
/// (subdivided) triangles. On a layered hull this is the outer skin (skin solids and panels, opened where rooms and
/// components cross it); the inner layer surfaces stay on the grid. Brush flair meshes are drawn as they are.
/// </summary>
public static class HullCsg
{
    private const double SideEpsilon = 1e-7;
    private const double ProbeMetres = 1e-4;
    private const double WeldMetres = 1e-5;
    private const double CoplanarCos = 0.99999;
    private const double CellMetres = 1.0;

    internal enum ShadeKind : byte { Flat, Cylinder, Smooth }

    internal sealed class Face
    {
        public Vector3d[] Points = [];
        public Vector3d Normal;
        public ShadeKind Shade;
        public Vector3d AxisOrigin, AxisDirection;
        public PreparedMesh? Mesh;
        public int Triangle;
        public double[]? M;
        public Vector3d Position;
    }

    internal sealed class Solid
    {
        public int Order;
        public SolidOp Op;
        public byte Material;
        public bool Keep = true;
        public readonly List<Face> Faces = new();
        public (Vector3d N, double D)[]? Planes;
        public PreparedMesh? Mesh;
        public readonly double[] M = new double[9];
        public Vector3d Position;
        public Vector3d Min, Max;
    }

    internal sealed class Piece
    {
        public List<Vector3d> Points = new();
        public Face Face = null!;
        public Vector3d Min, Max;

        public void Bounds()
        {
            Min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
            Max = new Vector3d(double.MinValue, double.MinValue, double.MinValue);

            foreach (var p in Points)
            {
                Min = new Vector3d(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z));
                Max = new Vector3d(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z));
            }
        }
    }

    internal sealed class Output
    {
        public List<Vector3d> Points = new();
        public Face Face = null!;
        public bool Reversed;
        public byte Material;
    }

    /// <summary>
    /// A plan's solids made ready for the surface build (faces, planes, bounds, prepared meshes). Prepared on the calling
    /// thread (it reads the mesh cache); <see cref="SolidSurface"/> then runs per kept solid on any thread, and
    /// <see cref="Finish"/> joins the pieces.
    /// </summary>
    public sealed class Scene
    {
        internal readonly List<Solid> Geometry = new();
        internal readonly List<Solid> Paints = new();
        internal readonly List<Solid> Splitters = new();
        internal readonly List<Solid> Kept = new();
        internal readonly List<Solid> Flair = new();

        /// <summary>Surfaces drawn as they are: component parts, pipes, placed prefabs.</summary>
        internal IReadOnlyList<SurfacePolygon> Extras = [];

        /// <summary>Solids whose faces can be on the surface: one <see cref="SolidSurface"/> call each.</summary>
        public int Count => Kept.Count;
    }

    /// <summary>One kept solid's surface pieces, from <see cref="SolidSurface"/>.</summary>
    public sealed class Pieces
    {
        internal readonly List<Output> Items = new();
    }

    public static Scene Prepare(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, bool layered, ExpandedHull? flair = null, IReadOnlyList<SurfacePolygon>? extras = null)
    {
        var scene = new Scene { Extras = extras ?? [] };
        var cutters = new List<Solid>();
        var panels = new List<Solid>();

        foreach (ref readonly var s in solids)
        {
            var role = layered ? SolidRoles.Of(s) : SolidRole.Skin;

            if (s.Op is not (SolidOp.Add or SolidOp.Subtract or SolidOp.Intersect or SolidOp.Paint))
                continue;

            var mesh = s.Shape == SolidShape.Mesh && s.Param < meshes.Count ? MeshCache.Get(meshes[s.Param]) : null;
            var solid = Prepare(s, mesh);

            if (solid is null)
                continue;

            if (s.Op == SolidOp.Paint)
                scene.Paints.Add(solid);
            else if (role == SolidRole.Skin)
                scene.Geometry.Add(solid);
            else if (role == SolidRole.Panel)
            {
                solid.Op = SolidOp.Add;
                panels.Add(solid);
            }
            else
            {
                // Rooms and components open the skin where they cross it; their own walls are the grid's.
                solid.Op = SolidOp.Subtract;
                solid.Keep = false;
                cutters.Add(solid);
            }
        }

        scene.Geometry.AddRange(cutters);
        scene.Geometry.AddRange(panels);

        for (var i = 0; i < scene.Geometry.Count; i++)
        {
            scene.Geometry[i].Order = i;

            if (scene.Geometry[i].Keep)
                scene.Kept.Add(scene.Geometry[i]);
        }

        scene.Splitters.AddRange(scene.Geometry);
        scene.Splitters.AddRange(scene.Paints);

        if (flair is not null)
        {
            for (var i = flair.PlanSolids; i < flair.Solids.Length; i++)
            {
                var s = flair.Solids[i];

                if (s.Param < flair.Meshes.Count && Prepare(s, MeshCache.Get(flair.Meshes[s.Param])) is { } solid)
                    scene.Flair.Add(solid);
            }
        }

        return scene;
    }

    /// <summary>The surface pieces of kept solid <paramref name="index"/>. Reads the scene only: safe on any thread.</summary>
    public static Pieces SolidSurface(Scene scene, int index)
    {
        var pieces = new Pieces();
        var solid = scene.Kept[index];

        foreach (var face in solid.Faces)
            Surface(solid, face, scene.Splitters, scene.Geometry, scene.Paints, pieces.Items);

        return pieces;
    }

    /// <summary>Joins the pieces (in solid order, so the result is the same every time), closes T-junctions, bins into bricks, adds the flair.</summary>
    public static HullCsgResult Finish(Scene scene, IReadOnlyList<Pieces> perSolid)
    {
        var start = Stopwatch.GetTimestamp();
        var result = new HullCsgResult();
        var outputs = Join(perSolid);
        CloseTJunctions(outputs);
        result.Fragments = outputs.Count;
        var bins = new Dictionary<BrickKey, ChunkMesh>();

        foreach (var o in outputs)
            Bin(o.Points, o.Face, o.Reversed, o.Material, bins);

        AddFlair(scene, bins, result);

        foreach (var poly in scene.Extras)
            BinShaded(poly, bins);

        foreach (var (key, mesh) in bins)
        {
            if (mesh.IndexCount == 0)
                continue;

            mesh.Pack(HullBins.CellMetres);
            var brick = new HullBrick { Key = key };
            brick.Vertices = mesh.Packed.AsSpan(0, mesh.VertexCount).ToArray();
            brick.Indices = mesh.Indices.AsSpan(0, mesh.IndexCount).ToArray();
            brick.VertexCount = mesh.VertexCount;
            brick.IndexCount = mesh.IndexCount;
            result.Triangles += mesh.IndexCount / 3;
            result.Bricks.Add(brick);
        }

        result.FinishMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return result;
    }

    /// <summary>Builds the drawn surface on the calling thread (tests, benchmarks): prepare, every solid, finish.</summary>
    public static HullCsgResult Build(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, bool layered, ExpandedHull? flair = null)
    {
        var start = Stopwatch.GetTimestamp();
        var scene = Prepare(solids, meshes, layered, flair);
        var pieces = new Pieces[scene.Count];

        for (var i = 0; i < pieces.Length; i++)
            pieces[i] = SolidSurface(scene, i);

        var result = Finish(scene, pieces);
        result.Ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return result;
    }

    /// <summary>The surface polygons (ship metres, wound outward) with their materials, before binning: for tests and tools.</summary>
    public static List<(Vector3d[] Points, byte Material)> Polygons(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, bool layered)
    {
        var scene = Prepare(solids, meshes, layered);
        var pieces = new Pieces[scene.Count];

        for (var i = 0; i < pieces.Length; i++)
            pieces[i] = SolidSurface(scene, i);

        var outputs = Join(pieces);
        CloseTJunctions(outputs);
        var list = new List<(Vector3d[], byte)>();

        foreach (var o in outputs)
        {
            var points = o.Points.ToArray();

            if (o.Reversed)
                Array.Reverse(points);

            list.Add((points, o.Material));
        }

        return list;
    }

    private static List<Output> Join(IReadOnlyList<Pieces> perSolid)
    {
        var outputs = new List<Output>();

        foreach (var p in perSolid)
            outputs.AddRange(p.Items);

        return outputs;
    }

    // ---- solids to faces ----

    private static Solid? Prepare(in HullSolid s, PreparedMesh? mesh)
    {
        if (!SolidRotation.IsValid(s.Rotation) || (mesh is null && !SolidGeometry.IsSupported(s.Shape)))
            return null;

        var solid = new Solid { Op = s.Op, Material = s.Material, Position = new Vector3d(s.PositionX, s.PositionY, s.PositionZ), Mesh = mesh };
        SolidRotation.Matrix(s.Rotation, solid.M);

        if (mesh is not null)
        {
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                solid.Faces.Add(new Face
                {
                    Points = [ToShip(solid, mesh.A[t]), ToShip(solid, mesh.B[t]), ToShip(solid, mesh.C[t])],
                    Normal = Rotate(solid.M, mesh.Normals[t]),
                    Shade = ShadeKind.Smooth,
                    Mesh = mesh,
                    Triangle = t,
                    M = solid.M,
                    Position = solid.Position,
                });
            }
        }
        else if (s.Shape == SolidShape.Cylinder)
        {
            Cylinder(solid, SolidGeometry.Half(s));
        }
        else
        {
            var b = new MeshBuilder();
            int hx = s.HalfX * 4, hy = s.HalfY * 4, hz = s.HalfZ * 4;

            if (s.Shape == SolidShape.Wedge)
                MeshPrimitives.Wedge(b, hx, hy, hz);
            else
                MeshPrimitives.Box(b, hx, hy, hz);

            foreach (var corners in b.Faces)
            {
                var points = new Vector3d[corners.Length];

                for (var k = 0; k < corners.Length; k++)
                {
                    var v = b.Vertices[corners[k]];
                    points[k] = ToShip(solid, new Vector3d(v.X * HullSolid.MetresPerPositionUnit, v.Y * HullSolid.MetresPerPositionUnit, v.Z * HullSolid.MetresPerPositionUnit));
                }

                solid.Faces.Add(new Face { Points = points, Normal = Newell(points), Shade = ShadeKind.Flat });
            }
        }

        if (mesh is null)
        {
            solid.Planes = new (Vector3d, double)[solid.Faces.Count];

            for (var f = 0; f < solid.Faces.Count; f++)
                solid.Planes[f] = (solid.Faces[f].Normal, Dot(solid.Faces[f].Normal, solid.Faces[f].Points[0]));
        }

        solid.Min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        solid.Max = new Vector3d(double.MinValue, double.MinValue, double.MinValue);

        foreach (var face in solid.Faces)
        {
            foreach (var p in face.Points)
            {
                solid.Min = new Vector3d(Math.Min(solid.Min.X, p.X), Math.Min(solid.Min.Y, p.Y), Math.Min(solid.Min.Z, p.Z));
                solid.Max = new Vector3d(Math.Max(solid.Max.X, p.X), Math.Max(solid.Max.Y, p.Y), Math.Max(solid.Max.Z, p.Z));
            }
        }

        return solid;
    }

    /// <summary>The same prism <see cref="ExactSurface"/> draws: sides from the ring, caps as one polygon each.</summary>
    private static void Cylinder(Solid solid, Vector3 h)
    {
        var r = (double)h.X;
        var sides = ExactSurface.SidesFor(r);
        var ring = new Vector3d[sides];

        for (var i = 0; i < sides; i++)
        {
            var angle = i * Math.Tau / sides;
            ring[i] = new Vector3d((float)Math.Cos(angle) * r, 0.0, (float)Math.Sin(angle) * r);
        }

        var axis = Rotate(solid.M, new Vector3(0, 1, 0));
        var top = new Vector3d[sides];
        var bottom = new Vector3d[sides];

        for (var i = 0; i < sides; i++)
        {
            var j = (i + 1) % sides;
            var a = ring[i];
            var b = ring[j];
            var points = new[]
            {
                ToShip(solid, a with { Y = -h.Y }), ToShip(solid, a with { Y = h.Y }),
                ToShip(solid, b with { Y = h.Y }), ToShip(solid, b with { Y = -h.Y }),
            };
            solid.Faces.Add(new Face { Points = points, Normal = Newell(points), Shade = ShadeKind.Cylinder, AxisOrigin = solid.Position, AxisDirection = axis });
            top[sides - 1 - i] = ToShip(solid, a with { Y = h.Y });
            bottom[i] = ToShip(solid, a with { Y = -h.Y });
        }

        solid.Faces.Add(new Face { Points = top, Normal = Newell(top), Shade = ShadeKind.Flat });
        solid.Faces.Add(new Face { Points = bottom, Normal = Newell(bottom), Shade = ShadeKind.Flat });
    }

    // ---- splitting and classification ----

    private static void Surface(Solid owner, Face face, List<Solid> splitters, List<Solid> geometry, List<Solid> paints, List<Output> outputs)
    {
        var first = new Piece { Face = face, Points = new List<Vector3d>(face.Points) };
        first.Bounds();
        var pieces = new List<Piece> { first };
        var next = new List<Piece>();

        foreach (var other in splitters)
        {
            if (other == owner || !Overlaps(first.Min, first.Max, other.Min, other.Max))
                continue;

            next.Clear();

            foreach (var piece in pieces)
            {
                if (!Overlaps(piece.Min, piece.Max, other.Min, other.Max))
                    next.Add(piece);
                else if (other.Planes is not null)
                    SplitConvex(piece, other.Planes, next);
                else
                    SplitMesh(piece, other, next);
            }

            (pieces, next) = (next, pieces);
        }

        foreach (var piece in pieces)
        {
            if (piece.Points.Count < 3)
                continue;

            var c = Centroid(piece.Points);
            var n = face.Normal;
            var front = Inside(geometry, Add(c, Scale(n, ProbeMetres)));
            var behind = Inside(geometry, Add(c, Scale(n, -ProbeMetres)));

            if (front == behind || LaterCoplanar(owner, geometry, c, n))
                continue;

            var outward = behind ? n : Scale(n, -1.0);
            var material = owner.Material;
            var inner = Add(c, Scale(outward, -ProbeMetres));

            foreach (var paint in paints)
            {
                if (Contains(paint, inner))
                    material = paint.Material;
            }

            outputs.Add(new Output { Points = piece.Points, Face = face, Reversed = !behind, Material = material });
        }
    }

    /// <summary>Splits off the parts outside each plane in turn; what is left is inside the solid.</summary>
    private static void SplitConvex(Piece piece, (Vector3d N, double D)[] planes, List<Piece> output)
    {
        var rest = piece;

        foreach (var (n, d) in planes)
        {
            Sides(rest.Points, n, d, out var above, out var below);

            if (!above)
                continue;

            if (!below)
            {
                output.Add(rest);
                return;
            }

            Split(rest, n, d, out var outside, out var inside);
            output.Add(outside);
            rest = inside;
        }

        output.Add(rest);
    }

    /// <summary>Splits by the planes of the mesh triangles that cross the piece (both straddle each other's plane).</summary>
    private static void SplitMesh(Piece piece, Solid mesh, List<Piece> output)
    {
        var work = new List<Piece> { piece };
        var triangles = new List<int>();
        LocalBounds(mesh, piece.Min, piece.Max, out var lmin, out var lmax);
        mesh.Mesh!.Overlapping(lmin, lmax, triangles);

        foreach (var t in triangles)
        {
            var a = ToShip(mesh, mesh.Mesh.A[t]);
            var b = ToShip(mesh, mesh.Mesh.B[t]);
            var c = ToShip(mesh, mesh.Mesh.C[t]);
            var n = Rotate(mesh.M, mesh.Mesh.Normals[t]);
            var d = Dot(n, a);
            var tmin = new Vector3d(Math.Min(a.X, Math.Min(b.X, c.X)), Math.Min(a.Y, Math.Min(b.Y, c.Y)), Math.Min(a.Z, Math.Min(b.Z, c.Z)));
            var tmax = new Vector3d(Math.Max(a.X, Math.Max(b.X, c.X)), Math.Max(a.Y, Math.Max(b.Y, c.Y)), Math.Max(a.Z, Math.Max(b.Z, c.Z)));

            for (var i = work.Count - 1; i >= 0; i--)
            {
                var p = work[i];

                if (!Overlaps(p.Min, p.Max, tmin, tmax))
                    continue;

                Sides(p.Points, n, d, out var above, out var below);

                if (!above || !below)
                    continue;

                var pn = p.Face.Normal;
                var pd = Dot(pn, p.Points[0]);
                var sa = Dot(pn, a) - pd;
                var sb = Dot(pn, b) - pd;
                var sc = Dot(pn, c) - pd;

                if ((sa > SideEpsilon || sb > SideEpsilon || sc > SideEpsilon) && (sa < -SideEpsilon || sb < -SideEpsilon || sc < -SideEpsilon))
                {
                    Split(p, n, d, out var outside, out var inside);
                    work[i] = outside;
                    work.Add(inside);
                }
            }
        }

        output.AddRange(work);
    }

    private static void Sides(List<Vector3d> points, Vector3d n, double d, out bool above, out bool below)
    {
        above = below = false;

        foreach (var p in points)
        {
            var s = Dot(n, p) - d;

            if (s > SideEpsilon)
                above = true;
            else if (s < -SideEpsilon)
                below = true;
        }
    }

    private static void Split(Piece piece, Vector3d n, double d, out Piece above, out Piece below)
    {
        above = new Piece { Face = piece.Face };
        below = new Piece { Face = piece.Face };
        var points = piece.Points;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Count];
            var sp = Dot(n, p) - d;
            var sq = Dot(n, q) - d;

            if (sp >= -SideEpsilon)
                above.Points.Add(p);

            if (sp <= SideEpsilon)
                below.Points.Add(p);

            if ((sp > SideEpsilon && sq < -SideEpsilon) || (sp < -SideEpsilon && sq > SideEpsilon))
            {
                var x = Crossing(p, q, n, d);
                above.Points.Add(x);
                below.Points.Add(x);
            }
        }

        above.Bounds();
        below.Bounds();
    }

    /// <summary>The edge's crossing, computed from its endpoints in a canonical order so both pieces sharing the edge agree.</summary>
    private static Vector3d Crossing(Vector3d p, Vector3d q, Vector3d n, double d)
    {
        if (Less(q, p))
            (p, q) = (q, p);

        var sp = Dot(n, p) - d;
        var sq = Dot(n, q) - d;
        return Lerp(p, q, sp / (sp - sq));
    }

    private static bool Inside(List<Solid> geometry, Vector3d p)
    {
        var inside = false;

        foreach (var s in geometry)
        {
            var contains = Contains(s, p);

            inside = s.Op switch
            {
                SolidOp.Add => inside || contains,
                SolidOp.Subtract => inside && !contains,
                _ => inside && contains,
            };
        }

        return inside;
    }

    private static bool Contains(Solid s, Vector3d p)
    {
        if (p.X < s.Min.X || p.Y < s.Min.Y || p.Z < s.Min.Z || p.X > s.Max.X || p.Y > s.Max.Y || p.Z > s.Max.Z)
            return false;

        if (s.Planes is not null)
        {
            foreach (var (n, d) in s.Planes)
            {
                if (Dot(n, p) - d > 0.0)
                    return false;
            }

            return true;
        }

        // Ray parity along a fixed, slightly skewed direction (the watertight test counts shared edges once).
        var local = ToLocal(s, Sub(p, s.Position));
        var hits = new List<double>();
        s.Mesh!.Crossings(local, new Vector3d(0.5773, 0.5774, 0.5775), hits);
        return (hits.Count & 1) != 0;
    }

    /// <summary>A later convex solid has a face on the same plane, facing the same way, covering this point: it draws it.</summary>
    private static bool LaterCoplanar(Solid owner, List<Solid> geometry, Vector3d c, Vector3d n)
    {
        for (var j = owner.Order + 1; j < geometry.Count; j++)
        {
            var s = geometry[j];

            if (s.Planes is null || c.X < s.Min.X - 1e-6 || c.Y < s.Min.Y - 1e-6 || c.Z < s.Min.Z - 1e-6 || c.X > s.Max.X + 1e-6 || c.Y > s.Max.Y + 1e-6 || c.Z > s.Max.Z + 1e-6)
                continue;

            var on = false;
            var inside = true;

            foreach (var (pn, d) in s.Planes)
            {
                var dist = Dot(pn, c) - d;

                if (Math.Abs(dist) < 1e-6 && Math.Abs(Dot(pn, n)) > CoplanarCos)
                    on = true;
                else if (dist > 1e-6)
                    inside = false;
            }

            if (on && inside)
                return true;
        }

        return false;
    }

    // ---- T-junctions ----

    /// <summary>
    /// Welds corners, then inserts into every edge the welded corners that lie on it, so neighbours share vertices. Each
    /// corner is filed in every grid cell its weld tolerance touches; an edge then only visits the cells it passes
    /// through (a 3D DDA walk), since any corner on it lies in one of them.
    /// </summary>
    private static void CloseTJunctions(List<Output> outputs)
    {
        var weld = new Dictionary<(long, long, long), Vector3d>();
        var grid = new Dictionary<(int, int, int), List<Vector3d>>();

        foreach (var o in outputs)
        {
            for (var i = 0; i < o.Points.Count; i++)
            {
                var key = WeldKey(o.Points[i]);

                if (weld.TryGetValue(key, out var existing))
                {
                    o.Points[i] = existing;
                    continue;
                }

                var p = o.Points[i];
                weld[key] = p;
                var (x0, y0, z0) = Cell(new Vector3d(p.X - WeldMetres, p.Y - WeldMetres, p.Z - WeldMetres));
                var (x1, y1, z1) = Cell(new Vector3d(p.X + WeldMetres, p.Y + WeldMetres, p.Z + WeldMetres));

                for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    if (!grid.TryGetValue((x, y, z), out var list))
                        grid[(x, y, z)] = list = new List<Vector3d>();

                    list.Add(p);
                }
            }
        }

        var inserts = new List<(double T, Vector3d P)>();
        var rebuilt = new List<Vector3d>();

        foreach (var o in outputs)
        {
            rebuilt.Clear();

            for (var i = 0; i < o.Points.Count; i++)
            {
                var a = o.Points[i];
                var b = o.Points[(i + 1) % o.Points.Count];
                rebuilt.Add(a);
                var ab = Sub(b, a);
                var length2 = Dot(ab, ab);

                if (length2 < WeldMetres * WeldMetres * 4)
                    continue;

                inserts.Clear();
                Walk(a, b, grid, ab, length2, inserts);

                if (inserts.Count == 0)
                    continue;

                inserts.Sort((x, y) => x.T.CompareTo(y.T));
                var last = double.NaN;

                foreach (var (t, p) in inserts)
                {
                    if (t != last)
                        rebuilt.Add(p);

                    last = t;
                }
            }

            if (rebuilt.Count != o.Points.Count)
                o.Points = new List<Vector3d>(rebuilt);
        }
    }

    /// <summary>Visits the cells from a to b in order (Amanatides–Woo) and collects the corners lying on the open segment.</summary>
    private static void Walk(Vector3d a, Vector3d b, Dictionary<(int, int, int), List<Vector3d>> grid, Vector3d ab, double length2, List<(double, Vector3d)> output)
    {
        var (x, y, z) = Cell(a);
        var (ex, ey, ez) = Cell(b);
        int sx = Math.Sign(ab.X), sy = Math.Sign(ab.Y), sz = Math.Sign(ab.Z);
        double tdx = sx != 0 ? CellMetres / Math.Abs(ab.X) : double.PositiveInfinity;
        double tdy = sy != 0 ? CellMetres / Math.Abs(ab.Y) : double.PositiveInfinity;
        double tdz = sz != 0 ? CellMetres / Math.Abs(ab.Z) : double.PositiveInfinity;
        double tx = sx != 0 ? ((sx > 0 ? (x + 1) * CellMetres : x * CellMetres) - a.X) / ab.X : double.PositiveInfinity;
        double ty = sy != 0 ? ((sy > 0 ? (y + 1) * CellMetres : y * CellMetres) - a.Y) / ab.Y : double.PositiveInfinity;
        double tz = sz != 0 ? ((sz > 0 ? (z + 1) * CellMetres : z * CellMetres) - a.Z) / ab.Z : double.PositiveInfinity;
        var guard = Math.Abs(ex - x) + Math.Abs(ey - y) + Math.Abs(ez - z) + 3;

        for (var step = 0; step <= guard; step++)
        {
            if (grid.TryGetValue((x, y, z), out var list))
            {
                foreach (var p in list)
                {
                    var t = Dot(Sub(p, a), ab) / length2;

                    if (t <= 1e-9 || t >= 1.0 - 1e-9)
                        continue;

                    var d = Sub(p, Lerp(a, b, t));

                    if (Dot(d, d) < WeldMetres * WeldMetres)
                        output.Add((t, p));
                }
            }

            if (x == ex && y == ey && z == ez)
                return;

            if (tx <= ty && tx <= tz)
            {
                x += sx;
                tx += tdx;
            }
            else if (ty <= tz)
            {
                y += sy;
                ty += tdy;
            }
            else
            {
                z += sz;
                tz += tdz;
            }
        }
    }

    private static (long, long, long) WeldKey(Vector3d p) => ((long)Math.Round(p.X / WeldMetres), (long)Math.Round(p.Y / WeldMetres), (long)Math.Round(p.Z / WeldMetres));

    private static (int, int, int) Cell(Vector3d p) => ((int)Math.Floor(p.X / CellMetres), (int)Math.Floor(p.Y / CellMetres), (int)Math.Floor(p.Z / CellMetres));

    // ---- bricks ----

    private static void Bin(List<Vector3d> points, Face face, bool reversed, byte material, Dictionary<BrickKey, ChunkMesh> bins)
    {
        var min = points[0];
        var max = points[0];

        foreach (var p in points)
        {
            min = new Vector3d(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new Vector3d(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        const double size = HullBins.BrickMetres;
        int x0 = (int)Math.Floor(min.X / size), x1 = (int)Math.Floor(max.X / size);
        int y0 = (int)Math.Floor(min.Y / size), y1 = (int)Math.Floor(max.Y / size);
        int z0 = (int)Math.Floor(min.Z / size), z1 = (int)Math.Floor(max.Z / size);
        var polygon = new List<Vector3d>();
        var clipped = new List<Vector3d>();

        for (var bz = z0; bz <= z1; bz++)
        for (var by = y0; by <= y1; by++)
        for (var bx = x0; bx <= x1; bx++)
        {
            polygon.Clear();
            polygon.AddRange(points);
            var origin = new Vector3d(bx * size, by * size, bz * size);

            for (var axis = 0; axis < 3 && polygon.Count >= 3; axis++)
            {
                Clip(polygon, clipped, axis, Get(origin, axis), true);
                Clip(polygon, clipped, axis, Get(origin, axis) + size, false);
            }

            if (polygon.Count < 3)
                continue;

            var key = new BrickKey((short)bx, (short)by, (short)bz);

            if (!bins.TryGetValue(key, out var mesh))
                bins[key] = mesh = new ChunkMesh(256, 1024);

            Emit(mesh, polygon, origin, face, reversed, material);
        }
    }

    private static void Clip(List<Vector3d> polygon, List<Vector3d> clipped, int axis, double plane, bool keepAbove)
    {
        clipped.Clear();

        for (var i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            var pin = keepAbove ? Get(p, axis) >= plane : Get(p, axis) <= plane;
            var qin = keepAbove ? Get(q, axis) >= plane : Get(q, axis) <= plane;

            if (pin)
                clipped.Add(p);

            if (pin != qin)
            {
                var (a, b) = Less(q, p) ? (q, p) : (p, q);
                var t = (plane - Get(a, axis)) / (Get(b, axis) - Get(a, axis));
                var x = Lerp(a, b, t);
                clipped.Add(axis == 0 ? x with { X = plane } : axis == 1 ? x with { Y = plane } : x with { Z = plane });
            }
        }

        polygon.Clear();
        polygon.AddRange(clipped);
    }

    /// <summary>
    /// The surface as shaded polygons (corner normals from each solid's shading, wound outward), in the solids' own frame:
    /// a component prefab's look, computed once and then placed (transformed) as many times as it is used.
    /// </summary>
    public static List<SurfacePolygon> Shaded(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes)
    {
        var scene = Prepare(solids, meshes, layered: false);
        var pieces = new Pieces[scene.Count];

        for (var i = 0; i < pieces.Length; i++)
            pieces[i] = SolidSurface(scene, i);

        var outputs = Join(pieces);
        CloseTJunctions(outputs);
        var list = new List<SurfacePolygon>(outputs.Count);

        foreach (var o in outputs)
        {
            var n = o.Points.Count;
            var poly = new SurfacePolygon { Points = new Vector3d[n], Normals = new Vector3d[n], Material = o.Material };

            for (var k = 0; k < n; k++)
            {
                var p = o.Points[o.Reversed ? n - 1 - k : k];
                var normal = ShadeNormal(o.Face, p);
                poly.Points[k] = p;
                poly.Normals[k] = o.Reversed ? Scale(normal, -1.0) : normal;
            }

            list.Add(poly);
        }

        return list;
    }

    /// <summary>Clips a shaded polygon into the bricks it crosses (normals interpolated along clipped edges).</summary>
    private static void BinShaded(SurfacePolygon poly, Dictionary<BrickKey, ChunkMesh> bins)
    {
        if (poly.Points.Length < 3)
            return;

        var min = poly.Points[0];
        var max = poly.Points[0];

        foreach (var p in poly.Points)
        {
            min = new Vector3d(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new Vector3d(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        const double size = HullBins.BrickMetres;
        int x0 = (int)Math.Floor(min.X / size), x1 = (int)Math.Floor(max.X / size);
        int y0 = (int)Math.Floor(min.Y / size), y1 = (int)Math.Floor(max.Y / size);
        int z0 = (int)Math.Floor(min.Z / size), z1 = (int)Math.Floor(max.Z / size);
        var polygon = new List<(Vector3d P, Vector3d N)>();
        var clipped = new List<(Vector3d P, Vector3d N)>();

        for (var bz = z0; bz <= z1; bz++)
        for (var by = y0; by <= y1; by++)
        for (var bx = x0; bx <= x1; bx++)
        {
            polygon.Clear();

            for (var k = 0; k < poly.Points.Length; k++)
                polygon.Add((poly.Points[k], poly.Normals[k]));

            var origin = new Vector3d(bx * size, by * size, bz * size);

            for (var axis = 0; axis < 3 && polygon.Count >= 3; axis++)
            {
                ClipShaded(polygon, clipped, axis, Get(origin, axis), true);
                ClipShaded(polygon, clipped, axis, Get(origin, axis) + size, false);
            }

            if (polygon.Count < 3)
                continue;

            var key = new BrickKey((short)bx, (short)by, (short)bz);

            if (!bins.TryGetValue(key, out var mesh))
                bins[key] = mesh = new ChunkMesh(256, 1024);

            var n = polygon.Count;
            Reserve(mesh, n, (n - 2) * 3);
            var first = mesh.VertexCount;

            foreach (var (p, normal) in polygon)
            {
                mesh.Vertices[mesh.VertexCount++] = new TerrainVertex
                {
                    Position = new Vector3((float)(p.X - origin.X), (float)(p.Y - origin.Y), (float)(p.Z - origin.Z)),
                    Normal = new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z),
                    Material = poly.Material,
                };
            }

            for (var k = 1; k + 1 < n; k++)
            {
                mesh.Indices[mesh.IndexCount++] = (uint)first;
                mesh.Indices[mesh.IndexCount++] = (uint)(first + k);
                mesh.Indices[mesh.IndexCount++] = (uint)(first + k + 1);
            }
        }
    }

    private static void ClipShaded(List<(Vector3d P, Vector3d N)> polygon, List<(Vector3d P, Vector3d N)> clipped, int axis, double plane, bool keepAbove)
    {
        clipped.Clear();

        for (var i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            var pin = keepAbove ? Get(p.P, axis) >= plane : Get(p.P, axis) <= plane;
            var qin = keepAbove ? Get(q.P, axis) >= plane : Get(q.P, axis) <= plane;

            if (pin)
                clipped.Add(p);

            if (pin != qin)
            {
                var (a, b) = Less(q.P, p.P) ? (q, p) : (p, q);
                var t = (plane - Get(a.P, axis)) / (Get(b.P, axis) - Get(a.P, axis));
                var x = Lerp(a.P, b.P, t);
                x = axis == 0 ? x with { X = plane } : axis == 1 ? x with { Y = plane } : x with { Z = plane };
                clipped.Add((x, Normalise(Lerp(a.N, b.N, t), a.N)));
            }
        }

        polygon.Clear();
        polygon.AddRange(clipped);
    }

    private static void Emit(ChunkMesh mesh, List<Vector3d> polygon, Vector3d origin, Face face, bool reversed, byte material)
    {
        var n = polygon.Count;
        Reserve(mesh, n, (n - 2) * 3);
        var first = mesh.VertexCount;

        for (var k = 0; k < n; k++)
        {
            var p = polygon[reversed ? n - 1 - k : k];
            var normal = ShadeNormal(face, p);

            if (reversed)
                normal = Scale(normal, -1.0);

            mesh.Vertices[mesh.VertexCount++] = new TerrainVertex
            {
                Position = new Vector3((float)(p.X - origin.X), (float)(p.Y - origin.Y), (float)(p.Z - origin.Z)),
                Normal = new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z),
                Material = material,
            };
        }

        for (var k = 1; k + 1 < n; k++)
        {
            mesh.Indices[mesh.IndexCount++] = (uint)first;
            mesh.Indices[mesh.IndexCount++] = (uint)(first + k);
            mesh.Indices[mesh.IndexCount++] = (uint)(first + k + 1);
        }
    }

    private static void Reserve(ChunkMesh mesh, int vertices, int indices)
    {
        if (mesh.VertexCount + vertices > mesh.Vertices.Length)
        {
            Array.Resize(ref mesh.Vertices, Math.Max(mesh.VertexCount + vertices, mesh.Vertices.Length * 2));
            Array.Resize(ref mesh.Packed, mesh.Vertices.Length);
        }

        if (mesh.IndexCount + indices > mesh.Indices.Length)
            Array.Resize(ref mesh.Indices, Math.Max(mesh.IndexCount + indices, mesh.Indices.Length * 2));
    }

    private static Vector3d ShadeNormal(Face face, Vector3d p)
    {
        switch (face.Shade)
        {
            case ShadeKind.Cylinder:
            {
                var r = Sub(p, face.AxisOrigin);
                var radial = Sub(r, Scale(face.AxisDirection, Dot(r, face.AxisDirection)));
                return Normalise(radial, face.Normal);
            }
            case ShadeKind.Smooth:
            {
                var m = face.M!;
                var rel = Sub(p, face.Position);
                var local = new Vector3((float)(m[0] * rel.X + m[3] * rel.Y + m[6] * rel.Z), (float)(m[1] * rel.X + m[4] * rel.Y + m[7] * rel.Z), (float)(m[2] * rel.X + m[5] * rel.Y + m[8] * rel.Z));
                return Normalise(Rotate(m, face.Mesh!.SmoothNormal(face.Triangle, local)), face.Normal);
            }
            default:
                return face.Normal;
        }
    }

    // ---- flair ----

    /// <summary>Brush flair: the generated meshes, drawn as they are; their volume per material.</summary>
    private static void AddFlair(Scene scene, Dictionary<BrickKey, ChunkMesh> bins, HullCsgResult result)
    {
        foreach (var solid in scene.Flair)
        {
            var volume = 0.0;

            foreach (var face in solid.Faces)
            {
                var a = Sub(face.Points[0], solid.Position);
                var b = Sub(face.Points[1], solid.Position);
                var c = Sub(face.Points[2], solid.Position);
                volume += Dot(a, Cross(b, c)) / 6.0;
                Bin(new List<Vector3d>(face.Points), face, false, solid.Material, bins);
            }

            result.FlairVolume[solid.Material] = result.FlairVolume.GetValueOrDefault(solid.Material) + volume;
        }
    }

    // ---- maths ----

    private static Vector3d ToShip(Solid s, Vector3 local) => ToShip(s, new Vector3d(local.X, local.Y, local.Z));

    private static Vector3d ToShip(Solid s, Vector3d l)
    {
        var m = s.M;
        return new Vector3d(
            s.Position.X + m[0] * l.X + m[1] * l.Y + m[2] * l.Z,
            s.Position.Y + m[3] * l.X + m[4] * l.Y + m[5] * l.Z,
            s.Position.Z + m[6] * l.X + m[7] * l.Y + m[8] * l.Z);
    }

    private static Vector3d ToLocal(Solid s, Vector3d v)
    {
        var m = s.M;
        return new Vector3d(m[0] * v.X + m[3] * v.Y + m[6] * v.Z, m[1] * v.X + m[4] * v.Y + m[7] * v.Z, m[2] * v.X + m[5] * v.Y + m[8] * v.Z);
    }

    private static Vector3d Rotate(double[] m, Vector3 v) => Normalise(new Vector3d(
        m[0] * v.X + m[1] * v.Y + m[2] * v.Z,
        m[3] * v.X + m[4] * v.Y + m[5] * v.Z,
        m[6] * v.X + m[7] * v.Y + m[8] * v.Z), new Vector3d(0, 1, 0));

    private static Vector3d Rotate(double[] m, Vector3d v) => Rotate(m, new Vector3((float)v.X, (float)v.Y, (float)v.Z));

    private static void LocalBounds(Solid s, Vector3d min, Vector3d max, out Vector3 lmin, out Vector3 lmax)
    {
        lmin = new Vector3(float.MaxValue);
        lmax = new Vector3(float.MinValue);

        for (var c = 0; c < 8; c++)
        {
            var p = new Vector3d((c & 1) != 0 ? max.X : min.X, (c & 2) != 0 ? max.Y : min.Y, (c & 4) != 0 ? max.Z : min.Z);
            var l = ToLocal(s, Sub(p, s.Position));
            var f = new Vector3((float)l.X, (float)l.Y, (float)l.Z);
            lmin = Vector3.Min(lmin, f);
            lmax = Vector3.Max(lmax, f);
        }

        lmin -= new Vector3(1e-3f);
        lmax += new Vector3(1e-3f);
    }

    /// <summary>Newell's normal: robust for any planar polygon.</summary>
    private static Vector3d Newell(Vector3d[] points)
    {
        double x = 0, y = 0, z = 0;

        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Length];
            x += (p.Y - q.Y) * (p.Z + q.Z);
            y += (p.Z - q.Z) * (p.X + q.X);
            z += (p.X - q.X) * (p.Y + q.Y);
        }

        return Normalise(new Vector3d(x, y, z), new Vector3d(0, 1, 0));
    }

    private static Vector3d Centroid(List<Vector3d> points)
    {
        double x = 0, y = 0, z = 0;

        foreach (var p in points)
        {
            x += p.X;
            y += p.Y;
            z += p.Z;
        }

        return new Vector3d(x / points.Count, y / points.Count, z / points.Count);
    }

    private static bool Overlaps(Vector3d amin, Vector3d amax, Vector3d bmin, Vector3d bmax) =>
        amin.X <= bmax.X + 1e-6 && amax.X >= bmin.X - 1e-6 && amin.Y <= bmax.Y + 1e-6 && amax.Y >= bmin.Y - 1e-6 && amin.Z <= bmax.Z + 1e-6 && amax.Z >= bmin.Z - 1e-6;

    private static bool Less(Vector3d a, Vector3d b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    private static double Get(Vector3d v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
