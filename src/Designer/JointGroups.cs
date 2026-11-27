using BrutalGame.Designer.Tools;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer;

/// <summary>
/// The ship split into its moving parts for baking and drawing: group 0 is the ship's own, group k the moving part of the
/// k-th joint. A plan solid goes with its joint; what sits on the hull (brush strokes, windows, decals, placed components)
/// goes with the solid it is on (<see cref="SolidUnder"/>); hallways stay with the ship. Built when the plan changes.
/// </summary>
public sealed class JointGroups
{
    private readonly Dictionary<uint, int> _solidGroup = new();
    private readonly List<int> _strokeGroup = new();
    private readonly List<int> _windowGroup = new();
    private readonly List<int> _decalGroup = new();
    private readonly List<int> _placementGroup = new();

    /// <summary>The joints in group order (group k is <c>Joints[k - 1]</c>).</summary>
    public readonly List<HullJoint> Joints = new();

    public int Count => Joints.Count + 1;

    /// <summary>The group of joint <paramref name="joint"/> (0: the ship's own).</summary>
    public int GroupOfJoint(uint joint)
    {
        for (var i = 0; i < Joints.Count; i++)
        {
            if (Joints[i].Id == joint)
                return i + 1;
        }

        return 0;
    }

    public static JointGroups Build(PlanDocument doc)
    {
        var groups = new JointGroups();

        if (doc.Joints is not { } set)
            return groups;

        groups.Joints.AddRange(set.Joints);

        foreach (var (solid, joint) in set.Members)
            groups._solidGroup[solid] = groups.GroupOfJoint(joint);

        if (doc.Brushes is { } brushes)
        {
            foreach (var s in brushes.Strokes)
                groups._strokeGroup.Add(s.Points.Count > 0 ? groups.SolidGroup(SolidUnder(doc, s.Points[0].Position, s.Points[0].Normal)) : 0);
        }

        if (doc.Windows is { } windows)
        {
            foreach (var w in windows.Windows)
                groups._windowGroup.Add(groups.SolidGroup(SolidUnder(doc, w.Position, w.Normal)));
        }

        if (doc.Decals is { } decals)
        {
            foreach (var d in decals.Decals)
                groups._decalGroup.Add(groups.SolidGroup(SolidUnder(doc, d.Position, d.Normal)));
        }

        if (doc.Components is { } components)
        {
            foreach (var p in components.Placements)
                groups._placementGroup.Add(groups.SolidGroup(SolidUnder(doc, p.Position, new Vector3d(0, 1, 0))));
        }

        return groups;
    }

    public int SolidGroup(uint solid) => _solidGroup.TryGetValue(solid, out var g) ? g : 0;

    public int StrokeGroup(int stroke) => stroke < _strokeGroup.Count ? _strokeGroup[stroke] : 0;

    public int DecalGroup(int decal) => decal < _decalGroup.Count ? _decalGroup[decal] : 0;

    public int PlacementGroup(int placement) => placement < _placementGroup.Count ? _placementGroup[placement] : 0;

    /// <summary>The group of a solid in the bake: a plan solid by its joint, a generated one by what made it.</summary>
    public int GroupOf(in HullSolid solid)
    {
        var id = solid.Id;

        if (id >= StrokeExpansion.GeneratedIdBase)
            return StrokeGroup((int)((id >> 16) & 0xFFF));

        if ((id & 0xF0000000) == 0xE0000000)
            return PlacementGroup((int)(id & 0x0FFFFFFF));

        if ((id & 0xF0000000) == WindowGeometry.CutIdBase)
            return (int)(id & 0x07FFFFFF) < _windowGroup.Count ? _windowGroup[(int)(id & 0x07FFFFFF)] : 0;

        if ((id & 0xF0000000) == 0xD0000000)
            return 0;

        return SolidGroup(id);
    }

    /// <summary>The solid a short ray from just off the point back along its normal hits (the surface it sits on), or 0.</summary>
    public static uint SolidUnder(PlanDocument doc, Vector3d at, Vector3d normal) =>
        HullPicking.Pick(doc, Add(at, Scale(normal, 0.25)), Scale(normal, -1), out _);
}
