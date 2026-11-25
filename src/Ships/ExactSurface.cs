using System.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.Ships;

/// <summary>
/// The exact render surface of one brush inside one brick: its own polygons (a mesh brush's subdivided triangles, or a
/// finely tessellated primitive), placed in ship space in double, made brick-relative and clipped to the brick's box.
/// Used where the bake shows that brush is the only one reaching the brick, so the surface there is exactly the brush's:
/// no grid, no resampling. Neighbouring exact bricks clip on the same planes from the same triangles, so they meet
/// without cracks; next to a dual-contoured brick the box grows one cell toward it so the two overlap instead of gapping.
/// Owns its scratch; one per worker.
/// </summary>
public sealed class ExactSurface
{
    public const int MinCylinderSides = 32;
    public const int MaxCylinderSides = 256;

    /// <summary>Largest gap between a cylinder's true surface and its polygon side.</summary>
    public const double CylinderSagMetres = 0.001;

    private readonly List<int> _triangles = new();
    private readonly List<Corner> _polygon = new();
    private readonly List<Corner> _clipped = new();
    private readonly double[] _rotation = new double[9];

    private struct Corner
    {
        public Vector3d Position;
        public Vector3 Normal;
    }

    private readonly Vector3[] _ring = new Vector3[MaxCylinderSides];

    /// <summary>
    /// Appends the brush's surface inside <c>[min, max]</c> (brick-relative metres) to <paramref name="output"/> as
    /// brick-relative vertices. The output is packed by the caller.
    /// </summary>
    public void Append(in HullBrush brush, PreparedMesh? mesh, Vector3d brickOrigin, Vector3d min, Vector3d max, ChunkMesh output)
    {
        BrushRotation.Matrix(brush.Rotation, _rotation);
        var position = new Vector3d(brush.PositionX - brickOrigin.X, brush.PositionY - brickOrigin.Y, brush.PositionZ - brickOrigin.Z);

        if (brush.Shape == BrushShape.Mesh)
        {
            if (mesh is null)
                return;

            AppendMesh(mesh, position, min, max, brush.Material, output);
            return;
        }

        var h = BrushGeometry.Half(brush);

        switch (brush.Shape)
        {
            case BrushShape.Cylinder:
                AppendCylinder(h, position, min, max, brush.Material, output);
                break;
            case BrushShape.Wedge:
                AppendWedge(h, position, min, max, brush.Material, output);
                break;
            default:
                AppendBox(h, position, min, max, brush.Material, output);
                break;
        }
    }

    private void AppendMesh(PreparedMesh mesh, Vector3d position, Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        // The clip box in brush-local axes (its local AABB) selects candidate triangles.
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);

        for (var c = 0; c < 8; c++)
        {
            var corner = new Vector3d((c & 1) != 0 ? max.X : min.X, (c & 2) != 0 ? max.Y : min.Y, (c & 4) != 0 ? max.Z : min.Z);
            var local = ToLocal(new Vector3d(corner.X - position.X, corner.Y - position.Y, corner.Z - position.Z));
            lo = Vector3.Min(lo, local);
            hi = Vector3.Max(hi, local);
        }

        _triangles.Clear();
        mesh.Overlapping(lo, hi, _triangles);
        var normals = mesh.CornerNormals;

        foreach (var t in _triangles)
        {
            _polygon.Clear();
            Add(mesh.A[t], normals[3 * t], position);
            Add(mesh.B[t], normals[3 * t + 1], position);
            Add(mesh.C[t], normals[3 * t + 2], position);
            Emit(min, max, material, output);
        }
    }

    /// <summary>Box faces, CCW from outside, flat normals.</summary>
    private void AppendBox(Vector3 h, Vector3d position, Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        ReadOnlySpan<int> faces = [1, 3, 7, 5, 0, 4, 6, 2, 2, 6, 7, 3, 0, 1, 5, 4, 4, 5, 7, 6, 0, 2, 3, 1];
        Span<Vector3> corners = stackalloc Vector3[8];

        for (var i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);

        for (var f = 0; f < 24; f += 4)
            Quad(corners[faces[f]], corners[faces[f + 1]], corners[faces[f + 2]], corners[faces[f + 3]], position, min, max, material, output);
    }

    /// <summary>The wedge brush: bottom, back (-Z), slope, two side triangles.</summary>
    private void AppendWedge(Vector3 h, Vector3d position, Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        Vector3 v0 = new(-h.X, -h.Y, -h.Z), v1 = new(h.X, -h.Y, -h.Z), v2 = new(-h.X, -h.Y, h.Z), v3 = new(h.X, -h.Y, h.Z), v4 = new(-h.X, h.Y, -h.Z), v5 = new(h.X, h.Y, -h.Z);
        Quad(v0, v1, v3, v2, position, min, max, material, output);
        Quad(v0, v4, v5, v1, position, min, max, material, output);
        Quad(v2, v3, v5, v4, position, min, max, material, output);
        Flat(v0, v2, v4, position, min, max, material, output);
        Flat(v1, v5, v3, position, min, max, material, output);
    }

    /// <summary>
    /// A cylinder along local Y: a smooth side of as many quads as keep the polygon within <see cref="CylinderSagMetres"/>
    /// of the circle, and flat caps.
    /// </summary>
    private void AppendCylinder(Vector3 h, Vector3d position, Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        var r = h.X;
        var sides = SidesFor(r);
        var top = new Vector3(0, h.Y, 0);
        var bottom = new Vector3(0, -h.Y, 0);

        for (var i = 0; i < sides; i++)
        {
            var angle = i * Math.Tau / sides;
            _ring[i] = new Vector3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
        }

        for (var i = 0; i < sides; i++)
        {
            var j = (i + 1) % sides;
            var a = _ring[i] * r;
            var b = _ring[j] * r;

            _polygon.Clear();
            Add(a + bottom, _ring[i], position);
            Add(a + top, _ring[i], position);
            Add(b + top, _ring[j], position);
            Add(b + bottom, _ring[j], position);
            Emit(min, max, material, output);

            Flat(top, b + top, a + top, position, min, max, material, output);
            Flat(bottom, a + bottom, b + bottom, position, min, max, material, output);
        }
    }

    /// <summary>Sides so the chord sag r (1 − cos(π / n)) stays under <see cref="CylinderSagMetres"/>, clamped.</summary>
    public static int SidesFor(double radius)
    {
        var half = radius > CylinderSagMetres ? Math.Acos(1.0 - CylinderSagMetres / radius) : Math.PI;
        return Math.Clamp((int)Math.Ceiling(Math.PI / half), MinCylinderSides, MaxCylinderSides);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3d position, Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        _polygon.Clear();
        Add(a, n, position);
        Add(b, n, position);
        Add(c, n, position);
        Add(d, n, position);
        Emit(min, max, material, output);
    }

    private void Flat(Vector3 a, Vector3 b, Vector3 c, Vector3d position, Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        _polygon.Clear();
        Add(a, n, position);
        Add(b, n, position);
        Add(c, n, position);
        Emit(min, max, material, output);
    }

    /// <summary>A brush-local corner into the brick frame: R · local + position, in double.</summary>
    private void Add(Vector3 local, Vector3 normal, Vector3d position)
    {
        var m = _rotation;
        _polygon.Add(new Corner
        {
            Position = new Vector3d(
                position.X + m[0] * local.X + m[1] * local.Y + m[2] * local.Z,
                position.Y + m[3] * local.X + m[4] * local.Y + m[5] * local.Z,
                position.Z + m[6] * local.X + m[7] * local.Y + m[8] * local.Z),
            Normal = new Vector3(
                (float)(m[0] * normal.X + m[1] * normal.Y + m[2] * normal.Z),
                (float)(m[3] * normal.X + m[4] * normal.Y + m[5] * normal.Z),
                (float)(m[6] * normal.X + m[7] * normal.Y + m[8] * normal.Z)),
        });
    }

    private Vector3 ToLocal(Vector3d v)
    {
        var m = _rotation;
        return new Vector3((float)(m[0] * v.X + m[3] * v.Y + m[6] * v.Z), (float)(m[1] * v.X + m[4] * v.Y + m[7] * v.Z), (float)(m[2] * v.X + m[5] * v.Y + m[8] * v.Z));
    }

    /// <summary>Clips the polygon to the box (Sutherland–Hodgman, fixed plane order) and fans it into the output.</summary>
    private void Emit(Vector3d min, Vector3d max, byte material, ChunkMesh output)
    {
        for (var axis = 0; axis < 3 && _polygon.Count >= 3; axis++)
        {
            ClipPlane(axis, Get(min, axis), keepAbove: true);
            ClipPlane(axis, Get(max, axis), keepAbove: false);
        }

        if (_polygon.Count < 3)
            return;

        var first = output.VertexCount;
        var vertices = _polygon.Count;
        var indices = (vertices - 2) * 3;

        if (output.VertexCount + vertices > output.Vertices.Length)
        {
            Array.Resize(ref output.Vertices, Math.Max(output.VertexCount + vertices, output.Vertices.Length * 2));
            Array.Resize(ref output.Packed, output.Vertices.Length);
        }

        if (output.IndexCount + indices > output.Indices.Length)
            Array.Resize(ref output.Indices, Math.Max(output.IndexCount + indices, output.Indices.Length * 2));

        foreach (var corner in _polygon)
        {
            output.Vertices[output.VertexCount++] = new TerrainVertex
            {
                Position = new Vector3((float)corner.Position.X, (float)corner.Position.Y, (float)corner.Position.Z),
                Normal = Vector3.Normalize(corner.Normal),
                Material = material,
            };
        }

        for (var i = 1; i + 1 < vertices; i++)
        {
            output.Indices[output.IndexCount++] = (uint)first;
            output.Indices[output.IndexCount++] = (uint)(first + i);
            output.Indices[output.IndexCount++] = (uint)(first + i + 1);
        }
    }

    private void ClipPlane(int axis, double plane, bool keepAbove)
    {
        _clipped.Clear();
        var n = _polygon.Count;

        for (var i = 0; i < n; i++)
        {
            var p = _polygon[i];
            var q = _polygon[(i + 1) % n];
            var dp = keepAbove ? Get(p.Position, axis) - plane : plane - Get(p.Position, axis);
            var dq = keepAbove ? Get(q.Position, axis) - plane : plane - Get(q.Position, axis);

            if (dp >= 0)
                _clipped.Add(p);

            if ((dp >= 0) != (dq >= 0))
                _clipped.Add(Intersect(p, q, axis, plane));
        }

        _polygon.Clear();
        _polygon.AddRange(_clipped);
    }

    /// <summary>
    /// The edge's crossing of the plane, computed from its endpoints in a canonical order so the brick on the other side
    /// of the plane gets the same point; the crossing coordinate is the plane itself.
    /// </summary>
    private static Corner Intersect(Corner p, Corner q, int axis, double plane)
    {
        if (Less(q.Position, p.Position))
            (p, q) = (q, p);

        var t = (plane - Get(p.Position, axis)) / (Get(q.Position, axis) - Get(p.Position, axis));
        var position = new Vector3d(p.Position.X + (q.Position.X - p.Position.X) * t, p.Position.Y + (q.Position.Y - p.Position.Y) * t, p.Position.Z + (q.Position.Z - p.Position.Z) * t);
        position = axis == 0 ? position with { X = plane } : axis == 1 ? position with { Y = plane } : position with { Z = plane };
        return new Corner { Position = position, Normal = Vector3.Lerp(p.Normal, q.Normal, (float)t) };
    }

    private static bool Less(Vector3d a, Vector3d b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    private static double Get(Vector3d v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
