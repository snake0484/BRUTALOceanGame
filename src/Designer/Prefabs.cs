using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer;

/// <summary>A folder or a prefab in the library tree.</summary>
public sealed class PrefabNode
{
    public string Name = "";
    public readonly List<PrefabNode> Children = new();

    /// <summary>The prefab; null for a folder.</summary>
    public PlanDocument? Prefab;

    /// <summary>The file it came from; empty for a built-in catalog entry.</summary>
    public string Path = "";

    public bool IsFolder => Prefab is null;
}

/// <summary>
/// The component prefabs the ship designer can place, as a tree: the built-in catalog (made in code, so it is always
/// there and always current) and the user's component plans, one folder per category under the library folder.
/// </summary>
public sealed class PrefabLibrary
{
    public const string Extension = "bplan";

    public readonly PrefabNode Root = new() { Name = "Components" };

    public string UserFolder { get; }

    public PrefabLibrary(string userFolder) => UserFolder = userFolder;

    /// <summary>Rebuilds the tree: catalog entries, then every component plan in the user folder.</summary>
    public void Refresh(IEnumerable<(string Category, PlanDocument Prefab)> catalog)
    {
        Root.Children.Clear();
        var builtIn = new PrefabNode { Name = "Catalog" };
        Root.Children.Add(builtIn);

        foreach (var (category, prefab) in catalog)
            Folder(builtIn, category).Children.Add(new PrefabNode { Name = prefab.Meta.Name.Length > 0 ? prefab.Meta.Name : "Unnamed", Prefab = prefab });

        var mine = new PrefabNode { Name = "My components" };
        Root.Children.Add(mine);

        if (!Directory.Exists(UserFolder))
            return;

        foreach (var file in Directory.EnumerateFiles(UserFolder, "*." + Extension, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!PlanFormat.TryDecode(File.ReadAllBytes(file), out var doc, out _) || doc.Level != PlanLevel.Component)
                continue;

            var relative = System.IO.Path.GetRelativePath(UserFolder, System.IO.Path.GetDirectoryName(file)!);
            var parent = relative == "." ? mine : Folder(mine, relative.Replace('\\', '/'));
            parent.Children.Add(new PrefabNode { Name = doc.Meta.Name.Length > 0 ? doc.Meta.Name : System.IO.Path.GetFileNameWithoutExtension(file), Prefab = doc, Path = file });
        }
    }

    private static PrefabNode Folder(PrefabNode parent, string path)
    {
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = parent.Children.Find(c => c.IsFolder && c.Name == name);

            if (next is null)
            {
                next = new PrefabNode { Name = name };
                parent.Children.Add(next);
            }

            parent = next;
        }

        return parent;
    }
}

/// <summary>
/// What a prefab looks like and weighs, in its own frame, computed once per prefab: the housing's exact surface, every
/// part's and pipe's surface, the bounds (its hull cutter) and the metal mass of its parts and pipes.
/// </summary>
public sealed class PrefabShape
{
    public readonly List<SurfacePolygon> Surface = new();
    public Vector3d Min, Max;
    public double PartsMassKg;

    public static PrefabShape Build(PlanDocument prefab, Func<DefId, int> density)
    {
        var shape = new PrefabShape();
        shape.Surface.AddRange(HullCsg.Shaded(prefab.Solids.AsSpan(), prefab.Meshes));
        var data = prefab.Components;

        if (data is not null)
        {
            var part = new List<SurfacePolygon>();

            foreach (var p in data.Parts)
            {
                part.Clear();
                PartGeometry.Surface(p, part);
                shape.Surface.AddRange(part);
                shape.PartsMassKg += PartGeometry.Volume(part) * Density(prefab, p.Material, density);
            }

            foreach (var pipe in data.Pipes)
            {
                PipeGeometry.Surface(pipe, shape.Surface);
                shape.PartsMassKg += PipeGeometry.Volume(pipe) * Density(prefab, pipe.Material, density);
            }
        }

        shape.Min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        shape.Max = new Vector3d(double.MinValue, double.MinValue, double.MinValue);

        foreach (var poly in shape.Surface)
        {
            foreach (var p in poly.Points)
            {
                shape.Min = new Vector3d(Math.Min(shape.Min.X, p.X), Math.Min(shape.Min.Y, p.Y), Math.Min(shape.Min.Z, p.Z));
                shape.Max = new Vector3d(Math.Max(shape.Max.X, p.X), Math.Max(shape.Max.Y, p.Y), Math.Max(shape.Max.Z, p.Z));
            }
        }

        if (shape.Surface.Count == 0)
            shape.Min = shape.Max = default;

        return shape;
    }

    private static double Density(PlanDocument doc, byte material, Func<DefId, int> density) => material < doc.Materials.Count ? density(doc.Materials[material]) : 0;

    /// <summary>The surface placed: rotated and moved into the ship, materials mapped to the ship's palette.</summary>
    public void Place(in ComponentPlacement placement, Func<byte, byte> material, List<SurfacePolygon> output)
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(placement.Rotation, m);
        var p = placement.Position;

        foreach (var poly in Surface)
        {
            var placed = new SurfacePolygon { Points = new Vector3d[poly.Points.Length], Normals = new Vector3d[poly.Points.Length], Material = material(poly.Material) };

            for (var i = 0; i < poly.Points.Length; i++)
            {
                var v = poly.Points[i];
                var n = poly.Normals[i];
                placed.Points[i] = new Vector3d(p.X + m[0] * v.X + m[1] * v.Y + m[2] * v.Z, p.Y + m[3] * v.X + m[4] * v.Y + m[5] * v.Z, p.Z + m[6] * v.X + m[7] * v.Y + m[8] * v.Z);
                placed.Normals[i] = new Vector3d(m[0] * n.X + m[1] * n.Y + m[2] * n.Z, m[3] * n.X + m[4] * n.Y + m[5] * n.Z, m[6] * n.X + m[7] * n.Y + m[8] * n.Z);
            }

            output.Add(placed);
        }
    }

    /// <summary>
    /// The placement's hull cutter: a box over the prefab's bounds, turned with it (its rotation is a snap index, so the box
    /// is an ordinary solid). Placed components are cut out of the hull automatically; on a layered hull the cut is a
    /// Component-role solid, so it gets its own wall stack.
    /// </summary>
    public HullSolid Cutter(in ComponentPlacement placement, bool layered, uint id)
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(placement.Rotation, m);
        var c = Scale(Add(Min, Max), 0.5);
        var centre = Add(placement.Position, new Vector3d(m[0] * c.X + m[1] * c.Y + m[2] * c.Z, m[3] * c.X + m[4] * c.Y + m[5] * c.Z, m[6] * c.X + m[7] * c.Y + m[8] * c.Z));
        const double margin = 0.05;
        var half = Scale(Sub(Max, Min), 0.5);
        var (x, y, z) = ComponentCommands.Quantise(centre);
        return new HullSolid
        {
            Id = id,
            Shape = SolidShape.Box,
            Op = layered ? SolidOp.Add : SolidOp.Subtract,
            Flags = layered ? SolidRoles.With(SolidFlags.None, SolidRole.Component) : SolidFlags.None,
            PosX = x,
            PosY = y,
            PosZ = z,
            Rotation = placement.Rotation,
            HalfX = Half(half.X + margin),
            HalfY = Half(half.Y + margin),
            HalfZ = Half(half.Z + margin),
        };

        static ushort Half(double metres) => (ushort)Math.Clamp(Math.Ceiling(metres * HullSolid.HalfUnitsPerMetre), 16, ushort.MaxValue);
    }
}
