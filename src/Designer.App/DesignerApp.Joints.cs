using Brutal.Numerics;
using BrutalGame.Designer.Ui;
using BrutalGame.Engine.Jobs;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;

namespace BrutalGame.Designer.App;

/// <summary>
/// Rotors and hinges in the designer: the ship split into moving parts (<see cref="JointGroups"/>), each baked and drawn
/// on its own (the ship's own part on the main bake and surface builder, each joint's on a one-thread pair of its own,
/// made when first needed), so a cut in one part never cuts another; and the preview ("Play joints"), which turns each
/// part about its joint. A ship without joints bakes exactly as before.
/// </summary>
public sealed partial class DesignerApp
{
    private JointGroups _groups = new();
    private readonly List<GroupBake> _groupBakes = new();
    private readonly List<List<SurfacePolygon>> _groupExtras = new();
    private double _jointSeconds;
    private double[] _poseRotations = new double[9];
    private Vector3d[] _poseTranslations = new Vector3d[1];

    /// <summary>One moving part's own bake and surface.</summary>
    private sealed class GroupBake : IDisposable
    {
        public readonly HullBaker Baker = new(1);
        public readonly HullSurfaceBuilder Surface = new(1);
        public HullCsgResult? Result;
        public bool Active;
        public int SeenBake = -1;

        public bool Busy => Baker.Busy || Surface.Busy;

        public void Dispose()
        {
            Baker.Wait();
            Surface.Wait();
            Baker.Dispose();
            Surface.Dispose();
        }
    }

    private void ClearGroupExtras()
    {
        foreach (var e in _groupExtras)
            e.Clear();
    }

    /// <summary>The surface polygons list of moving part <paramref name="group"/> (the ship's own is <paramref name="own"/>).</summary>
    private List<SurfacePolygon> ExtrasOf(int group, List<SurfacePolygon> own)
    {
        if (group == 0)
            return own;

        while (_groupExtras.Count < group)
            _groupExtras.Add(new List<SurfacePolygon>());

        return _groupExtras[group - 1];
    }

    /// <summary>Starts the bakes: one for the whole ship without joints; one per moving part with them.</summary>
    private void StartBakes(List<HullSolid> solids, IReadOnlyList<HullMesh> meshes, int[] densities, HullLayering? layers, ExpandedHull expanded, List<SurfacePolygon> extras)
    {
        _baker.InnerSurfacesOnly = !_stats.ShowGrid;

        if (_groups.Count == 1)
        {
            foreach (var bake in _groupBakes)
                (bake.Active, bake.Result) = (false, null);

            _baker.Start(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids), meshes, densities, layers);
            _surfaceBuilder.Start(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids), meshes, layers is not null, expanded, extras);
            return;
        }

        for (var g = 0; g < Math.Min(_groups.Count, HullRenderer.MaxGroups); g++)
        {
            var own = solids.Where(s => _groups.GroupOf(s) == g).ToArray();
            var flair = FlairOf(expanded, g);

            if (g == 0)
            {
                _baker.Start(own, meshes, densities, layers);
                _surfaceBuilder.Start(own, meshes, layers is not null, flair, extras);
                continue;
            }

            while (_groupBakes.Count < g)
                _groupBakes.Add(new GroupBake());

            var bake = _groupBakes[g - 1];
            bake.Active = true;
            bake.Baker.InnerSurfacesOnly = !_stats.ShowGrid;
            bake.Baker.Start(own, meshes, densities, layers);
            bake.Surface.Start(own, meshes, layers is not null, flair, ExtrasOf(g, extras).ToList());
        }

        for (var g = _groups.Count; g <= _groupBakes.Count; g++)
            (_groupBakes[g - 1].Active, _groupBakes[g - 1].Result) = (false, null);
    }

    /// <summary>The brush flair of moving part <paramref name="group"/>: its own plan solids, then its generated pieces.</summary>
    private ExpandedHull FlairOf(ExpandedHull expanded, int group)
    {
        var plan = expanded.Solids.AsSpan(0, expanded.PlanSolids).ToArray().Where(s => _groups.GroupOf(s) == group).ToArray();
        var generated = expanded.Solids.AsSpan(expanded.PlanSolids).ToArray().Where(s => _groups.GroupOf(s) == group).ToArray();
        return new ExpandedHull
        {
            Solids = [.. plan, .. generated],
            PlanSolids = plan.Length,
            Generated = generated.Length,
            Meshes = expanded.Meshes,
            Unstuck = expanded.Unstuck,
        };
    }

    /// <summary>Polls the moving parts' bakes; true when a surface changed.</summary>
    private bool PollGroupBakes()
    {
        var changed = false;

        foreach (var bake in _groupBakes)
        {
            bake.Baker.Poll();

            if (bake.Surface.Poll() && bake.Active)
            {
                bake.Result = bake.Surface.Result;
                changed = true;
            }

            // The grid's mass and (layered) inner surfaces arrive on their own.
            if (bake.Active && bake.Baker.Version != bake.SeenBake && !bake.Baker.Busy)
            {
                bake.SeenBake = bake.Baker.Version;
                changed = true;
            }
        }

        return changed;
    }

    private bool GroupsBusy => _groupBakes.Exists(b => b.Busy);

    /// <summary>The moving parts' bricks for the renderer, each tagged with its group.</summary>
    private void AddGroupBricks(List<HullBrick> bricks, List<int> groups)
    {
        for (var g = 1; g <= _groupBakes.Count && g < _groups.Count; g++)
        {
            var bake = _groupBakes[g - 1];

            if (!bake.Active)
                continue;

            if (_stats.ShowGrid)
                Tag(bake.Baker.Bricks, g);
            else
            {
                if (bake.Result is not null)
                    Tag(bake.Result.Bricks, g);

                if (_session.Doc.Layers is not null)
                    Tag(bake.Baker.Bricks, g);
            }
        }

        void Tag(IReadOnlyList<HullBrick> add, int group)
        {
            bricks.AddRange(add);

            for (var i = 0; i < add.Count; i++)
                groups.Add(group);
        }
    }

    /// <summary>The whole ship's mass: the ship's own part and every moving part.</summary>
    private HullMass TotalMass()
    {
        var mass = _baker.Mass;

        for (var g = 1; g <= _groupBakes.Count && g < _groups.Count; g++)
            Add(ref mass, PartMass(g));

        return mass;
    }

    /// <summary>The mass of moving part <paramref name="group"/> (0: the ship's own).</summary>
    private HullMass PartMass(int group) =>
        group == 0 ? _baker.Mass : group <= _groupBakes.Count && _groupBakes[group - 1].Active ? _groupBakes[group - 1].Baker.Mass : default;

    private static void Add(ref HullMass a, in HullMass b)
    {
        a.Occupancy += b.Occupancy;
        a.W += b.W;
        (a.Wx, a.Wy, a.Wz) = (a.Wx + b.Wx, a.Wy + b.Wy, a.Wz + b.Wz);
        (a.Wxx, a.Wyy, a.Wzz) = (a.Wxx + b.Wxx, a.Wyy + b.Wyy, a.Wzz + b.Wzz);
        (a.Wxy, a.Wxz, a.Wyz) = (a.Wxy + b.Wxy, a.Wxz + b.Wxz, a.Wyz + b.Wyz);
    }

    /// <summary>Each frame: the preview's clock and each part's pose for the renderer (at rest unless playing).</summary>
    private void UpdatePose(double dt)
    {
        _jointSeconds = _bar.PlayJoints ? _jointSeconds + dt : 0;
        var count = Math.Min(_groups.Count, HullRenderer.MaxGroups);

        if (_poseTranslations.Length != count)
            (_poseRotations, _poseTranslations, _poseAngles) = (new double[count * 9], new Vector3d[count], new double[count]);

        for (var g = 1; g < count; g++)
            _poseAngles[g] = JointPose.Angle(_groups.Joints[g - 1], _jointSeconds);

        JointPose.Transforms(_groups, _poseAngles, _poseRotations, _poseTranslations);
        _hull.SetPose(_poseRotations, _poseTranslations);
    }

    private void DisposeGroupBakes()
    {
        foreach (var bake in _groupBakes)
            bake.Dispose();

        _groupBakes.Clear();
    }
}
