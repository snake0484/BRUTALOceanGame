using BrutalGame.Designer.Ui;
using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer.App;

/// <summary>
/// Component programmes in the designer: the component editor's preview run, and one run per placed component that has a
/// programme (kept while its prefab is the same, started at rest otherwise). They step on a 60 Hz tick of their own; each
/// frame the parts they drive are posed from the runs' state records (closed form, between two ticks), in place of the
/// joints preview. Each run has its component's cores laid out (rebuilt with the plan); the component editor's run
/// looks at the Programme panel's test object and follows its power switch, placed runs see nothing yet (the game's
/// world query is not wired).
/// </summary>
public sealed partial class DesignerApp
{
    private ProgramPanel _programPanel = null!;
    private readonly ProgramInstance _componentRun = new();
    private HullProgram? _componentProgram;
    private readonly Dictionary<uint, PlacedRun> _placedRuns = new();
    private long _programTick;
    private double _programClock;
    private CoreCatalog _coreCatalog = new();
    private HullCoreSet? _componentCores;
    private readonly TestObject _testObject = new();

    /// <summary>The bench's test object: something straight ahead of every sensor, at the panel's distance.</summary>
    private sealed class TestObject : ISensorWorld
    {
        public double Distance = double.PositiveInfinity;

        public double Nearest(Vector3d origin, Vector3d direction, double halfAngleRadians, double range) => Distance;
    }

    private sealed class PlacedRun(HullProgram program, List<HullJoint> joints)
    {
        public readonly HullProgram Program = program;
        public readonly List<HullJoint> Joints = joints;
        public readonly ProgramInstance Run = new();
    }

    private void InitPrograms(string dataFolder)
    {
        _coreCatalog = CoreCatalog.Load(dataFolder);
        _programPanel = new ProgramPanel(new InterfaceLibrary(dataFolder, InterfaceLibrary.DefaultFolder)) { Catalog = _coreCatalog };
        _prefabPanel.ProgramState = id =>
            _placedRuns.TryGetValue(id, out var r) && r.Run.State < r.Program.Interface.States.Count
                ? r.Program.Interface.States[r.Run.State] + (r.Run.Held ? " (stopped)" : "")
                : null;
        _prefabPanel.CallPlacement = (id, call) =>
        {
            if (_placedRuns.TryGetValue(id, out var r))
                ProgramRunner.Call(r.Program, r.Run, call);
        };
        _prefabPanel.CallEvery = (key, call) =>
        {
            foreach (var r in _placedRuns.Values)
            {
                if (r.Program.Interface.Key == key)
                    ProgramRunner.Call(r.Program, r.Run, call);
            }
        };
    }

    /// <summary>On a new plan version: the component's run, and a run per placed programmed component (new ones at rest).</summary>
    private void SyncPrograms()
    {
        var doc = _session.Doc;
        var joints = doc.Joints?.Joints ?? [];

        if (_active.Component is not null && doc.Program is { } program)
        {
            _componentRun.Cores = CoreLayout.Build(doc.Cores, _coreCatalog, joints);
            _componentRun.World = _testObject;

            if (_componentProgram is null || _componentProgram.Interface.Key != program.Interface.Key || _componentRun.State >= program.Interface.States.Count
                || !ReferenceEquals(_componentCores, doc.Cores))
                ProgramRunner.Reset(program, joints, _componentRun, _programTick);

            (_componentProgram, _componentCores) = (program, doc.Cores);
        }
        else
        {
            _componentProgram = null;
        }

        var seen = new HashSet<uint>();

        if (_active.Component is null && doc.Components is { } components)
        {
            foreach (var placement in components.Placements)
            {
                if (!doc.Prefabs.TryGetValue(placement.Prefab, out var prefab) || prefab.Program is not { } p)
                    continue;

                seen.Add(placement.Id);

                if (_placedRuns.TryGetValue(placement.Id, out var run) && ReferenceEquals(run.Program, p))
                    continue;

                run = new PlacedRun(p, prefab.Joints?.Joints ?? []);
                run.Run.Cores = CoreLayout.Build(prefab.Cores, _coreCatalog, run.Joints);
                ProgramRunner.Reset(p, run.Joints, run.Run, _programTick);
                _placedRuns[placement.Id] = run;
            }
        }

        foreach (var id in _placedRuns.Keys.Where(k => !seen.Contains(k)).ToList())
            _placedRuns.Remove(id);
    }

    /// <summary>Each frame: the preview's requests, then whole 60 Hz ticks of every run.</summary>
    private void StepPrograms(double dt)
    {
        var joints = _session.Doc.Joints?.Joints ?? [];

        if (_componentProgram is { } program)
        {
            if (_programPanel.ResetRequest)
                ProgramRunner.Reset(program, joints, _componentRun, _programTick);

            if (_programPanel.CallRequest >= 0)
                ProgramRunner.Call(program, _componentRun, _programPanel.CallRequest);

            _componentRun.Obstructed = _programPanel.Jammed;
            _componentRun.Unpowered = !_programPanel.Powered;
            _testObject.Distance = _programPanel.ProbeDistance >= ProgramPanel.ProbeNone ? double.PositiveInfinity : _programPanel.ProbeDistance;
        }

        (_programPanel.ResetRequest, _programPanel.CallRequest) = (false, -1);
        const double step = 1.0 / ProgramRunner.TicksPerSecond;
        _programClock += dt;

        for (var n = 0; _programClock >= step; n++)
        {
            _programClock -= step;
            _programTick++;

            if (n >= 10)
            {
                _programClock = 0;
                break;
            }

            if (_componentProgram is { } p)
                ProgramRunner.Step(p, joints, _componentRun, _programTick);

            foreach (var run in _placedRuns.Values)
                ProgramRunner.Step(run.Program, run.Joints, run.Run, _programTick);
        }
    }

    /// <summary>The programmed parts' angles (radians; a slider's metres) from their runs' state records, between this tick and the next.</summary>
    private void ProgramAngles(double[] angles, int count)
    {
        var doc = _session.Doc;
        var t = _programClock * ProgramRunner.TicksPerSecond;

        for (var g = 1; g < count && g < _groups.Sources.Count; g++)
        {
            var (placement, k) = _groups.Sources[g];

            if (placement < 0)
            {
                if (_componentProgram is { } program && doc.Joints is { } set && k - 1 < set.Joints.Count)
                    angles[g] = Value(set.Joints[k - 1], Between(program, set.Joints, _componentRun, k - 1, t));

                continue;
            }

            if (doc.Components is { } components && placement < components.Placements.Count
                && _placedRuns.TryGetValue(components.Placements[placement].Id, out var run) && k - 1 < run.Joints.Count)
                angles[g] = Value(run.Joints[k - 1], Between(run.Program, run.Joints, run.Run, k - 1, t));
        }
    }

    private double Between(HullProgram program, List<HullJoint> joints, ProgramInstance run, int j, double t)
    {
        var a = ProgramRunner.Position(program, joints, run, j, _programTick);
        var b = ProgramRunner.Position(program, joints, run, j, _programTick + 1);
        return a + (b - a) * t;
    }

    private static double Value(HullJoint joint, double stored) =>
        joint.Kind == JointKind.Slider ? stored / 1000.0 : stored / 10.0 * Math.PI / 180.0;
}
